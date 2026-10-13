using AgentPointer.Core.Targeting;

namespace AgentPointer.Core.Tests;

/// <summary>
/// <see cref="WindowPicker"/> 契约：进程名/描述命中；给了标题就必须真的有窗口标题包含它
/// （对不上返回 null，也就是退出码 3），只有没给标题时才退回"同进程里最大的窗口"。
/// </summary>
public class WindowPickerTests
{
    private static WindowCandidate W(
        int order, string? process, string title, long area = 1000, string? description = null) =>
        new(order, process, description, title, area);

    private static WindowCandidate? Pick(string query, params WindowCandidate[] windows) =>
        WindowPicker.Pick(windows, query);

    [Fact]
    public void 没有标题时选同进程里面积最大的窗口()
    {
        var small = W(0, "chrome", "New Tab", 100);
        var big = W(1, "chrome", "Sourdough - Wikipedia", 9000);

        Assert.Equal(big, Pick("Google Chrome", small, big));
    }

    [Fact]
    public void 标题命中时按标题收窄()
    {
        var other = W(0, "chrome", "New Tab", 9000);
        var wanted = W(1, "chrome", "Inbox - Gmail", 10);

        Assert.Equal(wanted, Pick("Google Chrome:Inbox", other, wanted));
    }

    [Fact]
    public void 标题对不上就返回null而不是换一个窗口()
    {
        var windows = new[] { W(0, "chrome", "New Tab", 9000), W(1, "chrome", "Sourdough", 100) };

        Assert.Null(Pick("Google Chrome:DoesNotExist", windows));
    }

    [Fact]
    public void 标题大小写不敏感()
    {
        var wanted = W(1, "chrome", "Inbox - Gmail", 10);

        Assert.Equal(wanted, Pick("Google Chrome:inbox", W(0, "chrome", "New Tab", 9000), wanted));
    }

    [Fact]
    public void 别名按顺序命中第一个有窗口的候选()
    {
        var terminal = W(0, "WindowsTerminal", "Terminal", 100);
        var powershell = W(1, "powershell", "PowerShell", 200);

        Assert.Equal(terminal, Pick("Terminal", terminal, powershell));
        Assert.Equal(powershell, Pick("Terminal", powershell));
    }

    [Fact]
    public void FileDescription也参与匹配()
    {
        var window = W(0, "somehost", "Editor", description: "Visual Studio Code");

        Assert.Equal(window, Pick("Visual Studio Code", window));
    }

    [Fact]
    public void 进程名一个都没命中时才拿查询串当标题()
    {
        var window = W(0, "explorer", "big-arrow-on-the-screen", 100);

        Assert.Equal(window, Pick("big-arrow-on-the-screen", window));
    }

    [Fact]
    public void 进程命中但标题对不上时不再退回标题兜底()
    {
        var window = W(0, "chrome", "Inbox - Gmail", 100);

        Assert.Null(Pick("Google Chrome:DoesNotExist", window));
    }

    [Fact]
    public void safari与edge没有chromium兜底()
    {
        var chrome = W(0, "chrome", "New Tab", 9000);

        Assert.Null(Pick("Safari", chrome));
        Assert.Null(Pick("Edge", chrome));
        Assert.Null(Pick("Microsoft Edge", chrome));
    }

    [Fact]
    public void 面积相同时按枚举顺序稳定挑选()
    {
        var first = W(0, "chrome", "A", 100);
        var second = W(1, "chrome", "B", 100);

        Assert.Equal(first, Pick("Google Chrome", first, second));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空查询返回null(string? query)
    {
        Assert.Null(WindowPicker.Pick(new[] { W(0, "chrome", "New Tab") }, query!));
    }
}
