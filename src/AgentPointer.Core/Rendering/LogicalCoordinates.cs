using System.Drawing;

namespace AgentPointer.Core.Rendering;

/// <summary>
/// 逻辑坐标（DPI 无关，96 DPI 基准）与全局物理像素之间的换算。
///
/// <para>上游 macOS 全程用逻辑 point，agent 脚本里写着 <c>--at 760,500</c>。
/// Windows 的覆盖层按物理像素摆放，所以 <c>--logical</c> 需要把这层换算补上：
/// 先按目标显示器的缩放比放大，再加该显示器的全局物理原点（允许负值）。</para>
///
/// <para><b>纯函数</b>：不读显示器、不读 DPI，输入输出都是显式参数，可单测。</para>
/// </summary>
public static class LogicalCoordinates
{
    /// <summary>逻辑点 → 全局物理像素。缩放比不合法时按 1 处理，绝不抛。</summary>
    public static Point ToPhysical(Point logical, Rectangle monitorBounds, double scale)
    {
        var s = Normalize(scale);
        return new Point(
            monitorBounds.Left + Scale(logical.X, s),
            monitorBounds.Top + Scale(logical.Y, s));
    }

    /// <summary>
    /// 逻辑矩形 → 全局物理像素矩形。
    /// 左上角与右下角分别换算，避免把取整误差吃进宽高。
    /// </summary>
    public static Rectangle ToPhysical(Rectangle logical, Rectangle monitorBounds, double scale)
    {
        var s = Normalize(scale);
        var left = monitorBounds.Left + Scale(logical.Left, s);
        var top = monitorBounds.Top + Scale(logical.Top, s);
        var right = monitorBounds.Left + Scale(logical.Right, s);
        var bottom = monitorBounds.Top + Scale(logical.Bottom, s);

        return new Rectangle(
            left, top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top));
    }

    /// <summary>DPI / 96 的缩放比；非有限或非正一律回落到 1（100%）。</summary>
    public static double Normalize(double scale) =>
        double.IsFinite(scale) && scale > 0 ? scale : 1d;

    private static int Scale(int value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
}
