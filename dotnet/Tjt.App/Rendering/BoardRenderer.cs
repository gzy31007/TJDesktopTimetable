using Microsoft.UI;
using Microsoft.UI.Input;
using System.IO;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tjt.Widget;
using Windows.UI;

namespace Tjt.App.Rendering;

/// <summary>
/// 把 <see cref="BoardVisual"/> 摆到 XAML 上。
///
/// 这一层刻意"没有脑子"：坐标、字号、文案、染色全部由 <c>Tjt.Widget</c> 算好
/// （那部分能在 Linux 上单测），这里只做"照着数字放控件"。
///
/// 结构对应渲染层 WidgetApp.vue 的三段式：**顶部信息条**（学期 / 周次 / 今日节数）+
/// **网格**（可能横向或纵向滚动）。窗口尺寸变化时整棵树重建 —— 与渲染层重新计算
/// 布局是同一个语义，而重建 100 来个控件对几 Hz 的 resize 完全够用。
/// </summary>
internal static class BoardRenderer
{
    /// <summary>顶部条里的字号（比网格文字略大，作为层级提示）。</summary>
    private const double HeaderFontSize = 13;

    /// <summary>顶部条下方的分隔线高度。</summary>
    private const double HeaderRuleHeight = 1;

    /// <summary>
    /// 顶部条「下载新版」按钮的 AutomationId —— 隔离实机验收靠它定位，改它等于改验收契约。
    /// </summary>
    internal const string UpdateButtonId = "DownloadUpdateButton";

    /// <summary>与实际课程行共用 TextBlock 配置，按 Windows 字体度量单行高度。</summary>
    internal static double MeasureContentLine(double fontSize, bool semibold, double width, string text,
        XamlRoot? xamlRoot)
    {
        var label = ContentLine(text, fontSize, semibold);
        if (xamlRoot is not null) label.XamlRoot = xamlRoot;
        label.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        return label.DesiredSize.Height;
    }

    private static TextBlock ContentLine(string text, double fontSize, bool semibold) => new()
    {
        Text = text,
        UseLayoutRounding = false,
        FontSize = fontSize,
        FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    /// <summary>
    /// 渲染整块课表：返回可直接塞进窗口的根元素。
    /// </summary>
    /// <param name="visual">呈现模型。</param>
    /// <param name="dark">是否深色主题。</param>
    public static FrameworkElement Render(BoardVisual visual, bool dark, WidgetActions? actions = null,
        FrameworkElement? previous = null, string? contentIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(visual);
        actions ??= new WidgetActions();

        var shell = previous as BoardShell ?? new BoardShell();
        var root = shell.Body;
        var header = BuildHeaderBar(visual.Header, dark, actions);
        Grid.SetRow(header, 0);
        if (root.Children.Count > 0) root.Children.RemoveAt(0);
        root.Children.Insert(0, header);
        // 时间推进仍重算整个呈现模型，但未变化的摘要不能卸载：用户可能正在浮层内读完整组。
        if (shell.Summary is null || shell.Dark != dark || !SameSummary(shell.Summary, visual.NextClass))
        {
            if (root.Children.Count > 1) root.Children.RemoveAt(1);
            var summary = BuildNextClassRow(visual.NextClass, dark);
            Grid.SetRow(summary, 1);
            root.Children.Insert(1, summary);
        }
        shell.Summary = visual.NextClass;
        shell.Dark = dark;
        // 上一轮渲染的滚动查看器：外壳记下的那一个最可靠；按行号兜底只用于外壳尚未记录的历史状态。
        var previousScroller = shell.Scroller ?? root.Children.OfType<ScrollViewer>().FirstOrDefault();
        // 连续重绘可能发生在替代控件 Loaded 之前；此时它的零偏移不是用户的新阅读位置。
        var oldHorizontalOffset = previousScroller is { IsLoaded: true }
            ? previousScroller.HorizontalOffset : shell.PendingHorizontalOffset;
        var oldVerticalOffset = previousScroller is { IsLoaded: true }
            ? previousScroller.VerticalOffset : shell.PendingVerticalOffset;
        var sameContent = shell.ContentIdentity == contentIdentity;
        shell.ContentIdentity = contentIdentity;

        // 星期表头从画布里独立出来：纵向滚动只带走节次 / 网格 / 色块 / 时间线，表头留在原处。
        // 固定区高度取 Grid.Top（顶部呼吸位 + 星期头），滚动画布同步扣掉这一截，
        // 于是整块课表的纵向预算与「表头画在画布里」时完全一致。
        var fixedRegion = visual.Grid.Top;
        var dayStrip = BuildDayHeaderStrip(visual, dark);
        var dayHost = BuildDayHeaderHost(dayStrip, fixedRegion);
        var canvas = BuildCanvas(visual, dark, fixedRegion);
        // 网格可能比可用空间大（列宽或行高到了下限）—— 用 ScrollViewer 兜住，
        // 与渲染层 `.f-scroll` 的表现一致；装得下时滚动条不会出现。
        //
        // ⚠️ `VerticalContentAlignment = Top` 不能省：ScrollViewer 默认把**装得下的内容垂直居中**，
        // 于是画布 < 视口时（例如窗口 1100×760：画布 612 dip、视口 726 dip）上下各留
        // (726-612)/2 = 57 dip 空白，表头离头部条 57 dip —— 顶部那 6 dip 呼吸位的设计被架空，
        // 真机截图一眼可见"课表浮在中间"。贴顶后呼吸位才真的是 6 dip。
        var scroller = new ScrollViewer
        {
            Content = canvas,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = visual.NeedsHorizontalScroll
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            // Auto 按真实内容范围开关；Disabled 会把测量范围压到视口，掩盖像素舍入造成的裁剪。
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Padding = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(scroller, "BoardScrollViewer");
        scroller.Loaded += (_, _) => AppLog.Line(FormattableString.Invariant(
            $"[board-scroll] required={visual.RequiredHeight:0.###}dip viewport={scroller.ViewportWidth:0.###}x{scroller.ViewportHeight:0.###}dip extent={scroller.ExtentWidth:0.###}x{scroller.ExtentHeight:0.###}dip range={scroller.ScrollableWidth:0.###}/{scroller.ScrollableHeight:0.###} offset={scroller.HorizontalOffset:0.###}/{scroller.VerticalOffset:0.###} enabled={visual.NeedsHorizontalScroll}/{visual.NeedsVerticalScroll}"));
        Grid.SetRow(scroller, 3);
        shell.PendingHorizontalOffset = sameContent ? oldHorizontalOffset : 0;
        shell.PendingVerticalOffset = sameContent ? oldVerticalOffset : 0;
        shell.Scroller = scroller;
        if (previousScroller is not null)
        {
            scroller.Loaded += (_, _) =>
            {
                if (!ReferenceEquals(shell.Scroller, scroller)) return;
                scroller.ChangeView(
                    Math.Clamp(shell.PendingHorizontalOffset, 0, scroller.ScrollableWidth),
                    Math.Clamp(shell.PendingVerticalOffset, 0, scroller.ScrollableHeight),
                    null, disableAnimation: true);
            };
        }

        // 横向：表头与内容共用同一个 HorizontalOffset。表头用「裁剪宿主 + 平移」跟随，
        // 不额外加一条横向滚动条（不加高），也没有让旧表头继续滚动再拿覆盖层遮住的把戏。
        var dayShift = new TranslateTransform();
        dayStrip.RenderTransform = dayShift;
        void SyncDayHeader(double offset) => dayShift.X = -Math.Max(0, offset);
        SyncDayHeader(shell.PendingHorizontalOffset);
        scroller.ViewChanged += (_, _) => SyncDayHeader(scroller.HorizontalOffset);
        // 裁剪边界必须等于**内容视口**宽而不是客户区宽：纵向滚动条若占宽，
        // 内容视口会窄一截，表头按客户区宽铺开就会比内容多露出右侧一列。
        void FitDayHeader()
        {
            var width = scroller.ViewportWidth > 0 ? scroller.ViewportWidth : dayHost.ActualWidth;
            if (width <= 0) return;
            dayHost.Clip = new RectangleGeometry
            { Rect = new Windows.Foundation.Rect(0, 0, width, fixedRegion) };
        }
        dayHost.SizeChanged += (_, _) => FitDayHeader();
        scroller.SizeChanged += (_, _) => FitDayHeader();
        scroller.Loaded += (_, _) => FitDayHeader();

        ClearScrollRegion(root);
        Grid.SetRow(dayHost, 2);
        root.Children.Add(dayHost);
        root.Children.Add(scroller);

        // 边缘光标条放在**独立覆盖层**里，和整块课表同处一格（z 序在后 → 盖在上面）。
        // 不要把它们加进 `root` 本身：实测那样会把根网格的两行布局搞坏 ——
        // 整个课表被推到下方、上方留一大片空白（`--size` 下的截图对比确认）。
        // 用 WinUI 原生光标（`ProtectedCursor`）而不是 Win32 `SetCursor`：后者会被窗口过程
        // 在 WM_SETCURSOR 里按类光标重置（真机实测"能缩放但看不到缩放光标"）。
        // 只挂光标、不挂事件处理：拖拽判定仍在 Win32 侧（WindowEdgeResize）。
        if (shell.Children.Count > 1) shell.Children.RemoveAt(1);
        var overlay = new Grid { IsHitTestVisible = true };
        var totalW = visual.CanvasWidth;
        var totalH = visual.CanvasHeight;
        foreach (var zone in CursorZones.ForWindow(totalW, totalH))
        {
            // 用四边对齐 + Margin 精确定位：每块热区都是"贴哪两条边、离另一条边多远"。
            // 这样不依赖父容器的行/列定义，也就不会再出现"元素落到错误行把布局撑坏"那类问题
            // （本轮真的踩过：元素默认落 row 0，把 Auto 行撑到整窗高）。
            var strip = new CursorStrip(zone, actions.ReportResizeGrip);
            var left = zone.X;
            var top = zone.Y;
            var right = totalW - (zone.X + zone.Width);
            var bottom = totalH - (zone.Y + zone.Height);
            strip.HorizontalAlignment = left <= right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            strip.VerticalAlignment = top <= bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            strip.Margin = new Thickness(
                left <= right ? left : right,
                top <= bottom ? top : bottom,
                0,
                0);
            overlay.Children.Add(strip);
        }

        shell.Children.Add(overlay);
        return shell;
    }

    private sealed class BoardShell : Grid
    {
        internal Grid Body { get; } = new();
        internal NextClassVisual? Summary { get; set; }
        internal bool Dark { get; set; }
        internal string? ContentIdentity { get; set; }
        internal ScrollViewer? Scroller { get; set; }
        internal double PendingHorizontalOffset { get; set; }
        internal double PendingVerticalOffset { get; set; }

        internal BoardShell()
        {
            UseLayoutRounding = false;
            Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(NextClassSummary.RowHeight) });
            // 固定星期头：Auto 行由宿主 Height（= visual.Grid.Top）撑开，和滚动区严格分段、互不重叠。
            Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Children.Add(Body);
        }
    }

    /// <summary>摘掉上一轮渲染追加在摘要行之下的滚动区（固定星期头 + 滚动查看器）。</summary>
    private static void ClearScrollRegion(Grid root)
    {
        for (var index = root.Children.Count - 1; index >= 0; index -= 1)
            if (root.Children[index] is FrameworkElement element && Grid.GetRow(element) >= 2)
                root.Children.RemoveAt(index);
    }

    private static bool SameSummary(NextClassVisual first, NextClassVisual second) =>
        first.Group.Status == second.Group.Status && first.Group.StartsAt == second.Group.StartsAt
        && first.Group.LocalToday == second.Group.LocalToday && first.Tooltip == second.Tooltip
        // 不能只比较拼接后的悬停字符串：名称／教室中的分隔符可能造成相同字符串、不同宽度优先级。
        && first.Items.Count == second.Items.Count
        && first.Items.Zip(second.Items).All(pair => pair.First.Name == pair.Second.Name
            && pair.First.Room == pair.Second.Room);

    private static FrameworkElement BuildNextClassRow(NextClassVisual initial, bool dark)
    {
        var text = new SolidColorBrush(Parse(TintPalette.Text(dark)));
        var soft = new SolidColorBrush(Parse(TintPalette.TextSoft(dark)));
        var host = new Grid
        {
            Height = NextClassSummary.RowHeight,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, "NextClassSummary");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, initial.Tooltip);
        // ToolTip 不接收滚动交互。用覆盖整个摘要的 hover owner 打开可移入的 Popup，
        // 完整同刻组只在浮层里滚动；常驻行始终保持固定高度。
        NextClassHoverPopup.Attach(host, initial.Tooltip, dark, text,
            new SolidColorBrush(Parse(TintPalette.Stroke(dark))));
        TextBlock Label(string value, double? width = null, bool secondary = false) => new()
        {
            Text = value, FontSize = NextClassSummary.FontSize,
            Foreground = secondary ? soft : text,
            Width = width ?? double.NaN,
            TextWrapping = TextWrapping.NoWrap, MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        double Measure(string value)
        {
            var probe = Label(value);
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, NextClassSummary.RowHeight));
            return probe.DesiredSize.Width;
        }
        void Rebuild(double width)
        {
            var visual = NextClassSummary.Build(initial.Group, width, Measure);
            var line = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            if (visual.Message is { } message)
            {
                line.Children.Add(Label(message, secondary: true));
            }
            else
            {
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NextClassSummary.MarkerWidth) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var marker = Label(visual.Marker!);
                marker.Foreground = new SolidColorBrush(Parse(TintPalette.Accent(dark)));
                marker.FontWeight = FontWeights.SemiBold;
                line.Children.Add(marker);
                var items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = NextClassSummary.ItemGap };
                foreach (var item in visual.Items)
                {
                    var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = NextClassSummary.NameRoomGap };
                    pair.Children.Add(Label(item.DisplayName, item.NameWidth));
                    pair.Children.Add(Label(item.DisplayRoom, item.RoomWidth));
                    items.Children.Add(pair);
                }
                var content = new Grid();
                content.Children.Add(items);
                content.SizeChanged += (_, e) => content.Clip = new RectangleGeometry
                { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
                Grid.SetColumn(content, 1);
                line.Children.Add(content);
                if (visual.ShowAuxiliary)
                {
                    var auxiliary = Label($"{visual.DateText} {visual.StartText}", secondary: true);
                    auxiliary.Margin = new Thickness(NextClassSummary.ItemGap, 0, 0, 0);
                    Grid.SetColumn(auxiliary, 2);
                    line.Children.Add(auxiliary);
                }
                if (visual.CountText is { } count)
                {
                    var quantity = Label(count, secondary: true);
                    quantity.Margin = new Thickness(NextClassSummary.ItemGap, 0, 0, 0);
                    Grid.SetColumn(quantity, 3);
                    line.Children.Add(quantity);
                }
            }
            host.Children.Clear();
            host.Children.Add(line);
            host.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, NextClassSummary.RowHeight) };
        }
        host.SizeChanged += (_, e) => { if (e.NewSize.Width > 0) Rebuild(e.NewSize.Width); };
        return host;
    }

    /// <summary>
    /// 顶部信息条 —— 布局照资源管理器卡片头：**左边图标 + 标题 + 元信息，右边一组动作按钮**。
    ///
    /// 动作按钮是这个窗口唯一的交互入口（没有系统标题栏）：
    /// 刷新课表、以及一个 <c>⋯</c> 溢出菜单（设置 / 恢复默认位置 / 贴桌面层 / 隐藏 / 退出）。
    /// 查到新版本时右侧还会多一个「下载新版」（见 <see cref="BuildUpdateButton"/>）。
    /// </summary>
    private static FrameworkElement BuildHeaderBar(BoardHeader header, bool dark, WidgetActions actions)
    {
        var accent = TintPalette.Accent(dark);
        var text = TintPalette.Text(dark);
        var soft = TintPalette.TextSoft(dark);

        var host = new Grid { Height = BoardVisualBuilder.HeaderHeight };

        var row = new Grid { Padding = new Thickness(10, 0, 8, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── 左：应用图标（强调色圆角块 + 白色显示器字形，与托盘/任务栏图标同源）
        var left = new Grid { VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 8 };
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.Children.Add(BuildAppGlyph(accent));
        var title = new TextBlock
        {
            Text = header.Title, FontSize = HeaderFontSize, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Parse(header.IsHoliday ? soft : text)),
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(title, header.Title);
        Grid.SetColumn(title, 1);
        left.Children.Add(title);
        var week = new TextBlock
        {
            Text = header.WeekText, FontSize = HeaderFontSize - 1,
            Foreground = new SolidColorBrush(Parse(soft)), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(week, 2);
        left.Children.Add(week);
        if (header.TodayText is { Length: > 0 } today)
        {
            var todayLabel = new TextBlock
            {
                Text = $"· {today}", FontSize = HeaderFontSize - 1,
                Foreground = new SolidColorBrush(Parse(soft)), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(todayLabel, 3);
            left.Children.Add(todayLabel);
        }
        left.SizeChanged += (_, e) => left.Clip = new RectangleGeometry
        { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        // 顶部条空白处 = 窗口拖动区（系统标题栏已被移除）。按钮是 row 的子元素、
        // 会先吃掉自己的点击，所以不会误触发拖动。
        // 具体怎么拖（自实现位移）由外壳的 WindowDrag 负责，渲染层只负责"这块能抓"。
        row.Background = new SolidColorBrush(Colors.Transparent); // 空白处也要能命中指针
        actions.AttachDragArea?.Invoke(row);

        // ── 右：动作按钮（有新版本时先出现「下载新版」，然后才是刷新 + ⋯ 菜单）
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 有没有新版只看外壳给没给回调（见 WidgetActions.DownloadUpdate）：没有就整块不建，
        // 顶部条在"没新版"时的排布与以前逐像素一致。
        if (actions.DownloadUpdate is { } download)
        {
            right.Children.Add(BuildUpdateButton(dark, download));
        }

        right.Children.Add(BuildIconButton(IconGlyph.Refresh, dark, "重新载入课表", actions.Refresh));
        right.Children.Add(BuildOverflowButton(dark, actions));

        Grid.SetColumn(right, 1);
        row.Children.Add(right);

        host.Children.Add(row);
        host.Children.Add(new Rectangle
        {
            Height = HeaderRuleHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new SolidColorBrush(Parse(TintPalette.GridLine(dark))),
        });
        return host;
    }

    /// <summary>
    /// 应用图标（头部用）：与托盘同一个字形（Segoe Fluent Icons 的 Win11 日历 <c>E787</c>）。
    ///
    /// 头部用 <see cref="FontIcon"/> 而不是位图：小尺寸下矢量字形比缩放位图清晰，
    /// 而且能直接跟随主题前景色。托盘那边必须是位图（`Shell_NotifyIcon` 只吃 HICON），
    /// 所以图标文件由同一字形渲染而来，两处观感一致。
    /// </summary>
    private static FrameworkElement BuildAppGlyph(string accent)
    {
        return new FontIcon
        {
            Glyph = IconGlyph.Calendar,
            FontSize = 15,
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Parse(accent)),
        };
    }

    /// <summary>无边框图标按钮（Segoe Fluent Icons 字形），悬停用 Fluent 的 subtle 底。</summary>
    private static Button BuildIconButton(string glyph, bool dark, string tooltip, Action? action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Parse(TintPalette.TextSoft(dark))),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tooltip);
        // 挂件里不该出现键盘焦点框（Tab 也不该停在这里）
        button.IsTabStop = false;
        button.UseSystemFocusVisuals = false;
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// 顶部条的「下载新版」文本按钮（强调色）：只在真有新版本时**存在**
    /// （回调由外壳按 <c>CurrentUpdate.HasUpdate</c> 决定给不给），所以它的有无本身就是
    /// "有没有新版"的信号，不用再单独传一个布尔。
    ///
    /// <para><b>窗口拖到最小宽度（320 DIP）也不让位</b>：它在右侧 <c>Auto</c> 列里，左半边是
    /// <c>Star</c>，于是被压缩掉的只有左侧标题（那里本来就有
    /// <see cref="TextTrimming.CharacterEllipsis"/>，会截成省略号），按钮自己不参与收缩。
    /// 宽度是"文字自然宽 + 左右各 8 DIP"，没写死数字，换字号也不用跟着改。</para>
    ///
    /// <para><b>排在刷新 / ⋯ 之前</b>：两颗图标按钮的横向位置因此恒定 —— 新版提示出现或消失时，
    /// 用户的手不必重新找"刷新"。</para>
    ///
    /// <para><b>AutomationId = <see cref="UpdateButtonId"/></b>：隔离实机验收按它定位（与
    /// <c>BoardScrollViewer</c> 同一套做法）。同样不占 Tab 焦点、不画焦点框。</para>
    /// </summary>
    private static Button BuildUpdateButton(bool dark, Action download)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = "下载新版",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                // 颜色写在文字上而不是按钮上：悬停态由模板改的是按钮的 Foreground，
                // 那样一悬停强调色就被换成普通前景色，看起来像变成了另一个控件。
                Foreground = new SolidColorBrush(Parse(TintPalette.Accent(dark))),
                VerticalAlignment = VerticalAlignment.Center,
            },
            // 与图标按钮同高：两者底边对齐，顶部条看起来是一条而不是高低不一
            Height = 26,
            Padding = new Thickness(8, 0, 8, 0),
            MinWidth = 0,
            Margin = new Thickness(0, 0, 4, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, UpdateButtonId);
        // 内容是一整个 TextBlock，读屏器不总是能从中推出名字；显式给一句，
        // 让无障碍名字与隔离验收断言的是同一句。
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "下载新版");
        ToolTipService.SetToolTip(button, "打开新版本的下载页");
        button.IsTabStop = false;
        button.UseSystemFocusVisuals = false;
        button.Click += (_, _) => download();
        return button;
    }

    /// <summary>
    /// <c>⋯</c> 溢出菜单：设置 / 恢复默认位置 / 贴桌面层开关 / 显示周末开关 / 隐藏挂件 / 退出。
    ///
    /// 菜单里的两个**开关**（"贴桌面层"、"显示周末"）用 <see cref="ToggleMenuFlyoutItem"/> 反映当前值 ——
    /// 它们是唯一能用勾选状态表达"当前是否生效"的项，其余都是动作。周次视图是**四选一**，
    /// 所以单独用「周次视图」子菜单 + <see cref="RadioMenuFlyoutItem"/>。
    /// </summary>
    private static Button BuildOverflowButton(bool dark, WidgetActions actions)
    {
        var flyout = new MenuFlyout();

        var settings = new MenuFlyoutItem { Text = "设置", Icon = new FontIcon { Glyph = IconGlyph.Settings } };
        settings.Click += (_, _) => actions.OpenSettings?.Invoke();
        flyout.Items.Add(settings);

        // 换课表的入口放在最常用的位置：没导入过的挂件显示的是内置示例课表，用户第一件事就是导入
        var import = new MenuFlyoutItem { Text = "导入课表…", Icon = new FontIcon { Glyph = IconGlyph.Import } };
        import.Click += (_, _) => actions.OpenImport?.Invoke();
        flyout.Items.Add(import);

        var refresh = new MenuFlyoutItem { Text = "重新载入课表", Icon = new FontIcon { Glyph = IconGlyph.Refresh } };
        refresh.Click += (_, _) => actions.Refresh?.Invoke();
        flyout.Items.Add(refresh);

        var reset = new MenuFlyoutItem { Text = "恢复默认位置", Icon = new FontIcon { Glyph = IconGlyph.Recenter } };
        reset.Click += (_, _) => actions.ResetPosition?.Invoke();
        flyout.Items.Add(reset);

        flyout.Items.Add(new MenuFlyoutSeparator());

        if (actions.DesktopLayer is { } enabled && actions.ToggleDesktopLayer is not null)
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = "贴桌面层",
                IsChecked = enabled,
                Icon = new FontIcon { Glyph = IconGlyph.Pin },
            };
            toggle.Click += (_, _) => actions.ToggleDesktopLayer();
            flyout.Items.Add(toggle);
        }

        if (actions.ShowWeekend is { } weekend && actions.ToggleShowWeekend is not null)
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = "显示周末",
                IsChecked = weekend,
                Icon = new FontIcon { Glyph = IconGlyph.Weekend },
            };
            toggle.Click += (_, _) => actions.ToggleShowWeekend();
            flyout.Items.Add(toggle);
        }

        // 「周次视图」四项互斥：用 RadioMenuFlyoutItem 让系统画选中态（ToggleMenuFlyoutItem 表达不了"四选一"）
        if (actions.WeekView is { } view && actions.SetWeekView is not null)
        {
            var submenu = new MenuFlyoutSubItem { Text = WeekViewLabels.Title };
            foreach (var (item, label) in WeekViewLabels.Ordered)
            {
                var radio = new RadioMenuFlyoutItem
                {
                    Text = label,
                    GroupName = "week-view",
                    IsChecked = view == item,
                };
                radio.Click += (_, _) => actions.SetWeekView(item);
                submenu.Items.Add(radio);
            }

            flyout.Items.Add(submenu);
        }

        var hide = new MenuFlyoutItem { Text = "隐藏挂件", Icon = new FontIcon { Glyph = IconGlyph.Hide } };
        hide.Click += (_, _) => actions.Hide?.Invoke();
        flyout.Items.Add(hide);

        var quit = new MenuFlyoutItem { Text = "退出", Icon = new FontIcon { Glyph = IconGlyph.Close } };
        quit.Click += (_, _) => actions.Exit?.Invoke();
        flyout.Items.Add(quit);

        var button = BuildIconButton(IconGlyph.More, dark, "更多", null);
        button.Flyout = flyout;
        return button;
    }

    /// <summary>固定星期头的内容画布：宽同整块画布，左右溢出交给宿主裁剪，纵向只有「呼吸位 + 表头」那一段。</summary>
    private static Canvas BuildDayHeaderStrip(BoardVisual visual, bool dark)
    {
        var strip = new Canvas
        {
            Width = visual.CanvasWidth,
            Height = visual.Grid.Top,
            UseLayoutRounding = false,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(strip, "BoardWeekdayHeaderStrip");
        DrawHeaders(strip, visual, dark);
        return strip;
    }

    /// <summary>
    /// 固定星期头的宿主：高度就是原画布里「呼吸位 + 星期头」那一段，宽度对齐内容视口并裁掉溢出。
    /// 表头**不**放进滚动查看器，所以纵向滚动不会带走它，也不需要拿覆盖层去遮仍在滚动的旧画布。
    /// </summary>
    private static Grid BuildDayHeaderHost(Canvas strip, double height)
    {
        var host = new Grid
        {
            Height = height,
            UseLayoutRounding = false,
            // 透明背景与滚动画布一致：参与命中测试但不吞事件，表头不额外接手势。
            Background = new SolidColorBrush(Colors.Transparent),
            // 先按 0 宽裁剪，等布局拿到内容视口宽再放开：否则首帧会按整块画布宽溢出窗口。
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 0) },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, "BoardWeekdayHeader");
        host.Children.Add(strip);
        return host;
    }

    /// <summary>
    /// 滚动区画布（尺寸已由呈现模型算好，单位 DIP）。星期头已移出画布，
    /// 其余元素的 Y 统一减去 <paramref name="yShift"/>（= 固定区高度），相对位置保持不变。
    /// </summary>
    private static Canvas BuildCanvas(BoardVisual visual, bool dark, double yShift)
    {
        var canvas = new Canvas
        {
            Width = visual.CanvasWidth,
            Height = visual.CanvasHeight - yShift,
            // ⚠️ 显式 Top/Left 对齐：Canvas 有固定尺寸，而默认的 Stretch 只是"占满槽位"——
            // 尺寸小于槽位时它会被**居中**（真机实测：1100×760 的窗口里画布 612 dip、槽位 726 dip，
            // 于是整块课表下移 57 dip，表头离头部条 57 dip，顶部那 6 dip 呼吸位等于白设）。
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
        };

        DrawSlotLabels(canvas, visual, dark, yShift);
        DrawGrid(canvas, visual, dark, yShift);
        DrawBlocks(canvas, visual, yShift);
        // 时间线最后画：它压在课程块之上一点点才看得见（渲染层把它放在块下面，
        // 但桌面挂件的信息密度下，一条细线穿过色块更实用；这是有意的差异）。
        DrawNowLine(canvas, visual, dark, yShift);

        return canvas;
    }

    /// <summary>星期列头；今日用强调色 + 下划线。画在固定区画布上，不属于滚动内容。</summary>
    private static void DrawHeaders(Canvas canvas, BoardVisual visual, bool dark)
    {
        foreach (var day in visual.Days)
        {
            var accent = TintPalette.Accent(dark);
            var text = new TextBlock
            {
                Text = day.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Width = day.Width,
                TextAlignment = TextAlignment.Center,
                LineHeight = visual.Geometry.HeaderHeight - 2,
                Foreground = new SolidColorBrush(Parse(day.IsToday ? accent : TintPalette.TextSoft(dark))),
                Opacity = day.IsWeekend && !day.IsToday ? 0.72 : 1.0,
            };
            Canvas.SetLeft(text, day.Left);
            Canvas.SetTop(text, visual.HeaderTop);
            canvas.Children.Add(text);

            if (!day.IsToday) continue;
            var underline = new Rectangle
            {
                Width = day.Width * 0.56,
                Height = 2,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Parse(accent)),
            };
            Canvas.SetLeft(underline, day.Left + (day.Width * 0.22));
            Canvas.SetTop(underline, visual.HeaderTop + visual.Geometry.HeaderHeight - 5);
            canvas.Children.Add(underline);
        }
    }

    /// <summary>
    /// 左侧节次标签（"3 · 10:00"）；固定 11 DIP，居中且右侧留 8 DIP 给当前节次标记。
    /// </summary>
    private static void DrawSlotLabels(Canvas canvas, BoardVisual visual, bool dark, double yShift)
    {
        foreach (var slot in visual.Slots)
        {
            var accent = TintPalette.Accent(dark);
            var text = new TextBlock
            {
                Text = slot.Text,
                FontSize = BoardContentLayout.TimeFontSize,
                Width = visual.Geometry.GutterWidth - 8,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Parse(slot.IsCurrent ? accent : TintPalette.TextSoft(dark))),
                FontWeight = slot.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Canvas.SetLeft(text, 4);
            Canvas.SetTop(text, visual.Grid.Top + slot.Top - yShift);
            canvas.Children.Add(text);

            if (!slot.IsCurrent) continue;
            var marker = new Rectangle
            {
                Width = 3,
                Height = 10,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Parse(accent)),
            };
            Canvas.SetLeft(marker, visual.Geometry.GutterWidth - 4);
            Canvas.SetTop(marker, visual.Grid.Top + slot.Top + 3 - yShift);
            canvas.Children.Add(marker);
        }
    }

    /// <summary>网格：底色只由 1px 分隔线表达，周末列与今日列淡染。</summary>
    private static void DrawGrid(Canvas canvas, BoardVisual visual, bool dark, double yShift)
    {
        var line = Parse(TintPalette.GridLine(dark));
        var stroke = Parse(TintPalette.Stroke(dark));
        var weekend = new SolidColorBrush(Parse(TintPalette.WeekendCell(dark)));
        var today = new SolidColorBrush(Parse(TintPalette.TodayCell(dark)));

        foreach (var row in visual.Rows)
        {
            for (var col = 0; col < visual.Days.Count; col += 1)
            {
                var day = visual.Days[col];
                var cell = new Rectangle
                {
                    Width = day.Width,
                    Height = row.Height,
                    Fill = day.IsToday ? today : day.IsWeekend ? weekend : null,
                    Stroke = new SolidColorBrush(line),
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(cell, day.Left);
                Canvas.SetTop(cell, visual.Grid.Top + row.Top - yShift);
                canvas.Children.Add(cell);
            }
        }

        var frame = new Rectangle
        {
            Width = visual.Grid.Width,
            Height = visual.Grid.Height,
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 1,
            RadiusX = 12,
            RadiusY = 12,
        };
        Canvas.SetLeft(frame, visual.Grid.Left);
        Canvas.SetTop(frame, visual.Grid.Top - yShift);
        canvas.Children.Add(frame);
    }

    /// <summary>当前时间指示线（横穿网格 + 左侧圆点）。</summary>
    private static void DrawNowLine(Canvas canvas, BoardVisual visual, bool dark, double yShift)
    {
        if (visual.NowLineTop is not { } top) return;
        var accent = Parse(TintPalette.Accent(dark));

        var line = new Rectangle
        {
            Width = visual.Grid.Width,
            Height = 2,
            RadiusX = 1,
            RadiusY = 1,
            Fill = new SolidColorBrush(accent),
            Opacity = 0.8,
        };
        Canvas.SetLeft(line, visual.Grid.Left);
        Canvas.SetTop(line, top - yShift);
        canvas.Children.Add(line);

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(accent),
        };
        Canvas.SetLeft(dot, visual.Grid.Left - 5);
        Canvas.SetTop(dot, top - 2.5 - yShift);
        canvas.Children.Add(dot);
    }

    /// <summary>
    /// 课程色块：按 <see cref="BlockVisual.Frame"/> 摆放；非全周课用更实的描边表达"特殊"。
    /// </summary>
    private static void DrawBlocks(Canvas canvas, BoardVisual visual, double yShift)
    {
        foreach (var block in visual.Blocks)
        {
            var tint = block.Tint;
            var card = new Border
            {
                Width = block.Frame.Width,
                Height = block.Frame.Height,
                Background = new SolidColorBrush(Parse(tint.Tint)),
                BorderBrush = new SolidColorBrush(Parse(block.IsSpecial ? tint.EdgeStrong : tint.Edge)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 3, 6, 3),
                Opacity = block.IsStacked ? 1.0 : 0.98,
            };

            var stack = new StackPanel { Spacing = 0 };
            void AddLine(string text, double size, bool semibold, string ink, double? height)
            {
                var label = ContentLine(text, size, semibold);
                label.Foreground = new SolidColorBrush(Parse(ink));
                // 高度和量测使用同一配置；空教室也占中间文字行，不挤掉最后一行。
                if (height is { } measuredHeight) label.Height = measuredHeight;
                stack.Children.Add(label);
            }
            AddLine(block.DisplayName, block.FontSize, true, tint.Ink, block.Content?.NameHeight);
            AddLine(string.IsNullOrEmpty(block.Room) ? " " : block.Room,
                Math.Max(9, block.FontSize - 0.5), false, tint.InkSoft, block.Content?.RoomHeight);
            AddLine(block.WeeksLabel, Math.Max(9, block.FontSize - 1), false,
                tint.InkSoft, block.Content?.WeeksHeight);

            card.Child = stack;
            ToolTipService.SetToolTip(card, block.Tooltip);

            Canvas.SetLeft(card, block.Frame.Left);
            Canvas.SetTop(card, block.Frame.Top - yShift);
            canvas.Children.Add(card);
        }
    }

    /// <summary><c>#aarrggbb</c> / <c>#rrggbb</c> → <see cref="Color"/>。</summary>
    private static Color Parse(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        if (text.Length != 8) return Colors.Transparent;
        return Color.FromArgb(
            Convert.ToByte(text.Substring(0, 2), 16),
            Convert.ToByte(text.Substring(2, 2), 16),
            Convert.ToByte(text.Substring(4, 2), 16),
            Convert.ToByte(text.Substring(6, 2), 16));
    }
}
