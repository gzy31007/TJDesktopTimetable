using Tjt.App.Data;
using Xunit;

namespace Tjt.Core.Tests;

public class UpdateCheckSchedulerTests
{
    [Fact]
    public async Task 持续运行每六小时再次检查而无需重启()
    {
        var clock = new CheckClock();
        var checks = new List<TimeSpan>();
        using var scheduler = new UpdateCheckScheduler(() => true, _ =>
        {
            checks.Add(clock.Now);
            return Task.CompletedTask;
        }, ex => throw ex, clock.Delay);
        var loop = scheduler.Start();

        await clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(checks);
        await clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([TimeSpan.FromSeconds(12)], checks);
        await clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        Assert.Single(checks);
        await clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([TimeSpan.FromSeconds(12), TimeSpan.FromHours(6) + TimeSpan.FromSeconds(12)], checks);
        await clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(3, checks.Count);
        scheduler.Dispose();
        await loop;
    }

    [Fact]
    public async Task 自动和手动检查重叠时只执行一项并在退出后拒绝新检查()
    {
        var clock = new CheckClock();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var calls = 0;
        using var scheduler = new UpdateCheckScheduler(() => true, async token =>
        {
            calls++;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }, ex => throw ex, clock.Delay);
        var loop = scheduler.Start();
        var first = scheduler.CheckNowAsync();
        await started.Task;
        await clock.Advance(TimeSpan.FromSeconds(12));
        var overlapping = scheduler.CheckNowAsync();
        Assert.Equal(1, calls);
        scheduler.Dispose();
        await Task.WhenAll(first, overlapping, loop);
        await scheduler.CheckNowAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 每轮读取当前开关关闭时不自动检查但仍可手动检查()
    {
        var clock = new CheckClock();
        var enabled = false;
        var checks = 0;
        using var scheduler = new UpdateCheckScheduler(() => enabled, _ =>
        {
            checks++;
            return Task.CompletedTask;
        }, ex => throw ex, clock.Delay);
        var loop = scheduler.Start();
        Assert.Same(loop, scheduler.Start());
        await clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(0, checks);
        await scheduler.CheckNowAsync();
        Assert.Equal(1, checks);
        enabled = true;
        await clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(2, checks);
        enabled = false;
        await clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(2, checks);
        enabled = true;
        await clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(3, checks);
        scheduler.Dispose();
        await loop;
    }

    [Fact]
    public async Task 单轮失败保持静默且不阻断下轮检查()
    {
        var clock = new CheckClock();
        var checks = 0;
        var errors = new List<Exception>();
        using var scheduler = new UpdateCheckScheduler(() => true, _ =>
        {
            checks++;
            return checks == 1 ? Task.FromException(new IOException("synthetic offline")) : Task.CompletedTask;
        }, errors.Add, clock.Delay);
        var loop = scheduler.Start();
        await clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Single(errors);
        await clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(2, checks);
        Assert.Single(errors);
        scheduler.Dispose();
        await loop;
    }

    [Fact]
    public async Task 在启动等待期间退出会取消延迟且不再发起检查()
    {
        var clock = new CheckClock();
        var checks = 0;
        using var scheduler = new UpdateCheckScheduler(() => true, _ =>
        {
            checks++;
            return Task.CompletedTask;
        }, ex => throw ex, clock.Delay);
        var loop = scheduler.Start();
        scheduler.Dispose();
        await loop;
        await clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, checks);
    }

    // 只替换时间边界，调度与回调均使用生产实现。到期续体内联，避免真实等待与轮询。
    private sealed class CheckClock
    {
        private readonly List<(TimeSpan Due, TaskCompletionSource Completion, CancellationTokenRegistration Registration)> _pending = [];
        public TimeSpan Now { get; private set; }

        public Task Delay(TimeSpan duration, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource();
            var registration = cancellation.Register(() => completion.TrySetCanceled(cancellation));
            _pending.Add((Now + duration, completion, registration));
            return completion.Task;
        }

        public async Task Advance(TimeSpan duration)
        {
            Now += duration;
            foreach (var pending in _pending.Where(p => p.Due <= Now).ToArray())
            {
                _pending.Remove(pending);
                pending.Registration.Dispose();
                pending.Completion.TrySetResult();
                await Task.Yield();
            }
        }
    }
}
