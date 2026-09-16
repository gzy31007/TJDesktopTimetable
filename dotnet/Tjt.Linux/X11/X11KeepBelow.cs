using System.Runtime.InteropServices;

namespace Tjt.Linux.X11;

/// <summary>
/// X11 贴桌面层：窗口类型设为 <c>_NET_WM_WINDOW_TYPE_DESKTOP</c> + 状态加 <c>_NET_WM_STATE_BELOW</c>。
///
/// <para>这是 Windows 侧 <c>Owner = SHELLDLL_DefView</c> 的 Linux 对应物，两个属性各管一件事：</para>
/// <list type="bullet">
/// <item><b>DESKTOP 类型</b>（改属性）：把窗口放进 EWMH 的桌面层 —— 比 Below 层还低，
/// 被一切普通窗口覆盖，KWin「显示桌面」不隐藏（它就是桌面，conky 桌面挂件在 KDE 的通行做法）。
/// 早期用的是 DOCK 类型：KWin 5 的 <c>layerForDock()</c> 对 keep-below 的 dock 网开一面
/// （压到 Normal 层，"don't move keepbelow docks below normal window"），恰好能用；
/// KWin 6 起 <c>belongsToLayer()</c> 里 <c>isDock() → AboveLayer</c>（keepBelow 分支排在
/// isDock 之后，永远走不到），DOCK 一开就置顶 —— 实测 v6.7.5，已换 DESKTOP；</item>
/// <item><b>BELOW 状态</b>（EWMH 客户消息）：给不认 DESKTOP 类型的 WM 兜底（KWin 上
/// isDesktop() 先命中，此状态冗余但无害）；关掉「贴桌面层」时随类型一起撤掉。</item>
/// </list>
///
/// <para><b>DESKTOP 层内还要 XRaiseWindow</b>：KWin 会把新出现的 desktop 窗口压到
/// plasmashell 桌面容器（壁纸）之下 —— 不抬上去整个挂件就"消失"在壁纸后面
/// （实测 v6.7.5，客户端发 ConfigureRequest(Above) 即可，WM 会放行）。</para>
///
/// <para>只发协议消息，不改 <c>_NET_WM_STATE</c> 以外的 WM 拥有属性（窗口类型是允许客户端
/// 自己写的属性）。跳过任务栏由 Avalonia 的 <c>ShowInTaskbar = false</c> 自己完成。</para>
/// </summary>
internal static class X11KeepBelow
{
    private const int ClientMessage = 33;
    // EWMH：客户端向根窗口发 _NET_WM_STATE 客户消息，事件掩码必须是
    // SubstructureRedirectMask(1<<20) | SubstructureNotifyMask(1<<19)。
    // （1<<18 是 ResizeRedirectMask —— 只选 SubstructureRedirect 的 WM 会因此收不到这条消息。）
    private const long EventMask = (1L << 20) | (1L << 19);
    private const int PropModeReplace = 0;

    /// <summary>给 X11 窗口加上（或移除）贴桌面层。</summary>
    /// <param name="x11Window">窗口的 XID（<c>TryGetPlatformHandle().Handle</c>）。</param>
    /// <param name="below"><c>true</c> = DESKTOP 类型 + <c>_NET_WM_STATE_ADD</c>；<c>false</c> = NORMAL 类型 + <c>REMOVE</c>。</param>
    public static void Apply(nint x11Window, bool below)
    {
        if (x11Window == nint.Zero) return;

        var display = XOpenDisplay(null);
        if (display == nint.Zero)
        {
            AppLog.Line("[x11] XOpenDisplay 失败：跳过贴桌面层");
            return;
        }

        try
        {
            // ── 1. 窗口类型：DESKTOP（贴桌面）/ NORMAL（普通）—— 直接改属性
            var windowType = XInternAtom(display, "_NET_WM_WINDOW_TYPE", onlyIfExists: false);
            var typeValue = XInternAtom(display, below ? "_NET_WM_WINDOW_TYPE_DESKTOP" : "_NET_WM_WINDOW_TYPE_NORMAL", onlyIfExists: false);
            var atomType = XInternAtom(display, "ATOM", onlyIfExists: true);
            if (XChangeProperty(display, x11Window, windowType, atomType, 32, PropModeReplace, [typeValue], 1) == 0)
            {
                AppLog.Error($"[x11] XChangeProperty(_NET_WM_WINDOW_TYPE={(below ? "DESKTOP" : "NORMAL")}) 失败：类型属性没写进去（window=0x{x11Window:x}）");
            }

            // ── 2. keep-below 状态：EWMH 客户消息（经根窗口转给窗口管理器）
            var root = XDefaultRootWindow(display);
            var state = XInternAtom(display, "_NET_WM_STATE", onlyIfExists: false);
            var belowAtom = XInternAtom(display, "_NET_WM_STATE_BELOW", onlyIfExists: false);

            var message = new XClientMessageEvent
            {
                Type = ClientMessage,
                Display = display,
                Window = x11Window,
                MessageType = state,
                Format = 32,
                // data.l[0]：1 = _NET_WM_STATE_ADD，0 = _NET_WM_STATE_REMOVE
                Data0 = below ? 1 : 0,
                Data1 = belowAtom,
            };

            // 返回 0 = 根窗口上没有进程订阅 SubstructureRedirect（无 WM，或 WM 不支持）→ BELOW 不会生效
            var sent = XSendEvent(display, root, propagate: false, EventMask, ref message);
            XFlush(display);
            if (sent == 0)
            {
                AppLog.Error($"[x11] _NET_WM_STATE 客户消息没有被任何 WM 接收（无人订阅 SubstructureRedirect），keep-below 大概率未生效（window=0x{x11Window:x}）");
            }
            else
            {
                AppLog.Line($"[x11] 贴桌面层 {(below ? "开启" : "关闭")}：WINDOW_TYPE={(below ? "DESKTOP" : "NORMAL")} + _NET_WM_STATE_BELOW {(below ? "ADD" : "REMOVE")}（window=0x{x11Window:x}）");
            }

            if (below)
            {
                // KWin 把新 desktop 窗口压到 plasmashell 桌面容器（壁纸）之下：不抬上去整个挂件不可见。
                // 客户端发 ConfigureRequest(Above) 是合法操作，KWin 会在 Desktop 层内抬升（普通窗口仍覆盖它）。
                XRaiseWindow(display, x11Window);
                XFlush(display);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int Type;
        public nint Serial;
        public int SendEvent;
        public nint Display;
        public nint Window;
        public nint MessageType;
        public int Format;
        public nint Data0;
        public nint Data1;
        public nint Data2;
        public nint Data3;
        public nint Data4;
    }

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern nint XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6")]
    private static extern nint XInternAtom(nint display, string atomName, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XSendEvent(nint display, nint window, bool propagate, long eventMask, ref XClientMessageEvent ev);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XRaiseWindow(nint display, nint window);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, nint[] data, int nelements);
}
