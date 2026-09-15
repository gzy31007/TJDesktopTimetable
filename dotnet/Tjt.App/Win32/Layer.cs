using Microsoft.UI.Xaml;
using Tjt.Widget;
using WinRT.Interop;

namespace Tjt.App.Win32;

/// <summary>层级层状态（日志/自检用）。</summary>
/// <param name="Enabled">是否处于"贴桌面层"模式。</param>
/// <param name="Owner">当前 owner 句柄。</param>
/// <param name="Host">解析到的桌面宿主句柄。</param>
/// <param name="Visible">是否可见且未最小化。</param>
/// <param name="NoActivate">是否戴着 WS_EX_NOACTIVATE（正常应为 false）。</param>
internal sealed record LayerState(bool Enabled, nint Owner, nint Host, bool Visible, bool NoActivate);

/// <summary>
/// 挂件窗口的层级编排（对应 Electron 侧 <c>layer.ts</c>，2026-09-14 那版重写的结论）。
///
/// <para><b>职责划分</b>：</para>
/// <list type="number">
/// <item><description><b>静息落点</b>由 <see cref="RestingPolicy"/> 三选一，不再一律置底；</description></item>
/// <item><description><b>没有每秒重压</b>：只有一个 5 秒 owner 巡检，owner 正常时只做一次
/// <c>GetWindowLongPtrW</c> 读、不产生任何 z-order 变化；</description></item>
/// <item><description><b>可靠性来自事件而不是轮询</b>：Explorer 重启（<c>TaskbarCreated</c>）、
/// 显示/工作区变化（<c>WM_DISPLAYCHANGE</c> / <c>WM_SETTINGCHANGE</c>）都会作废宿主缓存并重新静息；</description></item>
/// <item><description><b>交互期临时浮起</b>，交互结束后按前台重新落点（成对调用）。</description></item>
/// </list>
///
/// <para>Electron 侧的教训（别再引入）：不要同时有多个机制改 z-order、不要在交互期动 owner、
/// 不要"修 Shell last active popup"（那等于周期性抢前台）。</para>
/// </summary>
internal sealed class DesktopLayer
{
    /// <summary>owner 巡检间隔：只兜"owner 关系丢了"这一种情况，低频足够。</summary>
    private static readonly TimeSpan PatrolInterval = TimeSpan.FromSeconds(5);

    private readonly Window _window;
    private readonly nint _hwnd;
    private readonly DispatcherTimer _patrol = new();
    private readonly List<Action> _unhooks = [];

    private bool _enabled;
    private bool _paused;
    private bool _ownerLossLogged;
    private string _disposition = "initial";

    private DesktopLayer(Window window, nint hwnd, bool enabled)
    {
        _window = window;
        _hwnd = hwnd;
        _enabled = enabled;
        _patrol.Interval = PatrolInterval;
        _patrol.Tick += (_, _) => Patrol();
    }

    /// <summary>最近一次落点（日志/自检用）。</summary>
    public string LastDisposition => _disposition;

    /// <summary>是否处于贴桌面层模式。</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// 把窗口挂到桌面层级。
    /// </summary>
    /// <param name="window">挂件窗口（必须已经创建）。</param>
    /// <param name="enabled">是否贴桌面层（false = 纯置底、不挂 owner）。</param>
    /// <param name="watchMessages">是否订阅显示 / Explorer 变化消息。</param>
    public static DesktopLayer Attach(Window window, bool enabled, bool watchMessages = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = WindowNative.GetWindowHandle(window);
        var layer = new DesktopLayer(window, hwnd, enabled);

        Resting.MarkToolWindow(hwnd);
        layer.ApplyRestingLayer("initial");

        if (watchMessages) layer.WatchMessages();

        layer._patrol.Start();
        AppLog.Line($"[layer] attach enabled={enabled} hwnd=0x{hwnd:X} disposition={layer._disposition}");
        return layer;
    }

    /// <summary>暂停 owner 巡检（窗口被主动隐藏 / 拖动缩放期间调用）。</summary>
    public void Pause()
    {
        _paused = true;
        _patrol.Stop();
    }

    /// <summary>恢复巡检并立即重新静息一次。</summary>
    public void Resume(string reason)
    {
        _paused = false;
        if (!_patrol.IsEnabled) _patrol.Start();
        Resting.EnsureVisible(_hwnd);
        ApplyRestingLayer(reason);
    }

    /// <summary>交互开始：临时浮起（并兜底清一次 NOACTIVATE，防止将来有人给静息态加回去）。</summary>
    public void SuspendForInteraction(string reason)
    {
        Pause();
        if (Resting.IsNoActivate(_hwnd))
        {
            Resting.SetNoActivate(_hwnd, false);
            AppLog.Line("[layer] 交互开始：已清除 NOACTIVATE（兜底）");
        }

        Resting.HoldTemporaryTopMost(_hwnd);
        AppLog.Line($"[layer] 交互浮起 {reason}");
    }

    /// <summary>交互结束：重新静息（按前台决定落点）。</summary>
    public void ResumeAfterInteraction(string reason) => Resume(reason);

    /// <summary>
    /// 重建桌面层级：作废宿主缓存后重新静息（Explorer 重启 / 显示拓扑变化）。
    /// </summary>
    public void Refresh(string reason)
    {
        DesktopHost.Invalidate();
        ApplyRestingLayer(reason);
    }

    /// <summary>解除控制（退出前调用）。**不隐藏窗口**（隐藏是另一条路）。</summary>
    public void Detach()
    {
        _patrol.Stop();
        foreach (var unhook in _unhooks) unhook();
        _unhooks.Clear();
        DesktopHost.RestoreOriginalOwner(_hwnd);
    }

    /// <summary>当前状态快照（自检用）。</summary>
    public LayerState Snapshot() => new(
        _enabled,
        DesktopHost.CurrentOwner(_hwnd),
        DesktopHost.Resolve(),
        Resting.IsVisibleAndNotMinimized(_hwnd),
        Resting.IsNoActivate(_hwnd));

    /// <summary>
    /// owner 巡检：**只在 owner 关系丢了的时候动窗口**。
    ///
    /// 需要它的场景：Explorer 重启、显示拓扑变化、其它桌面软件抢宿主之后，owner 可能被系统清掉，
    /// 此时窗口就失去了"Win+D 后仍可见"的保护。事件通道负责主要修复，这里是低频兜底。
    /// </summary>
    private void Patrol()
    {
        if (_paused || !_enabled) return;
        if (!NativeMethods.IsWindow(_hwnd)) return;
        if (DesktopHost.Resolve() == nint.Zero) return;
        if (DesktopHost.OwnerIsDesktopHost(_hwnd))
        {
            _ownerLossLogged = false;
            return;
        }

        if (!_ownerLossLogged)
        {
            _ownerLossLogged = true;
            AppLog.Line("[layer] 桌面层 owner 丢失，重新挂载（同类不再重复记录）");
        }

        ApplyRestingLayer("owner-patrol");
    }

    /// <summary>
    /// 重新静息：按当前前台窗口决定落点。这是**唯一**会改变挂件全局 z-order 的入口
    /// （除了交互期临时浮起）。
    /// </summary>
    private void ApplyRestingLayer(string reason)
    {
        if (!NativeMethods.IsWindow(_hwnd)) return;

        var foreground = DesktopHost.ForegroundRoot(NativeMethods.GetForegroundWindow());
        var disposition = RestingPolicy.Decide(new RestingInputs(
            HasForeground: foreground != nint.Zero,
            ForegroundIsDesktopShell: DesktopHost.IsDesktopShellWindow(foreground),
            ForegroundIsSelf: foreground == _hwnd,
            ForegroundIsOwnApp: DesktopHost.IsOwnProcess(foreground)));

        _disposition = disposition.ToString();

        switch (disposition)
        {
            case RestingDisposition.DesktopBottom:
                if (_enabled)
                {
                    AttachOwnerAndBottom(reason);
                }
                else
                {
                    // 用户关掉了桌面层：保持无 owner，纯置底
                    Resting.ClearTopMost(_hwnd);
                    Resting.PushToBottom(_hwnd);
                }

                break;

            case RestingDisposition.BehindForeground:
                // 保住 owner（Win+D 保护），但不要压到底：插到当前前台之后
                AttachOwnerOnly();
                if (foreground != nint.Zero) Resting.PlaceBehind(_hwnd, foreground);
                break;

            case RestingDisposition.PreservePeerOrder:
                // 前台是我们自己：只确保 owner，不动全局层级
                AttachOwnerOnly();
                break;
        }

        var state = Snapshot();
        AppLog.Line($"[layer] 静息 {reason} disposition={disposition} owner=0x{state.Owner:X} host=0x{state.Host:X} visible={state.Visible}");
    }

    /// <summary>
    /// 把窗口放回桌面层：挂 owner + 置底。
    ///
    /// 没有可用宿主时（Explorer 还没起来）**不挂 owner**，退化为纯置底，
    /// 下一次巡检 / 事件通道会再试挂载。这里不戴 <c>WS_EX_NOACTIVATE</c>（见 <c>Resting</c> 的说明）。
    /// </summary>
    private void AttachOwnerAndBottom(string reason)
    {
        if (!DesktopHost.Attach(_hwnd, out var detail))
        {
            AppLog.Line($"[layer] 桌面宿主不可用，回退为无 owner 置底（reason={reason}）：{detail}");
            Resting.ClearTopMost(_hwnd);
            Resting.PushToBottom(_hwnd);
            return;
        }

        Resting.PushToBottom(_hwnd);
    }

    /// <summary>只确保 owner 关系（不改变全局 z-order）。</summary>
    private void AttachOwnerOnly()
    {
        if (!_enabled) return;
        if (DesktopHost.OwnerIsDesktopHost(_hwnd)) return;
        DesktopHost.Attach(_hwnd, out _);
    }

    /// <summary>
    /// 订阅桌面层级相关的窗口消息（事件驱动，替代轮询兜底）。
    ///
    /// 三条通道对应三类会把宿主关系打坏的系统事件：
    /// <c>WM_DISPLAYCHANGE</c>（分辨率/拓扑变化）、<c>WM_SETTINGCHANGE</c>（工作区变化，去抖）、
    /// <c>TaskbarCreated</c>（Explorer 重启，旧 DefView 句柄已失效）。
    /// </summary>
    private void WatchMessages()
    {
        Hook(NativeMethods.Constants.WmDisplayChange, "display-change");
        Hook(NativeMethods.Constants.WmSettingChange, "setting-change");
        try
        {
            var taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (taskbarCreated != 0) Hook(taskbarCreated, "explorer-restart");
        }
        catch (Exception ex)
        {
            AppLog.Line($"[layer] 注册 TaskbarCreated 失败：{ex.Message}");
        }
    }

    private void Hook(uint message, string reason)
    {
        try
        {
            // WinUI 的 Window 没有 Electron 那种 hookWindowMessage，所以自己 subclass：
            // 见 MessageHook（SetWindowLongPtr(GWLP_WNDPROC) + 原窗口过程转发）。
            _unhooks.Add(MessageHook.Subscribe(_hwnd, message, () => Refresh(reason)));
        }
        catch (Exception ex)
        {
            AppLog.Line($"[layer] 订阅窗口消息失败 message=0x{message:X}：{ex.Message}");
        }
    }
}
