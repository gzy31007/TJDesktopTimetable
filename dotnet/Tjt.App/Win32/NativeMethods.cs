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

    /* ------------------------------------------------------ 托盘图标与原生菜单（TrayIcon 用） */

    /// <summary>调用默认窗口过程（消息专用窗口未处理的消息交给它）。</summary>
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>注册窗口类。</summary>
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClass(ref WndClass wndClass);

    /// <summary>创建窗口（托盘用消息专用窗口：parent = HWND_MESSAGE）。</summary>
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    /// <summary>销毁窗口。</summary>
    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(nint hwnd);

    /// <summary>取模块句柄（<c>GetModuleHandleW</c>）。</summary>
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    /// <summary>加载图标资源（<c>LoadImageW</c>）。</summary>
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    internal static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);

    /// <summary>加载系统预定义图标（<c>LoadIconW</c>）。</summary>
    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    internal static extern nint LoadIcon(nint instance, nint name);

    /// <summary>销毁图标句柄。</summary>
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(nint icon);

    /// <summary>增删改托盘图标。</summary>
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    /// <summary>创建弹出菜单。</summary>
    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    /// <summary>追加菜单项。</summary>
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenu(nint menu, uint flags, uint id, string? text);

    /// <summary>弹出菜单（<c>TPM_RETURNCMD</c> 时返回选中的命令 id）。</summary>
    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    /// <summary>销毁菜单。</summary>
    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(nint menu);

    /// <summary>取光标位置（物理像素）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    /// <summary>屏幕坐标点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        /// <summary>X。</summary>
        public int X;

        /// <summary>Y。</summary>
        public int Y;
    }

    /// <summary>窗口类描述（只填 TrayIcon 用到的字段）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClass
    {
        /// <summary>样式。</summary>
        public uint style;

        /// <summary>窗口过程指针。</summary>
        public nint lpfnWndProc;

        /// <summary>类额外字节。</summary>
        public int cbClsExtra;

        /// <summary>窗口额外字节。</summary>
        public int cbWndExtra;

        /// <summary>模块实例。</summary>
        public nint hInstance;

        /// <summary>类图标。</summary>
        public nint hIcon;

        /// <summary>光标。</summary>
        public nint hCursor;

        /// <summary>背景刷。</summary>
        public nint hbrBackground;

        /// <summary>菜单名。</summary>
        public string? lpszMenuName;

        /// <summary>类名。</summary>
        public string lpszClassName;
    }

    /// <summary>托盘图标数据（<c>NOTIFYICONDATAW</c>，按 x64 布局）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        /// <summary>结构大小。</summary>
        public uint cbSize;

        /// <summary>接收回调消息的窗口。</summary>
        public nint hWnd;

        /// <summary>图标 id。</summary>
        public uint uID;

        /// <summary>有效字段掩码。</summary>
        public uint uFlags;

        /// <summary>回调消息。</summary>
        public uint uCallbackMessage;

        /// <summary>图标句柄。</summary>
        public nint hIcon;

        /// <summary>悬停提示。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;

        /// <summary>状态。</summary>
        public uint dwState;

        /// <summary>状态掩码。</summary>
        public uint dwStateMask;

        /// <summary>气泡文本。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;

        /// <summary>版本（联合体）。</summary>
        public uint uVersion;

        /// <summary>气泡标题。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;

        /// <summary>气泡图标标志。</summary>
        public uint dwInfoFlags;

        /// <summary>GUID（联合体）。</summary>
        public Guid guidItem;

        /// <summary>气泡自定义图标。</summary>
        public nint hBalloonIcon;
    }
}
