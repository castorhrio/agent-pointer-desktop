using System.Globalization;
using AgentPointer.Core.Cli;

namespace AgentPointer.Core.Tests;

/// <summary>
/// 帮助文本契约：中英两种语言必须覆盖同一批参数。
/// 少一个选项就是一种语言下的静默功能缺失。
/// </summary>
public class HelpTextTests
{
    /// <summary>两种帮助都必须提到的参数（也就是两套文本共同的对外契约）。</summary>
    public static readonly string[] Flags =
    {
        "--at", "--rect", "--mouse", "--window", "--app", "--element", "--role", "--anchor",
        "--display", "--logical",
        "--text", "--from", "--color", "--size", "--style", "--shape", "--corners", "--border",
        "--border-color", "--text-color", "--edge-color", "--close-color", "--close-x-color",
        "--duration", "--raise", "--no-raise", "--no-animation", "--follow",
        "--close-button", "--until-click", "--until-any-click", "--say", "--voice",
        "--flash", "--notify", "--png",
        "--json", "--dry-run", "--lang", "--help", "--version",
        "--claude", "--codex", "--force", "--add-path",
        "--all", "--hook", "--match", "--peekaboo", "--snapshot",
    };

    [Fact]
    public void 中英帮助覆盖同一批参数()
    {
        foreach (var flag in Flags)
        {
            Assert.Contains(flag, ArgParser.HelpTextZh);
            Assert.Contains(flag, ArgParser.HelpTextEn);
        }
    }

    [Fact]
    public void 两种语言都列出全部退出码()
    {
        foreach (var code in new[] { "0", "2", "3", "4", "5", "6" })
        {
            Assert.Contains(code, ArgParser.HelpTextZh);
            Assert.Contains(code, ArgParser.HelpTextEn);
        }
    }

    [Fact]
    public void 显式lang优先于ui语言()
    {
        Assert.Same(ArgParser.HelpTextEn, ArgParser.HelpFor("en", new CultureInfo("zh-CN")));
        Assert.Same(ArgParser.HelpTextZh, ArgParser.HelpFor("zh", new CultureInfo("en-US")));
        Assert.Same(ArgParser.HelpTextZh, ArgParser.HelpFor("ZH-cn", CultureInfo.InvariantCulture));
        Assert.Same(ArgParser.HelpTextEn, ArgParser.HelpFor("en-US", new CultureInfo("zh-CN")));
    }

    [Fact]
    public void 非法或缺失的lang回退到ui语言()
    {
        Assert.Same(ArgParser.HelpTextEn, ArgParser.HelpFor("de", new CultureInfo("en-US")));
        Assert.Same(ArgParser.HelpTextZh, ArgParser.HelpFor("de", new CultureInfo("zh-CN")));
        Assert.Same(ArgParser.HelpTextEn, ArgParser.HelpFor(null, CultureInfo.InvariantCulture));
        Assert.Same(ArgParser.HelpTextEn, ArgParser.HelpFor(null, new CultureInfo("de-DE")));
    }

    [Fact]
    public void zh的ui语言选中文帮助_其他语言选英文()
    {
        Assert.Same(ArgParser.HelpTextZh, ArgParser.HelpFor(null, new CultureInfo("zh")));
        Assert.Same(ArgParser.HelpTextZh, ArgParser.HelpFor(null, new CultureInfo("zh-TW")));
        Assert.Same(ArgParser.HelpTextEn, ArgParser.HelpFor(null, new CultureInfo("ja-JP")));
    }

    [Fact]
    public void 从参数里取lang()
    {
        Assert.Equal("en", ArgParser.LanguageFromArgs(new[] { "--help", "--lang", "en" }));
        Assert.Equal("zh", ArgParser.LanguageFromArgs(new[] { "point", "--at", "1,1", "--lang=zh-CN" }));
        Assert.Null(ArgParser.LanguageFromArgs(new[] { "point", "--at", "1,1" }));
        Assert.Null(ArgParser.LanguageFromArgs(new[] { "--help", "--lang", "de" }));
        Assert.Null(ArgParser.LanguageFromArgs(null));
    }
}
