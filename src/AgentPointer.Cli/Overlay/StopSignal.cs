using System.ComponentModel;
using System.Diagnostics;
using AgentPointer.Cli.Interop;

namespace AgentPointer.Cli.Overlay;

public enum StopReason
{
    StopSignalled,
    OwnerExited,
    DurationElapsed,
    WindowLost,

    /// <summary>用户点了牌子或箭杆，箭头自己收场。</summary>
    Clicked,

    /// <summary>用户点了牌子上的 ×（--close-button）。</summary>
    Closed,

    /// <summary>--until-click / --until-any-click：用户点到了目标（或任意位置）。</summary>
    TargetClicked,
}

/// <summary>每次 start 使用独立的就绪事件，避免并发启动互相放行。</summary>
internal static class ShownSignal
{
    public const string EnvironmentVariable = "AGENTPOINTER_SHOWN_EVENT";
    public const string ResultEnvironmentVariable = "AGENTPOINTER_START_RESULT";
    public static string? EventName => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static IntPtr CreateListener(string name) =>
        NativeMethods.CreateEventW(IntPtr.Zero, true, false, name);

    public static void Signal()
    {
        var handle = NativeMethods.OpenEventW(NativeMethods.EVENT_MODIFY_STATE, false, EventName);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        try
        {
            if (!NativeMethods.SetEvent(handle)) throw new Win32Exception();
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public static bool WaitShownOrExit(IntPtr shownEvent, IntPtr childProcess, uint timeoutMs) =>
        NativeMethods.WaitForMultipleObjects(2, new[] { shownEvent, childProcess }, false, timeoutMs)
        == NativeMethods.WAIT_OBJECT_0;
}

/// <summary>
/// 每个覆盖层进程有独立的停止事件，不复位其他进程的信号。
/// stop 只打开相同 owner 的事件；--all 打开当前 Windows 会话中的全部实例事件。
/// </summary>
internal sealed class StopSignal : IDisposable
{
    private const string Prefix = @"Local\AgentPointer.Stop.v2.";
    private readonly IntPtr[] _handles;
    private readonly bool _ownerExited;

    private StopSignal(IntPtr[] handles, bool ownerExited)
    {
        _handles = handles;
        _ownerExited = ownerExited;
    }

    private static string ScopedName(int? ownerPid, int instancePid) =>
        $"{Prefix}Owner.{(ownerPid is { } pid ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unowned")}.{instancePid}";

    public static StopSignal CreateListener(int? ownerPid)
    {
        var handles = new List<IntPtr>(3);
        try
        {
            foreach (var name in new[] { Prefix + Environment.ProcessId, ScopedName(ownerPid, Environment.ProcessId) })
            {
                var handle = NativeMethods.CreateEventW(IntPtr.Zero, true, false, name);
                if (handle == IntPtr.Zero) throw new Win32Exception();
                handles.Add(handle);
            }

            var ownerExited = false;
            if (ownerPid is { } pid)
            {
                var owner = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE, false, pid);
                if (owner != IntPtr.Zero) handles.Add(owner);
                else
                {
                    var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (error != 87) throw new Win32Exception(error);
                    ownerExited = true;
                }
            }

            return new StopSignal(handles.ToArray(), ownerExited);
        }
        catch
        {
            foreach (var handle in handles) NativeMethods.CloseHandle(handle);
            throw;
        }
    }

    public static bool Signal(int? ownerPid, bool all)
    {
        var signalled = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var name = all ? Prefix + process.Id : ScopedName(ownerPid, process.Id);
                var handle = NativeMethods.OpenEventW(NativeMethods.EVENT_MODIFY_STATE, false, name);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (!NativeMethods.SetEvent(handle)) throw new Win32Exception();
                    signalled = true;
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
        }
        return signalled;
    }

    public StopReason Wait(StopReason timeoutReason, double timeoutSeconds)
    {
        if (_ownerExited) return StopReason.OwnerExited;
        var result = NativeMethods.WaitForMultipleObjects(
            (uint)_handles.Length, _handles, false, (uint)Math.Max(1, timeoutSeconds * 1000));
        return result switch
        {
            NativeMethods.WAIT_OBJECT_0 or NativeMethods.WAIT_OBJECT_0_PLUS_1 => StopReason.StopSignalled,
            NativeMethods.WAIT_OBJECT_0 + 2 => StopReason.OwnerExited,
            NativeMethods.WAIT_TIMEOUT => timeoutReason,
            _ => StopReason.WindowLost,
        };
    }

    public void Dispose()
    {
        foreach (var handle in _handles) NativeMethods.CloseHandle(handle);
    }
}
