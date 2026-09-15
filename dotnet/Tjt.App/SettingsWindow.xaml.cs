using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tjt.App.Data;
using Tjt.App.Rendering;
using Windows.Graphics;
using WinRT.Interop;

namespace Tjt.App;

/// <summary>
/// 设置窗口 —— 视觉照 DeskBox 那套：**左侧导航 + 顶部搜索 + 右侧卡片行**。
///
/// <para>页面：常规（启动/贴桌面层/位置）、外观（主题/材质）、关于。
/// 只做本机设置；数据导入（粘贴 JSON / 选文件 / Cookie 抓取）还没搬过来，
/// 所以这里不放导入面板 —— 宁可少做，也不要放一个点了没反应的按钮。</para>
///
/// <para>改动**即时生效并落盘**（没有"保存"按钮）：设置项都是开关/单选/下拉，
/// 改完立刻能看到效果，多一个"保存"只会多一个忘记点的机会。**材质也在运行时即时切换**
/// （换控制器、不重建窗口，见 <c>BackdropHelper.SetMaterial</c>）。</para>
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>导航项定义（字形 + 文案 + 页面标题）。</summary>
    private static readonly (string Glyph, string Label, string PageTitle)[] NavItems =
    [
        ("\uE80F", "常规", "常规"),
        ("\uE790", "外观", "外观"),
        ("\uE946", "关于", "关于"),
    ];

    private readonly Action<WidgetSettings> _onChanged;
    private readonly Action _onResetPosition;
    private readonly ContentPresenter _page = new();
    private WidgetSettings _current;
    private bool _loading = true;

    /// <summary>构造设置窗口。</summary>
    /// <param name="current">当前设置（用来回显）。</param>
    /// <param name="dark">当前是否深色主题。</param>
    /// <param name="onChanged">设置变化回调（外壳负责应用 + 落盘）。</param>
    /// <param name="onResetPosition">"把挂件放回右下角"的回调。</param>
    internal SettingsWindow(WidgetSettings current, bool dark, Action<WidgetSettings> onChanged, Action onResetPosition)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onResetPosition = onResetPosition ?? throw new ArgumentNullException(nameof(onResetPosition));

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        }

        var layout = BuildLayout(dark);
        SetTitleBar(layout.TitleBar);
        Host.Children.Add(layout.Root);
        Select(0);

        _loading = false;
        AppWindow.ResizeClient(new SizeInt32(980, 720));
        CenterOnScreen();
    }

    /// <summary>把窗口摆到工作区中间。</summary>
    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null) return;
        var work = area.WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.X + ((work.Width - size.Width) / 2),
            work.Y + ((work.Height - size.Height) / 2)));
    }

    /// <summary>整窗骨架：左导航 + 右内容，顶部一条自绘标题栏（含搜索框）。</summary>
    private (Grid Root, Grid TitleBar) BuildLayout(bool dark)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── 顶部：左侧标题、右侧搜索框（拖拽区由 SetTitleBar 指定整条）
        var titleBar = new Grid
        {
            Height = 48,
            Padding = new Thickness(16, 0, 16, 0),
            ColumnSpacing = 12,
            Background = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
                : Windows.UI.Color.FromArgb(14, 0, 0, 0)),
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "课表挂件",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        titleBar.Children.Add(title);

        var search = new TextBox
        {
            PlaceholderText = "搜索设置",
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            // 只有一项设置页，搜索暂无落地动作 —— 先不假装能搜
            IsEnabled = false,
        };
        ToolTipService.SetToolTip(search, "搜索还没做（当前设置项很少）");
        Grid.SetColumn(search, 1);
        titleBar.Children.Add(search);

        var spacer = new Grid();
        Grid.SetColumn(spacer, 2);
        titleBar.Children.Add(spacer);

        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        // ── 主体：NavigationView 左栏（内置 Fluent 选中态、动画、无障碍）
        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 208,
            IsPaneOpen = true,
            Content = new ScrollViewer
            {
                Padding = new Thickness(28, 12, 28, 24),
                Content = _page,
            },
        };

        foreach (var (glyph, label, _) in NavItems)
        {
            nav.MenuItems.Add(new NavigationViewItem
            {
                Content = label,
                Icon = new FontIcon { Glyph = glyph },
            });
        }

        nav.SelectionChanged += (_, args) =>
        {
            var index = nav.MenuItems.IndexOf(args.SelectedItem);
            if (index >= 0) Select(index);
        };

        Grid.SetRow(nav, 1);
        root.Children.Add(nav);
        return (root, titleBar);
    }

    /// <summary>切到第 <paramref name="index"/> 个设置页。</summary>
    private void Select(int index)
    {
        var dark = CurrentIsDark();
        _page.Content = index switch
        {
            1 => BuildAppearancePage(dark),
            2 => BuildAboutPage(dark),
            _ => BuildGeneralPage(dark),
        };
    }

    /* ------------------------------------------------------------------ 页面 */

    private UIElement BuildGeneralPage(bool dark)
    {
        var rows = new List<FrameworkElement>();

        var theme = new ComboBox
        {
            ItemsSource = new[] { "跟随系统", "深色", "浅色" },
            SelectedIndex = _current.Theme switch { ThemeMode.Dark => 1, ThemeMode.Light => 2, _ => 0 },
            MinWidth = 168,
        };
        theme.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Apply(_current with
            {
                Theme = theme.SelectedIndex switch
                {
                    1 => ThemeMode.Dark,
                    2 => ThemeMode.Light,
                    _ => ThemeMode.Auto,
                },
            });
            // 主题切换要把当前页重画一次（控件自身颜色由 RequestedTheme 决定）
            if (Content is FrameworkElement root) root.RequestedTheme = CurrentIsDark() ? ElementTheme.Dark : ElementTheme.Light;
        };
        rows.Add(SettingsView.Row("\uE793", "主题", "切换挂件的深浅配色", theme, dark));

        var showWidget = SettingsView.Switch(_current.ShowWidget);
        showWidget.Toggled += (_, _) =>
        {
            if (_loading) return;
            Apply(_current with { ShowWidget = showWidget.IsOn });
        };
        rows.Add(SettingsView.Row("\uE7F4", "启动时显示挂件", "关掉后启动只在托盘区常驻，需要时再从托盘显示", showWidget, dark));

        var desktopLayer = SettingsView.Switch(_current.DesktopLayer);
        desktopLayer.Toggled += (_, _) =>
        {
            if (_loading) return;
            Apply(_current with { DesktopLayer = desktopLayer.IsOn });
        };
        rows.Add(SettingsView.Row("\uE718", "贴桌面层", "固定在桌面图标之上：Win+D 之后仍然可见", desktopLayer, dark));

        var reset = new Button { Content = "恢复", MinWidth = 96 };
        reset.Click += (_, _) => _onResetPosition();
        rows.Add(SettingsView.Row("\uE73F", "恢复默认位置", "把挂件放回屏幕右下角（尺寸不变）", reset, dark));

        return Page("常规", rows, dark);
    }

    private UIElement BuildAppearancePage(bool dark)
    {
        var rows = new List<FrameworkElement>();

        var material = new ComboBox
        {
            ItemsSource = new[] { "Mica（推荐）", "Mica Alt", "Acrylic", "无材质（实色）" },
            SelectedIndex = _current.Material switch
            {
                MaterialMode.MicaAlt => 1,
                MaterialMode.Acrylic => 2,
                MaterialMode.Solid => 3,
                _ => 0,
            },
            MinWidth = 168,
        };
        material.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Apply(_current with
            {
                Material = material.SelectedIndex switch
                {
                    1 => MaterialMode.MicaAlt,
                    2 => MaterialMode.Acrylic,
                    3 => MaterialMode.Solid,
                    _ => MaterialMode.Mica,
                },
            });
        };
        rows.Add(SettingsView.Row("\uE790", "窗口材质", "改动即时生效；Acrylic 需要透明窗口，个别机型上观感可能与 Mica 接近", material, dark));

        var fontSize = SettingsView.Switch(true);
        fontSize.IsEnabled = false;
        ToolTipService.SetToolTip(fontSize, "还没做：字号目前随行高自适应");
        rows.Add(SettingsView.Row("\uE8D2", "显示周末", "还没做（需要用周次过滤那一套设置）", fontSize, dark));

        return Page("外观", rows, dark);
    }

    private UIElement BuildAboutPage(bool dark)
    {
        var rows = new List<FrameworkElement>
        {
            SettingsView.InfoRow("版本", Version(), dark),
            SettingsView.InfoRow("课表数据", "读的是 packages/core/fixtures 里的样例数据；导入真实课表还没搬过来", dark),
            SettingsView.InfoRow("设置文件", SettingsStore.FilePath, dark),
        };
        return Page("关于", rows, dark);
    }

    private static UIElement Page(string title, IEnumerable<FrameworkElement> rows, bool dark)
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(SettingsView.PageTitle(title));
        panel.Children.Add(SettingsView.Card(rows, dark));
        return panel;
    }

    /* ------------------------------------------------------------------ 辅助 */

    private static string Version()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        return version is null ? "未知" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private bool CurrentIsDark() => _current.Theme switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => BackdropHelper.SystemUsesDarkTheme(),
    };

    private void Apply(WidgetSettings next)
    {
        _current = next;
        _onChanged(next);
    }
}
