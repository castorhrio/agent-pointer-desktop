using System.Text;

namespace AgentPointer.Core.Rendering;

/// <summary>牌子文字的一段。<see cref="IsValue"/> 为 true 时是可点的复制芯片。</summary>
public readonly record struct SignSegment(string Text, bool IsValue);

/// <summary>
/// 牌子文字的纯解析：把 <c>{{value}}</c> 记号切成"普通文字"和"复制值"。
/// 特意放在 Core 且不依赖任何 UI——这条规则必须能被单测锁住，
/// 渲染器、朗读、--json 也都由它给出同一份解释。
/// </summary>
public static class SignText
{
    /// <summary>
    /// 按 <c>{{value}}</c> 切段。规则：
    /// <list type="bullet">
    /// <item>内容 trim 后非空的 <c>{{...}}</c> 才是一段复制值，内容也一并 trim；</item>
    /// <item>落单的 <c>{{</c>（没有收尾的 <c>}}</c>）和空的 <c>{{}}</c> 都只是普通文字，不产生按钮；</item>
    /// <item>多段复制值、紧挨着的复制值都允许。</item>
    /// </list>
    /// 复制值的花括号只属于记号：把段落按原顺序拼起来，得到的正是原文少了这些花括号的样子
    /// （和 <see cref="PlainText"/> 一致，唯一的例外是留在普通文字里的空记号 <c>{{}}</c>）。
    /// </summary>
    public static IReadOnlyList<SignSegment> Parse(string? text)
    {
        var segments = new List<SignSegment>();
        if (string.IsNullOrEmpty(text)) return segments;

        var literal = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                literal.Append(text, index, text.Length - index);
                break;
            }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                // 落单的 {{：剩下全是普通文字
                literal.Append(text, index, text.Length - index);
                break;
            }

            var value = text[(open + 2)..close].Trim();
            if (value.Length == 0)
            {
                // 空记号 {{}} 不是按钮，原样留作普通文字
                literal.Append(text, index, close + 2 - index);
                index = close + 2;
                continue;
            }

            literal.Append(text, index, open - index);
            if (literal.Length > 0)
            {
                segments.Add(new SignSegment(literal.ToString(), false));
                literal.Clear();
            }

            segments.Add(new SignSegment(value, true));
            index = close + 2;
        }

        if (literal.Length > 0) segments.Add(new SignSegment(literal.ToString(), false));
        return segments;
    }

    /// <summary>
    /// 牌子上的可见文字：去掉复制记号的花括号（它们只是按钮记号，不是要显示的字符）。
    /// 落单的、空的记号也一并去掉，保证 JSON / 朗读和肉眼看到的一致。
    /// </summary>
    public static string PlainText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '{' or '}') continue;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>牌子上的全部复制值，按出现顺序。用于 <c>--json</c> 的 values 字段。</summary>
    public static IReadOnlyList<string> Values(string? text) =>
        Parse(text).Where(segment => segment.IsValue).Select(segment => segment.Text).ToArray();
}
