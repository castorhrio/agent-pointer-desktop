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
            Out(ArgParser.HelpText);
            return ExitCode.Ok;
        }

        if (parse.VersionRequested)
        {
            Out(AgentPointerInfo.Version);
            return ExitCode.Ok;
        }

        if (!parse.Ok)
        {
            return Fail(parse.ExitCode, parse.Error ?? "未知错误", args.Contains("--json"));
        }

        var command = parse.Command!;

        try
        {
            if (command.KeepAlive && (command.Kind != CommandKind.Start || ShownSignal.EventName is null))
            {
                return Fail(ExitCode.BadInput, "--keep-alive 是后台启动的内部参数。", command.Json);
            }

            return command.Kind switch
            {
                CommandKind.Doctor => RunDoctor(command),
                CommandKind.Elements => RunElements(command),
                CommandKind.Stop => RunStop(command),
                CommandKind.Start when !command.KeepAlive && !command.DryRun => RunStart(command, args),
                CommandKind.Point or CommandKind.Start => RunPoint(command),
                CommandKind.InstallSkill => SkillInstaller.Install(command.Json, command.DryRun),
                _ => Fail(ExitCode.BadInput, "不支持的子命令"),
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
            return Fail(ExitCode.InternalError, $"内部错误：{ex.Message}", command.Json);
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
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
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
                    return Fail(ExitCode.InternalError, "后台箭头未能在 5 秒内显示。", command.Json);
                }

                if (!NativeMethods.GetExitCodeProcess(process.hProcess, out var code)) throw new Win32Exception();
                var error = "后台箭头在显示前退出。";
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
                if (notices.Length > 0) Err("以下参数暂不生效：" + string.Join("、", notices));
            }
            Out(command.Json ? report : $"箭头已显示（PID {process.dwProcessId}）。");
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

    private static int RunPoint(PointerCommand command)
    {
        if (MonitorService.IsSecureOrLockedDesktop())
        {
            return Fail(ExitCode.PermissionMissing,
                "当前处于锁屏或 UAC 安全桌面，无法在屏幕最上层绘制覆盖层。", command.Json);
        }

        using var manager = new OverlayManager();

        var target = ResolveTarget(manager, command, out var element, out var targetRect);

        var display = ToZeroBased(command.Target.DisplayIndex);
        if (display is { } index && !MonitorService.TryFromIndex(index, manager.Monitors, out _))
        {
            return Fail(ExitCode.BadInput, $"显示器序号 {command.Target.DisplayIndex} 不存在（共 {manager.Monitors.Count} 块，从 1 开始）", command.Json);
        }

        // 只有输入坐标需要从显示器局部换算；窗口和鼠标已经是全局坐标。
        manager.TryToGlobal(command.Target.Kind is TargetKind.At or TargetKind.Rect ? display : null, target, out var global);
        var monitorForTarget = manager.MonitorFor(global);
        if (display is { } selected && monitorForTarget.Index != selected)
        {
            throw new TargetNotFoundException($"目标不在显示器 #{selected + 1} 内。");
        }

        if (command.DryRun)
        {
            return Report(command, global, "dry-run：本来会指向这里", monitorForTarget.Index + 1);
        }

        var notices = IneffectiveNotice(command);
        if (!command.Json && notices.Length > 0) Err("以下参数暂不生效：" + string.Join("、", notices));

        var sign = new SignContent(command.Text, SafeParseColor(command.Look.Color));
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

            if (form.IsEntering) throw new InvalidOperationException("箭头入场动画未完成。");
            Out(JsonSerializer.Serialize(new
            {
                ok = true,
                started = true,
                pid = Environment.ProcessId,
                pointed = new { x = global.X, y = global.Y },
                element = ElementJson(element),
                text = sign.DisplayText,
                monitor = monitorForTarget.Index + 1,
                ineffective = notices,
            }, JsonOptions));
            ShownSignal.Signal();
            Environment.SetEnvironmentVariable(ShownSignal.ResultEnvironmentVariable, null);
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

        watch?.Dispose();

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
                pointed = new { x = global.X, y = global.Y },
                element = ElementJson(element),
                monitor = monitorForTarget.Index + 1,
                endedBy = ReasonName(reason),
                text = sign.DisplayText,
                ineffective = IneffectiveNotice(command),
            }, JsonOptions));
        }

        return ExitCode.Ok;
    }

    /// <summary>上游承诺 --json 标注"接受但未生效"的参数，不让 agent 静默踩空。</summary>
    private static string[] IneffectiveNotice(PointerCommand command)
    {
        var notices = new List<string>(command.Behavior.IneffectiveFlags);

        if (command.Look.Shape is "zigzag" or "spiral")
        {
            notices.Add($"--shape {command.Look.Shape}（尚未实现，当前按 bend 渲染）");
        }

        if (command.Look.Style is "ring" or "box")
        {
            notices.Add($"--style {command.Look.Style}（尚未实现，当前按 arrow 渲染）");
        }

        if (command.Look.Anchor is { } anchor && anchor != "center" && anchor != "title")
        {
            notices.Add($"--anchor {anchor}（当前按 center 定位）");
        }

        return notices.Distinct().ToArray();
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
    /// </summary>
    private static Point ResolveTarget(
        OverlayManager manager, PointerCommand command, out ElementInfo? element, out Rectangle targetRect)
    {
        element = null;
        var target = command.Target;

        switch (target.Kind)
        {
            case TargetKind.At:
                targetRect = new Rectangle(target.At!.Value, new Size(1, 1));
                return target.At!.Value;

            case TargetKind.Mouse:
                if (!NativeMethods.GetCursorPos(out var cursor))
                {
                    throw new TargetNotFoundException("取不到当前鼠标位置");
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
                          ?? throw new TargetNotFoundException(
                              $"找不到与 '{target.WindowQuery}' 匹配的窗口。"
                              + $"当前可见的进程：{DescribeVisibleProcesses()}");
                if (command.Behavior.Raise && !command.DryRun)
                {
                    // 置前失败不算致命——指着一个被挡住的窗口很难用，但不能因此让命令失败
                    WindowFinder.TryRaise(match.Handle);
                }

                targetRect = match.Bounds;

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
                var window = ResolveElementWindow(command);

                // 与窗口目标一致：只在显式 --raise 时置前，置前失败不算致命
                if (command.Behavior.Raise && !command.DryRun)
                {
                    WindowFinder.TryRaise(window.Handle);
                }

                element = ElementFinder.Find(window.Handle, target.ElementQuery!, target.Role, window.Title);

                // 元素包围盒走与 --rect 完全相同的渲染路径（取中心）
                var bounds = new Rectangle(element.X, element.Y, element.Width, element.Height);
                targetRect = bounds;
                return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            }

            default:
                throw new TargetNotFoundException("没有可用的目标");
        }
    }

    /// <summary>上游语义：exit 3 的消息要列出当前存在什么，帮 agent 自己修正。</summary>
    private static string DescribeVisibleProcesses() =>
        string.Join("、", WindowFinder.Enumerate()
            .Select(w => w.ProcessName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)) is { Length: > 0 } list
            ? list
            : "（无可见窗口）";

    /// <summary>--display N 是 1-based（与上游一致），内部数组下标从 0 开始。</summary>
    private static int? ToZeroBased(int? display) => display is { } d ? d - 1 : null;

    // ---------------- stop ----------------

    private static int RunStop(PointerCommand command)
    {
        // Claude Code 的 UserPromptSubmit 钩子：绝不允许阻塞或污染用户的下一条提示词。
        // 所以这里读干 stdin、吞掉一切错误、不写 stdout，并且永远返回 0。
        if (command.StopHook)
        {
            DrainRedirectedStdin();

            try
            {
                StopSignal.Signal(command.OwnerPid, command.StopAll);
            }
            catch (Exception)
            {
                // 钩子模式下任何失败都必须静默：用户的下一条提示词比收箭头重要
            }

            return ExitCode.Ok;
        }

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
            Err("没有正在显示的箭头");
        }

        return ExitCode.Ok;
    }

    /// <summary>
    /// 钩子模式下 stdin 通常是 Claude Code 写进来的一段 JSON。
    /// 读掉它（有超时上限）并丢弃：不读的话管道可能被写满，钩子反而会卡住。
    /// stdin 没被重定向时直接返回，绝不阻塞在控制台输入上。
    /// </summary>
    private static void DrainRedirectedStdin()
    {
        try
        {
            if (!Console.IsInputRedirected) return;

            var buffer = new char[4096];
            var deadline = Environment.TickCount64 + 250;
            while (Environment.TickCount64 < deadline)
            {
                var read = Console.In.Read(buffer, 0, buffer.Length);
                if (read <= 0) return;
            }
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
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
            return $"覆盖层自检失败：{probe.Error}";
        }

        var lines = new List<string>
        {
            $"覆盖层自检（显示器 #{probe.MonitorIndex}）",
            $"  窗口区域  {probe.WindowRect.X},{probe.WindowRect.Y} " +
            $"{probe.WindowRect.Width}x{probe.WindowRect.Height}（只覆盖内容包围盒）",
            $"  原始 EXSTYLE  0x{probe.RawExStyle:X}",
            $"  扩展样式  LAYERED={probe.StyleLayered}  TOOLWINDOW={probe.StyleToolWindow}  " +
            $"NOACTIVATE={probe.StyleNoActivate}  TRANSPARENT={probe.StyleTransparent}",
            $"  命中测试  {string.Join(", ", probe.HitTestResults.Select(r => $"{r.Label}({r.X},{r.Y})→{r.HitTest}"))}",
            $"  样式与命中测试  {(probe.ConfigurationValid ? "通过" : "失败")}",
            $"  可点区域  LAYERED={probe.StyleLayered}  NOACTIVATE={probe.StyleNoActivate}  " +
            "牌子→HTCLIENT、目标→HTTRANSPARENT（点牌子收场，点目标照常送达）",
            "  跨进程点击送达  未测试；需要点击被箭头覆盖的测试按钮确认。",
        };

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

        if (command.Behavior.Raise && !command.DryRun)
        {
            WindowFinder.TryRaise(window.Handle);
        }

        var all = ElementFinder.Collect(window.Handle);

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
                window = new { title = window.Title, process = window.ProcessName },
                count = elements.Count,
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
                ? $"窗口 \"{window.Title}\" 没有暴露可访问性元素。"
                : "没有匹配的元素。");
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
                ?? throw new TargetNotFoundException(
                    $"找不到与 '{query}' 匹配的窗口。当前可见的进程：{DescribeVisibleProcesses()}");
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            throw new TargetNotFoundException("取不到前台窗口，请用 --app / --window 指定窗口。");
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

    private static int Report(PointerCommand command, Point target, string what, int monitorIndex)
    {
        if (!command.Json)
        {
            Out($"{what}：({target.X}, {target.Y})，显示器 #{monitorIndex}");
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
    public const string Version = "0.1.1";
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
