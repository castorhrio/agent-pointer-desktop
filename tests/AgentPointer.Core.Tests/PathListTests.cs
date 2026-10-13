using AgentPointer.Core.Cli;

namespace AgentPointer.Core.Tests;

public class PathListTests
{
    [Fact]
    public void 空路径直接写入新目录()
    {
        var (newValue, changed) = PathList.Add(null, @"C:\tools\agentpointer");
        Assert.True(changed);
        Assert.Equal(@"C:\tools\agentpointer", newValue);
    }

    [Fact]
    public void 缺少结尾分号时补一个()
    {
        var (newValue, changed) = PathList.Add(@"C:\Windows;C:\tools", @"D:\agentpointer");
        Assert.True(changed);
        Assert.Equal(@"C:\Windows;C:\tools;D:\agentpointer", newValue);
    }

    [Fact]
    public void 已有结尾分号时不补出空项()
    {
        var (newValue, changed) = PathList.Add(@"C:\Windows;", @"D:\agentpointer");
        Assert.True(changed);
        Assert.Equal(@"C:\Windows;D:\agentpointer", newValue);
    }

    [Fact]
    public void 重复目录不修改原值()
    {
        const string current = @"C:\Windows;C:\tools";
        var (newValue, changed) = PathList.Add(current, @"C:\tools");
        Assert.False(changed);
        Assert.Equal(current, newValue);
    }

    [Fact]
    public void 大小写不同的重复目录也算已存在()
    {
        var (newValue, changed) = PathList.Add(@"C:\Tools", @"c:\tools");
        Assert.False(changed);
        Assert.Equal(@"C:\Tools", newValue);
    }

    [Theory]
    [InlineData(@"C:\tools\")]
    [InlineData(@"C:\tools\\")]
    [InlineData(@"  C:\tools  ")]
    [InlineData(@"C:\tools/")]
    public void 结尾分隔符与首尾空白不影响判重(string duplicate)
    {
        const string current = @"C:\Windows;C:\tools";
        var (newValue, changed) = PathList.Add(current, duplicate);
        Assert.False(changed);
        Assert.Equal(current, newValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白目录被忽略(string directory)
    {
        var (newValue, changed) = PathList.Add(@"C:\Windows", directory);
        Assert.False(changed);
        Assert.Equal(@"C:\Windows", newValue);
    }

    [Fact]
    public void 原值里的变量引用原样保留()
    {
        var (newValue, changed) = PathList.Add(@"%USERPROFILE%\bin;", @"C:\tools");
        Assert.True(changed);
        Assert.Equal(@"%USERPROFILE%\bin;C:\tools", newValue);
    }

    [Fact]
    public void 加入时去掉目录首尾空白()
    {
        var (newValue, changed) = PathList.Add(string.Empty, @"  C:\tools  ");
        Assert.True(changed);
        Assert.Equal(@"C:\tools", newValue);
    }

    [Fact]
    public void 空项不参与判重()
    {
        var (newValue, changed) = PathList.Add(";;", @"C:\tools");
        Assert.True(changed);
        Assert.Equal(@";;C:\tools", newValue);
    }
}
