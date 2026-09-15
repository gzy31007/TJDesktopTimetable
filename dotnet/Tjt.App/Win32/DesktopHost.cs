using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Tjt.App.Win32;

/// <summary>
/// 桌面宿主（owner）解析与管理 —— 找 Explorer 已经创建好的桌面图标视图 <c>SHELLDLL_DefView</c>。
///
/// <para>为什么是它而不是 Progman / WorkerW（结论来自 Electron 侧的真机验收，见仓根 AGENTS.md）：</para>
/// <list type="bullet">
/// <item><description>把挂件设成该窗口的 **owned window**，Win+D 时既不隐藏也不最小化，
/// 且仍是顶层窗口（拖动、鼠标、坐标都正常）；</description></item>
/// <item><description><c>SetParent</c> 成 WorkerW 的子窗口会被桌面图标压住、拖动坐标错乱；</description></item>
/// <item><description>发 <c>0x052C</c> 催生 WorkerW 会在登录期和 Explorer 抢时序，打乱用户桌面图标布局。</description></item>
/// </list>
///
/// <para>句柄会被缓存，并用 <see cref="NativeMethods.IsWindow"/> 自愈（Explorer 重启后句柄会变）；
/// 写入 owner 前存档原值、写入后**读回校验**，失败即还原 —— 静默失败的观感是"窗口不见了"，
/// 属于最难排查的一类。</para>
/// </summary>
internal static class DesktopHost
{
    /// <summary>桌面图标视图的窗口类名。</summary>
    private const string DefViewClass = "SHELLDLL_DefView";

    /// <summary>桌面壳的类名（判定"前台是不是桌面"）。</summary>
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", DefViewClass];

    /// <summary>每个窗口挂载前存档的原 owner，用于还原（句柄 → 原值）。</summary>
    private static readonly ConcurrentDictionary<nint, nint> OriginalOwners = new();

    private static nint _cached;

    /// <summary>
    /// 解析桌面图标视图句柄；解析不到返回 0。
    ///
    /// 每次调用都先校验缓存是否仍有效，失效就重新遍历 —— Electron 侧的教训是
    /// **不能缓存"期望值"**：Explorer 重启后句柄会变，拿旧句柄去比较会误判"丢失"并反复搅动 z-order。
    /// </summary>
    public static nint Resolve()
    {
        if (_cached != nint.Zero && NativeMethods.IsWindow(_cached)) return _cached;
        _cached = Find();
        return _cached;
    }

    /// <summary>作废缓存（Explorer 重启 / 显示拓扑变化后调用）。</summary>
    public static void Invalidate() => _cached = nint.Zero;

    /// <summary>当前 owner 是谁（调试/自检用；失败返回 0）。</summary>
    public static nint CurrentOwner(nint hwnd) => NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent);

    /// <summary>owner 是否已经是期望的桌面宿主。</summary>
    public static bool OwnerIsDesktopHost(nint hwnd)
    {
        var host = Resolve();
        return host != nint.Zero && CurrentOwner(hwnd) == host;
    }

    /// <summary>
    /// 把 <paramref name="hwnd"/> 的 owner 挂到桌面图标视图上。
    ///
    /// 已挂且句柄未变时**不做任何窗口操作**（巡检每次都调它，不能产生 z-order 抖动）。
    /// </summary>
    /// <param name="hwnd">挂件窗口。</param>
    /// <param name="detail">结果描述（日志用）。</param>
    public static bool Attach(nint hwnd, out string detail)
    {
        var host = Resolve();
        if (host == nint.Zero)
        {
            detail = "未找到 SHELLDLL_DefView（Explorer 尚未就绪或已被替换）";
            return false;
        }

        var current = CurrentOwner(hwnd);
        if (current == host)
        {
            detail = $"owner 已是桌面宿主 0x{host:X}";
            return true;
        }

        var original = OriginalOwners.GetOrAdd(hwnd, current);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent, host);
        var written = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent);
        if (written != host)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent, original);
            detail = $"owner 写入失败（写回=0x{written:X}，期望=0x{host:X}，Win32 错误={error}），已还原原 owner";
            return false;
        }

        detail = $"owner=0x{host:X}（原 owner=0x{original:X}）";
        return true;
    }

    /// <summary>把 owner 还原成存档的原值（退出 / 切换层级模式时用）。</summary>
    public static void RestoreOriginalOwner(nint hwnd)
    {
        if (!OriginalOwners.TryRemove(hwnd, out var original)) return;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent, original);
    }

    /// <summary>给某个窗口单独设 owner（诊断 / A/B 对照用）。</summary>
    public static void SetOwner(nint hwnd, nint owner) =>
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpHwndParent, owner);

    /// <summary>
    /// 沿着 owner / parent 链找到"根"窗口 —— 用来判断前台窗口到底属于谁。
    ///
    /// owned 窗口（例如某个对话框）会被报成前台，但真正代表"用户在看哪个应用"的是它的根；
    /// 与 Electron 侧 <c>foregroundRoot()</c> 同义。
    /// </summary>
    public static nint ForegroundRoot(nint foreground)
    {
        var cursor = foreground;
        var guard = 0;
        while (cursor != nint.Zero && guard < 16)
        {
            var owner = CurrentOwner(cursor);
            if (owner == nint.Zero || owner == cursor) break;
            cursor = owner;
            guard += 1;
        }

        return cursor;
    }

    /// <summary>该窗口是否属于桌面壳（Progman / WorkerW / SHELLDLL_DefView）。</summary>
    public static bool IsDesktopShellWindow(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        var className = ClassNameOf(hwnd);
        return ShellClasses.Contains(className, StringComparer.Ordinal);
    }

    /// <summary>取窗口类名（失败返回空串）。</summary>
    public static string ClassNameOf(nint hwnd)
    {
        if (hwnd == nint.Zero) return string.Empty;
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>该窗口是否属于本进程。</summary>
    public static bool IsOwnProcess(nint hwnd) =>
        hwnd != nint.Zero && NativeMethods.GetWindowThreadProcessId(hwnd, nint.Zero) == Environment.ProcessId;

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

        var found = nint.Zero;
        NativeMethods.EnumWindows((top, _) =>
        {
            var defView = NativeMethods.FindWindowEx(top, nint.Zero, DefViewClass, null);
            if (defView == nint.Zero) return true;
            found = defView;
            return false;
        }, nint.Zero);

        return found;
    }
}
