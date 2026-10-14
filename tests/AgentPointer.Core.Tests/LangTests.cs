using System.Globalization;
using AgentPointer.Core;
using AgentPointer.Core.Cli;

namespace AgentPointer.Core.Tests;

/// <summary>
/// 语言选择契约：--lang 优先，其次 AGENTPOINTER_LANG，最后按 UI 语言。
/// <see cref="Lang"/> 是静态状态，所以本类与 ArgParserTests / HelpTextTests 同属
/// "Lang" 集合，xunit 会让它们串行执行，避免相互踩语言。
/// </summary>
[Collection("Lang")]
public class LangTests : IDisposable
{
    private readonly string? _originalLanguage;

    public LangTests()
    {
        _originalLanguage = Environment.GetEnvironmentVariable("AGENTPOINTER_LANG");
        Environment.SetEnvironmentVariable("AGENTPOINTER_LANG", null);
        Lang.Configure(null, _ => null, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AGENTPOINTER_LANG", _originalLanguage);
        Lang.Configure(null, _ => null, CultureInfo.InvariantCulture);
    }

    /// <summary>注入式环境变量读取器，只有 AGENTPOINTER_LANG 有值。</summary>
    private static Func<string, string?> Env(string? value) =>
        name => name == "AGENTPOINTER_LANG" ? value : null;

    [Fact]
    public void 缺省语言来自ui文化()
    {
        Lang.Configure(null, _ => null, new CultureInfo("zh-CN"));
        Assert.True(Lang.IsZh);

        Lang.Configure(null, _ => null, new CultureInfo("zh-TW"));
        Assert.True(Lang.IsZh);

        Lang.Configure(null, _ => null, new CultureInfo("en-US"));
        Assert.False(Lang.IsZh);

        Lang.Configure(null, _ => null, new CultureInfo("ja-JP"));
        Assert.False(Lang.IsZh);
    }

    [Fact]
    public void lang开关优先于环境变量与ui文化()
    {
        Lang.Configure("en", Env("zh"), new CultureInfo("zh-CN"));
        Assert.False(Lang.IsZh);

        Lang.Configure("zh", Env("en"), new CultureInfo("en-US"));
        Assert.True(Lang.IsZh);
    }

    [Fact]
    public void 环境变量在ui文化之前生效()
    {
        Lang.Configure(null, Env("zh"), new CultureInfo("en-US"));
        Assert.True(Lang.IsZh);

        Lang.Configure(null, Env("en"), new CultureInfo("zh-CN"));
        Assert.False(Lang.IsZh);
    }

    [Fact]
    public void 非法取值一律回退到ui文化()
    {
        Lang.Configure("de", Env("de"), new CultureInfo("zh-TW"));
        Assert.True(Lang.IsZh);

        Lang.Configure("de", Env("de"), new CultureInfo("en-GB"));
        Assert.False(Lang.IsZh);

        Lang.Configure(null, _ => null, CultureInfo.InvariantCulture);
        Assert.False(Lang.IsZh);
    }

    [Fact]
    public void 区域写法也被接受()
    {
        Lang.Configure("ZH-cn", _ => null, new CultureInfo("en-US"));
        Assert.True(Lang.IsZh);

        Lang.Configure("en-US", _ => null, new CultureInfo("zh-CN"));
        Assert.False(Lang.IsZh);
    }

    [Fact]
    public void T按已选语言返回()
    {
        Lang.Configure("zh", _ => null, CultureInfo.InvariantCulture);
        Assert.Equal("中文", Lang.T("中文", "English"));

        Lang.Configure("en", _ => null, CultureInfo.InvariantCulture);
        Assert.Equal("English", Lang.T("中文", "English"));
    }

    [Fact]
    public void 列表与分句分隔符跟随语言()
    {
        Lang.Configure("zh", _ => null, CultureInfo.InvariantCulture);
        Assert.Equal("、", Lang.ListSep);
        Assert.Equal("；", Lang.ClauseSep);
        Assert.Equal("a、b", Lang.Join(new[] { "a", "b" }));

        Lang.Configure("en", _ => null, CultureInfo.InvariantCulture);
        Assert.Equal(", ", Lang.ListSep);
        Assert.Equal("; ", Lang.ClauseSep);
        Assert.Equal("a, b", Lang.Join(new[] { "a", "b" }));
    }

    [Fact]
    public void parse的错误信息跟随lang开关()
    {
        var english = ArgParser.Parse(
            new[] { "point", "--at", "1,1", "--lang", "en", "--frobnicate" }, _ => null);
        Assert.Equal(ExitCode.BadInput, english.ExitCode);
        Assert.Contains("--frobnicate", english.Error);
        Assert.DoesNotContain("未知参数", english.Error);

        var chinese = ArgParser.Parse(
            new[] { "point", "--at", "1,1", "--lang", "zh", "--frobnicate" }, _ => null);
        Assert.Equal(ExitCode.BadInput, chinese.ExitCode);
        Assert.Contains("未知参数", chinese.Error);
    }

    [Fact]
    public void parse的错误信息也读环境变量()
    {
        var english = ArgParser.Parse(new[] { "point", "--at", "1,1", "--frobnicate" }, Env("en"));
        Assert.Equal(ExitCode.BadInput, english.ExitCode);
        Assert.DoesNotContain("未知参数", english.Error);

        var chinese = ArgParser.Parse(new[] { "point" }, Env("zh"));
        Assert.Equal(ExitCode.BadInput, chinese.ExitCode);
        Assert.Contains("目标", chinese.Error);
    }

    [Fact]
    public void 帮助与错误信息用同一套语言规则()
    {
        Environment.SetEnvironmentVariable("AGENTPOINTER_LANG", "zh");
        try
        {
            Assert.True(Lang.Resolve(null, Environment.GetEnvironmentVariable, new CultureInfo("en-US")));
            Assert.Same(ArgParser.HelpTextZh, ArgParser.HelpFor(null, new CultureInfo("en-US")));
            Assert.Same(ArgParser.HelpTextEn, ArgParser.HelpFor("en", new CultureInfo("zh-CN")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTPOINTER_LANG", null);
        }
    }
}
