using System.Drawing.Drawing2D;
using System.Drawing.Text;
using AgentPointer.Core.Protocol;
using AgentPointer.Core.Rendering;

namespace AgentPointer.Cli.Overlay;

/// <summary>
/// 一帧覆盖层的完整绘制输入。坐标全部是<strong>位图局部坐标</strong>（已减去窗口原点）。
/// </summary>
internal sealed record RenderFrame(
    RectangleF SignRect,
    PointF SignCenter,
    PointF Target,
    ArrowColor Color,
    float SignFontSize,
    string? Text,
    LookSpec Look,
    ArrowMetrics Metrics,
    double DpiScale,
    double AnimScale);

/// <summary>
/// 覆盖层渲染器。只负责"把一帧画进 Graphics"，不持有窗口、定时器等任何状态，
/// 因此可以脱离 WinForms 消息循环做离屏验证。
///
/// <para>尺寸来自 <see cref="ArrowMetrics"/>（上游 S/M/L 逐项一致），
/// 颜色语义来自 <see cref="ArrowColor"/>（Rec.709 亮度、contrast/outline 阈值），
/// 圆角是上游的胶囊规则 min(h/2, 30pt)。绘制层次：
/// 投影 → 白描边（可再包细黑边）→ 彩色箭 + 牌子 → 文字。</para>
///
/// <para>箭杆是二次贝塞尔弧（上游默认 bend 的观感）：垂度取杆长的
/// <see cref="PointerStyle.SagRatio"/>、上限 <see cref="PointerStyle.SagMax"/>，
/// 头部沿弧线末端切线摆放。杆的起点在牌子朝目标一侧的边缘中点。</para>
/// </summary>
internal static class OverlayRenderer
{
    public static void DrawFrame(Graphics g, RenderFrame frame)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        g.Clear(Color.Transparent);

        var s = (float)frame.DpiScale;

        // 入场/呼吸缩放：以目标点为锚，让箭头"从目标点长出来"
        if (Math.Abs(frame.AnimScale - 1.0) > 0.0005)
        {
            g.TranslateTransform(frame.Target.X, frame.Target.Y);
            g.ScaleTransform((float)frame.AnimScale, (float)frame.AnimScale);
            g.TranslateTransform(-frame.Target.X, -frame.Target.Y);
        }

        using var geo = ArrowGeometry.Compute(frame, s);

        var fill = ToGdi(frame.Color);
        var outline = ResolveOutlineColor(frame.Look, frame.Color);
        var ow = PointerStyle.OutlineWidth * s;
        var thin = PointerStyle.OutlineThin * s;
        var shadowOffset = PointerStyle.ShadowOffsetY * s;
        var border = frame.Look.Border?.ToLowerInvariant() ?? "shadow";

        // 1) 投影层：整支箭 + 牌子，向下偏移。抗锯齿边缘天然形成软影。
        if (border != "black")
        {
            g.TranslateTransform(0, shadowOffset);
            using (var shadowPen = new Pen(Color.FromArgb(PointerStyle.ShadowAlpha, 0, 0, 0), geo.ShaftWidth))
            {
                shadowPen.StartCap = LineCap.Round;
                shadowPen.EndCap = LineCap.Round;
                g.DrawPath(shadowPen, geo.ShaftPath);
            }

            using (var shadowHead = new SolidBrush(Color.FromArgb(PointerStyle.ShadowAlpha, 0, 0, 0)))
            {
                g.FillPath(shadowHead, geo.HeadPath);
            }

            using (var shadowSign = new SolidBrush(Color.FromArgb(PointerStyle.ShadowAlpha, 0, 0, 0)))
            {
                using var shadowPath = RoundedSign(frame.SignRect, s, frame.Look.Corners);
                g.FillPath(shadowSign, shadowPath);
            }

            g.TranslateTransform(0, -shadowOffset);
        }

        // 2) 描边层：粗白描边先铺，保证任意背景上都看得见。
        //    头部描边必须用"宽笔画路径 + 圆角连接"，不能用顶点外扩的三角形——
        //    三角形顶点外扩得不到等宽描边，角上会戳出尖刺。
        using (var edgePen = new Pen(outline, geo.ShaftWidth + 2 * ow))
        {
            edgePen.StartCap = LineCap.Round;
            edgePen.EndCap = LineCap.Round;
            g.DrawPath(edgePen, geo.ShaftPath);
        }

        using (var edgeHeadPen = new Pen(outline, 2 * ow)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        })
        {
            g.DrawPath(edgeHeadPen, geo.HeadPath);
        }

        // white-black 档：白描边之外再包一圈细黑边
        if (border == "white-black")
        {
            var thinColor = ToGdi(frame.Color.Outline);

            using (var thinPen = new Pen(thinColor, geo.ShaftWidth + 2 * ow + 2 * thin)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            })
            {
                g.DrawPath(thinPen, geo.ShaftPath);
            }

            using (var thinHeadPen = new Pen(thinColor, 2 * (ow + thin))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            })
            {
                g.DrawPath(thinHeadPen, geo.HeadPath);
            }
        }

        // 3) 彩色层：牌子最后画，盖住箭杆在牌子内侧的起笔
        using (var shaftPen = new Pen(fill, geo.ShaftWidth))
        {
            shaftPen.StartCap = LineCap.Round;
            shaftPen.EndCap = LineCap.Round;
            g.DrawPath(shaftPen, geo.ShaftPath);
        }

        using (var headBrush = new SolidBrush(fill))
        {
            g.FillPath(headBrush, geo.HeadPath);
        }

        DrawSign(g, frame, fill, outline, border, s);
    }

    // ---------------- 牌子 ----------------

    private static void DrawSign(
        Graphics g, RenderFrame frame, Color fill, Color outline, string border, float s)
    {
        using var path = RoundedSign(frame.SignRect, s, frame.Look.Corners);
        using (var fillBrush = new SolidBrush(Color.FromArgb(28, 33, 44)))
        {
            g.FillPath(fillBrush, path);
        }

        var signEdge = frame.Look.BorderColor is not null ? outline : Color.FromArgb(83, 94, 113);
        using var edgePen = new Pen(signEdge, Math.Max(1f, s));
        g.DrawPath(edgePen, path);

        using var accent = new Pen(fill, 3f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(accent, frame.SignRect.Left + 5f * s, frame.SignRect.Top + 13f * s,
            frame.SignRect.Left + 5f * s, frame.SignRect.Bottom - 13f * s);

        var pad = PointerStyle.SignPadX * s;
        var headerHeight = PointerStyle.SignHeaderHeight * s;
        var top = frame.SignRect.Top + PointerStyle.SignPadY * s;
        var header = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "AI 助手提示" : "AI ASSISTANT";
        using var headerFont = ResolveFont(11f * s / (g.DpiX / 96f), HasCjk(header));
        using var headerBrush = new SolidBrush(Color.FromArgb(163, 177, 198));
        g.DrawString(header, headerFont, headerBrush, frame.SignRect.Left + pad, top);

        if (string.IsNullOrWhiteSpace(frame.Text))
        {
            return;
        }

        // 字号在上游 fontSize 基础上随测量档位修正（MeasureSign 可能缩过字号）；
        // 含 CJK 的文字显式优选中文字体，避免依赖 GDI+ 的字体替换链（不同机器行为不一致）
        var fontPx = frame.SignFontSize * s / (g.DpiX / 96f);
        using var font = ResolveFont(fontPx, HasCjk(frame.Text));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit,
        };

        var textColor = frame.Look.TextColor is { } named
            ? ToGdi(ArrowColor.Parse(named))
            : Color.FromArgb(245, 247, 251);

        using var textBrush = new SolidBrush(textColor);

        var inner = new RectangleF(
            frame.SignRect.X + pad,
            top + headerHeight,
            Math.Max(1f, frame.SignRect.Width - pad * 2),
            Math.Max(1f, frame.SignRect.Height - PointerStyle.SignPadY * s * 2 - headerHeight));
        g.DrawString(frame.Text, font, textBrush, inner, format);
    }

    /// <summary>上游 SignCorners 规则：round = 胶囊 min(h/2, 30pt)，sharp = 直角。</summary>
    private static GraphicsPath RoundedSign(RectangleF rect, float scale, string? corners)
    {
        if (corners?.ToLowerInvariant() == "sharp")
        {
            var path = new GraphicsPath();
            path.AddRectangle(rect);
            return path;
        }

        var radius = MathF.Min(rect.Height / 2f, PointerStyle.SignRadius * scale);
        return RoundedRect(rect, radius);
    }

    // ---------------- 颜色 ----------------

    private static Color ResolveOutlineColor(LookSpec look, ArrowColor baseColor)
    {
        if (look.BorderColor is { } named)
        {
            return ToGdi(ArrowColor.Parse(named));
        }

        // 默认白描边；白色箭头配白描边没有意义，换成深色（上游 darkThreshold 语义）
        return baseColor.Luminance > ArrowColor.DarkThreshold && IsWhite(baseColor)
            ? Color.FromArgb(70, 26, 26, 26)
            : Color.White;
    }

    private static bool IsWhite(ArrowColor c) => c.R > 0.92f && c.G > 0.92f && c.B > 0.92f;

    internal static Color ToGdi(ArrowColor c)
    {
        var (r, g, b) = c.ToBytes();
        return Color.FromArgb(255, r, g, b);
    }

    // ---------------- 几何 ----------------

    /// <summary>弧形箭头的一次性几何缓存。</summary>
    private sealed class ArrowGeometry : IDisposable
    {
        public required GraphicsPath ShaftPath { get; init; }
        public required GraphicsPath HeadPath { get; init; }
        public required float ShaftWidth { get; init; }

        public static ArrowGeometry Compute(RenderFrame frame, float s)
        {
            var metrics = frame.Metrics;
            var shaftWidth = MathF.Max(4f, metrics.Stroke * s);
            var headLength = metrics.HeadLength * s;
            var headWidth = metrics.HeadWidth * s;

            // 杆的起点：牌子朝目标一侧边缘的中点——箭顶着牌子，不从牌子底下钻出来
            var start = EdgeToward(frame.SignRect, frame.SignCenter, frame.Target);
            var target = frame.Target;

            var dx = target.X - start.X;
            var dy = target.Y - start.Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1)
            {
                start = new PointF(start.X - 1, start.Y);
                dx = target.X - start.X;
                dy = target.Y - start.Y;
                len = MathF.Sqrt(dx * dx + dy * dy);
            }

            // 弧线垂度：垂直于杆方向，偏好屏幕上方（像被轻轻提起）
            var nx = dy / len;
            var ny = -dx / len;
            if (ny > 0 || (MathF.Abs(dy) < 0.01f && nx < 0))
            {
                nx = -nx;
                ny = -ny;
            }

            var straight = frame.Look.Shape is "straight";
            var sag = straight
                ? 0f
                : MathF.Min(len * PointerStyle.SagRatio, PointerStyle.SagMax * s);

            var mid = new PointF((start.X + target.X) / 2f, (start.Y + target.Y) / 2f);
            var control = new PointF(mid.X + nx * sag, mid.Y + ny * sag);

            // 头部沿弧线末端切线摆放，尖端落在目标点上
            var hx = target.X - control.X;
            var hy = target.Y - control.Y;
            var hl = MathF.Max(1f, MathF.Sqrt(hx * hx + hy * hy));
            var huX = hx / hl;
            var huY = hy / hl;

            var headBase = new PointF(target.X - huX * headLength, target.Y - huY * headLength);
            var pX = -huY;
            var pY = huX;

            var baseLeft = new PointF(headBase.X + pX * headWidth / 2f, headBase.Y + pY * headWidth / 2f);
            var baseRight = new PointF(headBase.X - pX * headWidth / 2f, headBase.Y - pY * headWidth / 2f);

            // 二次贝塞尔 (start, control, headBase) 按标准公式转成三次：
            // 与二次曲线完全重合，且末端切向 = control → headBase 方向
            var shaft = new GraphicsPath();
            shaft.AddBezier(
                start,
                new PointF(
                    start.X + (control.X - start.X) * 2f / 3f,
                    start.Y + (control.Y - start.Y) * 2f / 3f),
                new PointF(
                    headBase.X + (control.X - headBase.X) * 2f / 3f,
                    headBase.Y + (control.Y - headBase.Y) * 2f / 3f),
                headBase);

            var head = new GraphicsPath();
            head.AddPolygon(new[] { target, baseLeft, baseRight });

            return new ArrowGeometry
            {
                ShaftPath = shaft,
                HeadPath = head,
                ShaftWidth = shaftWidth,
            };
        }

        public void Dispose()
        {
            ShaftPath.Dispose();
            HeadPath.Dispose();
        }
    }

    /// <summary>牌子四条边里朝目标那一条的中点。</summary>
    private static PointF EdgeToward(RectangleF sign, PointF center, PointF target)
    {
        var dx = target.X - center.X;
        var dy = target.Y - center.Y;

        if (MathF.Abs(dx) * sign.Height >= MathF.Abs(dy) * sign.Width)
        {
            return dx >= 0
                ? new PointF(sign.Right, center.Y)
                : new PointF(sign.Left, center.Y);
        }

        return dy >= 0
            ? new PointF(center.X, sign.Bottom)
            : new PointF(center.X, sign.Top);
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var rr = MathF.Min(radius, MathF.Min(r.Width, r.Height) / 2f);

        path.AddArc(r.Left, r.Top, rr, rr, 180, 90);
        path.AddArc(r.Right - rr, r.Top, rr, rr, 270, 90);
        path.AddArc(r.Right - rr, r.Bottom - rr, rr, rr, 0, 90);
        path.AddArc(r.Left, r.Bottom - rr, rr, rr, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>CJK 检测：覆盖汉字、注音、假名、兼容区与 CJK 标点。</summary>
    internal static bool HasCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var ch in text)
        {
            if (ch is >= '\u2E80' and <= '\u9FFF'
                or >= '\u3000' and <= '\u303F'
                or >= '\uF900' and <= '\uFAFF'
                or >= '\uFF00' and <= '\uFFEF')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>字体回退链。含中文时优先雅黑（不依赖 GDI+ 字体替换），否则 Segoe UI 优先。</summary>
    internal static Font ResolveFont(float size, bool preferCjk = false)
    {
        var families = preferCjk
            ? new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial" }
            : new[] { "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", "Arial" };

        foreach (var family in families)
        {
            try
            {
                return new Font(family, size, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
                // 该机器没装这个字体，试下一个
            }
        }

        return new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold, GraphicsUnit.Pixel);
    }
}
