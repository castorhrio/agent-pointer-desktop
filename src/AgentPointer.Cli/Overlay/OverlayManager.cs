using System.Drawing;
using AgentPointer.Cli.Targeting;
using AgentPointer.Core.Protocol;

namespace AgentPointer.Cli.Overlay;

/// <summary>
/// 覆盖层窗口池。<b>每个显示器一个窗口，常驻复用</b>。
///
/// <para>绝不用一块横跨虚拟桌面的巨窗：PerMonitorV2 下它只会按"占比最大的那块屏"的 DPI 渲染，
/// 跨屏部分会被拉伸模糊——这是混合 DPI 机器上最典型的糊图来源。</para>
/// </summary>
internal sealed class OverlayManager : IDisposable
{
    private readonly Dictionary<IntPtr, OverlayForm> _windows = new();
    private readonly List<MonitorInfo> _monitors;

    public OverlayManager()
    {
        _monitors = MonitorService.Enumerate().ToList();
        if (_monitors.Count == 0)
        {
            throw new OverlayUnavailableException(
                "枚举不到任何显示器。可能处于锁屏或 UAC 安全桌面，此时无法绘制覆盖层。");
        }
    }

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public Rectangle VirtualScreen => MonitorService.VirtualScreen(_monitors);

    /// <summary>把 <c>--display N</c> 的局部坐标换算成全局物理像素。</summary>
    public bool TryToGlobal(int? displayIndex, Point local, out Point global)
    {
        if (displayIndex is not { } index)
        {
            global = local;
            return true;
        }

        if (!MonitorService.TryFromIndex(index, _monitors, out var monitor) || monitor is null)
        {
            global = local;
            return false;
        }

        if (local.X < 0 || local.X >= monitor.Bounds.Width || local.Y < 0 || local.Y >= monitor.Bounds.Height)
        {
            throw new TargetNotFoundException($"局部坐标 ({local.X}, {local.Y}) 不在显示器 #{index + 1} 内。");
        }

        global = new Point(monitor.Bounds.Left + local.X, monitor.Bounds.Top + local.Y);
        return true;
    }

    /// <summary>目标必须落在真实显示器内；虚拟桌面包围盒中的空隙也不算屏幕。</summary>
    public MonitorInfo MonitorFor(Point global) =>
        MonitorService.FromPoint(global, _monitors)
        ?? throw new TargetNotFoundException($"坐标 ({global.X}, {global.Y}) 不在任何显示器内。请用 doctor 查看屏幕范围。");

    /// <summary>拿到（或懒创建）承载该显示器的窗口。</summary>
    public OverlayForm WindowFor(MonitorInfo monitor)
    {
        if (_windows.TryGetValue(monitor.Handle, out var existing))
        {
            return existing;
        }

        var form = new OverlayForm();
        form.EnsureHandle();
        _windows[monitor.Handle] = form;
        return form;
    }

    public void HideAll()
    {
        foreach (var form in _windows.Values)
        {
            form.HideNow();
        }
    }

    public void Dispose()
    {
        foreach (var form in _windows.Values)
        {
            form.Dispose();
        }

        _windows.Clear();
    }
}
