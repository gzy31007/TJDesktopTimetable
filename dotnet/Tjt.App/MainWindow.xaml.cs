using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tjt.App.Data;
using Tjt.App.Rendering;
using Tjt.App.Win32;
using Tjt.Widget;
using Windows.Graphics;
using WinRT.Interop;

namespace Tjt.App;

/// <summary>
/// 挂件主窗口。
///
/// 结构上是"薄壳"：窗口选项 / 材质 / 桌面层 / 尺寸换算在这里，课表内容一律交给
/// <see cref="BoardRenderer"/>（数据由 <c>Tjt.Widget</c> 算好）。
///
/// <para><b>自适应</b>：窗口尺寸一变就按新尺寸重建整棵可视树（语义与渲染层重新计算布局一致）。
/// 纵向会把行高压到刚好铺满（下限 34），横向保持最小列宽 72、装不下就横向滚动 ——
/// 也就是说"缩窗口"不会把格子压成一条缝，而是先压行高、再出滚动条。</para>
///
/// <para><b>尺寸换算的坑</b>：XAML 的 <c>Width</c>/<c>Height</c> 是 DIP，而 <c>AppWindow.ResizeClient</c>
/// 要物理像素。150% 缩放下直接把 DIP 当像素用，窗口会比预期小一半、网格被裁掉。</para>
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>时间线刷新间隔：一分钟够用（节次最小粒度是 5 分钟）。</summary>
    private static readonly TimeSpan NowRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>默认窗口尺寸（DIP）：够放 7 列 × 11 节且不用滚动。</summary>
    internal const int DefaultWidth = 1080;
    internal const int DefaultHeight = 700;

    /// <summary>贴边留白（物理像素）：右下角定位用。</summary>
    private const int ScreenMargin = 24;

    private readonly DispatcherTimer _nowTimer = new();

    private LoadedTimetable? _loaded;
    private AppStartupOptions _options = new();
    private BackdropHelper? _backdrop;
    private FrameworkElement? _root;
    private double _dpiScale = 1.0;

    /// <summary>构造窗口但不做任何可见性动作（冒烟测试需要"建了但不显示"）。</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
        SizeChanged += OnSizeChanged;
        _nowTimer.Interval = NowRefreshInterval;
        _nowTimer.Tick += (_, _) => Render(reason: "时间线刷新");
    }

    /// <summary>渲染结果自检信息（冒烟测试与日志用）。</summary>
    internal WindowLayoutInfo? Layout { get; private set; }

    /// <summary>材质模式（日志/自检用）。</summary>
    internal string BackdropMode => _backdrop?.Mode ?? "none";

    /// <summary>布局信息的快照（比渲染层内部的 BoardVisual 更适合对外断言）。</summary>
    /// <param name="CanvasWidth">画布宽（DIP）。</param>
    /// <param name="CanvasHeight">画布高（DIP）。</param>
    /// <param name="Blocks">色块数。</param>
    /// <param name="Days">列数（天）。</param>
    /// <param name="Slots">行数（节次）。</param>
    /// <param name="Title">学期标题。</param>
    /// <param name="HeaderText">顶部条整行文案。</param>
    /// <param name="RowHeight">当前行高。</param>
    /// <param name="NeedsScroll">是否出现滚动（横向或纵向）。</param>
    internal sealed record WindowLayoutInfo(
        double CanvasWidth,
        double CanvasHeight,
        int Blocks,
        int Days,
        int Slots,
        string Title,
        string HeaderText,
        double RowHeight,
        bool NeedsScroll);

    /// <summary>载入课表 → 建树 → 挂材质 → （可选）贴桌面层 → 定位到右下角。</summary>
    /// <param name="startup">命令行选项。</param>
    /// <param name="loaded">已载入的课表。</param>
    internal void Initialize(AppStartupOptions startup, LoadedTimetable loaded)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(loaded);

        _options = startup;
        _loaded = loaded;

        // 1) 材质：窗口创建后尽早设置
        AppLog.Line("[stage] window-created");
        if (startup.NoBackdrop)
        {
            AppLog.Line("[backdrop] skipped (--no-backdrop)");
        }
        else
        {
            _backdrop = BackdropHelper.Apply(this, micaAlt: false);
            AppLog.Line($"[backdrop] mode={_backdrop.Mode}");
        }

        // 2) 显式给了 --size 就精确设成它（用于验证自适应）；否则用窗口系统给的默认尺寸
        if (startup is { Width: { } w, Height: { } h })
        {
            var dpi = NativeMethods.GetDpiForWindow(WindowNative.GetWindowHandle(this));
            var scale = dpi > 0 ? dpi / NativeMethods.DefaultDpi : 1.0;
            AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale)));
            AppLog.Line($"[window] --size {w}x{h}dip");
        }

        // 3) 首屏按当前客户区算一次
        Render("首次布局");
        AppLog.Line("[stage] canvas-rendered");

        // 4) 默认贴右下角（用户自己拖过之后就该由 settings 决定，那部分还没做）
        PlaceBottomRight();

        _nowTimer.Start();
        AppLog.Line($"[layout] canvas={Layout!.CanvasWidth:0}x{Layout.CanvasHeight:0}dip rowH={Layout.RowHeight:0.#} scroll={Layout.NeedsScroll} blocks={Layout.Blocks} header=\"{Layout.HeaderText}\"");
    }

    /// <summary>真正显示窗口（冒烟测试不调用它，因此不会弹窗）。</summary>
    internal void ShowWidget()
    {
        Activate();
        AppLog.Line($"[window] visible handle=0x{WindowNative.GetWindowHandle(this):X}");
    }

    /// <summary>断开材质与事件（关闭前调用）。</summary>
    internal void Teardown()
    {
        _nowTimer.Stop();
        _backdrop?.Dispose();
        _backdrop = null;
    }

    /// <summary>
    /// 按当前窗口尺寸重建可视树。
    ///
    /// <paramref name="reason"/> 只用于日志（startup / resize / tick），
    /// 方便在真机上看清"是谁触发的重排"。
    /// </summary>
    private void Render(string reason)
    {
        if (_loaded is null) return;

        var handle = WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(handle);
        _dpiScale = dpi > 0 ? dpi / NativeMethods.DefaultDpi : 1.0;

        // 首帧窗口还没量出客户区（ClientSize 为 0），先用默认尺寸算一次；
        // 之后一律以真实客户区为准 —— 用户缩窗口不会被我们顶回去。
        var client = ClientSizeDip(handle);
        var widthDip = client.Width > 1 ? client.Width : DefaultWidth;
        var heightDip = client.Height > 1 ? client.Height : DefaultHeight;

        var board = Tjt.Core.Layout.BuildBoard(
            _loaded.Timetable.Courses,
            _loaded.Timetable.Term,
            new Tjt.Core.BoardOptions { TrimEmptySlots = true });
        var visual = BoardVisualBuilder.Build(
            board,
            widthDip,
            _options.Dark ?? BackdropHelper.SystemUsesDarkTheme(),
            Tjt.Core.Time.LocalMinutesOfDay(),
            minCellWidth: 72,
            availableHeight: heightDip);

        var root = BoardRenderer.Render(visual, _options.Dark ?? BackdropHelper.SystemUsesDarkTheme());
        Host.Children.Clear();
        Host.Children.Add(root);
        _root = root;

        Layout = new WindowLayoutInfo(
            visual.CanvasWidth,
            visual.CanvasHeight,
            visual.Blocks.Count,
            visual.Days.Count,
            visual.Slots.Count,
            visual.Title,
            $"{visual.Header.Title} {visual.Header.WeekText} {visual.Header.TodayText}".Trim(),
            visual.Geometry.RowHeight,
            visual.NeedsHorizontalScroll || visual.NeedsVerticalScroll);

        AppLog.Line($"[relayout] {reason} client={widthDip:0}x{heightDip:0}dip colW={visual.Geometry.CellWidth:0} rowH={visual.Geometry.RowHeight:0.#} scroll={visual.NeedsHorizontalScroll}/{visual.NeedsVerticalScroll} blocks={visual.Blocks.Count}");
    }

    /// <summary>窗口尺寸变化 → 重排（带一点去抖，拖动缩放时不必每帧都重建）。</summary>
    private void OnSizeChanged(object sender, WindowSizeChangedEventArgs args) => Render("窗口尺寸变化");

    /// <summary>客户区尺寸（DIP）。</summary>
    private (double Width, double Height) ClientSizeDip(nint handle)
    {
        var size = AppWindow.ClientSize;
        var scale = _dpiScale > 0 ? _dpiScale : 1.0;
        return (size.Width / scale, size.Height / scale);
    }

    /// <summary>
    /// 默认摆到**右下角**（工作区内留 24px 边距）。
    ///
    /// 用 <see cref="DisplayArea"/> 拿工作区而不是屏幕尺寸：任务栏在下面时，
    /// 按屏幕高度算会把挂件压到任务栏底下。
    /// </summary>
    private void PlaceBottomRight()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null)
        {
            AppLog.Line("[window] 拿不到 DisplayArea，跳过右下角定位");
            return;
        }

        var work = area.WorkArea;
        var size = AppWindow.Size;
        var x = work.X + work.Width - size.Width - ScreenMargin;
        var y = work.Y + work.Height - size.Height - ScreenMargin;
        AppWindow.Move(new PointInt32(Math.Max(work.X, x), Math.Max(work.Y, y)));
        AppLog.Line($"[window] bottom-right at ({x},{y}) work={work.X},{work.Y} {work.Width}x{work.Height} size={size.Width}x{size.Height}");
    }
}
