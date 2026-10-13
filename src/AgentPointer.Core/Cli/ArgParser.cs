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
        "--close-color", "--close-x-color", "--duration", "--voice", "--lang",
        "--peekaboo", "--snapshot", "--match",
    };

    /// <summary>开关型参数。</summary>
    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal)
    {
        "--mouse", "--no-raise", "--no-animation", "--follow", "--until-click", "--until-any-click",
        "--close-button", "--say", "--flash", "--notify", "--png", "--dry-run", "--json", "--logical",
        "--help", "--version", "--all", "--hook", "--keep-alive", "--raise",
        "--claude", "--codex", "--force", "--add-path",
    };

    /// <summary>只有 install-skill 才成立的开关。给别的子命令就是 agent 敲错了命令。</summary>
    private static readonly string[] InstallOnlyFlags =
    {
        "--claude", "--codex", "--force", "--add-path",
    };

    /// <summary>上游的 macOS 专属参数。在 Windows 上明确拒绝，不静默忽略。</summary>
    private static readonly HashSet<string> MacOnlyFlags = new(StringComparer.Ordinal)
    {
        "--peekaboo", "--snapshot",
    };

    /// <summary>
    /// 上游支持、Windows 版尚未实现的行为参数。不是静默忽略：直接以退出码 5
    /// （功能不支持）报错，让 agent 去掉这个参数重试，而不是以为它已经生效。
    /// </summary>
    private static readonly HashSet<string> UnsupportedOnWindows = new(StringComparer.Ordinal)
    {
        "--follow", "--png",
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

        // 语言必须在任何错误信息之前定下来：下面每条消息（包括 --lang 自己的）都靠它
        Lang.Configure(LanguageFromArgs(args), env, CultureInfo.CurrentUICulture);

        if (args.Length == 0)
        {
            return ParseResult.BadInput(Lang.T(
                "缺少子命令。可用子命令：point / start / stop / elements / doctor / install-skill",
                "Missing command. Available: point / start / stop / elements / doctor / install-skill"));
        }

        // --lang 允许出现在任何位置（包括与 --help 一起），所以先把取值整体校验一遍：
        // 放在 --help 的提前返回之前，非法值才能保证拿到退出码 2。
        for (var scan = 0; scan < args.Length; scan++)
        {
            var token = args[scan];

            if (token.StartsWith("--lang=", StringComparison.Ordinal))
            {
                var inline = token["--lang=".Length..];
                if (!TryNormalizeLanguage(inline, out _))
                {
                    return ParseResult.BadInput(Lang.T(
                        $"参数 '--lang' 取值 '{inline}' 非法。可用：en / zh",
                        $"Argument '--lang' has the invalid value '{inline}'. Available: en / zh"));
                }

                continue;
            }

            if (!string.Equals(token, "--lang", StringComparison.Ordinal)) continue;

            if (scan + 1 >= args.Length)
            {
                return ParseResult.BadInput(Lang.T(
                    "参数 '--lang' 缺少取值。可用：en / zh",
                    "Argument '--lang' is missing its value. Available: en / zh"));
            }

            if (!TryNormalizeLanguage(args[scan + 1], out _))
            {
                return ParseResult.BadInput(Lang.T(
                    $"参数 '--lang' 取值 '{args[scan + 1]}' 非法。可用：en / zh",
                    $"Argument '--lang' has the invalid value '{args[scan + 1]}'. Available: en / zh"));
            }

            scan++;
        }

        var index = 0;

        // --help / --version 可以不带子命令出现
        if (args[0] is "--help" or "-h" or "help") return ParseResult.Help();
        if (args[0] is "--version" or "-v") return ParseResult.Version();

        if (!TryParseKind(args[0], out var kind))
        {
            return ParseResult.BadInput(Lang.T(
                $"未知子命令 '{args[0]}'。可用子命令：point / start / stop / elements / doctor / install-skill",
                $"Unknown command '{args[0]}'. Available: point / start / stop / elements / doctor / install-skill"));
        }

        index++;

        // 先把参数扫进字典，保持"后出现覆盖先出现"的直觉行为
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        string? badFlag = null;
        string? badReason = null;

        while (index < args.Length && badFlag is null)
        {
            var token = args[index];
            index++;

            if (!token.StartsWith('-'))
            {
                badFlag = token;
                badReason = Lang.T(
                    "位置参数不受支持，所有输入都必须以 --key 形式给出",
                    "positional arguments are not supported, every input must be given as --key");
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
                return ParseResult.BadInput(Lang.T(
                    $"'{name}' 是 macOS 专属功能，Windows 上没有对应物",
                    $"'{name}' is macOS-only and has no Windows counterpart"));
            }

            if (UnsupportedOnWindows.Contains(name))
            {
                return ParseResult.Unsupported(Lang.T(
                    $"'{name}' 在 Windows 上尚未实现，本次没有绘制任何东西。请去掉该参数后重试。",
                    $"'{name}' is not supported on Windows yet, so nothing was drawn. "
                    + "Drop the argument and retry."));
            }

            if (BoolFlags.Contains(name))
            {
                switches.Add(name);
                continue;
            }

            if (ValueFlags.Contains(name))
            {
                if (value is null)
                {
                    if (index >= args.Length)
                    {
                        badFlag = name;
                        badReason = Lang.T("缺少取值", "is missing its value");
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
                ? Lang.T("不受支持的参数", "is not supported")
                : Lang.T("未知参数", "is not a known argument");
        }

        if (badFlag is not null)
        {
            var hint = Lang.T(
                "运行 agentpointer --help 查看可用参数。",
                "Run agentpointer --help to see the available arguments.");
            return ParseResult.BadInput(Lang.T(
                $"参数 '{badFlag}' {badReason}。{hint}",
                $"Argument '{badFlag}' {badReason}. {hint}"));
        }

        return Build(kind, flags, switches, env);
    }

    private static ParseResult Build(
        CommandKind kind,
        Dictionary<string, string> flags,
        HashSet<string> switches,
        Func<string, string?> env)
    {
        // ---------- 外观 ----------
        string? from = null;
        if (flags.TryGetValue("--from", out var fromRaw))
        {
            if (!FromSides.Contains(fromRaw))
            {
                return ParseResult.BadInput(Lang.T(
                    $"参数 '--from' 取值 '{fromRaw}' 非法。可用：auto / left / right / top / bottom / " +
                    "top-left / top-right / bottom-left / bottom-right",
                    $"Argument '--from' has the invalid value '{fromRaw}'. Available: auto / left / right / " +
                    "top / bottom / top-left / top-right / bottom-left / bottom-right"));
            }

            from = fromRaw.ToLowerInvariant();
        }

        string? color = null;
        if (flags.TryGetValue("--color", out var colorRaw) && !TryNormalizeColor(colorRaw, out color))
        {
            var names = string.Join(" / ", ColorNames);
            return ParseResult.BadInput(Lang.T(
                $"参数 '--color' 取值 '{colorRaw}' 非法。可用：{names} 或 #RRGGBB / #RGB",
                $"Argument '--color' has the invalid value '{colorRaw}'. Available: {names} or #RRGGBB / #RGB"));
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
            return ParseResult.BadInput(string.Join(Lang.ClauseSep, enumErrors));
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
            return ParseResult.BadInput(Lang.T("--role 需要配合 --element 使用。", "--role needs --element."));
        }

        // --logical 只改 at/rect 的坐标解释，给别处没有意义，直接点出来
        if (switches.Contains("--logical")
            && (kind is CommandKind.Point or CommandKind.Start)
            && target.Kind is not (TargetKind.At or TargetKind.Rect))
        {
            return ParseResult.BadInput(Lang.T(
                "--logical 需要配合 --at / --rect 使用。", "--logical needs --at / --rect."));
        }

        // --hook 是 Claude Code 的 UserPromptSubmit 钩子入口，只在 stop 上成立
        if (switches.Contains("--hook") && kind != CommandKind.Stop)
        {
            return ParseResult.BadInput(Lang.T(
                "--hook 只用于 stop（Claude Code 的 UserPromptSubmit 钩子）。",
                "--hook is only valid for stop (Claude Code's UserPromptSubmit hook)."));
        }

        // install-skill 专属开关出现在别的子命令上：直接拒绝，别让 agent 以为自己做成了什么
        var installOnly = InstallOnlyFlags.Where(switches.Contains).ToArray();
        if (installOnly.Length > 0 && kind != CommandKind.InstallSkill)
        {
            var installOnlyNames = string.Join(" / ", installOnly);
            return ParseResult.BadInput(Lang.T(
                $"{installOnlyNames} 只用于 install-skill。",
                $"{installOnlyNames} is only valid for install-skill."));
        }

        // point / start 必须有目标，否则 agent 无从知道指哪
        if (kind is CommandKind.Point or CommandKind.Start && targetResult.Target.Kind == TargetKind.None)
        {
            var commandName = kind.ToString().ToLowerInvariant();
            return ParseResult.BadInput(Lang.T(
                $"{commandName} 需要一个目标：--at / --rect / --mouse / --window / --app / --element 至少给一个",
                $"{commandName} needs a target: give at least one of "
                + "--at / --rect / --mouse / --window / --app / --element"));
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
                return ParseResult.BadInput(Lang.T(
                    $"参数 '--duration' 取值 '{durationRaw}' 非法，必须是 >= 0 的秒数",
                    $"Argument '--duration' has the invalid value '{durationRaw}', it must be a number of "
                    + "seconds >= 0"));
            }

            duration = parsed;
        }

        flags.TryGetValue("--text", out var text);
        flags.TryGetValue("--match", out var match);

        var command = new PointerCommand
        {
            Kind = kind,
            Text = text,
            Target = target with { Logical = switches.Contains("--logical") },
            Duration = duration,
            Json = switches.Contains("--json"),
            DryRun = switches.Contains("--dry-run"),
            StopAll = switches.Contains("--all"),
            StopHook = switches.Contains("--hook"),
            KeepAlive = switches.Contains("--keep-alive"),
            OwnerPid = ResolveOwnerPid(env),
            ElementsMatch = match,
            InstallClaude = switches.Contains("--claude"),
            InstallCodex = switches.Contains("--codex"),
            Force = switches.Contains("--force"),
            AddPath = switches.Contains("--add-path"),
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
                // 上游默认把窗口/元素目标带到前台；--no-raise 关掉，--raise 仅为兼容保留。
                // 坐标目标（--at / --rect / --mouse）没有窗口可置前，永远不置前。
                NoRaise = switches.Contains("--no-raise"),
                Raise = target.Kind is TargetKind.Window or TargetKind.Element
                        && !switches.Contains("--no-raise"),
                NoAnimation = switches.Contains("--no-animation"),
                Follow = switches.Contains("--follow"),
                UntilClick = switches.Contains("--until-click"),
                UntilAnyClick = switches.Contains("--until-any-click"),
                CloseButton = switches.Contains("--close-button"),
                Say = switches.Contains("--say"),
                Voice = flags.GetValueOrDefault("--voice"),
                Flash = switches.Contains("--flash"),
                Notify = switches.Contains("--notify"),
                Png = switches.Contains("--png"),
                // --edge-color 是唯一"接受但不生效"的外观参数；
                // --follow / --png 这类不支持的选项一律走退出码 5（见 Parse）。
                IneffectiveFlags = new[] { "--edge-color" }.Where(flags.ContainsKey).ToArray(),
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
                return new TargetResult(new TargetSpec(), Lang.T(
                    "--element 与 --at / --rect / --mouse 互斥。",
                    "--element is mutually exclusive with --at / --rect / --mouse."));
            }
            if (string.IsNullOrWhiteSpace(element))
            {
                return new TargetResult(new TargetSpec(), Lang.T(
                    "参数 '--element' 不能为空", "Argument '--element' must not be empty"));
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
            var joined = string.Join(Lang.T(" 和 ", " and "), provided);
            return new TargetResult(new TargetSpec(), Lang.T(
                $"目标参数互斥，但同时给了 {joined}",
                $"Target arguments are mutually exclusive, but {joined} were given"));
        }

        if (flags.TryGetValue("--at", out var atRaw))
        {
            if (!TryParsePoint(atRaw, out var at))
            {
                return new TargetResult(new TargetSpec(), Lang.T(
                    $"参数 '--at' 取值 '{atRaw}' 非法，格式应为 X,Y（允许负坐标）",
                    $"Argument '--at' has the invalid value '{atRaw}', the format is X,Y "
                    + "(negative coordinates are allowed)"));
            }

            return new TargetResult(
                new TargetSpec { Kind = TargetKind.At, At = at, DisplayIndex = display }, null);
        }

        if (flags.TryGetValue("--rect", out var rectRaw))
        {
            if (!TryParseRect(rectRaw, out var rect))
            {
                return new TargetResult(new TargetSpec(), Lang.T(
                    $"参数 '--rect' 取值 '{rectRaw}' 非法，格式应为 X,Y,W,H",
                    $"Argument '--rect' has the invalid value '{rectRaw}', the format is X,Y,W,H"));
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
                return new TargetResult(new TargetSpec(), Lang.T(
                    "参数 '--window' / '--app' 不能为空",
                    "Arguments '--window' / '--app' must not be empty"));
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
            error = Lang.T(
                $"参数 '--display' 取值 '{raw}' 非法，必须是 >= 1 的显示器序号（1-based，见 doctor）",
                $"Argument '--display' has the invalid value '{raw}', it must be a display index >= 1 "
                + "(1-based, see doctor)");
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
            errors.Add(Lang.T($"参数 '{name}' 无法识别", $"Argument '{name}' is not recognised"));
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

        var values = string.Join(" / ", allowed);
        errors.Add(Lang.T(
            $"参数 '{name}' 取值 '{raw}' 非法。可用：{values}",
            $"Argument '{name}' has the invalid value '{raw}'. Available: {values}"));
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

    /// <summary>按当前 UI 语言选出的帮助文本（保留旧调用点）。</summary>
    public static string HelpText => HelpFor(null, CultureInfo.CurrentUICulture);

    public static string HelpTextZh => """
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
          --logical              at/rect 改用逻辑坐标（96 DPI，与上游 macOS 的 point 一致），
                                 相对 --display 指定的屏（缺省主屏）换算成全局物理像素

        外观：
          --text <s>             牌子上的文字；{{value}} 变成可点的复制按钮（点击复制，箭头不收场）
          --from <side>          auto(默认)/left/right/top/bottom/top-left/top-right/bottom-left/bottom-right
          --color <name|#hex>    red orange yellow green teal blue purple pink black white，#RRGGBB 或 #RGB
          --size S|M|L           --style arrow           --shape bend|straight
          --corners round|sharp --border shadow|white-black|black
          --border-color <c>     --text-color <c>     --close-color <c>     --close-x-color <c>

        行为：
          --duration <sec>       非负有限秒数，0 表示不限时
          --raise                兼容占位（窗口/元素目标本来就默认置前）
          --no-raise             不把目标窗口置前，保持当前焦点
          --no-animation         无入场/呼吸/淡出
          --close-button         牌子右端画一个圆形 ×，点它即收起（原因 closed）
          --until-click          用户点到目标时收起（原因 target-clicked），点击照常送达下层应用
          --until-any-click      用户在任何位置点一下即收起
          --say                  上屏时把牌子上的话念出来（去掉 {{ }}，只念值本身）
          --voice <name>         --say 使用的语音，按名称子串匹配已安装语音；找不到用默认并警告
          --flash                目标窗口不在前台时闪 3 次任务栏按钮
          --notify               额外弹一条系统通知（标题 AgentPointer）

        点击交互：
          点牌子或箭杆会立刻收起箭头（原因 clicked）；目标附近与空白像素继续穿透到下层应用
          {{value}} 芯片点了只把值复制到剪贴板，箭头不收场，图标约 1.5 秒后从 ✓ 变回复制图标
          stop --hook 供 Claude Code 的 UserPromptSubmit 钩子使用：静默、永不写 stdout、永远返回 0
          ~/.claude/settings.json：{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command",
          "command":"agentpointer stop --hook"}]}]}}

        通用：
          --json                 结构化输出
          --lang en|zh           帮助文本语言；缺省看 AGENTPOINTER_LANG，再按 UI 语言（zh-* 中文，否则英文）
          --dry-run              验证目标或预览技能安装路径，不绘制/写文件
          --help --version

        install-skill 选项：
          --claude               只装到 ~/.claude/skills/agentpointer
          --codex                只装到 ~/.agents/skills/agentpointer（Codex）
          都不给                 两个都装
          --force                目标文件已存在且内容不同时覆盖（默认跳过，并提示重跑加 --force）
          --add-path             把 CLI 所在目录加入用户 PATH（HKCU，保留原值类型）并广播环境变更，
                                 之后请新开一个终端
          状态                   installed 新装 / up-to-date 已最新 / exists 已存在内容不同被跳过 /
                                 updated 已覆盖；四种情况都返回 0
          --add-path 的 path 字段 added 新加 / already 已存在 / skipped 跳过
          --dry-run 只报告每个目标的计划状态，不写文件、不改 PATH

        尚不支持：
          --edge-color；--style ring|box、--shape zigzag|spiral、其他 anchor
          以上外观兼容参数会给出未生效提示，--json 中见 ineffective
          --follow / --png 尚未实现，直接返回退出码 5，请去掉参数后重试
          --peekaboo / --snapshot 是 macOS 专属，返回退出码 2

        示例：
          agentpointer point --app notepad --element "File" --role menuitem --text "点这里"
          agentpointer elements --app notepad --match "file" --json

        退出码：0 成功  2 参数错误  3 目标未找到  4 权限/桌面不可用  5 功能未支持  6 内部错误

        环境变量：
          CLAUDE_PID / BIGARROW_OWNER_PID   绑定 agent PID，箭头随它退出，stop 按此隔离
          AGENTPOINTER_LANG                 en / zh，帮助文本语言
        """;

    /// <summary>
    /// 英文帮助。它和 <see cref="HelpTextZh"/> 必须覆盖同一批参数——
    /// 少一个选项就是一种语言下的静默功能缺失（HelpTextTests 逐项比对）。
    /// </summary>
    public static string HelpTextEn => """
        agentpointer — let an AI agent point at things on your Windows screen

        Usage: agentpointer <command> [options]

        Commands:
          point          draw the arrow and wait for it to end (default 8 s)
          start          draw the arrow in the background and return (default 300 s, 0 = no limit)
          stop           remove the arrows of the same owner; without an owner only unowned arrows
                         --all clears every arrow in this Windows session
                         --hook needs an owner and is silent (Claude Code UserPromptSubmit hook)
          elements       list what a window exposes to --element; filter with --app/--window, --role, --match
          doctor         version, monitors, DPI, overlay styles and hit testing (does not prove click delivery)
          install-skill  install into ~/.claude/skills/agentpointer and ~/.agents/skills/agentpointer

        Targets (mutually exclusive, give one):
          --at X,Y               global physical pixels, (0,0) at the primary monitor's top-left, negatives ok
          --rect X,Y,W,H         physical pixel rectangle, points at its centre
          --mouse                the current mouse position
          --window App[:title]   match by process name plus an optional window title, does not steal focus
          --app App[:title]      same as --window
          --element <label>      UI Automation element name, needs --app/--window (else the foreground
                                 window); the arrow is drawn on the element's bounding box
          --role <type>          restrict --element or elements to a control type (button / menuitem / edit)
          --anchor center|title  window anchor
          --display N            1-based, numbering as in doctor; for at/rect the coordinates are local to
                                 that monitor, for window/mouse it filters the monitor
          --logical              read at/rect as logical (96 DPI, same as upstream macOS points) coordinates
                                 relative to --display (default primary), then convert to physical pixels

        Appearance:
          --text <s>             text on the sign; {{value}} becomes a clickable copy button (copy, arrow stays)
          --from <side>          auto(default)/left/right/top/bottom/top-left/top-right/bottom-left/bottom-right
          --color <name|#hex>    red orange yellow green teal blue purple pink black white, #RRGGBB or #RGB
          --size S|M|L           --style arrow           --shape bend|straight
          --corners round|sharp  --border shadow|white-black|black
          --border-color <c>     --text-color <c>     --close-color <c>     --close-x-color <c>

        Behaviour:
          --duration <sec>       finite, non-negative seconds; 0 means no limit
          --raise                compatibility placeholder (window/element targets are raised by default)
          --no-raise             do not raise the target window, keep the current focus
          --no-animation         no entrance / breathing / fade animation
          --close-button         draw a round x at the right end of the sign; clicking it ends the arrow
          --until-click          end when the human clicks the target; the click still reaches the app
          --until-any-click      end on any click anywhere
          --say                  speak the sign when it appears ({{ }} removed, the bare value is spoken)
          --voice <name>         voice for --say, matched by name substring; unknown name warns and falls back
          --flash                blink the target window's taskbar button 3 times when it is not in front
          --notify               also show a Windows notification (title AgentPointer)

        install-skill options:
          --claude               install only into ~/.claude/skills/agentpointer
          --codex                install only into ~/.agents/skills/agentpointer (Codex)
          neither                install into both
          --force                overwrite a destination whose content differs (otherwise it is skipped)
          --add-path             add the CLI's directory to the user PATH (HKCU, value type preserved) and
                                 broadcast the environment change; open a new terminal afterwards
          status                 installed / up-to-date / exists (skipped, rerun with --force) / updated;
                                 all four exit with code 0
          path in JSON           added / already / skipped
          --dry-run              report the planned status of every destination, write nothing, change no PATH

        Click interaction:
          clicking the sign or the shaft ends the arrow at once; the target and the empty pixels stay
          click-through to the app below
          a {{value}} chip only copies the value (the arrow stays; the icon turns back after ~1.5 s)
          stop --hook is silent, never writes stdout and always exits 0
          ~/.claude/settings.json: {"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command",
          "command":"agentpointer stop --hook"}]}]}}

        Common:
          --json                 structured output
          --lang en|zh           help language; next comes AGENTPOINTER_LANG, then the UI language
                                 (zh-* Chinese, anything else English)
          --dry-run              validate the target or preview skill installation, draw/write nothing
          --help --version

        Not supported yet:
          --edge-color; --style ring|box, --shape zigzag|spiral, other anchors
          those appearance flags are accepted and reported as ineffective in --json
          --follow / --png are not implemented and exit with code 5; drop them and retry
          --peekaboo / --snapshot are macOS-only and exit with code 2

        Examples:
          agentpointer point --app notepad --element "File" --role menuitem --text "Click here"
          agentpointer elements --app notepad --match "file" --json

        Exit codes: 0 ok  2 bad input  3 target not found  4 permission/desktop unavailable  5 unsupported  6 internal error

        Environment:
          CLAUDE_PID / BIGARROW_OWNER_PID   bind an agent PID: the arrow dies with it, stop is scoped to it
          AGENTPOINTER_LANG                 en / zh, help text language
        """;

    /// <summary>
    /// 帮助文本的语言选择：显式 <c>--lang en|zh</c> 优先，其次是环境变量 <c>AGENTPOINTER_LANG</c>，
    /// 最后按 UI 语言（zh-* 中文，其余英文）。取值非法一律当作没给。
    /// </summary>
    public static string HelpFor(string? lang, CultureInfo? ui) =>
        Lang.Resolve(lang, Environment.GetEnvironmentVariable, ui) ? HelpTextZh : HelpTextEn;

    /// <summary>
    /// 从原始参数里取 <c>--lang</c>：走帮助路径时参数还没进字典，调用方需要自己扫一遍。
    /// 缺失或取值非法都返回 null（非法取值在 <see cref="Parse"/> 里已经退过 2）。
    /// </summary>
    public static string? LanguageFromArgs(string[]? args)
    {
        if (args is null) return null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];

            if (token.StartsWith("--lang=", StringComparison.Ordinal))
            {
                if (TryNormalizeLanguage(token["--lang=".Length..], out var inline)) return inline;
                continue;
            }

            if (!string.Equals(token, "--lang", StringComparison.Ordinal)) continue;

            if (index + 1 < args.Length && TryNormalizeLanguage(args[index + 1], out var next)) return next;
            index++;
        }

        return null;
    }

    /// <summary>归一化 --lang：en / zh，大小写无关，也接受 en-US / zh-CN 这类区域写法。</summary>
    internal static bool TryNormalizeLanguage(string? raw, out string? normalized) =>
        Lang.TryNormalize(raw, out normalized);
}
