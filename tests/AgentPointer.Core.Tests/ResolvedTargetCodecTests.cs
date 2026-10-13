using System;
using System.Collections.Generic;
using AgentPointer.Core.Protocol;
using AgentPointer.Core.Targeting;
using Xunit;

public class ResolvedTargetCodecTests
{
    private static readonly ElementInfo Button =
        new("Allow", "button", "allowButton", 10, 20, 30, 40, true, false);

    private static Func<string, string?> Env(IReadOnlyDictionary<string, string> values) =>
        key => values.TryGetValue(key, out var value) ? value : null;

    [Fact]
    public void EncodeThenDecodeRoundTrips()
    {
        var original = new ResolvedTarget(1, 2, 3, 4, new IntPtr(4242), Button, true, false);

        var encoded = ResolvedTargetCodec.Encode(original);

        Assert.True(ResolvedTargetCodec.TryDecode(Env(encoded), out var decoded));
        Assert.Equal(1, decoded.X);
        Assert.Equal(2, decoded.Y);
        Assert.Equal(3, decoded.Width);
        Assert.Equal(4, decoded.Height);
        Assert.Equal(new IntPtr(4242), decoded.Window);
        Assert.Equal(Button, decoded.Element);
        Assert.True(decoded.Raised);
        Assert.False(decoded.Truncated);
    }

    [Fact]
    public void EncodeThenDecodeRoundTripsWithoutElementWindowOrRaise()
    {
        var original = new ResolvedTarget(-10, -20, 300, 400, IntPtr.Zero, null, null, true);

        var encoded = ResolvedTargetCodec.Encode(original);

        Assert.False(encoded.ContainsKey(ResolvedTargetCodec.ElementVariable));
        Assert.True(ResolvedTargetCodec.TryDecode(Env(encoded), out var decoded));
        Assert.Equal(-10, decoded.X);
        Assert.Equal(new IntPtr(0), decoded.Window);
        Assert.Null(decoded.Element);
        Assert.Null(decoded.Raised);
        Assert.True(decoded.Truncated);
    }

    [Fact]
    public void MissingEnvironmentIsRejected() =>
        Assert.False(ResolvedTargetCodec.TryDecode(_ => null, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("a,2,3,4")]
    [InlineData("1,2,x,4")]
    public void MalformedRectIsRejected(string raw)
    {
        var encoded = new Dictionary<string, string> { [ResolvedTargetCodec.RectVariable] = raw };

        Assert.False(ResolvedTargetCodec.TryDecode(Env(encoded), out _));
    }

    [Fact]
    public void MalformedWindowElementAndRaisedFallBackToDefaults()
    {
        var encoded = new Dictionary<string, string>
        {
            [ResolvedTargetCodec.RectVariable] = "1,2,3,4",
            [ResolvedTargetCodec.WindowVariable] = "not-a-handle",
            [ResolvedTargetCodec.ElementVariable] = "{ not json",
            [ResolvedTargetCodec.RaisedVariable] = "maybe",
            [ResolvedTargetCodec.TruncatedVariable] = "perhaps",
        };

        Assert.True(ResolvedTargetCodec.TryDecode(Env(encoded), out var decoded));
        Assert.Equal(1, decoded.X);
        Assert.Equal(new IntPtr(0), decoded.Window);
        Assert.Null(decoded.Element);
        Assert.Null(decoded.Raised);
        Assert.False(decoded.Truncated);
    }
}
