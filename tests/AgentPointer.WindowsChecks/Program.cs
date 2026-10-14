using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    [STAThread]
    private static int Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args.Length == 3 && args[0] == "--installer") return InstallerChecks.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--tray-lifecycle") return DesktopChecks.CheckTrayLifecycle(args[1]);
        if (args.Length == 3 && args[0] == "--desktop") return DesktopChecks.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--target") return RunTarget(args[1]);
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: WindowsChecks <agentpointer.exe> <preview.png>");
            return 2;
        }

        var cli = Path.GetFullPath(args[0]);
        var preview = Path.GetFullPath(args[1]);
        var marker = Path.Combine(Path.GetTempPath(), "AgentPointer.Check." + Guid.NewGuid().ToString("N"));
        var overlays = new List<int>();
        GetCursorPos(out var originalCursor);
        var originalForeground = GetForegroundWindow();
        using var target = StartSelf("--target", marker);
        try
        {
            WaitFor(() => File.Exists(marker), "target window readiness");
            var bounds = JsonNode.Parse(File.ReadAllText(marker))!;
            var x = bounds["x"]!.GetValue<int>();
            var y = bounds["y"]!.GetValue<int>();
            var width = bounds["width"]!.GetValue<int>();
            var height = bounds["height"]!.GetValue<int>();
            WaitFor(() =>
            {
                GetWindowThreadProcessId(WindowFromPoint(new Point(x + 30, y + 30)), out var receiver);
                return receiver == target.Id;
            }, "test window is actually visible above other applications");
            WaitFor(() =>
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
                return foregroundPid == target.Id;
            }, "test window has finished activating before focus preservation is measured");
            var targetPoint = $"{x + width - 130},{y + height / 2}";
            var foreground = GetForegroundWindow();
            var first = Invoke(cli, Environment.ProcessId, "start", "--at", targetPoint,
                "--from", "left", "--text", "点击 Allow，继续安装", "--duration", "30", "--json");
            Check(first.Code == 0 && first.Milliseconds < 3000, "start returns promptly including pipe EOF");
            var firstPid = first.Json["pid"]!.GetValue<int>();
            overlays.Add(firstPid);
            Check(IsAlive(firstPid), "background overlay survives start");
            Check(GetForegroundWindow() == foreground, "start preserves focus");

            // 捕获自建测试窗口中的真实覆盖层，并找到说明卡片的不透明像素。
            using (var capture = new Bitmap(width, height))
            {
                using (var graphics = Graphics.FromImage(capture))
                    graphics.CopyFromScreen(x, y, 0, 0, capture.Size);
                Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                capture.Save(preview, System.Drawing.Imaging.ImageFormat.Png);
                Point? opaque = null;
                var blackPixels = 0;
                for (var py = 20; py < height - 20; py++)
                for (var px = 20; px < width - 20; px++)
                {
                    var color = capture.GetPixel(px, py);
                    if (color.R < 8 && color.G < 8 && color.B < 8) blackPixels++;
                }
                Check(blackPixels < 1500, "transparent areas do not become an opaque black rectangle");
                for (var py = 20; py < height - 20 && opaque is null; py++)
                for (var px = 20; px < width - 20; px++)
                {
                    var color = capture.GetPixel(px, py);
                    if (color.R == 28 && color.G == 33 && color.B == 44)
                    {
                        opaque = new Point(x + px, y + py);
                        break;
                    }
                }
                Check(opaque is not null, "opaque instruction card was rendered");
                var point = opaque!.Value;
                GetWindowThreadProcessId(WindowFromPoint(point), out var receiver);
                Check(receiver == target.Id, "opaque overlay pixel routes to the separate target process");

                // 点击说明卡片只让箭头消失，点击本身不得落到下层按钮。
                // 先悬停：覆盖层每 30 ms 轮询一次光标，悬停后才取消 WS_EX_TRANSPARENT（真人点击前必然先移过去）
                SetCursorPos(point.X, point.Y);
                Thread.Sleep(200);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                WaitFor(() => !IsAlive(firstPid), "clicking the sign dismisses the arrow");
                Thread.Sleep(250);
                Check(!File.Exists(marker + ".clicked"), "sign click does not reach the underlying button");
            }

            // 同一目标点上再起一支箭头；点击卡片与箭杆之外应直达按钮。
            var clickable = Invoke(cli, Environment.ProcessId, "start", "--at", targetPoint,
                "--from", "left", "--duration", "30", "--no-animation", "--json");
            Check(clickable.Code == 0, "click-through fixture starts");
            var clickablePid = clickable.Json["pid"]!.GetValue<int>();
            overlays.Add(clickablePid);
            SetCursorPos(x + width - 60, y + height / 2);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            WaitFor(() => File.Exists(marker + ".clicked"), "real mouse input reaches underlying button");
            Check(IsAlive(clickablePid), "clicking through does not dismiss the arrow");

            var second = Invoke(cli, target.Id, "start", "--at", targetPoint,
                "--duration", "30", "--no-animation", "--json");
            Check(second.Code == 0, "second owner starts");
            var secondPid = second.Json["pid"]!.GetValue<int>();
            overlays.Add(secondPid);
            Check(!string.IsNullOrWhiteSpace(second.Json["text"]!.GetValue<string>()), "missing text gets a meaningful default");
            Check(Invoke(cli, Environment.ProcessId, "stop", "--json").Code == 0, "scoped stop succeeds");
            WaitFor(() => !IsAlive(clickablePid), "scoped overlay stops");
            Check(IsAlive(secondPid), "other owner's overlay remains");
            Check(Invoke(cli, target.Id, "stop", "--hook", "--json").Code == 0, "scoped hook succeeds");
            WaitFor(() => !IsAlive(secondPid), "hook stops scoped overlay");
            Check(Invoke(cli, null, "stop", "--hook", "--json").Code == 2, "ownerless hook is rejected");

            var doctor = Invoke(cli, null, "doctor", "--json");
            Check(doctor.Code == 0, "doctor styles pass");
            var monitors = doctor.Json["monitors"]!.AsArray();
            for (var i = 0; i < monitors.Count; i++)
            {
                Check(monitors[i]!["index"]!.GetValue<int>() == i + 1, "doctor uses one-based numbering");
                var local = Invoke(cli, null, "point", "--at", "50,50", "--display", (i + 1).ToString(), "--dry-run", "--json");
                Check(local.Code == 0 && local.Json["monitor"]!.GetValue<int>() == i + 1, "display selection after conversion");
                Check(local.Json["wouldPointAt"]!["x"]!.GetValue<int>() == monitors[i]!["bounds"]!["x"]!.GetValue<int>() + 50,
                    "display local coordinate converts to global");
            }
            Check(Invoke(cli, null, "point", "--at", "99999,99999", "--dry-run", "--json").Code == 3, "off-screen dry-run fails");
            Check(Invoke(cli, null, "start", "--at", "99999,99999", "--json").Code == 3, "start propagates target failure");
            Check(Invoke(cli, null, "elements", "--json").Code == 5, "elements is unsupported, not target-not-found");
            Check(Invoke(cli, null, "point", "--element", "Allow", "--json").Code == 5, "element targeting is unsupported");

            var owned = Invoke(cli, target.Id, "start", "--at", targetPoint, "--duration", "30", "--json");
            Check(owned.Code == 0, "owner-exit fixture starts");
            var ownedPid = owned.Json["pid"]!.GetValue<int>();
            overlays.Add(ownedPid);
            target.Kill();
            target.WaitForExit(2000);
            WaitFor(() => !IsAlive(ownedPid), "owner exit closes overlay");
            Console.WriteLine($"PASS: cross-process mouse delivery, startup EOF ({first.Milliseconds} ms), owner/stop/hook isolation, display conversion, exit codes, default label. Preview: {preview}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            foreach (var pid in overlays)
            {
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(); }
                catch (ArgumentException) { }
            }
            if (!target.HasExited) { target.Kill(); target.WaitForExit(2000); }
            foreach (var file in new[] { marker, marker + ".clicked" }) File.Delete(file);
            SetCursorPos(originalCursor.X, originalCursor.Y);
            SetForegroundWindow(originalForeground);
        }
    }

    private static int RunTarget(string marker)
    {
        using var form = new Form
        {
            Text = "AgentPointer click-through test", TopMost = true, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(120, 120), ClientSize = new Size(820, 440),
        };
        using var button = new Button
        {
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 248, 252), FlatStyle = FlatStyle.Flat,
            Text = "Allow", TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 90, 0),
            Font = new Font("Segoe UI", 16, FontStyle.Regular), UseVisualStyleBackColor = false,
        };
        button.Click += (_, _) => File.WriteAllText(marker + ".clicked", "clicked", Encoding.UTF8);
        form.Controls.Add(button);
        form.Shown += (_, _) =>
        {
            form.Activate();
            form.Refresh();
            var bounds = form.RectangleToScreen(form.ClientRectangle);
            File.WriteAllText(marker, $$"""{"x":{{bounds.X}},"y":{{bounds.Y}},"width":{{bounds.Width}},"height":{{bounds.Height}}}""", Encoding.UTF8);
        };
        System.Windows.Forms.Application.Run(form);
        return 0;
    }

    private static Process StartSelf(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    private sealed record Result(int Code, long Milliseconds, JsonNode Json);

    private static Result Invoke(string cli, int? owner, params string[] args)
    {
        var info = new ProcessStartInfo(cli)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment.Remove("CLAUDE_PID");
        info.Environment.Remove("BIGARROW_OWNER_PID");
        if (owner is { } pid) info.Environment["BIGARROW_OWNER_PID"] = pid.ToString();
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        var output = new StringBuilder();
        using var outputDone = new ManualResetEvent(false);
        using var errorDone = new ManualResetEvent(false);
        process.OutputDataReceived += (_, e) => { if (e.Data is null) outputDone.Set(); else output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) errorDone.Set(); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(7000)) { process.Kill(); throw new Exception("CLI timed out"); }
        Check(outputDone.WaitOne(1000) && errorDone.WaitOne(1000), "CLI does not retain caller pipes");
        return new Result(process.ExitCode, watch.ElapsedMilliseconds, JsonNode.Parse(output.ToString())!);
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void WaitFor(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 5000) Thread.Sleep(25);
        Check(condition(), description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
    }
}
