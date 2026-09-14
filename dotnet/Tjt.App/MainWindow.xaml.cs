using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// 尺寸换算的坑：XAML 的 <c>Width</c>/<c>Height</c> 是 DIP，而 <c>AppWindow.ResizeClient</c>
/// 要物理像素。150% 缩放下直接把 DIP 当像素用，窗口会比预期小一半、网格被裁掉。
/// 所以先把画布量出来（DIP），再乘 DPI 比例去设置窗口客户区。
/// </summary>
public sealed partial class MainWindow : Window
{
    private BackdropHelper? _backdrop;
    private double _dpiScale = 1.0;

    /// <summary>构造窗口但不做任何可见性动作（冒烟测试需要"建了但不显示"）。</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
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
    internal sealed record WindowLayoutInfo(double CanvasWidth, double CanvasHeight, int Blocks, int Days, int Slots, string Title);

    /// <summary>
    /// 载入课表 → 建画布 → 挂材质 → （可选）贴桌面层。
    /// </summary>
    /// <param name="startup">命令行选项。</param>
    /// <param name="loaded">已载入的课表。</param>
    internal void Initialize(AppStartupOptions startup, LoadedTimetable loaded)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(loaded);

        var dark = startup.Dark ?? BackdropHelper.SystemUsesDarkTheme();

        // 1) 材质：窗口创建后尽早设置（材质只影响窗口本身，与控制内容无关）
        _backdrop = BackdropHelper.Apply(this, micaAlt: false);

        // 2) 用 core 布局 + widget 呈现模型算好整块课表，再量出画布尺寸（DIP）
        var state = Tjt.Core.Layout.BuildBoard(
            loaded.Timetable.Courses,
            loaded.Timetable.Term,
            new Tjt.Core.BoardOptions { TrimEmptySlots = true });
        var visual = BoardVisualBuilder.Build(state, startup.Width, dark, Tjt.Core.Time.LocalMinutesOfDay());

        var canvas = BoardRenderer.Render(visual, dark);
        Host.Children.Clear();
        Host.Children.Add(canvas);

        Layout = new WindowLayoutInfo(
            canvas.Width,
            canvas.Height,
            visual.Blocks.Count,
            visual.Days.Count,
            visual.Slots.Count,
            visual.Title);

        // 3) 按画布尺寸设置窗口客户区（DIP → 物理像素）
        var handle = WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(handle);
        _dpiScale = dpi > 0 ? dpi / NativeMethods.DefaultDpi : 1.0;
        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Ceiling(canvas.Width * _dpiScale),
            (int)Math.Ceiling(canvas.Height * _dpiScale)));

        // 4) 贴桌面层：owner 挂 SHELLDLL_DefView，再压到 z-order 最底
        if (startup.DesktopLayer)
        {
            DesktopHost.MarkToolWindow(handle);
            var attached = DesktopHost.Attach(handle, out var detail);
            Console.WriteLine($"[desktop-layer] attach={(attached ? "ok" : "failed")} {detail}");
            if (attached)
            {
                Console.WriteLine($"[desktop-layer] send-to-bottom={(DesktopHost.SendToBottom(handle) ? "ok" : "failed")}");
            }
        }

        Console.WriteLine($"[layout] canvas={canvas.Width:0}x{canvas.Height:0}dip scale={_dpiScale:0.###} blocks={visual.Blocks.Count} days={visual.Days.Count} slots={visual.Slots.Count}");
    }

    /// <summary>真正显示窗口（冒烟测试不调用它，因此不会弹窗）。</summary>
    internal void ShowWidget()
    {
        Activate();
        Console.WriteLine($"[window] visible handle=0x{WindowNative.GetWindowHandle(this):X}");
    }

    /// <summary>断开材质与事件（关闭前调用，避免控制器在窗口销毁后继续引用它）。</summary>
    internal void Teardown()
    {
        _backdrop?.Dispose();
        _backdrop = null;
    }
}
