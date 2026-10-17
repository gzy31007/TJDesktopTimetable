using Tjt.App.Data;
using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 导入结果文案的回归（issue 13）：<b>「应用」= 整份替换 + 落盘成功</b>。
///
/// <para>测的是 <c>dotnet/Tjt.App/Data/ImportService.cs</c> 本身 —— 它由 csproj 以 <c>Link</c>
/// <b>同源链接</b>进本工程（没有 WinUI 依赖），外壳静态类由
/// <c>ShellStubsForImportService.cs</c> 桩住。复制副本会立刻与出货代码不同步，所以不复制。</para>
/// </summary>
public sealed class ImportServiceMessageTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>只记录"有没有调落盘、调了几次"的假外壳。</summary>
    private sealed class Recorder
    {
        public int ApplyCalls { get; private set; }

        public ImportService Build(bool saveSucceeds) => new(
            (_, _) =>
            {
                ApplyCalls++;
                return saveSucceeds;
            },
            () => true,
            () => "内置示例课表");
    }

    [Fact]
    public void 成功时说清已整份替换并保存到本机()
    {
        var recorder = new Recorder();
        var outcome = recorder.Build(saveSucceeds: true)
            .ImportText(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), null, null);

        Assert.True(outcome.Ok);
        Assert.Equal(1, recorder.ApplyCalls);
        Assert.Contains("门课程", outcome.Message);
        Assert.Contains("已整份替换为", outcome.Message);
        Assert.Contains("保存到本机", outcome.Message);
    }

    [Fact]
    public void 保存失败时不能报成功也不能说已替换()
    {
        var recorder = new Recorder();
        var outcome = recorder.Build(saveSucceeds: false)
            .ImportText(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), null, null);

        Assert.False(outcome.Ok);
        Assert.Equal(1, recorder.ApplyCalls); // 解析是成功的，失败在落盘这一步
        Assert.DoesNotContain("已整份替换为", outcome.Message);
        Assert.DoesNotContain("保存到本机", outcome.Message);
        Assert.Contains("新表没有保存成功", outcome.Message);
        Assert.Contains("仍是旧表", outcome.Message);
    }

    [Fact]
    public void 重新载入只读本机且不承诺联网()
    {
        var outcome = new Recorder().Build(saveSucceeds: true).ReloadTimetable();

        Assert.True(outcome.Ok);
        Assert.Contains("本机", outcome.Message);
        Assert.Contains("没有向学校取新数据", outcome.Message);
    }

    /// <summary>
    /// 合成的最小个人课表响应：能识别成同济个人课表（<c>data.selectedCourses</c> 存在），但一门课都没有。
    /// 刻意内联而不新建 fixture —— <c>dotnet/fixtures/</c> 是黄金数据真源，不该为一个边界往里塞文件。
    /// </summary>
    private const string EmptySelectedCourses = """{"code":0,"data":{"calendarId":"122","selectedCourses":[]}}""";

    [Fact]
    public void 零课程时不调用apply且说清课表没被改动()
    {
        var recorder = new Recorder();
        var outcome = recorder.Build(saveSucceeds: true).ImportText(EmptySelectedCourses, null, null);

        Assert.False(outcome.Ok);
        Assert.Equal(0, recorder.ApplyCalls); // 这个分支根本不该走到落盘
        Assert.Contains("没有解析出任何课程", outcome.Message);
        Assert.Contains("当前课表保持不变", outcome.Message);
        Assert.DoesNotContain("已整份替换为", outcome.Message);
    }

    [Fact]
    public void 内置登录捕获成功时也走同一句保存文案()
    {
        var recorder = new Recorder();
        var outcome = recorder.Build(saveSucceeds: true)
            .ApplyCapturedResponse(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), "122");

        Assert.True(outcome.Ok);
        Assert.Equal(1, recorder.ApplyCalls);
        Assert.Contains("已整份替换为", outcome.Message);
        Assert.Contains("保存到本机", outcome.Message);
    }

    [Fact]
    public void 内置登录捕获保存失败时同样不能报成功()
    {
        var recorder = new Recorder();
        var outcome = recorder.Build(saveSucceeds: false)
            .ApplyCapturedResponse(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), "122");

        Assert.False(outcome.Ok);
        Assert.Equal(1, recorder.ApplyCalls); // 解析成功、落盘失败
        Assert.Contains("新表没有保存成功", outcome.Message);
        Assert.Contains("仍是旧表", outcome.Message);
        Assert.DoesNotContain("已整份替换为", outcome.Message);
    }

    // ── CapturedResponseCompleted：内置登录窗口交回结果时通知设置页 ────────────────────

    private static ImportService Service(bool saveSucceeds, out Func<int> applied)
    {
        var calls = 0;
        applied = () => calls;
        return new ImportService(
            (_, _) =>
            {
                calls++;
                return saveSucceeds;
            },
            () => true,
            () => "已导入的课表");
    }

    [Fact]
    public void 捕获成功会发一次事件且带回同一条结果()
    {
        var service = Service(saveSucceeds: true, out _);
        var seen = new List<ImportOutcome>();
        service.CapturedResponseCompleted += seen.Add;

        var returned = service.ApplyCapturedResponse(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), "122");

        var single = Assert.Single(seen);
        Assert.Same(returned, single);
        Assert.True(single.Ok);
    }

    // 设置页也要知道"这次失败"，否则它会一直停在旧的成功状态
    [Fact]
    public void 空响应这种失败也会发一次事件()
    {
        var service = Service(saveSucceeds: true, out _);
        var seen = new List<ImportOutcome>();
        service.CapturedResponseCompleted += seen.Add;

        var returned = service.ApplyCapturedResponse(string.Empty, "122");

        Assert.False(returned.Ok);
        var single = Assert.Single(seen);
        Assert.Same(returned, single);
        Assert.Contains("空", single.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 观察者抛异常不会把已保存的成功改成失败()
    {
        var service = Service(saveSucceeds: true, out var applied);
        service.CapturedResponseCompleted += _ => throw new InvalidOperationException("界面订阅者炸了");

        var outcome = service.ApplyCapturedResponse(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), "122");

        // 课表已经落盘了：返回值必须还是成功，否则用户会以为要重来一次
        Assert.True(outcome.Ok);
        Assert.Contains("保存到本机", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(1, applied());
    }

    [Fact]
    public void 一个观察者抛异常不影响其他观察者()
    {
        var service = Service(saveSucceeds: true, out _);
        var seen = new List<ImportOutcome>();
        service.CapturedResponseCompleted += _ => throw new InvalidOperationException("第一个订阅者炸了");
        service.CapturedResponseCompleted += seen.Add;

        var outcome = service.ApplyCapturedResponse(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), "122");

        Assert.True(outcome.Ok);
        Assert.Same(outcome, Assert.Single(seen));
    }

    [Fact]
    public void 退订后不再收到通知()
    {
        var service = Service(saveSucceeds: true, out _);
        var seen = new List<ImportOutcome>();
        Action<ImportOutcome> handler = seen.Add;

        service.CapturedResponseCompleted += handler;
        service.CapturedResponseCompleted -= handler;
        service.ApplyCapturedResponse(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), "122");

        Assert.Empty(seen);
    }

    [Fact]
    public void 粘贴导入这条路不发这个事件()
    {
        // 事件只属于"内置登录窗口交回响应"；粘贴/文件导入自己就会当场铺结果，重复通知只会让状态闪一下
        var service = Service(saveSucceeds: true, out _);
        var seen = new List<ImportOutcome>();
        service.CapturedResponseCompleted += seen.Add;

        service.ImportText(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), null, null);

        Assert.Empty(seen);
    }

    // ── LastCapturedOutcome：构造页面与订阅之间那次结果不能丢 ────────────────

    private static ImportOutcome Capture(ImportService service) =>
        service.ApplyCapturedResponse(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), "122");

    [Fact]
    public void 捕获结果留在缓存里供刚挂载的页面补读()
    {
        var service = Service(saveSucceeds: true, out _);

        var outcome = Capture(service);

        Assert.Same(outcome, service.LastCapturedOutcome);
    }

    [Fact]
    public void 失败的捕获同样进缓存()
    {
        var service = Service(saveSucceeds: true, out _);

        var outcome = service.ApplyCapturedResponse(string.Empty, "122");

        Assert.False(outcome.Ok);
        Assert.Same(outcome, service.LastCapturedOutcome);
    }

    [Fact]
    public void 粘贴导入开始时清掉捕获缓存()
    {
        var service = Service(saveSucceeds: true, out _);
        Capture(service);

        service.ImportText(File.ReadAllText(Fixture("tongji-2026-1-personal.json")), null, null);

        Assert.Null(service.LastCapturedOutcome);
    }

    [Fact]
    public void 重新载入开始时清掉捕获缓存()
    {
        var service = Service(saveSucceeds: true, out _);
        Capture(service);

        service.ReloadTimetable();

        Assert.Null(service.LastCapturedOutcome);
    }

    [Fact]
    public void 清空课表开始时清掉捕获缓存()
    {
        var service = Service(saveSucceeds: true, out _);
        Capture(service);

        service.ClearTimetable();

        Assert.Null(service.LastCapturedOutcome);
    }

    [Fact]
    public async Task 抓取还没出结果就先清掉捕获缓存()
    {
        var service = Service(saveSucceeds: true, out _);
        Capture(service);
        Assert.NotNull(service.LastCapturedOutcome);

        // 这段测试不联网（SchoolFetcher 是桩，直接抛）：要验的是"开始就清"，不是抓取成败
        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.FetchAsync("GET https://1.tongji.edu.cn/api/x HTTP/1.1\r\nCookie: a=b\r\n\r\n"));

        Assert.Null(service.LastCapturedOutcome);
    }
}
