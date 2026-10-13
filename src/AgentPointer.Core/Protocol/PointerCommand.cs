using System.Drawing;

namespace AgentPointer.Core.Protocol;

/// <summary>子命令。对齐上游 bigarrow 的六个子命令，不要随意增删——兼容性依赖于此。</summary>
public enum CommandKind
{
    /// <summary>画一个会自己消失的箭头。默认时长 8 秒。</summary>
    Point,

    /// <summary>画一个持续存在的箭头，立即返回。默认时长 300 秒。</summary>
    Start,

    /// <summary>移除箭头。</summary>
    Stop,

    /// <summary>列出某个窗口下可被 --element 匹配的元素。</summary>
    Elements,

    /// <summary>环境诊断：版本、显示器、DPI、覆盖层可用性。</summary>
    Doctor,

    /// <summary>安装 skill 到 ~/.claude/skills 与 ~/.agents/skills。</summary>
    InstallSkill,
}

/// <summary>目标类型。</summary>
public enum TargetKind
{
    None,

    /// <summary>--at X,Y，虚拟屏幕物理像素坐标。</summary>
    At,

    /// <summary>--rect X,Y,W,H。</summary>
    Rect,

    /// <summary>--mouse，当前鼠标位置。</summary>
    Mouse,

    /// <summary>--window / --app App[:title]，进程名 + 可选窗口标题。</summary>
    Window,

    /// <summary>--element Label --app App，UI Automation 按元素名定位。</summary>
    Element,
}

/// <summary>目标描述。最多解析出一个，多个目标同时给出按优先级取一个并报错。</summary>
public sealed record TargetSpec
{
    public TargetKind Kind { get; init; } = TargetKind.None;

    /// <summary>--at 的坐标。</summary>
    public Point? At { get; init; }

    /// <summary>--rect 的矩形。</summary>
    public Rectangle? Rect { get; init; }

    /// <summary>--window / --app 的原始查询串，形如 <c>WindowsTerminal</c> 或 <c>Chrome:Inbox</c>。</summary>
    public string? WindowQuery { get; init; }

    /// <summary>--element 的元素名。</summary>
    public string? ElementQuery { get; init; }

    /// <summary>--role 的控件类型限定（button / radiobutton / menubutton / textfield ...）。</summary>
    public string? Role { get; init; }

    /// <summary>--display N，把坐标解释为第 N 个显示器的局部坐标（1-based，与上游一致）。</summary>
    public int? DisplayIndex { get; init; }
}

/// <summary>外观参数。尚未实现的样式会在输出中说明回退；上游免费样式不属于付费功能。</summary>
public sealed record LookSpec
{
    /// <summary>--from：箭头从哪一侧指向目标。上游 9 方位 + auto（默认）。</summary>
    public string? From { get; init; }

    /// <summary>--anchor：窗口目标的锚点（center / title / top-left / ...），默认 center。</summary>
    public string? Anchor { get; init; }

    /// <summary>--color：颜色名或 #RRGGBB。</summary>
    public string? Color { get; init; }

    /// <summary>--size：S / M / L。</summary>
    public string? Size { get; init; }

    /// <summary>--style：arrow / ring / box。</summary>
    public string? Style { get; init; }

    /// <summary>--shape：bend / straight / zigzag / spiral。</summary>
    public string? Shape { get; init; }

    /// <summary>--corners：round / sharp。</summary>
    public string? Corners { get; init; }

    /// <summary>--border：shadow / white-black / black。</summary>
    public string? Border { get; init; }

    public string? BorderColor { get; init; }
    public string? TextColor { get; init; }
    public string? EdgeColor { get; init; }
    public string? CloseColor { get; init; }
    public string? CloseXColor { get; init; }
}

/// <summary>行为参数。未在 Windows 上实现的参数仍会被解析，但标记为未生效。</summary>
public sealed record BehaviorSpec
{
    public bool NoRaise { get; init; }

    /// <summary>--raise：显式请求将目标窗口置前；默认不抢焦点。</summary>
    public bool Raise { get; init; }

    /// <summary>--no-animation：直出最终帧，无入场/呼吸/淡出（上游语义，已生效）。</summary>
    public bool NoAnimation { get; init; }

    public bool Follow { get; init; }
    public bool UntilClick { get; init; }

    /// <summary>--until-any-click：任意一次点击后消失（解析接受，Windows 版未实现）。</summary>
    public bool UntilAnyClick { get; init; }

    public bool CloseButton { get; init; }
    public bool Say { get; init; }

    /// <summary>--voice：--say 的语音（解析接受，Windows 版未实现）。</summary>
    public string? Voice { get; init; }

    public bool Png { get; init; }

    /// <summary>由本命令使用、但在 Windows 版上尚未实现的参数名。用于 --json 输出提示，避免 agent 静默踩空。</summary>
    public IReadOnlyList<string> IneffectiveFlags { get; init; } = Array.Empty<string>();
}

/// <summary>一条已解析的命令。</summary>
public sealed record PointerCommand
{
    public required CommandKind Kind { get; init; }

    /// <summary>牌子上显示的文字。</summary>
    public string? Text { get; init; }

    public TargetSpec Target { get; init; } = new();

    /// <summary>显示秒数。0 表示不限时。已按子命令填好默认值。</summary>
    public double Duration { get; init; }

    public LookSpec Look { get; init; } = new();

    public BehaviorSpec Behavior { get; init; } = new();

    /// <summary>--json：结构化输出。</summary>
    public bool Json { get; init; }

    /// <summary>--dry-run：只报告"本来会指向哪里"，不实际绘制。</summary>
    public bool DryRun { get; init; }

    /// <summary>stop --all：清掉全部箭头。</summary>
    public bool StopAll { get; init; }

    /// <summary>stop --hook：供 Claude Code 的 UserPromptSubmit hook 使用。</summary>
    public bool StopHook { get; init; }

    /// <summary>
    /// 内部参数 --keep-alive：本次调用是 start 派生出来的常驻子进程，
    /// 负责"显示箭头 → 等待结束 → 淡出"的完整生命周期。不进帮助文本。
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>agent 进程 PID（CLAUDE_PID / BIGARROW_OWNER_PID），箭头随它退出而消失。</summary>
    public int? OwnerPid { get; init; }

    /// <summary>元素匹配用的查询串，elements 子命令用。</summary>
    public string? ElementsMatch { get; init; }
}
