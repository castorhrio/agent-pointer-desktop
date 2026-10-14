using System.Linq;

namespace AgentPointer.Core.Cli;

/// <summary>
/// 分号分隔列表（典型场景是用户 PATH）的纯字符串处理。
/// 放在 Core 里是为了能脱离 Windows 注册表直接测。
/// </summary>
public static class PathList
{
    /// <summary>
    /// 把 <paramref name="directory"/> 追加到 <paramref name="current"/> 末尾（若尚未存在）。
    ///
    /// <para>判重忽略大小写、首尾空白和结尾反斜杠；<paramref name="current"/> 的原有内容
    /// 一律原样保留（包括空项与 <c>%VAR%</c> 引用），只为新目录补一个必要的分号。</para>
    /// </summary>
    public static (string newValue, bool changed) Add(string? current, string? directory)
    {
        var existing = current ?? string.Empty;
        var wanted = directory?.Trim();
        if (string.IsNullOrEmpty(wanted)) return (existing, false);

        if (Contains(existing, wanted)) return (existing, false);

        // 末尾没有分号就先补一个，否则新目录会粘到上一项后面
        var separator = existing.Length == 0 || existing.EndsWith(';') ? string.Empty : ";";
        return (existing + separator + wanted, true);
    }

    /// <summary>列表里是否已经有这个目录（同上，忽略大小写、空白与结尾反斜杠）。</summary>
    public static bool Contains(string? current, string? directory)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(directory)) return false;

        var wanted = Key(directory);
        return Split(current).Any(entry => string.Equals(Key(entry), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>归一化：去首尾空白、去结尾目录分隔符。</summary>
    private static string Key(string value) => value.Trim().TrimEnd('\\', '/');
}
