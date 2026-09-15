using System.Runtime.InteropServices;

namespace Tjt.App.Win32;

/// <summary>托盘菜单项（<c>null</c> id 表示分隔线）。</summary>
/// <param name="Id">命令 id。</param>
/// <param name="Text">显示文案。</param>
/// <param name="Checked">是否显示勾选（用于"贴桌面层"这类开关项）。</param>
internal sealed record TrayMenuItem(uint? Id, string Text, bool Checked = false);

/// <summary>
/// 托盘图标（<c>Shell_NotifyIcon</c> + 原生右键菜单）。
///
/// <para><b>为什么手写而不用现成封装</b>：WinUI 3 / WASDK 1.8 没有托盘 API
/// （`Microsoft.UI.Xaml` 里没有 TaskbarIcon，那是社区工具包 CommunityToolkit 的东西，
/// 本项目不想为此多引一个包），所以直接用 user32/shell32。</para>
///
/// <para><b>为什么用"消息专用窗口"</b>：托盘回调要求一个 <c>hWnd</c>。
/// 借用挂件窗口也能跑，但会牵进"菜单弹出时窗口被激活、z-order 被搅动"的副作用；
/// 建一个 <c>HWND_MESSAGE</c> 窗口则完全离屏、不参与层级 —— 这正是它存在的用途。</para>
///
/// <para>右键菜单用 <c>TrackPopupMenu(TPM_RETURNCMD)</c> 同步取回选中项，
/// 因此不需要 <c>WM_COMMAND</c> 分发，也不依赖外部消息循环。</para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000 + 1;      // WM_APP + 1：托盘回调消息
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmContextMenu = 0x007B;
    private const uint MfString = 0x0000;
    private const uint MfChecked = 0x0008;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NiifMessage = 0x00000001;
    private const uint NiifIcon = 0x00000002;
    private const uint NiifTip = 0x00000004;

    private static readonly nint HwndMessage = -3;

    private readonly string _tooltip;
    private readonly Action<uint> _onCommand;
    private readonly Action? _onLeftClick;
    private readonly Dictionary<uint, List<TrayMenuItem>> _menus = [];

    private nint _hwnd;
    private nint _icon;
    private WndProcDelegate? _proc;   // 保引用：被 GC 会让原生回调指向空

    private TrayIcon(string tooltip, Action<uint> onCommand, Action? onLeftClick)
    {
        _tooltip = tooltip;
        _onCommand = onCommand;
        _onLeftClick = onLeftClick;
    }

    /// <summary>
    /// 创建并显示托盘图标。
    /// </summary>
    /// <param name="tooltip">悬停提示。</param>
    /// <param name="iconPath">图标文件（<c>.ico</c>）路径。</param>
    /// <param name="onCommand">菜单命令回调（收到命令 id）。</param>
    /// <param name="onLeftClick">左键单击回调（一般为"显示挂件"）。</param>
    public static TrayIcon Create(string tooltip, string iconPath, Action<uint> onCommand, Action? onLeftClick = null)
    {
        var tray = new TrayIcon(tooltip, onCommand, onLeftClick);
        tray.RegisterWindow();
        tray.LoadIcon(iconPath);
        tray.AddOrUpdate(NimAdd);
        return tray;
    }

    /// <summary>设置右键菜单（每次弹出前调用，以便刷新勾选状态）。</summary>
    /// <param name="menuId">菜单 id（本实现固定用 1）。</param>
    /// <param name="items">菜单项。</param>
    public void SetMenu(uint menuId, IReadOnlyList<TrayMenuItem> items) => _menus[menuId] = [.. items];

    /// <summary>更新悬停提示（例如显示"今日 N 节"）。</summary>
    public void UpdateTooltip(string tooltip) => AddOrUpdate(NimModify, tooltip);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_hwnd != nint.Zero)
        {
            AddOrUpdate(NimDelete, _tooltip, forceNoIcon: true);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }

        if (_icon != nint.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = nint.Zero;
        }
    }

    /* ------------------------------------------------------------------ 内部 */

    private void RegisterWindow()
    {
        _proc = OnMessage;
        var className = $"TjtTray_{Environment.ProcessId}";
        var wc = new NativeMethods.WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className,
        };
        NativeMethods.RegisterClass(ref wc);
        _hwnd = NativeMethods.CreateWindowEx(0, className, "TjtTray", 0, 0, 0, 0, 0, HwndMessage, nint.Zero, wc.hInstance, nint.Zero);
        if (_hwnd == nint.Zero)
        {
            AppLog.Error($"[tray] 创建消息窗口失败：{Marshal.GetLastWin32Error()}");
        }
    }

    private void LoadIcon(string path)
    {
        _icon = File.Exists(path)
            ? NativeMethods.LoadImage(nint.Zero, path, ImageIcon, 0, 0, LrLoadFromFile)
            : NativeMethods.LoadIcon(nint.Zero, new nint(32512)); // IDI_APPLICATION 兜底
        if (_icon == nint.Zero)
        {
            AppLog.Line("[tray] 图标加载失败，用系统默认图标");
        }
    }

    private void AddOrUpdate(uint action, string? tooltip = null, bool forceNoIcon = false)
    {
        if (_hwnd == nint.Zero) return;
        var data = new NativeMethods.NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NiifMessage | NiifTip | (forceNoIcon ? 0 : NiifIcon),
            uCallbackMessage = WmApp,
            hIcon = forceNoIcon ? nint.Zero : _icon,
            szTip = tooltip ?? _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        if (!NativeMethods.ShellNotifyIcon(action, ref data))
        {
            AppLog.Error($"[tray] Shell_NotifyIcon 失败 action={action} err={Marshal.GetLastWin32Error()}");
        }
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmApp:
                // lParam 低位是鼠标消息（老式约定）
                var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouse is WmLButtonUp or 0x0203 /* WM_LBUTTONDBLCLK */)
                {
                    _onLeftClick?.Invoke();
                    return nint.Zero;
                }

                if (mouse is WmRButtonUp or WmContextMenu)
                {
                    ShowMenu();
                    return nint.Zero;
                }

                return nint.Zero;

            case NativeMethods.Constants.WmSettingChange:
            case NativeMethods.Constants.WmDisplayChange:
                // 任务栏重建（Explorer 重启）后图标会消失，需要重新添加
                AddOrUpdate(NimAdd);
                return nint.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>在光标处弹出右键菜单，并按选中项回调。</summary>
    private void ShowMenu()
    {
        if (_menus.Count == 0 || !_menus.TryGetValue(1, out var items)) return;

        var menu = NativeMethods.CreatePopupMenu();
        if (menu == nint.Zero) return;
        try
        {
            foreach (var item in items)
            {
                if (item.Id is not { } id)
                {
                    NativeMethods.AppendMenu(menu, MfSeparator, 0, null);
                    continue;
                }

                var flags = MfString | (item.Checked ? MfChecked : 0);
                NativeMethods.AppendMenu(menu, flags, id, item.Text);
            }

            NativeMethods.GetCursorPos(out var point);
            // TPM_RETURNCMD：同步拿回命令 id，不需要 WM_COMMAND 分发；
            // SetForegroundWindow 是托盘菜单的标准前置动作，否则点外面菜单不消失。
            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _hwnd, nint.Zero);
            if (command != 0) _onCommand(command);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    /* ------------------------------------------------------------------ 互操作 */

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

}
