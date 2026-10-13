namespace AgentPointer.Core.Targeting;

/// <summary>
/// 友好的应用名 → Windows 进程名别名表。
///
/// <para>上游 macOS 用 App bundle 名（<c>"Google Chrome"</c>、<c>Finder</c>、<c>"Visual Studio Code"</c>），
/// Windows 上对应的却是进程名（<c>chrome</c>、<c>explorer</c>、<c>Code</c>）。
/// 现有 agent 脚本照搬上游写法会直接找不到窗口，所以这里做一次纯字符串映射。</para>
///
/// <para><b>纯函数</b>：不碰窗口、不碰进程，可单测。匹配本身（含 FileDescription 与标题兜底）
/// 由 <c>WindowFinder</c> 负责。</para>
/// </summary>
public static class AppAliases
{
    /// <summary>
    /// <c>ApplicationFrameHost</c> 是设置类窗口的宿主进程，一个进程承载多个窗口，
    /// 只能靠标题区分。别名把它映射出来，标题过滤交给 <c>WindowFinder</c>。
    /// </summary>
    public static readonly IReadOnlyList<string> ApplicationFrameHostTitleHints = new[]
    {
        "Settings", "设置",
    };

    /// <summary>键为去掉空格、去掉 .exe、小写后的应用名。</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["googlechrome"] = new[] { "chrome" },
        ["chrome"] = new[] { "chrome" },
        ["microsoftedge"] = new[] { "msedge", "chrome" },
        ["edge"] = new[] { "msedge", "chrome" },
        ["safari"] = new[] { "msedge", "chrome" },
        ["firefox"] = new[] { "firefox" },

        ["visualstudiocode"] = new[] { "Code" },
        ["vscode"] = new[] { "Code" },
        ["code"] = new[] { "Code" },
        ["cursor"] = new[] { "Cursor" },

        ["terminal"] = new[] { "WindowsTerminal", "powershell", "pwsh", "cmd" },
        ["windowsterminal"] = new[] { "WindowsTerminal", "powershell", "pwsh", "cmd" },
        ["iterm"] = new[] { "WindowsTerminal", "powershell", "pwsh", "cmd" },
        ["iterm2"] = new[] { "WindowsTerminal", "powershell", "pwsh", "cmd" },

        ["finder"] = new[] { "explorer" },
        ["explorer"] = new[] { "explorer" },
        ["fileexplorer"] = new[] { "explorer" },

        ["systemsettings"] = new[] { "SystemSettings", "ApplicationFrameHost" },
        ["systempreferences"] = new[] { "SystemSettings", "ApplicationFrameHost" },
        ["settings"] = new[] { "SystemSettings", "ApplicationFrameHost" },

        ["notepad"] = new[] { "Notepad" },
        ["textedit"] = new[] { "notepad", "Notepad" },

        ["word"] = new[] { "WINWORD" },
        ["excel"] = new[] { "EXCEL" },
        ["powerpoint"] = new[] { "POWERPNT" },
        ["keynote"] = new[] { "POWERPNT" },
        ["outlook"] = new[] { "olk", "OUTLOOK" },
        ["mail"] = new[] { "olk", "OUTLOOK" },
        ["teams"] = new[] { "ms-teams", "Teams" },
        ["slack"] = new[] { "slack" },
        ["discord"] = new[] { "Discord" },
        ["claude"] = new[] { "claude" },
    };

    /// <summary>
    /// 按顺序返回要尝试的进程名。
    /// 第一个永远是调用方给的原串（只去掉 .exe）；随后是去掉空格的写法；最后才是别名。
    /// 空白输入返回空列表。
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? app)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(app)) return result;

        var trimmed = app.Trim();
        Add(result, StripExe(trimmed));
        Add(result, RemoveSpaces(StripExe(trimmed)));

        // 调用方直接给了进程名（Chrome.exe）时不做友好名映射：原串已经是它要找的名字，
        // 再补一个只差大小写的别名只会往候选里塞噪声。
        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && Aliases.TryGetValue(Key(trimmed), out var mapped))
        {
            foreach (var name in mapped) Add(result, name);
        }

        return result;
    }

    /// <summary><c>--app</c> 的键：去掉 .exe、去掉所有空格、小写。</summary>
    internal static string Key(string app) => RemoveSpaces(StripExe(app.Trim()));

    /// <summary>
    /// 只按<strong>原样</strong>去重：别名给的往往是真正的进程名，与上游写法只差大小写
    /// （<c>Firefox</c> → <c>firefox</c>、<c>Excel</c> → <c>EXCEL</c>）。
    /// 大小写无关去重会把它们当重复项吃掉，agent 就再也拿不到那个进程名了。
    /// </summary>
    private static void Add(List<string> result, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        if (result.Contains(candidate, StringComparer.Ordinal)) return;
        result.Add(candidate);
    }

    private static string StripExe(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private static string RemoveSpaces(string value) =>
        value.Contains(' ') ? value.Replace(" ", string.Empty) : value;
}
