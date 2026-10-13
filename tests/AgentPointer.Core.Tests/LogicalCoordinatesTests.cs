using System.Drawing;
using AgentPointer.Core.Rendering;

namespace AgentPointer.Core.Tests;

/// <summary>
/// --logical 的换算契约：上游 macOS 的逻辑 point 必须能落到物理像素上，含负原点与混合 DPI。
/// </summary>
public class LogicalCoordinatesTests
{
    [Fact]
    public void 主屏100百分比时逻辑等于物理()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        Assert.Equal(new Point(760, 500), LogicalCoordinates.ToPhysical(new Point(760, 500), monitor, 1d));
    }

    [Fact]
    public void 缩放150百分比时乘1点5()
    {
        var monitor = new Rectangle(0, 0, 2880, 1620);
        Assert.Equal(new Point(1140, 750), LogicalCoordinates.ToPhysical(new Point(760, 500), monitor, 1.5d));
    }

    [Fact]
    public void 加显示器全局物理原点()
    {
        var monitor = new Rectangle(1920, 0, 2560, 1440);
        Assert.Equal(new Point(2070, 300), LogicalCoordinates.ToPhysical(new Point(100, 200), monitor, 1.5d));
    }

    [Fact]
    public void 支持主屏左侧的负原点()
    {
        var monitor = new Rectangle(-1920, -300, 1920, 1080);
        Assert.Equal(new Point(-1820, -100), LogicalCoordinates.ToPhysical(new Point(100, 200), monitor, 1d));
    }

    [Fact]
    public void 缩放200百分比时乘2()
    {
        var monitor = new Rectangle(0, 0, 3840, 2160);
        Assert.Equal(new Point(1520, 1000), LogicalCoordinates.ToPhysical(new Point(760, 500), monitor, 2d));
    }

    [Fact]
    public void 取整采用四舍五入()
    {
        var monitor = new Rectangle(0, 0, 1000, 1000);
        Assert.Equal(new Point(2, 3), LogicalCoordinates.ToPhysical(new Point(1, 2), monitor, 1.5d));
    }

    [Fact]
    public void 矩形按左上右下分别换算()
    {
        var monitor = new Rectangle(1920, 0, 2560, 1440);
        var physical = LogicalCoordinates.ToPhysical(new Rectangle(10, 20, 100, 200), monitor, 1.5d);
        Assert.Equal(new Rectangle(1935, 30, 150, 300), physical);
    }

    [Fact]
    public void 矩形整体平移到全局坐标()
    {
        // --rect 配 --display：整块矩形都要平移，--until-click 是拿整块矩形去比对点击的
        var local = new Rectangle(10, 20, 100, 200);
        Assert.Equal(new Rectangle(1930, 20, 100, 200),
            LogicalCoordinates.ToGlobal(local, new Point(1920, 0)));
    }

    [Fact]
    public void 负原点显示器的矩形也整体平移()
    {
        var local = new Rectangle(0, 0, 100, 100);
        Assert.Equal(new Rectangle(-1920, -300, 100, 100),
            LogicalCoordinates.ToGlobal(local, new Point(-1920, -300)));
    }

    [Fact]
    public void 退化矩形不会出现负宽高()
    {
        var monitor = new Rectangle(0, 0, 100, 100);
        var physical = LogicalCoordinates.ToPhysical(new Rectangle(10, 10, 0, 0), monitor, 1d);
        Assert.Equal(0, physical.Width);
        Assert.Equal(0, physical.Height);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 非法缩放回落到100百分比(double scale)
    {
        Assert.Equal(1d, LogicalCoordinates.Normalize(scale));
        var monitor = new Rectangle(0, 0, 1000, 1000);
        Assert.Equal(new Point(100, 200), LogicalCoordinates.ToPhysical(new Point(100, 200), monitor, scale));
    }
}
