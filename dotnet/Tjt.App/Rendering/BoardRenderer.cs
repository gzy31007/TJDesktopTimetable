using Microsoft.UI;
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
    public static FrameworkElement Render(BoardVisual visual, bool dark)
    {
        ArgumentNullException.ThrowIfNull(visual);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeaderBar(visual.Header, dark);
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

        return root;
    }

    /// <summary>顶部信息条：左侧学期名，右侧"周次 · 今日 N 节"（对应 <c>.widget-bar</c>）。</summary>
    private static FrameworkElement BuildHeaderBar(BoardHeader header, bool dark)
    {
        var accent = new SolidColorBrush(Parse(TintPalette.Accent(dark)));
        var text = TintPalette.Text(dark);
        var soft = TintPalette.TextSoft(dark);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0, 12, 0),
        };

        // 品牌点（渲染层 .brand 的等价物）：一小块强调色，让标题条不显得空
        row.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = accent,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = header.Title,
            FontSize = HeaderFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Parse(header.IsHoliday ? soft : text)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = header.WeekText,
            FontSize = HeaderFontSize - 1,
            Foreground = new SolidColorBrush(Parse(soft)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (header.TodayText is { Length: > 0 } today)
        {
            row.Children.Add(new TextBlock
            {
                Text = "·",
                FontSize = HeaderFontSize - 1,
                Foreground = new SolidColorBrush(Parse(soft)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = today,
                FontSize = HeaderFontSize - 1,
                Foreground = new SolidColorBrush(Parse(soft)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var host = new Grid { Height = BoardVisualBuilder.HeaderHeight };
        host.Children.Add(row);
        host.Children.Add(new Rectangle
        {
            Height = HeaderRuleHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new SolidColorBrush(Parse(TintPalette.GridLine(dark))),
        });
        return host;
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
            Canvas.SetTop(text, 0);
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
            Canvas.SetTop(underline, visual.Geometry.HeaderHeight - 5);
            canvas.Children.Add(underline);
        }
    }

    /// <summary>左侧节次标签（"3 · 10:00"）；"正在上"的那一节用强调色加粗。</summary>
    private static void DrawSlotLabels(Canvas canvas, BoardVisual visual, bool dark)
    {
        foreach (var slot in visual.Slots)
        {
            var accent = TintPalette.Accent(dark);
            var text = new TextBlock
            {
                Text = slot.Text,
                FontSize = Math.Max(9, visual.Geometry.RowHeight * 0.24),
                Width = visual.Geometry.GutterWidth - 9,
                TextAlignment = TextAlignment.Right,
                Foreground = new SolidColorBrush(Parse(slot.IsCurrent ? accent : TintPalette.TextSoft(dark))),
                FontWeight = slot.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Canvas.SetLeft(text, 0);
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
