using System.Drawing;
using System.Globalization;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPointer.Cli;
using AgentPointer.Cli.Interop;
using AgentPointer.Cli.Overlay;
using AgentPointer.Cli.Targeting;
using AgentPointer.Core;
using AgentPointer.Core.Cli;
using AgentPointer.Core.Protocol;
using AgentPointer.Core.Rendering;
using AgentPointer.Core.Targeting;

namespace AgentPointer.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // PowerShell 5 用当前控制台代码页解码原生命令的管道输出。
        // 保持宿主编码不变，避免退出时恢复代码页后，宿主把 UTF-8 字节按 CP936 解码。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codePage = NativeMethods.GetConsoleOutputCP();
        Console.OutputEncoding = codePage == 0 ? new UTF8Encoding(false) : Encoding.GetEncoding((int)codePage);
        return Run(args);
    }

    private static int Run(string[] args)
    {
        var parse = ArgParser.Parse(args);

        // --help / --version 走 stdout
        if (parse.HelpRequested)
        {
            // 语言来自 --lang，其次 AGENTPOINTER_LANG，最后 UI 语言
            Out(ArgParser.HelpFor(ArgParser.LanguageFromArgs(args), CultureInfo.CurrentUICulture));
            return ExitCode.Ok;
        }

        if (parse.VersionRequested)
        {
            Out(AgentPointerInfo.Version);
            return ExitCode.Ok;
        }

        if (!parse.Ok)
        {
            return Fail(parse.ExitCode, parse.Error
                ?? Lang.T("未知错误", "Unknown error"), args.Contains("--json"));
        }

        var command = parse.Command!;

        try
        {
            if (command.KeepAlive && (command.Kind != CommandKind.Start || ShownSignal.EventName is null))
            {
                return Fail(ExitCode.BadInput, Lang.T(
                    "--keep-alive 是后台启动的内部参数。",
                    "--keep-alive is an internal flag of the background start."), command.Json);
            }

            return command.Kind switch
            {
                CommandKind.Doctor => RunDoctor(command),
                CommandKind.Elements => RunElements(command),
                CommandKind.Stop => RunStop(command),
                CommandKind.Start when !command.KeepAlive && !command.DryRun => RunStart(command, args),
                CommandKind.Point or CommandKind.Start => RunPoint(command),
                CommandKind.InstallSkill => SkillInstaller.Install(command),
                _ => Fail(ExitCode.BadInput, Lang.T("不支持的子命令", "unsupported command")),
            };
        }
        catch (OverlayUnavailableException ex)
        {
            return Fail(ExitCode.PermissionMissing, ex.Message, command.Json);
        }
        catch (TargetNotFoundException ex)
        {
            return Fail(ExitCode.TargetNotFound, ex.Message, command.Json);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(ExitCode.PermissionMissing, ex.Message, command.Json);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return Fail(ExitCode.PermissionMissing, ex.Message, command.Json);
        }
        catch (Exception ex)
        {
            // 任何未预料的异常都要转成退出码，绝不让调用方看到堆栈
            return Fail(ExitCode.InternalError, Lang.T(
                $"内部错误：{ex.Message}", $"Internal error: {ex.Message}"), command.Json);
        }
    }

    // ---------------- point / start ----------------

    private static int RunStart(PointerCommand command, string[] args)
    {
        var eventName = @"Local\AgentPointer.Shown." + Guid.NewGuid().ToString("N");
        var shown = ShownSignal.CreateListener(eventName);
        if (shown == IntPtr.Zero) throw new Win32Exception();
        var resultPath = Path.GetTempFileName();
        var process = default(NativeMethods.PROCESS_INFORMATION);
        var environmentBlock = IntPtr.Zero;

        try
        {
            // --element 的 UI Automation 遍历可能被一次卡住的跨进程调用拖过 5 秒，而 5 秒
            // 预算买的是"窗口已经画出来"。所以元素先在父进程里解析成矩形，再交给后台
            // 子进程：UIA 完全不进那 5 秒，也就不可能再把它撑爆。
            var resolved = command.Target.Kind == TargetKind.Element ? ResolveElementForChild(command) : null;

            var executable = Environment.ProcessPath ?? throw new InvalidOperationException(
                Lang.T("无法确定程序路径。", "Could not determine the executable path."));
            var arguments = new List<string> { executable };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add(typeof(Program).Assembly.Location);
            }
            arguments.AddRange(args);
            arguments.Add("--keep-alive");
            arguments.Add("--json");
            var commandLine = new StringBuilder(string.Join(" ", arguments.Select(QuoteProcessArgument)));
            var environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);
            environment[ShownSignal.EnvironmentVariable] = eventName;
            environment[ShownSignal.ResultEnvironmentVariable] = resultPath;

            if (resolved is not null)
            {
                foreach (var (key, value) in ResolvedTargetCodec.Encode(resolved))
                {
                    environment[key] = value;
                }
            }
            environmentBlock = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(
                string.Join("\0", environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Key + "=" + e.Value)) + "\0\0");
            var startup = new NativeMethods.STARTUPINFO
            {
                cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
            };

            // 不继承任何调用方句柄：即使重定向了标准流，普通 Process.Start 仍可能
            // 把 agent 的其他管道句柄传下去，使调用方一直等到后台箭头结束才看到 EOF。
            const uint noWindowAndUnicodeEnvironment = 0x08000400;
            if (!NativeMethods.CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero,
                    false, noWindowAndUnicodeEnvironment, environmentBlock, null, ref startup, out process))
            {
                throw new Win32Exception();
            }

            if (!ShownSignal.WaitShownOrExit(shown, process.hProcess, 5000))
            {
                if (NativeMethods.WaitForSingleObject(process.hProcess, 0) == NativeMethods.WAIT_TIMEOUT)
                {
                    if (!NativeMethods.TerminateProcess(process.hProcess, ExitCode.InternalError)) throw new Win32Exception();
                    NativeMethods.WaitForSingleObject(process.hProcess, 2000);
                    return Fail(ExitCode.InternalError, Lang.T(
                        "后台箭头未能在 5 秒内显示。",
                        "The background arrow did not appear within 5 seconds."), command.Json);
                }

                if (!NativeMethods.GetExitCodeProcess(process.hProcess, out var code)) throw new Win32Exception();
                var error = Lang.T(
                    "后台箭头在显示前退出。", "The background arrow exited before it appeared.");
                var failedReport = File.ReadAllText(resultPath, Encoding.UTF8);
                if (failedReport.Length > 0)
                {
                    using var payload = JsonDocument.Parse(failedReport);
                    if (payload.RootElement.TryGetProperty("error", out var message)) error = message.GetString() ?? error;
                }
                return Fail(code == ExitCode.Ok ? ExitCode.InternalError : (int)code, error, command.Json);
            }

            var report = File.ReadAllText(resultPath, Encoding.UTF8);
            if (!command.Json)
            {
                var notices = IneffectiveNotice(command);
                if (notices.Length > 0)
                {
                    Err(Lang.T("以下参数暂不生效：", "These arguments have no effect yet: ")
                        + Lang.Join(notices));
                }
            }
            Out(command.Json
                ? report
                : Lang.T($"箭头已显示（PID {process.dwProcessId}）。", $"Arrow shown (PID {process.dwProcessId})."));
            return ExitCode.Ok;
        }
        finally
        {
            if (process.hThread != IntPtr.Zero) NativeMethods.CloseHandle(process.hThread);
            if (process.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(process.hProcess);
            if (environmentBlock != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(environmentBlock);
            NativeMethods.CloseHandle(shown);
            File.Delete(resultPath);
        }
    }

    // Win32 的参数转义（不是 shell 转义）：引号前与结尾的反斜线必须翻倍。
    private static string QuoteProcessArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    /// <summary>
    /// <c>start --element</c>：在父进程里把整套 UI Automation 解析做完（置前也在这里），
    /// 结果经环境变量交给后台子进程，见 <see cref="ResolvedTargetCodec"/>。
    /// </summary>
    private static ResolvedTarget ResolveElementForChild(PointerCommand command)
    {
        var window = ResolveElementWindow(command);
        var raised = TryRaiseTarget(window.Handle, command);
        var found = ElementFinder.Find(window.Handle, command.Target.ElementQuery!, command.Target.Role, window.Title);
        var element = found.Element;

        // 窗口句柄必须一起下发：子进程手里的目标除了坐标还有一个 HWND，
        // --flash（以及以后任何按窗口干活的行为）都靠它。
        return new ResolvedTarget(
            element.X, element.Y, element.Width, element.Height, window.Handle, element, raised, found.Truncated);
    }

    private static int RunPoint(PointerCommand command)
    {
        if (MonitorService.IsSecureOrLockedDesktop())
        {
            return Fail(ExitCode.PermissionMissing, Lang.T(
                "当前处于锁屏或 UAC 安全桌面，无法在屏幕最上层绘制覆盖层。",
                "The desktop is locked or a UAC secure desktop is up, so the overlay cannot be drawn on top. "
                + "Unlock the screen and retry."), command.Json);
        }

        using var manager = new OverlayManager();

        var target = ResolveTarget(
            manager, command, out var element, out var targetRect, out var targetWindow, out var raised,
            out var preResolved, out var truncated);

        // 命中了也可能只是"在截断前先撞上了一个"：提醒一句，别让 agent 以为清单是完整的。
        if (truncated && !command.Json)
        {
            Err(Lang.T(
                "UI Automation 搜索被截断，可能还有没扫到的元素。"
                + "用 --app App:title 缩小窗口范围，或 agentpointer elements --app X 查看清单。",
                "The UI Automation search was cut off, so more elements may exist. "
                + "Narrow the window with --app App:title, or list them with agentpointer elements --app X."));
        }

        var truncatedJson = truncated ? (bool?)true : null;

        var display = ToZeroBased(command.Target.DisplayIndex);
        if (display is { } index && !MonitorService.TryFromIndex(index, manager.Monitors, out _))
        {
            return Fail(ExitCode.BadInput, Lang.T(
                $"显示器序号 {command.Target.DisplayIndex} 不存在（共 {manager.Monitors.Count} 块，从 1 开始）",
                $"Display {command.Target.DisplayIndex} does not exist "
                + $"({manager.Monitors.Count} in total, numbered from 1). Run agentpointer doctor."), command.Json);
        }

        // --logical：把 at/rect 从 96 DPI 逻辑坐标换算成全局物理像素（上游 macOS 的 point 语义）。
        // 换算以 --display 指定的屏为参照，缺省用主屏。
        // 父进程解析好的元素矩形已经是全局物理像素，不能再换算第二次。
        if (command.Target.Logical && !preResolved)
        {
            var logicalMonitor = display is { } logicalIndex
                ? manager.Monitors[logicalIndex]
                : PrimaryMonitor(manager.Monitors);

            if (command.Target.Kind == TargetKind.Rect)
            {
                targetRect = LogicalCoordinates.ToPhysical(targetRect, logicalMonitor.Bounds, logicalMonitor.Scale);
                target = new Point(targetRect.Left + targetRect.Width / 2, targetRect.Top + targetRect.Height / 2);
            }
            else
            {
                target = LogicalCoordinates.ToPhysical(target, logicalMonitor.Bounds, logicalMonitor.Scale);
                targetRect = new Rectangle(target, new Size(1, 1));
            }
        }

        // 只有输入坐标需要从显示器局部换算；窗口和鼠标已经是全局坐标。
        // --logical 已经自己算成全局物理像素，不能再按 --display 翻译第二次。
        var localDisplay = !command.Target.Logical && command.Target.Kind is TargetKind.At or TargetKind.Rect
            ? display
            : null;
        manager.TryToGlobal(localDisplay, target, out var global);

        // --rect 的整块矩形都要平移，不能只平移中心点：--until-click 是拿 targetRect
        // 去比对点击坐标的，只平移中心等于让它去盯屏幕角落里的一个不存在的矩形。
        if (localDisplay is { } localIndex && command.Target.Kind == TargetKind.Rect)
        {
            targetRect = LogicalCoordinates.ToGlobal(targetRect, manager.Monitors[localIndex].Bounds.Location);
        }
        var monitorForTarget = manager.MonitorFor(global);
        if (display is { } selected && monitorForTarget.Index != selected)
        {
            throw new TargetNotFoundException(Lang.T(
                $"目标不在显示器 #{selected + 1} 内。",
                $"The target is not on display #{selected + 1}."));
        }

        if (command.DryRun)
        {
            return Report(command, global,
                Lang.T("dry-run：本来会指向这里", "dry-run: would point here"), monitorForTarget.Index + 1,
                truncatedJson);
        }

        var notices = IneffectiveNotice(command);
        if (!command.Json && notices.Length > 0)
        {
            Err(Lang.T("以下参数暂不生效：", "These arguments have no effect yet: ")
                + Lang.Join(notices));
        }

        var sign = new SignContent(command.Text, SafeParseColor(command.Look.Color));
        var values = AgentPointer.Core.Rendering.SignText.Values(sign.RenderText);
        using var stop = StopSignal.CreateListener(command.OwnerPid);
        var form = manager.WindowFor(monitorForTarget);

        // 点牌子 / 箭杆 / 关闭按钮 = 用户看过了，立刻收场，不必再等 stop 信号
        StopReason? dismissed = null;
        form.DismissRequested = reason => dismissed = reason;

        // --until-click / --until-any-click：钩子装在当前 UI 线程，
        // 等待循环里的 Application.DoEvents 会把回调泵进来。
        MouseWatch? watch = null;
        if (command.Behavior.UntilClick || command.Behavior.UntilAnyClick)
        {
            var scale = monitorForTarget.Dpi / 96d;
            var watchRect = command.Target.Kind is TargetKind.At or TargetKind.Mouse
                ? Around(global, (int)Math.Round(UntilClickRadiusPx * scale))
                : targetRect;

            watch = new MouseWatch(
                watchRect,
                command.Behavior.UntilAnyClick,
                () => dismissed = StopReason.TargetClicked);
        }

        // 钩子一旦装上就必须拆掉：进程退出虽然会带走低层钩子，但异常路径跳过显式
        // UnhookWindowsHookEx 之前，系统会一直回调一个已经不再需要它的箭头。
        try
        {
            form.ShowPointer(global, monitorForTarget.Bounds, monitorForTarget.Dpi, sign, command);

            // 常驻子进程上屏后立刻放行 start 父进程（--keep-alive 内部开关）
            if (command.KeepAlive)
            {
                var deadline = Environment.TickCount64 + PointerStyle.IntroMs + 1000;
                while (form.IsEntering && Environment.TickCount64 < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }

                if (form.IsEntering) throw new InvalidOperationException(
                    Lang.T("箭头入场动画未完成。", "The arrow's entrance animation did not finish."));
                Out(JsonSerializer.Serialize(new
                {
                    ok = true,
                    started = true,
                    raised = raised,
                    pid = Environment.ProcessId,
                    pointed = new { x = global.X, y = global.Y },
                    element = ElementJson(element),
                    text = sign.DisplayText,
                    values = values.Count > 0 ? values : null,
                    monitor = monitorForTarget.Index + 1,
                    ineffective = notices,
                    truncated = truncatedJson,
                }, JsonOptions));
                ShownSignal.Signal();
                Environment.SetEnvironmentVariable(ShownSignal.ResultEnvironmentVariable, null);
            }

            // 3) 注意力特性：念牌子（--say）、闪任务栏（--flash）、系统通知（--notify）。
            //    全部在箭头已经上屏之后启动，任何一个失败都只写 stderr，不影响箭头本身。
            using var speaker = command.Behavior.Say
                ? SignSpeaker.Start(sign.DisplayText, command.Behavior.Voice, command.Json)
                : null;

            using var toast = command.Behavior.Notify
                ? ToastNotifier.Show(sign.DisplayText, command.Json)
                : null;

            if (command.Behavior.Flash)
            {
                FlashTarget(targetWindow, command.Json);
            }

            // duration=0 表示不限时；缺省时由解析器按子命令填好默认值。
            // 等待必须做成 30ms 轮询 + DoEvents：动画（入场/呼吸）由 WinForms Timer 驱动，
            // 主线程死等会让 Timer 永不触发——箭头会停在入场的第一帧（全透明）。
            var startedAt = Environment.TickCount64;
            StopReason reason;
            while (true)
            {
                if (dismissed is { } requested)
                {
                    reason = requested;
                    break; // 牌子 / 箭杆 / × / 目标上的点击
                }

                reason = stop.Wait(StopReason.DurationElapsed, 0.03);

                if (reason != StopReason.DurationElapsed)
                {
                    break; // stop 信号或 owner 进程退出
                }

                Application.DoEvents();

                var elapsedSeconds = (Environment.TickCount64 - startedAt) / 1000d;
                if (command.Duration > 0 && elapsedSeconds >= command.Duration)
                {
                    break;
                }
            }

            if (!command.Behavior.NoAnimation)
            {
                form.BeginFadeOut(FadeDurationMs);
                WaitForFade(form);
            }

            manager.HideAll();

            if (command.Json && !command.KeepAlive)
            {
                Out(JsonSerializer.Serialize(new
                {
                    ok = true,
                    raised = raised,
                    pointed = new { x = global.X, y = global.Y },
                    element = ElementJson(element),
                    monitor = monitorForTarget.Index + 1,
                    endedBy = ReasonName(reason),
                    text = sign.DisplayText,
                    values = values.Count > 0 ? values : null,
                    ineffective = IneffectiveNotice(command),
                    truncated = truncatedJson,
                }, JsonOptions));
            }

            return ExitCode.Ok;
        }
        finally
        {
            watch?.Dispose();
        }
    }

    /// <summary>上游承诺 --json 标注"接受但未生效"的参数，不让 agent 静默踩空。</summary>
    private static string[] IneffectiveNotice(PointerCommand command)
    {
        var notices = new List<string>(command.Behavior.IneffectiveFlags);

        if (command.Look.Shape is "zigzag" or "spiral")
        {
            notices.Add(Lang.T(
                $"--shape {command.Look.Shape}（尚未实现，当前按 bend 渲染）",
                $"--shape {command.Look.Shape} (not implemented yet, renders as bend)"));
        }

        if (command.Look.Style is "ring" or "box")
        {
            notices.Add(Lang.T(
                $"--style {command.Look.Style}（尚未实现，当前按 arrow 渲染）",
                $"--style {command.Look.Style} (not implemented yet, renders as arrow)"));
        }

        if (command.Look.Anchor is { } anchor && anchor != "center" && anchor != "title")
        {
            notices.Add(Lang.T(
                $"--anchor {anchor}（当前按 center 定位）",
                $"--anchor {anchor} (positions as center for now)"));
        }

        return notices.Distinct().ToArray();
    }

    /// <summary>
    /// <c>--raise</c>：把目标窗口置前。失败不算致命（指着一个被挡住的窗口很难用，
    /// 但不能因此让裸命令失败），只在 stderr 提醒一次，并让 <c>--json</c> 带上
    /// <c>raised: false</c>。返回 null 表示根本没尝试置前。
    /// </summary>
    private static bool? TryRaiseTarget(IntPtr window, PointerCommand command)
    {
        if (window == IntPtr.Zero || !command.Behavior.Raise || command.DryRun) return null;

        if (WindowFinder.TryRaise(window)) return true;

        if (!command.Json)
        {
            Err(Lang.T(
                "置前失败（Windows 前台锁）：目标窗口可能停在其它窗口后面，箭头也会被挡住。",
                "Could not bring the target window to the front (Windows foreground lock); "
                + "it and the arrow may stay behind other windows."));
        }

        return false;
    }

    /// <summary>
    /// --flash：目标窗口不在前台时闪 3 次任务栏按钮。
    /// <c>FLASHW_TIMERNOFG</c> 让系统在窗口回到前台时自动停闪，不必再补一次 FLASHW_STOP。
    /// 没有 HWND 的目标（--at / --rect / --mouse）什么也不做。
    /// </summary>
    private static void FlashTarget(IntPtr window, bool json)
    {
        if (window == IntPtr.Zero || NativeMethods.GetForegroundWindow() == window) return;

        try
        {
            var info = new NativeMethods.FLASHWINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
                hwnd = window,
                dwFlags = NativeMethods.FLASHW_TRAY | NativeMethods.FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0,
            };

            NativeMethods.FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            if (!json) Err(Lang.T(
                $"任务栏闪烁失败：{ex.Message}", $"Flashing the taskbar button failed: {ex.Message}"));
        }
    }

    /// <summary>--at / --mouse 没有矩形，用一个以目标为心的小方框代替（逻辑像素，再乘 DPI）。</summary>
    private const int UntilClickRadiusPx = 24;

    private const int FadeDurationMs = Core.Rendering.PointerStyle.FadeOutMs;

    private static Rectangle Around(Point center, int radius) =>
        new(center.X - radius, center.Y - radius, radius * 2, radius * 2);

    /// <summary>--json 的 endedBy：新增的收场原因用 kebab-case，旧值保持原样不破坏已有脚本。</summary>
    private static string ReasonName(StopReason reason) => reason switch
    {
        StopReason.Clicked => "clicked",
        StopReason.Closed => "closed",
        StopReason.TargetClicked => "target-clicked",
        _ => reason.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// 等淡出动画跑完。
    ///
    /// <para>这里必须有<strong>硬超时</strong>：这个 CLI 是给 agent 调的，一个永不退出的进程
    /// 会把 agent 的整条工作流挂住，比画不出淡出严重得多。超时后直接放行。</para>
    /// </summary>
    private static void WaitForFade(OverlayForm form)
    {
        var deadline = Environment.TickCount64 + FadeDurationMs + 2000;

        while (form.IsFading && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        form.ForceFinishFade();
    }

    /// <summary>
    /// 把命令里的目标解析成一个全局物理像素坐标。
    ///
    /// <para><paramref name="element"/> 只在 <c>--element</c> 目标时非空，
    /// 供 --json 回显命中的元素。</para>
    /// <para><paramref name="targetRect"/> 是目标的全局物理像素包围盒（<c>--at</c> / <c>--mouse</c>
    /// 只有 1x1 的点），供 <c>--until-click</c> 判断点击落在了哪里。</para>
    /// <para><paramref name="targetWindow"/> 是目标所在的顶层窗口句柄（<c>--at</c> / <c>--rect</c> /
    /// <c>--mouse</c> 没有窗口，返回 <see cref="IntPtr.Zero"/>），供 <c>--flash</c> 闪任务栏按钮。</para>
    /// <para><paramref name="raised"/> 是 <c>--raise</c> 的结果：null 表示没有尝试置前
    /// （没开 --raise、--dry-run 或目标没有窗口），false 表示尝试了但被前台锁挡住。</para>
    /// <para><paramref name="preResolved"/> 为 true 表示目标矩形是父进程算好的（<c>start
    /// --element</c>），这里一次 UI Automation 都没有发。</para>
    /// <para><paramref name="truncated"/> 为 true 表示这次元素搜索撞上了超时或数量上限，
    /// 结果可能不完整（命中与否都算）。</para>
    /// </summary>
    private static Point ResolveTarget(
        OverlayManager manager, PointerCommand command,
        out ElementInfo? element, out Rectangle targetRect, out IntPtr targetWindow,
        out bool? raised, out bool preResolved, out bool truncated)
    {
        element = null;
        targetWindow = IntPtr.Zero;
        raised = null;
        preResolved = false;
        truncated = false;
        var target = command.Target;

        switch (target.Kind)
        {
            case TargetKind.At:
                targetRect = new Rectangle(target.At!.Value, new Size(1, 1));
                return target.At!.Value;

            case TargetKind.Mouse:
                if (!NativeMethods.GetCursorPos(out var cursor))
                {
                    throw new TargetNotFoundException(Lang.T(
                        "取不到当前鼠标位置", "Could not read the current mouse position."));
                }

                targetRect = new Rectangle(new Point(cursor.X, cursor.Y), new Size(1, 1));
                return new Point(cursor.X, cursor.Y);

            case TargetKind.Rect:
            {
                var r = target.Rect!.Value;
                targetRect = r;
                return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            }

            case TargetKind.Window:
            {
                var match = WindowFinder.Find(target.WindowQuery!)
                          ?? throw new TargetNotFoundException(Lang.T(
                              $"找不到与 '{target.WindowQuery}' 匹配的窗口。"
                              + $"当前可见的进程：{DescribeVisibleProcesses()}。"
                              + "用 agentpointer elements --app <名字> 查看窗口里可匹配的元素。",
                              $"No window matches '{target.WindowQuery}'. "
                              + $"Visible processes: {DescribeVisibleProcesses()}. "
                              + "Use agentpointer elements --app <name> to list what a window exposes."));
                // 置前失败不算致命——指着一个被挡住的窗口很难用，但不能因此让命令失败
                raised = TryRaiseTarget(match.Handle, command);

                targetRect = match.Bounds;
                targetWindow = match.Handle;

                // --anchor：窗口内的锚点。center 默认；title 取标题栏区（上缘 10%）的中点
                return command.Look.Anchor?.ToLowerInvariant() switch
                {
                    "title" => new Point(
                        match.Bounds.Left + match.Bounds.Width / 2,
                        match.Bounds.Top + Math.Max(8, match.Bounds.Height / 10)),
                    _ => new Point(
                        match.Bounds.Left + match.Bounds.Width / 2,
                        match.Bounds.Top + match.Bounds.Height / 2),
                };
            }

            case TargetKind.Element:
            {
                // start 的父进程可能已经把 UI Automation 跑完了（见 ResolveElementForChild）：
                // 直接用它留下的全局矩形和窗口句柄，子进程一次 UIA 调用都不用发。
                if (ResolvedTargetCodec.TryDecode(Environment.GetEnvironmentVariable, out var resolved))
                {
                    element = resolved.Element;
                    targetRect = new Rectangle(resolved.X, resolved.Y, resolved.Width, resolved.Height);
                    raised = resolved.Raised;
                    targetWindow = resolved.Window;
                    truncated = resolved.Truncated;
                    preResolved = true;
                    return new Point(
                        targetRect.Left + targetRect.Width / 2,
                        targetRect.Top + targetRect.Height / 2);
                }

                var window = ResolveElementWindow(command);

                // 与窗口目标一致：只在显式 --raise 时置前，置前失败不算致命
                raised = TryRaiseTarget(window.Handle, command);

                var found = ElementFinder.Find(window.Handle, target.ElementQuery!, target.Role, window.Title);
                element = found.Element;
                truncated = found.Truncated;

                // 元素包围盒走与 --rect 完全相同的渲染路径（取中心）
                var bounds = new Rectangle(element.X, element.Y, element.Width, element.Height);
                targetRect = bounds;
                targetWindow = window.Handle;
                return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            }

            default:
                throw new TargetNotFoundException(Lang.T("没有可用的目标。", "No usable target."));
        }
    }

    /// <summary>上游语义：exit 3 的消息要列出当前存在什么，帮 agent 自己修正。</summary>
    private static string DescribeVisibleProcesses() =>
        string.Join(Lang.ListSep, WindowFinder.Enumerate()
            .Select(w => w.ProcessName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)) is { Length: > 0 } list
            ? list
            : Lang.T("（无可见窗口）", "(no visible windows)");

    /// <summary>--display N 是 1-based（与上游一致），内部数组下标从 0 开始。</summary>
    private static int? ToZeroBased(int? display) => display is { } d ? d - 1 : null;

    /// <summary>主屏：包含全局原点 (0,0) 的那块屏；枚举顺序不保证，找不到就退回第一块。</summary>
    private static MonitorInfo PrimaryMonitor(IReadOnlyList<MonitorInfo> monitors)
    {
        var primary = monitors.FirstOrDefault(m => m.Bounds.Contains(0, 0)) ?? monitors.FirstOrDefault();
        return primary ?? throw new TargetNotFoundException(Lang.T(
            "没有可用的显示器。", "No usable display."));
    }

    // ---------------- stop ----------------

    private static int RunStop(PointerCommand command)
    {
        // --hook 供 Claude Code 的 UserPromptSubmit 钩子使用，必须绑定一个 owner，
        // 否则无从知道该收哪些箭头；--all 是会话级清理，与按 owner 收场互斥。
        // 参数错误必须在做任何事之前退 2，而不是静默什么都不做。
        if (command.StopHook && (command.OwnerPid is null || command.StopAll))
        {
            return Fail(ExitCode.BadInput, Lang.T(
                "stop --hook 需要 CLAUDE_PID / BIGARROW_OWNER_PID，且不能与 --all 同用。",
                "stop --hook needs CLAUDE_PID / BIGARROW_OWNER_PID and cannot be combined with --all."),
                command.Json);
        }

        // 注意：这里绝不读 stdin。钩子进程的 stdin 往往是宿主留下的、永不关闭的管道，
        // 一旦去读就会把用户的下一条提示词一起挂住。
        var signalled = StopSignal.Signal(command.OwnerPid, command.StopAll);

        if (command.Json)
        {
            Out(JsonSerializer.Serialize(new
            {
                ok = true,
                stopped = signalled,
                scope = command.StopAll ? "session" : command.OwnerPid is { } ? "owner" : "unowned",
                ownerPid = command.OwnerPid,
                hook = command.StopHook,
            }, JsonOptions));
        }
        else if (!signalled && !command.StopHook)
        {
            Err(Lang.T("没有正在显示的箭头", "No arrow is on screen"));
        }

        return ExitCode.Ok;
    }


    // ---------------- doctor ----------------

    private static int RunDoctor(PointerCommand command)
    {
        var secure = MonitorService.IsSecureOrLockedDesktop();
        var monitors = MonitorService.Enumerate();

        OverlayProbe? probe = null;
        if (!secure && monitors.Count > 0)
        {
            probe = RunOverlayProbe(monitors[0]);
        }

        var payload = new
        {
            ok = !secure && probe?.ConfigurationValid == true,
            version = AgentPointerInfo.Version,
            runtime = Environment.Version.ToString(),
            overlayAvailable = !secure && probe?.ConfigurationValid == true,
            coordinateSpace = "physical-pixels",
            secureOrLockedDesktop = secure,
            ownerPid = command.OwnerPid,
            ownerPidAlive = command.OwnerPid is { } pid && OwnerProcess.IsAlive(pid),
            virtualScreen = ToRect(MonitorService.VirtualScreen(monitors)),
            monitors = monitors.Select(m => new
            {
                index = m.Index + 1,
                device = m.DeviceName,
                bounds = ToRect(m.Bounds),
                workArea = ToRect(m.WorkArea),
                dpi = m.Dpi,
            }),
            overlayProbe = probe,
        };

        Out(JsonSerializer.Serialize(payload, JsonOptions));

        if (!command.Json && probe is not null)
        {
            Err(DescribeProbe(probe));
        }

        return secure ? ExitCode.PermissionMissing : probe?.ConfigurationValid == true ? ExitCode.Ok : ExitCode.InternalError;
    }

    /// <summary>
    /// 真的画一个箭头出来，然后把真实的 WM_NCHITTEST 发给它自己，
    /// 拿回系统实际看到的返回值。
    ///
    /// <para><b>能证明什么</b>：窗口的实际扩展样式位正确，且我们的 WndProc 在窗口真实范围内
    /// 确实返回了 HTTRANSPARENT（也就是 WM_NCHITTEST 那条消息真的送达了我们的处理器）。</para>
    /// <para><b>不能证明什么</b>：系统收到 HTTRANSPARENT 后是否真的把点击转交下层窗口——
    /// 那是 Win32 自身的行为，只能靠真人点一下来确认。</para>
    /// </summary>
    private static OverlayProbe? RunOverlayProbe(MonitorInfo monitor)
    {
        try
        {
            using var manager = new OverlayManager();
            var form = manager.WindowFor(monitor);

            // 画一个真箭头，让窗口处于真实的位置与尺寸
            var target = new Point(
                monitor.Bounds.Left + monitor.Bounds.Width / 2,
                monitor.Bounds.Top + monitor.Bounds.Height / 2);

            var probeCommand = new PointerCommand { Kind = CommandKind.Point };
            form.ShowPointer(
                target, monitor.Bounds, monitor.Dpi,
                new SignContent("probe", ArrowColor.Parse("red")), probeCommand);

            var rect = form.WindowRect;
            var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            var sign = form.SignCenterScreen;

            // 三个有语义的探点：目标本身必须穿透，牌子必须可点，窗口中心不预设期望
            var results = new[]
            {
                Probe(form, "target", target),
                Probe(form, "sign", sign),
                Probe(form, "center", center),
            };

            var bits = form.StyleBits();

            manager.HideAll();

            var transparentAtTarget = results.First(r => r.Label == "target").HitTest == NativeMethods.HTTRANSPARENT;
            var clickableAtSign = results.First(r => r.Label == "sign").HitTest == NativeMethods.HTCLIENT;

            return new OverlayProbe(
                MonitorIndex: monitor.Index + 1,
                RawExStyle: bits.Raw,
                StyleLayered: bits.Layered,
                StyleToolWindow: bits.ToolWindow,
                StyleNoActivate: bits.NoActivate,
                StyleTransparent: bits.Transparent,
                WindowRect: rect,
                HitTestResults: results,
                ConfigurationValid: transparentAtTarget && clickableAtSign
                    && bits.Layered && bits.NoActivate && bits.ToolWindow);
        }
        catch (Exception ex)
        {
            return new OverlayProbe(
                MonitorIndex: monitor.Index + 1,
                RawExStyle: 0,
                StyleLayered: false,
                StyleToolWindow: false,
                StyleNoActivate: false,
                StyleTransparent: false,
                WindowRect: Rectangle.Empty,
                HitTestResults: Array.Empty<HitTestPoint>(),
                ConfigurationValid: false,
                Error: ex.Message);
        }
    }

    private static string DescribeProbe(OverlayProbe probe)
    {
        if (probe.Error is not null)
        {
            return Lang.T($"覆盖层自检失败：{probe.Error}", $"Overlay probe failed: {probe.Error}");
        }

        var hitTests = string.Join(", ", probe.HitTestResults.Select(r => $"{r.Label}({r.X},{r.Y})"));

        var lines = new List<string>
        {
            Lang.T(
                $"覆盖层自检（显示器 #{probe.MonitorIndex}）",
                $"Overlay probe (display #{probe.MonitorIndex})"),
            Lang.T(
                $"  窗口区域  {probe.WindowRect.X},{probe.WindowRect.Y} " +
                $"{probe.WindowRect.Width}x{probe.WindowRect.Height}（只覆盖内容包围盒）",
                $"  window rect  {probe.WindowRect.X},{probe.WindowRect.Y} " +
                $"{probe.WindowRect.Width}x{probe.WindowRect.Height} (content bounds only)"),
            Lang.T($"  原始 EXSTYLE  0x{probe.RawExStyle:X}", $"  raw EXSTYLE  0x{probe.RawExStyle:X}"),
            Lang.T(
                $"  扩展样式  LAYERED={probe.StyleLayered}  TOOLWINDOW={probe.StyleToolWindow}  " +
                $"NOACTIVATE={probe.StyleNoActivate}  TRANSPARENT={probe.StyleTransparent}",
                $"  extended styles  LAYERED={probe.StyleLayered}  TOOLWINDOW={probe.StyleToolWindow}  " +
                $"NOACTIVATE={probe.StyleNoActivate}  TRANSPARENT={probe.StyleTransparent}"),
            Lang.T(
                $"  命中测试  {string.Join(", ", probe.HitTestResults.Select(r => $"{r.Label}({r.X},{r.Y})→{r.HitTest}"))}",
                $"  hit test  {string.Join(", ", probe.HitTestResults.Select(r => $"{r.Label}({r.X},{r.Y})->{r.HitTest}"))}"),
            Lang.T(
                $"  样式与命中测试  {(probe.ConfigurationValid ? "通过" : "失败")}",
                $"  styles and hit test  {(probe.ConfigurationValid ? "pass" : "fail")}"),
            Lang.T(
                $"  可点区域  LAYERED={probe.StyleLayered}  NOACTIVATE={probe.StyleNoActivate}  " +
                "牌子→HTCLIENT、目标→HTTRANSPARENT（点牌子收场，点目标照常送达）",
                $"  clickable area  LAYERED={probe.StyleLayered}  NOACTIVATE={probe.StyleNoActivate}  " +
                "sign -> HTCLIENT, target -> HTTRANSPARENT (a click on the sign dismisses, the target stays clickable)"),
            Lang.T(
                "  跨进程点击送达  未测试；需要点击被箭头覆盖的测试按钮确认。",
                "  cross-process click delivery  not tested; click a test button covered by the arrow to confirm."),
        };

        _ = hitTests;

        return string.Join("\n", lines);
    }

    private static HitTestPoint Probe(OverlayForm form, string label, Point point) =>
        new(label, point.X, point.Y, form.ProbeHitTest(point));

    private sealed record HitTestPoint(string Label, int X, int Y, int HitTest);

    private sealed record OverlayProbe(
        int MonitorIndex,
        long RawExStyle,
        bool StyleLayered,
        bool StyleToolWindow,
        bool StyleNoActivate,
        bool StyleTransparent,
        Rectangle WindowRect,
        HitTestPoint[] HitTestResults,
        bool ConfigurationValid,
        string ClickDelivery = "not-tested",
        string? Error = null);

    // ---------------- elements ----------------

    /// <summary>列出窗口里可被 <c>--element</c> 匹配的元素，让 agent 自己修正查询串。</summary>
    private static int RunElements(PointerCommand command)
    {
        var window = ResolveElementWindow(command);

        var raised = TryRaiseTarget(window.Handle, command);

        // elements 要的是完整清单，多给两秒；等不到就如实报 truncated，别让 agent
        // 以为手里的就是全部。
        var scan = ElementFinder.CollectDetailed(window.Handle, timeout: ElementFinder.ElementsTimeout);
        var all = scan.Elements;

        var roleFilter = string.IsNullOrWhiteSpace(command.Target.Role)
            ? null
            : ElementMatcher.NormalizeRole(command.Target.Role);
        var match = command.ElementsMatch?.Trim();

        var elements = all
            .Where(e => roleFilter is null ||
                        string.Equals(ElementMatcher.NormalizeRole(e.Role), roleFilter, StringComparison.Ordinal))
            .Where(e => string.IsNullOrEmpty(match) ||
                        e.Name.Contains(match, StringComparison.OrdinalIgnoreCase) ||
                        (e.AutomationId?.Contains(match, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (command.Json)
        {
            Out(JsonSerializer.Serialize(new
            {
                ok = true,
                raised = raised,
                window = new { title = window.Title, process = window.ProcessName },
                count = elements.Count,
                truncated = scan.Truncated,
                elements = elements.Select(e => new
                {
                    name = e.Name,
                    role = e.Role,
                    automationId = e.AutomationId,
                    x = e.X,
                    y = e.Y,
                    width = e.Width,
                    height = e.Height,
                    enabled = e.Enabled,
                }),
            }, JsonOptions));

            return ExitCode.Ok;
        }

        if (elements.Count == 0)
        {
            Err(all.Count == 0
                ? Lang.T(
                    $"窗口 \"{window.Title}\" 没有暴露可访问性元素。",
                    $"Window \"{window.Title}\" exposes no accessible elements.")
                : Lang.T("没有匹配的元素。", "No element matches."));
            return ExitCode.Ok;
        }

        foreach (var e in elements)
        {
            Out($"{e.Role,-12} \"{e.Name}\"  {e.X},{e.Y},{e.Width},{e.Height}");
        }

        return ExitCode.Ok;
    }

    /// <summary>
    /// 元素目标的窗口作用域：给了 <c>--app</c> / <c>--window</c> 就按它找
    /// （找不到即退出码 3），否则退回当前前台窗口。
    /// </summary>
    private static WindowMatch ResolveElementWindow(PointerCommand command)
    {
        var query = command.Target.WindowQuery;

        if (!string.IsNullOrWhiteSpace(query))
        {
            return WindowFinder.Find(query!)
                ?? throw new TargetNotFoundException(Lang.T(
                    $"找不到与 '{query}' 匹配的窗口。当前可见的进程：{DescribeVisibleProcesses()}。"
                    + "用 agentpointer elements --app <名字> 查看可匹配的元素。",
                    $"No window matches '{query}'. Visible processes: {DescribeVisibleProcesses()}. "
                    + "Use agentpointer elements --app <name> to list what a window exposes."));
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            throw new TargetNotFoundException(Lang.T(
                "取不到前台窗口，请用 --app / --window 指定窗口。",
                "Could not read the foreground window; pass --app / --window."));
        }

        // 前台窗口若在枚举列表里，就能带上进程名和标题；否则退化成只有一个句柄
        return WindowFinder.Enumerate().FirstOrDefault(w => w.Handle == foreground)
               ?? new WindowMatch(foreground, null, string.Empty, Rectangle.Empty);
    }

    /// <summary>--json 里回显命中的元素；非元素目标时为 null，会被序列化器略过。</summary>
    private static object? ElementJson(ElementInfo? element) => element is null
        ? null
        : new
        {
            name = element.Name,
            role = element.Role,
            automationId = element.AutomationId,
            x = element.X,
            y = element.Y,
            width = element.Width,
            height = element.Height,
        };

    // ---------------- 输出 ----------------

    private static int Fail(int exitCode, string message, bool json = false)
    {
        if (json) Out(JsonSerializer.Serialize(new { ok = false, exitCode, error = message }, JsonOptions));
        Err(message);
        return exitCode;
    }

    private static int Report(PointerCommand command, Point target, string what, int monitorIndex, bool? truncated)
    {
        if (!command.Json)
        {
            Out(Lang.T(
                $"{what}：({target.X}, {target.Y})，显示器 #{monitorIndex}",
                $"{what}: ({target.X}, {target.Y}), display #{monitorIndex}"));
            return ExitCode.Ok;
        }

        Out(JsonSerializer.Serialize(new
        {
            ok = true,
            dryRun = true,
            wouldPointAt = new { x = target.X, y = target.Y },
            monitor = monitorIndex,
            coordinateSpace = "physical-pixels",
            ineffective = IneffectiveNotice(command),
            truncated = truncated,
        }, JsonOptions));

        return ExitCode.Ok;
    }

    /// <summary>
    /// 控制台子系统使用宿主编码；无控制台的后台子进程使用 UTF-8。
    /// </summary>
    private static void Out(string text)
    {
        if (Environment.GetEnvironmentVariable(ShownSignal.ResultEnvironmentVariable) is { } startupResult)
        {
            File.WriteAllText(startupResult, text, new UTF8Encoding(false));
            return;
        }
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }

    private static void Err(string text)
    {
        Console.Error.WriteLine($"agentpointer: {text}");
        Console.Error.Flush();
    }

    private static object ToRect(Rectangle r) => new
    {
        x = r.X,
        y = r.Y,
        w = r.Width,
        h = r.Height,
    };

    /// <summary>
    /// 颜色统一走 <see cref="ArrowColor.Parse"/>（上游 presets + Rec.709）。
    /// 解析层已校验过取值，这里只对异常输入回退默认红——渲染永远不该因颜色崩掉。
    /// </summary>
    internal static ArrowColor SafeParseColor(string? value)
    {
        try
        {
            return ArrowColor.Parse(value);
        }
        catch (ArgumentException)
        {
            return ArrowColor.Parse("red");
        }
    }
}

/// <summary>目标找不到，映射为退出码 3。</summary>
internal sealed class TargetNotFoundException : Exception
{
    public TargetNotFoundException(string message) : base(message) { }
}

internal static class AgentPointerInfo
{
    public const string Version = "0.2.0";
}

/// <summary>owner agent 进程是否还活着。</summary>
internal static class OwnerProcess
{
    public static bool IsAlive(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE, false, pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            return NativeMethods.WaitForSingleObject(handle, 0) == NativeMethods.WAIT_TIMEOUT;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
