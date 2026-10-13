using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgentPointer.Tray;

/// <summary>
/// 托盘图标工厂：用与覆盖层箭头同一套视觉（弧形红箭头 + 白描边）现场画一枚 32px 图标，
/// 不引入 .ico 资源——图标天然跟随品牌色变化，也不会出现资源与代码两套规格。
/// </summary>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var accent = Color.FromArgb(255, 220, 30, 30);
            var outline = Color.White;

            // 弧形箭杆：左下缘起笔，弯向右上
            using var shaft = new GraphicsPath();
            shaft.AddBezier(
                new PointF(4, 24),
                new PointF(10, 20),
                new PointF(16, 16),
                new PointF(24, 10));

            const float shaftWidth = 7f;
            const float outlineWidth = 2.5f;

            using (var edge = new Pen(outline, shaftWidth + 2 * outlineWidth))
            {
                edge.StartCap = LineCap.Round;
                edge.EndCap = LineCap.Round;
                g.DrawPath(edge, shaft);
            }

            using (var pen = new Pen(accent, shaftWidth))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawPath(pen, shaft);
            }

            // 头部：沿末端切线（指向右上）的三角形
            var dir = new PointF(24 - 16, 10 - 16);
            var len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            dir = new PointF(dir.X / len, dir.Y / len);
            var perp = new PointF(-dir.Y, dir.X);

            var headLength = 12f;
            var headWidth = 11f;
            var tip = new PointF(28, 6);
            var baseCenter = new PointF(tip.X - dir.X * headLength, tip.Y - dir.Y * headLength);
            var left = new PointF(baseCenter.X + perp.X * headWidth / 2, baseCenter.Y + perp.Y * headWidth / 2);
            var right = new PointF(baseCenter.X - perp.X * headWidth / 2, baseCenter.Y - perp.Y * headWidth / 2);

            using var head = new GraphicsPath();
            head.AddPolygon(new[] { tip, left, right });

            using (var edgeBrush = new SolidBrush(outline))
            {
                Inflate(head, outlineWidth);
                g.FillPath(edgeBrush, head);
            }

            using var headBrush = new SolidBrush(accent);
            g.FillPath(headBrush, head);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private static void Inflate(GraphicsPath path, float amount)
    {
        // 近似外扩：沿法线方向把顶点各自外推
        var points = path.PathPoints;
        var cx = points.Average(p => p.X);
        var cy = points.Average(p => p.Y);
        for (var i = 0; i < points.Length; i++)
        {
            var dx = points[i].X - cx;
            var dy = points[i].Y - cy;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) continue;
            points[i] = new PointF(
                points[i].X + dx / len * amount,
                points[i].Y + dy / len * amount);
        }

        path.Reset();
        path.AddPolygon(points);
    }
}
