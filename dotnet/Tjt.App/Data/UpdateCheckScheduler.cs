namespace Tjt.App.Data;

/// <summary>Windows 更新检查的生命周期。回调在启动它的 UI 上下文中执行。</summary>
internal sealed class UpdateCheckScheduler(
    Func<bool> enabled,
    Func<CancellationToken, Task> check,
    Action<Exception> onError,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private Task? _loop;
    private int _checking;

    public Task Start() => _loop ??= RunAsync();

    private async Task RunAsync()
    {
        try
        {
            await _delay(StartupDelay, _lifetime.Token);
            while (!_lifetime.IsCancellationRequested)
            {
                if (enabled()) await CheckNowAsync();
                await _delay(Interval, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task CheckNowAsync()
    {
        if (_lifetime.IsCancellationRequested || Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try { await check(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { onError(ex); }
        finally { Volatile.Write(ref _checking, 0); }
    }

    public void Dispose() => _lifetime.Cancel();
}
