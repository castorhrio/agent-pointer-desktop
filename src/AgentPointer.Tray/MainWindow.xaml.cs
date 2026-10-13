using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AgentPointer.Tray;

public partial class MainWindow : Window
{
    private readonly string _cliPath = Path.Combine(AppContext.BaseDirectory, "agentpointer.exe");
    private readonly DispatcherTimer _demoTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WinForms.NotifyIcon? _notifyIcon;
    private string? _releaseUrl;
    private bool _cliAvailable;
    private bool _skillAvailable;
    private bool _checking;
    private bool _actionRunning;
    private bool _demoActive;
    private bool _stoppingDemo;
    private bool _exitRequested;
    private bool _updateRunning;
    private int _demoGeneration;
    private long _demoDeadline;
    private string _demoStatusKey = "Str.DemoIdle";

    public string VersionLabel => "v" + AgentPointerVersion.Current;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 48);
        Width = Math.Min(820, SystemParameters.WorkArea.Width - 48);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        Activated += async (_, _) => { if (IsLoaded) await RefreshSetupAsync(); };
        Deactivated += async (_, _) => { if (_demoActive) await StopDemoAsync("Str.DemoInterrupted"); };
        LocationChanged += async (_, _) => { if (_demoActive) await StopDemoAsync("Str.DemoInterrupted"); };
        _demoTimer.Tick += async (_, _) =>
        {
            if (_demoActive && Environment.TickCount64 >= _demoDeadline)
                await StopDemoAsync("Str.DemoEnded");
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyWindowIcon();
        ApplyDarkTitleBar();
        UpdateLanguageButton();
        LoadEnvironment();
        SetupResult.Text = Tr("Str.SetupHint");
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "Agent Pointer for Windows", Icon = TrayIconFactory.Create(), Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        BuildTrayMenu();
        if (App.ShowOnLaunch) Activate();
        else Hide();
        await RefreshSetupAsync();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        Hide();
        await StopDemoAsync("Str.DemoEnded");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _demoTimer.Stop();
        if (_notifyIcon is { } icon)
        {
            icon.Visible = false;
            var image = icon.Icon;
            icon.Dispose();
            image?.Dispose();
        }
    }

    internal void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void BuildTrayMenu()
    {
        if (_notifyIcon is null) return;
        var old = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = null;
        old?.Dispose();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Tr("Str.TrayOpen"), null, (_, _) => ShowFromTray());
        var endDemo = menu.Items.Add(Tr("Str.StopDemo"), null, async (_, _) => await StopDemoAsync("Str.DemoEnded"));
        endDemo.Enabled = _demoActive;
        var language = new WinForms.ToolStripMenuItem(Tr("Str.TrayLanguage")) { Enabled = !_actionRunning && !_demoActive };
        var systemLanguage = new WinForms.ToolStripMenuItem("跟随系统 / System") { Checked = App.LanguagePreference == Languages.System };
        systemLanguage.Click += async (_, _) => await SetLanguageAndRefreshAsync(Languages.System);
        language.DropDownItems.Add(systemLanguage);
        foreach (var lang in Languages.All)
        {
            var item = new WinForms.ToolStripMenuItem(lang.NativeName) { Checked = lang.Code == App.LanguagePreference };
            item.Click += async (_, _) => await SetLanguageAndRefreshAsync(lang.Code);
            language.DropDownItems.Add(item);
        }
        menu.Items.Add(language);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        var exit = menu.Items.Add(Tr("Str.TrayExit"), null, async (_, _) =>
        {
            await StopDemoAsync("Str.DemoEnded");
            _exitRequested = true;
            Application.Current.Shutdown();
        });
        exit.Enabled = !_actionRunning;
        _notifyIcon.ContextMenuStrip = menu;
    }

    private async Task SetLanguageAndRefreshAsync(string language)
    {
        if (App.LanguagePreference == language) return;
        await StopDemoAsync("Str.DemoEnded");
        App.SetLanguage(language);
        UpdateLanguageButton();
        SetDemoStatus(_demoStatusKey);
        BuildTrayMenu();
        LoadEnvironment();
        await RefreshSetupAsync();
    }

    private void OnLanguageMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { StaysOpen = false };
        var systemLanguage = new MenuItem { Header = "跟随系统 / System", IsChecked = App.LanguagePreference == Languages.System };
        systemLanguage.Click += async (_, _) => await SetLanguageAndRefreshAsync(Languages.System);
        menu.Items.Add(systemLanguage);
        foreach (var lang in Languages.All)
        {
            var item = new MenuItem
            {
                Header = lang.NativeName, IsChecked = lang.Code == App.LanguagePreference,
                FontWeight = lang.Code == App.LanguagePreference ? FontWeights.SemiBold : FontWeights.Normal,
            };
            item.Click += async (_, _) => await SetLanguageAndRefreshAsync(lang.Code);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = LanguageButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UpdateLanguageButton() =>
        LanguageButton.Content = App.LanguagePreference == Languages.System
            ? Tr("Str.LanguageSystem") : Languages.Find(App.Language)?.NativeName ?? App.Language;

    private static string Tr(string key) => (string?)Application.Current.TryFindResource(key) ?? key;

    private async Task RefreshSetupAsync()
    {
        if (_checking || _actionRunning || _demoActive || _exitRequested) return;
        _checking = true;
        _skillAvailable = false;
        _cliAvailable = false;
        ClaudeStatus.Text = CodexStatus.Text = Tr("Str.StatusChecking");
        UpdateActions();
        try
        {
            if (!File.Exists(_cliPath))
            {
                ComponentStatus.Text = Tr("Str.ComponentMissing");
                SetupResult.Text = Tr("Str.ComponentMissing");
                ClaudeStatus.Text = CodexStatus.Text = Tr("Str.StatusUnavailable");
                return;
            }
            var version = await RunCliAsync("--version");
            if (version.ExitCode != 0) throw new InvalidOperationException(Tr("Str.ComponentFailed"));
            _cliAvailable = true;
            ComponentStatus.Text = string.Format(CultureInfo.CurrentCulture, Tr("Str.ComponentReady"), version.Output.Trim());
            using var response = ParseResponse(await RunCliAsync("install-skill", "--dry-run", "--json"));
            foreach (var skill in response.RootElement.GetProperty("skills").EnumerateArray())
            {
                var installed = skill.GetProperty("installed").GetBoolean();
                var current = skill.GetProperty("current").GetBoolean();
                var readable = skill.GetProperty("error").ValueKind == JsonValueKind.Null;
                var status = !readable ? "Str.StatusUnavailable" : current ? "Str.StatusInstalled"
                    : installed ? "Str.StatusOutdated" : "Str.StatusMissing";
                var control = skill.GetProperty("agent").GetString() == "Codex" ? CodexStatus : ClaudeStatus;
                control.Text = Tr(status);
                control.Foreground = (System.Windows.Media.Brush)FindResource(current ? "BrushOk" : "BrushTextDim");
                _skillAvailable |= current;
            }
            SetupResult.Text = Tr(_skillAvailable ? "Str.SkillsInstalled" : "Str.SetupHint");
        }
        catch (Exception ex)
        {
            ClaudeStatus.Text = CodexStatus.Text = Tr("Str.StatusUnavailable");
            if (!_cliAvailable) ComponentStatus.Text = Tr("Str.ComponentFailed");
            SetupResult.Text = string.Format(CultureInfo.CurrentCulture, Tr("Str.ActionFailed"), ex.Message);
        }
        finally
        {
            _checking = false;
            UpdateActions();
        }
    }

    private async void OnInstallSkills(object sender, RoutedEventArgs e)
    {
        if (_actionRunning || !_cliAvailable || _demoActive) return;
        _actionRunning = true;
        SetupResult.Text = Tr("Str.InstallingSkills");
        UpdateActions();
        string? error = null;
        try
        {
            using var response = ParseResponse(await RunCliAsync("install-skill", "--force", "--json"));
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            _actionRunning = false;
            await RefreshSetupAsync();
            if (error is not null)
                SetupResult.Text = string.Format(CultureInfo.CurrentCulture, Tr("Str.ActionFailed"), error);
        }
    }

    private async void OnStartDemo(object sender, RoutedEventArgs e)
    {
        if (_actionRunning || !_cliAvailable || _demoActive) return;
        _actionRunning = true;
        _demoActive = true;
        var generation = ++_demoGeneration;
        SetDemoStatus("Str.DemoStarting");
        UpdateActions();
        try
        {
            // WPF PointToScreen 返回屏幕像素。箭尖落在按钮左缘前，保留完整按钮文字。
            var target = DemoTargetButton.PointToScreen(new System.Windows.Point(-8, DemoTargetButton.ActualHeight / 2));
            var coordinates = string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(target.X)},{(int)Math.Round(target.Y)}");
            using var response = ParseResponse(await RunCliAsync("start", "--at", coordinates,
                "--text", Tr("Str.DemoInstruction"), "--from", "left", "--size", "S", "--duration", "20", "--json"));
            if (generation != _demoGeneration)
            {
                ParseResponse(await RunCliAsync("stop", "--json")).Dispose();
                return;
            }
            DemoTargetButton.IsEnabled = true;
            SetDemoStatus("Str.DemoVisible");
            _demoDeadline = Environment.TickCount64 + 20_000;
            _demoTimer.Start();
        }
        catch (Exception ex)
        {
            _demoActive = false;
            DemoStatus.Text = string.Format(CultureInfo.CurrentCulture, Tr("Str.ActionFailed"), ex.Message);
        }
        finally
        {
            _actionRunning = false;
            UpdateActions();
        }
    }

    private async void OnCompleteDemo(object sender, RoutedEventArgs e) => await StopDemoAsync("Str.DemoCompleted");
    private async void OnStopDemo(object sender, RoutedEventArgs e) => await StopDemoAsync("Str.DemoEnded");

    private async Task StopDemoAsync(string statusKey)
    {
        if (!_demoActive || _stoppingDemo) return;
        _stoppingDemo = true;
        _demoActive = false;
        ++_demoGeneration;
        _demoTimer.Stop();
        DemoTargetButton.IsEnabled = false;
        UpdateActions();
        try
        {
            using var response = ParseResponse(await RunCliAsync("stop", "--json"));
            SetDemoStatus(statusKey);
        }
        catch (Exception ex)
        {
            DemoStatus.Text = string.Format(CultureInfo.CurrentCulture, Tr("Str.ActionFailed"), ex.Message);
        }
        finally
        {
            _stoppingDemo = false;
            UpdateActions();
        }
    }

    private void SetDemoStatus(string key)
    {
        _demoStatusKey = key;
        DemoStatus.Text = Tr(key);
    }

    private void UpdateActions()
    {
        var idle = !_actionRunning && !_demoActive && !_stoppingDemo && !_checking;
        StartDemoButton.IsEnabled = _cliAvailable && idle;
        InstallSkillsButton.IsEnabled = _cliAvailable && idle;
        CopyPromptButton.IsEnabled = _skillAvailable && idle;
        RefreshButton.IsEnabled = idle;
        LanguageButton.IsEnabled = !_actionRunning && !_demoActive;
        StopDemoButton.Visibility = _demoActive ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = UpdateChecker.IsConfigured && !_updateRunning && !_demoActive && !_actionRunning;
        CheckUpdateButton.ToolTip = UpdateChecker.IsConfigured ? null : Tr("Str.UpdateNotConfigured");
        BuildTrayMenu();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshSetupAsync();

    private async Task<(int ExitCode, string Output, string Error)> RunCliAsync(params string[] args)
    {
        var info = new ProcessStartInfo(_cliPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        // 桌面体验只操作自己的箭头，不能继承启动终端的 agent owner。
        info.Environment.Remove("CLAUDE_PID");
        info.Environment["BIGARROW_OWNER_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(Tr("Str.ComponentFailed"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException(Tr("Str.ComponentFailed"));
        }
    }

    private static JsonDocument ParseResponse((int ExitCode, string Output, string Error) result)
    {
        if (result.ExitCode == 0) return JsonDocument.Parse(result.Output);
        var message = result.Error.Trim();
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            if (json.RootElement.TryGetProperty("error", out var error)) message = error.GetString() ?? message;
        }
        catch (JsonException) { }
        throw new InvalidOperationException(message.Length > 0 ? message : Tr("Str.ComponentFailed"));
    }

    private void OnCopyPrompt(object sender, RoutedEventArgs e) =>
        SetupResult.Text = Tr(CopyToClipboard(Tr("Str.AgentPrompt")) ? "Str.PromptCopied" : "Str.ClipboardFailed");

    private void OnCopyDoctor(object sender, RoutedEventArgs e)
    {
        var command = "& '" + _cliPath.Replace("'", "''") + "' doctor --json";
        SetupResult.Text = Tr(CopyToClipboard(command) ? "Str.DoctorCopied" : "Str.ClipboardFailed");
    }

    private static bool CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (COMException) { return false; }
    }

    private void LoadEnvironment()
    {
        var screens = WinForms.Screen.AllScreens;
        var primary = WinForms.Screen.PrimaryScreen ?? screens.FirstOrDefault();
        if (primary is null) return;
        var dpi = 96f;
        try { using var graphics = Graphics.FromHwnd(IntPtr.Zero); dpi = graphics.DpiX; }
        catch (ExternalException) { }
        EnvironmentText.Text = string.Format(CultureInfo.CurrentCulture, Tr("Str.EnvFormat"),
            screens.Length, primary.Bounds.Width, primary.Bounds.Height, (int)Math.Round(dpi / 96f * 100));
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateRunning) return;
        _updateRunning = true;
        CopyLinkButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Visibility = Visibility.Visible;
        UpdateStatus.Text = Tr("Str.Checking");
        UpdateActions();
        try
        {
            var result = await UpdateChecker.CheckAsync(AgentPointerVersion.Current);
            UpdateStatus.Text = result.Kind switch
            {
                UpdateKind.Available => string.Format(CultureInfo.CurrentCulture, Tr("Str.NewVersion"), result.Latest),
                UpdateKind.UpToDate => Tr("Str.UpToDate"),
                UpdateKind.NotConfigured => Tr("Str.UpdateNotConfigured"),
                _ => Tr("Str.UpdateFailed"),
            };
            _releaseUrl = result.ReleaseUrl;
            CopyLinkButton.Visibility = result.Kind == UpdateKind.Available ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updateRunning = false; UpdateActions(); }
    }

    private void OnCopyReleaseLink(object sender, RoutedEventArgs e)
    {
        if (_releaseUrl is { } url && !CopyToClipboard(url)) UpdateStatus.Text = Tr("Str.ClipboardFailed");
    }

    private void ApplyWindowIcon()
    {
        try
        {
            using var icon = TrayIconFactory.Create();
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Icon = source;
        }
        catch (ExternalException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
}

internal static class AgentPointerVersion
{
    public const string Current = "0.1.1";
}
