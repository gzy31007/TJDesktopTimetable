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
/// <para>实现要点：原窗口过程必须转发回去，否则窗口会失去全部默认行为（拖动、缩放、
/// 关闭都会失灵）。回调在窗口线程上同步执行，所以回调里只做轻量操作。</para>
/// </summary>
internal static class MessageHook
{
    private static readonly Dictionary<uint, List<Action>> Handlers = [];
    private static NativeMethods.WndProc? _hookProc; // 保引用：委托被 GC 会让原生回调指向空
    private static nint _originalProc;
    private static nint _hooked;

    /// <summary>
    /// 订阅 <paramref name="message"/>；返回取消订阅的委托。
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

    private static void EnsureHooked(nint hwnd)
    {
        if (_hooked == hwnd && _originalProc != nint.Zero) return;

        _hookProc ??= HookProc;
        _originalProc = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.Constants.GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_hookProc));
        _hooked = hwnd;
    }

    private static nint HookProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
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
                    // 回调失败不能影响窗口的正常消息处理
                    AppLog.Error($"[hook] 消息回调异常 0x{message:X}：{ex.Message}");
                }
            }
        }

        return NativeMethods.CallWindowProc(_originalProc, hwnd, message, wParam, lParam);
    }
}
