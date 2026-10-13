using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using AgentPointer.Cli.Interop;
using AgentPointer.Core.Targeting;

namespace AgentPointer.Cli.Targeting;

/// <summary>一个匹配上的窗口。</summary>
public sealed record WindowMatch(IntPtr Handle, string? ProcessName, string Title, Rectangle Bounds);

/// <summary>
/// <c>--window</c> / <c>--app</c> 的解析。
///
/// <para>Windows 上的 <c>App</c> 是<strong>进程名</strong>（<c>WindowsTerminal</c>、<c>Code</c>），
/// 不是 macOS 的 App bundle 名。支持 <c>App:标题</c> 语法来进一步缩小范围。</para>
/// </summary>
public static class WindowFinder
{
    /// <summary>列出所有可见且有标题的顶层窗口。</summary>
    public static IReadOnlyList<WindowMatch> Enumerate()
    {
        var results = new List<WindowMatch>();

        bool Callback(IntPtr hWnd, IntPtr lParam)
        {
            // 回调里禁止抛异常——异常穿过原生边界会直接终止进程
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                if (GetWindowTitle(hWnd) is not { Length: > 0 } title) return true;
                if (NativeMethods.GetWindowRect(hWnd, out var rect) is false) return true;
                if (rect.Width <= 0 || rect.Height <= 0) return true;

                var processName = GetProcessName(hWnd);

                results.Add(new WindowMatch(
                    hWnd, processName, title,
                    new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height)));
            }
            catch
            {
                // 单个窗口查不出来不影响其它窗口
            }

            return true;
        }

        NativeMethods.EnumWindows(Callback, IntPtr.Zero);
        return results;
    }

    /// <summary>
    /// 按 <c>App</c> 或 <c>App:title</c> 找窗口。找不到返回 null。
    ///
    /// <para>匹配顺序：<see cref="AppAliases.Candidates"/> 逐个候选尝试，每个候选先比进程名、
    /// 再比可执行文件的 FileDescription（上游 App 名往往落在描述里）。给了 <c>App:title</c>
    /// 就<strong>必须</strong>有窗口的标题包含它，否则返回 null（退出码 3）——绝不退回同进程里
    /// 随便一个窗口，那等于指着错误的窗口说"就是它"。只有所有候选都没命中任何进程时，才把
    /// 查询串当成窗口标题找。挑选规则本身在 <see cref="WindowPicker"/> 里，可单测。</para>
    /// </summary>
    public static WindowMatch? Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var windows = Enumerate();
        var candidates = windows
            .Select((window, index) => new WindowCandidate(
                index,
                window.ProcessName,
                FileDescriptionOf(window.Handle),
                window.Title,
                (long)window.Bounds.Width * window.Bounds.Height))
            .ToList();

        var picked = WindowPicker.Pick(candidates, query);
        return picked is null ? null : windows[picked.Order];
    }

    /// <summary>单个候选是否命中这个窗口：进程名 → FileDescription → ApplicationFrameHost 标题。</summary>
    internal static bool CandidateMatches(WindowMatch window, string candidate) =>
        WindowPicker.Matches(
            new WindowCandidate(
                0, window.ProcessName, FileDescriptionOf(window.Handle), window.Title,
                (long)window.Bounds.Width * window.Bounds.Height),
            candidate);

    /// <summary>把 <c>App:标题</c> 拆开。标题里可以含冒号，所以只拆第一个。</summary>
    public static (string App, string? Title) SplitQuery(string query) => WindowPicker.SplitQuery(query);

    /// <summary>进程名匹配：忽略大小写、忽略 .exe 后缀。</summary>
    public static bool ProcessMatches(string? processName, string query) =>
        WindowPicker.ProcessMatches(processName, query);

    /// <summary>
    /// 把目标窗口置前。后台进程直接调 <c>SetForegroundWindow</c> 会被 Windows 的前台锁挡下，
    /// 而 <c>AllowSetForegroundWindow</c> 只对"调用者自己就是前台进程"有效——从 agentpointer
    /// 这个后台进程调它换不来置前权限，所以这里不依赖它。
    ///
    /// <para>真正有效的组合：把<strong>当前线程挂到前台线程上</strong>（只要线程 id 不同，哪怕目标
    /// 就是前台线程所在进程的另一个窗口也要挂），然后 <c>BringWindowToTop</c> 调 Z 序、
    /// <c>SetForegroundWindow</c> 拿激活，最后在 <c>finally</c> 里摘下。</para>
    ///
    /// <para>返回 false 表示没置前成功；调用方只提醒，不让命令失败。</para>
    /// </summary>
    public static bool TryRaise(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, Win32.SW_RESTORE);
            }

            var currentThread = NativeMethods.GetCurrentThreadId();
            var foreground = NativeMethods.GetForegroundWindow();
            var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);

            var attached = foregroundThread != 0
                           && foregroundThread != currentThread
                           && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

            try
            {
                NativeMethods.BringWindowToTop(hWnd);
                return NativeMethods.SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? GetWindowTitle(IntPtr hWnd)
    {
        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length <= 0) return null;

        var sb = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    /// <summary>pid → FileDescription 缓存。逐窗口读文件版本信息很贵，而且同一进程结果固定。</summary>
    private static readonly ConcurrentDictionary<int, string?> FileDescriptions = new();

    private static string? FileDescriptionOf(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0) return null;

        return FileDescriptions.GetOrAdd((int)pid, static rawPid =>
        {
            try
            {
                using var process = Process.GetProcessById(rawPid);
                var description = process.MainModule?.FileVersionInfo?.FileDescription;
                return string.IsNullOrWhiteSpace(description) ? null : description;
            }
            catch
            {
                // 权限不足或进程已退出：没有描述信息不算错误
                return null;
            }
        });
    }

    private static string? GetProcessName(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0) return null;

        var handle = NativeMethods.OpenProcess(
            Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);

        if (handle == IntPtr.Zero) return null;

        try
        {
            var size = (uint)1024;
            var sb = new StringBuilder((int)size);
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size)) return null;

            var full = sb.ToString();
            var slash = full.LastIndexOf('\\');
            return slash >= 0 ? full[(slash + 1)..] : full;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
