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
    private WidgetSettings _settings = new();
    private DesktopLayer? _layer;
    private WindowBounds _targetBounds = new(0, 0, DefaultWidth, DefaultHeight);
    private bool _userSizedRecently;
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
        _settings = SettingsStore.Load();

        // 1) 材质：窗口创建后尽早设置
        AppLog.Line("[stage] window-created");
        if (startup.NoBackdrop || _settings.NoBackdrop)
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

        // 4) 位置：优先恢复上次保存的；没有（或已经不在可见工作区）才落到右下角
        var restored = RestoreSavedBounds();
        if (!restored) PlaceBottomRight();

        // 5) 贴桌面层（owner 挂 SHELLDLL_DefView）+ 5 秒 owner 巡检 + 事件通道
        var desktopLayer = startup.DesktopLayer ?? _settings.DesktopLayer;
        _layer = DesktopLayer.Attach(this, desktopLayer);
        InstallWindowHooks();
        SaveBounds("启动");

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
        if (_layer is not null)
        {
            SaveBounds("退出");
            _layer.Detach();
            _layer = null;
        }

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

    /// <summary>
    /// 订阅三类窗口消息，把"用户开始拖动"、"拖动结束"、"窗口失活"变成事件。
    ///
    /// 拖动期间**必须暂停 owner 巡检**：巡检会在拖动中途重挂 owner、和拖动抢 z-order
    /// （Electron 侧的老实现正是栽在这里）。
    /// </summary>
    private void InstallWindowHooks()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        MessageHook.Subscribe(hwnd, NativeMethods.Constants.WmEnterSizeMove, () =>
        {
            AppLog.Line("[layer] 开始拖动/缩放 → 暂停巡检并临时浮起");
            _layer?.SuspendForInteraction("enter-size-move");
        });
        MessageHook.Subscribe(hwnd, NativeMethods.Constants.WmExitSizeMove, () =>
        {
            // 先把新位置写盘，再落点：落点可能改变 z-order，但不改坐标。
            // 标记"用户调过" → SaveBounds 会存实测外框（他想要的就是当前这个尺寸）。
            _userSizedRecently = true;
            SaveBounds("拖动结束");
            _layer?.ResumeAfterInteraction("exit-size-move");
        });
        MessageHook.Subscribe(hwnd, NativeMethods.Constants.WmActivate, () =>
        {
            // 窗口失活（用户点到别处）也是一次"位置可能变了"的信号，顺便持久化
            SaveBounds("失活");
        });
    }

    /// <summary>
    /// 恢复上次保存的位置与尺寸。
    ///
    /// 校验两件事：尺寸可用、且**至少有一部分落在某个显示器的工作区内**
    /// —— 否则拔掉外接屏之后挂件会恢复到看不见的地方。
    /// </summary>
    private bool RestoreSavedBounds()
    {
        if (_settings.Bounds is not { } bounds || !bounds.IsUsable)
        {
            return false;
        }

        // 保存的是外框：换算成 ResizeClient 需要的客户区（首帧没有校正值时先按外框试一次，
        // 随后由 SaveBounds 实测出来的 FrameCorrection 收敛）。
        var correction = _settings.FrameCorrection ?? new WindowBounds(0, 0, 15, 45);
        var clientWidth = Math.Max(320, bounds.Width - correction.Width);
        var clientHeight = Math.Max(240, bounds.Height - correction.Height);

        AppWindow.Move(new PointInt32(
            (int)Math.Ceiling(bounds.X * _dpiScale),
            (int)Math.Ceiling(bounds.Y * _dpiScale)));
        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Ceiling(clientWidth * _dpiScale),
            (int)Math.Ceiling(clientHeight * _dpiScale)));

        _targetBounds = bounds;

        if (!IsVisibleOnSomeDisplay(bounds))
        {
            return false;
        }

        AppLog.Line($"[window] 已恢复上次位置 bounds={bounds} correction={correction} → client={clientWidth}x{clientHeight}dip");
        AppLog.Line($"[window] 恢复后实测 {MeasureGeometry()}");
        return true;
    }

    /// <summary>
    /// 保存的位置是否还看得见（至少左上角落在某个显示器上）。
    ///
    /// 不需要枚举所有显示器：<see cref="DisplayArea.GetFromPoint"/> 在"该点不在任何显示器上"时
    /// 会返回 <see cref="DisplayAreaFallback.Primary"/>，据此就能判定。WASDK 1.8 **没有**
    /// <c>DisplayArea.FindAll()</c>，而构造 <c>DisplayId</c> 的投影类型也不稳（真机实测两次编译错），
    /// 所以走这条更简单也更稳的路。
    /// </summary>
    private bool IsVisibleOnSomeDisplay(WindowBounds bounds)
    {
        // 左上角往内收一点：贴边摆放时 (x, y) 可能刚好在边界外半个像素
        var x = bounds.X + 8;
        var y = bounds.Y + 8;
        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.None);
        if (area is null)
        {
            AppLog.Line($"[window] 保存的位置 ({x},{y}) 不在任何显示器上，回退右下角");
            return false;
        }

        var work = area.WorkArea;
        var inside = x >= work.X && x < work.X + work.Width && y >= work.Y && y < work.Y + work.Height;
        if (!inside)
        {
            AppLog.Line($"[window] 保存的位置 ({x},{y}) 落在工作区之外（work={work.X},{work.Y} {work.Width}x{work.Height}），回退右下角");
        }

        return inside;
    }

    /// <summary>
    /// 把位置与尺寸写进设置（DIP，外框口径）。
    ///
    /// <para><b>为什么分两种口径</b>：Windows 会把窗口外框 snap 到一个**最小尺寸**，
    /// 所以 `ResizeClient(期望值)` 之后实测外框可能比期望大一点点。如果把 snap 后的尺寸当
    /// "用户尺寸"存回去，下次恢复就更大一点 —— 实测每跑一次长 30 DIP，是正反馈不是收敛。
    /// 因此：</para>
    /// <list type="bullet">
    /// <item><description>**用户刚拖过 / 缩放过**（<c>WM_EXITSIZEMOVE</c>）→ 存实测外框（他想要的就是这个）；</description></item>
    /// <item><description>否则 → 存**期望外框**，并把"实测外框 - 客户区"记成校正值，
    /// 下次恢复用它把外框换算成 <c>ResizeClient</c> 要的客户区。</description></item>
    /// </list>
    /// </summary>
    private void SaveBounds(string reason)
    {
        // 冒烟自检不写用户设置：它是"跑一遍就退出"的短命进程，落盘的坐标只会污染真实配置
        if (_options.Smoke) return;

        var hwnd = WindowNative.GetWindowHandle(this);
        if (!NativeMethods.IsWindow(hwnd)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var outer)) return;

        var scale = _dpiScale > 0 ? _dpiScale : 1.0;
        var measured = new WindowBounds(
            (int)Math.Round(outer.Left / scale),
            (int)Math.Round(outer.Top / scale),
            (int)Math.Round((outer.Right - outer.Left) / scale),
            (int)Math.Round((outer.Bottom - outer.Top) / scale));

        var client = AppWindow.ClientSize;
        var correction = new WindowBounds(
            0,
            0,
            Math.Max(0, measured.Width - (int)Math.Round(client.Width / scale)),
            Math.Max(0, measured.Height - (int)Math.Round(client.Height / scale)));

        // 位置永远按实测存（拖动就是位置变化的唯一来源）。
        //
        // 尺寸只在"用户真的改了尺寸"时才按实测存：`WM_EXITSIZEMOVE` 对**纯移动**也会触发，
        // 若不加这层判断，一次拖动就会把 Windows snap 出来的尺寸写成"用户尺寸"，
        // 于是又回到"每跑一次长一点"的正反馈（实测如此）。
        var sizeChangedByUser = _userSizedRecently &&
                                (measured.Width != _targetBounds.Width || measured.Height != _targetBounds.Height);
        var stored = sizeChangedByUser
            ? measured
            : _targetBounds with { X = measured.X, Y = measured.Y };

        if (!stored.IsUsable)
        {
            AppLog.Line($"[settings] 尺寸不可用（{stored}），跳过保存（reason={reason}）");
            return;
        }

        _targetBounds = stored;
        _userSizedRecently = false;

        if (_settings.Bounds == stored && _settings.FrameCorrection == correction) return;
        _settings = _settings with { Bounds = stored, FrameCorrection = correction };
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// 实测几何（物理像素）：外框来自 <c>GetWindowRect</c>，客户区来自 WinUI 的 <c>ClientSize</c>。
    /// 两个值并排打出来，才能看清"窗口系统 snap 了多少"。
    /// </summary>
    private string MeasureGeometry()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (!NativeMethods.GetWindowRect(hwnd, out var outer)) return "outer=n/a";
        var client = AppWindow.ClientSize;
        return $"outer={outer.Left},{outer.Top} {outer.Right - outer.Left}x{outer.Bottom - outer.Top} " +
               $"client={client.Width}x{client.Height} scale={_dpiScale:0.###}";
    }

    /// <summary>层级状态快照（冒烟自检 / 真机日志用）。</summary>
    internal LayerState? LayerSnapshot() => _layer?.Snapshot();

    /// <summary>最近一次静息落点（冒烟自检 / 真机日志用）。</summary>
    internal string LastDisposition => _layer?.LastDisposition ?? "none";

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
