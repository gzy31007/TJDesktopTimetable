using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Tjt.App.Rendering;

/// <summary>
/// 设置界面的行/卡片构件（照 DeskBox 那套视觉语言：**每项一行，左边图标 + 标题 + 说明，
/// 右边控件**，若干行组成一张卡片）。
///
/// <para>抽成静态构件的原因：设置页会长，行与卡片的间距/圆角/描边必须一致，
/// 复制粘贴十六份 <c>Thickness</c> 一定会走样。</para>
/// </summary>
internal static class SettingsView
{
    /// <summary>副说明文字的不透明度（Fluent 的次级文字大致在这个量级）。</summary>
    private const double SecondaryOpacity = 0.66;

    /// <summary>
    /// 一行设置。
    /// </summary>
    /// <param name="glyph">左侧字形（Segoe Fluent Icons）。</param>
    /// <param name="title">标题。</param>
    /// <param name="description">说明（可为空）。</param>
    /// <param name="control">右侧控件。</param>
    /// <param name="dark">深色主题（决定描边与图标色）。</param>
    public static Grid Row(string glyph, string title, string? description, FrameworkElement control, bool dark)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (description is { Length: > 0 })
        {
            text.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Opacity = SecondaryOpacity,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 240, 240, 240) : Color.FromArgb(255, 32, 32, 32)),
        };

        var row = new Grid
        {
            Padding = new Thickness(14, 12, 14, 12),
            ColumnSpacing = 12,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(control, 2);
        control.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    /// <summary>一行"只读信息"（右侧没有控件）—— 左侧字形从行首移到文字里更省地方。</summary>
    /// <param name="title">标题。</param>
    /// <param name="description">说明。</param>
    /// <param name="dark">深色主题。</param>
    public static Grid InfoRow(string title, string description, bool dark)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Opacity = SecondaryOpacity,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        var row = new Grid { Padding = new Thickness(14, 12, 14, 12) };
        row.Children.Add(text);
        return row;
    }

    /// <summary>把若干行拼成一张卡片（行之间一条极淡的分隔线）。</summary>
    /// <param name="rows">行。</param>
    /// <param name="dark">深色主题。</param>
    public static Border Card(IEnumerable<FrameworkElement> rows, bool dark)
    {
        var panel = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (!first)
            {
                panel.Children.Add(new Rectangle
                {
                    Height = 1,
                    Margin = new Thickness(14, 0, 14, 0),
                    Fill = new SolidColorBrush(dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)),
                });
            }

            first = false;
            panel.Children.Add(row);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0)),
            Child = panel,
        };
    }

    /// <summary>页面标题（对应截图里那个"常规"）。</summary>
    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        FontSize = 26,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 0, 0, 6),
    };

    /// <summary>下拉框（固定宽度，与截图里的控件栏对齐）。</summary>
    public static ComboBox Combo(string[] items, int selected)
    {
        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = selected,
            MinWidth = 168,
        };
        return combo;
    }

    /// <summary>只有开关、没有文字的 <see cref="ToggleSwitch"/>（截图里右侧那个小开关）。</summary>
    public static ToggleSwitch Switch(bool value)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
        };
        return toggle;
    }
}
