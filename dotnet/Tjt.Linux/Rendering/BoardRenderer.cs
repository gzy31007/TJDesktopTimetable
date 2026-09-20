using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tjt.Widget;

namespace Tjt.Linux.Rendering;

/// <summary>
/// 把 <see cref="BoardVisual"/> 摆到 Avalonia 上（Tjt.App/Rendering/BoardRenderer.cs 的移植）。
///
/// 这一层刻意"没有脑子"：坐标、字号、文案、染色全部由 <c>Tjt.Widget</c> 算好
/// （那部分能在 Linux 上单测），这里只做"照着数字放控件"。
///
/// 结构与 Windows 版一一对应：**顶部信息条**（学期 / 周次 / 今日节数）+ **网格**
/// （可能横向或纵向滚动）+ **边缘缩放热区覆盖层**。窗口尺寸变化时整棵树重建。
/// </summary>
internal static class BoardRenderer
{
    /// <summary>顶部条里的字号（比网格文字略大，作为层级提示）。</summary>
    private const double HeaderFontSize = 13;

    /// <summary>顶部条下方的分隔线高度。</summary>
    private const double HeaderRuleHeight = 1;

    /// <summary>渲染整块课表：返回可直接塞进窗口的根元素。</summary>
    public static Control Render(BoardVisual visual, bool dark, WidgetActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(visual);
        actions ??= new WidgetActions();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        var header = BuildHeaderBar(visual.Header, dark, actions);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var canvas = BuildCanvas(visual, dark);
        // 网格可能比可用空间大（列宽或行高到了下限）—— 用 ScrollViewer 兜住；装得下时滚动条不会出现。
        var scroller = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = visual.NeedsHorizontalScroll
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = visual.NeedsVerticalScroll
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        // 边缘缩放热区放在独立覆盖层里，和整块课表同处一格（z 序在后 → 盖在上面）。
        var shell = new Grid();
        shell.Children.Add(root);

        var overlay = new Grid { IsHitTestVisible = true };
        var totalW = visual.CanvasWidth;
        var totalH = visual.CanvasHeight;
        foreach (var zone in CursorZones.ForWindow(totalW, totalH))
        {
            var strip = BuildResizeStrip(zone, actions);
            // 四边对齐 + Margin 精确定位：每块热区都是"贴哪两条边、离另一条边多远"，
            // 不依赖父容器的行/列定义（Windows 版踩过"元素落错行把布局撑坏"的坑，口径照搬）。
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
            strip.Width = zone.Width;
            strip.Height = zone.Height;
            overlay.Children.Add(strip);
        }

        shell.Children.Add(overlay);
        return shell;
    }

    /// <summary>
    /// 顶部信息条：左边图标 + 标题 + 元信息，右边一组动作按钮（刷新 / ⋯ 菜单）。
    /// 空白处 = 窗口拖动区（按钮会先吃掉自己的点击，不会误触发拖动）。
    /// </summary>
    private static Control BuildHeaderBar(BoardHeader header, bool dark, WidgetActions actions)
    {
        var accent = TintPalette.Accent(dark);
        var text = TintPalette.Text(dark);
        var soft = TintPalette.TextSoft(dark);

        var host = new Grid { Height = BoardVisualBuilder.HeaderHeight };

        // Avalonia 的 Grid 没有 Padding：用 Border 包一层给内边距
        var paddedRow = new Border { Padding = new Thickness(10, 0, 8, 0) };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        left.Children.Add(new TextBlock
        {
            Text = IconGlyph.Calendar,
            FontSize = 14,
            Width = 18,
            Height = 18,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Parse(accent)),
        });
        left.Children.Add(new TextBlock
        {
            Text = header.Title,
            FontSize = HeaderFontSize,
            FontWeight = FontWeight.SemiBold,
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

        // 顶部条空白处也要能命中指针（拖动区由外壳经 actions 挂上）
        row.Background = Brushes.Transparent;
        actions.AttachDragArea?.Invoke(row);

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

        paddedRow.Child = row;
        host.Children.Add(paddedRow);
        host.Children.Add(new Border
        {
            Height = HeaderRuleHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Parse(TintPalette.GridLine(dark))),
        });
        return host;
    }

    /// <summary>无边框图标按钮，悬停底由 Fluent 主题给。</summary>
    private static Button BuildIconButton(string glyph, bool dark, string tooltip, Action? action)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 13 },
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Parse(TintPalette.TextSoft(dark))),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        ToolTip.SetTip(button, tooltip);
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// <c>⋯</c> 溢出菜单：导入课表 / 重新载入 / 恢复默认位置 / 贴桌面层开关 / 显示周末开关 /
    /// 周次视图（四选一子菜单）/ 退出。开关用 <see cref="MenuItem.ToggleType"/> 反映当前值
    /// （Linux 版没有设置窗口与托盘，这份菜单就是全部入口）。
    /// </summary>
    private static Button BuildOverflowButton(bool dark, WidgetActions actions)
    {
        var menu = new ContextMenu();

        // 换课表的入口放在最常用的位置：没导入过的挂件显示的是内置示例课表，用户第一件事就是导入
        var import = new MenuItem { Header = "导入课表…" };
        import.Click += (_, _) => actions.OpenImport?.Invoke();
        menu.Items.Add(import);

        var refresh = new MenuItem { Header = "重新载入课表" };
        refresh.Click += (_, _) => actions.Refresh?.Invoke();
        menu.Items.Add(refresh);

        var reset = new MenuItem { Header = "恢复默认位置" };
        reset.Click += (_, _) => actions.ResetPosition?.Invoke();
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());

        if (actions.DesktopLayer is { } enabled && actions.ToggleDesktopLayer is not null)
        {
            var toggle = new MenuItem
            {
                Header = "贴桌面层",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = enabled,
            };
            toggle.Click += (_, _) => actions.ToggleDesktopLayer();
            menu.Items.Add(toggle);
        }

        if (actions.ShowWeekend is { } weekend && actions.ToggleShowWeekend is not null)
        {
            var toggle = new MenuItem
            {
                Header = "显示周末",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = weekend,
            };
            toggle.Click += (_, _) => actions.ToggleShowWeekend();
            menu.Items.Add(toggle);
        }

        // 「周次视图」四项互斥：Avalonia 的 MenuItem 用 Radio 类型画选中态（与 Windows 的
        // RadioMenuFlyoutItem 对应）；文案取自 Tjt.Widget 的共用表，两端逐字一致
        if (actions.WeekView is { } view && actions.SetWeekView is not null)
        {
            var submenu = new MenuItem { Header = Tjt.Widget.WeekViewLabels.Title };
            foreach (var (item, label) in Tjt.Widget.WeekViewLabels.Ordered)
            {
                var radio = new MenuItem
                {
                    Header = label,
                    ToggleType = MenuItemToggleType.Radio,
                    GroupName = "week-view",
                    IsChecked = view == item,
                };
                radio.Click += (_, _) => actions.SetWeekView(item);
                submenu.Items.Add(radio);
            }

            menu.Items.Add(submenu);
        }

        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => actions.Exit?.Invoke();
        menu.Items.Add(quit);

        var button = BuildIconButton(IconGlyph.More, dark, "更多", null);
        button.Click += (_, _) => menu.Open(button);
        return button;
    }

    /// <summary>一块边缘缩放热区：透明、带方向光标；按下即把方向报给外壳开始系统缩放循环。</summary>
    private static Control BuildResizeStrip(CursorZones.Zone zone, WidgetActions actions)
    {
        var (edge, cursor) = Map(zone.Grip);
        var strip = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
        };
        strip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed)
            {
                actions.BeginResize?.Invoke(edge, e);
            }
        };
        return strip;
    }

    /// <summary><see cref="ResizeGrip"/> → Avalonia <see cref="WindowEdge"/> + 方向光标。</summary>
    private static (WindowEdge Edge, StandardCursorType Cursor) Map(ResizeGrip grip) => grip switch
    {
        ResizeGrip.Top => (WindowEdge.North, StandardCursorType.SizeNorthSouth),
        ResizeGrip.Bottom => (WindowEdge.South, StandardCursorType.SizeNorthSouth),
        ResizeGrip.Left => (WindowEdge.West, StandardCursorType.SizeWestEast),
        ResizeGrip.Right => (WindowEdge.East, StandardCursorType.SizeWestEast),
        ResizeGrip.TopLeft => (WindowEdge.NorthWest, StandardCursorType.TopLeftCorner),
        ResizeGrip.TopRight => (WindowEdge.NorthEast, StandardCursorType.TopRightCorner),
        ResizeGrip.BottomLeft => (WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner),
        ResizeGrip.BottomRight => (WindowEdge.SouthEast, StandardCursorType.BottomRightCorner),
        _ => (WindowEdge.North, StandardCursorType.Arrow),
    };

    /// <summary>网格画布（尺寸已由呈现模型算好，单位 DIP）。</summary>
    private static Canvas BuildCanvas(BoardVisual visual, bool dark)
    {
        var canvas = new Canvas
        {
            Width = visual.CanvasWidth,
            Height = visual.CanvasHeight,
            Background = Brushes.Transparent,
        };

        DrawHeaders(canvas, visual, dark);
        DrawSlotLabels(canvas, visual, dark);
        DrawGrid(canvas, visual, dark);
        DrawBlocks(canvas, visual);
        // 时间线最后画：它压在课程块之上一点点才看得见（与 Windows 版一致的有意差异）。
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
                FontWeight = FontWeight.SemiBold,
                Width = day.Width,
                TextAlignment = TextAlignment.Center,
                LineHeight = visual.Geometry.HeaderHeight - 2,
                Foreground = new SolidColorBrush(Parse(day.IsToday ? accent : TintPalette.TextSoft(dark))),
                Opacity = day.IsWeekend && !day.IsToday ? 0.72 : 1.0,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(text, day.Left);
            Canvas.SetTop(text, visual.HeaderTop);
            canvas.Children.Add(text);

            if (!day.IsToday) continue;
            var underline = new Border
            {
                Width = day.Width * 0.56,
                Height = 2,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Parse(accent)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(underline, day.Left + (day.Width * 0.22));
            Canvas.SetTop(underline, visual.HeaderTop + visual.Geometry.HeaderHeight - 5);
            canvas.Children.Add(underline);
        }
    }

    /// <summary>
    /// 左侧节次标签（"3 · 10:00"）；"正在上"的那一节用强调色加粗。
    /// 文字居中（行高压小时字号跟着变小，居中后左右空白对称，口径与 Windows 版一致）。
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
                FontWeight = slot.IsCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(text, 4);
            Canvas.SetTop(text, visual.Grid.Top + slot.Top);
            canvas.Children.Add(text);

            if (!slot.IsCurrent) continue;
            var marker = new Border
            {
                Width = 3,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Parse(accent)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(marker, visual.Geometry.GutterWidth - 4);
            Canvas.SetTop(marker, visual.Grid.Top + slot.Top + 3);
            canvas.Children.Add(marker);
        }
    }

    /// <summary>网格：底色只由 1px 分隔线表达，周末列与今日列淡染（外框 12dip 圆角）。</summary>
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
                var cell = new Border
                {
                    Width = day.Width,
                    Height = visual.Geometry.RowHeight,
                    Background = day.IsToday ? today : day.IsWeekend ? weekend : null,
                    BorderBrush = new SolidColorBrush(line),
                    BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(cell, day.Left);
                Canvas.SetTop(cell, visual.Grid.Top + (row * visual.Geometry.RowHeight));
                canvas.Children.Add(cell);
            }
        }

        var frame = new Border
        {
            Width = visual.Grid.Width,
            Height = visual.Grid.Height,
            BorderBrush = new SolidColorBrush(stroke),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            IsHitTestVisible = false,
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

        var line = new Border
        {
            Width = visual.Grid.Width,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(accent),
            Opacity = 0.8,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(line, visual.Grid.Left);
        Canvas.SetTop(line, top);
        canvas.Children.Add(line);

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(accent),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, visual.Grid.Left - 5);
        Canvas.SetTop(dot, top - 2.5);
        canvas.Children.Add(dot);
    }

    /// <summary>课程色块：按 <see cref="BlockVisual.Frame"/> 摆放；非全周课用更实的描边表达"特殊"。</summary>
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
                FontWeight = FontWeight.SemiBold,
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
            ToolTip.SetTip(card, block.Tooltip);

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
