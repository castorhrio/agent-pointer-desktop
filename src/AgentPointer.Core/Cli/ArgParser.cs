using System.Drawing;
using System.Globalization;
using AgentPointer.Core.Protocol;

namespace AgentPointer.Core.Cli;

/// <summary>
/// 参数解析器。沿用上游参数名；平台差异和未支持功能在帮助中明确列出。
/// 凡是上游接受、但 Windows 版尚未实现的参数，一律<strong>接受并标记</strong>，
/// 绝不报错——否则现有 agent 脚本会直接崩掉。
/// </summary>
public static class ArgParser
{
    private const double DefaultPointDuration = 8;
    private const double DefaultStartDuration = 300;

    /// <summary>需要取值的参数。</summary>
    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "--at", "--rect", "--window", "--app", "--element", "--role", "--display",
        "--text", "--from", "--anchor", "--color", "--size", "--style", "--shape", "--corners",
        "--border", "--border-color", "--text-color", "--edge-color",
        "--close-color", "--close-x-color", "--duration", "--voice",
        "--peekaboo", "--snapshot", "--match",
    };

    /// <summary>开关型参数。</summary>
    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal)
    {
        "--mouse", "--no-raise", "--no-animation", "--follow", "--until-click", "--until-any-click",
        "--close-button", "--say", "--png", "--dry-run", "--json", "--help", "--version",
        "--all", "--hook", "--keep-alive", "--raise",
    };

    /// <summary>上游的 macOS 专属参数。在 Windows 上明确拒绝，不静默忽略。</summary>
    private static readonly HashSet<string> MacOnlyFlags = new(StringComparer.Ordinal)
    {
        "--peekaboo", "--snapshot",
    };

    /// <summary>上游支持、Windows 版 v1 未实现的行为参数。接受但标记为未生效。</summary>
    private static readonly HashSet<string> IneffectiveOnWindows = new(StringComparer.Ordinal)
    {
        "--follow", "--say", "--png",
    };

    private static readonly string[] ColorNames =
    {
        "red", "orange", "yellow", "green", "teal", "blue", "purple", "pink", "black", "white",
    };

    private static readonly Dictionary<string, string[]> Enums = new(StringComparer.Ordinal)
    {
        ["--size"] = new[] { "S", "M", "L" },
        ["--style"] = new[] { "arrow", "ring", "box" },
        ["--shape"] = new[] { "bend", "straight", "zigzag", "spiral" },
        ["--corners"] = new[] { "round", "sharp" },
        ["--border"] = new[] { "shadow", "white-black", "black" },
        ["--anchor"] = new[] { "center", "title", "top-left", "top-right", "bottom-left", "bottom-right" },
    };

    /// <summary>上游 --from 的 9 个方位 + auto（默认值）。</summary>
    private static readonly HashSet<string> FromSides = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "left", "right", "top", "bottom",
        "top-left", "top-right", "bottom-left", "bottom-right",
    };

    /// <summary>解析参数。env 为 null 时读取真实环境变量；测试可注入。</summary>
    public static ParseResult Parse(string[] args, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        args ??= Array.Empty<string>();

        if (args.Length == 0)
        {
            return ParseResult.BadInput(
                "缺少子命令。可用子命令：point / start / stop / elements / doctor / install-skill");
        }

        var index = 0;

        // --help / --version 可以不带子命令出现
        if (args[0] is "--help" or "-h" or "help") return ParseResult.Help();
        if (args[0] is "--version" or "-v") return ParseResult.Version();

        if (!TryParseKind(args[0], out var kind))
        {
            return ParseResult.BadInput($"未知子命令 '{args[0]}'。可用子命令：point / start / stop / elements / doctor / install-skill");
        }

        index++;

        // 先把参数扫进字典，保持"后出现覆盖先出现"的直觉行为
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        var ineffective = new List<string>();
        string? badFlag = null;
        string? badReason = null;

        while (index < args.Length && badFlag is null)
        {
            var token = args[index];
            index++;

            if (!token.StartsWith('-'))
            {
                badFlag = token;
                badReason = "位置参数不受支持，所有输入都必须以 --key 形式给出";
                break;
            }

            string name;
            string? value = null;

            var eq = token.IndexOf('=');
            if (eq > 0)
            {
                name = token[..eq];
                value = token[(eq + 1)..];
            }
            else
            {
                name = token;
            }

            if (name is "--help" or "-h") return ParseResult.Help();
            if (name is "--version" or "-v") return ParseResult.Version();

            if (MacOnlyFlags.Contains(name))
            {
                return ParseResult.BadInput($"'{name}' 是 macOS 专属功能，Windows 上没有对应物");
            }

            if (BoolFlags.Contains(name))
            {
                switches.Add(name);
                if (IneffectiveOnWindows.Contains(name)) ineffective.Add(name);
                continue;
            }

            if (ValueFlags.Contains(name))
            {
                if (value is null)
                {
                    if (index >= args.Length)
                    {
                        badFlag = name;
                        badReason = "缺少取值";
                        break;
                    }

                    value = args[index];
                    index++;
                }

                flags[name] = value;
                continue;
            }

            badFlag = name;
            badReason = ValueFlags.Contains(name) || BoolFlags.Contains(name)
                ? "不受支持的参数"
                : "未知参数";
        }

        if (badFlag is not null)
        {
            return ParseResult.BadInput($"参数 '{badFlag}' {badReason}");
        }

        return Build(kind, flags, switches, ineffective, env);
    }

    private static ParseResult Build(
        CommandKind kind,
        Dictionary<string, string> flags,
        HashSet<string> switches,
        List<string> ineffective,
        Func<string, string?> env)
    {
        // ---------- 外观 ----------
        string? from = null;
        if (flags.TryGetValue("--from", out var fromRaw))
        {
            if (!FromSides.Contains(fromRaw))
            {
                return ParseResult.BadInput(
                    $"参数 '--from' 取值 '{fromRaw}' 非法。可用：auto / left / right / top / bottom / " +
                    "top-left / top-right / bottom-left / bottom-right");
            }

            from = fromRaw.ToLowerInvariant();
        }

        string? color = null;
        if (flags.TryGetValue("--color", out var colorRaw) && !TryNormalizeColor(colorRaw, out color))
        {
            return ParseResult.BadInput(
                $"参数 '--color' 取值 '{colorRaw}' 非法。可用：{string.Join(" / ", ColorNames)} 或 #RRGGBB / #RGB");
        }

        var enumErrors = new List<string>();
        var anchor = ReadEnum(flags, "--anchor", enumErrors);
        var size = ReadEnum(flags, "--size", enumErrors);
        var style = ReadEnum(flags, "--style", enumErrors);
        var shape = ReadEnum(flags, "--shape", enumErrors);
        var corners = ReadEnum(flags, "--corners", enumErrors);
        var border = ReadEnum(flags, "--border", enumErrors);
        if (enumErrors.Count > 0)
        {
            return ParseResult.BadInput(string.Join("；", enumErrors));
        }

        // ---------- 目标 ----------
        var targetResult = ResolveTarget(flags, switches);
        if (targetResult.Error is not null)
        {
            return ParseResult.BadInput(targetResult.Error);
        }

        // elements 的 --role 是列表过滤器，不是"元素 + 作用域"语法，所以单独接住
        var target = kind == CommandKind.Elements && flags.TryGetValue("--role", out var elementsRole)
            ? targetResult.Target with { Role = elementsRole }
            : targetResult.Target;

        if (flags.ContainsKey("--role") && !flags.ContainsKey("--element")
            && kind is CommandKind.Point or CommandKind.Start)
        {
            return ParseResult.BadInput("--role 需要配合 --element 使用。");
        }

        // --hook 是 Claude Code 的 UserPromptSubmit 钩子入口，只在 stop 上成立
        if (switches.Contains("--hook") && kind != CommandKind.Stop)
        {
            return ParseResult.BadInput("--hook 只用于 stop（Claude Code 的 UserPromptSubmit 钩子）。");
        }

        // point / start 必须有目标，否则 agent 无从知道指哪
        if (kind is CommandKind.Point or CommandKind.Start && targetResult.Target.Kind == TargetKind.None)
        {
            return ParseResult.BadInput(
                $"{kind.ToString().ToLowerInvariant()} 需要一个目标：--at / --rect / --mouse / --window / --app / --element 至少给一个");
        }

        // ---------- 时长 ----------
        var duration = kind switch
        {
            CommandKind.Point => DefaultPointDuration,
            CommandKind.Start => DefaultStartDuration,
            _ => 0d,
        };

        if (flags.TryGetValue("--duration", out var durationRaw))
        {
            if (!double.TryParse(durationRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || !double.IsFinite(parsed) || parsed < 0)
            {
                return ParseResult.BadInput($"参数 '--duration' 取值 '{durationRaw}' 非法，必须是 >= 0 的秒数");
            }

            duration = parsed;
        }

        flags.TryGetValue("--text", out var text);
        flags.TryGetValue("--match", out var match);

        var command = new PointerCommand
        {
            Kind = kind,
            Text = text,
            Target = target,
            Duration = duration,
            Json = switches.Contains("--json"),
            DryRun = switches.Contains("--dry-run"),
            StopAll = switches.Contains("--all"),
            StopHook = switches.Contains("--hook"),
            KeepAlive = switches.Contains("--keep-alive"),
            OwnerPid = ResolveOwnerPid(env),
            ElementsMatch = match,
            Look = new LookSpec
            {
                From = from,
                Anchor = anchor,
                Color = color,
                Size = size,
                Style = style,
                Shape = shape,
                Corners = corners,
                Border = border,
                BorderColor = flags.GetValueOrDefault("--border-color"),
                TextColor = flags.GetValueOrDefault("--text-color"),
                EdgeColor = flags.GetValueOrDefault("--edge-color"),
                CloseColor = flags.GetValueOrDefault("--close-color"),
                CloseXColor = flags.GetValueOrDefault("--close-x-color"),
            },
            Behavior = new BehaviorSpec
            {
                NoRaise = !switches.Contains("--raise") || switches.Contains("--no-raise"),
                Raise = switches.Contains("--raise") && !switches.Contains("--no-raise"),
                NoAnimation = switches.Contains("--no-animation"),
                Follow = switches.Contains("--follow"),
                UntilClick = switches.Contains("--until-click"),
                UntilAnyClick = switches.Contains("--until-any-click"),
                CloseButton = switches.Contains("--close-button"),
                Say = switches.Contains("--say"),
                Voice = flags.GetValueOrDefault("--voice"),
                Png = switches.Contains("--png"),
                IneffectiveFlags = ineffective
                    .Concat(new[] { "--voice", "--edge-color" }.Where(flags.ContainsKey))
                    .Distinct().ToArray(),
            },
        };

        return ParseResult.Success(command);
    }

    private readonly record struct TargetResult(TargetSpec Target, string? Error);

    private static TargetResult ResolveTarget(
        Dictionary<string, string> flags,
        HashSet<string> switches)
    {
        var display = ReadDisplayIndex(flags, out var displayError);
        if (displayError is not null)
        {
            return new TargetResult(new TargetSpec(), displayError);
        }

        // --element 与 --app/--window 是"元素 + 作用域"的关系，不算两个目标
        var hasElement = flags.TryGetValue("--element", out var element);
        var windowQuery = flags.GetValueOrDefault("--window") ?? flags.GetValueOrDefault("--app");

        if (hasElement)
        {
            if (flags.ContainsKey("--at") || flags.ContainsKey("--rect") || switches.Contains("--mouse"))
            {
                return new TargetResult(new TargetSpec(), "--element 与 --at / --rect / --mouse 互斥。");
            }
            if (string.IsNullOrWhiteSpace(element))
            {
                return new TargetResult(new TargetSpec(), "参数 '--element' 不能为空");
            }

            return new TargetResult(
                new TargetSpec
                {
                    Kind = TargetKind.Element,
                    ElementQuery = element,
                    Role = flags.GetValueOrDefault("--role"),
                    WindowQuery = windowQuery,
                    DisplayIndex = display,
                },
                null);
        }

        // 其余目标互斥
        var provided = new List<string>();
        if (flags.ContainsKey("--at")) provided.Add("--at");
        if (flags.ContainsKey("--rect")) provided.Add("--rect");
        if (switches.Contains("--mouse")) provided.Add("--mouse");
        if (windowQuery is not null) provided.Add(flags.ContainsKey("--window") ? "--window" : "--app");

        if (provided.Count > 1)
        {
            return new TargetResult(
                new TargetSpec(),
                $"目标参数互斥，但同时给了 {string.Join(" 和 ", provided)}");
        }

        if (flags.TryGetValue("--at", out var atRaw))
        {
            if (!TryParsePoint(atRaw, out var at))
            {
                return new TargetResult(
                    new TargetSpec(),
                    $"参数 '--at' 取值 '{atRaw}' 非法，格式应为 X,Y（允许负坐标）");
            }

            return new TargetResult(
                new TargetSpec { Kind = TargetKind.At, At = at, DisplayIndex = display }, null);
        }

        if (flags.TryGetValue("--rect", out var rectRaw))
        {
            if (!TryParseRect(rectRaw, out var rect))
            {
                return new TargetResult(
                    new TargetSpec(),
                    $"参数 '--rect' 取值 '{rectRaw}' 非法，格式应为 X,Y,W,H");
            }

            return new TargetResult(
                new TargetSpec { Kind = TargetKind.Rect, Rect = rect, DisplayIndex = display }, null);
        }

        if (switches.Contains("--mouse"))
        {
            return new TargetResult(
                new TargetSpec { Kind = TargetKind.Mouse, DisplayIndex = display }, null);
        }

        if (windowQuery is not null)
        {
            if (string.IsNullOrWhiteSpace(windowQuery))
            {
                return new TargetResult(new TargetSpec(), "参数 '--window' / '--app' 不能为空");
            }

            return new TargetResult(
                new TargetSpec { Kind = TargetKind.Window, WindowQuery = windowQuery, DisplayIndex = display },
                null);
        }

        return new TargetResult(new TargetSpec { DisplayIndex = display }, null);
    }

    private static int? ReadDisplayIndex(Dictionary<string, string> flags, out string? error)
    {
        error = null;
        if (!flags.TryGetValue("--display", out var raw)) return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            error = $"参数 '--display' 取值 '{raw}' 非法，必须是 >= 1 的显示器序号（1-based，见 doctor）";
            return null;
        }

        return value;
    }

    private static bool TryNormalizeColor(string raw, out string? normalized)
    {
        normalized = null;
        var trimmed = raw.Trim();

        if (trimmed.StartsWith('#'))
        {
            var hex = trimmed[1..];
            if (hex.Length == 6 && hex.All(Uri.IsHexDigit))
            {
                normalized = "#" + hex.ToUpperInvariant();
                return true;
            }

            // 上游接受短格式 #F31
            if (hex.Length == 3 && hex.All(Uri.IsHexDigit))
            {
                normalized = "#" + string.Concat(hex.Select(c => $"{c}{c}")).ToUpperInvariant();
                return true;
            }

            return false;
        }

        var lowered = trimmed.ToLowerInvariant();
        if (ColorNames.Contains(lowered))
        {
            normalized = lowered;
            return true;
        }

        return false;
    }

    private static string? ReadEnum(Dictionary<string, string> flags, string name, List<string> errors)
    {
        if (!flags.TryGetValue(name, out var raw)) return null;
        if (!Enums.TryGetValue(name, out var allowed))
        {
            errors.Add($"参数 '{name}' 无法识别");
            return null;
        }

        // 返回规范写法而不是原样小写：--size 必须是 S/M/L，不能被压成 s/m/l
        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, raw, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        errors.Add($"参数 '{name}' 取值 '{raw}' 非法。可用：{string.Join(" / ", allowed)}");
        return null;
    }

    internal static bool TryParsePoint(string raw, out Point point)
    {
        point = Point.Empty;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return false;
        point = new Point(x, y);
        return true;
    }

    internal static bool TryParseRect(string raw, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        rect = new Rectangle(x, y, w, h);
        return true;
    }

    private static bool TryParseKind(string token, out CommandKind kind)
    {
        switch (token)
        {
            case "point": kind = CommandKind.Point; return true;
            case "start": kind = CommandKind.Start; return true;
            case "stop": kind = CommandKind.Stop; return true;
            case "elements": kind = CommandKind.Elements; return true;
            case "doctor": kind = CommandKind.Doctor; return true;
            case "install-skill": kind = CommandKind.InstallSkill; return true;
            default: kind = default; return false;
        }
    }

    private static int? ResolveOwnerPid(Func<string, string?> env) =>
        ReadPid(env, "CLAUDE_PID") ?? ReadPid(env, "BIGARROW_OWNER_PID");

    private static int? ReadPid(Func<string, string?> env, string name)
    {
        var raw = env(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }

    public const string HelpText = """
        agentpointer — 让 AI agent 在 Windows 屏幕上指给你看

        用法：agentpointer <子命令> [选项]

        子命令：
          point          显示箭头并等待结束（默认 8 秒）
          start          后台显示箭头，上屏后返回（默认 300 秒，0 = 不限时）
          stop           移除同 owner 的箭头；无 owner 时只清未归属箭头
                         --all 清当前 Windows 会话全部；--hook 需要 owner，空操作时静默
          elements       列出窗口里可被 --element 匹配的元素，可用 --app/--window、--role、--match 过滤
          doctor         版本、显示器、DPI、窗口样式与命中测试（不证明点击送达）
          install-skill  安装到 ~/.claude/skills/agentpointer 与 ~/.agents/skills/agentpointer

        目标（互斥，给一个）：
          --at X,Y               全局物理像素，主屏左上角为 (0,0)，允许负数
          --rect X,Y,W,H         物理像素矩形，定位其中心
          --mouse                当前鼠标位置
          --window App[:title]   按进程名 + 可选窗口标题定位，默认不抢焦点
          --app App[:title]      同 --window
          --element <label>      UI Automation 元素名，需配合 --app/--window（缺省用前台窗口）；
                                 命中后按元素包围盒绘制
          --role <type>          限定 --element 或 elements 的元素类型（button / menuitem / edit ...）
          --anchor center|title  窗口锚点
          --display N            从 1 开始，编号见 doctor；at/rect 使用屏内局部像素，
                                 window/mouse 作为显示器过滤条件

        外观：
          --text <s>             牌子上的文字
          --from <side>          auto(默认)/left/right/top/bottom/top-left/top-right/bottom-left/bottom-right
          --color <name|#hex>    red orange yellow green teal blue purple pink black white，#RRGGBB 或 #RGB
          --size S|M|L           --style arrow           --shape bend|straight
          --corners round|sharp --border shadow|white-black|black
          --border-color <c>     --text-color <c>

        行为：
          --duration <sec>       非负有限秒数，0 表示不限时
          --raise                显式请求把目标窗口置前
          --no-raise             保持焦点（默认；与 --raise 同用时优先）
          --no-animation         无入场/呼吸/淡出
          --close-button         牌子右端画一个圆形 ×，点它即收起（原因 closed）
          --until-click          用户点到目标时收起（原因 target-clicked），点击照常送达下层应用
          --until-any-click      用户在任何位置点一下即收起

        点击交互：
          点牌子或箭杆会立刻收起箭头（原因 clicked）；目标附近与空白像素继续穿透到下层应用
          stop --hook 供 Claude Code 的 UserPromptSubmit 钩子使用：静默、永不写 stdout、永远返回 0
          ~/.claude/settings.json：{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command",
          "command":"agentpointer stop --hook"}]}]}}

        通用：
          --json                 结构化输出
          --dry-run              验证目标或预览技能安装路径，不绘制/写文件
          --help --version

        尚不支持：
          --follow --say --voice --png --edge-color
          --style ring|box、--shape zigzag|spiral、其他 anchor
          上述外观/行为兼容参数会给出未生效提示，--json 中见 ineffective
          --peekaboo / --snapshot 是 macOS 专属，返回退出码 2

        示例：
          agentpointer point --app notepad --element "File" --role menuitem --text "点这里"
          agentpointer elements --app notepad --match "file" --json

        退出码：0 成功  2 参数错误  3 目标未找到  4 权限/桌面不可用  5 功能未支持  6 内部错误

        环境变量：
          CLAUDE_PID / BIGARROW_OWNER_PID   绑定 agent PID，箭头随它退出，stop 按此隔离
        """;
}
