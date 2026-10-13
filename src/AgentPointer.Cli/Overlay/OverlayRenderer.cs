using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
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
    double AnimScale,
    bool CloseButton = false,
    /// <summary>刚复制成功、正在显示 ✓ 的芯片序号（约 1.5 秒后清空）。</summary>
    int? CopiedChip = null);

/// <summary>可点区域。<see cref="None"/> 表示该点继续穿透到下层应用。</summary>
internal enum OverlayHit
{
    None,
    Shaft,
    Sign,
    Close,

    /// <summary><c>{{value}}</c> 复制芯片：点它只复制，不收场。</summary>
    Chip,
}

/// <summary>一个复制芯片：命中矩形 + 要复制的值 + 出现顺序。</summary>
internal sealed record SignChip(RectangleF Rect, string Value, int Index);

/// <summary>
/// 可点区域缓存（窗口客户区坐标）。布局确定后算一次，动画期间不变。
/// <see cref="Shaft"/> 是已按描边宽度外扩过的区域——用原始细路径判命中，用户几乎点不中。
/// </summary>
internal sealed class HitRegions : IDisposable
{
    public required GraphicsPath Shaft { get; init; }
    public required RectangleF Sign { get; init; }
    public RectangleF? Close { get; init; }

    /// <summary>复制芯片的命中区域，按出现顺序；牌子没有 <c>{{value}}</c> 时为空。</summary>
    public SignChip[] Chips { get; init; } = Array.Empty<SignChip>();

    /// <summary>箭头尖端（目标点，窗口客户区坐标）。</summary>
    public required PointF Target { get; init; }

    /// <summary>
    /// 尖端周围"永远穿透"的圆半径（约 1.5 倍箭头长度）。见
    /// <see cref="OverlayRenderer.TargetExclusionRadiusFactor"/>。
    /// </summary>
    public required float TargetExclusionRadius { get; init; }

    public void Dispose() => Shaft.Dispose();
}

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
    /// <summary>--close-button 圆形 × 的直径（逻辑像素）。</summary>
    internal const float CloseButtonDiameter = 22f;

    /// <summary>
    /// 尖端排除区半径 = 该系数 × 箭头长度。头部本身和紧挨头部的杆末段都落在圆里，
    /// 那里点下去必须穿透给下层应用；1.5 倍的余量让"点在目标旁边一点"也不算我们的。
    /// </summary>
    internal const float TargetExclusionRadiusFactor = 1.5f;

    /// <summary>× 与牌子边缘、正文之间的留白（逻辑像素）。</summary>
    internal const float CloseButtonPadding = 8f;

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

        if (frame.CloseButton)
        {
            DrawCloseButton(g, frame, outline, s);
        }
    }

    // ---------------- 命中区域 ----------------

    /// <summary>
    /// 计算可点区域（窗口客户区坐标）。<b>布局定下来后只调用一次</b>：
    /// 入场/呼吸动画只改呈现缩放，不改命中判定。
    ///
    /// <para>只包含牌子、箭杆和关闭按钮：目标附近保持穿透，用户点到目标时点击照常送达下层应用。</para>
    /// </summary>
    internal static HitRegions ComputeHitRegions(RenderFrame frame, float scale)
    {
        var geometry = ArrowGeometry.Compute(frame, scale);

        // 外扩一圈：细箭杆按原始路径判命中，实际上很难点中
        var widened = (GraphicsPath)geometry.ShaftPath.Clone();
        var hitWidth = geometry.ShaftWidth + 2 * MathF.Max(PointerStyle.OutlineWidth, 6f) * scale;
        using (var pen = new Pen(Color.Black, hitWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        })
        {
            widened.Widen(pen);
        }

        geometry.Dispose();

        return new HitRegions
        {
            Shaft = widened,
            Sign = frame.SignRect,
            Close = frame.CloseButton ? CloseButtonRect(frame.SignRect, scale) : null,
            Chips = ComputeChips(frame, scale),
            Target = frame.Target,
            TargetExclusionRadius = frame.Metrics.HeadLength * TargetExclusionRadiusFactor * scale,
        };
    }

    /// <summary>
    /// 芯片的命中矩形。量文字要一节 <see cref="Graphics"/>，所以单独用一个 1x1 位图；
    /// 字号公式与 <see cref="BuildTextLayout"/> 完全一致（都按 96 DPI 的位图算），矩形才对得上。
    /// </summary>
    private static SignChip[] ComputeChips(RenderFrame frame, float scale)
    {
        if (string.IsNullOrWhiteSpace(frame.Text)) return Array.Empty<SignChip>();

        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        using var layout = BuildTextLayout(g, frame, scale);
        return layout.Chips;
    }

    /// <summary>命中判定。关闭按钮优先于牌子，箭头不算——那里点下去应该给到目标。</summary>
    internal static OverlayHit HitTest(HitRegions? regions, PointF point)
    {
        if (regions is null) return OverlayHit.None;

        // 尖端周围永远穿透，优先级高于牌子与箭杆：目标及其旁边一圈的点击必须送达下层应用，
        // 否则箭头头部画在目标之上时会把这一次点击吞掉。
        if (regions.TargetExclusionRadius > 0f)
        {
            var dx = point.X - regions.Target.X;
            var dy = point.Y - regions.Target.Y;
            if (dx * dx + dy * dy <= regions.TargetExclusionRadius * regions.TargetExclusionRadius)
            {
                return OverlayHit.None;
            }
        }

        if (regions.Close is { } close && close.Contains(point)) return OverlayHit.Close;
        if (ChipAt(regions, point) is not null) return OverlayHit.Chip;
        if (regions.Sign.Contains(point)) return OverlayHit.Sign;
        return regions.Shaft.IsVisible(point) ? OverlayHit.Shaft : OverlayHit.None;
    }

    /// <summary>命中的复制芯片序号，没命中返回 null。点芯片只复制，不收场。</summary>
    internal static int? ChipAt(HitRegions? regions, PointF point)
    {
        if (regions is null) return null;

        foreach (var chip in regions.Chips)
        {
            if (chip.Rect.Contains(point)) return chip.Index;
        }

        return null;
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
        var header = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "AI 助手提示" : "AI ASSISTANT";
        using var headerFont = ResolveFont(11f * s / (g.DpiX / 96f), HasCjk(header));
        using var headerBrush = new SolidBrush(Color.FromArgb(163, 177, 198));
        g.DrawString(header, headerFont, headerBrush, frame.SignRect.Left + pad,
            frame.SignRect.Top + PointerStyle.SignPadY * s);

        if (string.IsNullOrWhiteSpace(frame.Text))
        {
            return;
        }

        var textColor = frame.Look.TextColor is { } named
            ? ToGdi(ArrowColor.Parse(named))
            : Color.FromArgb(245, 247, 251);

        using var textBrush = new SolidBrush(textColor);

        // 正文按 run 排：普通文字一段段画，{{value}} 画成可点的复制芯片
        using var layout = BuildTextLayout(g, frame, s);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit,
        };

        foreach (var run in layout.Runs)
        {
            if (run.Segment.IsValue)
            {
                DrawValueChip(g, frame, run, layout.Font, textBrush, outline, s);
                continue;
            }

            g.DrawString(run.Segment.Text, layout.Font, textBrush, run.Rect, format);
        }
    }

    // ---------------- 牌子正文：文字 + {{value}} 复制芯片 ----------------

    /// <summary>芯片内部的水平留白（逻辑像素）。</summary>
    internal const float ChipPadX = 6f;

    /// <summary>芯片文字与图标之间的间隔（逻辑像素）。</summary>
    internal const float ChipGap = 4f;

    /// <summary>芯片里复制图标 / 对钩的边长（逻辑像素）。</summary>
    internal const float ChipIconSize = 13f;

    /// <summary>芯片与相邻文字之间的呼吸（逻辑像素）。</summary>
    private const float ChipMargin = 3f;

    private static readonly Color ChipBackground = Color.FromArgb(255, 34, 40, 54);
    private static readonly Color CopyCheckColor = Color.FromArgb(255, 76, 217, 130);

    /// <summary>牌子上的一段文字（普通文字或芯片）及其排布矩形。</summary>
    private sealed record SignRun(SignSegment Segment, RectangleF Rect, int ValueIndex);

    private sealed class SignTextLayout : IDisposable
    {
        public required Font Font { get; init; }
        public required SignRun[] Runs { get; init; }
        public required SignChip[] Chips { get; init; }

        public void Dispose() => Font.Dispose();
    }

    /// <summary>
    /// 量牌子的自然尺寸（逻辑像素，已含内边距、提示标题行和关闭按钮占位）。
    ///
    /// <para>必须走与绘制相同的 <see cref="BuildTextLayout"/>：直接对纯文本
    /// <c>MeasureString</c> 量不到 <c>{{value}}</c> 芯片多出来的内边距、间隔和图标，
    /// 量出来的卡片就装不下芯片，芯片会溢出或者被换行挤出去。</para>
    ///
    /// <para><paramref name="maxWidth"/> 是牌子允许的最大宽度（含内边距）。正文超过
    /// <see cref="ArrowMetrics.MaximumLines"/> 行时返回的 <c>Truncated</c> 为 true，
    /// 调用方据此缩小字号；高度已按行数封顶。</para>
    /// </summary>
    internal static (Size Size, bool Truncated) MeasureSignText(
        Graphics g, string? text, float fontSize, bool closeButton, int maxWidth)
    {
        const float s = 1f;

        var pad = PointerStyle.SignPadX * s;
        var closeReserve = closeButton ? (CloseButtonDiameter + CloseButtonPadding * 2) * s : 0f;
        var cardWidth = MathF.Max(80f, maxWidth);

        // 用一块"宽度就是上限、高度足够长"的假牌子把正文排一遍，尺寸自然就出来了
        var frame = new RenderFrame(
            SignRect: new RectangleF(0, 0, cardWidth, 100_000f),
            SignCenter: new PointF(cardWidth / 2f, 0f),
            Target: new PointF(cardWidth / 2f, 1f),
            Color: ArrowColor.Parse("red"),
            SignFontSize: fontSize,
            Text: text,
            Look: new LookSpec(),
            Metrics: ArrowMetrics.For(ArrowSize.Medium),
            DpiScale: s,
            AnimScale: 1d,
            CloseButton: closeButton);

        using var layout = BuildTextLayout(g, frame, s);
        var area = SignTextArea(frame, s);

        var contentWidth = 0f;
        var bottom = area.Top;
        var lineHeight = 0f;

        foreach (var run in layout.Runs)
        {
            contentWidth = MathF.Max(contentWidth, run.Rect.Right - area.Left);
            bottom = MathF.Max(bottom, run.Rect.Bottom);
            lineHeight = MathF.Max(lineHeight, run.Rect.Height);
        }

        var lines = lineHeight > 0f
            ? Math.Max(1, (int)Math.Round((bottom - area.Top) / lineHeight))
            : 0;

        var size = new Size(
            (int)Math.Ceiling(contentWidth + pad * 2 + closeReserve),
            (int)Math.Ceiling(
                Math.Min(lines, ArrowMetrics.MaximumLines) * lineHeight
                + PointerStyle.SignPadY * 2 + PointerStyle.SignHeaderHeight));

        return (size, lines > ArrowMetrics.MaximumLines);
    }

    /// <summary>
    /// 牌子正文区：扣掉内边距、提示标题行和关闭按钮的占位。
    /// 绘制与命中判定都走这里，两边的矩形才不会错位。
    /// </summary>
    private static RectangleF SignTextArea(RenderFrame frame, float s)
    {
        var pad = PointerStyle.SignPadX * s;
        var top = frame.SignRect.Top + PointerStyle.SignPadY * s + PointerStyle.SignHeaderHeight * s;
        var closeReserve = frame.CloseButton ? (CloseButtonDiameter + CloseButtonPadding * 2) * s : 0f;

        return new RectangleF(
            frame.SignRect.X + pad,
            top,
            Math.Max(1f, frame.SignRect.Width - pad * 2 - closeReserve),
            Math.Max(1f, frame.SignRect.Bottom - PointerStyle.SignPadY * s - top));
    }

    /// <summary>
    /// 把牌子正文排成若干 run：空白与 CJK 处都可以换行，芯片整体不拆开。
    /// 含 <c>{{value}}</c> 时行高至少要装得下芯片里的图标。
    /// </summary>
    private static SignTextLayout BuildTextLayout(Graphics g, RenderFrame frame, float s)
    {
        var area = SignTextArea(frame, s);

        // 字号在上游 fontSize 基础上随测量档位修正（MeasureSign 可能缩过字号）；
        // 含 CJK 的文字显式优选中文字体，避免依赖 GDI+ 的字体替换链（不同机器行为不一致）
        var fontPx = frame.SignFontSize * s / (g.DpiX / 96f);
        var font = ResolveFont(fontPx, HasCjk(frame.Text));

        var segments = SignText.Parse(frame.Text);
        var lineHeight = font.GetHeight(g);
        if (segments.Any(segment => segment.IsValue))
        {
            // 行高至少要装得下芯片里的图标，否则芯片会压到上下两行
            lineHeight = MathF.Max(lineHeight, (ChipIconSize + 6f) * s);
        }

        var runs = new List<SignRun>();
        var chips = new List<SignChip>();
        var x = area.Left;
        var top = area.Top;
        var valueIndex = 0;

        void Wrap(float width)
        {
            if (x + width > area.Right && x > area.Left)
            {
                x = area.Left;
                top += lineHeight;
            }
        }

        foreach (var segment in segments)
        {
            if (segment.IsValue)
            {
                var textWidth = g.MeasureString(segment.Text, font).Width;
                var width = ChipPadX * 2 * s + textWidth + ChipGap * s + ChipIconSize * s;
                Wrap(width + ChipMargin * s);

                var rect = new RectangleF(x + ChipMargin * s, top, width, lineHeight);
                runs.Add(new SignRun(segment, rect, valueIndex));
                chips.Add(new SignChip(rect, segment.Text, valueIndex));

                valueIndex++;
                x = rect.Right + ChipMargin * s;
                continue;
            }

            foreach (var token in Tokenize(segment.Text))
            {
                var width = g.MeasureString(token, font).Width;
                Wrap(width);

                var rect = new RectangleF(x, top, width, lineHeight);
                runs.Add(new SignRun(new SignSegment(token, false), rect, -1));
                x = rect.Right;
            }
        }

        return new SignTextLayout
        {
            Font = font,
            Runs = runs.ToArray(),
            Chips = chips.ToArray(),
        };
    }

    /// <summary>
    /// 切词：拉丁词按空白断，CJK 逐字断（中文没有空格，不逐字断就永远换不了行）。
    /// 空白挂在上一段尾部，拼回去仍是原文。
    /// </summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                builder.Append(c);
                continue;
            }

            if (IsBreakable(c))
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                yield return c.ToString();
                continue;
            }

            builder.Append(c);
        }

        if (builder.Length > 0) yield return builder.ToString();
    }

    /// <summary>可逐字换行的字符（CJK 及其标点、假名、全角形式）。</summary>
    private static bool IsBreakable(char c) => c is
        >= '\u2E80' and <= '\u303F'
        or >= '\u3040' and <= '\u30FF'
        or >= '\uF900' and <= '\uFAFF'
        or >= '\uFF00' and <= '\uFFEF';

    /// <summary>芯片：深色圆角底 + 值文字 + 右侧复制图标（复制成功后变成 ✓）。</summary>
    private static void DrawValueChip(
        Graphics g, RenderFrame frame, SignRun run, Font font, Brush textBrush, Color outline, float s)
    {
        using var path = RoundedRect(run.Rect, MathF.Min(run.Rect.Height / 2f, 6f * s));

        using (var back = new SolidBrush(ChipBackground))
        {
            g.FillPath(back, path);
        }

        using (var edge = new Pen(Color.FromArgb(170, outline), Math.Max(1f, s)))
        {
            g.DrawPath(edge, path);
        }

        var icon = new RectangleF(
            run.Rect.Right - ChipPadX * s - ChipIconSize * s,
            run.Rect.Top + (run.Rect.Height - ChipIconSize * s) / 2f,
            ChipIconSize * s,
            ChipIconSize * s);

        var textLeft = run.Rect.Left + ChipPadX * s;
        var textRect = new RectangleF(
            textLeft,
            run.Rect.Top,
            Math.Max(1f, icon.Left - ChipGap * s - textLeft),
            run.Rect.Height);

        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        })
        {
            g.DrawString(run.Segment.Text, font, textBrush, textRect, format);
        }

        if (frame.CopiedChip == run.ValueIndex)
        {
            DrawCheckIcon(g, icon, s);
        }
        else
        {
            DrawCopyIcon(g, icon, textBrush, s);
        }
    }

    /// <summary>复制图标：两张叠在一起的纸。</summary>
    private static void DrawCopyIcon(Graphics g, RectangleF box, Brush brush, float s)
    {
        var color = brush is SolidBrush solid ? solid.Color : Color.White;
        var size = box.Width * 0.68f;
        var offset = box.Width - size;

        using var pen = new Pen(color, Math.Max(1f, 1.4f * s)) { LineJoin = LineJoin.Round };
        using var back = RoundedRect(new RectangleF(box.Left, box.Top, size, size), size * 0.25f);
        using var front = RoundedRect(new RectangleF(box.Left + offset, box.Top + offset, size, size), size * 0.25f);

        g.DrawPath(pen, back);

        // 前景那张纸先填芯片底色再描边，两张纸才分得开
        using (var fill = new SolidBrush(ChipBackground))
        {
            g.FillPath(fill, front);
        }

        g.DrawPath(pen, front);
    }

    /// <summary>复制成功后的 ✓（约 1.5 秒，之后由 OverlayForm 重绘回复制图标）。</summary>
    private static void DrawCheckIcon(Graphics g, RectangleF box, float s)
    {
        using var pen = new Pen(CopyCheckColor, Math.Max(1.5f, 2f * s))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        g.DrawLines(pen, new[]
        {
            new PointF(box.Left + box.Width * 0.12f, box.Top + box.Height * 0.55f),
            new PointF(box.Left + box.Width * 0.42f, box.Top + box.Height * 0.82f),
            new PointF(box.Left + box.Width * 0.92f, box.Top + box.Height * 0.18f),
        });
    }

    /// <summary>牌子右端、垂直居中的关闭按钮圆。</summary>
    internal static RectangleF CloseButtonRect(RectangleF sign, float scale)
    {
        var diameter = CloseButtonDiameter * scale;
        var pad = CloseButtonPadding * scale;
        return new RectangleF(
            sign.Right - pad - diameter,
            sign.Top + sign.Height / 2f - diameter / 2f,
            diameter,
            diameter);
    }

    /// <summary>
    /// 画关闭按钮。圆形 + 浅色 ×，颜色取自 --close-color / --close-x-color，
    /// 非法取值一律回退默认色——渲染永远不该因为颜色崩掉。
    /// </summary>
    private static void DrawCloseButton(Graphics g, RenderFrame frame, Color outline, float s)
    {
        var circle = CloseButtonRect(frame.SignRect, s);
        var disc = TryParseColor(frame.Look.CloseColor, Color.FromArgb(255, 60, 70, 88));
        var cross = TryParseColor(frame.Look.CloseXColor, Color.FromArgb(255, 235, 240, 248));

        using (var brush = new SolidBrush(disc))
        {
            g.FillEllipse(brush, circle);
        }

        using (var ring = new Pen(outline, Math.Max(1f, s)))
        {
            g.DrawEllipse(ring, circle);
        }

        var inset = circle.Width * 0.32f;
        using var pen = new Pen(cross, Math.Max(1.5f, 2f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, circle.Left + inset, circle.Top + inset, circle.Right - inset, circle.Bottom - inset);
        g.DrawLine(pen, circle.Right - inset, circle.Top + inset, circle.Left + inset, circle.Bottom - inset);
    }

    private static Color TryParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            return ToGdi(ArrowColor.Parse(value));
        }
        catch (ArgumentException)
        {
            return fallback;
        }
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
