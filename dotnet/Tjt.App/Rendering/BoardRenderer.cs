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
/// 把 <see cref="BoardVisual"/> 摆到 XAML <see cref="Canvas"/> 上。
///
/// 这一层刻意"没有脑子"：坐标、字号、文案、染色全部由 <c>Tjt.Widget</c> 算好
/// （那部分能在 Linux 上单测），这里只做"照着数字放控件"。
/// 画布尺寸用 DIP，由调用方按窗口 DPI 换算，避免 150% 缩放下像素与 DIP 混用。
/// </summary>
internal static class BoardRenderer
{
    /// <summary>浅色主题下网格外的字号基准（课程名 / 节次标签共用）。</summary>
    private const double CaptionSize = 11;

    /// <summary>
    /// 渲染整块课表，返回画布（调用方负责放进窗口）。
    /// </summary>
    /// <param name="visual">呈现模型。</param>
    /// <param name="dark">是否深色主题。</param>
    public static Canvas Render(BoardVisual visual, bool dark)
    {
        ArgumentNullException.ThrowIfNull(visual);

        var canvas = new Canvas
        {
            Width = visual.Grid.Left + visual.Grid.Width,
            Height = visual.Grid.Top + visual.Grid.Height,
            Background = new SolidColorBrush(Colors.Transparent),
        };

        DrawHeaders(canvas, visual, dark);
        DrawSlotLabels(canvas, visual, dark);
        DrawGrid(canvas, visual, dark);
        DrawNowLine(canvas, visual, dark);
        DrawBlocks(canvas, visual);

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
                FontSize = CaptionSize,
                Width = visual.Geometry.GutterWidth - 9,
                TextAlignment = TextAlignment.Right,
                Foreground = new SolidColorBrush(Parse(slot.IsCurrent ? accent : TintPalette.TextSoft(dark))),
                FontWeight = slot.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Canvas.SetLeft(text, 0);
            Canvas.SetTop(text, visual.Grid.Top + slot.Top);
            canvas.Children.Add(text);

            if (!slot.IsCurrent) continue;
            // 当前节次的右侧竖条（渲染层 .slot-labels div.current::before）
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

    /// <summary>
    /// 网格：底色只由 1px 分隔线表达（对齐渲染层 <c>.grid-bg</c>），周末列额外淡染，
    /// 今日列整列淡染。
    /// </summary>
    private static void DrawGrid(Canvas canvas, BoardVisual visual, bool dark)
    {
        var line = Parse(TintPalette.GridLine(dark));
        var stroke = Parse(TintPalette.Stroke(dark));

        var weekend = new SolidColorBrush(Parse(TintPalette.WeekendCell(dark)));

        for (var row = 0; row < visual.Slots.Count; row += 1)
        {
            for (var col = 0; col < visual.Days.Count; col += 1)
            {
                var day = visual.Days[col];
                var cell = new Rectangle
                {
                    Width = day.Width,
                    Height = visual.Geometry.RowHeight,
                    Fill = day.IsToday
                        ? new SolidColorBrush(Parse(TintPalette.TodayCell(dark)))
                        : day.IsWeekend ? weekend : null,
                    Stroke = new SolidColorBrush(line),
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(cell, day.Left);
                Canvas.SetTop(cell, visual.Grid.Top + (row * visual.Geometry.RowHeight));
                canvas.Children.Add(cell);
            }
        }

        // 外框：贴在网格整体边界上，比逐格描边更"卡片"
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

    /// <summary>当前时间指示线（压在色块之下，只作背景刻度）。</summary>
    private static void DrawNowLine(Canvas canvas, BoardVisual visual, bool dark)
    {
        if (visual.NowLineTop is not { } top) return;
        var line = new Rectangle
        {
            Width = visual.Grid.Width + 4,
            Height = 2,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(Parse(TintPalette.Accent(dark))),
            Opacity = 0.85,
        };
        Canvas.SetLeft(line, visual.Grid.Left - 4);
        Canvas.SetTop(line, top);
        canvas.Children.Add(line);
    }

    /// <summary>
    /// 课程色块：按 <see cref="BlockVisual.Frame"/> 摆放，色值走染色结果，
    /// 非全周课用虚线上边（XAML 的 <see cref="Rectangle"/> 没有 dashed border，
    /// 用边框粗细 + 透明度表达"特殊但克制"，与渲染层的意图一致）。
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
                // 完整信息在 ToolTip 里，块内只放能放得下的部分
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
                    FontSize = block.FontSize - 0.5,
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
