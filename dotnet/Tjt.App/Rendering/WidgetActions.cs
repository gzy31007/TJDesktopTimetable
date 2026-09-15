namespace Tjt.App.Rendering;

/// <summary>
/// 挂件窗口上的动作回调集合（顶部条的按钮与 <c>⋯</c> 菜单用）。
///
/// <para>渲染层不认识窗口与服务，只发"用户点了什么"这种意图；具体做什么由外壳决定
/// —— 这样"设置 / 刷新 / 隐藏 / 退出"的逻辑仍然留在 <c>MainWindow</c>，渲染层保持无脑。</para>
/// </summary>
public sealed record WidgetActions
{
    /// <summary>打开设置窗口。</summary>
    public Action? OpenSettings { get; init; }

    /// <summary>重新载入课表（改过 fixture / 设置之后不用重启）。</summary>
    public Action? Refresh { get; init; }

    /// <summary>把挂件放回默认位置（右下角）。</summary>
    public Action? ResetPosition { get; init; }

    /// <summary>当前是否贴桌面层（<c>null</c> = 不显示这一项）。</summary>
    public bool? DesktopLayer { get; init; }

    /// <summary>切换贴桌面层。</summary>
    public Action? ToggleDesktopLayer { get; init; }

    /// <summary>隐藏挂件（本次运行内；下次启动照常显示）。</summary>
    public Action? Hide { get; init; }

    /// <summary>退出应用。</summary>
    public Action? Exit { get; init; }

    /// <summary>
    /// 把顶部条注册成窗口拖动区（系统标题栏已被移除，拖动由 <c>WindowDrag</c> 自实现）。
    /// </summary>
    public Action<Microsoft.UI.Xaml.FrameworkElement>? AttachDragArea { get; init; }
}
