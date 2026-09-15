using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Tjt.App.Win32;

/// <summary>
/// 窗口拖动 —— **自实现**（去掉系统标题栏之后唯一的办法）。
///
/// <para><b>为什么不用原生 move loop</b>：真机实测（`.tools/test-drag.ps1` 用合成鼠标做
/// 按下→移动→抬起）<c>ReleaseCapture() + SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)</c>
/// **完全不动窗口**，与 Electron 侧当年"从主进程发 WM_NCLBUTTONDOWN 不生效"是同一个结论。
/// 参考项目 DeskBox 同样没有走原生路径：它清掉 <c>WS_CAPTION</c> 等样式后自己实现拖动。</para>
///
/// <para><b>为什么不用 XAML 的 <c>CapturePointer</c></b>：也实测过 —— 按下时
/// <c>capture=True</c>，但紧接着就收到 <c>PointerCaptureLost</c>（Windows 会向"失去捕获"的窗口
/// 发消息，那条路径上元素被重建，捕获随之作废），于是 <c>PointerMoved</c> 一条都收不到，
/// 表现为"窗口纹丝不动"。所以改为**窗口级 <c>SetCapture</c> + <c>DispatcherTimer</c> 轮询光标**：
/// 轮询与元素生命周期无关，是这类自实现拖动最稳的写法（4ms 级开销，可忽略）。</para>
///
/// <para><b>收尾</b>：轮询里发现左键已松开 → 释放捕获、回调结束（存位置 + 重新落点）。
/// 另外主进程窗口在拖动期间会被反复重排，所以调用方要暂停 owner 巡检。</para>
/// </summary>
internal sealed class WindowDrag
{
    /// <summary>起拖阈值（物理像素）：小于它的位移不认为是在拖窗口（避免手一抖窗口就跑）。</summary>
    private const int DragThreshold = 4;

    /// <summary>轮询间隔：16ms ≈ 60Hz，肉眼跟手。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(16);

    private readonly nint _hwnd;
    private readonly Action _onStart;
    private readonly Action _onEnd;

    private readonly DispatcherTimer _poll = new();
    private bool _tracking;
    private bool _dragging;
    private int _ticks;
    private NativeMethods.Point _originCursor;
    private int _originX;
    private int _originY;

    private WindowDrag(nint hwnd, Action onStart, Action onEnd)
    {
        _hwnd = hwnd;
        _onStart = onStart;
        _onEnd = onEnd;
        _poll.Interval = PollInterval;
        _poll.Tick += (_, _) => Step();
    }

    /// <summary>把某个元素变成窗口拖动区（一般是顶部条）。</summary>
    /// <param name="grabArea">抓取区域（只用来接 PointerPressed）。</param>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="onStart">按下时回调（暂停巡检 / 临时浮起）。</param>
    /// <param name="onEnd">结束时回调（存位置 / 重新落点）。</param>
    public static WindowDrag Attach(FrameworkElement grabArea, nint hwnd, Action onStart, Action onEnd)
    {
        ArgumentNullException.ThrowIfNull(grabArea);
        var drag = new WindowDrag(hwnd, onStart, onEnd);
        grabArea.PointerPressed += drag.OnPressed;
        return drag;
    }

    /// <summary>左键当前是否按着（拖动收尾判据）。</summary>
    private static bool IsLeftButtonDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VkLButton) & 0x8000) != 0;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tracking) return;
        if (!NativeMethods.GetCursorPos(out _originCursor)) return;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return;

        _originX = rect.Left;
        _originY = rect.Top;
        _dragging = false;
        _tracking = true;

        // 窗口级捕获：指针移出窗口后仍能拿到位置（并让系统别把这次按下交给别的窗口）
        NativeMethods.SetCapture(_hwnd);
        AppLog.Line($"[drag] 按下 origin=({_originX},{_originY}) cursor=({_originCursor.X},{_originCursor.Y}) capture=0x{NativeMethods.GetCapture():X}");
        _onStart();
        _ticks = 0;
        _poll.Start();
        e.Handled = true;
    }

    /// <summary>轮询一步：算位移、搬窗口；左键已松开就收尾。</summary>
    private void Step()
    {
        if (!_tracking) return;
        _ticks++;
        // 每 2 秒打一行心跳：真机上排查"按下没反应/拖不动"时，看它就能区分
        // "按下事件没来"与"按下来了但左键状态读不到"。
        if (_ticks % 120 == 0)
        {
            NativeMethods.GetCursorPos(out var probe);
            AppLog.Line($"[drag] poll#{_ticks} lbutton={IsLeftButtonDown()} dragging={_dragging} cursor=({probe.X},{probe.Y}) origin=({_originCursor.X},{_originCursor.Y})");
        }

        // 左键松开 = 拖动结束。这里**必须**用按键状态：指针可能早就移出窗口，
        // PointerReleased 不会送到我们手上（这正是 XAML 捕获方案失败的原因）。
        if (!IsLeftButtonDown())
        {
            End("left-button-up");
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var dx = cursor.X - _originCursor.X;
        var dy = cursor.Y - _originCursor.Y;

        if (!_dragging)
        {
            if ((dx * dx) + (dy * dy) < DragThreshold * DragThreshold) return;
            _dragging = true;
            AppLog.Line($"[drag] 开始拖动（阈值 {DragThreshold}px）");
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            nint.Zero,
            _originX + dx,
            _originY + dy,
            0,
            0,
            NativeMethods.Constants.SwpNoSize | NativeMethods.Constants.SwpNoZOrder | NativeMethods.Constants.SwpNoActivate);
    }

    private void End(string reason)
    {
        if (!_tracking) return;
        _tracking = false;
        _poll.Stop();
        NativeMethods.ReleaseCapture();
        AppLog.Line($"[drag] 结束 reason={reason} dragged={_dragging}");
        _dragging = false;
        _onEnd();
    }
}
