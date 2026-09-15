using Microsoft.UI.Xaml;
using Tjt.Widget;

namespace Tjt.App.Win32;

/// <summary>
/// 窗口缩放 —— **自实现**（与 <see cref="WindowDrag"/> 同一套做法）。
///
/// <para><b>光标不在这里设</b>：Win32 侧 <c>SetCursor</c> 会被窗口过程/渲染层在
/// <c>WM_SETCURSOR</c> 里按类光标重置（真机实测：返回成功、但用户看不到缩放光标）。
/// 光标改由渲染层用 WinUI 原生 <c>ProtectedCursor</c> 挂在边缘条上，见
/// <c>Tjt.Widget/CursorZones</c> 与 <c>BoardRenderer</c>。这里只管判定与搬窗口。</para>
///
/// <para><b>为什么不再走原生缩放循环</b>：原生循环要求窗口带 <c>WS_THICKFRAME</c>，
/// 而那个样式位正是挂件四边那圈 10px 非客户区框的来源（白边/黑带的根因，
/// 见 <c>MainWindow.ConfigureWindowChrome</c>）。两者不可兼得：真机实测
/// <c>IsResizable=true</c> → <c>frame=10,10</c>；<c>false</c> → <c>frame=0,0</c> 但
/// 边缘拖不动。于是按 DeskBox 的路子把缩放也自实现（它连 <c>WS_THICKFRAME</c> 都清了，
/// 缩放同样是自实现 + 引导层）。</para>
///
/// <para><b>实现</b>：16ms <c>DispatcherTimer</c> 轮询光标（与拖动同一套，实测稳）——
/// <list type="bullet">
/// <item><description>左键按下且落在带内、**且指针确实归我们**（<see cref="PointerTarget"/>）→
/// 记下起点与起始外框，开始拖拽；</description></item>
/// <item><description>每帧用 <see cref="ResizePolicy.Resolve"/>（纯函数，有单测）算新外框，
/// 一次 <c>SetWindowPos</c> 搬窗口；</description></item>
/// <item><description>左键松开 → 收尾（存位置 + 重新落点），并把光标还原成箭头。</description></item>
/// </list>
/// 「指针确实归我们」这一条是**必需**的：抓取带只看坐标，看不见窗口上面压着谁，
/// 挂件被别的窗口遮挡时，用户在遮挡窗口上按住左键拖动、光标扫过挂件边缘带就会把挂件一起缩放
/// （真机 bug）。</para>
///
/// <para><b>为什么要轮询而不是 XAML 指针事件</b>：与拖动同因 ——
/// <c>CapturePointer</c> 会在元素重建时丢掉捕获，<c>PointerMoved</c> 一条都收不到
/// （真机实测过）。轮询与元素生命周期无关。</para>
///
/// <para><b>DPI</b>：<c>GetCursorPos</c> / <c>GetWindowRect</c> / <c>SetWindowPos</c>
/// 全是物理像素，位移直接相减即可，不做任何换算；<see cref="ResizePolicy.Resolve"/>
/// 收的也是物理像素，算完由调用方落成整数外框。</para>
/// </summary>
internal sealed class WindowEdgeResize
{
    /// <summary>轮询间隔：16ms ≈ 60Hz。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>跳过一次命中测试的移动阈值（物理像素）：过滤手抖，避免每次微动都打日志。</summary>
    private const int HoverLogThreshold = 3;

    private readonly nint _hwnd;
    private readonly Func<int, int, ResizeGrip> _resolve;
    private readonly Action _onStart;
    private readonly Action _onEnd;

    private readonly DispatcherTimer _poll = new();

    private bool _resizing;
    private ResizeGrip _grip = ResizeGrip.None;

    /// <summary>
    /// 渲染层边缘热区在 <c>PointerPressed</c> 时报告的方向（按下元素自己知道是不是角）。
    /// 起拖时优先用它 —— 比"轮询从坐标反推"更准（角上不会退化成单轴）也更及时（不用等下一拍）。
    /// </summary>
    private ResizeGrip _pressedGrip = ResizeGrip.None;
    private NativeMethods.Point _originCursor;
    private WindowBounds _originWindow = new(0, 0, 0, 0);
    private int _ticks;
    private ResizeGrip _loggedHover = ResizeGrip.None;

    /// <summary>已经为用户记过一次"被遮挡，忽略起手"的日志（避免 16ms 刷屏）。</summary>
    private bool _blockedLogged;

    private WindowEdgeResize(nint hwnd, Func<int, int, ResizeGrip> resolve, Action onStart, Action onEnd)
    {
        _hwnd = hwnd;
        _resolve = resolve;
        _onStart = onStart;
        _onEnd = onEnd;
        _poll.Interval = PollInterval;
        _poll.Tick += (_, _) => Step();
        _poll.Start();

    }

    /// <summary>开始监听边缘（常驻定时器，窗口存活期间一直跑）。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="resolve">输入屏幕物理坐标、返回抓取边（见 <c>MainWindow.ResolveResizeGrip</c>）。</param>
    /// <param name="onStart">起拖时回调（暂停 owner 巡检）。</param>
    /// <param name="onEnd">结束时回调（存位置 / 重新落点）。</param>
    public static WindowEdgeResize Attach(
        nint hwnd,
        Func<int, int, ResizeGrip> resolve,
        Action onStart,
        Action onEnd)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return new WindowEdgeResize(hwnd, resolve, onStart, onEnd);
    }

    /// <summary>
    /// 渲染层报告"用户按下的热区方向"。由边缘热区的 <c>PointerPressed</c> 调用。
    ///
    /// <para>这是"先移到边缘、再按下"能起手的唯一来源：那一拍抓取边没有发生变化，
    /// 纯坐标判定不会进入起手分支。也正因为如此，<c>PointerPressed</c> 与它是天然一致的 ——
    /// 元素收不到指针事件，就不会有报告，被遮挡时也就不会误起手。</para>
    /// </summary>
    /// <param name="grip">按下的是哪条边/哪个角。</param>
    public void NotePressedGrip(ResizeGrip grip) => _pressedGrip = grip;

    /// <summary>左键当前是否按着（收尾判据）。</summary>
    private static bool IsLeftButtonDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkLButton) & 0x8000) != 0;

    /// <summary>轮询一步：先看左键是否按下决定"起拖 / 拖拽中 / 收尾"，没按下时只更新光标。</summary>
    private void Step()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        var down = IsLeftButtonDown();

        if (!_resizing)
        {
            // 左键没按下时作废"渲染层报来的方向"：否则在边缘点一下的残留值会在
            // 下一次"按在窗口中间"时被当成边缘方向，凭空起缩放。
            if (!down) _pressedGrip = ResizeGrip.None;

            var grip = _resolve(cursor.X, cursor.Y);
            var pressed = _pressedGrip;

            // 方向没变、也没有"按下那一刻报来的方向"→ 无事可做。
            // 后半条不可少：用户最常见的动作是**先移到边缘、再按下**，那一拍 grip 与上一拍
            // 完全相同，只看 grip 变化就永远起不了手（`NotePressedGrip` 就是为这条路存在的）。
            if (grip == _grip && pressed == ResizeGrip.None) return;

            _grip = grip;

            // 抓取边变化时打一行（真机排查"拖不动"时，看它就能分清是被判定漏了还是循环没跑）
            if (grip != _loggedHover)
            {
                _loggedHover = grip;
                if (grip != ResizeGrip.None)
                {
                    AppLog.Line($"[resize] 悬停 {grip}（屏幕 {cursor.X},{cursor.Y}）");
                }
            }

            // 用户可能"快速移到边上立刻按下"——那时轮询还没把 _grip 更新到新的区域，
            // 但渲染层已经报来了真实方向，用它补齐（并顺手把 _grip 纠正过来）。
            var effective = pressed != ResizeGrip.None ? pressed : grip;
            if (effective != ResizeGrip.None && down)
            {
                // 起手前必须确认这次左键真的按在挂件身上：抓取带只看坐标，看不见窗口上面
                // 压着谁 —— 挂件被别的窗口遮挡时，用户在那个窗口上按住拖动、光标扫过边缘带
                // 也会走到这里（真机 bug"被遮挡时拖动仍然生效"）。判据见 PointerTarget。
                if (!PointerTarget.CursorIsOurs(_hwnd, out var who))
                {
                    if (!_blockedLogged)
                    {
                        _blockedLogged = true;
                        AppLog.Line($"[resize] 忽略起手：指针不在挂件上（{who}，光标 {cursor.X},{cursor.Y}）");
                    }

                    _pressedGrip = ResizeGrip.None;
                    return;
                }

                _blockedLogged = false;
                _grip = effective;
                Begin(cursor, effective);
            }
            return;
        }

        _ticks++;
        if (_ticks % 120 == 0)
        {
            AppLog.Line($"[resize] poll#{_ticks} lbutton={down} grip={_grip} cursor=({cursor.X},{cursor.Y})");
        }

        // 左键松开 = 缩放结束。必须用按键状态：指针可能早就移出窗口，收不到 PointerReleased。
        if (!down)
        {
            End("left-button-up");
            return;
        }

        var target = ResizePolicy.Resolve(
            _originWindow,
            _grip,
            (double)(cursor.X - _originCursor.X),
            (double)(cursor.Y - _originCursor.Y));

        NativeMethods.SetWindowPos(
            _hwnd,
            nint.Zero,
            target.X,
            target.Y,
            target.Width,
            target.Height,
            NativeMethods.Constants.SwpNoZOrder | NativeMethods.Constants.SwpNoActivate);
    }

    /// <summary>左键落在抓取带上：记下起点与起始外框，进入拖拽。</summary>
    private void Begin(NativeMethods.Point cursor, ResizeGrip grip)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return;

        _originWindow = new WindowBounds(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
        _originCursor = cursor;
        _resizing = true;
        _pressedGrip = ResizeGrip.None;
        _ticks = 0;
        AppLog.Line($"[resize] 开始缩放 grip={grip} origin=({_originWindow.X},{_originWindow.Y} {_originWindow.Width}x{_originWindow.Height}) cursor=({cursor.X},{cursor.Y})");
        _onStart();
    }

    /// <summary>收尾：释放状态、还原光标、回调（存位置 + 重新落点）。</summary>
    private void End(string reason)
    {
        if (!_resizing) return;
        _resizing = false;
        _grip = ResizeGrip.None;
        _loggedHover = ResizeGrip.None;
        _blockedLogged = false;
        AppLog.Line($"[resize] 结束 reason={reason}");
        _onEnd();
    }
}
