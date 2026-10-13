using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using AgentPointer.Cli.Interop;

namespace AgentPointer.Cli.Targeting;

/// <summary>一个显示器的快照。全部使用<strong>虚拟屏幕物理像素</strong>，原点是主屏左上角（0,0），允许负值。</summary>
public sealed record MonitorInfo(
    IntPtr Handle,
    int Index,
    Rectangle Bounds,
    Rectangle WorkArea,
    uint Dpi,
    string DeviceName)
{
    public double Scale => Dpi / 96d;

    public override string ToString() =>
        $"#{Index + 1} {DeviceName} {Bounds.Width}x{Bounds.Height}@{Dpi}dpi";
}

/// <summary>
/// 显示器枚举与坐标换算。
///
/// <para><b>关键设计</b>：覆盖层是"每个显示器一个窗口"，不是一块横跨虚拟桌面的巨窗。
/// PerMonitorV2 下巨窗只会按占比最大的那块屏的 DPI 渲染，跨屏部分会被拉伸模糊——
/// 这是混合 DPI 机器上最典型的糊图来源。</para>
/// </summary>
public static class MonitorService
{
    /// <summary>枚举全部显示器，顺序即 <c>--display N</c> 的编号顺序。</summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var result = new List<MonitorInfo>();

        // 回调里绝不能抛异常——异常穿过原生边界会直接杀掉进程
        bool Callback(IntPtr hMonitor, IntPtr hdc, IntPtr lRect, IntPtr lParam)
        {
            try
            {
                var info = default(NativeMethods.MONITORINFOEX);
                info.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>();

                if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    return true;
                }

                var bounds = ToRect(info.rcMonitor);
                var dpi = EffectiveDpi(hMonitor);

                result.Add(new MonitorInfo(
                    hMonitor,
                    result.Count,
                    bounds,
                    ToRect(info.rcWork),
                    dpi,
                    info.szDevice ?? string.Empty));
            }
            catch
            {
                // 吞掉：单个显示器枚举失败不应该影响其它显示器
            }

            return true;
        }

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return result;
    }

    public static Rectangle VirtualScreen(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return Rectangle.Empty;

        var left = monitors.Min(m => m.Bounds.Left);
        var top = monitors.Min(m => m.Bounds.Top);
        var right = monitors.Max(m => m.Bounds.Right);
        var bottom = monitors.Max(m => m.Bounds.Bottom);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>目标点落在哪块屏上。</summary>
    public static MonitorInfo? FromPoint(Point p, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => m.Bounds.Contains(p));

    /// <summary><c>--display N</c>：把第 N 块屏的局部坐标转成全局物理像素。</summary>
    public static bool TryFromIndex(int index, IReadOnlyList<MonitorInfo> monitors, out MonitorInfo? monitor)
    {
        monitor = index >= 0 && index < monitors.Count ? monitors[index] : null;
        return monitor is not null;
    }

    /// <summary>
    /// 检测是否处于安全桌面（UAC 提权）或锁屏状态——这两种情况下创建覆盖层窗口会失败。
    /// 取当前输入桌面的名字，和默认桌面比一下。
    /// </summary>
    public static bool IsSecureOrLockedDesktop()
    {
        const uint desktopSwitchDesktop = 0x0100;

        var handle = NativeMethods.OpenInputDesktop(0, false, desktopSwitchDesktop);
        if (handle == IntPtr.Zero)
        {
            // 拿不到就保守认为不可用，让调用方走降级
            return true;
        }

        try
        {
            uint needed;
            NativeMethods.GetUserObjectInformation(handle, Win32.UOI_NAME, null!, 0, out needed);
            if (needed == 0) return true;

            var buffer = new byte[needed];
            if (!NativeMethods.GetUserObjectInformation(handle, Win32.UOI_NAME, buffer, needed, out _))
            {
                return true;
            }

            var name = Encoding.Unicode.GetString(buffer).TrimEnd('\0');

            // Winlogon / LockWin 桌面对应 UAC 提权与锁屏
            return name.Contains("Winlogon", StringComparison.OrdinalIgnoreCase)
                || name.Contains("LockWin", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.CloseDesktop(handle);
        }
    }

    /// <summary>
    /// 取显示器的 DPI。<b>必须按显示器取而不是按窗口取</b>——覆盖层窗口刚被移动过去时
    /// <c>GetDpiForWindow</c> 还返回旧值，会导致第一帧用错缩放。
    /// </summary>
    public static uint EffectiveDpi(IntPtr hMonitor)
    {
        try
        {
            var hr = NativeMethods.GetDpiForMonitor(
                hMonitor, Win32.MDT_EFFECTIVE_DPI, out var dpiX, out _);

            if (hr == 0 && dpiX > 0) return dpiX;
        }
        catch (DllNotFoundException)
        {
            // Windows 8.1 以下没有 shcore.dll
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            var dpi = NativeMethods.GetDpiForWindow(hMonitor);
            return dpi == 0 ? 96 : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static Rectangle ToRect(NativeMethods.RECT r) =>
        new(r.Left, r.Top, r.Width, r.Height);
}
