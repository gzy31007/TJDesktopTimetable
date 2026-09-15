using Tjt.Widget;

namespace Tjt.App.Win32;

/// <summary>
/// 窗口缩放 —— 由我们自己回答 <c>WM_NCHITTEST</c>，把系统丢失的那圈抓取带补回来。
///
/// <para><b>为什么需要它</b>：为了去掉右上角的三个系统按钮，窗口清掉了
/// <c>WS_CAPTION | WS_BORDER | WS_DLGFRAME | WS_THICKFRAME</c>。清掉之后
/// <c>DefWindowProc</c> 也不再在边缘返回 <c>HTLEFT/HTBOTTOMRIGHT/…</c>，于是
/// <b>整圈边缘都抓不住</b>（用户报告的那圈"不可见的抓取边"就是这么来的：
/// 定义它的是窗口样式，而不是屏幕上看得见的东西）。</para>
///
/// <para><b>怎么做</b>：只做命中测试，缩放本身仍然交给系统。命中到边上时返回
/// <c>HTLEFT</c> 这类码，Windows 会自己把鼠标消息交给 <c>DefWindowProc</c>，
/// 由它跑**原生缩放循环**（含最小尺寸夹取、吸附、DPI 变化处理）—— 这也是
/// <c>WM_ENTERSIZEMOVE</c> / <c>WM_EXITSIZEMOVE</c> 仍然会到来的原因，
/// 外壳那两条钩子（暂停 owner 巡检 / 存位置）因此不需要任何改动。</para>
///
/// <para><b>为什么不用自实现循环</b>：挂件窗口是普通顶层窗口，<c>DefWindowProc</c>
/// 的缩放循环在它身上完全可用。自实现要自己管最小尺寸、自己画缩放引导层、还要自己处理
/// "缩到屏幕外"，收益为零。拖动那条路走自实现是因为它不是窗口缩放
/// （渲染层把顶部条当拖拽区在用），两条路互不影响。</para>
///
/// <para><b>判据</b>：客户区坐标 + <see cref="ResizePolicy"/>（纯函数，有单测）。
/// 命中带贴在内缩矩形上，也就是"看得见的那圈边缘"，与描边观感一致。</para>
/// </summary>
internal sealed class WindowResize
{
    private readonly Func<int, int, ResizeGrip> _resolve;

    private ResizeGrip _logged = ResizeGrip.None;

    private WindowResize(nint hwnd, Func<int, int, ResizeGrip> resolve)
    {
        _resolve = resolve;
        MessageHook.Subscribe(hwnd, NativeMethods.Constants.WmNcHitTest, OnHitTest);
    }

    /// <summary>把"该抓哪条边"的判断接到窗口的 <c>WM_NCHITTEST</c> 上。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="resolve">
    /// 输入客户区坐标、返回抓取边（<see cref="ResizeGrip.None"/> = 不拦，交回
    /// <c>DefWindowProc</c> 的默认命中测试）。用委托而不是缓存矩形，
    /// 是为了让"窗口刚被拖过、尺寸还没写回设置"这种时序不会喂给命中测试一个过期矩形。
    /// </param>
    public static WindowResize Attach(nint hwnd, Func<int, int, ResizeGrip> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return new WindowResize(hwnd, resolve);
    }

    /// <summary>
    /// 命中：<c>lParam</c> 给的是**屏幕坐标**（低位 X、高位 Y，各自 16 位**有符号**），
    /// 直接交给策略（策略同用屏幕坐标比对窗口外框，少一次 <c>ScreenToClient</c> 往返）。
    ///
    /// <para>多显示器时左侧/上方的屏幕是负坐标，不做符号扩展会把 -100 读成 65436，
    /// 表现为"左边永远抓不住、右边抓的是空气"。这里刻意**不**用
    /// <c>GetCursorPos</c>：命中测试本来就带着坐标，少一次系统调用，
    /// 也避免"消息里的坐标与当前光标不一致"的窗口期。</para>
    /// </summary>
    /// <param name="wParam">未使用。</param>
    /// <param name="lParam">屏幕坐标（打包的两个 short）。</param>
    private void OnHitTest(nint wParam, nint lParam)
    {
        var packed = (int)lParam;
        var screenX = (short)(packed & 0xFFFF);
        var screenY = (short)((packed >> 16) & 0xFFFF);

        var grip = _resolve(screenX, screenY);
        if (grip == ResizeGrip.None)
        {
            _logged = ResizeGrip.None;
            return; // 没命中就不设返回值 → 走原窗口过程
        }

        // 只在"抓取边变化"时打一行：命中测试每次鼠标移动都会来，
        // 无条件打日志会把 run.log 冲掉（拖动那条路的心跳就是按这个思路限流的）。
        if (_logged != grip)
        {
            _logged = grip;
            AppLog.Line($"[resize] 命中 {grip}（屏幕 {screenX},{screenY}）→ 交原生缩放循环");
        }

        MessageHook.SetResult((nint)grip);
    }
}
