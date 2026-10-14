using AgentPointer.Core.Targeting;

namespace AgentPointer.Core.Tests;

/// <summary>
/// 别名表契约：上游 macOS 写法必须能落到 Windows 进程名上，且原串永远排在第一个。
/// </summary>
public class AppAliasesTests
{
    private static string[] Candidates(string app) => AppAliases.Candidates(app).ToArray();

    [Fact]
    public void 原串永远排在第一个()
    {
        Assert.Equal("Google Chrome", Candidates("Google Chrome")[0]);
        Assert.Equal("frobnicate", Candidates("frobnicate")[0]);
        Assert.Equal("Chrome", Candidates("Chrome.exe")[0]);
    }

    [Fact]
    public void 去掉空格与exe后缀的写法也会被尝试()
    {
        Assert.Contains("Chrome", Candidates("Chrome.exe"));
        Assert.Contains("SystemSettings", Candidates("System Settings"));
    }

    [Fact]
    public void chrome的多种上游写法()
    {
        Assert.Equal(new[] { "Google Chrome", "GoogleChrome", "chrome" }, Candidates("Google Chrome"));
        Assert.Equal(new[] { "chrome" }, Candidates("chrome"));
        Assert.Equal(new[] { "Chrome" }, Candidates("Chrome.exe"));
    }

    [Fact]
    public void 大小写与空格不影响命中()
    {
        Assert.Contains("chrome", Candidates("  GOOGLE   CHROME  "));
        Assert.Contains("Code", Candidates("visualstudio code"));
    }

    [Theory]
    [InlineData("Visual Studio Code", "Code")]
    [InlineData("VS Code", "Code")]
    [InlineData("vscode", "Code")]
    [InlineData("Cursor", "Cursor")]
    [InlineData("Firefox", "firefox")]
    [InlineData("Slack", "slack")]
    [InlineData("Discord", "Discord")]
    [InlineData("Claude", "claude")]
    public void 常用应用映射到进程名(string app, string expected)
    {
        Assert.Contains(expected, Candidates(app));
    }

    [Fact]
    public void edge只映射到msedge()
    {
        Assert.Equal(new[] { "Edge", "msedge" }, Candidates("Edge"));
        Assert.Equal(new[] { "Microsoft Edge", "MicrosoftEdge", "msedge" }, Candidates("Microsoft Edge"));
    }

    [Fact]
    public void safari硬失配且不会退到chrome()
    {
        // Windows 上没有 Safari：只给一个必然落空的进程名，让调用方拿到退出码 3
        Assert.Equal(new[] { "Safari", "safari" }, Candidates("Safari"));
    }

    [Fact]
    public void 只有chromium系别名才给出chrome进程名()
    {
        Assert.Contains("chrome", Candidates("Google Chrome"));

        foreach (var app in new[] { "Safari", "Edge", "Microsoft Edge", "Firefox", "Notepad" })
        {
            Assert.DoesNotContain("chrome", Candidates(app));
        }
    }

    [Fact]
    public void 终端类别名给出所有候选且按顺序()
    {
        Assert.Equal(
            new[] { "WindowsTerminal", "powershell", "pwsh", "cmd" },
            Candidates("Terminal").Skip(1));
        // 第一个候选永远是原串，本来就不同；比较的是别名尾部
        Assert.Equal(Candidates("Terminal").Skip(1), Candidates("iTerm2").Skip(1));
    }

    [Fact]
    public void finder映射到资源管理器()
    {
        Assert.Contains("explorer", Candidates("Finder"));
        Assert.Contains("explorer", Candidates("File Explorer"));
    }

    [Fact]
    public void 系统设置映射到设置进程与宿主()
    {
        Assert.Contains("SystemSettings", Candidates("System Preferences"));
        Assert.Contains("ApplicationFrameHost", Candidates("Settings"));
    }

    [Theory]
    [InlineData("Word", "WINWORD")]
    [InlineData("Excel", "EXCEL")]
    [InlineData("PowerPoint", "POWERPNT")]
    [InlineData("Keynote", "POWERPNT")]
    [InlineData("Outlook", "olk")]
    [InlineData("Mail", "OUTLOOK")]
    [InlineData("Teams", "ms-teams")]
    public void office类应用映射到可执行名(string app, string expected)
    {
        Assert.Contains(expected, Candidates(app));
    }

    [Fact]
    public void 未知应用只返回原串相关候选()
    {
        Assert.Equal(new[] { "frobnicate" }, Candidates("frobnicate"));
        Assert.Empty(Candidates("frobnicate").Where(c => c != "frobnicate"));
    }

    [Fact]
    public void 没有重复候选且忽略大小写()
    {
        foreach (var app in new[] { "Google Chrome", "Visual Studio Code", "Terminal", "Settings", "Notepad" })
        {
            var list = Candidates(app);
            Assert.Equal(list.Length, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白输入返回空列表(string? app)
    {
        Assert.Empty(AppAliases.Candidates(app));
    }

    [Fact]
    public void 应用框架宿主带有标题提示()
    {
        Assert.Contains("Settings", AppAliases.ApplicationFrameHostTitleHints);
        Assert.Contains("设置", AppAliases.ApplicationFrameHostTitleHints);
    }
}
