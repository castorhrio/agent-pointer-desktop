using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows;

using Application = System.Windows.Application;

namespace AgentPointer.Tray;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\AgentPointer.Desktop.v1";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showWait;
    private bool _ownsMutex;

    /// <summary>正常启动打开面板；--background 仅进入托盘。</summary>
    public static bool ShowOnLaunch { get; private set; }

    /// <summary>当前 UI 语言。解析顺序：持久化选择 → 系统语言 → <see cref="Languages.Fallback"/>。</summary>
    public static string Language { get; private set; } = Languages.Fallback;

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentPointer");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (EventWaitHandle.TryOpenExisting(InstanceName + ".Show", out var request))
            {
                using (request) request.Set();
            }
            Shutdown();
            return;
        }
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.ShowFromTray());
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        ShowOnLaunch = !e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        LoadLanguage();
        ApplyLanguage(Language);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showRequest?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>切换语言并持久化；语言入口（面板按钮 / 托盘菜单）统一走这里。</summary>
    public static void SetLanguage(string language)
    {
        if (Languages.Find(language) is null) return;

        Language = language;
        SaveLanguage();
        ApplyLanguage(language);
    }

    private static void LoadLanguage()
    {
        try
        {
            if (File.Exists(SettingsPath)
                && JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(SettingsPath, Encoding.UTF8)) is { Language: { } saved}
                && Languages.Find(saved) is not null)
            {
                Language = Languages.Find(saved)!.Code;
                return;
            }
        }
        catch
        {
            // 配置损坏就回退系统语言
        }

        Language = Languages.ResolveSystem();
    }

    private static void SaveLanguage()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new SettingsDto { Language = Language }), new UTF8Encoding(false));
        }
        catch
        {
            // 持久化失败不阻断切换
        }
    }

    /// <summary>
    /// 换掉合并字典里的语言层。回退顺序靠合并顺序实现：en-US（基础）先合并、
    /// 所选语言后合并——后者覆盖同名 key，缺失的 key 自动露出英文。
    /// </summary>
    private static void ApplyLanguage(string language)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        var existing = dictionaries
            .Where(d => d.Source?.OriginalString.Contains("Strings.", StringComparison.Ordinal) == true)
            .ToList();

        foreach (var dictionary in existing)
        {
            dictionaries.Remove(dictionary);
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Strings.{Languages.Fallback}.xaml", UriKind.Absolute),
        });

        if (language != Languages.Fallback)
        {
            dictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Strings.{language}.xaml", UriKind.Absolute),
            });
        }
    }

    private sealed record SettingsDto
    {
        public string? Language { get; init; }
    }
}
