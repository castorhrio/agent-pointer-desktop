using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using AgentPointer.Tray;

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
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rectangle rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    public static int CheckTrayLifecycle(string executable)
    {
        System.Windows.Application? app = null;
        var createdIcons = new List<System.Windows.Forms.NotifyIcon>();
        try
        {
            var assembly = System.Reflection.Assembly.LoadFrom(Path.ChangeExtension(Path.GetFullPath(executable), ".dll"));
            app = (System.Windows.Application)Activator.CreateInstance(assembly.GetType("AgentPointer.Tray.App", throwOnError: true)!)!;
            app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
            var window = (System.Windows.Window)Activator.CreateInstance(assembly.GetType("AgentPointer.Tray.MainWindow", throwOnError: true)!)!;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                // WPF 主题/视觉树重载可以再次发出 Loaded；验证生产事件处理器只注册一个真实图标。
                window.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent));
                var icon = (System.Windows.Forms.NotifyIcon)window.GetType().GetField("_notifyIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                if (!createdIcons.Contains(icon)) createdIcons.Add(icon);
                Check(createdIcons.Count(item => item.Visible) == 1, "repeated Loaded events do not retain another visible NotifyIcon (cycle " + cycle + ")");
            }
            Console.WriteLine("PASS: repeated WPF Loaded events retain exactly one visible NotifyIcon.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
        finally
        {
            foreach (var icon in createdIcons) icon.Dispose();
            foreach (var icon in TrayIcons(Environment.ProcessId)) SendMessage(icon.Window, 0x0002, IntPtr.Zero, IntPtr.Zero);
            app?.Shutdown();
        }
    }

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
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentPointer", "settings.json");
        var savedSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        Process? desktop = null;
        try
        {
            Check(Languages.Resolve(CultureInfo.GetCultureInfo("zh-TW")) == "zh-CN", "Chinese variants use Chinese");
            Check(Languages.Resolve(CultureInfo.GetCultureInfo("en-GB")) == "en-US", "English variants use English");
            Check(Languages.Resolve(CultureInfo.GetCultureInfo("ja-JP")) == "en-US", "unsupported languages fall back to English");
            var systemLanguage = Languages.ResolveSystem();
            var originalUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(systemLanguage == "zh-CN" ? "en-US" : "zh-CN");
                Check(Languages.ResolveSystem() == systemLanguage, "Windows display language is independent of process culture");
            }
            finally { CultureInfo.CurrentUICulture = originalUiCulture; }
            File.Delete(settingsPath); // 模拟首次启动；finally 精确还原用户原配置。
            desktop = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true })!;
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
            WaitFor(() => TrayIcons(desktop.Id).Count == 1, "exactly one registered tray icon on startup");

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

            for (var cycle = 0; cycle < 4; cycle++)
            {
                ((WindowPattern)root.GetCurrentPattern(WindowPattern.Pattern)).Close();
                WaitFor(() => root.Current.IsOffscreen, "window close hides to tray");
                Check(!desktop.HasExited, "desktop remains alive in tray");
                using var second = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true })!;
                Check(second.WaitForExit(5000), "second launch exits instead of creating another tray app");
                WaitFor(() => !root.Current.IsOffscreen, "second launch reopens the original desktop window");
                Thread.Sleep(150);
                Check(TrayIcons(desktop.Id).Count == 1, "reopening does not register another tray icon (cycle " + cycle + ")");
            }
            var language = Find(root, "Language");
            var expectedSystemLabel = systemLanguage == "zh-CN" ? "跟随系统" : "System default";
            Check(language.Current.Name == expectedSystemLabel, "first launch follows Windows display language");
            SwitchLanguage(root, desktop.Id, "English");
            WaitFor(() => language.Current.Name == "English", "manual language selection applies");
            using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                Check(settings.RootElement.GetProperty("Language").GetString() == "en-US", "manual language choice is persisted");
            SwitchLanguage(root, desktop.Id, "跟随系统 / System");
            WaitFor(() => language.Current.Name == expectedSystemLabel, "system language mode can be restored");
            using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                Check(settings.RootElement.GetProperty("Language").GetString() == "system", "system mode is persisted without fixing a language");
            Check(TrayIcons(desktop.Id).Count == 1, "language switching keeps one tray icon");
            Console.WriteLine("PASS: desktop launch/demo, actual click, four tray reopen cycles with exactly one registered icon, Windows language detection, manual/system language selection and persistence.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
        finally
        {
            if (desktop is { HasExited: false })
            {
                // WinForms 在 WM_DESTROY 中注销图标；强制结束测试进程前先清理，避免托盘残影。
                foreach (var icon in TrayIcons(desktop.Id)) SendMessage(icon.Window, 0x0002, IntPtr.Zero, IntPtr.Zero);
                desktop.Kill(); desktop.WaitForExit(3000);
            }
            desktop?.Dispose();
            if (savedSettings is null) File.Delete(settingsPath);
            else File.WriteAllBytes(settingsPath, savedSettings);
            SetCursorPos(originalCursor.X, originalCursor.Y);
            SetForegroundWindow(originalForeground);
        }
    }

    private static List<NotifyIconIdentifier> TrayIcons(int pid)
    {
        var icons = new List<NotifyIconIdentifier>();
        var windows = new List<IntPtr>();
        EnumWindows((window, parameter) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        // WinForms 可把通知回调窗口放在 HWND_MESSAGE 下，EnumWindows 不包含这类窗口。
        var messageWindow = IntPtr.Zero;
        while ((messageWindow = FindWindowEx(new IntPtr(-3), messageWindow, null, null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(messageWindow, out var owner);
            if (owner == pid) windows.Add(messageWindow);
        }
        foreach (var window in windows)
        {
            for (uint id = 1; id <= 32; id++)
            {
                var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = id };
                if (Shell_NotifyIconGetRect(ref identifier, out _) == 0) icons.Add(identifier);
            }
        }
        return icons;
    }

    private static void SwitchLanguage(AutomationElement root, int pid, string name)
    {
        ((InvokePattern)Find(root, "Language").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        AutomationElement? item = null;
        WaitFor(() => (item = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, name)))) is not null, "language menu item is available");
        ((InvokePattern)item!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
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
