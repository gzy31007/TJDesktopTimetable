namespace Tjt.Linux;

/// <summary>
/// 命令行选项（Tjt.App/AppStartupOptions.cs 的移植，去掉了 Windows 专属开关）。
/// 未知参数被忽略（不崩在 CLI 上）。
/// </summary>
/// <param name="DesktopLayer">贴桌面层（X11 DESKTOP 类型 + keep-below）；<c>null</c> = 用设置里的值（默认 true）。</param>
/// <param name="Weekend">显示周末两列；<c>null</c> = 用设置里的值。只影响本次运行、不落盘。</param>
/// <param name="Today">覆盖"今日"（<c>--today YYYY-MM-DD</c>）：唯一真源，连带决定今日高亮 / 当前教学周 / 今日节数。</param>
/// <param name="WeekView">覆盖周次视图（<c>--week-view all|current|odd|even</c>）；<c>null</c> = 用设置里的值（默认只看本周）。</param>
/// <param name="LogPath">日志文件路径。</param>
/// <param name="FixturePath">显式指定要导入的课表 JSON；为空时按约定位置探测。</param>
/// <param name="Dark">强制深色主题；<c>null</c> 表示跟随系统。</param>
/// <param name="Width">显式指定的窗口宽度（DIP）；<c>null</c> = 用默认尺寸。</param>
/// <param name="Height">显式指定的窗口高度（DIP）。</param>
/// <param name="ImportPath">启动时先把这份 JSON 走导入管线落盘，再照常启动。</param>
/// <param name="FetchCheckPath">只做一次抓取诊断（<c>--fetch-check</c>），写日志后退出，不落盘。</param>
/// <param name="OpenImportWindow">启动时直接打开导入窗口（<c>--import-window</c>）。</param>
/// <param name="SuppressImportWindow">抑制"首次启动自动弹导入窗口"（<c>--no-import-window</c>）；
/// 脚本 / Xvfb 下要确定首屏时用。</param>
internal sealed record AppStartupOptions(
    bool? DesktopLayer = null,
    bool? Weekend = null,
    string? Today = null,
    Tjt.Core.WeekView? WeekView = null,
    string? LogPath = null,
    string? FixturePath = null,
    bool? Dark = null,
    int? Width = null,
    int? Height = null,
    string? ImportPath = null,
    string? FetchCheckPath = null,
    bool OpenImportWindow = false,
    bool SuppressImportWindow = false)
{
    /// <summary>
    /// 解析命令行。支持的形态：<c>--desktop-layer</c> / <c>--no-desktop-layer</c>、
    /// <c>--weekend</c> / <c>--no-weekend</c>、<c>--today YYYY-MM-DD</c>、
    /// <c>--week-view all|current|odd|even</c>、<c>--log &lt;path&gt;</c>、<c>--dark</c> / <c>--light</c>、
    /// <c>--fixture &lt;path&gt;</c>、<c>--size WxH</c>、<c>--import &lt;path&gt;</c>、
    /// <c>--fetch-check &lt;path&gt;</c>、<c>--import-window</c>、<c>--no-import-window</c>。
    /// </summary>
    public static AppStartupOptions Parse(string[] args)
    {
        bool? desktopLayer = null;
        bool? weekend = null;
        string? today = null;
        Tjt.Core.WeekView? weekView = null;
        string? logPath = null;
        var dark = (bool?)null;
        string? fixture = null;
        int? width = null;
        int? height = null;
        string? importPath = null;
        string? fetchCheck = null;
        var openImport = false;
        var suppressImport = false;

        for (var i = 0; i < args.Length; i += 1)
        {
            var arg = args[i];
            if (Matches(arg, "desktop-layer")) desktopLayer = true;
            else if (Matches(arg, "no-desktop-layer")) desktopLayer = false;
            else if (Matches(arg, "weekend")) weekend = true;
            else if (Matches(arg, "no-weekend")) weekend = false;
            else if (Matches(arg, "log") && i + 1 < args.Length) logPath = args[++i];
            else if (Matches(arg, "today") && i + 1 < args.Length && LooksLikeDate(args[i + 1]))
            {
                today = args[i + 1];
                i += 1;
            }
            else if (Matches(arg, "week-view") && i + 1 < args.Length && ParseWeekView(args[i + 1]) is { } parsedView)
            {
                weekView = parsedView;
                i += 1;
            }
            else if (Matches(arg, "dark")) dark = true;
            else if (Matches(arg, "light")) dark = false;
            else if (Matches(arg, "fixture") && i + 1 < args.Length) fixture = args[++i];
            else if (Matches(arg, "import") && i + 1 < args.Length) importPath = args[++i];
            else if (Matches(arg, "fetch-check") && i + 1 < args.Length) fetchCheck = args[++i];
            else if (Matches(arg, "import-window")) openImport = true;
            else if (Matches(arg, "no-import-window")) suppressImport = true;
            else if (Matches(arg, "size") && i + 1 < args.Length && TryParseSize(args[i + 1], out var w, out var h))
            {
                width = w;
                height = h;
                i += 1;
            }
        }

        return new AppStartupOptions(
            DesktopLayer: desktopLayer,
            Weekend: weekend,
            Today: today,
            WeekView: weekView,
            LogPath: logPath,
            FixturePath: fixture,
            Dark: dark,
            Width: width,
            Height: height,
            ImportPath: importPath,
            FetchCheckPath: fetchCheck,
            OpenImportWindow: openImport,
            SuppressImportWindow: suppressImport);
    }

    /// <summary>周次视图名 → 枚举（认 all / current / odd / even，忽略大小写；不认识返回 null）。</summary>
    private static Tjt.Core.WeekView? ParseWeekView(string text) => text.ToLowerInvariant() switch
    {
        "all" => Tjt.Core.WeekView.All,
        "current" or "this" or "this-week" => Tjt.Core.WeekView.Current,
        "odd" => Tjt.Core.WeekView.Odd,
        "even" => Tjt.Core.WeekView.Even,
        _ => null,
    };

    /// <summary><c>--today</c> 的取值校验：只收 <c>YYYY-MM-DD</c> 形状（不合法当没给）。</summary>
    private static bool LooksLikeDate(string text) =>
        text.Length == 10 && text[4] == '-' && text[7] == '-'
        && int.TryParse(text.AsSpan(0, 4), out _)
        && int.TryParse(text.AsSpan(5, 2), out _)
        && int.TryParse(text.AsSpan(8, 2), out _);

    /// <summary>支持 <c>--flag</c> / <c>-flag</c> / <c>/flag</c> 三种前缀。</summary>
    private static bool Matches(string arg, string name) =>
        string.Equals(arg, $"--{name}", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, $"-{name}", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, $"/{name}", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        var parts = text.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }
}
