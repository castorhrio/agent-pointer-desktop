using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using AgentPointer.Tray;
using Microsoft.Win32;

internal static class InstallerChecks
{
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    public static int Run(string executable, string installationDirectory)
    {
        executable = Path.GetFullPath(executable);
        installationDirectory = Path.GetFullPath(installationDirectory);
        var artifactRoot = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!installationDirectory.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase) || Directory.Exists(installationDirectory))
        {
            Console.Error.WriteLine("Use a new directory inside this checkout's artifacts folder for installer checks.");
            return 2;
        }
        var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Agent Pointer.lnk");
        var menuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Agent Pointer");
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentPointer";
        using (var existing = Registry.CurrentUser.OpenSubKey(uninstallKey))
        using (var existingSettings = Registry.CurrentUser.OpenSubKey(@"Software\AgentPointer"))
        {
            if (existing is not null || existingSettings is not null || File.Exists(desktopShortcut) || Directory.Exists(menuDirectory))
            {
                Console.Error.WriteLine("Installer checks require no existing Agent Pointer installation or shortcuts; they will not overwrite yours.");
                return 2;
            }
        }
        var foreground = GetForegroundWindow();
        var uninstallerPath = Path.Combine(installationDirectory, "Uninstall.exe");
        Process? setup = null;
        try
        {
            setup = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
            AutomationElement? root = null;
            WaitFor(() => (root = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, setup.Id))) is not null, "installer welcome page");
            var next = AutomationElement.FromHandle(GetDlgItem(new IntPtr(root!.Current.NativeWindowHandle), 1));
            Check(next.Current.Name.Contains(Languages.ResolveSystem() == "zh-CN" ? "下一步" : "Next"), "installer follows Windows display language");
            ((InvokePattern)next.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            AutomationElement? directory = null;
            WaitFor(() => (directory = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))) is not null, "installation directory page");
            ((ValuePattern)directory!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(installationDirectory);
            ((InvokePattern)next.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            AutomationElement? run = null;
            WaitFor(() => (run = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))) is not null
                && File.Exists(uninstallerPath), "installation finishes and offers to launch the app");
            var launch = (TogglePattern)run!.GetCurrentPattern(TogglePattern.Pattern);
            if (launch.Current.ToggleState == ToggleState.On) launch.Toggle();
            ((InvokePattern)next.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Check(setup.WaitForExit(5000) && setup.ExitCode == 0, "installer finishes successfully");
            var installedApp = Path.Combine(installationDirectory, "AgentPointer.Desktop.exe");
            foreach (var file in new[] { "AgentPointer.Desktop.exe", "agentpointer.exe", "README.md", "README.zh-CN.md", "LICENSE", "THIRD_PARTY_NOTICES.md" })
                Check(File.Exists(Path.Combine(installationDirectory, file)), "installed file: " + file);
            Check(File.Exists(desktopShortcut) && File.Exists(Path.Combine(menuDirectory, "Agent Pointer.lnk"))
                && File.Exists(Path.Combine(menuDirectory, "Uninstall.lnk")), "desktop and Start-menu shortcuts");
            using (var registration = Registry.CurrentUser.OpenSubKey(uninstallKey))
            {
                Check((string?)registration?.GetValue("InstallLocation") == installationDirectory, "Windows Apps uninstall entry");
                Check((string?)registration?.GetValue("UninstallString") == '"' + uninstallerPath + '"', "quoted uninstall command");
            }
            Check(DesktopChecks.Run(installedApp, Path.Combine(installationDirectory, "installer-desktop-check.png")) == 0,
                "installed app interaction, language, and tray regression checks");
            var keepFile = Path.Combine(installationDirectory, "keep-me.txt");
            File.WriteAllText(keepFile, "Uninstallation must preserve unrelated files.", new UTF8Encoding(false));
            using var uninstall = Process.Start(new ProcessStartInfo(uninstallerPath) { UseShellExecute = false, Arguments = "/S" })!;
            Check(uninstall.WaitForExit(5000), "uninstaller starts successfully");
            WaitFor(() => !File.Exists(installedApp) && !File.Exists(uninstallerPath) && !File.Exists(desktopShortcut)
                && !Directory.Exists(menuDirectory), "uninstaller removes the installed application and shortcuts");
            using (var removed = Registry.CurrentUser.OpenSubKey(uninstallKey)) Check(removed is null, "uninstaller removes its Windows Apps registration");
            Check(File.Exists(keepFile), "uninstaller preserves unrelated files");
            Console.WriteLine("PASS: localized setup wizard, custom Unicode directory, complete install, shortcuts, Apps registration, installed desktop checks, uninstall, unrelated-file preservation.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
        finally
        {
            if (setup is { HasExited: false }) { setup.Kill(); setup.WaitForExit(3000); }
            setup?.Dispose();
            // 清理仅属于本次检查的新目录；前置校验拒绝已有安装与快捷方式。
            if (File.Exists(uninstallerPath))
            {
                using var cleanup = Process.Start(new ProcessStartInfo(uninstallerPath) { UseShellExecute = false, Arguments = "/S" });
                cleanup?.WaitForExit(5000);
            }
            SetForegroundWindow(foreground);
        }
    }

    private static void WaitFor(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 15000) Thread.Sleep(60);
        Check(condition(), description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }
}
