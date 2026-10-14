using AgentPointer.Core.Rendering;

public class SignLayoutTests
{
    private const float LineHeight = 20f;

    /// <summary>假的量字：每字符 10 逻辑像素，行高固定——排版本身与字体无关。</summary>
    private static float Measure(string token) => token.Length * 10f;

    private static SignLayoutResult Build(string? text, float width = 10_000f, int maxLines = 0) =>
        SignLayout.Build(text, Measure, 0f, 0f, width, LineHeight, SignChipStyle.Default, maxLines);

    /// <summary>回归：芯片前面的普通文字必须排出来，也要算进宽度（曾经整段丢失）。</summary>
    [Fact]
    public void TextBeforeTheFirstChipIsKept()
    {
        const string text = "Copy the PIN {{482913}} and paste it here";

        var layout = Build(text);

        var first = layout.Runs[0];
        Assert.Equal(SignRunKind.Text, first.Kind);
        Assert.Equal("Copy the PIN ", first.Text);
        Assert.Equal(0, first.Line);
        Assert.Equal(0f, first.X);

        var chip = Assert.Single(layout.Chips);
        Assert.Equal("482913", chip.Value);

        // 芯片排在正文之后，而不是顶到行首、把前面的文字挤掉
        Assert.Equal(first.Right + SignChipStyle.Default.Margin, chip.X);
        Assert.True(chip.X > first.Width);

        // 每一段都在，顺序不变，拼回去正好是牌子上的可见文字
        Assert.Equal(SignText.PlainText(text), string.Concat(layout.Runs.Select(run => run.Text)));
        Assert.Equal(1, layout.LineCount);
        Assert.False(layout.Truncated);
    }

    /// <summary>量的尺寸必须覆盖包括芯片前文字在内的所有段。</summary>
    [Fact]
    public void ContentWidthCoversTheTextBeforeAChip()
    {
        var layout = Build("Copy the PIN {{482913}}");

        var expected = layout.Runs.Sum(run => run.Width) + SignChipStyle.Default.Margin;

        Assert.Equal(expected, layout.ContentWidth, 3);
        Assert.True(layout.ContentWidth >= layout.Runs[0].Width);
    }

    /// <summary>芯片整体不拆开：放不下就整块挪到下一行，且不越出正文区。</summary>
    [Fact]
    public void AChipMovesToTheNextLineInsteadOfBeingSplit()
    {
        var layout = Build("ab {{1234567890}}", width: 60f);

        var text = Assert.Single(layout.Runs.Where(run => !run.IsValue));
        var chip = Assert.Single(layout.Chips);

        Assert.Equal(0, text.Line);
        Assert.Equal(0f, chip.X);
        Assert.Equal(LineHeight, chip.Y);
        Assert.Equal(2, layout.LineCount);

        foreach (var run in layout.Runs)
        {
            Assert.True(run.Right <= 60f, $"run '{run.Text}' 越出正文区: {run.Right}");
        }
    }

    /// <summary>超出允许行数时停下并置 Truncated，行数按真正排下的行算。</summary>
    [Fact]
    public void TruncatedWhenTheTextNeedsMoreLinesThanAllowed()
    {
        var layout = Build("aaaaaaaaaa bbbbbbbbbb cccccccccc", width: 105f, maxLines: 2);

        Assert.True(layout.Truncated);
        Assert.Equal(2, layout.LineCount);
        Assert.Equal("aaaaaaaaaa bbbbbbbbbb ", string.Concat(layout.Runs.Select(run => run.Text)));
    }

    /// <summary>没有芯片的牌子也走同一条路：纯文字全部保留。</summary>
    [Fact]
    public void PlainTextWithoutChipsIsKeptWhole()
    {
        const string text = "Franz, click Save";

        var layout = Build(text);

        Assert.Empty(layout.Chips);
        Assert.Equal(text, string.Concat(layout.Runs.Select(run => run.Text)));
        Assert.All(layout.Runs, run => Assert.Equal(-1, run.ValueIndex));
    }
}
