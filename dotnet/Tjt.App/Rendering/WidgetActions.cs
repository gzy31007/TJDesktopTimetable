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

    /// <summary>直接打开设置窗口的「导入课表」页（挂件上换课表的入口）。</summary>
    public Action? OpenImport { get; init; }

    /// <summary>重新载入课表（改过 fixture / 设置之后不用重启）。</summary>
    public Action? Refresh { get; init; }

    /// <summary>
    /// 「下载新版」：<c>null</c> = 没有待提示的新版本，顶部条不出现这个按钮。
    ///
    /// <para>用"回调的有无"表达可见性，而不是再传一个 <c>HasUpdate</c> 布尔：两个入参总有
    /// 一方忘记同步的时候，结果就是按钮出现了却点不动（或者点得动却该消失）。渲染层只需要
    /// 知道"有没有活可干"，判断留给外壳。</para>
    ///
    /// <para>打开哪个地址由外壳在**点击时**按当时的结论算 —— 链接挑选规则（安装包直链 /
    /// Release 页 / 仓库首页的兜底顺序、镜像前缀）只存在于 <c>UpdateCheck</c> 一处，
    /// 挂件顶部与托盘因此不会跳出两个不同的地址。</para>
    /// </summary>
    public Action? DownloadUpdate { get; init; }

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

    /// <summary>隐藏挂件（本次运行内；下次启动照常显示）。</summary>
    public Action? Hide { get; init; }

    /// <summary>退出应用。</summary>
    public Action? Exit { get; init; }

    /// <summary>
    /// 把顶部条注册成窗口拖动区（系统标题栏已被移除，拖动由 <c>WindowDrag</c> 自实现）。
    /// </summary>
    public Action<Microsoft.UI.Xaml.FrameworkElement>? AttachDragArea { get; init; }

    /// <summary>
    /// 报告"用户按下的正是哪条缩放边/哪个角"（由边缘光标条的 <c>PointerPressed</c> 触发）。
    ///
    /// <para>为什么由渲染层报告而不是外壳自己重新判定：边/角是**元素**，按下时元素自己是确定的
    /// （`TopLeft` 就是 `TopLeft`）；外壳的轮询只能从光标坐标反推，而坐标反推在角上要靠
    /// "先角后边"的分支才勉强对，且按下与轮询之间还有一拍延迟。让按下元素直接说，最准也最快。</para>
    /// </summary>
    public Action<Tjt.Widget.ResizeGrip>? ReportResizeGrip { get; init; }
}
