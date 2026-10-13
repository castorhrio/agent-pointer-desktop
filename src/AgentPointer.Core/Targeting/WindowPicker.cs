namespace AgentPointer.Core.Targeting;

/// <summary>
/// 一个窗口的候选信息。<see cref="Order"/> 是枚举顺序，用来在面积相同时稳定挑选。
/// </summary>
public sealed record WindowCandidate(
    int Order,
    string? ProcessName,
    string? FileDescription,
    string Title,
    long Area);

/// <summary>
/// <c>--window</c> / <c>--app</c> 的挑选规则。纯函数：不碰 Win32，只吃
/// <see cref="WindowCandidate"/>，所以可以单测。
/// </summary>
public static class WindowPicker
{
    /// <summary>
    /// 按查询串挑一个窗口，挑不出来返回 null（调用方映射为退出码 3）。
    ///
    /// <list type="number">
    /// <item>逐个候选（<see cref="AppAliases.Candidates"/>）找命中的窗口。</item>
    /// <item>没有 <c>App:title</c> 时，取该候选里面积最大的窗口。</item>
    /// <item>有 <c>App:title</c> 时，必须有窗口的标题包含它（忽略大小写）；对不上就继续试
    /// 后面的候选，但<strong>绝不</strong>退回同进程里随便一个窗口——那等于指着错误的
    /// 窗口说"就是它"。候选全都对不上标题时返回 null。</item>
    /// <item>一个候选都没命中任何进程时，才把查询串当成窗口标题找
    /// （上游 <c>App:title</c> 里的 title 有时是唯一的线索）。</item>
    /// </list>
    /// </summary>
    public static WindowCandidate? Pick(IReadOnlyList<WindowCandidate> windows, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var (appPart, titlePart) = SplitQuery(query);
        var processHit = false;

        foreach (var candidate in AppAliases.Candidates(appPart))
        {
            var hits = windows.Where(w => Matches(w, candidate)).ToList();
            if (hits.Count == 0) continue;

            processHit = true;

            if (titlePart is null) return Best(hits, null);

            var narrowed = hits
                .Where(w => w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (narrowed.Count > 0) return Best(narrowed, titlePart);
        }

        if (!processHit)
        {
            var byTitle = windows
                .Where(w => w.Title.Contains(appPart, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (byTitle.Count > 0) return Best(byTitle, appPart);
        }

        return null;
    }

    /// <summary>单个候选是否命中这个窗口：进程名 → FileDescription → ApplicationFrameHost 标题。</summary>
    public static bool Matches(WindowCandidate window, string candidate)
    {
        if (ProcessMatches(window.ProcessName, candidate)) return true;

        if (window.FileDescription is { } description
            && description.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // ApplicationFrameHost 一个进程承载一堆设置窗口，只能靠标题认
        return string.Equals(candidate, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
            && AppAliases.ApplicationFrameHostTitleHints.Any(hint =>
                window.Title.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>进程名匹配：忽略大小写、忽略 .exe 后缀。</summary>
    public static bool ProcessMatches(string? processName, string query)
    {
        if (string.IsNullOrEmpty(processName)) return false;

        return string.Equals(Normalize(processName), Normalize(query), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 <c>App:标题</c> 拆开。标题里可以含冒号，所以只拆第一个。</summary>
    public static (string App, string? Title) SplitQuery(string query)
    {
        var trimmed = query.Trim();
        var idx = trimmed.IndexOf(':');

        return idx < 0
            ? (trimmed, null)
            : (trimmed[..idx].Trim(), trimmed[(idx + 1)..].Trim() is { Length: > 0 } t ? t : null);
    }

    private static string Normalize(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    /// <summary>候选里挑一个：标题命中优先，然后面积最大，最后枚举顺序（结果稳定）。</summary>
    private static WindowCandidate Best(IReadOnlyList<WindowCandidate> candidates, string? titlePart) =>
        candidates
            .OrderByDescending(w => titlePart is not null &&
                                    w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.Area)
            .ThenBy(w => w.Order)
            .First();
}
