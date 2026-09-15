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
    /// 渲染整块课表：返回可直接塞进窗口的根元素。
    /// </summary>
    /// <param name="visual">呈现模型。</param>
    /// <param name="dark">是否深色主题。</param>
    public static FrameworkElement Render(BoardVisual visual, bool dark, WidgetActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(visual);
        actions ??= new WidgetActions();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeaderBar(visual.Header, dark, actions);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var canvas = BuildCanvas(visual, dark);
        // 网格可能比可用空间大（列宽或行高到了下限）—— 用 ScrollViewer 兜住，
        // 与渲染层 `.f-scroll` 的表现一致；装得下时滚动条不会出现。
        var scroller = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = visual.NeedsHorizontalScroll
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = visual.NeedsVerticalScroll
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Padding = new Thickness(0),
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        // 边缘光标条放在**独立覆盖层**里，和整块课表同处一格（z 序在后 → 盖在上面）。
        // 不要把它们加进 `root` 本身：实测那样会把根网格的两行布局搞坏 ——
        // 整个课表被推到下方、上方留一大片空白（`--size` 下的截图对比确认）。
        // 用 WinUI 原生光标（`ProtectedCursor`）而不是 Win32 `SetCursor`：后者会被窗口过程
        // 在 WM_SETCURSOR 里按类光标重置（真机实测"能缩放但看不到缩放光标"）。
        // 只挂光标、不挂事件处理：拖拽判定仍在 Win32 侧（WindowEdgeResize）。
        var shell = new Grid();
        shell.Children.Add(root);

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

    /// <summary>
    /// 顶部信息条 —— 布局照资源管理器卡片头：**左边图标 + 标题 + 元信息，右边一组动作按钮**。
    ///
    /// 动作按钮是这个窗口唯一的交互入口（没有系统标题栏）：
    /// 刷新课表、以及一个 <c>⋯</c> 溢出菜单（设置 / 恢复默认位置 / 贴桌面层 / 隐藏 / 退出）。
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
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(BuildAppGlyph(accent));
        left.Children.Add(new TextBlock
        {
            Text = header.Title,
            FontSize = HeaderFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Parse(header.IsHoliday ? soft : text)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(new TextBlock
        {
            Text = header.WeekText,
            FontSize = HeaderFontSize - 1,
            Foreground = new SolidColorBrush(Parse(soft)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (header.TodayText is { Length: > 0 } today)
        {
            left.Children.Add(new TextBlock
            {
                Text = "·",
                FontSize = HeaderFontSize - 1,
                Foreground = new SolidColorBrush(Parse(soft)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            left.Children.Add(new TextBlock
            {
                Text = today,
                FontSize = HeaderFontSize - 1,
                Foreground = new SolidColorBrush(Parse(soft)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        // 顶部条空白处 = 窗口拖动区（系统标题栏已被移除）。按钮是 row 的子元素、
        // 会先吃掉自己的点击，所以不会误触发拖动。
        // 具体怎么拖（自实现位移）由外壳的 WindowDrag 负责，渲染层只负责"这块能抓"。
        row.Background = new SolidColorBrush(Colors.Transparent); // 空白处也要能命中指针
        actions.AttachDragArea?.Invoke(row);

        // ── 右：动作按钮（刷新 + ⋯ 菜单）
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
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
    /// <c>⋯</c> 溢出菜单：设置 / 恢复默认位置 / 贴桌面层开关 / 显示周末开关 / 隐藏挂件 / 退出。
    ///
    /// 菜单里的两个**开关**（"贴桌面层"、"显示周末"）用 <see cref="ToggleMenuFlyoutItem"/> 反映当前值 ——
    /// 它们是唯一能用勾选状态表达"当前是否生效"的项，其余都是动作。
    /// </summary>
    private static Button BuildOverflowButton(bool dark, WidgetActions actions)
    {
        var flyout = new MenuFlyout();

        var settings = new MenuFlyoutItem { Text = "设置", Icon = new FontIcon { Glyph = IconGlyph.Settings } };
        settings.Click += (_, _) => actions.OpenSettings?.Invoke();
        flyout.Items.Add(settings);

        // 换课表的入口放在最常用的位置：没导入过的挂件显示的是内置样例，用户第一件事就是导入
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

    /// <summary>网格画布（尺寸已由呈现模型算好，单位 DIP）。</summary>
    private static Canvas BuildCanvas(BoardVisual visual, bool dark)
    {
        var canvas = new Canvas
        {
            Width = visual.CanvasWidth,
            Height = visual.CanvasHeight,
            Background = new SolidColorBrush(Colors.Transparent),
        };

        DrawHeaders(canvas, visual, dark);
        DrawSlotLabels(canvas, visual, dark);
        DrawGrid(canvas, visual, dark);
        DrawBlocks(canvas, visual);
        // 时间线最后画：它压在课程块之上一点点才看得见（渲染层把它放在块下面，
        // 但桌面挂件的信息密度下，一条细线穿过色块更实用；这是有意的差异）。
        DrawNowLine(canvas, visual, dark);

        return canvas;
    }

    /// <summary>星期列头；今日用强调色 + 下划线。</summary>
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
    /// 左侧节次标签（"3 · 10:00"）；"正在上"的那一节用强调色加粗。
    ///
    /// <para>文字**居中**而不是右对齐：行高被压小时字号跟着变小（<c>RowHeight × 0.24</c>），
    /// 右对齐会让"文字与窗口左边缘的那片空白"随字号变小而变大（真机反馈："第一列时间与窗口的
    /// 边距过大"）；居中后左右空白对称，字号怎么变都稳定。右侧留 8dip 给"正在上"的竖条标记。</para>
    /// </summary>
    private static void DrawSlotLabels(Canvas canvas, BoardVisual visual, bool dark)
    {
        foreach (var slot in visual.Slots)
        {
            var accent = TintPalette.Accent(dark);
            var text = new TextBlock
            {
                Text = slot.Text,
                FontSize = Math.Max(9, visual.Geometry.RowHeight * 0.24),
                Width = visual.Geometry.GutterWidth - 8,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Parse(slot.IsCurrent ? accent : TintPalette.TextSoft(dark))),
                FontWeight = slot.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Canvas.SetLeft(text, 4);
            Canvas.SetTop(text, visual.Grid.Top + slot.Top);
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
            Canvas.SetTop(marker, visual.Grid.Top + slot.Top + 3);
            canvas.Children.Add(marker);
        }
    }

    /// <summary>网格：底色只由 1px 分隔线表达，周末列与今日列淡染。</summary>
    private static void DrawGrid(Canvas canvas, BoardVisual visual, bool dark)
    {
        var line = Parse(TintPalette.GridLine(dark));
        var stroke = Parse(TintPalette.Stroke(dark));
        var weekend = new SolidColorBrush(Parse(TintPalette.WeekendCell(dark)));
        var today = new SolidColorBrush(Parse(TintPalette.TodayCell(dark)));

        for (var row = 0; row < visual.Slots.Count; row += 1)
        {
            for (var col = 0; col < visual.Days.Count; col += 1)
            {
                var day = visual.Days[col];
                var cell = new Rectangle
                {
                    Width = day.Width,
                    Height = visual.Geometry.RowHeight,
                    Fill = day.IsToday ? today : day.IsWeekend ? weekend : null,
                    Stroke = new SolidColorBrush(line),
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(cell, day.Left);
                Canvas.SetTop(cell, visual.Grid.Top + (row * visual.Geometry.RowHeight));
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
        Canvas.SetTop(frame, visual.Grid.Top);
        canvas.Children.Add(frame);
    }

    /// <summary>当前时间指示线（横穿网格 + 左侧圆点）。</summary>
    private static void DrawNowLine(Canvas canvas, BoardVisual visual, bool dark)
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
        Canvas.SetTop(line, top);
        canvas.Children.Add(line);

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(accent),
        };
        Canvas.SetLeft(dot, visual.Grid.Left - 5);
        Canvas.SetTop(dot, top - 2.5);
        canvas.Children.Add(dot);
    }

    /// <summary>
    /// 课程色块：按 <see cref="BlockVisual.Frame"/> 摆放；非全周课用更实的描边表达"特殊"。
    /// </summary>
    private static void DrawBlocks(Canvas canvas, BoardVisual visual)
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
            stack.Children.Add(new TextBlock
            {
                Text = block.DisplayName,
                FontSize = block.FontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Parse(tint.Ink)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
            if (block.Room is { Length: > 0 } room)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = room,
                    FontSize = Math.Max(9, block.FontSize - 0.5),
                    Foreground = new SolidColorBrush(Parse(tint.InkSoft)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text = block.WeeksLabel,
                FontSize = Math.Max(9, block.FontSize - 1),
                Foreground = new SolidColorBrush(Parse(tint.InkSoft)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });

            card.Child = stack;
            ToolTipService.SetToolTip(card, block.Tooltip);

            Canvas.SetLeft(card, block.Frame.Left);
            Canvas.SetTop(card, block.Frame.Top);
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
