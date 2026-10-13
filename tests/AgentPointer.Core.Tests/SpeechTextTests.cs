using AgentPointer.Core.Rendering;

namespace AgentPointer.Core.Tests;

/// <summary>--say 的两条纯规则：只念值本身、按语言挑语音。</summary>
public class SpeechTextTests
{
    [Fact]
    public void 花括号被剥掉只念值本身()
    {
        // 规则只是"剥掉花括号，只念值本身"；文本里的 & 原样保留，不该被改写成 "and"
        Assert.Equal(
            "Franz, copy & paste the PIN 482913 here",
            SpeechText.ToSpeakable("Franz, copy & paste the PIN {{482913}} here"));
    }

    [Fact]
    public void 没有花括号时原样返回()
    {
        Assert.Equal("Franz, click Save", SpeechText.ToSpeakable("Franz, click Save"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{{}}")]
    [InlineData("{ }")]
    public void 没有可念内容时返回空串(string? text)
    {
        Assert.Equal(string.Empty, SpeechText.ToSpeakable(text));
    }

    [Fact]
    public void 剥掉花括号后裁掉两侧空白()
    {
        Assert.Equal("click", SpeechText.ToSpeakable("  {{}}click  "));
    }

    [Theory]
    [InlineData("Franz, click Allow")]
    [InlineData("PIN 482913")]
    [InlineData("")]
    public void 纯拉丁文本不含CJK(string text)
    {
        Assert.False(SpeechText.ContainsCjk(text));
    }

    [Theory]
    [InlineData("弗朗茨，点允许")]
    [InlineData("click 允许")]
    [InlineData("注意：请点保存")]
    public void 含汉字时判定为CJK(string text)
    {
        Assert.True(SpeechText.ContainsCjk(text));
    }

    [Fact]
    public void null与空串不含CJK()
    {
        Assert.False(SpeechText.ContainsCjk(null));
        Assert.False(SpeechText.ContainsCjk(""));
    }
}
