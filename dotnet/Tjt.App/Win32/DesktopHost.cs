using System.Runtime.InteropServices;

namespace Tjt.App.Win32;

/// <summary>
/// 桌面宿主（owner）解析 —— 找 Explorer 已经创建好的桌面图标视图 <c>SHELLDLL_DefView</c>。
///
/// 为什么是它而不是 Progman / WorkerW（这些结论来自 Electron 侧的真机验收，见仓根 AGENTS.md）：
/// - 把挂件设成该窗口的 **owned window**，Win+D 时既不隐藏也不最小化，且仍是顶层窗口
///   （拖动、鼠标、坐标都正常）；
/// - <c>SetParent</c> 成 WorkerW 的子窗口会被桌面图标压住、拖动坐标错乱；
/// - 发 <c>0x052C</c> 催生 WorkerW 会在登录期和 Explorer 抢时序，打乱用户桌面图标布局。
///
/// 句柄会被缓存，并用 <see cref="NativeMethods.IsWindow"/> 自愈（Explorer 重启后句柄会变）。
/// </summary>
internal static class DesktopHost
{
    /// <summary>桌面图标视图的窗口类名。</summary>
    private const string DefViewClass = "SHELLDLL_DefView";

    private static nint _cached;

    /// <summary>
    /// 解析桌面图标视图句柄；解析不到返回 <see cref="nint.Zero"/>。
    ///
    /// 每次调用都先校验缓存是否仍有效，失效就重新遍历 —— Electron 侧的教训是
    /// **不能缓存"期望值"**，Explorer 重启后每个周期都拿旧句柄去比较会误判"丢失"并反复搅动 z-order。
    /// </summary>
    public static nint Resolve()
    {
        if (_cached != nint.Zero && NativeMethods.IsWindow(_cached)) return _cached;

        _cached = Find();
        return _cached;
    }

    /// <summary>作废缓存（收到 <c>TaskbarCreated</c> / 显示变化后调用）。</summary>
    public static void Invalidate() => _cached = nint.Zero;

    /// <summary>
    /// 遍历桌面壳窗口的子窗口找第一个 <c>SHELLDLL_DefView</c>。
    ///
    /// 入口既可能是 <c>GetShellWindow()</c>（Progman），也可能是某个 WorkerW —— 壁纸程序
    /// 注入后 <c>DefView</c> 会被挪到 WorkerW 下，所以两条路都要试。
    /// </summary>
    private static nint Find()
    {
        var shell = NativeMethods.GetShellWindow();
        var direct = NativeMethods.FindWindowEx(shell, nint.Zero, DefViewClass, null);
        if (direct != nint.Zero) return direct;

        // 退回：遍历 shell 的所有子窗口，逐个找 DefView（WorkerW 场景）
        var child = nint.Zero;
        while (true)
        {
            child = NativeMethods.FindWindowEx(shell, child, null, null);
            if (child == nint.Zero) break;
            var defView = NativeMethods.FindWindowEx(child, nint.Zero, DefViewClass, null);
            if (defView != nint.Zero) return defView;
        }

        return nint.Zero;
    }

    /// <summary>当前 owner 是谁（调试/自检用；失败返回 <see cref="nint.Zero"/>）。</summary>
    public static nint CurrentOwner(nint hwnd) => NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent);

    /// <summary>
    /// 把 <paramref name="hwnd"/> 的 owner 挂到桌面图标视图上。
    ///
    /// 写入前存档原值、写入后**读回校验**，失败即还原并返回失败 —— owner 是唯一让挂件
    /// 在 Win+D 后仍然可见的机制，静默失败会表现为"窗口不见了"这种极难排查的现象。
    /// </summary>
    public static bool Attach(nint hwnd, out string detail)
    {
        var host = Resolve();
        if (host == nint.Zero)
        {
            detail = "未找到 SHELLDLL_DefView（Explorer 尚未就绪或已被替换）";
            return false;
        }

        var original = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent);
        var previous = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent, host);
        var written = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent);
        if (written != host)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent, original);
            detail = $"owner 写入失败（写回={written}，期望={host}，Win32 错误={error}），已还原原 owner";
            return false;
        }

        detail = $"owner=0x{host:X}（原 owner=0x{original:X}）";
        return true;
    }

    /// <summary>把 owner 还原成给定值（回退路径用）。</summary>
    public static void SetOwner(nint hwnd, nint owner)
    {
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent, owner);
    }

    /// <summary>静息落点：压到 z-order 最底（owned 窗口本来就恒在 owner 之上）。</summary>
    public static bool SendToBottom(nint hwnd) =>
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.Constants.HwndBottom,
            0,
            0,
            0,
            0,
            NativeMethods.Constants.SwpNoMove | NativeMethods.Constants.SwpNoSize | NativeMethods.Constants.SwpNoActivate);

    /// <summary>
    /// 把这层窗口标记成工具窗口：不出现在任务栏与 Alt+Tab。
    /// 桌面挂件本来就该"只在桌面上"，与 Electron 侧的 <c>skipTaskbar: true</c> 等价。
    /// </summary>
    public static void MarkToolWindow(nint hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlExStyle);
        // nint | long 的运算结果是 long，而 SetWindowLongPtr 第 3 参是 nint —— 必须显式转回来
        var updated = (nint)(style | NativeMethods.Constants.WsExToolWindow);
        if (updated != style)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlExStyle, updated);
        }
    }
}
