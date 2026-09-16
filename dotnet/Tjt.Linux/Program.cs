using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Tjt.Core;
using Tjt.Core.Adapters;
using Tjt.Linux.Data;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Tjt.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppStartupOptions.Parse(args);
        AppLog.UseFile(options.LogPath ?? DefaultLogPath());
        AppLog.Line($"[startup] Tjt.Linux {typeof(Program).Assembly.GetName().Version}");

        InstallGlobalExceptionHandlers();

        // 自检类开关在进入 GUI 之前完成（不落用户数据，退出码表成败）
        if (options.FetchCheckPath is { } fetchCheckPath)
        {
            return RunFetchCheck(fetchCheckPath);
        }

        // --import：先走一遍导入管线落盘，再按载入顺序读回来（应用继续启动）
        if (options.ImportPath is { } importPath)
        {
            ImportOnce(importPath);
        }

        App.Startup = options;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // 启动期崩溃（--fixture 指向坏文件、平台初始化失败…）：GUI 无控制台时
            // 文件日志是唯一可靠通道，不兜底就是"一行日志都没有地消失"（Windows 侧同款 [fatal]）。
            AppLog.Error($"[fatal] {ex}");
            return 1;
        }
    }

    /// <summary>顶层异常兜底（对齐 Windows 的 UnhandledException + [fatal]）：先落日志，再按原语义继续。</summary>
    private static void InstallGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error($"[fatal] 未处理异常：{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error($"[fatal] 未观察的任务异常：{e.Exception}");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // 挂件是常驻窗口：一次 UI 回调异常不该让整个挂件消失，记下来并标记已处理。
            AppLog.Error($"[fatal] UI 线程未处理异常：{e.Exception}");
            Environment.ExitCode = 1;
            e.Handled = true;
        };
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(BuildFontOptions())
            .LogToTrace();

    /// <summary>
    /// 默认字体：Avalonia 12 的内置默认是 Inter，多数发行版没装（直接抛 glyphTypeface 异常）。
    /// 以前判的是"候选非空"而不是"系统里装了"—— fc-match 失败就落回多半不存在的
    /// Microsoft YaHei，是一段死代码。现在候选一律过 <c>fc-list</c> 验证；
    /// 装了的 CJK 字体同时配进 <c>FontFallbacks</c>，默认字体缺中文字形时也有地方回退。
    /// </summary>
    private static FontManagerOptions BuildFontOptions()
    {
        var cjk = new[] { "Noto Sans CJK SC", "Noto Sans CJK JP", "Source Han Sans CN", "WenQuanYi Zen Hei", "Microsoft YaHei" }
            .Where(FontInstalled)
            .ToArray();

        // 优先级：fc-match 解析的本机真实默认 → 装了的 CJK 候选 → fontconfig 泛型别名（永远兜底）
        var matched = QueryFcMatch();
        var options = new FontManagerOptions
        {
            DefaultFamilyName = (matched is not null && FontInstalled(matched) ? matched : null)
                ?? cjk.FirstOrDefault()
                ?? "sans-serif",
        };

        if (cjk.Length > 0)
        {
            options.FontFallbacks = cjk
                .Select(family => new FontFallback { FontFamily = new FontFamily(family) })
                .ToList();
        }

        AppLog.Line($"[font] 默认 {options.DefaultFamilyName}，回退 {string.Join(" / ", cjk.DefaultIfEmpty("（无）"))}");
        return options;
    }

    private static string? QueryFcMatch()
    {
        var family = Subprocess.Output("fc-match", "--format=%{family} sans-serif")?.Trim();
        if (string.IsNullOrEmpty(family)) return null;
        AppLog.Line($"[font] fc-match sans-serif → {family}");
        return family.Split(',')[0].Trim();
    }

    /// <summary>判断一个字体族是否真的装了（<c>fc-list</c> 有输出才算，泛型别名除外）。</summary>
    private static bool FontInstalled(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return false;
        return !string.IsNullOrWhiteSpace(Subprocess.Output("fc-list", family));
    }

    /// <summary>
    /// 抓取自检：读文件里的浏览器请求 → 抓一次 → 过一遍导入管线（带 TermId）→
    /// 结论与诊断写日志 → 退出码表成败（不落盘）。退出码代表"能解析成课表"，
    /// 而不只是"网络通 + 响应像课表"。
    /// </summary>
    private static int RunFetchCheck(string path)
    {
        string request;
        try
        {
            request = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[fetch-check] 读不到 {path}：{ex.Message}");
            return 2;
        }

        // 只记长度：内容含 Cookie
        AppLog.Line($"[fetch-check] 开始（请求 {request.Trim().Length} 字符）");
        var fetched = TongjiFetcher.FetchAsync(request).GetAwaiter().GetResult();
        foreach (var probe in fetched.Probes)
        {
            AppLog.Line($"[fetch-check] {probe.Label}：{probe.Value}");
        }
        AppLog.Line($"[fetch-check] 抓取 ok={fetched.Ok}：{fetched.Message}");
        if (!fetched.Ok || fetched.TimetableText is null) return 1;

        try
        {
            var result = ImportPipeline.ImportTimetable(new ImportInput
            {
                Text = fetched.TimetableText,
                AdapterId = TongjiStudentAdapter.AdapterId,
                // 与界面同一条抓取路：学期 id 来自请求 URL（报表格式响应体里没有），不过管线就丢掉了
                TermId = fetched.TermId,
            });
            foreach (var diagnostic in result.Diagnostics)
            {
                AppLog.Line($"[fetch-check] 诊断 {diagnostic.Level} {diagnostic.Code}：{diagnostic.Message}");
            }

            var sessions = result.Courses.Sum(course => course.Sessions.Count);
            AppLog.Line($"[fetch-check] 解析 {result.AdapterId}：{result.Courses.Count} 门 / {sessions} 条，学期 {result.Term.Label}");
            return result.Courses.Count > 0 ? 0 : 1;
        }
        catch (ImportException ex)
        {
            AppLog.Error($"[fetch-check] 解析失败 {ex.Code}：{ex.Message}");
            return 1;
        }
    }

    /// <summary>启动时导入一份 JSON 并落盘（之后 AppHost.Load 会优先读到它）。</summary>
    private static void ImportOnce(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            // 走界面同一条编排（窗口还没建，apply 只有落盘兜底 —— 与 Windows 的 --import 同语义），
            // 这样 [import] adapter=… applied=… 与诊断日志只有一份实现。
            var service = new ImportService(
                apply: (timetable, _) => TimetableStore.Save(timetable),
                reload: () => true,
                describe: () => "（--import 阶段挂件还没起来）");
            var outcome = service.ImportText(text, adapterId: null, files: null);
            if (outcome.Ok)
            {
                foreach (var diagnostic in outcome.Diagnostics)
                {
                    AppLog.Line($"[import] 诊断 {diagnostic.Level} {diagnostic.Code}：{diagnostic.Message}");
                }
            }
            else
            {
                AppLog.Line($"[import] 导入 {path} 失败：{outcome.Message}（继续按载入顺序启动）");
            }
        }
        catch (Exception ex)
        {
            AppLog.Line($"[import] 导入 {path} 失败：{ex.Message}（继续按载入顺序启动）");
        }
    }

    private static string DefaultLogPath() => Path.Combine(Path.GetTempPath(), "tjt-linux.log");
}
