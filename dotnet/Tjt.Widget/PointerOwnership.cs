namespace Tjt.Widget;

/// <summary>
/// 指针归属策略 —— **纯函数，零依赖，可单测**（与 <see cref="ResizePolicy"/> /
/// <see cref="RestingPolicy"/> 同一层）。
///
/// <para><b>为什么需要它</b>：挂件的自实现交互里有一条路径只按「光标坐标 + 左键状态」起手
/// （缩放的抓取带判定，见 <c>Tjt.App/Win32/WindowEdgeResize.cs</c>），而坐标判定**看不见
/// 窗口上面压着谁**。于是挂件被别的窗口遮挡时，用户在那个窗口上按住左键拖动，只要光标扫过
/// 挂件的边缘带，挂件就会跟着缩放 —— 真机 bug（"课表被遮挡时拖动仍然生效"）。</para>
///
/// <para><b>判据取自 DeskBox</b>（它只在窗口真能收到指针时才认这次交互）：
/// 取光标下的窗口 → <c>GetAncestor(GA_ROOT)</c> 拿到根窗口，只有下面三种才算
/// "这次指针属于我们"：
/// <list type="number">
/// <item><description>根窗口就是自己（正常按下）；</description></item>
/// <item><description>根窗口是**桌面壳**（<c>Progman</c> / <c>WorkerW</c> /
/// <c>SHELLDLL_DefView</c>）—— 贴桌面层的 no-activate 窗口在"首次点击之前"
/// <c>WindowFromPoint</c> 会报成桌面宿主而不是我们的 HWND（DeskBox 的实测结论），
/// 不放行就会退化成"边缘拖不动"；</description></item>
/// <item><description>根窗口是 owner 的根（同一条 owner 链）。</description></item>
/// </list>
/// 命中**别的应用窗口**（应用、任务栏……）一律拒绝 —— 那正是"被遮挡"的定义。</para>
///
/// <para>调用方负责把 Win32 事实（<c>WindowFromPoint</c> / <c>GetAncestor</c> / 类名）
/// 解析成这三个入参，本类只做判断，因此能在 Linux 上单测。</para>
/// </summary>
public static class PointerOwnership
{
    /// <summary>
    /// 这次指针事件/轮询是否落在 <paramref name="self"/> 的可交互范围上。
    /// </summary>
    /// <param name="self">挂件窗口句柄。</param>
    /// <param name="pointerRoot">光标下窗口的根窗口（<c>GetAncestor(GA_ROOT)</c>；拿不到传 0）。</param>
    /// <param name="ownerRoot">挂件 owner 的根窗口（没有 owner 传 0）。</param>
    /// <param name="pointerRootIsDesktopShell">根窗口是否属于桌面壳。</param>
    public static bool Accepts(
        nint self,
        nint pointerRoot,
        nint ownerRoot,
        bool pointerRootIsDesktopShell)
    {
        if (self == nint.Zero || pointerRoot == nint.Zero) return false;
        if (pointerRoot == self) return true;
        if (pointerRootIsDesktopShell) return true;
        if (ownerRoot != nint.Zero && pointerRoot == ownerRoot) return true;
        return false;
    }
}
