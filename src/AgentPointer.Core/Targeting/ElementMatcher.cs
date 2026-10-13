using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentPointer.Core.Targeting;

/// <summary>
/// 一个可匹配的可访问性元素。坐标为全局左上角逻辑点。
/// </summary>
public sealed record ElementInfo(
    string Name,
    string Role,
    string? AutomationId,
    int X,
    int Y,
    int Width,
    int Height,
    bool Enabled,
    bool Offscreen);

/// <summary>
/// 平台无关的元素匹配逻辑，语义对齐上游 bigarrow 的 ElementMatcher：
/// 精确 &gt; 前缀 &gt; 包含（包含须命中完整词，避免 "Save" 命中 "Unsaved changes"），
/// 角色按别名归一，同分时偏好可见、面积小、可用、名字更短者。
/// </summary>
public static class ElementMatcher
{
    private const int ExactScore = 3;
    private const int PrefixScore = 2;
    private const int ContainsScore = 1;

    private static readonly Dictionary<string, string> RoleAliases = new(StringComparer.Ordinal)
    {
        ["link"] = "hyperlink",
        ["textfield"] = "edit",
        ["field"] = "edit",
        ["textbox"] = "edit",
        ["input"] = "edit",
        ["statictext"] = "text",
        ["label"] = "text",
        ["tab"] = "tabitem",
        ["popupbutton"] = "combobox",
        ["dropdown"] = "combobox",
        ["row"] = "dataitem",
        ["cell"] = "dataitem",
        ["menuitem"] = "menuitem",
        ["checkbox"] = "checkbox",
    };

    /// <summary>
    /// 归一化角色名：小写、去掉 "controltype." 前缀、去掉 AX 前缀（AXButton -&gt; button）、
    /// 再去掉分隔符并套用别名。
    /// </summary>
    public static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return string.Empty;
        }

        var value = role.Trim().ToLowerInvariant();

        const string controlTypePrefix = "controltype.";
        if (value.StartsWith(controlTypePrefix, StringComparison.Ordinal))
        {
            value = value[controlTypePrefix.Length..];
        }

        value = value.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);

        // "axbutton" -> "button"，仅当 ax 后面确实跟着字母时才剥离。
        if (value.Length > 2 && value[0] == 'a' && value[1] == 'x' && char.IsLetter(value[2]))
        {
            value = value[2..];
        }

        return RoleAliases.TryGetValue(value, out var alias) ? alias : value;
    }

    /// <summary>
    /// 按 label（可选的 role 过滤）排序候选元素，最匹配的排在最前。
    /// </summary>
    public static IReadOnlyList<ElementInfo> Rank(IEnumerable<ElementInfo> items, string label, string? role)
    {
        ArgumentNullException.ThrowIfNull(items);

        var needle = label?.Trim() ?? string.Empty;
        var roleFilter = string.IsNullOrWhiteSpace(role) ? null : NormalizeRole(role);

        var candidates = new List<(ElementInfo Item, int Score, int Index)>();
        var index = 0;

        foreach (var item in items)
        {
            var position = index++;
            if (item is null || item.Width <= 0 || item.Height <= 0)
            {
                continue;
            }

            if (roleFilter is not null &&
                !string.Equals(NormalizeRole(item.Role), roleFilter, StringComparison.Ordinal))
            {
                continue;
            }

            var score = Score(item, needle);
            if (score == 0)
            {
                continue;
            }

            candidates.Add((item, score, position));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Item.Offscreen)
            .ThenBy(c => (long)c.Item.Width * c.Item.Height)
            .ThenByDescending(c => c.Item.Enabled)
            .ThenBy(c => (c.Item.Name ?? string.Empty).Trim().Length)
            .ThenBy(c => c.Index)
            .Select(c => c.Item)
            .ToList();
    }

    /// <summary>
    /// 最接近 label 的已有名字，用于 "没找到" 时提示用户。
    /// </summary>
    public static IReadOnlyList<string> Suggestions(IEnumerable<ElementInfo> items, string label, int max = 10)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (max <= 0)
        {
            return Array.Empty<string>();
        }

        var needle = label?.Trim() ?? string.Empty;

        return items
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => i.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new
            {
                Name = name,
                Similarity = Similarity(needle, name),
                Distance = Distance(needle.ToLowerInvariant(), name.ToLowerInvariant()),
            })
            .OrderByDescending(x => x.Similarity)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    private static int Score(ElementInfo item, string label)
    {
        if (label.Length == 0)
        {
            return 0;
        }

        var name = item.Name?.Trim() ?? string.Empty;
        var automationId = item.AutomationId?.Trim();

        if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(automationId) &&
             string.Equals(automationId, label, StringComparison.OrdinalIgnoreCase)))
        {
            return ExactScore;
        }

        if (name.StartsWith(label, StringComparison.OrdinalIgnoreCase))
        {
            return PrefixScore;
        }

        if (ContainsWord(name, label))
        {
            return ContainsScore;
        }

        return 0;
    }

    /// <summary>
    /// label 是否作为完整词出现在 name 中：前后须是字符串边界或非字母数字字符。
    /// 否则 "Save" 会命中 "Unsaved changes" 这类错误目标。
    /// </summary>
    private static bool ContainsWord(string name, string label)
    {
        if (label.Length == 0 || label.Length > name.Length)
        {
            return false;
        }

        var start = 0;

        while (start <= name.Length - label.Length)
        {
            var index = name.IndexOf(label, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var beforeBoundary = index == 0 || !char.IsLetterOrDigit(name[index - 1]);
            var end = index + label.Length;
            var afterBoundary = end >= name.Length || !char.IsLetterOrDigit(name[end]);

            if (beforeBoundary && afterBoundary)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    /// <summary>共有的不同字符数 / (编辑距离 + 1)，越大越像。</summary>
    private static double Similarity(string label, string candidate)
    {
        if (label.Length == 0)
        {
            return 0d;
        }

        var lowerLabel = label.ToLowerInvariant();
        var lowerCandidate = candidate.ToLowerInvariant();
        var shared = lowerLabel.Distinct().Count(c => lowerCandidate.Contains(c));
        var distance = Distance(lowerLabel, lowerCandidate);

        return (double)shared / (distance + 1);
    }

    private static int Distance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
