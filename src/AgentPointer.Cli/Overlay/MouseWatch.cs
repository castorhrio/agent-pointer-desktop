using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using AgentPointer.Cli.Interop;

namespace AgentPointer.Cli.Overlay;

/// <summary>
/// 低层鼠标钩子（<c>WH_MOUSE_LL</c>）。<c>--until-click</c> / <c>--until-any-click</c>
/// 靠它在用户真的点下去时收起箭头。
///
/// <para>钩子装在覆盖层的 UI 线程上：那里的等待循环用 <c>Application.DoEvents</c> 泵消息，
/// 低层钩子的回调就是这么被送进来的。</para>
///
/// <para>无论是否命中，一律 <c>CallNextHookEx</c>——点击必须照常送进下层应用，我们只是旁观者。
/// 回调里绝不抛异常，异常穿过原生边界会直接终止进程。</para>
/// </summary>
internal sealed class MouseWatch : IDisposable
{
    private readonly NativeMethods.HookProc _proc;
    private readonly Rectangle _target;
    private readonly bool _anyClick;
    private readonly Action _onTriggered;
    private IntPtr _hook;
    private bool _fired;

    /// <param name="target">全局物理像素下的目标矩形（--at / --mouse 已按 DPI 外扩成小方框）。</param>
    /// <param name="anyClick">true 时任意位置的按键都算命中。</param>
    public MouseWatch(Rectangle target, bool anyClick, Action onTriggered)
    {
        _target = target;
        _anyClick = anyClick;
        _onTriggered = onTriggered;
        // 委托必须保持存活：被 GC 掉之后原生侧还会继续回调，那就是必崩
        _proc = Callback;
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception();
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_fired && IsButtonDown((int)wParam))
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var point = new Point(data.pt.X, data.pt.Y);

            if (_anyClick || _target.Contains(point))
            {
                _fired = true;
                try
                {
                    _onTriggered();
                }
                catch
                {
                    // 钩子回调里吞掉一切：不能让它冒泡到原生边界
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsButtonDown(int message) =>
        message is NativeMethods.WM_LBUTTONDOWN
            or NativeMethods.WM_RBUTTONDOWN
            or NativeMethods.WM_MBUTTONDOWN
            or NativeMethods.WM_XBUTTONDOWN;

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
