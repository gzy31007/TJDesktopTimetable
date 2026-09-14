using Microsoft.UI.Xaml;
using Tjt.App.Data;
using Tjt.App.Win32;

namespace Tjt.App;

/// <summary>
/// 应用入口。
///
/// 这里只做三件事：解析命令行 → 从 core 取课表 → 建窗口；课表本身怎么算、怎么画分别在
/// <c>Tjt.Widget</c> 与 <c>Rendering</c> 里。
///
/// <c>--smoke</c> 是给 CI 用的**自检模式**：建窗口、渲染一遍、校验布局、退出。
/// 它不调用 <c>Activate()</c>，所以不会往 runner 的桌面上弹窗；布局校验走 XAML 的
/// <c>Measure/Arrange</c>，不依赖真实合成，因此在无显卡的 runner 上也能跑。
/// </summary>
public partial class App : Application
{
    /// <summary>自检硬超时（毫秒）：比 CI 侧的 WaitForExit 短，好让日志先落盘。</summary>
    private const int SmokeTimeout = 45_000;

    private readonly List<MainWindow> _windows = [];

    /// <summary>
    /// 构造应用。
    ///
    /// 命令行在这里读：XAML 编译器会为项目生成 <c>Main</c>（<c>App.g.i.cs</c> 里的
    /// <c>Program.Main</c>），它直接 <c>new App()</c> —— 自己再写一个 <c>Main</c> 会撞
    /// 「namespace 已包含 Program 定义」。所以入口交给生成代码，参数从
    /// <see cref="Environment.GetCommandLineArgs"/> 取。
    /// </summary>
    public App()
    {
        Startup = AppStartupOptions.Parse(Environment.GetCommandLineArgs());
        AppLog.UseFile(Startup.LogPath);
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>冒烟自检结果；<c>false</c> 会让进程以非零码退出（CI 视为失败）。</summary>
    public bool SmokePassed { get; private set; } = true;

    /// <summary>本次启动的命令行选项（构造时解析；见构造函数的说明）。</summary>
    internal AppStartupOptions Startup { get; private set; } = new();

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = Startup;
        AppLog.Line($"[start] smoke={options.Smoke} desktopLayer={options.DesktopLayer} noBackdrop={options.NoBackdrop} size={options.Width}x{options.Height}");

        // 自检看门狗：无 GPU 的 runner 上曾卡到 CI 只能看到"90 秒超时"，不知道卡在哪一步。
        // 有它就能看到最后到达的阶段标记；同时给自检一个硬上限，绝不让 CI 悬着。
        if (options.Smoke) StartSmokeWatchdog();

        try
        {
            var loaded = AppHost.Load(options.FixturePath);
            AppLog.Line($"[data] source={loaded.Source} courses={loaded.Timetable.Courses.Count} sessions={SessionCount(loaded)}");

            var window = new MainWindow();
            _windows.Add(window);
            window.Initialize(options, loaded);

            if (options.Smoke)
            {
                SmokePassed = VerifySmoke(window, loaded, options.NoBackdrop);
                return; // finally 里收尾
            }

            window.ShowWidget();
        }
        catch (Exception ex)
        {
            AppLog.Error($"[fatal] {ex}");
            SmokePassed = false;
        }
        finally
        {
            if (options.Smoke)
            {
                // 冒烟模式一律**硬退出**：实测 Application.Exit() 在这种"窗口从未激活"的路径上
                // 会让进程挂住（退出消息投给消息循环，而循环在窗口激活前不推进），
                // CI 上表现为 job 无限期 in_progress。自检是纯短命进程，不需要 WinUI 的清理路径，
                // 用 Environment.Exit 保证一定结束（退出码直接决定 CI 成败）。
                                Environment.Exit(SmokePassed ? 0 : 1);
            }
        }
    }

    /// <summary>自检看门狗：超时即打印最后阶段并硬退出（退出码非零）。</summary>
    private static void StartSmokeWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(SmokeTimeout);
            AppLog.Error($"[smoke] fail: 自检 {SmokeTimeout / 1000} 秒未完成（最后阶段见上方 [stage]/[backdrop] 日志）");
            Console.Error.Flush();
            Environment.Exit(1);
        })
        {
            IsBackground = true,
            Name = "smoke-watchdog",
        };
        watchdog.Start();
    }

    /// <summary>冒烟自检：布局信息必须自洽，且至少画出一块课表。</summary>
    private static bool VerifySmoke(MainWindow window, LoadedTimetable loaded, bool noBackdrop)
    {
        var problems = new List<string>();
        var layout = window.Layout;
        if (layout is null)
        {
            problems.Add("窗口没有产出布局信息");
        }
        else
        {
            if (layout.CanvasWidth <= 0 || layout.CanvasHeight <= 0)
            {
                problems.Add($"画布尺寸非法：{layout.CanvasWidth}x{layout.CanvasHeight}");
            }

            if (layout.Days != 7) problems.Add($"列数应为 7，实际 {layout.Days}");
            if (layout.Slots <= 0) problems.Add($"节次行数应大于 0，实际 {layout.Slots}");

            // 色块数必须等于"可见时段的条数"（周次过滤后仍可见的那些）
            var expected = loaded.Timetable.Courses.Sum(course => course.Sessions.Count);
            if (layout.Blocks != expected)
            {
                problems.Add($"色块数 {layout.Blocks} 与可见时段数 {expected} 不一致");
            }
        }

        if (!noBackdrop && string.Equals(window.BackdropMode, "none", StringComparison.Ordinal))
        {
            // 材质拿不到不算失败（无显卡 / 老系统的 runner 上本来就没有 Mica），但要留痕。
            // `--no-backdrop` 是调用方主动跳过的，不该在这里报"不可用"。
            AppLog.Line("[smoke] warn: 材质不可用，退回无材质窗口");
        }

        if (problems.Count == 0)
        {
            AppLog.Line($"[smoke] ok blocks={layout!.Blocks} canvas={layout.CanvasWidth:0}x{layout.CanvasHeight:0} title={layout.Title}");
            return true;
        }

        foreach (var problem in problems) AppLog.Error($"[smoke] fail: {problem}");
        return false;
    }

    private static int SessionCount(LoadedTimetable loaded) =>
        loaded.Timetable.Courses.Sum(course => course.Sessions.Count);

    /// <summary>关掉所有窗口（冒烟模式下窗口没被激活过，<c>Close()</c> 同样有效）。</summary>
    private void QuitAll()
    {
        foreach (var window in _windows.ToArray())
        {
            try
            {
                window.Teardown();
                window.Close();
            }
            catch (Exception ex)
            {
                AppLog.Error($"[shutdown] 关闭窗口失败：{ex.Message}");
            }
        }

        _windows.Clear();
    }

    /// <summary>
    /// 兜底：任何未处理异常都要把退出码置为非零，否则 CI 会把崩溃当成功。
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error($"[fatal] 未处理异常：{e.Exception}");
        SmokePassed = false;
        Environment.ExitCode = 1;
    }
}
