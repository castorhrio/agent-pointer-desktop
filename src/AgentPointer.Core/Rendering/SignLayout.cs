using System.Text;

namespace AgentPointer.Core.Rendering;

/// <summary>牌子正文里一段内容的种类。</summary>
public enum SignRunKind
{
    /// <summary>普通文字。</summary>
    Text,

    /// <summary><c>{{value}}</c> 复制芯片。</summary>
    Value,
}

/// <summary>
/// 排好的一段内容：位置与尺寸在<strong>牌子正文区坐标系</strong>里
/// （调用方给什么坐标系就是什么坐标系，绘制必须用同一套）。只用 float，Core 不认识 System.Drawing。
/// </summary>
public readonly record struct SignRun(
    string Text,
    SignRunKind Kind,
    int ValueIndex,
    int Line,
    float X,
    float Y,
    float Width,
    float Height)
{
    /// <summary>该段右边缘。</summary>
    public float Right => X + Width;

    /// <summary>该段下边缘。</summary>
    public float Bottom => Y + Height;

    /// <summary>是否是 <c>{{value}}</c> 芯片（可点、可复制）。</summary>
    public bool IsValue => Kind == SignRunKind.Value;
}

/// <summary>一个复制芯片的命中矩形（坐标系同 <see cref="SignRun"/>）。</summary>
public readonly record struct SignValueChip(int Index, string Value, float X, float Y, float Width, float Height)
{
    /// <summary>右边缘。</summary>
    public float Right => X + Width;

    /// <summary>下边缘。</summary>
    public float Bottom => Y + Height;

    /// <summary>点是否落在芯片里。</summary>
    public bool Contains(float px, float py) =>
        px >= X && px <= X + Width && py >= Y && py <= Y + Height;
}

/// <summary>芯片的尺寸参数（逻辑像素，调用方已按 DPI 缩放）。</summary>
public readonly record struct SignChipStyle(float PadX, float Gap, float IconSize, float Margin)
{
    /// <summary>与 <c>OverlayRenderer</c> 里的常量一致：6 / 4 / 13 / 3。</summary>
    public static SignChipStyle Default => new(6f, 4f, 13f, 3f);
}

/// <summary>
/// 一次排版的全部结果。<see cref="ContentWidth"/> / <see cref="ContentHeight"/> 是内容相对
/// 传入原点（<c>left</c> / <c>top</c>）的包围盒，量牌子尺寸直接用它们——
/// 于是"量的"和"画的"来自同一份数据，不可能再各算各的。
/// </summary>
public sealed record SignLayoutResult(
    IReadOnlyList<SignRun> Runs,
    IReadOnlyList<SignValueChip> Chips,
    int LineCount,
    float LineHeight,
    float ContentWidth,
    float ContentHeight,
    bool Truncated);

/// <summary>
/// 牌子正文的<strong>纯</strong>排版：把 <c>{{value}}</c> 记号切成文字段与芯片段，按换行规则排到若干行。
/// 完全不碰 GDI+：量文字由调用方以 <paramref name="measureText"/> 注入，
/// 所以这条规则能被单测锁住，渲染、命中判定、量尺寸也都共用同一份结果。
/// </summary>
public static class SignLayout
{
    /// <summary>
    /// 排出牌子的正文。
    /// <list type="bullet">
    /// <item>段与段严格按原文顺序排布（芯片前面的文字也必须排出来，且计入宽度）；</item>
    /// <item>拉丁词按空白断，CJK 逐字断；芯片整体不拆开，放不下就整块挪到下一行，
    /// 比正文区还宽时压到正文区宽，绝不越界；</item>
    /// <item>断词只为换行：同一行上相邻的文字合排成一段，放得下就整句一个 run；</item>
    /// <item><paramref name="maxLines"/> 大于 0 时超出即停并置 <c>Truncated</c>（调用方据此缩小字号）。</item>
    /// </list>
    /// </summary>
    /// <param name="text">牌子原文（可含 <c>{{value}}</c>）。</param>
    /// <param name="measureText">量一段文字的宽度，坐标系与 <paramref name="left"/> / <paramref name="top"/> 一致。</param>
    /// <param name="left">正文区左边缘。</param>
    /// <param name="top">正文区上边缘。</param>
    /// <param name="width">正文区可用宽度。</param>
    /// <param name="lineHeight">单行高度（有芯片时会自动抬高到装得下芯片图标）。</param>
    /// <param name="chipStyle">芯片内边距 / 间隔 / 图标 / 与相邻文字的呼吸。</param>
    /// <param name="maxLines">最多几行，0 表示不限。</param>
    public static SignLayoutResult Build(
        string? text,
        Func<string, float> measureText,
        float left,
        float top,
        float width,
        float lineHeight,
        SignChipStyle chipStyle,
        int maxLines = 0)
    {
        var segments = SignText.Parse(text);
        var hasChip = segments.Any(segment => segment.IsValue);

        // 行高至少要装得下芯片里的图标，否则芯片会压到上下两行
        var height = MathF.Max(1f, lineHeight);
        if (hasChip) height = MathF.Max(height, chipStyle.IconSize + 6f);

        var right = left + MathF.Max(1f, width);
        var runs = new List<SignRun>();
        var chips = new List<SignValueChip>();
        var row = top;
        var line = 0;
        var x = left;
        var valueIndex = 0;
        var truncated = false;

        // 同一行上相邻的文字合排成一段：装得下就整句一个 run，换行处才断开。
        var pending = new StringBuilder();
        var pendingLine = 0;
        var pendingRow = top;
        var pendingX = left;
        var pendingWidth = 0f;

        void FlushText()
        {
            if (pending.Length == 0) return;
            runs.Add(new SignRun(pending.ToString(), SignRunKind.Text, -1, pendingLine, pendingX, pendingRow, pendingWidth, height));
            pending.Clear();
            pendingWidth = 0f;
        }

        // 行首永远放得下：只有"这一行已经放了东西"时才换行
        void NewLine()
        {
            FlushText();
            x = left;
            row += height;
            line++;
        }

        foreach (var segment in segments)
        {
            if (segment.IsValue)
            {
                var chipWidth = chipStyle.PadX * 2f
                    + MathF.Max(0f, measureText(segment.Text))
                    + chipStyle.Gap
                    + chipStyle.IconSize;

                // 芯片整体不拆开：放不下就整块挪到下一行，行首不留多余呼吸
                if (x > left && x + chipWidth + chipStyle.Margin > right) NewLine();
                if (maxLines > 0 && line >= maxLines)
                {
                    truncated = true;
                    break;
                }

                // 比整个正文区还宽的芯片压到正文区宽，绝不越界
                chipWidth = MathF.Min(chipWidth, right - left);
                var chipX = x + (x > left ? chipStyle.Margin : 0f);
                FlushText();
                runs.Add(new SignRun(segment.Text, SignRunKind.Value, valueIndex, line, chipX, row, chipWidth, height));
                chips.Add(new SignValueChip(valueIndex, segment.Text, chipX, row, chipWidth, height));

                valueIndex++;
                x = chipX + chipWidth + chipStyle.Margin;
                continue;
            }

            foreach (var token in Tokenize(segment.Text))
            {
                var tokenWidth = MathF.Max(0f, measureText(token));
                if (x > left && x + tokenWidth > right) NewLine();
                if (maxLines > 0 && line >= maxLines)
                {
                    truncated = true;
                    break;
                }

                if (pending.Length == 0)
                {
                    pendingX = x;
                    pendingRow = row;
                    pendingLine = line;
                }

                pending.Append(token);
                pendingWidth += tokenWidth;
                x += tokenWidth;
            }

            if (truncated) break;
        }

        FlushText();

        var contentWidth = 0f;
        var contentHeight = 0f;
        var lineCount = 0;

        foreach (var run in runs)
        {
            contentWidth = MathF.Max(contentWidth, run.Right - left);
            contentHeight = MathF.Max(contentHeight, run.Bottom - top);
            lineCount = Math.Max(lineCount, run.Line + 1);
        }

        return new SignLayoutResult(runs, chips, lineCount, height, contentWidth, contentHeight, truncated);
    }

    /// <summary>
    /// 切词：拉丁词按空白断，CJK 逐字断（中文没有空格，不逐字断就永远换不了行）。
    /// 空白挂在上一段尾部，拼回去仍是原文。
    /// </summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                // 空白挂在上一段尾部，到此收一段：拉丁文按空白断，
                // 否则整句会连成一个永远换不了行的长 token
                builder.Append(c);
                yield return builder.ToString();
                builder.Clear();
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
}
