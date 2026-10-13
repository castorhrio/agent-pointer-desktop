using AgentPointer.Core;
using AgentPointer.Core.Cli;
using AgentPointer.Core.Protocol;

namespace AgentPointer.Core.Tests;

/// <summary>
/// 参数契约测试。目标是锁死"与上游 bigarrow 的 CLI 兼容性"——
/// 这些用例一旦失败，就意味着已有 agent 的脚本会崩。
/// </summary>
public class ArgParserTests
{
    /// <summary>隔离环境变量，避免测试机上恰好设置了 CLAUDE_PID 影响断言。</summary>
    private static ParseResult Parse(params string[] args) =>
        ArgParser.Parse(args, _ => null);

    private static ParseResult Ok(params string[] args)
    {
        var r = Parse(args);
        Assert.True(r.Ok, $"期望解析成功，实际失败：{r.Error}");
        Assert.NotNull(r.Command);
        return r;
    }

    private static string ErrorOf(params string[] args) => Parse(args).Error ?? string.Empty;

    // ---------------- 子命令 ----------------

    [Theory]
    [InlineData("point", CommandKind.Point)]
    [InlineData("start", CommandKind.Start)]
    [InlineData("stop", CommandKind.Stop)]
    [InlineData("elements", CommandKind.Elements)]
    [InlineData("doctor", CommandKind.Doctor)]
    [InlineData("install-skill", CommandKind.InstallSkill)]
    public void 六个子命令都能识别(string token, CommandKind expected)
    {
        // point / start 必须带目标，其余子命令不需要
        var args = token is "point" or "start"
            ? new[] { token, "--at", "1,1" }
            : new[] { token };

        Assert.Equal(expected, Ok(args).Command!.Kind);
    }

    [Fact]
    public void 无参数时退出码为2()
    {
        var r = Parse();
        Assert.False(r.Ok);
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("子命令", r.Error);
    }

    [Fact]
    public void 未知子命令退出码为2()
    {
        var r = Parse("frobnicate", "--at", "1,1");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("frobnicate", r.Error);
    }

    [Fact]
    public void help与version不需要子命令()
    {
        Assert.True(Parse("--help").HelpRequested);
        Assert.True(Parse("-h").HelpRequested);
        Assert.True(Parse("--version").VersionRequested);
        Assert.True(Parse("point", "--help").HelpRequested);
    }

    // ---------------- --at ----------------

    [Fact]
    public void at解析正坐标()
    {
        var c = Ok("point", "--at", "760,500").Command!;
        Assert.Equal(TargetKind.At, c.Target.Kind);
        Assert.Equal(760, c.Target.At!.Value.X);
        Assert.Equal(500, c.Target.At!.Value.Y);
    }

    [Fact]
    public void at解析负坐标_主屏左上方的显示器()
    {
        var c = Ok("point", "--at", "-1920,-300").Command!;
        Assert.Equal(-1920, c.Target.At!.Value.X);
        Assert.Equal(-300, c.Target.At!.Value.Y);
    }

    [Fact]
    public void at支持等号写法()
    {
        var c = Ok("point", "--at=-1,2").Command!;
        Assert.Equal(-1, c.Target.At!.Value.X);
        Assert.Equal(2, c.Target.At!.Value.Y);
    }

    [Theory]
    [InlineData("760")]
    [InlineData("760,500,1")]
    [InlineData("abc,500")]
    [InlineData("760,def")]
    [InlineData("")]
    public void at格式错误退出码为2(string raw)
    {
        var r = Parse("point", "--at", raw);
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("--at", r.Error);
    }

    [Fact]
    public void at缺少取值退出码为2()
    {
        var r = Parse("point", "--at");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("--at", r.Error);
    }

    // ---------------- --rect ----------------

    [Fact]
    public void rect解析四个分量()
    {
        var c = Ok("point", "--rect", "10,20,300,400").Command!;
        Assert.Equal(TargetKind.Rect, c.Target.Kind);
        Assert.Equal(new System.Drawing.Rectangle(10, 20, 300, 400), c.Target.Rect);
    }

    [Theory]
    [InlineData("10,20,300")]
    [InlineData("10,20,300,400,5")]
    [InlineData("10,20,x,400")]
    public void rect格式错误退出码为2(string raw)
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--rect", raw).ExitCode);
    }

    // ---------------- --window / --app ----------------

    [Fact]
    public void window解析进程名()
    {
        var c = Ok("start", "--window", "WindowsTerminal").Command!;
        Assert.Equal(TargetKind.Window, c.Target.Kind);
        Assert.Equal("WindowsTerminal", c.Target.WindowQuery);
    }

    [Fact]
    public void window支持App冒号标题语法()
    {
        var c = Ok("start", "--window", "Safari:Inbox").Command!;
        Assert.Equal("Safari:Inbox", c.Target.WindowQuery);
    }

    [Fact]
    public void app是window的语法糖()
    {
        var c = Ok("point", "--app", "Code").Command!;
        Assert.Equal(TargetKind.Window, c.Target.Kind);
        Assert.Equal("Code", c.Target.WindowQuery);
    }

    [Fact]
    public void app与window同时给出时window优先且不报冲突()
    {
        var c = Ok("point", "--app", "Code", "--window", "Notepad").Command!;
        Assert.Equal("Notepad", c.Target.WindowQuery);
    }

    // ---------------- --element ----------------

    [Fact]
    public void element配合app构成作用域而非两个目标()
    {
        var c = Ok("point", "--element", "Allow", "--app", "SystemSettings").Command!;
        Assert.Equal(TargetKind.Element, c.Target.Kind);
        Assert.Equal("Allow", c.Target.ElementQuery);
        Assert.Equal("SystemSettings", c.Target.WindowQuery);
    }

    [Fact]
    public void element带role限定()
    {
        var c = Ok("point", "--element", "Add", "--role", "button").Command!;
        Assert.Equal("button", c.Target.Role);
    }

    [Fact]
    public void element为空退出码为2()
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--element", "  ").ExitCode);
    }

    // ---------------- 目标互斥 ----------------

    [Fact]
    public void 多个互斥目标同时给出退出码为2()
    {
        var r = Parse("point", "--at", "1,1", "--rect", "0,0,10,10");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("互斥", r.Error);
    }

    [Fact]
    public void at与window互斥()
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--at", "1,1", "--window", "Notepad").ExitCode);
    }

    [Fact]
    public void mouse与rect互斥()
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--mouse", "--rect", "0,0,10,10").ExitCode);
    }

    [Fact]
    public void point没有目标退出码为2()
    {
        var r = Parse("point", "--text", "hello");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("目标", r.Error);
    }

    [Fact]
    public void start没有目标退出码为2()
    {
        Assert.Equal(ExitCode.BadInput, Parse("start").ExitCode);
    }

    [Fact]
    public void stop不需要目标()
    {
        Assert.True(Ok("stop").Ok);
    }

    // ---------------- --mouse / --display ----------------

    [Fact]
    public void mouse被识别为目标()
    {
        var c = Ok("point", "--mouse").Command!;
        Assert.Equal(TargetKind.Mouse, c.Target.Kind);
    }

    [Fact]
    public void display解析序号()
    {
        var c = Ok("point", "--at", "10,10", "--display", "1").Command!;
        Assert.Equal(1, c.Target.DisplayIndex);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void display非法退出码为2(string raw)
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--at", "1,1", "--display", raw).ExitCode);
    }

    // ---------------- --duration ----------------

    [Fact]
    public void point默认时长为8秒()
    {
        Assert.Equal(8d, Ok("point", "--at", "1,1").Command!.Duration);
    }

    [Fact]
    public void start默认时长为300秒()
    {
        Assert.Equal(300d, Ok("start", "--at", "1,1").Command!.Duration);
    }

    [Fact]
    public void duration零表示不限时()
    {
        Assert.Equal(0d, Ok("point", "--at", "1,1", "--duration", "0").Command!.Duration);
    }

    [Fact]
    public void duration支持小数()
    {
        Assert.Equal(2.5d, Ok("point", "--at", "1,1", "--duration", "2.5").Command!.Duration);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void duration非法退出码为2(string raw)
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--at", "1,1", "--duration", raw).ExitCode);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    public void duration拒绝非有限数值(string raw)
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--at", "1,1", "--duration", raw).ExitCode);
    }

    [Fact]
    public void 窗口默认不抢焦点且只有显式raise才置前()
    {
        Assert.False(Ok("point", "--window", "Notepad").Command!.Behavior.Raise);
        Assert.True(Ok("point", "--window", "Notepad", "--raise").Command!.Behavior.Raise);
        Assert.False(Ok("point", "--window", "Notepad", "--raise", "--no-raise").Command!.Behavior.Raise);
    }

    [Fact]
    public void element不能绕过其他目标的互斥校验()
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--element", "Allow", "--at", "1,1").ExitCode);
        Assert.Equal(ExitCode.BadInput, Parse("point", "--at", "1,1", "--role", "button").ExitCode);
    }

    // ---------------- 外观 ----------------

    [Theory]
    [InlineData("red")]
    [InlineData("#00FF00")]
    [InlineData("#0000ff")]
    [InlineData("#abc")]
    [InlineData("#F31")]
    public void color接受颜色名与十六进制_含上游短格式RGB(string raw)
    {
        Assert.True(Ok("point", "--at", "1,1", "--color", raw).Ok);
    }

    [Theory]
    [InlineData("chartreuse")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    public void color非法值退出码为2(string raw)
    {
        var r = Parse("point", "--at", "1,1", "--color", raw);
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("--color", r.Error);
    }

    [Fact]
    public void 颜色名归一化为小写()
    {
        Assert.Equal("#00FF00", Ok("point", "--at", "1,1", "--color", "#00ff00").Command!.Look.Color);
        Assert.Equal("red", Ok("point", "--at", "1,1", "--color", "RED").Command!.Look.Color);
    }

    [Theory]
    [InlineData("--size", "S")]
    [InlineData("--size", "M")]
    [InlineData("--style", "arrow")]
    [InlineData("--style", "ring")]
    [InlineData("--shape", "zigzag")]
    [InlineData("--corners", "sharp")]
    [InlineData("--border", "white-black")]
    public void 枚举参数接受合法值(string flag, string value)
    {
        Assert.True(Ok("point", "--at", "1,1", flag, value).Ok);
    }

    /// <summary>
    /// 回归用例：曾经把 --size L 归一化成小写 "l"，会让下游按大小写分发的渲染逻辑失效。
    /// </summary>
    [Fact]
    public void size归一化为大写SML_其余枚举归一化为小写()
    {
        Assert.Equal("L", Ok("point", "--at", "1,1", "--size", "l").Command!.Look.Size);
        Assert.Equal("L", Ok("point", "--at", "1,1", "--size", "L").Command!.Look.Size);
        Assert.Equal("arrow", Ok("point", "--at", "1,1", "--style", "ARROW").Command!.Look.Style);
        Assert.Equal("zigzag", Ok("point", "--at", "1,1", "--shape", "ZigZag").Command!.Look.Shape);
        Assert.Equal("white-black", Ok("point", "--at", "1,1", "--border", "WHITE-BLACK").Command!.Look.Border);
    }

    [Theory]
    [InlineData("--size", "XL")]
    [InlineData("--style", "triangle")]
    [InlineData("--shape", "lightning")]
    [InlineData("--corners", "bevel")]
    [InlineData("--border", "neon")]
    public void 枚举参数非法值退出码为2(string flag, string value)
    {
        var r = Parse("point", "--at", "1,1", flag, value);
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains(flag, r.Error);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top-left")]
    [InlineData("bottom-right")]
    public void from接受合法方位(string side)
    {
        Assert.True(Ok("point", "--at", "1,1", "--from", side).Ok);
    }

    [Fact]
    public void from非法值退出码为2()
    {
        Assert.Equal(ExitCode.BadInput, Parse("point", "--at", "1,1", "--from", "nowhere").ExitCode);
    }

    [Fact]
    public void 全部外观参数可同时给出()
    {
        var r = Ok(
            "point", "--at", "1,1",
            "--text", "点这里",
            "--from", "left",
            "--color", "blue",
            "--size", "L",
            "--style", "box",
            "--shape", "spiral",
            "--corners", "round",
            "--border", "black",
            "--border-color", "white",
            "--text-color", "black",
            "--edge-color", "gray",
            "--close-color", "red",
            "--close-x-color", "white");

        var look = r.Command!.Look;
        Assert.Equal("left", look.From);
        Assert.Equal("blue", look.Color);
        Assert.Equal("L", look.Size);
        Assert.Equal("box", look.Style);
        Assert.Equal("spiral", look.Shape);
        Assert.Equal("round", look.Corners);
        Assert.Equal("black", look.Border);
        Assert.Equal("white", look.BorderColor);
        Assert.Equal("gray", look.EdgeColor);
    }

    // ---------------- 兼容性：未实现的参数必须被接受 ----------------

    [Theory]
    [InlineData("--follow")]
    [InlineData("--say")]
    [InlineData("--png")]
    public void Windows未实现的行为参数被接受而不报错(string flag)
    {
        var r = Ok("point", "--at", "1,1", flag);
        Assert.Contains(flag, r.Command!.Behavior.IneffectiveFlags);
    }

    [Fact]
    public void 未实现参数在json输出里可被识别()
    {
        var r = Ok("point", "--at", "1,1", "--follow", "--say");
        Assert.Equal(2, r.Command!.Behavior.IneffectiveFlags.Count);
    }

    [Fact]
    public void 已实现的参数不会被标记为无效()
    {
        var r = Ok("point", "--at", "1,1", "--no-raise", "--no-animation");
        Assert.Empty(r.Command!.Behavior.IneffectiveFlags);
    }

    [Fact]
    public void 未实现的语音与边色明确标记()
    {
        var r = Ok("point", "--at", "1,1", "--voice", "zira", "--edge-color", "red");
        Assert.Contains("--voice", r.Command!.Behavior.IneffectiveFlags);
        Assert.Contains("--edge-color", r.Command.Behavior.IneffectiveFlags);
    }

    // ---------------- 点击收场：牌子本身、关闭按钮、点目标 ----------------

    [Fact]
    public void close_button与until_click已经生效且不再标记为未生效()
    {
        var r = Ok("point", "--at", "1,1", "--close-button", "--until-click");
        Assert.True(r.Command!.Behavior.CloseButton);
        Assert.True(r.Command.Behavior.UntilClick);
        Assert.Empty(r.Command.Behavior.IneffectiveFlags);
    }

    [Fact]
    public void until_any_click被解析()
    {
        var r = Ok("point", "--at", "1,1", "--until-any-click");
        Assert.True(r.Command!.Behavior.UntilAnyClick);
        Assert.Empty(r.Command.Behavior.IneffectiveFlags);
    }

    [Fact]
    public void hook只允许用于stop()
    {
        Assert.True(Ok("stop", "--hook").Command!.StopHook);

        var r = Parse("point", "--at", "1,1", "--hook");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("--hook", r.Error);
        Assert.Equal(ExitCode.BadInput, Parse("doctor", "--hook").ExitCode);
    }

    [Fact]
    public void peekaboo被拒绝并说明是macOS专属()
    {
        var r = Parse("point", "--at", "1,1", "--peekaboo", "xyz");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("macOS", r.Error);
    }

    [Fact]
    public void snapshot被拒绝并说明是macOS专属()
    {
        var r = Parse("point", "--at", "1,1", "--snapshot", "see.json");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("macOS", r.Error);
    }

    // ---------------- stop 子命令 ----------------

    [Fact]
    public void stop支持all()
    {
        Assert.True(Ok("stop", "--all").Command!.StopAll);
    }

    [Fact]
    public void stop支持hook()
    {
        Assert.True(Ok("stop", "--hook").Command!.StopHook);
    }

    // ---------------- 未知参数 ----------------

    [Fact]
    public void 未知参数退出码为2并点名参数()
    {
        var r = Parse("point", "--at", "1,1", "--frobnicate");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("--frobnicate", r.Error);
        Assert.Contains("未知参数", r.Error);
    }

    [Fact]
    public void 位置参数被拒绝()
    {
        var r = Parse("point", "请点这里");
        Assert.Equal(ExitCode.BadInput, r.ExitCode);
        Assert.Contains("位置参数", r.Error);
    }

    [Fact]
    public void 参数错误时绝不抛异常()
    {
        var inputs = new[]
        {
            Array.Empty<string>(),
            new[] { "point" },
            new[] { "point", "--at" },
            new[] { "point", "--at", "," },
            new[] { "point", "--color", "#" },
            new[] { "point", "--duration", "NaN" },
            new[] { "---" },
            new[] { "point", "=" },
        };

        foreach (var input in inputs)
        {
            var r = ArgParser.Parse(input, _ => null);
            Assert.NotNull(r);
            Assert.False(r.Ok);
            Assert.Equal(ExitCode.BadInput, r.ExitCode);
        }
    }

    // ---------------- 通用开关 ----------------

    [Fact]
    public void json开关被识别()
    {
        Assert.True(Ok("doctor", "--json").Command!.Json);
        Assert.True(Ok("point", "--at", "1,1", "--dry-run").Command!.DryRun);
    }

    [Fact]
    public void text被解析()
    {
        Assert.Equal("点一下 Allow", Ok("point", "--at", "1,1", "--text", "点一下 Allow").Command!.Text);
    }

    [Fact]
    public void elements支持match过滤()
    {
        Assert.Equal("allow", Ok("elements", "--app", "Notepad", "--match", "allow").Command!.ElementsMatch);
    }

    // ---------------- owner pid ----------------

    [Fact]
    public void 从环境变量读取ownerPid()
    {
        var r = ArgParser.Parse(new[] { "point", "--at", "1,1" }, name => name == "CLAUDE_PID" ? "4242" : null);
        Assert.Equal(4242, r.Command!.OwnerPid);
    }

    [Fact]
    public void bigarrowOwnerPid作为回退()
    {
        var r = ArgParser.Parse(
            new[] { "point", "--at", "1,1" },
            name => name == "BIGARROW_OWNER_PID" ? "77" : null);
        Assert.Equal(77, r.Command!.OwnerPid);
    }

    [Fact]
    public void claudePid优先于bigarrowOwnerPid()
    {
        var r = ArgParser.Parse(
            new[] { "point", "--at", "1,1" },
            name => name == "CLAUDE_PID" ? "11" : name == "BIGARROW_OWNER_PID" ? "22" : null);
        Assert.Equal(11, r.Command!.OwnerPid);
    }

    [Fact]
    public void 环境变量缺失时ownerPid为null()
    {
        Assert.Null(Ok("point", "--at", "1,1").Command!.OwnerPid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void 非法的pid环境变量被忽略而不是报错(string raw)
    {
        var r = ArgParser.Parse(new[] { "point", "--at", "1,1" }, name => name == "CLAUDE_PID" ? raw : null);
        Assert.True(r.Ok);
        Assert.Null(r.Command!.OwnerPid);
    }
}
