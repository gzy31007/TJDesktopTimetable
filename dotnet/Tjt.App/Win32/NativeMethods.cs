using System.Runtime.InteropServices;

namespace Tjt.App.Win32;

/// <summary>
/// user32 的 P/Invoke 绑定（**唯一入口**，避免各处散落 DllImport）。
///
/// 只声明本项目真正用到的：找桌面图标视图 / owner 读写 / z-order / 扩展样式 / DPI。
/// 用 <c>LibraryImport</c>（源生成）而不是 <c>DllImport</c>：非托管签名在编译期就校验，
/// 也避免运行时再走一次 marshalling 反射。
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>常量集合（保持与 Win32 头文件同名，便于对照文档）。</summary>
    internal static class Constants
    {
        /// <summary>Topmost 层（<c>HWND_TOPMOST</c>）。</summary>
        public static readonly nint HwndTopmost = -1;

        /// <summary>"非 topmost"哨兵（<c>HWND_NOTOPMOST</c>）。</summary>
        public static readonly nint HwndNotTopmost = -2;

        /// <summary>z-order 最底（<c>HWND_BOTTOM</c>）。</summary>
        public static readonly nint HwndBottom = 1;

        /// <summary>不改变位置（<c>SWP_NOMOVE</c>）。</summary>
        public const uint SwpNoMove = 0x0002;

        /// <summary>不改变尺寸（<c>SWP_NOSIZE</c>）。</summary>
        public const uint SwpNoSize = 0x0001;

        /// <summary>不激活窗口（<c>SWP_NOACTIVATE</c>）。</summary>
        public const uint SwpNoActivate = 0x0010;

        /// <summary>窗口不属于任务栏 / Alt+Tab（<c>WS_EX_TOOLWINDOW</c>）。</summary>
        public const long WsExToolWindow = 0x00000080L;

        /// <summary>owner 句柄在 <c>SetWindowLongPtrW</c> 里的 index（<c>GWLP_HWNDPARENT</c>）。</summary>
        public const int GwlpHwndParent = -8;

        /// <summary>扩展样式 index（<c>GWL_EXSTYLE</c>）。</summary>
        public const int GwlExStyle = -20;

        /// <summary>常规样式 index（<c>GWL_STYLE</c>）。</summary>
        public const int GwlStyle = -16;

        /// <summary>窗口过程 index（<c>GWLP_WNDPROC</c>）。</summary>
        public const int GwlpWndProc = -4;

        /// <summary>"不可激活"扩展样式（<c>WS_EX_NOACTIVATE</c>）。静息态**不戴**它，见 Layer 的说明。</summary>
        public const long WsExNoActivate = 0x08000000L;

        /// <summary>拖动消息：<c>WM_NCLBUTTONDOWN</c>。</summary>
        public const uint WmNcLButtonDown = 0x00A1;

        /// <summary>命中测试结果：标题栏（<c>HTCAPTION</c>）。</summary>
        public const nint HtCaption = 2;

        /// <summary>窗口激活状态变化（<c>WM_ACTIVATE</c>）。</summary>
        public const uint WmActivate = 0x0006;

        /// <summary>用户开始/结束拖动或缩放窗口（<c>WM_ENTERSIZEMOVE</c> / <c>WM_EXITSIZEMOVE</c>）。</summary>
        public const uint WmEnterSizeMove = 0x0231;

        /// <summary>用户结束拖动或缩放窗口。</summary>
        public const uint WmExitSizeMove = 0x0232;

        /// <summary>显示分辨率 / 拓扑变化（<c>WM_DISPLAYCHANGE</c>）。</summary>
        public const uint WmDisplayChange = 0x007E;

        /// <summary>系统设置变化（工作区 / 任务栏；<c>WM_SETTINGCHANGE</c>）。</summary>
        public const uint WmSettingChange = 0x001A;

        /// <summary>窗口被隐藏（Win+D 会触发；<c>WM_SHOWWINDOW</c>）。</summary>
        public const uint WmShowWindow = 0x0018;

        /// <summary>不激活地显示（<c>SW_SHOWNOACTIVATE</c>）。</summary>
        public const int SwShowNoActivate = 4;

        /// <summary>恢复正常显示（<c>SW_RESTORE</c>）。</summary>
        public const int SwRestore = 9;
    }

    /// <summary>
    /// 取桌面窗口（<c>GetShellWindow</c>）——即 Progman/WorkerW 的父级，用来遍历桌面子窗口。
    /// 找不到返回 <see cref="nint.Zero"/>。
    /// </summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    /// <summary>按类名/窗口名找子窗口（<c>FindWindowExW</c>）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    /// <summary>句柄是否仍然有效（<c>IsWindow</c>），用于缓存自愈。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);

    /// <summary>读窗口属性（<c>GetWindowLongPtrW</c>；64 位下 index 为负）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    /// <summary>写窗口属性（<c>SetWindowLongPtrW</c>；owner 与扩展样式都走它）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>
    /// 调整窗口的 z-order / 位置 / 尺寸（<c>SetWindowPos</c>）。
    ///
    /// 注意 <paramref name="insertAfter"/> 的语义是"插到这个窗口<b>之后</b>（z-order 更低）"，
    /// 不是"放到它上面" —— 项目里踩过这个坑（把 owner 传进去结果被桌面盖住）。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>取窗口 DPI（<c>GetDpiForWindow</c>），用于把 DIP 换算成物理像素。</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>当前前台窗口（<c>GetForegroundWindow</c>）；拿不到返回 0。</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    /// <summary>窗口所属进程 id（<c>GetWindowThreadProcessId</c>，pid 允许传 0）。</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, nint processId);

    /// <summary>遍历顶层窗口（<c>EnumWindows</c>）；回调返回 false 即停止。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, nint parameter);

    /// <summary>窗口类名（<c>GetClassNameW</c>）；返回写入的字符数。</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(nint hwnd, [Out] char[] buffer, int maxCount);

    /// <summary>窗口是否可见（<c>IsWindowVisible</c>）。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    /// <summary>窗口是否最小化（<c>IsIconic</c>）。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    /// <summary>显示 / 恢复窗口（<c>ShowWindow</c>）。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int command);

    /// <summary>把窗口提到前台（<c>SetForegroundWindow</c>）。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    /// <summary>把窗口插到指定窗口之后（<c>SetWindowPos</c> 的封装见 <c>Resting</c>）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out Rect rect);

    /// <summary>窗口矩形（物理像素）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        /// <summary>左。</summary>
        public int Left;

        /// <summary>上。</summary>
        public int Top;

        /// <summary>右。</summary>
        public int Right;

        /// <summary>下。</summary>
        public int Bottom;
    }

    /// <summary>调用原始窗口过程（子类化后必须转发，见 <c>MessageHook</c>）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static partial nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>窗口过程签名。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="message">消息。</param>
    /// <param name="wParam">参数。</param>
    /// <param name="lParam">参数。</param>
    internal delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>注册窗口消息，返回消息号（<c>RegisterWindowMessageW</c>；TaskbarCreated 用它拿）。</summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string message);

    /// <summary>把顶层窗口遍历的回调。</summary>
    /// <param name="hwnd">顶层窗口句柄。</param>
    /// <param name="parameter">透传参数。</param>
    /// <returns>返回 <c>true</c> 继续遍历。</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool EnumWindowsProc(nint hwnd, nint parameter);

    /// <summary>把物理像素换算成 DIP 时的基准 DPI（<c>USER_DEFAULT_SCREEN_DPI</c>）。</summary>
    public const double DefaultDpi = 96d;
}
