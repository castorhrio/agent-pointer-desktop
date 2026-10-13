using System.Drawing;
using AgentPointer.Core.Rendering;
using Xunit;

namespace AgentPointer.Core.Tests;

public class PlacementTests
{
    private static readonly RectangleF Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void 自动选边_牌子不进入禁区且不出屏()
    {
        var result = Placement.Place(new SizeF(300, 70), new RectangleF(960, 540, 1, 1), Screen, 170);

        Assert.False(result.SignRect.IntersectsWith(result.KeepOut), "牌子不得遮挡目标禁区");
        Assert.True(RectangleF.Inflate(Screen, -Placement.Margin, -Placement.Margin).Contains(result.SignRect),
            "牌子必须完整落在可用区内");
    }

    [Fact]
    public void 目标贴左缘时牌子不会放到屏外()
    {
        var result = Placement.Place(new SizeF(300, 70), new RectangleF(10, 540, 1, 1), Screen, 170);

        Assert.True(result.SignRect.Right <= Screen.Right);
        Assert.True(result.SignRect.X >= 0, "兜底钳制也必须保住屏内");
    }

    [Fact]
    public void 强制方向放不下时换到能放下的一侧()
    {
        // 目标贴右缘，强制 right（牌子想放右侧）必然放不下
        var result = Placement.Place(
            new SizeF(300, 70), new RectangleF(1900, 540, 1, 1), Screen, 170,
            forced: ApproachDirection.Right);

        Assert.True(result.SignRect.Right <= Screen.Right + Placement.Margin,
            "箭头永远不能为了偏好方向而跑出屏幕");
        Assert.False(result.SignRect.IntersectsWith(result.KeepOut), "换边后仍不得遮挡目标");
    }

    [Fact]
    public void 极小屏幕触发兜底钳制而不是失败()
    {
        var tiny = new RectangleF(0, 0, 260, 200);
        var result = Placement.Place(new SizeF(240, 60), new RectangleF(130, 100, 1, 1), tiny, 170);

        Assert.True(result.SignRect.Height > 0);
        // 钳制后牌子至少和屏幕保持重叠
        Assert.True(result.SignRect.IntersectsWith(tiny));
    }

    [Fact]
    public void 方向解析_九方位加auto()
    {
        Assert.Null(Placement.ParseDirection("auto"));
        Assert.Null(Placement.ParseDirection(null));
        Assert.Equal(ApproachDirection.TopLeft, Placement.ParseDirection("top-left"));
        Assert.Equal(ApproachDirection.BottomRight, Placement.ParseDirection("BOTTOM-RIGHT"));
        Assert.Equal(ApproachDirection.Left, Placement.ParseDirection("left"));

        Assert.Throws<ArgumentException>(() => Placement.ParseDirection("diagonal"));
    }

    [Fact]
    public void 对角单位向量已归一化()
    {
        var (dx, dy) = Placement.Unit(ApproachDirection.TopLeft);
        Assert.Equal(-0.7071, dx, 4);
        Assert.Equal(-0.7071, dy, 4);
    }
}

public class ArrowMetricsTests
{
    [Fact]
    public void 上游SML尺寸表_数值不漂移()
    {
        var s = ArrowMetrics.For(ArrowSize.Small);
        Assert.Equal(10, s.Stroke);
        Assert.Equal(46, s.HeadLength);
        Assert.Equal(48, s.HeadWidth);
        Assert.Equal(30, s.FontSize);
        Assert.Equal(130, s.Reach);

        var m = ArrowMetrics.For(ArrowSize.Medium);
        Assert.Equal(15, m.Stroke);
        Assert.Equal(62, m.HeadLength);
        Assert.Equal(64, m.HeadWidth);
        Assert.Equal(44, m.FontSize);
        Assert.Equal(170, m.Reach);

        var l = ArrowMetrics.For(ArrowSize.Large);
        Assert.Equal(21, l.Stroke);
        Assert.Equal(84, l.HeadLength);
        Assert.Equal(88, l.HeadWidth);
        Assert.Equal(60, l.FontSize);
        Assert.Equal(220, l.Reach);
    }

    [Fact]
    public void 尺寸解析_大小写不敏感_默认M()
    {
        Assert.Equal(ArrowSize.Small, ArrowMetrics.ParseSize("s"));
        Assert.Equal(ArrowSize.Medium, ArrowMetrics.ParseSize(null));
        Assert.Throws<ArgumentException>(() => ArrowMetrics.ParseSize("XL"));
    }
}

public class ArrowPaletteTests
{
    [Fact]
    public void 上游预置色_hex逐项一致()
    {
        Assert.Equal("FF3B1F", ToHex(ArrowColor.Parse("red")));
        Assert.Equal("FF8A00", ToHex(ArrowColor.Parse("orange")));
        Assert.Equal("FFD60A", ToHex(ArrowColor.Parse("yellow")));
        Assert.Equal("21B24B", ToHex(ArrowColor.Parse("green")));
        Assert.Equal("00B5AD", ToHex(ArrowColor.Parse("teal")));
        Assert.Equal("0A84FF", ToHex(ArrowColor.Parse("blue")));
        Assert.Equal("8E44FF", ToHex(ArrowColor.Parse("purple")));
        Assert.Equal("FF2D78", ToHex(ArrowColor.Parse("pink")));
        Assert.Equal("1C1C1E", ToHex(ArrowColor.Parse("black")));
        Assert.Equal("FFFFFF", ToHex(ArrowColor.Parse("white")));
    }

    [Fact]
    public void 短格式hex_逐位展开()
    {
        var c = ArrowColor.Parse("#F31");
        Assert.Equal("FF3311", ToHex(c));
    }

    [Fact]
    public void 黄色配深字_红色配白字_上游Rec709语义()
    {
        Assert.True(ArrowColor.Parse("yellow").IsLight);
        Assert.False(ArrowColor.Parse("red").IsLight);

        var onYellow = ArrowColor.Parse("yellow").Contrast;
        Assert.True(onYellow.R < 0.5f && onYellow.G < 0.5f, "浅色箭头文字应为深色");

        var onRed = ArrowColor.Parse("red").Contrast;
        Assert.True(onRed.R > 0.9f && onRed.G > 0.9f, "深色箭头文字应为白色");
    }

    [Fact]
    public void 近黑箭头的黑描边自动转白()
    {
        var black = ArrowColor.Parse("black");
        Assert.True(black.Outline.R > 0.9f, "1C1C1E 亮度低于 0.2，black 边应为白");

        var red = ArrowColor.Parse("red");
        Assert.True(red.Outline.R < 0.1f, "红色箭头的 black 边应为黑");
    }

    [Fact]
    public void 非法颜色抛异常()
    {
        Assert.Throws<ArgumentException>(() => ArrowColor.Parse("chartreuse"));
        Assert.Throws<ArgumentException>(() => ArrowColor.Parse("#12345"));
    }

    private static string ToHex(ArrowColor c)
    {
        var (r, g, b) = c.ToBytes();
        return $"{r:X2}{g:X2}{b:X2}";
    }
}
