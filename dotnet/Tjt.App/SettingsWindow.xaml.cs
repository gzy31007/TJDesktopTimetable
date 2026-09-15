using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tjt.App.Data;
using Tjt.App.Rendering;
using Tjt.App.Win32;
using Windows.Graphics;
using WinRT.Interop;

namespace Tjt.App;

/// <summary>
/// 设置窗口 —— 视觉照 DeskBox 那套：**左侧导航 + 顶部搜索 + 右侧卡片行**。
///
/// <para>页面：常规（启动/贴桌面层/位置）、导入（抓取 / 本地 JSON / 数据目录）、
/// 外观（主题/材质）、关于。</para>
///
/// <para>改动**即时生效并落盘**（没有"保存"按钮）：设置项都是开关/单选/下拉，
/// 改完立刻能看到效果，多一个"保存"只会多一个忘记点的机会。**材质也在运行时即时切换**
/// （换控制器、不重建窗口，见 <c>BackdropHelper.SetMaterial</c>）。</para>
///
/// <para><b>它不认识 <c>MainWindow</c></b>：需要外壳做的事（应用设置、重载课表、导入落盘）
/// 全部通过 <see cref="SettingsHost"/> 的委托走 —— 窗口之间不互相引用，改一边不会牵动另一边。</para>
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>页下标：常规。</summary>
    internal const int PageGeneral = 0;

    /// <summary>页下标：导入课表。</summary>
    internal const int PageImport = 1;

    /// <summary>页下标：外观。</summary>
    internal const int PageAppearance = 2;

    /// <summary>页下标：关于。</summary>
    internal const int PageAbout = 3;

    /// <summary>导航项定义（字形 + 文案 + 页面标题）。</summary>
    private static readonly (string Glyph, string Label, string PageTitle)[] NavItems =
    [
        ("\uE80F", "常规", "常规"),
        (IconGlyph.Import, "导入", "导入课表"),
        ("\uE790", "外观", "外观"),
        ("\uE946", "关于", "关于"),
    ];

    private readonly SettingsHost _host;
    private readonly ContentPresenter _page = new();
    private NavigationView? _nav;
    private WidgetSettings _current;
    private bool _loading = true;
    private int _shownPage;

    /// <summary>
    /// 设置窗口需要外壳配合的三件事 + 导入编排。
    /// </summary>
    /// <param name="Apply">应用新设置（外壳负责落盘 + 立即生效）。</param>
    /// <param name="ResetPosition">把挂件放回屏幕右下角。</param>
    /// <param name="ReloadTimetable">按载入顺序重新读一遍课表。</param>
    /// <param name="Imports">导入编排（抓取 / 本地导入 / 清空）。</param>
    /// <param name="OpenLogin">打开内置登录窗口（导入页的「登录同济并获取」按钮）。</param>
    internal sealed record SettingsHost(
        Action<WidgetSettings> Apply,
        Action ResetPosition,
        Action ReloadTimetable,
        ImportService Imports,
        Action? OpenLogin = null);

    /// <summary>构造设置窗口。</summary>
    /// <param name="current">当前设置（用来回显）。</param>
    /// <param name="dark">当前是否深色主题。</param>
    /// <param name="host">外壳回调 + 导入编排。</param>
    /// <param name="initialPage">
    /// 打开时停在哪一页。**必须走构造参数**：只调 <see cref="SelectPage"/> 在"窗口还没加载"时
    /// 可能不回调 <c>SelectionChanged</c>，于是托盘「导入课表…」第一次点开会落在「常规」页
    /// （截图实测过这个 bug）。
    /// </param>
    internal SettingsWindow(WidgetSettings current, bool dark, SettingsHost host, int initialPage = 0)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
        _host = host ?? throw new ArgumentNullException(nameof(host));

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
        Select(Math.Clamp(initialPage, 0, NavItems.Length - 1));

        _loading = false;
        ResizeForDpi(980, 720);
        CenterOnScreen();
    }

    /// <summary>
    /// 按设计尺寸（DIP）设窗口客户区，并夹到当前显示器的工作区内。
    ///
    /// <para><b>为什么不能直接 <c>ResizeClient(980, 720)</c></b>：那个 API 收的是**物理像素**，
    /// 所以 150% 缩放下窗口在屏幕上只有 653×480 DIP —— 左侧导航占掉 208 DIP 后，
    /// 卡片只剩约 380 DIP 宽，导入页的说明文字一行只放得下十个字（真机截图实测）。
    /// 这里按 <c>GetDpiForWindow()/96</c> 折算，让"看起来的大小"与缩放无关。</para>
    /// </summary>
    private void ResizeForDpi(int widthDip, int heightDip)
    {
        var handle = WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / NativeMethods.DefaultDpi : 1.0;
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);

        // 夹到工作区：小屏 + 高缩放时（如 1080p @150%）按 DIP 折算会超出屏幕高度
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is not null)
        {
            var work = area.WorkArea;
            width = Math.Min(width, work.Width - 40);
            height = Math.Min(height, work.Height - 40);
        }

        AppWindow.ResizeClient(new SizeInt32(width, height));
        AppLog.Line($"[settings] 客户区 {width}x{height}px（{widthDip}x{heightDip} DIP，scale={scale:0.##}）");
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
        _nav = nav;

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
            PageImport => ImportPage.Build(_host.Imports, dark, WindowNative.GetWindowHandle(this), () => _page.XamlRoot, _host.OpenLogin),
            PageAppearance => BuildAppearancePage(dark),
            PageAbout => BuildAboutPage(dark),
            _ => BuildGeneralPage(dark),
        };
        _shownPage = index;
        // 左侧导航的高亮也要跟上（构造期选页时 SelectionChanged 可能还没挂上）
        if (_nav is not null && index < _nav.MenuItems.Count && !ReferenceEquals(_nav.SelectedItem, _nav.MenuItems[index]))
        {
            _nav.SelectedItem = _nav.MenuItems[index];
        }
    }

    /// <summary>当前真正显示的是第几页（自检与截图脚本用它确认"打开时停在哪一页"）。</summary>
    internal int ShownPageIndex => _shownPage;

    /// <summary>
    /// 从外面切页（托盘「导入课表…」要直接落在导入页）。
    ///
    /// 走导航项的选中状态而不是直接 <c>Select</c>：否则左侧高亮与右侧内容会不一致
    /// （看起来像点错了页）。
    /// </summary>
    internal void SelectPage(int index)
    {
        if (_loading || _nav is null) return;
        if (index < 0 || index >= _nav.MenuItems.Count) return;
        _nav.SelectedItem = _nav.MenuItems[index];
    }

    /// <summary>
    /// 把某一页真的建出来并量一遍（<c>--smoke --settings-page N</c> 用）。
    ///
    /// <para>为什么要 <c>Measure</c>：只"造出来"不足以发现版式问题 —— 上一轮"光标条落错行"
    /// 就是构建成功但尺寸塌掉的典型。量一遍能同时验证"分支跑通"和"尺寸算得出来"。</para>
    /// </summary>
    internal bool VerifyPage(int index)
    {
        Select(index);
        if (_page.Content is not Panel panel) return false;
        panel.Measure(new Windows.Foundation.Size(900, 1600));
        AppLog.Line($"[settings] page={index} children={panel.Children.Count} desired={panel.DesiredSize.Width:0}x{panel.DesiredSize.Height:0}");
        return panel.Children.Count > 0 && panel.DesiredSize.Height > 0;
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
        reset.Click += (_, _) => _host.ResetPosition();
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
            SettingsView.InfoRow("当前课表", _host.Imports.DescribeCurrent(), dark),
            SettingsView.InfoRow("数据目录", ImportService.DataDirectory + "（settings.json / timetable.json / credentials.json）", dark),
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
        _host.Apply(next);
    }
}
