using AgentPointer.Core.Rendering;
using Xunit;

namespace AgentPointer.Core.Tests;

public class SignTextTests
{
    [Fact]
    public void SingleValueSplitsIntoLiteralsAndOneChip()
    {
        var segments = SignText.Parse("copy & paste the PIN {{482913}} here");

        Assert.Equal(3, segments.Count);
        Assert.Equal("copy & paste the PIN ", segments[0].Text);
        Assert.False(segments[0].IsValue);
        Assert.Equal("482913", segments[1].Text);
        Assert.True(segments[1].IsValue);
        Assert.Equal(" here", segments[2].Text);
        Assert.False(segments[2].IsValue);
    }

    [Fact]
    public void MultipleValuesKeepTheirOrder()
    {
        var segments = SignText.Parse("{{alpha}} then {{beta}}");

        Assert.Equal(3, segments.Count);
        Assert.True(segments[0].IsValue);
        Assert.True(segments[2].IsValue);
        Assert.Equal(new[] { "alpha", "beta" }, SignText.Values("{{alpha}} then {{beta}}"));
    }

    [Fact]
    public void AdjacentValuesAreTwoChips()
    {
        var segments = SignText.Parse("{{a}}{{b}}");

        Assert.Equal(2, segments.Count);
        Assert.True(segments[0].IsValue);
        Assert.True(segments[1].IsValue);
        Assert.Equal("a", segments[0].Text);
        Assert.Equal("b", segments[1].Text);
    }

    [Fact]
    public void EmptyBracesStayLiteral()
    {
        var segments = SignText.Parse("click {{}} now");

        Assert.Single(segments);
        Assert.False(segments[0].IsValue);
        Assert.Equal("click {{}} now", segments[0].Text);
        Assert.Empty(SignText.Values("click {{}} now"));
    }

    [Fact]
    public void UnmatchedOpenStaysLiteral()
    {
        var segments = SignText.Parse("click {{here");

        Assert.Single(segments);
        Assert.False(segments[0].IsValue);
        Assert.Equal("click {{here", segments[0].Text);
    }

    [Fact]
    public void UnmatchedCloseStaysLiteral()
    {
        var segments = SignText.Parse("click here}}");

        Assert.Single(segments);
        Assert.False(segments[0].IsValue);
        Assert.Equal("click here}}", segments[0].Text);
    }

    [Fact]
    public void CjkTextAroundAValueSurvives()
    {
        var segments = SignText.Parse("先把验证码 {{ 482 913 }} 填进去");

        Assert.Equal(3, segments.Count);
        Assert.True(segments[1].IsValue);
        Assert.Equal("482 913", segments[1].Text);
        Assert.Equal("先把验证码 ", segments[0].Text);
        Assert.Equal(" 填进去", segments[2].Text);
    }

    [Fact]
    public void LiteralTextWithoutBracesIsOneSegment()
    {
        var segments = SignText.Parse("点这里继续");

        Assert.Single(segments);
        Assert.False(segments[0].IsValue);
        Assert.Equal("点这里继续", segments[0].Text);
        Assert.Empty(SignText.Values("点这里继续"));
    }

    [Fact]
    public void PlainTextRemovesBraces()
    {
        Assert.Equal("copy the PIN 482913 here", SignText.PlainText("copy the PIN {{482913}} here"));
        Assert.Equal("click  now", SignText.PlainText("click {{}} now"));
    }

    [Fact]
    public void NullAndEmptyProduceNothing()
    {
        Assert.Empty(SignText.Parse(null));
        Assert.Empty(SignText.Parse(""));
        Assert.Empty(SignText.Values(null));
        Assert.Equal(string.Empty, SignText.PlainText(null));
    }

    [Fact]
    public void SegmentsRejoinToTheOriginalText()
    {
        const string original = "PIN {{482913}} 或者 {{482 913}} 都行";

        // 复制记号的花括号不落在任何一段里：拼回去等于原文去掉花括号
        var rejoined = string.Concat(SignText.Parse(original).Select(segment => segment.Text));

        Assert.Equal("PIN 482913 或者 482 913 都行", rejoined);
        Assert.Equal(SignText.PlainText(original), rejoined);
    }
}
