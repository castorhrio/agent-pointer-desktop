using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static class DesktopChecks
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    public static int Run(string executable, string preview)
    {
        executable = Path.GetFullPath(executable);
        preview = Path.GetFullPath(preview);
        // 不接管或关闭用户已经打开的桌面实例。
        if (EventWaitHandle.TryOpenExisting(@"Local\AgentPointer.Desktop.v1.Show", out var existing))
        {
            existing.Dispose();
            Console.Error.WriteLine("Close your existing Agent Pointer instance before running desktop checks.");
            return 2;
        }
        var originalForeground = GetForegroundWindow();
        GetCursorPos(out var originalCursor);
        using var desktop = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        try
        {
            AutomationElement? window = null;
            WaitFor(() =>
            {
                window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, desktop.Id));
                return window is not null && !window.Current.IsOffscreen;
            }, "desktop opens visibly on normal launch");
            var root = window!;
            var start = Find(root, "StartDemo");
            WaitFor(() => start.Current.IsEnabled, "bundled CLI is available to the desktop");
            var install = Find(root, "InstallSkills");
            Check(install.Current.IsEnabled, "agent setup action is available");
            Check(!string.IsNullOrWhiteSpace(Find(root, "ClaudeSkillStatus").Current.Name), "Claude skill status is shown");
            Check(!string.IsNullOrWhiteSpace(Find(root, "CodexSkillStatus").Current.Name), "Codex skill status is shown");

            // 首屏截图不包含其他应用。
            Thread.Sleep(400); // 等 Windows 首次显示窗口的淡入动画完成。
            Capture(root, preview);
            ((InvokePattern)start.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var target = Find(root, "DemoTarget");
            WaitFor(() => target.Current.IsEnabled, "demo arrow is ready before enabling the confirmation button");
            Check(!install.Current.IsEnabled, "setup is disabled while the demo is active");
            var bounds = target.Current.BoundingRectangle;
            var point = new Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            GetWindowThreadProcessId(WindowFromPoint(point), out var receiver);
            Check(receiver == desktop.Id, "demo click is confined to the desktop test window");
            SetCursorPos(point.X, point.Y);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            WaitFor(() => !target.Current.IsEnabled && start.Current.IsEnabled, "confirmation completes demo and restores actions");
            Check(Find(root, "DemoStatus").Current.Name.Contains("体验完成") ||
                  Find(root, "DemoStatus").Current.Name.StartsWith("Done!"), "demo reports a completed handoff");

            ((WindowPattern)root.GetCurrentPattern(WindowPattern.Pattern)).Close();
            WaitFor(() => root.Current.IsOffscreen, "window close hides to tray");
            Check(!desktop.HasExited, "desktop remains alive in tray");
            using var second = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true })!;
            Check(second.WaitForExit(5000), "second launch exits instead of creating another tray app");
            WaitFor(() => !root.Current.IsOffscreen, "second launch reopens the original desktop window");
            Console.WriteLine("PASS: visible desktop launch, real component and skill states, demo startup, actual button click, completion, close-to-tray, single-instance reopen.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
        finally
        {
            if (!desktop.HasExited) { desktop.Kill(); desktop.WaitForExit(3000); }
            SetCursorPos(originalCursor.X, originalCursor.Y);
            SetForegroundWindow(originalForeground);
        }
    }

    private static AutomationElement Find(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
        ?? throw new Exception("UI element missing: " + id);

    private static void Capture(AutomationElement root, string path)
    {
        var rect = root.Current.BoundingRectangle;
        using var bitmap = new Bitmap((int)rect.Width, (int)rect.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { Check(PrintWindow(new IntPtr(root.Current.NativeWindowHandle), dc, 2), "capture only the desktop app window"); }
            finally { graphics.ReleaseHdc(dc); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void WaitFor(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 8000) Thread.Sleep(40);
        Check(condition(), description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }
}
