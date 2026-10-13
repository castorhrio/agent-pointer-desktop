using System.Globalization;
using System.Runtime.InteropServices;

namespace AgentPointer.Tray;

/// <summary>一种界面语言：文化代码 + 菜单里显示的原生名。</summary>
internal sealed record AppLanguage(string Code, string NativeName);

/// <summary>
/// 语言注册表——多语言的唯一清单。
///
/// <para><b>加一种新语言只需要两步</b>：复制一份 <c>Strings.en-US.xaml</c> 改名
/// <c>Strings.ja-JP.xaml</c> 并翻译文案，然后在这里加一行。面板的语言按钮、托盘菜单、
/// 系统语言探测、持久化校验全部自动跟上——UI 上没有任何硬编码的语言项。</para>
///
/// <para>缺 key 的回退：ApplyLanguage 总是先合并 en-US（基础字典），再合并所选语言，
/// 所选语言里缺的文案自动落到英文。</para>
/// </summary>
internal static class Languages
{
    public const string System = "system";

    /// <summary>en-US 是回退基础语言，必须永远存在且字典齐全。</summary>
    public const string Fallback = "en-US";

    public static readonly AppLanguage[] All =
    {
        new("zh-CN", "中文（简体）"),
        new("en-US", "English"),
    };

    public static AppLanguage? Find(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析系统语言：精确匹配（zh-CN）→ 中性匹配（任何 zh-* → zh-CN，任何 en-* → en-US）
    /// → 注册表都没有就回退英文。
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    public static string ResolveSystem()
    {
        // 使用 Windows 显示语言，不受启动终端或进程 CurrentUICulture 覆盖影响。
        try { return Resolve(CultureInfo.GetCultureInfo(GetUserDefaultUILanguage())); }
        catch (CultureNotFoundException) { return Resolve(CultureInfo.CurrentUICulture); }
    }

    internal static string Resolve(CultureInfo ui)
    {
        if (!string.IsNullOrEmpty(ui.Name) && Find(ui.Name) is { } exact)
        {
            return exact.Code;
        }

        var twoLetter = ui.TwoLetterISOLanguageName;
        if (!string.IsNullOrEmpty(twoLetter))
        {
            var neutral = All.FirstOrDefault(l =>
                l.Code.Split('-')[0].Equals(twoLetter, StringComparison.OrdinalIgnoreCase));
            if (neutral is not null)
            {
                return neutral.Code;
            }
        }

        return Fallback;
    }
}
