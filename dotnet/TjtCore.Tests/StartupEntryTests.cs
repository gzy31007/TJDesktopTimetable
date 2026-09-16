using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 开机自启的命令行约定（`StartupEntry`）。
///
/// <para>这些断言针对的是"真机上肉眼看不出来"的失败模式：注册表里值在、命令行被空格截断、
/// 或关掉之后没删干净（设置是关的，开机照样起来）。</para>
/// </summary>
public class StartupEntryTests
{
    [Fact]
    public void 命令行总是给exe路径加引号()
    {
        Assert.Equal("\"C:\\tjt\\Tjt.App.exe\"", StartupEntry.CommandLine(@"C:\tjt\Tjt.App.exe"));
        Assert.Equal("\"C:\\Program Files\\TJDesktopTimetable\\Tjt.App.exe\"",
            StartupEntry.CommandLine(@"C:\Program Files\TJDesktopTimetable\Tjt.App.exe"));
    }

    [Fact]
    public void 已带引号的路径不会再包一层()
    {
        Assert.Equal("\"C:\\tjt\\Tjt.App.exe\"", StartupEntry.CommandLine("\"C:\\tjt\\Tjt.App.exe\""));
        Assert.Equal("\"C:\\tjt\\Tjt.App.exe\"", StartupEntry.CommandLine("  \"C:\\tjt\\Tjt.App.exe\"  "));
    }

    [Fact]
    public void 路径里的中文与空格原样保留()
    {
        Assert.Equal("\"D:\\桌面\\课表 挂件\\Tjt.App.exe\"", StartupEntry.CommandLine(@"D:\桌面\课表 挂件\Tjt.App.exe"));
    }

    [Fact]
    public void 空路径直接拒绝()
    {
        Assert.Throws<ArgumentException>(() => StartupEntry.CommandLine("   "));
    }

    [Fact]
    public void 比较时忽略大小写与首尾空白()
    {
        Assert.True(StartupEntry.Matches("\"c:\\TJT\\tjt.app.EXE\" ", "\"C:\\tjt\\Tjt.App.exe\""));
        Assert.False(StartupEntry.Matches("\"C:\\tjt\\Tjt.App.exe\" --smoke", "\"C:\\tjt\\Tjt.App.exe\""));
        Assert.False(StartupEntry.Matches(null, "\"C:\\tjt\\Tjt.App.exe\""));
    }

    [Fact]
    public void 开启时写入引号路径_关闭时要求删除()
    {
        var expected = "\"C:\\tjt\\Tjt.App.exe\"";
        Assert.Equal(expected, StartupEntry.DesiredValue(true, @"C:\tjt\Tjt.App.exe"));
        Assert.Null(StartupEntry.DesiredValue(false, @"C:\tjt\Tjt.App.exe"));
    }

    [Fact]
    public void 值名与键路径是约定值()
    {
        Assert.Equal("TJDesktopTimetable", StartupEntry.ValueName);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupEntry.RunKeyPath);
    }
}
