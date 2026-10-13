using System.Globalization;
using System.Speech.Synthesis;
using AgentPointer.Core.Cli;
using AgentPointer.Core.Rendering;

namespace AgentPointer.Cli.Overlay;

/// <summary>
/// <c>--say</c>：把牌子上的话念出来。
///
/// <para><b>为什么单独开线程</b>：<see cref="SpeechSynthesizer"/> 不是线程安全的，而且
/// <c>SpeakAsync</c> 需要有人在同一个线程上泵消息。朗读跑在独立的 STA 后台线程上，
/// 覆盖层的动画和等待循环一秒都不会被它挡住。</para>
///
/// <para><b>怎么挑语音</b>：<c>--voice</c> 按名称子串匹配已安装语音；没给或没匹配上，
/// 文本含 CJK 就优先中文语音、否则优先英文语音，最后退到系统默认。</para>
///
/// <para><b>失败怎么办</b>：任何语音层异常都只写 stderr（<c>--json</c> 时连 stderr 也不写），
/// 绝不影响箭头本身——一个念不出话的环境不该让"指给你看"失败。</para>
/// </summary>
internal sealed class SignSpeaker : IDisposable
{
    private readonly SpeechSynthesizer _synth;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly ManualResetEventSlim _spoken = new(false);
    private readonly bool _json;
    private int _disposed;

    private SignSpeaker(SpeechSynthesizer synth, string spoken, string? voiceName, bool json)
    {
        _synth = synth;
        _json = json;

        try
        {
            SelectVoice(synth, spoken, voiceName, json);
        }
        catch (Exception ex)
        {
            Warn(json, Lang.T(
                $"语音配置失败，改用系统默认语音：{ex.Message}",
                $"Voice configuration failed, falling back to the system default: {ex.Message}"));
        }

        _thread = new Thread(() => Speak(spoken)) { IsBackground = true, Name = "agentpointer-speech" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>开始朗读。文本为空（或只有花括号）时不朗读，返回 null。</summary>
    public static SignSpeaker? Start(string? text, string? voiceName, bool json)
    {
        var spoken = SpeechText.ToSpeakable(text);
        if (spoken.Length == 0) return null;

        SpeechSynthesizer synth;
        try
        {
            synth = new SpeechSynthesizer();
        }
        catch (Exception ex)
        {
            Warn(json, Lang.T(
                $"没有可用的语音合成器：{ex.Message}", $"No speech synthesizer available: {ex.Message}"));
            return null;
        }

        try
        {
            return new SignSpeaker(synth, spoken, voiceName, json);
        }
        catch (Exception ex)
        {
            Warn(json, Lang.T($"朗读未能启动：{ex.Message}", $"Could not start speaking: {ex.Message}"));
            synth.Dispose();
            return null;
        }
    }

    private void Speak(string spoken)
    {
        try
        {
            _synth.SpeakCompleted += OnSpeakCompleted;
            _synth.SpeakAsync(spoken);
            // SpeakCompleted 由合成器自己的线程触发（取消时也会触发），这里只是把
            // 这条后台线程挂住等结果，覆盖层的动画和等待循环一点都不会被挡住。
            _spoken.Wait();
        }
        catch (Exception ex)
        {
            Warn(_json, Lang.T($"朗读失败：{ex.Message}", $"Speaking failed: {ex.Message}"));
        }
        finally
        {
            try
            {
                _synth.SpeakCompleted -= OnSpeakCompleted;
                _synth.Dispose();
            }
            catch (Exception)
            {
                // 合成器自己释放失败没有补救办法，也不该冒泡到 UI 线程
            }

            _finished.Set();
        }
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e) => _spoken.Set();

    private static void SelectVoice(SpeechSynthesizer synth, string spoken, string? voiceName, bool json)
    {
        var installed = synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .ToList();

        if (installed.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            var wanted = voiceName.Trim();
            var match = installed.FirstOrDefault(v =>
                v.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                synth.SelectVoice(match.Name);
                return;
            }

            // 未知语音不是错误：退回默认，只提醒一句
            Warn(json, Lang.T(
                $"找不到名为 '{wanted}' 的语音，改用默认语音。",
                $"No voice named '{wanted}', falling back to the default voice."));
        }

        // 中文文本优先中文语音；否则优先英文语音；都没有就交给系统默认
        var preferred = SpeechText.ContainsCjk(spoken)
            ? installed.FirstOrDefault(v => IsChinese(v.Culture))
            : installed.FirstOrDefault(v => IsEnglish(v.Culture));

        if (preferred is not null)
        {
            synth.SelectVoice(preferred.Name);
            return;
        }

        synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet);
    }

    private static bool IsChinese(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnglish(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase);

    private static void Warn(bool json, string message)
    {
        if (json) return;

        try
        {
            Console.Error.WriteLine($"agentpointer: {message}");
            Console.Error.Flush();
        }
        catch (IOException)
        {
        }
    }

    /// <summary>收场：取消正在念的内容，等后台线程退出，绝不让它拖住进程退出。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _synth.SpeakAsyncCancelAll();
        }
        catch (Exception)
        {
        }

        if (_finished.Wait(1000))
        {
            _finished.Dispose();
        }
    }
}
