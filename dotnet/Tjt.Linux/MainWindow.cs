using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tjt.Core;
using Tjt.Linux.Data;
using Tjt.Linux.Rendering;
using Tjt.Linux.X11;
using Tjt.Widget;

namespace Tjt.Linux;

/// <summary>
/// 挂件主窗口：无边框 + 半透明圆角 + 贴桌面层（X11 keep-below）。
///
/// <para>窗口行为对应 Windows 版的自实现那套，但走 Avalonia 原生循环：
/// 拖动 = 顶部条 <c>BeginMoveDrag</c>，缩放 = 八块热区 <c>BeginResizeDrag</c>
/// （热区几何与最小尺寸仍由 <c>Tjt.Widget/ResizePolicy·CursorZones</c> 的纯函数决定），
/// 不需要 Windows 版那套 16ms 光标轮询 —— 那是 WinUI 指针捕获缺陷逼出来的补丁。</para>
///
/// <para>位置尺寸持久化沿用 <c>settings.json</c>（DIP 口径），恢复时校验"在某个屏幕上"，
/// 不在（拔了外接屏）就回退工作区右下角。</para>
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Size DefaultSize = new(880, 600);
    private const double WorkAreaMargin = 24;

    // 挂件外壳底色：Linux 没有 Mica/Acrylic，用固定半透明色 + 合成器真透明近似观感
    private const string ShellDark = "#D91E2124";
    private const string ShellLight = "#D9F9F9FB";

    private readonly AppStartupOptions _options;
    private WidgetSettings _settings;
    private LoadedTimetable _loaded;
    private readonly bool _dark;
    private readonly ImportService _importService;
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private ImportWindow? _importWindow;
    private (double? NowLine, string Week, string? Today) _lastClockSignature;
    private bool _restoringBounds;

    public MainWindow(AppStartupOptions options)
    {
        _options = options;
        _settings = SettingsStore.Load();
        _dark = options.Dark ?? _settings.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => DetectSystemDark(),
        };

        Title = "TJDesktopTimetable";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        MinWidth = ResizePolicy.MinWidth;
        MinHeight = ResizePolicy.MinHeight;

        Width = options.Width ?? _settings.Bounds?.Width ?? (int)DefaultSize.Width;
        Height = options.Height ?? _settings.Bounds?.Height ?? (int)DefaultSize.Height;

        _loaded = AppHost.Load(options.FixturePath);
        AppLog.Line($"[main] 课表来源：{_loaded.Source}（{AppHost.OriginLabel(_loaded.Origin)}）；主题 {(_dark ? "深色" : "浅色")}");

        _importService = new ImportService(
            apply: (timetable, _) =>
            {
                var ok = TimetableStore.Save(timetable);
                if (ok) Reload();
                return ok;
            },
            reload: () =>
            {
                Reload();
                return true;
            },
            describe: () => $"{_loaded.Timetable.Courses.Count} 门 · {TimetableStore.Count(_loaded.Timetable)} 条 · {_loaded.Source}");

        Render();

        Opened += OnOpened;
        Resized += (_, _) =>
        {
            Render();
            QueueSaveBounds();
        };
        PositionChanged += (_, _) => QueueSaveBounds();
        Closing += (_, _) => SaveBoundsNow();

        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveBoundsNow();
        };

        // 每分钟刷新一次"当前时间线 / 今日高亮"；签名没变就跳过（避免重置滚动位置）
        _clock.Tick += (_, _) => Render(clockOnly: true);
        _clock.Start();
    }

    /// <summary>当前课表来源（"首次启动自动开导入窗口"的判据）。</summary>
    public TimetableOrigin LoadedOrigin => _loaded.Origin;

    /// <summary>当前是否深色主题（导入窗口跟随）。</summary>
    public bool Dark => _dark;

    public bool WeekendEnabled => _options.Weekend ?? _settings.ShowWeekend;

    public bool DesktopLayerEnabled => _options.DesktopLayer ?? _settings.DesktopLayer;

    /// <summary>重新按载入顺序读课表并重画（"重新载入"与导入应用共用这一个入口）。</summary>
    public void Reload()
    {
        _loaded = AppHost.Load(_options.FixturePath);
        AppLog.Line($"[main] 重新载入：{_loaded.Source}（{AppHost.OriginLabel(_loaded.Origin)}）");
        Render();
    }

    /// <summary>打开导入窗口（单例：已开着就激活）。</summary>
    public void OpenImportWindow()
    {
        if (_importWindow is null)
        {
            _importWindow = new ImportWindow(this, _importService);
            _importWindow.Closed += (_, _) => _importWindow = null;
            _importWindow.Show(this);
        }
        else
        {
            _importWindow.Activate();
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DesktopLayerEnabled)
        {
            ApplyDesktopLayer(true);
        }

        // 位置恢复：DIP → 物理像素（用主屏缩放近似；多屏异缩放时 Opened 后有屏内校验兜底）
        if (_settings.Bounds is { IsUsable: true } bounds && _options.Width is null && _options.Height is null)
        {
            var scale = Screens.Primary?.Scaling ?? RenderScaling;
            _restoringBounds = true;
            Position = new PixelPoint((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale));
            _restoringBounds = false;
            if (IsOnAnyScreen())
            {
                AppLog.Line($"[bounds] 已恢复 {bounds}");
            }
            else
            {
                AppLog.Line("[bounds] 恢复的位置不在任何显示器上，回退右下角");
                ResetToDefaultPosition();
            }
        }
        else
        {
            ResetToDefaultPosition();
        }
    }

    private void Render(bool clockOnly = false)
    {
        var size = ClientSize;
        var width = size.Width > 0 ? size.Width : Width;
        var height = size.Height > 0 ? size.Height : Height;

        var state = Layout.BuildBoard(_loaded.Timetable.Courses, _loaded.Timetable.Term, new BoardOptions
        {
            // 与 Windows 线同口径（Tjt.App/MainWindow.xaml.cs）：不收窄会画满 1..11 节，
            // 行数 / 行高 / 纵向滚动判定都会与 Windows 分叉（黄金 fixture 最大 11 节所以之前没暴露）。
            TrimEmptySlots = true,
            ShowWeekend = WeekendEnabled,
            Now = DateTimeOffset.UtcNow,
        });
        var nowMinutes = Time.LocalMinutesOfDay(DateTimeOffset.UtcNow, TimetableModel.DefaultTzOffsetMinutes);
        var visual = BoardVisualBuilder.Build(state, width, _dark, nowMinutes, 72, height);

        // 时间线 / 周次 / 今日文案都没变就不重建（滚动位置、悬停状态全部保持）
        var signature = (visual.NowLineTop, visual.Header.WeekText, visual.Header.TodayText);
        if (clockOnly && signature == _lastClockSignature) return;
        _lastClockSignature = signature;

        var board = BoardRenderer.Render(visual, _dark, BuildActions());
        Content = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(ParseColor(_dark ? ShellDark : ShellLight)),
            BorderBrush = new SolidColorBrush(ParseColor(TintPalette.Stroke(_dark))),
            BorderThickness = new Thickness(1),
            Child = board,
        };
    }

    private WidgetActions BuildActions() => new()
    {
        OpenImport = OpenImportWindow,
        Refresh = Reload,
        ResetPosition = ResetToDefaultPosition,
        DesktopLayer = DesktopLayerEnabled,
        ToggleDesktopLayer = ToggleDesktopLayer,
        ShowWeekend = WeekendEnabled,
        ToggleShowWeekend = ToggleWeekend,
        Exit = Close,
        AttachDragArea = AttachDrag,
        BeginResize = BeginResizeFromZone,
    };

    private void AttachDrag(Control element)
    {
        element.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }

    private void BeginResizeFromZone(WindowEdge edge, PointerPressedEventArgs e) => BeginResizeDrag(edge, e);

    private void ToggleWeekend()
    {
        if (_options.Weekend is not null)
        {
            AppLog.Line("[menu] 显示周末被 CLI 覆盖，忽略切换");
            return;
        }

        _settings = _settings with { ShowWeekend = !WeekendEnabled };
        SettingsStore.Save(_settings);
        AppLog.Line($"[settings] 显示周末 → {WeekendEnabled}");
        Render();
    }

    private void ToggleDesktopLayer()
    {
        if (_options.DesktopLayer is not null)
        {
            AppLog.Line("[menu] 贴桌面层被 CLI 覆盖，忽略切换");
            return;
        }

        _settings = _settings with { DesktopLayer = !DesktopLayerEnabled };
        SettingsStore.Save(_settings);
        var enabled = DesktopLayerEnabled;
        AppLog.Line($"[settings] 贴桌面层 → {enabled}");
        ApplyDesktopLayer(enabled);
    }

    private void ApplyDesktopLayer(bool below)
    {
        var handle = TryGetPlatformHandle();
        // Avalonia 12 里 X11 句柄的描述符叫 "XID"（11.x 时代叫 "X11"，两个都认）
        if (handle is null || (handle.HandleDescriptor != "X11" && handle.HandleDescriptor != "XID"))
        {
            AppLog.Line($"[layer] 非 X11 后端（{handle?.HandleDescriptor ?? "none"}），贴桌面层不可用（可改用 WM 规则）");
            return;
        }

        X11KeepBelow.Apply(handle.Handle, below);
    }

    private void ResetToDefaultPosition()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        var work = screen.WorkingArea;
        Position = new PixelPoint(work.Right - (int)WorkAreaMargin - w, work.Bottom - (int)WorkAreaMargin - h);
        AppLog.Line($"[bounds] 默认位置：右下角（工作区 {work.Width}x{work.Height}，缩放 {scale}）");
    }

    private bool IsOnAnyScreen()
    {
        var scale = RenderScaling;
        var size = ClientSize;
        var rect = new PixelRect(Position, new PixelSize(
            Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale))));
        return Screens.All.Any(screen => screen.Bounds.Intersects(rect));
    }

    private void QueueSaveBounds()
    {
        if (!IsVisible || _restoringBounds) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveBoundsNow()
    {
        if (!IsVisible || _restoringBounds) return;
        var scale = RenderScaling;
        var pos = Position;
        var size = ClientSize;
        var bounds = new WindowBounds(
            (int)Math.Round(pos.X / scale),
            (int)Math.Round(pos.Y / scale),
            (int)Math.Round(size.Width),
            (int)Math.Round(size.Height));
        if (bounds == _settings.Bounds) return;
        _settings = _settings with { Bounds = bounds };
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// 探测桌面环境是否深色（<c>ThemeMode.Auto</c> 用）：
    /// KDE 读 <c>~/.config/kdedefaults/package|colors</c>，GNOME 读 <c>gsettings</c>；
    /// 都拿不到按浅色。探测失败不影响启动。
    /// </summary>
    internal static bool DetectSystemDark()
    {
        try
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var name in new[] { "package", "colors" })
            {
                var path = Path.Combine(config, "kdedefaults", name);
                if (File.Exists(path) &&
                    File.ReadAllText(path).Contains("dark", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // 探测不到就按浅色
        }

        try
        {
            // 走带超时的 Subprocess：gsettings 挂住不能拖死窗口构造（这里在构造函数里）
            var output = Subprocess.Output("gsettings", "get", "org.gnome.desktop.interface", "color-scheme");
            if (output is not null && output.Contains("dark", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception)
        {
            // 没装 gsettings 的桌面环境（KDE 常见）
        }

        return false;
    }

    private static Color ParseColor(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        return Color.FromArgb(
            Convert.ToByte(text.Substring(0, 2), 16),
            Convert.ToByte(text.Substring(2, 2), 16),
            Convert.ToByte(text.Substring(4, 2), 16),
            Convert.ToByte(text.Substring(6, 2), 16));
    }
}
