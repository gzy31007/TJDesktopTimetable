using Avalonia.Controls;
using Avalonia.Input;

namespace Tjt.Linux.Rendering;

/// <summary>
/// 挂件窗口上的动作回调集合（Tjt.App/Rendering/WidgetActions.cs 的移植）。
///
/// <para>渲染层不认识窗口与服务，只发"用户点了什么"这种意图；具体做什么由外壳决定，
/// 这样渲染层保持无脑。Linux 版没有设置窗口与托盘，动作集合比 Windows 版小。</para>
/// </summary>
public sealed record WidgetActions
{
    /// <summary>打开导入窗口（Linux 版没有内置登录窗口，导入窗口就是换课表的唯一入口）。</summary>
    public Action? OpenImport { get; init; }

    /// <summary>重新载入课表（改过 fixture / 设置之后不用重启）。</summary>
    public Action? Refresh { get; init; }

    /// <summary>把挂件放回默认位置（右下角）。</summary>
    public Action? ResetPosition { get; init; }

    /// <summary>当前是否贴桌面层（<c>null</c> = 不显示这一项）。</summary>
    public bool? DesktopLayer { get; init; }

    /// <summary>切换贴桌面层。</summary>
    public Action? ToggleDesktopLayer { get; init; }

    /// <summary>当前是否显示周末（<c>null</c> = 不显示这一项）。</summary>
    public bool? ShowWeekend { get; init; }

    /// <summary>切换"显示周末"。</summary>
    public Action? ToggleShowWeekend { get; init; }

    /// <summary>当前周次视图（<c>null</c> = 不显示这一项）。</summary>
    public Tjt.Core.WeekView? WeekView { get; init; }

    /// <summary>切到某个周次视图（四项互斥，选中项由 <see cref="WeekView"/> 表达）。</summary>
    public Action<Tjt.Core.WeekView>? SetWeekView { get; init; }

    /// <summary>退出应用。</summary>
    public Action? Exit { get; init; }

    /// <summary>
    /// 把顶部条注册成窗口拖动区（系统标题栏已被移除；Avalonia 的
    /// <c>BeginMoveDrag</c> 由外壳在指针事件里调）。
    /// </summary>
    public Action<Control>? AttachDragArea { get; init; }

    /// <summary>
    /// 从边缘热区开始缩放（热区元素在 <c>PointerPressed</c> 里把自己的方向报上来，
    /// 外壳转给 <c>BeginResizeDrag</c> —— 与 Windows 版"元素自己知道按在角上"同一个理由）。
    /// </summary>
    public Action<WindowEdge, PointerPressedEventArgs>? BeginResize { get; init; }
}
