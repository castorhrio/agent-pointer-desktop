using System.Globalization;
using System.Speech.Synthesis;
using AgentPointer.Core.Cli;
using AgentPointer.Core.Rendering;

namespace AgentPointer.Cli.Overlay;

/// <summary>
/// <c>--say</c>：把牌子上的话念出来。
///
/// <para><b>为什么单独开线程</b>：<see cref="SpeechSynthesizer"/> 不是线程安全的，SAPI 又是
/// 套间线程模型。所以合成器的<strong>一切</strong>——构造、挑语音、<c>SpeakAsync</c>、
/// 等 <c>SpeakCompleted</c>、取消、释放——都发生在同一条专用 STA 后台线程上，UI 线程
/// 从头到尾一次都不碰它，覆盖层的动画和等待循环一秒也不会被它挡住。</para>
///
/// <para><b>怎么挑语音</b>：<c>--voice</c> 按名称子串匹配已安装语音；没给或没匹配上，
/// 文本含 CJK 就优先中文语音、否则优先英文语音，最后退到系统默认。</para>
///
/// <para><b>失败怎么办</b>：任何语音层异常都只写 stderr（<c>--json</c> 时连 stderr 也不写），
/// 绝不影响箭头本身——一个念不出话的环境不该让"指给你看"失败。</para>
/// </summary>
internal sealed class SignSpeaker : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly ManualResetEventSlim _cancel = new(false);
    private readonly string _spokenText;
    private readonly string? _voiceName;
    private readonly bool _json;
    private int _disposed;

    private SignSpeaker(string spoken, string? voiceName, bool json)
    {
        _spokenText = spoken;
        _voiceName = voiceName;
        _json = json;

        _thread = new Thread(Run) { IsBackground = true, Name = "agentpointer-speech" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>开始朗读。文本为空（或只有花括号）时不朗读，返回 null。</summary>
    public static SignSpeaker? Start(string? text, string? voiceName, bool json)
    {
        var spoken = SpeechText.ToSpeakable(text);
        if (spoken.Length == 0) return null;

        try
        {
            return new SignSpeaker(spoken, voiceName, json);
        }
        catch (Exception ex)
        {
            Warn(json, Lang.T($"朗读未能启动：{ex.Message}", $"Could not start speaking: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// 这条线程就是合成器的所有者：在这里建、在这里配、在这里念、在这里等结果、
    /// 在这里取消、在这里释放。UI 线程只负责 <see cref="Dispose"/> 时置一个标志。
    /// </summary>
    private void Run()
    {
        SpeechSynthesizer? synth = null;

        try
        {
            synth = new SpeechSynthesizer();

            try
            {
                SelectVoice(synth, _spokenText, _voiceName, _json);
            }
            catch (Exception ex)
            {
                Warn(_json, Lang.T(
                    $"语音配置失败，改用系统默认语音：{ex.Message}",
                    $"Voice configuration failed, falling back to the system default: {ex.Message}"));
            }

            var completed = new ManualResetEventSlim(false);
            EventHandler<SpeakCompletedEventArgs> onCompleted = (_, _) => completed.Set();
            synth.SpeakCompleted += onCompleted;

            try
            {
                synth.SpeakAsync(_spokenText);

                // 在这条线程上等：谁也不用泵消息，完成回调自己会把事件置上。
                // 取消同样只置事件——合成器的每一次调用都留在这条线程里。
                WaitHandle.WaitAny(new[] { completed.WaitHandle, _cancel.WaitHandle });

                if (_cancel.IsSet)
                {
                    try
                    {
                        synth.SpeakAsyncCancelAll();
                    }
                    catch (Exception)
                    {
                        // 取消失败只能算了，反正线程马上就结束
                    }
                }
            }
            finally
            {
                synth.SpeakCompleted -= onCompleted;
                completed.Dispose();
            }
        }
        catch (Exception ex)
        {
            Warn(_json, Lang.T($"朗读失败：{ex.Message}", $"Speaking failed: {ex.Message}"));
        }
        finally
        {
            try
            {
                synth?.Dispose();
            }
            catch (Exception)
            {
                // 合成器自己释放失败没有补救办法，也不该冒泡到 UI 线程
            }

            _finished.Set();
        }
    }

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

    /// <summary>
    /// 收场：请朗读线程自己取消（<c>SpeakAsyncCancelAll</c> 只能由它来调），
    /// 等它退出，绝不让它拖住进程退出。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cancel.Set();

        if (_finished.Wait(1500))
        {
            _finished.Dispose();
            _cancel.Dispose();
        }
    }
}
