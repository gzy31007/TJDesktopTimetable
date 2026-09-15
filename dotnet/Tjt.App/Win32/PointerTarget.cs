using Tjt.Widget;

namespace Tjt.App.Win32;

/// <summary>
/// 光标此刻"真正按在谁身上" —— 自实现交互（拖动 / 缩放）起手前的**归属校验**。
///
/// <para><b>解决什么问题</b>：缩放的抓取带只按「光标坐标 + 左键状态」判定，坐标判定看不见
/// 窗口上面压着谁。挂件被别的窗口遮挡时，用户在那个窗口上按住左键拖动，光标扫过挂件边缘带
/// 就会把挂件一起缩放（真机 bug：<i>"课表被其他窗口遮挡时，拖动仍然生效"</i>）。
/// 本类把"指针归属"这件事从坐标里补回来。</para>
///
/// <para><b>判据与 DeskBox 同源</b>：DeskBox 收指针事件时会先
/// <c>WindowFromPoint</c> → <c>GetAncestor(GA_ROOT)</c>，再对比自己的 HWND / 桌面壳 / owner 链
/// （它在实现里还专门注释了"首次点击前 <c>WindowFromPoint</c> 可能报成 Explorer 桌面宿主"）。
/// 判定规则本身是纯策略 <see cref="PointerOwnership.Accepts"/>（在 <c>Tjt.Widget</c>，有单测），
/// 本类只负责采集 Win32 事实。</para>
///
/// <para><b>只用在起手**那一刻**</b>：拖动/缩放进行中光标本来就会移出窗口（挂件是临时 topmost），
/// 那时再校验会把正常交互打断。</para>
/// </summary>
internal static class PointerTarget
{
    /// <summary>光标下的根窗口（无窗口 / 取不到光标返回 0）。</summary>
    public static nint RootUnderCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point)) return nint.Zero;
        return RootOf(NativeMethods.WindowFromPoint(point));
    }

    /// <summary>把任意窗口并到它的根（<c>GA_ROOT</c>）；失败就返回原窗口。</summary>
    public static nint RootOf(nint hwnd)
    {
        if (hwnd == nint.Zero) return nint.Zero;
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.Constants.GaRoot);
        return root != nint.Zero ? root : hwnd;
    }

    /// <summary>
    /// 这次左键/指针是否落在 <paramref name="hwnd"/> 自己身上（被遮挡时返回 false）。
    /// </summary>
    /// <param name="hwnd">挂件窗口句柄。</param>
    /// <param name="detail">判定依据（日志用，形如 <c>root=0x1234(self)</c>）。</param>
    public static bool CursorIsOurs(nint hwnd, out string detail)
    {
        var root = RootUnderCursor();
        var owner = DesktopHost.CurrentOwner(hwnd);
        var ownerRoot = RootOf(owner);
        var rootIsShell = DesktopHost.IsDesktopShellWindow(root);

        if (PointerOwnership.Accepts(hwnd, root, ownerRoot, rootIsShell))
        {
            var kind = root == hwnd ? "self" : rootIsShell ? "desktop-shell" : "owner-chain";
            detail = $"root=0x{root:X}({kind})";
            return true;
        }

        detail = $"root=0x{root:X} class={DesktopHost.ClassNameOf(root)}";
        return false;
    }
}
