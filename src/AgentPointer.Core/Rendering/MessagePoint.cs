using System.Drawing;

namespace AgentPointer.Core.Rendering;

/// <summary>
/// 鼠标消息 LPARAM 的解码。
///
/// <para>Win32 的鼠标消息并不共用一套坐标空间：<c>WM_NCHITTEST</c> 的 LPARAM 是
/// <strong>屏幕</strong>坐标，而 <c>WM_MOUSEMOVE</c> / <c>WM_LBUTTONDOWN</c> 之类的普通鼠标消息
/// 的 LPARAM <strong>已经是客户区</strong>坐标。把后者再 <c>PointToClient</c> 一次，窗口只要不在
/// 屏幕原点（覆盖层窗口永远不在），命中判定就整体错位，点击被吃掉却什么也不发生。</para>
///
/// <para>坐标是 16 位<strong>有符号</strong>数（GET_X_LPARAM / GET_Y_LPARAM），
/// 主屏左边的负坐标显示器上必须是负值，所以按 <c>short</c> 截断而不是 <c>ushort</c>。</para>
///
/// <para><b>纯函数</b>：不读窗口、不读消息队列，可单测。</para>
/// </summary>
public static class MessagePoint
{
    /// <summary>
    /// 消息坐标 → 客户区坐标。
    /// </summary>
    /// <param name="isScreenMessage">
    /// lParam 是否为屏幕坐标。只有 <c>WM_NCHITTEST</c> 是 true。
    /// </param>
    /// <param name="packedLParam">消息 lParam 的低 32 位。</param>
    /// <param name="windowOrigin">窗口客户区原点（屏幕坐标）。只有屏幕消息才需要它。</param>
    public static Point ToClient(bool isScreenMessage, int packedLParam, Point windowOrigin)
    {
        var point = Unpack(packedLParam);

        return isScreenMessage
            ? new Point(point.X - windowOrigin.X, point.Y - windowOrigin.Y)
            : point;
    }

    /// <summary>把 lParam 解包成 16 位有符号的一对坐标（不做任何原点平移）。</summary>
    public static Point Unpack(int packedLParam) => new(
        unchecked((short)(packedLParam & 0xFFFF)),
        unchecked((short)((packedLParam >> 16) & 0xFFFF)));
}
