namespace Tjt.App.Win32;

/// <summary>
/// z-order 与静息样式的原语（对应 Electron 侧 <c>resting.ts</c>）。
///
/// <para><b>静息态不戴 <c>WS_EX_NOACTIVATE</c></b>（真机结论）：戴着它时系统会跳过
/// 原生 move loop，而 WinUI 的标题栏拖动正是走那条路 —— 结果是**窗口拖不动**。
/// 想要"点击不抢前台"只能走命中测试临时摘样式那条路，代价与复杂度都不值当。</para>
///
/// <para>本类只提供原语，"该落到哪一层"由 <c>Layer</c> 结合
/// <see cref="Tjt.Widget.RestingPolicy"/> 决定。</para>
/// </summary>
internal static class Resting
{
    /// <summary>压到 z-order 最底（owned 窗口本来就恒在 owner 之上）。</summary>
    public static bool PushToBottom(nint hwnd) =>
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.Constants.HwndBottom,
            0,
            0,
            0,
            0,
            NativeMethods.Constants.SwpNoMove | NativeMethods.Constants.SwpNoSize | NativeMethods.Constants.SwpNoActivate);

    /// <summary>
    /// 插到 <paramref name="target"/> **之后**（z-order 更低）。
    ///
    /// 注意语义：<c>SetWindowPos</c> 的 <c>hWndInsertAfter</c> 是"插到这个窗口之后"，
    /// 不是"放到它上面"。项目里踩过这个坑（把 owner 传进去结果被桌面盖住）。
    /// </summary>
    public static bool PlaceBehind(nint hwnd, nint target)
    {
        if (target == nint.Zero) return false;
        return NativeMethods.SetWindowPos(
            hwnd,
            target,
            0,
            0,
            0,
            0,
            NativeMethods.Constants.SwpNoMove | NativeMethods.Constants.SwpNoSize | NativeMethods.Constants.SwpNoActivate);
    }

    /// <summary>取消 topmost（临时浮起之后收回）。</summary>
    public static bool ClearTopMost(nint hwnd) =>
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.Constants.HwndNotTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.Constants.SwpNoMove | NativeMethods.Constants.SwpNoSize | NativeMethods.Constants.SwpNoActivate);

    /// <summary>
    /// 交互期"临时浮起"：<c>HWND_TOPMOST</c> → 立刻 <c>HWND_NOTOPMOST</c> 的脉冲。
    ///
    /// 只借 topmost 这一下把窗口提到普通层级带顶部，**不留 topmost 状态**
    /// （否则挂件会永久盖住所有窗口）。
    /// </summary>
    public static void HoldTemporaryTopMost(nint hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.Constants.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.Constants.SwpNoMove | NativeMethods.Constants.SwpNoSize | NativeMethods.Constants.SwpNoActivate);
        ClearTopMost(hwnd);
    }

    /// <summary>给窗口加 / 摘 <c>WS_EX_NOACTIVATE</c>（静息态默认不戴，见类文档）。</summary>
    public static void SetNoActivate(nint hwnd, bool enabled)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlExStyle);
        var updated = enabled
            ? (nint)(style | NativeMethods.Constants.WsExNoActivate)
            : (nint)(style & ~NativeMethods.Constants.WsExNoActivate);
        if (updated != style)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlExStyle, updated);
        }
    }

    /// <summary>当前是否戴着 <c>WS_EX_NOACTIVATE</c>。</summary>
    public static bool IsNoActivate(nint hwnd) =>
        (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlExStyle) & NativeMethods.Constants.WsExNoActivate) != 0;

    /// <summary>
    /// 标记成工具窗口：不出现在任务栏与 Alt+Tab（等价 Electron 的 <c>skipTaskbar: true</c>）。
    /// </summary>
    public static void MarkToolWindow(nint hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlExStyle);
        // nint | long 的运算结果是 long，SetWindowLongPtr 第 3 参是 nint，必须显式转回来
        var updated = (nint)(style | NativeMethods.Constants.WsExToolWindow);
        if (updated != style)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlExStyle, updated);
        }
    }

    /// <summary>
    /// 窗口当前是否可见且没被最小化（Win+D 之后应为真：owner 保护让它既不隐藏也不最小化）。
    /// </summary>
    public static bool IsVisibleAndNotMinimized(nint hwnd) =>
        NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd);

    /// <summary>恢复可见性（Win+D 之后如果被系统隐藏了，这里把它无激活地显示回来）。</summary>
    public static void EnsureVisible(nint hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.Constants.SwShowNoActivate);
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.Constants.SwRestore);
        }
    }
}
