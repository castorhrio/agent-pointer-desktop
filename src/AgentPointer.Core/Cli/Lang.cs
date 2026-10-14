using System.Globalization;

namespace AgentPointer.Core.Cli;

/// <summary>
/// CLI 面向用户文本的语言选择：显式 <c>--lang en|zh</c> 优先，其次环境变量
/// <c>AGENTPOINTER_LANG</c>，最后按 UI 语言（zh-* 中文，其余英文）。
///
/// <para>这是进程级静态状态：CLI 每个进程只处理一条命令，解析一开始就
/// <see cref="Configure"/> 定好，之后所有 <see cref="T"/> 都按它返回。
/// 帮助文本（<see cref="ArgParser.HelpFor"/>）走的是同一套规则，两者不会说法不一致。</para>
/// </summary>
public static class Lang
{
    private const string EnvironmentVariable = "AGENTPOINTER_LANG";

    /// <summary>未显式配置时的默认值：跟随 UI 文化。</summary>
    private static bool _isZh = IsChinese(CultureInfo.CurrentUICulture);

    /// <summary>当前语言是否为中文。默认取自 <see cref="CultureInfo.CurrentUICulture"/>。</summary>
    public static bool IsZh => _isZh;

    /// <summary>按 <paramref name="langFlag"/> → 环境变量 → UI 文化定下当前语言。</summary>
    public static void Configure(string? langFlag, Func<string, string?> env, CultureInfo ui) =>
        _isZh = Resolve(langFlag, env, ui);

    /// <summary>
    /// 纯函数版本的语言选择，帮助文本与错误信息共用。
    /// 取值非法一律当作没给：一个拼错的 <c>--lang</c> 不该改变任何行为。
    /// </summary>
    public static bool Resolve(string? langFlag, Func<string, string?>? env, CultureInfo? ui)
    {
        if (TryNormalize(langFlag, out var explicitLanguage)) return explicitLanguage == "zh";
        if (TryNormalize(env?.Invoke(EnvironmentVariable), out var fromEnvironment)) return fromEnvironment == "zh";
        return ui is { } culture && IsChinese(culture);
    }

    /// <summary>按当前语言二选一：<paramref name="zh"/> 给中文，<paramref name="en"/> 给英文。</summary>
    public static string T(string zh, string en) => _isZh ? zh : en;

    /// <summary>列表分隔符：中文顿号，英文逗号加空格。</summary>
    public static string ListSep => _isZh ? "、" : ", ";

    /// <summary>分句分隔符：中文全角分号，英文分号加空格。</summary>
    public static string ClauseSep => _isZh ? "；" : "; ";

    /// <summary>按当前语言连接一组条目（列表用 <see cref="ListSep"/>）。</summary>
    public static string Join(IEnumerable<string> items) => string.Join(ListSep, items);

    /// <summary>归一化语言取值：en / zh，大小写无关，也接受 en-US / zh_CN 这类区域写法。</summary>
    public static bool TryNormalize(string? raw, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim().ToLowerInvariant().Replace('_', '-');
        if (value == "en" || value.StartsWith("en-", StringComparison.Ordinal))
        {
            normalized = "en";
            return true;
        }

        if (value == "zh" || value.StartsWith("zh-", StringComparison.Ordinal))
        {
            normalized = "zh";
            return true;
        }

        return false;
    }

    private static bool IsChinese(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
}
