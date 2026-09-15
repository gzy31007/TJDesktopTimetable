using Microsoft.UI.Xaml;
using Tjt.Widget;

namespace Tjt.App.Win32;

/// <summary>
/// 窗口缩放 —— **自实现**（与 <see cref="WindowDrag"/> 同一套做法）。
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
/// <item><description>光标进入边缘抓取带 → <c>SetCursor</c> 显示对应缩放光标；</description></item>
/// <item><description>左键按下且落在带内 → 记下起点与起始外框，开始拖拽；</description></item>
/// <item><description>每帧用 <see cref="ResizePolicy.Resolve"/>（纯函数，有单测）算新外框，
/// 一次 <c>SetWindowPos</c> 搬窗口；</description></item>
/// <item><description>左键松开 → 收尾（存位置 + 重新落点），并把光标还原成箭头。</description></item>
/// </list></para>
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
    private bool _cursorLogged;
    private ResizeGrip _grip = ResizeGrip.None;
    private NativeMethods.Point _originCursor;
    private WindowBounds _originWindow = new(0, 0, 0, 0);
    private int _ticks;
    private ResizeGrip _loggedHover = ResizeGrip.None;

    private WindowEdgeResize(nint hwnd, Func<int, int, ResizeGrip> resolve, Action onStart, Action onEnd)
    {
        _hwnd = hwnd;
        _resolve = resolve;
        _onStart = onStart;
        _onEnd = onEnd;
        _poll.Interval = PollInterval;
        _poll.Tick += (_, _) => Step();
        _poll.Start();

        // WM_SETCURSOR 是**唯一**能保住自定义光标的地方：窗口过程每次鼠标移动都会在这里
        // 用类光标重置一次。只在自己窗口（wParam == hwnd）且正悬停在抓取带上时接管。
        MessageHook.Subscribe(hwnd, NativeMethods.Constants.WmSetCursor, OnSetCursor);
    }

    /// <summary>
    /// 光标消息：悬停在抓取带上 → 自己设缩放光标并声明"已处理"，让默认处理别再覆盖。
    /// </summary>
    /// <param name="wParam">发起消息的窗口（必须是我们自己，子窗口的不抢）。</param>
    /// <param name="lParam">低位是命中测试码（这里不看，用当前抓取边判断）。</param>
    private void OnSetCursor(nint wParam, nint lParam)
    {
        if (_grip == ResizeGrip.None) return;   // 不在带上：交回默认处理（按钮/文本的光标）
        if (wParam != _hwnd) return;            // 不是本窗口发起的：不抢

        var previous = ApplyCursor(_grip);
        if (!_cursorLogged)
        {
            _cursorLogged = true;
            AppLog.Line($"[resize] 已用 WM_SETCURSOR 设缩放光标 grip={_grip}（SetCursor 返回 0x{previous:X}）");
        }

        MessageHook.SetResult(1);               // TRUE = "光标已设好，别动它"
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

    /// <summary>左键当前是否按着（收尾判据）。</summary>
    private static bool IsLeftButtonDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkLButton) & 0x8000) != 0;

    /// <summary>
    /// 缩放光标：按抓取边选 <c>IDC_SIZEWE/NS/NWSE/NESW</c>。
    ///
    /// <para>触发点是 <c>WM_SETCURSOR</c>（见构造函数里的订阅），不是轮询计数 ——
    /// 因为**每次鼠标移动窗口过程都会重置光标**，只有在这个消息里设才不会被立刻覆盖
    /// （真机验收"没有缩放图标，但可以缩放"就是这么来的）。
    /// 只在"当前悬停在某条抓取带上"时设，其余情况把消息交回默认处理，
    /// 免得抢掉按钮/文本自己该有的光标。</para>
    /// </summary>
    private static nint ApplyCursor(ResizeGrip grip)
    {
        var id = grip switch
        {
            ResizeGrip.Left or ResizeGrip.Right => NativeMethods.IdcSizeWe,
            ResizeGrip.Top or ResizeGrip.Bottom => NativeMethods.IdcSizeNs,
            ResizeGrip.TopLeft or ResizeGrip.BottomRight => NativeMethods.IdcSizeNwse,
            ResizeGrip.TopRight or ResizeGrip.BottomLeft => NativeMethods.IdcSizeNesw,
            _ => nint.Zero, // 默认箭头（IDC_ARROW = 32512）
        };

        var cursor = NativeMethods.LoadCursor(nint.Zero, id == nint.Zero ? 32512 : id);
        // SetCursor 返回"被替换掉的光标"：为 0 通常意味着这个线程不拥有光标
        // （前台是别的进程时会发生），照着它就能判断"到底设成功了没有"。
        return cursor == nint.Zero ? nint.Zero : NativeMethods.SetCursor(cursor);
    }

    /// <summary>轮询一步：先看左键是否按下决定"起拖 / 拖拽中 / 收尾"，没按下时只更新光标。</summary>
    private void Step()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        if (!_resizing)
        {
            var grip = _resolve(cursor.X, cursor.Y);
            if (grip == _grip) return;

            _grip = grip;
            _cursorLogged = false;   // 换了抓取边，下次 WM_SETCURSOR 再打一行

            // 抓取边变化时打一行（真机排查"拖不动/没光标"时，看它就能分清
            // 是命中测试没到、还是光标被别的东西覆盖了）
            if (grip != _loggedHover)
            {
                _loggedHover = grip;
                if (grip != ResizeGrip.None)
                {
                    AppLog.Line($"[resize] 悬停 {grip}（屏幕 {cursor.X},{cursor.Y}）");
                }
            }

            if (grip != ResizeGrip.None && IsLeftButtonDown()) Begin(cursor, grip);
            return;
        }

        _ticks++;
        if (_ticks % 120 == 0)
        {
            AppLog.Line($"[resize] poll#{_ticks} lbutton={IsLeftButtonDown()} grip={_grip} cursor=({cursor.X},{cursor.Y})");
        }

        // 左键松开 = 缩放结束。必须用按键状态：指针可能早就移出窗口，收不到 PointerReleased。
        if (!IsLeftButtonDown())
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
        _cursorLogged = false;
        ApplyCursor(ResizeGrip.None);   // 还原箭头（此后 WM_SETCURSOR 交回默认处理）
        AppLog.Line($"[resize] 结束 reason={reason}");
        _onEnd();
    }
}
