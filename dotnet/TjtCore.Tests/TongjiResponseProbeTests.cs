using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 同济响应探测（<see cref="TongjiResponseProbe"/>）的验收 —— 逐条对齐 TS 侧
/// <c>looksLikeTimetable</c> 的分支，因为这段文案会**原样显示给用户**，
/// 分支走错就等于给错提示（"粘错请求"是这条链路最常见的失败）。
/// </summary>
public class TongjiResponseProbeTests
{
    [Fact]
    public void 有selectedCourses就认()
    {
        var verdict = TongjiResponseProbe.Inspect("""{"code":200,"data":{"selectedCourses":[{},{}]}}""");
        Assert.True(verdict.Ok);
        Assert.Equal("selectedCourses 2 门", verdict.Note);
    }

    [Fact]
    public void data是数组也认()
    {
        var verdict = TongjiResponseProbe.Inspect("""{"code":200,"data":[{"a":1},{"a":2},{"a":3}]}""");
        Assert.True(verdict.Ok);
        Assert.Equal("data 数组 3 条", verdict.Note);
    }

    [Fact]
    public void 只有message没有data时报服务端原话()
    {
        var verdict = TongjiResponseProbe.Inspect("""{"code":500,"message":"登录状态已失效"}""");
        Assert.False(verdict.Ok);
        Assert.Contains("登录状态已失效", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void data里没有selectedCourses时列出字段名()
    {
        var verdict = TongjiResponseProbe.Inspect("""{"data":{"studentId":"x","termId":1}}""");
        Assert.False(verdict.Ok);
        Assert.Contains("studentId", verdict.Note, StringComparison.Ordinal);
        Assert.Contains("termId", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 顶层没有data时列出顶层字段名()
    {
        var verdict = TongjiResponseProbe.Inspect("""{"code":200,"foo":1,"bar":2}""");
        Assert.False(verdict.Ok);
        Assert.Contains("code", verdict.Note, StringComparison.Ordinal);
        Assert.Contains("foo", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 字段名最多列八个()
    {
        var verdict = TongjiResponseProbe.Inspect(
            """{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6,"g":7,"h":8,"i":9,"j":10}""");
        Assert.False(verdict.Ok);
        Assert.DoesNotContain("i,", verdict.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("j", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 非JSON与空白都判不合格()
    {
        Assert.False(TongjiResponseProbe.Inspect("<html><body>登录</body></html>").Ok);
        Assert.False(TongjiResponseProbe.Inspect("").Ok);
        Assert.False(TongjiResponseProbe.Inspect(null).Ok);
        // 顶层是数组（不是对象）也不是我们要的形状
        Assert.False(TongjiResponseProbe.Inspect("[1,2,3]").Ok);
    }

    [Fact]
    public void 过长的服务端message被截断()
    {
        var message = new string('长', 200);
        var verdict = TongjiResponseProbe.Inspect($$"""{"message":"{{message}}"}""");
        Assert.False(verdict.Ok);
        // "服务端返回：" 前缀 + 最多 80 个字符
        Assert.Equal(6 + 80, verdict.Note.Length);
    }
}
