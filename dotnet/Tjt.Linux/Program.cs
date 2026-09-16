using Avalonia;
using Avalonia.Media;
using Tjt.Core;
using Tjt.Core.Adapters;
using Tjt.Linux.Data;

namespace Tjt.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppStartupOptions.Parse(args);
        AppLog.UseFile(options.LogPath ?? DefaultLogPath());
        AppLog.Line($"[startup] Tjt.Linux {typeof(Program).Assembly.GetName().Version}");

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
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = ResolveDefaultFontFamily() })
            .LogToTrace();

    /// <summary>
    /// 默认字体：Avalonia 12 的内置默认是 Inter，但多数 Linux 发行版没装（直接抛
    /// glyphTypeface 异常），所以运行时用 fontconfig 问一遍默认 sans-serif，
    /// 失败时退到常见中文字体，最后退 fontconfig 泛型别名。
    /// </summary>
    private static string ResolveDefaultFontFamily()
    {
        foreach (var candidate in new[] { QueryFcMatch(), "Microsoft YaHei", "Noto Sans CJK SC", "sans-serif" })
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }

        return "sans-serif";
    }

    private static string? QueryFcMatch()
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("fc-match", "--format=%{family} sans-serif")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null) return null;
            var family = process.StandardOutput.ReadToEnd().Trim();
            AppLog.Line($"[font] fc-match sans-serif → {family}");
            return family.Split(',')[0].Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>抓取自检：读文件里的浏览器请求 → 抓一次 → 探测结果写日志 → 退出码表成败（不落盘）。</summary>
    private static int RunFetchCheck(string path)
    {
        try
        {
            var request = File.ReadAllText(path);
            var outcome = TongjiFetcher.FetchAsync(request).GetAwaiter().GetResult();
            AppLog.Line($"[fetch-check] ok={outcome.Ok} {outcome.Message}");
            foreach (var probe in outcome.Probes)
            {
                AppLog.Line($"[fetch-check] {probe.Label}: {probe.Value}");
            }

            return outcome.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[fetch-check] 读取 {path} 失败：{ex.Message}");
            return 2;
        }
    }

    /// <summary>启动时导入一份 JSON 并落盘（之后 AppHost.Load 会优先读到它）。</summary>
    private static void ImportOnce(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var result = ImportPipeline.ImportTimetable(new ImportInput { Text = text });
            var timetable = ImportPipeline.MaterializeTimetable(result);
            TimetableStore.Save(timetable);
            AppLog.Line($"[import] 已导入 {timetable.Courses.Count} 门 → {TimetableStore.FilePath}");
        }
        catch (Exception ex)
        {
            AppLog.Line($"[import] 导入 {path} 失败：{ex.Message}（继续按载入顺序启动）");
        }
    }

    private static string DefaultLogPath() => Path.Combine(Path.GetTempPath(), "tjt-linux.log");
}
