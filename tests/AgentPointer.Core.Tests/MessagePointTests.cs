using System.Drawing;
using AgentPointer.Core.Rendering;

namespace AgentPointer.Core.Tests;

/// <summary>
/// 鼠标消息坐标空间的契约：WM_NCHITTEST 是屏幕坐标，其余鼠标消息已经是客户区坐标。
/// 覆盖层窗口永远不在屏幕原点，把两者搞混会让牌子 / 箭杆 / 芯片上的点击全部落空。
/// </summary>
public class MessagePointTests
{
    /// <summary>按 GET_X_LPARAM / GET_Y_LPARAM 的方式打包，允许负坐标。</summary>
    private static int Pack(int x, int y) => unchecked((x & 0xFFFF) | (y << 16));

    [Fact]
    public void 客户区消息不做原点平移()
    {
        // 窗口在 (1200, 700)，客户区 (100, 40) 就该是 (100, 40)
        Assert.Equal(new Point(100, 40),
            MessagePoint.ToClient(isScreenMessage: false, Pack(100, 40), new Point(1200, 700)));
    }

    [Fact]
    public void 屏幕消息减去窗口原点()
    {
        Assert.Equal(new Point(100, 40),
            MessagePoint.ToClient(isScreenMessage: true, Pack(1300, 740), new Point(1200, 700)));
    }

    [Fact]
    public void 同一次点击两种消息给出一致的客户区坐标()
    {
        var origin = new Point(1200, 700);

        var fromNcHitTest = MessagePoint.ToClient(true, Pack(1300, 740), origin);
        var fromMouseMove = MessagePoint.ToClient(false, Pack(100, 40), origin);

        Assert.Equal(fromMouseMove, fromNcHitTest);
    }

    [Fact]
    public void 负坐标显示器上的客户区消息保持负值()
    {
        Assert.Equal(new Point(-100, -50),
            MessagePoint.ToClient(isScreenMessage: false, Pack(-100, -50), new Point(0, 0)));
    }

    [Fact]
    public void 负原点窗口上的屏幕消息正确平移()
    {
        // 窗口左上角在 (-1000, -200)，屏幕 (-100, -50) 落在客户区 (900, 150)
        Assert.Equal(new Point(900, 150),
            MessagePoint.ToClient(isScreenMessage: true, Pack(-100, -50), new Point(-1000, -200)));
    }

    [Fact]
    public void 打包坐标按16位有符号解码()
    {
        Assert.Equal(new Point(-1, -1), MessagePoint.Unpack(unchecked((int)0xFFFFFFFF)));
        Assert.Equal(new Point(32767, -32768), MessagePoint.Unpack(Pack(32767, -32768)));
    }
}
