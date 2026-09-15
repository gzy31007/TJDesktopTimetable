using Microsoft.UI.Xaml;
using Tjt.App.Data;
using Tjt.App.Win32;
using Tjt.Core;
using Tjt.Core.Adapters;
using WinRT.Interop;

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

    /// <summary>登录窗口自检的硬超时（毫秒）：要等页面脚本跑完并发出课表请求。</summary>
    private const int LoginTimeout = 60_000;

    /// <summary>托盘菜单命令 id（与 <c>ShowTrayMenu</c> 里的项一一对应）。</summary>
    private enum TrayCommand : uint
    {
        Show = 1,
        Settings = 2,
        Refresh = 3,
        ResetPosition = 4,
        ToggleDesktopLayer = 5,
        Import = 6,
        Exit = 7,
        Login = 8,
    }

    private readonly List<MainWindow> _windows = [];
    private readonly List<SettingsWindow> _settingsWindows = [];
    private TrayIcon? _tray;
    private ImportService? _imports;
    private TongjiLoginWindow? _loginWindow;

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
        var sizeText = options is { Width: { } w, Height: { } h } ? $"{w}x{h}" : "default";
        var layerText = options.DesktopLayer is { } value ? value.ToString() : "from-settings";
        AppLog.Line($"[start] smoke={options.Smoke} desktopLayer={layerText} noBackdrop={options.NoBackdrop} size={sizeText}");
        // 原始 argv 也打一行：CLI 解析出问题（参数没到进程 / 前缀不符）时，这是唯一能分辨的线索
        AppLog.Line($"[start] argv=[{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}]");

        // 自检看门狗：无 GPU 的 runner 上曾卡到 CI 只能看到"90 秒超时"，不知道卡在哪一步。
        // 有它就能看到最后到达的阶段标记；同时给自检一个硬上限，绝不让 CI 悬着。
        if (options.Smoke) StartSmokeWatchdog();

        try
        {
            // 0) 抓取诊断（--fetch-check）：只抓一次、把结论写日志、退出。**不落盘**。
            if (options.FetchCheckPath is not null)
            {
                SmokePassed = RunFetchCheck(options.FetchCheckPath);
                return; // finally 里收尾（smoke 之外也会正常退出：这是一条"跑完即走"的路径）
            }

            // 导入编排要在建窗口之前就绪：`--import` 与设置窗口都依赖它。
            // `apply` 里对"窗口还没建"做了兜底（直接落盘）—— 否则 `--import` 会因为
            // 那一刻 _windows 还是空的而静默失败。
            _imports = new ImportService(
                apply: (timetable, source) =>
                {
                    var widget = _windows.FirstOrDefault();
                    return widget is not null
                        ? widget.ApplyImportedTimetable(timetable, source)
                        : TimetableStore.Save(timetable);
                },
                reload: () => _windows.FirstOrDefault()?.ReloadTimetable() ?? false,
                describe: () => _windows.FirstOrDefault()?.DescribeTimetable() ?? "还没有课表");

            // 0b) 导入一条命令行指定的课表（--import）：走的就是界面那条管线
            if (options.ImportPath is not null && !ImportFromFile(options.ImportPath))
            {
                SmokePassed = false;
            }

            // 0c) 登录窗口自检（--login-check <url>）：开一个真窗口、跑完整捕获链路、用完即走。
            //     刻意**不建挂件窗口** —— 验收脚本只关心"页面发出的课表请求能不能被接住并落盘"，
            //     少一个窗口就少一处干扰；落盘走 ImportService 的兜底分支（TimetableStore.Save）。
            if (options.LoginCheckUrl is not null)
            {
                RunLoginCheck(options.LoginCheckUrl);
                return; // 由结束回调 / 看门狗硬退出
            }

            var loaded = AppHost.Load(options.FixturePath);
            AppLog.Line($"[data] source={loaded.Source} origin={AppHost.OriginLabel(loaded.Origin)} courses={loaded.Timetable.Courses.Count} sessions={SessionCount(loaded)}");

            var window = new MainWindow();
            _windows.Add(window);
            window.SetSettingsOpener(ShowSettings);
            window.Initialize(options, loaded);

            if (options.Smoke)
            {
                // 两个检查都要跑（不用 && 短路：设置页构建失败的原因也想知道）
                var layoutOk = VerifySmoke(window, loaded, options.NoBackdrop);
                var pagesOk = VerifySettingsPage(options);
                SmokePassed = layoutOk && pagesOk;
                return; // finally 里收尾
            }

            window.ShowWidget();
            _tray = BuildTray(window);

            // 内置登录窗口的入口（挂件菜单、托盘菜单、设置页按钮三处都落到同一个 ShowTongjiLogin）
            window.SetLoginOpener(() => ShowTongjiLogin(window.CurrentIsDark));
            if (options.Login) ShowTongjiLogin(window.CurrentIsDark);

            // 启动就停在某一页（验证导入页/截图用；托盘与挂件菜单也会用它）
            if (options.SettingsPage is { } page) ShowSettings(window.CurrentSettings, window.CurrentIsDark, page);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[fatal] {ex}");
            SmokePassed = false;
        }
        finally
        {
            // 自检与抓取诊断都是"跑完即走"的短命进程：一律**硬退出**。
            // 实测 Application.Exit() 在"窗口从未激活"的路径上会让进程挂住（退出消息投给消息循环，
            // 而循环在窗口激活前不推进），CI 上表现为 job 无限期 in_progress。
            if (options.Smoke || options.FetchCheckPath is not null)
            {
                Environment.Exit(SmokePassed ? 0 : 1);
            }
        }
    }

    /// <summary>
    /// <c>--import &lt;path&gt;</c>：把文件内容交给导入管线（同时落盘），与界面点「导入并应用」是同一条路。
    /// </summary>
    private bool ImportFromFile(string path)
    {
        try
        {
            var outcome = _imports!.ImportText(File.ReadAllText(path), null, null);
            foreach (var diagnostic in outcome.Diagnostics)
            {
                AppLog.Line($"[import] 诊断 {diagnostic.Level} {diagnostic.Code}：{diagnostic.Message}");
            }

            AppLog.Line($"[import] --import {path} ok={outcome.Ok}：{outcome.Message}");
            return outcome.Ok;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[import] --import 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <c>--fetch-check &lt;path&gt;</c>：用文件里那段浏览器请求抓一次个人课表，把探测结果写日志。
    ///
    /// <para><b>不落盘</b>：这是诊断路径，不该改用户的课表文件。失败即退出码非零，
    /// 用来回答"我这条请求为什么抓不到"。</para>
    /// </summary>
    private static bool RunFetchCheck(string requestPath)
    {
        string requestText;
        try
        {
            requestText = File.ReadAllText(requestPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[fetch-check] 读不到 {requestPath}：{ex.Message}");
            return false;
        }

        // 只记长度：内容含 Cookie
        AppLog.Line($"[fetch-check] 开始（请求 {requestText.Trim().Length} 字符）");
        // 在 UI 线程上阻塞等待是安全的：TongjiFetcher 内部一律 ConfigureAwait(false)，
        // 它的续体不需要回到 UI 线程，因此不会互相等（死锁）。
        var fetched = TongjiFetcher.FetchAsync(requestText).GetAwaiter().GetResult();
        foreach (var probe in fetched.Probes) AppLog.Line($"[fetch-check] {probe.Label}：{probe.Value}");
        AppLog.Line($"[fetch-check] 抓取 ok={fetched.Ok}：{fetched.Message}");
        if (!fetched.Ok || fetched.TimetableText is null) return false;

        try
        {
            var result = ImportPipeline.ImportTimetable(new ImportInput
            {
                Text = fetched.TimetableText,
                AdapterId = TongjiStudentAdapter.AdapterId,
                // 与界面那条抓取路一样：学期 id 来自请求 URL（不然报表格式只能退化成"未知学期"）
                TermId = fetched.TermId,
            });
            foreach (var diagnostic in result.Diagnostics)
            {
                AppLog.Line($"[fetch-check] 诊断 {diagnostic.Level} {diagnostic.Code}：{diagnostic.Message}");
            }

            var sessions = result.Courses.Sum(course => course.Sessions.Count);
            AppLog.Line($"[fetch-check] 解析 {result.AdapterId}：{result.Courses.Count} 门 / {sessions} 条，学期 {result.Term.Label}");
            return result.Courses.Count > 0;
        }
        catch (ImportException ex)
        {
            AppLog.Error($"[fetch-check] 解析失败 {ex.Code}：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <c>--settings-page &lt;n&gt;</c> 在自检模式下的对应检查：把那一页真的建出来并量一遍。
    /// </summary>
    private bool VerifySettingsPage(AppStartupOptions options)
    {
        if (options.SettingsPage is not { } page) return true;
        var widget = _windows.FirstOrDefault();
        if (widget is null) return false;

        try
        {
            var window = new SettingsWindow(widget.CurrentSettings, widget.CurrentIsDark, new SettingsWindow.SettingsHost(
                Apply: next => widget.ApplySettings(next),
                ResetPosition: () => widget.BuildActions().ResetPosition?.Invoke(),
                ReloadTimetable: () => widget.ReloadTimetable(),
                Imports: _imports ?? throw new InvalidOperationException("导入编排尚未初始化"),
                OpenLogin: () => ShowTongjiLogin(widget.CurrentIsDark)), page);
            _settingsWindows.Add(window);
            // 两个都要：VerifyPage 量一遍布局，shown 确认"打开时真的停在这一页"
            var built = window.VerifyPage(page);
            var onRequestedPage = window.ShownPageIndex == page;
            AppLog.Line($"[smoke] settings page={page} built={built} shown={window.ShownPageIndex}");
            return built && onRequestedPage;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[smoke] 设置页 {page} 构建失败：{ex}");
            return false;
        }
    }

    /// <summary>
    /// 打开设置窗口（已开着就激活它，并切到请求的那一页）。
    ///
    /// 参数里带当前设置是为了**回显**，返回新设置是为了把"用户改了什么"带回调用方
    /// （挂件负责落盘与生效）—— 设置窗口因此不需要认识存储层。
    /// </summary>
    /// <param name="current">当前设置。</param>
    /// <param name="dark">当前是否深色主题。</param>
    /// <param name="page">初始页下标（0 常规 / 1 导入 / 2 外观 / 3 关于）。</param>
    private WidgetSettings ShowSettings(WidgetSettings current, bool dark, int page)
    {
        var existing = _settingsWindows.FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            existing.SelectPage(page);
            return current;
        }

        var window = new SettingsWindow(current, dark, new SettingsWindow.SettingsHost(
            Apply: next => _windows.FirstOrDefault()?.ApplySettings(next),
            ResetPosition: () => _windows.FirstOrDefault()?.BuildActions().ResetPosition?.Invoke(),
            ReloadTimetable: () => _windows.FirstOrDefault()?.ReloadTimetable(),
            Imports: _imports ?? throw new InvalidOperationException("导入编排尚未初始化"),
            OpenLogin: () => ShowTongjiLogin(dark)), page);
        _settingsWindows.Add(window);
        window.Closed += (_, _) => _settingsWindows.Remove(window);
        window.Activate();
        // hwnd 进日志：自动化截图脚本靠它定位设置窗口（它是进程里的第二个顶层窗口，
        // MainWindowHandle 永远指向挂件那个）
        AppLog.Line($"[settings] 已打开设置窗口 page={page} shown={window.ShownPageIndex} hwnd=0x{WindowNative.GetWindowHandle(window):X}");
        return current;
    }

    /// <summary>
    /// 打开**内置登录窗口**（已经开着就把它激活）。
    ///
    /// <para>这是"课表从哪来"的推荐路径：在应用自己的 WebView2 里走学校的统一身份认证
    /// （含短信），课表页那条接口的响应被我们在一旁接住 —— 不碰浏览器数据、不猜加密、
    /// 也不接触用户密码。抓到的响应交给 <see cref="ImportService.ApplyCapturedResponse"/>，
    /// 与粘贴请求那条路落在同一个 <c>Apply</c> 上。</para>
    /// </summary>
    /// <param name="dark">当前是否深色主题（只影响这一个窗口）。</param>
    private void ShowTongjiLogin(bool dark)
    {
        if (_loginWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new TongjiLoginWindow(
            RequireImports(),
            dark,
            (ok, message) => AppLog.Line($"[login] 结束 ok={ok}：{message}"));
        _loginWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_loginWindow, window)) _loginWindow = null;
        };
        window.Activate();
        // hwnd 进日志：自动化脚本靠它定位登录窗口（它不是本进程的主窗口）
        AppLog.Line($"[login] 已打开内置登录窗口 hwnd=0x{WindowNative.GetWindowHandle(window):X}");
    }

    /// <summary>
    /// <c>--login-check &lt;url&gt;</c>：把登录窗口导航到给定地址，接住课表即退出（退出码表成败）。
    ///
    /// <para>给验收脚本用的：指向本地合成服务（一个会像 1 系统那样发出课表请求的小页面），
    /// 于是**不需要拿真账号去登录**也能验证"捕获 → 解析 → 落盘"整条链路；
    /// 加看门狗兜底，绝不让脚本悬着（超时即非零退出）。</para>
    /// </summary>
    private void RunLoginCheck(string url)
    {
        AppLog.Line($"[login] 自检模式：起点 {url}");
        StartLoginWatchdog();

        var window = new TongjiLoginWindow(
            RequireImports(),
            dark: false,
            finished: (ok, message) =>
            {
                AppLog.Line($"[login] 自检结果 ok={ok}：{message}");
                Environment.Exit(ok ? 0 : 1);
            },
            startUrl: url);
        _loginWindow = window;
        window.Activate();
    }

    /// <summary>取导入编排（还没建好就是编程错误，直接抛）。</summary>
    private ImportService RequireImports() =>
        _imports ?? throw new InvalidOperationException("导入编排尚未初始化");

    /// <summary>登录自检的看门狗：超时即打印最后阶段并硬退出（退出码非零）。</summary>
    private static void StartLoginWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(LoginTimeout);
            AppLog.Error($"[login] fail: 自检 {LoginTimeout / 1000} 秒内没有捕获到课表（最后阶段见上方 [login] 日志）");
            Environment.Exit(1);
        })
        {
            IsBackground = true,
            Name = "login-watchdog",
        };
        watchdog.Start();
    }

    /// <summary>建托盘图标：左键显示挂件，右键菜单给出常用动作。</summary>
    private TrayIcon BuildTray(MainWindow widget)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        var tray = TrayIcon.Create("同济课表挂件", iconPath, command => OnTrayCommand(widget, command), widget.ShowWidgetAgain);
        tray.SetMenu(1, BuildTrayMenu(widget));
        AppLog.Line($"[tray] 已创建（图标 {iconPath}）");
        return tray;
    }

    /// <summary>托盘菜单（每次弹出前重建，好让"贴桌面层"的勾选状态是最新的）。</summary>
    private List<TrayMenuItem> BuildTrayMenu(MainWindow widget) =>
    [
        new TrayMenuItem((uint)TrayCommand.Show, "显示挂件"),
        new TrayMenuItem((uint)TrayCommand.Settings, "设置…"),
        new TrayMenuItem((uint)TrayCommand.Import, "导入课表…"),
        new TrayMenuItem((uint)TrayCommand.Login, "登录同济获取课表…"),
        new TrayMenuItem(null, string.Empty),
        new TrayMenuItem((uint)TrayCommand.Refresh, "重新载入课表"),
        new TrayMenuItem((uint)TrayCommand.ResetPosition, "恢复默认位置"),
        new TrayMenuItem((uint)TrayCommand.ToggleDesktopLayer, "贴桌面层", widget.LayerEnabled),
        new TrayMenuItem(null, string.Empty),
        new TrayMenuItem((uint)TrayCommand.Exit, "退出"),
    ];

    private void OnTrayCommand(MainWindow widget, uint command)
    {
        switch ((TrayCommand)command)
        {
            case TrayCommand.Show:
                widget.ShowWidgetAgain();
                break;
            case TrayCommand.Settings:
                ShowSettings(widget.CurrentSettings, widget.CurrentIsDark, SettingsWindow.PageGeneral);
                break;
            case TrayCommand.Import:
                ShowSettings(widget.CurrentSettings, widget.CurrentIsDark, SettingsWindow.PageImport);
                break;
            case TrayCommand.Login:
                ShowTongjiLogin(widget.CurrentIsDark);
                break;
            case TrayCommand.Refresh:
                widget.ReloadTimetable();
                break;
            case TrayCommand.ResetPosition:
                widget.BuildActions().ResetPosition?.Invoke();
                break;
            case TrayCommand.ToggleDesktopLayer:
                widget.BuildActions().ToggleDesktopLayer?.Invoke();
                _tray?.SetMenu(1, BuildTrayMenu(widget));
                break;
            case TrayCommand.Exit:
                Exit();
                break;
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

        // 层级层：贴桌面层时 owner 必须真的挂上（这是"Win+D 后仍可见"的唯一机制）
        var layer = window.LayerSnapshot();
        if (layer is not null)
        {
            if (layer.Enabled && layer.Owner != layer.Host)
            {
                problems.Add($"贴桌面层已启用但 owner 未挂上（owner=0x{layer.Owner:X}，host=0x{layer.Host:X}）");
            }

            if (layer.Host == 0)
            {
                Console.WriteLine("[smoke] warn: 没解析到桌面宿主（Explorer 未就绪？）");
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
            AppLog.Line($"[smoke] ok blocks={layout!.Blocks} canvas={layout.CanvasWidth:0}x{layout.CanvasHeight:0} rowH={layout.RowHeight:0.#} scroll={layout.NeedsScroll} title={layout.Title}");
            AppLog.Line(layer is null
                ? "[smoke] layer 未启用"
                : $"[smoke] layer enabled={layer.Enabled} owner=0x{layer.Owner:X} host=0x{layer.Host:X} disposition={window.LastDisposition} backdrop={window.BackdropMode}");
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
        _settingsWindows.Clear();
        _tray?.Dispose();
        _tray = null;
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
