using System.Runtime.InteropServices;

namespace Tjt.App.Win32;

/// <summary>
/// 极简窗口过程子类化 —— 订阅若干个窗口消息，收到就回调（对应 Electron 的
/// <c>hookWindowMessage</c>；WinUI 没有等价能力，只能自己替换 <c>GWLP_WNDPROC</c>）。
///
/// <para>用途：把"Explorer 重启 / 显示拓扑变化 / 拖动结束"这类事件变成**事件驱动**，
/// 而不是靠轮询。挂件只在收到消息时动一次 z-order，避免"多个机制同时改层级"那类问题
/// （Electron 侧的历史教训）。</para>
///
/// <para>两种订阅方式：<see cref="Subscribe(nint, uint, Action)"/> 只关心"发生了"；
/// <see cref="Subscribe(nint, uint, Action{nint, nint})"/> 还要看参数（例如
/// <c>WM_NCHITTEST</c> 的命中坐标）。</para>
///
/// <para>实现要点：原窗口过程必须转发回去，否则窗口会失去全部默认行为（拖动、缩放、
/// 关闭都会失灵）。回调在窗口线程上同步执行，所以回调里只做轻量操作。</para>
/// </summary>
internal static class MessageHook
{
    private static readonly Dictionary<uint, List<Action>> Handlers = [];
    private static readonly Dictionary<uint, List<Action<nint, nint>>> ParamHandlers = [];

    /// <summary>保引用：委托被 GC 会让原生回调指向空。</summary>
    private static NativeMethods.WndProc? _hookProc;

    private static nint _originalProc;
    private static nint _hooked;

    /// <summary>
    /// 订阅 <paramref name="message"/>（不看参数）；返回取消订阅的委托。
    ///
    /// 同一窗口多次调用只会子类化一次；不同消息共用同一条钩子链。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="message">要监听的消息。</param>
    /// <param name="callback">收到消息时的回调（在窗口线程上执行）。</param>
    public static Action Subscribe(nint hwnd, uint message, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (hwnd == nint.Zero) return () => { };

        if (!Handlers.TryGetValue(message, out var list))
        {
            list = [];
            Handlers[message] = list;
        }

        list.Add(callback);
        EnsureHooked(hwnd);

        return () =>
        {
            if (Handlers.TryGetValue(message, out var current)) current.Remove(callback);
        };
    }

    /// <summary>
    /// 订阅 <paramref name="message"/> 并拿到 <c>wParam</c> / <c>lParam</c>。
    ///
    /// <para>回调可以调 <see cref="SetResult"/> 直接把返回值交给原生调用方
    /// （<c>WM_NCHITTEST</c> 就靠这个把 <c>HT*</c> 码送回去）。没调
    /// <see cref="SetResult"/> 就与普通订阅等价。</para>
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="message">要监听的消息。</param>
    /// <param name="callback">收到消息时的回调（参数为 wParam、lParam，在窗口线程上执行）。</param>
    public static Action Subscribe(nint hwnd, uint message, Action<nint, nint> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (hwnd == nint.Zero) return () => { };

        if (!ParamHandlers.TryGetValue(message, out var list))
        {
            list = [];
            ParamHandlers[message] = list;
        }

        list.Add(callback);
        EnsureHooked(hwnd);

        return () =>
        {
            if (ParamHandlers.TryGetValue(message, out var current)) current.Remove(callback);
        };
    }

    /// <summary>
    /// 把 <paramref name="value"/> 当作这条消息的返回值（覆盖原窗口过程的结果）。
    ///
    /// **只对当前这条消息有效**：<see cref="HookProc"/> 在每条消息开始时复位，
    /// 免得一次命中测试的结果粘到后面的消息上。
    /// </summary>
    /// <param name="value">要返回的值（例如 <c>HTLEFT</c> = 10）。</param>
    public static void SetResult(nint value)
    {
        _hasResult = true;
        _result = value;
    }

    /// <summary>这条消息是否已有回调给出返回值。</summary>
    private static bool _hasResult;

    /// <summary>回调给出的返回值。</summary>
    private static nint _result;

    private static void EnsureHooked(nint hwnd)
    {
        if (_hooked == hwnd && _originalProc != nint.Zero) return;

        _hookProc ??= HookProc;
        _originalProc = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_hookProc));
        _hooked = hwnd;
    }

    private static nint HookProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        _hasResult = false;

        if (ParamHandlers.TryGetValue(message, out var paramList))
        {
            foreach (var callback in paramList.ToArray())
            {
                try
                {
                    callback(wParam, lParam);
                }
                catch (Exception ex)
                {
                    // 回调失败不能影响窗口的正常消息处理
                    AppLog.Error($"[hook] 消息回调异常 0x{message:X}：{ex.Message}");
                }
            }
        }

        if (Handlers.TryGetValue(message, out var list))
        {
            foreach (var callback in list.ToArray())
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    AppLog.Error($"[hook] 消息回调异常 0x{message:X}：{ex.Message}");
                }
            }
        }

        // 有回调给了明确返回值就直接用它，否则走原窗口过程
        return _hasResult
            ? _result
            : NativeMethods.CallWindowProc(_originalProc, hwnd, message, wParam, lParam);
    }
}
