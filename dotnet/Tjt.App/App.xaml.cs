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
        ToggleWeekend = 8,

        /// <summary>「发现新版本 vX」——打开下载页（只有查到新版本时这一项才在菜单里）。</summary>
        OpenUpdate = 9,

        /// <summary>周次视图四项（互斥；勾选状态表达"当前生效的是哪个"）。</summary>
        WeekViewAll = 10,
        WeekViewCurrent = 11,
        WeekViewOdd = 12,
        WeekViewEven = 13,
    }

    private readonly List<MainWindow> _windows = [];
    private readonly List<SettingsWindow> _settingsWindows = [];
    private TrayIcon? _tray;
    private ImportService? _imports;
    private TongjiLoginWindow? _loginWindow;

    /// <summary>本次运行已经弹过系统通知的版本（同一版本只提醒一次，见 <see cref="UpdateCheck.ShouldNotify"/>）。</summary>
    private string? _notifiedUpdateVersion;

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
                SmokePassed = RunFetchCheck(options.FetchCheckPath, options.SjtuHost);
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

            // 0d) 更新检查自检（--update-check）：查一次最新 Release、把结论写日志、退出。
            //     **不建窗口、不落盘**；`--update-api <url>` 可把地址指向本地合成服务
            //     （验收脚本因此不受网络环境影响，见 .tools/verify-update-check.ps1）。
            if (options.UpdateCheck)
            {
                SmokePassed = RunUpdateCheck(options.UpdateApiUrl, options.UpdateProxyUrl);
                return;
            }

            // 0e) 系统通知自检（--update-notify）：查一次最新 Release，**有更新就把那条系统通知
            //     真弹出来**，留几秒让人看见 / 截图，然后退出。同样不建挂件窗口、不落盘；
            //     通知点击在自检模式下只写日志（没有挂件窗口可挂「关于」页）。
            //     验收脚本 .tools/verify-update-notify.ps1 靠它跑"有更新 → 弹 / 无更新 → 不弹"。
            if (options.UpdateNotify)
            {
                SmokePassed = RunUpdateNotifyCheck(options.UpdateApiUrl, options.UpdateProxyUrl);
                return;
            }

            // 0c) 登录窗口自检（--login-check <url>）：开一个真窗口、跑完整捕获链路、用完即走。
            //     刻意**不建挂件窗口** —— 验收脚本只关心"页面发出的课表请求能不能被接住并落盘"，
            //     少一个窗口就少一处干扰；落盘走 ImportService 的兜底分支（TimetableStore.Save）。
            if (options.LoginCheckUrl is not null)
            {
                RunLoginCheck(options.LoginCheckUrl, options.School);
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

            // 开机自启：以 settings.json 为准把注册表 Run 项对齐一次（幂等 —— 换了安装目录、
            // 或用户手工删过那项，都会在这一步自动纠正）。
            // 只在交互模式做：自检 / 诊断路径（--smoke / --fetch-check / --login-check）都已提前 return，
            // 那些会反复启动的验收流程不该动真实系统状态（同"冒烟不写设置"的理由）。
            AutoStart.Sync(window.CurrentSettings.LaunchAtLogin);

            // 新版本检查：后台线程 + 延迟 12 秒（DeskBox 同款思路 —— 别跟启动抢资源），
            // 失败静默、只记日志。走的是"交互模式"这条路径，所以自检 / 诊断都不会触发。
            ScheduleUpdateCheck(window);

            window.ShowWidget();
            _tray = BuildTray(window);

            // 启动时"还没有课表"的处理（走到这里一定是交互模式：--smoke / --fetch-check /
            // --login-check 都已提前 return）：
            //   ① `--login` 显式要求 → 直接开内置登录窗口，学校由 `--login-school` 定（默认同济）；
            //   ② 挂件上只有内置示例课表（= 还没有真实课表）→ 打开设置窗口「导入」页，
            //      **不替用户猜学校** —— 那一页有「登录同济」/「登录交大」两个按钮与说明。
            // ⚠️ 别把它改回"自动弹同济登录窗口"：非同济用户第一次启动会被直接塞进同济登录页
            //    （2026-09-17 用户实测反馈）。`--fixture` 是**显式**指定，也不该被任何窗口盖住。
            if (options.Login)
            {
                var school = options.School ?? LoginSchool.Tongji;
                AppLog.Line($"[login] --login 显式要求：打开内置登录窗口（school={school}）");
                ShowSchoolLogin(school, window.CurrentIsDark);
            }
            else if (options.SettingsPage is null && loaded.Origin is TimetableOrigin.Demo)
            {
                AppLog.Line($"[settings] 还没有真实课表（origin={loaded.Origin}）：打开「导入」页让用户选择学校");
                ShowSettings(window.CurrentSettings, window.CurrentIsDark, SettingsWindow.PageImport);
            }

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
            if (options.Smoke || options.FetchCheckPath is not null || options.UpdateCheck || options.UpdateNotify)
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
    private static bool RunFetchCheck(string requestPath, string? sjtuBaseUrl)
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
        // 在 UI 线程上阻塞等待是安全的：抓取层内部一律 ConfigureAwait(false)，
        // 它的续体不需要回到 UI 线程，因此不会互相等（死锁）。
        // SchoolFetcher 按主机分派：同济原样重发，交大改写成整学期请求（外加一份教务日历）。
        var fetched = SchoolFetcher.FetchAsync(requestText, default, sjtuBaseUrl).GetAwaiter().GetResult();
        foreach (var probe in fetched.Probes) AppLog.Line($"[fetch-check] {probe.Label}：{probe.Value}");
        AppLog.Line($"[fetch-check] 抓取 ok={fetched.Ok}：{fetched.Message}");
        if (!fetched.Ok || fetched.TimetableText is null) return false;

        try
        {
            var result = ImportPipeline.ImportTimetable(new ImportInput
            {
                Text = fetched.TimetableText,
                AdapterId = fetched.AdapterId ?? TongjiStudentAdapter.AdapterId,
                // 与界面那条抓取路一样：学期 id 来自请求 URL（不然报表格式只能退化成"未知学期"）
                TermId = fetched.TermId,
                Files = fetched.Files,
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

    /* ---------------------------------------------------------- 新版本检查（DeskBox 同款） */

    /// <summary>启动后延迟多久查一次：DeskBox 是 45 秒，我们启动开销小，12 秒足够错开启动高峰。</summary>
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 后台查一次新版本：**失败静默**（只记日志），结论交给 <see cref="ApplyUpdateResult"/> 分发。
    /// </summary>
    private void ScheduleUpdateCheck(MainWindow widget)
    {
        if (!widget.CurrentSettings.CheckUpdates)
        {
            AppLog.Line("[update] 设置里关掉了「检查新版本」，本次不查");
            return;
        }

        var apiUrl = Startup.UpdateApiUrl;
        var proxyUrl = Startup.UpdateProxyUrl;
        var skipped = widget.CurrentSettings.SkippedVersion;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(UpdateCheckDelay).ConfigureAwait(false);
                var result = await UpdateChecker.CheckAsync(apiUrl, skipped, proxyUrl).ConfigureAwait(false);
                if (result is null) return;
                widget.DispatcherQueue.TryEnqueue(() => ApplyUpdateResult(widget, result));
            }
            catch (Exception ex)
            {
                AppLog.Line($"[update] 后台检查异常（静默）：{ex.Message}");
            }
        });
    }

    /// <summary>手动查一次（设置页「关于」的按钮）；<paramref name="onDone"/> 在 UI 线程回调，用来重画那一页。</summary>
    private void CheckUpdatesNow(MainWindow widget, Action? onDone = null)
    {
        var apiUrl = Startup.UpdateApiUrl;
        var proxyUrl = Startup.UpdateProxyUrl;
        var skipped = widget.CurrentSettings.SkippedVersion;
        _ = Task.Run(async () =>
        {
            var result = await UpdateChecker.CheckAsync(apiUrl, skipped, proxyUrl).ConfigureAwait(false);
            widget.DispatcherQueue.TryEnqueue(() =>
            {
                // result 为 null = 没查到（网络失败）：保留上一次结论，只把 UI 刷新回去
                if (result is not null) ApplyUpdateResult(widget, result);
                onDone?.Invoke();
            });
        });
    }

    /// <summary>结论分发：挂件（状态）→ 托盘（菜单项 + 提示 + 系统通知）→ 设置窗口（重画关于页）。</summary>
    private void ApplyUpdateResult(MainWindow widget, UpdateCheckResult result)
    {
        widget.ApplyUpdateResult(result);
        NotifyUpdate(result);
        _tray?.SetMenu(1, BuildTrayMenu(widget));
        UpdateTrayTooltip(widget);
        RefreshSettingsWindows();
    }

    /// <summary>
    /// 查到新版本时弹一条系统通知（同一版本每个进程只弹一次）。
    ///
    /// <para>为什么不只靠托盘菜单项：挂件贴桌面层、托盘图标又常被折叠进"隐藏的图标"里，
    /// 「发现新版本」很容易没人看见。通知是第四个落点，点击直达设置「关于」页。
    /// 去重交给 <see cref="UpdateCheck.ShouldNotify"/>（手动点「检查新版本」不会重复弹）。</para>
    /// </summary>
    private void NotifyUpdate(UpdateCheckResult result)
    {
        if (!UpdateCheck.ShouldNotify(result, _notifiedUpdateVersion)) return;

        _notifiedUpdateVersion = UpdateCheck.NotificationVersion(result);
        var title = UpdateCheck.NotificationTitle(result);
        var body = UpdateCheck.NotificationBody(result);

        if (_tray is null)
        {
            AppLog.Line($"[notify-skip] 托盘还没建好，跳过这次通知：{title}");
            return;
        }

        _tray.ShowNotification(title, body);
        AppLog.Line($"[notify] 已弹出系统通知：{title}／{body}");
    }

    /// <summary>让所有开着的设置窗口重画当前页（检查更新是异步的，状态会变）。</summary>
    private void RefreshSettingsWindows()
    {
        foreach (var window in _settingsWindows) window.RefreshCurrentPage();
    }

    /// <summary>「跳过此版本」：记住版本号（落盘）→ 撤掉提示 → 刷新托盘与设置页。</summary>
    private void SkipUpdate(MainWindow widget)
    {
        if (widget.CurrentUpdate?.Latest is not { } latest) return;
        widget.SkipUpdateVersion($"{latest.Major}.{latest.Minor}.{latest.Build}");
        _tray?.SetMenu(1, BuildTrayMenu(widget));
        UpdateTrayTooltip(widget);
        RefreshSettingsWindows();
    }

    /// <summary>
    /// 设置窗口的回调集合（两个构造点共用：自检建页、用户真的打开）。
    ///
    /// <para>新版本那一行统一读同一个 <paramref name="widget"/> 的结论，所以两个入口
    /// （托盘 / 设置页）不会说两套话；`ShowSettings` 的 `current` / `dark` 参数只用于回显。</para>
    /// </summary>
    private SettingsWindow.SettingsHost BuildSettingsHost(MainWindow widget) => new(
        Apply: next => widget.ApplySettings(next),
        ResetPosition: () => widget.BuildActions().ResetPosition?.Invoke(),
        ReloadTimetable: () => widget.ReloadTimetable(),
        Imports: _imports ?? throw new InvalidOperationException("导入编排尚未初始化"),
        OpenLogin: () => ShowSchoolLogin(LoginSchool.Tongji, widget.CurrentIsDark),
        OpenSjtuLogin: () => ShowSchoolLogin(LoginSchool.Sjtu, widget.CurrentIsDark),
        DescribeUpdate: () => DescribeUpdate(widget),
        CheckUpdates: () => CheckUpdatesNow(widget, RefreshSettingsWindows),
        OpenUpdatePage: () => OpenUpdatePage(widget),
        SkipUpdate: () => SkipUpdate(widget),
        HasUpdate: widget.CurrentUpdate is { HasUpdate: true });

    /// <summary>「关于」页那一行的文本（与托盘提示同源：都读 <c>widget.CurrentUpdate</c>）。</summary>
    private static string DescribeUpdate(MainWindow widget)
    {
        if (widget.CurrentUpdate is not { } result) return "还没检查（启动后会自动查一次）";
        return result.Status switch
        {
            UpdateStatus.UpdateAvailable => $"有新版本 v{result.Latest}（当前 {result.Current}）",
            UpdateStatus.Skipped => $"已跳过 v{result.Latest}；更高的版本仍会提示",
            UpdateStatus.UpToDate => $"已是最新（当前 {result.Current}）",
            _ => "检查失败（网络或响应异常，已静默处理）",
        };
    }

    /// <summary>托盘提示文字：有更新时带上版本号（不打开菜单也能看见）。</summary>
    private void UpdateTrayTooltip(MainWindow widget)
    {
        var tooltip = "同济课表挂件";
        if (widget.CurrentUpdate is { HasUpdate: true, Latest: { } latest })
        {
            tooltip += $"（有新版本 v{latest}）";
        }
        _tray?.UpdateTooltip(tooltip);
    }

    /// <summary>打开下载页：优先给本平台的包，没有对应资产时退回 Release 页面。</summary>
    private static void OpenUpdatePage(MainWindow widget)
    {
        var url = widget.CurrentUpdate is { } result ? UpdateCheck.DownloadUrlFor(result) : UpdateCheck.RepoUrl;
        try
        {
            AppLog.Line($"[update] 打开下载页：{url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Line($"[update] 打开下载页失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <c>--update-check</c>：查一次、把结论写日志、用退出码表成败（**不建窗口、不落盘**）。
    /// 给验收脚本用，配合 <c>--update-api</c> 指向本地合成服务。
    /// </summary>
    private static bool RunUpdateCheck(string? apiUrl, string? proxyUrl = null)
    {
        try
        {
            var settings = SettingsStore.Load();
            AppLog.Line(
                $"[update-check] 本机版本 {UpdateChecker.CurrentVersion()}，"
                + $"API {apiUrl ?? UpdateCheck.LatestReleaseApiUrl}，兜底 {proxyUrl ?? UpdateCheck.ProxyPrefix}");
            // 诊断路径阻塞等待（与 --fetch-check 同口径）：跑完即走，没有"卡住 UI"的问题
            var result = UpdateChecker.CheckAsync(apiUrl, settings.SkippedVersion, proxyUrl).GetAwaiter().GetResult();
            if (result is null) return false;
            AppLog.Line(
                $"[update-check] status={result.Status} latest={result.Latest?.ToString() ?? "?"} "
                + $"package={result.Package?.Name ?? "-"} url={UpdateCheck.DownloadUrlFor(result)}");
            return result.Status != UpdateStatus.InvalidResponse;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[update-check] 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <c>--update-notify</c>：查一次，**有更新就把系统通知真弹出来**（留 6 秒），再退出。
    ///
    /// <para>给验收脚本用（配合 <c>--update-api</c> 指向本地合成服务）：日志里有没有
    /// <c>[notify] 已弹出系统通知</c> 就是"该不该弹"的判据；弹出来的样子仍需人眼 / 截图确认
    /// （通知由系统托管，脚本抓不到它的窗口）。**不建挂件窗口、不落盘**。</para>
    /// </summary>
    /// <returns>查到结论（哪怕没有更新）为 <c>true</c>；网络 / 响应失败与通知发送失败为 <c>false</c>。</returns>
    private static bool RunUpdateNotifyCheck(string? apiUrl, string? proxyUrl = null)
    {
        try
        {
            var settings = SettingsStore.Load();
            AppLog.Line(
                $"[notify-check] 本机版本 {UpdateChecker.CurrentVersion()}，"
                + $"API {apiUrl ?? UpdateCheck.LatestReleaseApiUrl}，兜底 {proxyUrl ?? UpdateCheck.ProxyPrefix}");

            var result = UpdateChecker.CheckAsync(apiUrl, settings.SkippedVersion, proxyUrl).GetAwaiter().GetResult();
            if (result is null)
            {
                AppLog.Error("[notify-check] 没查到结论（网络或响应失败）");
                return false;
            }

            AppLog.Line($"[notify-check] status={result.Status} latest={result.Latest?.ToString() ?? "?"}");
            if (!UpdateCheck.ShouldNotify(result, null))
            {
                AppLog.Line("[notify-check] 无需通知：没有比本机更新的正式版本（或被跳过）");
                // 响应不可用（不是 JSON / 缺字段）与"没有更新"都一样：没弹通知就不是成功路径
                return result.Status != UpdateStatus.InvalidResponse;
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            using var tray = TrayIcon.Create(
                "同济课表挂件",
                iconPath,
                _ => { },
                onNotificationClick: () => AppLog.Line("[notify-check] 通知被点击（自检模式：不打开设置窗口）"));

            var title = UpdateCheck.NotificationTitle(result);
            var body = UpdateCheck.NotificationBody(result);
            tray.ShowNotification(title, body);
            AppLog.Line($"[notify] 已弹出系统通知：{title}／{body}");

            // 留几秒让通知真的显示出来（人眼 / 截图），随后 Dispose 会顺带移除托盘图标
            Thread.Sleep(6000);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[notify-check] 失败：{ex.Message}");
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
            var window = new SettingsWindow(widget.CurrentSettings, widget.CurrentIsDark, BuildSettingsHost(widget), page);
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

        var window = new SettingsWindow(current, dark, BuildSettingsHost(
            _windows.FirstOrDefault() ?? throw new InvalidOperationException("挂件窗口还没建，设置窗口不该在这里打开")), page);
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
    /// <para>这是"课表从哪来"的推荐路径：在应用自己的 WebView2 里走学校自己的登录
    /// （同济的统一身份认证含短信 / 交大的 jAccount），课表相关响应被我们接住 —— 不碰浏览器数据、
    /// 不猜加密、也不接触用户密码。抓到的东西交给 <see cref="ImportService.ApplyCapturedResponse"/>，
    /// 与粘贴请求那条路落在同一个 <c>Apply</c> 上。</para>
    ///
    /// <para>两校的差别（都在 <see cref="TongjiLoginWindow"/> 里按 <see cref="LoginSchool"/> 分支）：
    /// 同济只做旁观者（课表接口要前端加密的 <c>studentCode</c>，猜不了）；
    /// 交大拿到登录态后主动取整学期课表 + 教务日历（接口只要明文的 <c>year</c>/<c>semester</c>，
    /// 而且页面默认的按周接口不带周次信息）。</para>
    ///
    /// <para><b>触发点</b>（挂件 <c>⋯</c> 菜单与托盘里**没有**这一项了）：
    /// ① 用户在设置窗口「导入」页点「登录同济并获取课表」/「登录交大并获取课表」
    /// （<c>SettingsHost.OpenLogin</c> / <c>OpenSjtuLogin</c>）；
    /// ② `--login` 显式要求（学校取 `--login-school`，默认同济）。</para>
    ///
    /// <para>⚠️ 启动时"还没有真实课表"**不再**自动开这个窗口（那样会把交大用户塞进同济登录页，
    /// 2026-09-17 用户实测反馈）：那一条改成打开设置窗口「导入」页，让用户自己选学校。</para>
    /// </summary>
    /// <param name="school">服务哪所学校（同济 1 系统 / 交大学在交大）。</param>
    /// <param name="dark">当前是否深色主题（只影响这一个窗口）。</param>
    private void ShowSchoolLogin(LoginSchool school, bool dark)
    {
        if (_loginWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new TongjiLoginWindow(
            RequireImports(),
            dark,
            (ok, message) => AppLog.Line($"[login] 结束 ok={ok}：{message}"),
            school,
            sjtuBaseUrl: Startup.SjtuHost);
        _loginWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_loginWindow, window)) _loginWindow = null;
        };
        window.Activate();
        // hwnd 进日志：自动化脚本靠它定位登录窗口（它不是本进程的主窗口）
        AppLog.Line($"[login] 已打开内置登录窗口 school={school} hwnd=0x{WindowNative.GetWindowHandle(window):X}");
    }

    /// <summary>
    /// <c>--login-check &lt;url&gt;</c>：把登录窗口导航到给定地址，接住课表即退出（退出码表成败）。
    ///
    /// <para>给验收脚本用的：指向本地合成服务（一个会像 1 系统那样发出课表请求的小页面），
    /// 于是**不需要拿真账号去登录**也能验证"捕获 → 解析 → 落盘"整条链路；
    /// 加看门狗兜底，绝不让脚本悬着（超时即非零退出）。</para>
    /// </summary>
    private void RunLoginCheck(string url, LoginSchool? overrideSchool)
    {
        // 合成服务跑在 127.0.0.1 上，主机名看不出学校 → 以 --login-school 为准；
        // 没给就按 URL 主机猜（真站点能猜对，本地服务默认同济）
        var school = overrideSchool
            ?? (SjtuWebCapture.IsSjtuUrl(url) ? LoginSchool.Sjtu : LoginSchool.Tongji);
        AppLog.Line($"[login] 自检模式：起点 {url}（学校 {school}）");
        StartLoginWatchdog();

        var window = new TongjiLoginWindow(
            RequireImports(),
            dark: false,
            finished: (ok, message) =>
            {
                AppLog.Line($"[login] 自检结果 ok={ok}：{message}");
                Environment.Exit(ok ? 0 : 1);
            },
            school,
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

    /// <summary>建托盘图标：左键显示挂件，右键菜单给出常用动作，点系统通知回「关于」页。</summary>
    private TrayIcon BuildTray(MainWindow widget)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        var tray = TrayIcon.Create(
            "同济课表挂件",
            iconPath,
            command => OnTrayCommand(widget, command),
            widget.ShowWidgetAgain,
            onNotificationClick: () => OpenAboutPage(widget));
        tray.SetMenu(1, BuildTrayMenu(widget));
        AppLog.Line($"[tray] 已创建（图标 {iconPath}）");
        return tray;
    }

    /// <summary>
    /// 系统通知被点：打开设置窗口的「关于」页（那一页有版本状态 +「下载」/「跳过此版本」按钮）。
    ///
    /// <para>只走 <see cref="ShowSettings"/> —— 窗口已开着时它自己会 <c>Activate</c> + <c>SelectPage</c>，
    /// 不会叠出第二个设置窗口。</para>
    /// </summary>
    private void OpenAboutPage(MainWindow widget)
    {
        AppLog.Line("[notify] 系统通知被点击：打开设置窗口「关于」页");
        ShowSettings(widget.CurrentSettings, widget.CurrentIsDark, SettingsWindow.PageAbout);
    }

    /// <summary>托盘菜单（每次弹出前重建，好让两个开关的勾选状态是最新的）。</summary>
    private List<TrayMenuItem> BuildTrayMenu(MainWindow widget)
    {
        var items = new List<TrayMenuItem>
        {
            new((uint)TrayCommand.Show, "显示挂件"),
            new((uint)TrayCommand.Settings, "设置…"),
            new((uint)TrayCommand.Import, "导入课表…"),
            new(null, string.Empty),
            new((uint)TrayCommand.Refresh, "重新载入课表"),
            new((uint)TrayCommand.ResetPosition, "恢复默认位置"),
            new((uint)TrayCommand.ToggleDesktopLayer, "贴桌面层", widget.LayerEnabled),
            new((uint)TrayCommand.ToggleWeekend, "显示周末", widget.WeekendEnabled),
            WeekViewMenuItem(widget),
            new(null, string.Empty),
            new((uint)TrayCommand.Exit, "退出"),
        };

        // 有新版本时在最上面插一项（点了直接打开下载页）；没有更新时菜单和以前一模一样
        if (widget.CurrentUpdate is { HasUpdate: true, Latest: { } latest })
        {
            items.Insert(1, new TrayMenuItem((uint)TrayCommand.OpenUpdate, $"发现新版本 v{latest}"));
        }

        return items;
    }

    /// <summary>
    /// 托盘的「周次视图」子菜单：四项**普通勾**（<c>MF_CHECKED</c>），只有当前项打勾。
    ///
    /// <para>刻意**不用** <c>MFT_RADIOCHECK</c>：本实现不设菜单位图，没有位图时它不画圆点，
    /// 观感与"没选中"完全一样（实测过）。</para>
    /// </summary>
    private static TrayMenuItem WeekViewMenuItem(MainWindow widget)
    {
        var children = new List<TrayMenuItem>();
        foreach (var (view, label) in Tjt.Widget.WeekViewLabels.Ordered)
        {
            var id = view switch
            {
                WeekView.Odd => TrayCommand.WeekViewOdd,
                WeekView.Even => TrayCommand.WeekViewEven,
                WeekView.All => TrayCommand.WeekViewAll,
                _ => TrayCommand.WeekViewCurrent,
            };
            children.Add(new TrayMenuItem((uint)id, label, widget.WeekViewEnabled == view));
        }

        return new TrayMenuItem(null, Tjt.Widget.WeekViewLabels.Title, Children: children);
    }

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
            case TrayCommand.ToggleWeekend:
                widget.BuildActions().ToggleShowWeekend?.Invoke();
                _tray?.SetMenu(1, BuildTrayMenu(widget));
                break;
            case TrayCommand.WeekViewAll:
                SetTrayWeekView(widget, WeekView.All);
                break;
            case TrayCommand.WeekViewCurrent:
                SetTrayWeekView(widget, WeekView.Current);
                break;
            case TrayCommand.WeekViewOdd:
                SetTrayWeekView(widget, WeekView.Odd);
                break;
            case TrayCommand.WeekViewEven:
                SetTrayWeekView(widget, WeekView.Even);
                break;
            case TrayCommand.OpenUpdate:
                OpenUpdatePage(widget);
                break;
            case TrayCommand.Exit:
                Exit();
                break;
        }
    }

    /// <summary>托盘点了周次视图的某一项：切视图 + 立刻重建菜单（勾选状态要跟上）。</summary>
    private void SetTrayWeekView(MainWindow widget, WeekView view)
    {
        widget.BuildActions().SetWeekView?.Invoke(view);
        _tray?.SetMenu(1, BuildTrayMenu(widget));
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

            // 列数跟着"显示周末"走：显示 = 7 列，隐藏 = 5 列（`--no-weekend` 时就是这个分支）
            var weekend = window.WeekendEnabled;
            var expectedDays = weekend ? 7 : 5;
            if (layout.Days != expectedDays)
            {
                problems.Add($"列数应为 {expectedDays}（显示周末={weekend}），实际 {layout.Days}");
            }

            if (layout.Slots <= 0) problems.Add($"节次行数应大于 0，实际 {layout.Slots}");

            // 色块数必须等于"可见时段的条数"：周次视图过滤后仍可见、且落在可见列上的那些。
            // 隐藏周末时周末的课**不占列**，布局层直接丢弃；周次视图（默认只看本周）还会丢掉别的周 —— 
            // 两件事都要在这里跟着过滤，否则默认视图一上线冒烟就假 FAIL。
            var mask = Tjt.Core.Weeks.ResolveFilter(window.WeekViewEnabled, window.CurrentWeek, loaded.Timetable.Term.TotalWeeks);
            var expected = loaded.Timetable.Courses
                .SelectMany(course => course.Sessions)
                .Count(session =>
                    session.Weeks != 0
                    && (weekend || !Tjt.Core.TimetableModel.IsWeekend(session.Day))
                    && (mask is null || (session.Weeks & mask.Value) != 0));
            if (layout.Blocks != expected)
            {
                problems.Add(
                    $"色块数 {layout.Blocks} 与可见时段数 {expected} 不一致"
                    + $"（view={MainWindow.WeekViewName(window.WeekViewEnabled)} today={window.TodayOverride ?? "system"} week={window.CurrentWeek?.ToString() ?? "holiday"}）");
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
            // view / today / week 是给验收脚本断言的 ASCII 锚点（周次视图的验收全靠它们；
            // `--today` 必须由调用方显式给，否则这些数字会随日历漂）。
            AppLog.Line(
                $"[smoke] ok blocks={layout!.Blocks} days={layout.Days} canvas={layout.CanvasWidth:0}x{layout.CanvasHeight:0} "
                + $"rowH={layout.RowHeight:0.#} scroll={layout.NeedsScroll} title={layout.Title} "
                + $"view={MainWindow.WeekViewName(window.WeekViewEnabled)} today={window.TodayOverride ?? "system"} "
                + $"week={window.CurrentWeek?.ToString() ?? "holiday"}");
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
