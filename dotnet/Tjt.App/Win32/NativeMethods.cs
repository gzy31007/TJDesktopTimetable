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

    /// <summary>把物理像素换算成 DIP 时的基准 DPI（<c>USER_DEFAULT_SCREEN_DPI</c>）。</summary>
    public const double DefaultDpi = 96d;
}
