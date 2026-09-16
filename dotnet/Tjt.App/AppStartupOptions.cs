namespace Tjt.App;

/// <summary>命令行选项（GUI 应用没有 TTY，参数是唯一的开关面）。</summary>
/// <param name="Smoke">自带窗口跑一遍渲染自检后退出（CI 用）。</param>
/// <param name="DesktopLayer">贴桌面层；<c>null</c> = 用设置里的值（默认 true）。</param>
/// <param name="Weekend">
/// 显示周末两列；<c>null</c> = 用设置里的值（默认 true）。
/// 只影响本次运行、不落盘，供验收脚本跑"隐藏周末"那种布局。
/// </param>
/// <param name="NowMinutes">
/// 覆盖"当前时刻"（<c>--now HH:mm</c>，如 <c>--now 12:30</c>），只影响**当前时间线**的位置；
/// <c>null</c> = 用系统时钟。存在的理由：时间线的几何取决于真实时间，而课间 / 午休那些分支
/// 不可能等到点上再去截图验证（见 <c>BoardVisualTests</c> 的同名用例）。
/// </param>
/// <param name="Material">
/// 覆盖窗口材质（<c>--material mica|mica-alt|acrylic|acrylic-thin|solid</c>），只影响本次运行、不落盘。
/// 为的是能用截图逐个材质对比（设置页也能切，但脚本点不到下拉）。
/// </param>
/// <param name="NoBackdrop">跳过系统材质（无 GPU 的 CI runner 上 D3D 合成会挂住）。</param>
/// <param name="LogPath">日志文件路径（WinExe 不附加控制台，CI 只能靠文件拿输出）。</param>
/// <param name="FixturePath">显式指定要导入的课表 JSON；为空时按约定位置探测。</param>
/// <param name="Dark">强制深色主题；<c>null</c> 表示跟随系统。</param>
/// <param name="Width">显式指定的窗口宽度（DIP）；<c>null</c> = 用系统默认尺寸。</param>
/// <param name="Height">显式指定的窗口高度（DIP）；<c>null</c> = 用系统默认尺寸。</param>
/// <param name="ImportPath">
/// 启动时先走一遍**导入管线**把这份 JSON 导入并落盘，再按载入顺序读回来
/// （<c>--import</c>）。用来验证"导入 → 落盘 → 下次启动读回"整条链路，不用手点界面。
/// </param>
/// <param name="FetchCheckPath">
/// 只做一次抓取诊断：读文件里的浏览器请求 → 抓一次 → 把探测结果与解析结果写日志 → 退出
/// （<c>--fetch-check</c>）。**不落盘**（诊断不该改用户数据）；失败时进程退出码非零。
/// </param>
/// <param name="SettingsPage">启动时直接打开设置窗口的第 N 页（0 常规 / 1 导入 / 2 外观 / 3 关于）。</param>
/// <param name="Login">
/// 启动后直接打开**内置登录窗口**（<c>--login</c>）：在应用自己的 WebView2 里登录 1 系统，
/// 课表页那条接口的响应会被自动接住并导入。
/// </param>
/// <param name="LoginCheckUrl">
/// 登录窗口的**自检**（<c>--login-check &lt;url&gt;</c>）：打开登录窗口并导航到给定地址，
/// 等它捕获到课表 → 写日志 → 退出（退出码表成败）。给验收脚本指向本地合成服务用，
/// 这样不必拿真账号去登录（详见 <c>.tools/verify-login.ps1</c>）。
/// </param>
/// <param name="UpdateCheck">
/// 启动即查一次新版本、把结论写日志后退出（<c>--update-check</c>）。**不建挂件窗口、不落盘**，
/// 退出码表"有没有查到"（查到=0，网络/解析失败=1）—— 给验收脚本用。
/// </param>
/// <param name="UpdateApiUrl">
/// 覆盖更新检查的 API 地址（<c>--update-api &lt;url&gt;</c>）：验收脚本指向本地合成服务，
/// 不必真的去打 GitHub（也不受网络环境影响）。<c>null</c> = 真 GitHub。
/// </param>
internal sealed record AppStartupOptions(
    bool Smoke = false,
    bool? DesktopLayer = null,
    bool? Weekend = null,
    int? NowMinutes = null,
    Data.MaterialMode? Material = null,
    bool NoBackdrop = false,
    string? LogPath = null,
    string? FixturePath = null,
    bool? Dark = null,
    int? Width = null,
    int? Height = null,
    string? ImportPath = null,
    string? FetchCheckPath = null,
    int? SettingsPage = null,
    bool Login = false,
    string? LoginCheckUrl = null,
    bool UpdateCheck = false,
    string? UpdateApiUrl = null)
{
    /// <summary>
    /// 解析命令行。
    ///
    /// 支持的形态：<c>--smoke</c>、<c>--desktop-layer</c> / <c>--no-desktop-layer</c>、<c>--weekend</c> / <c>--no-weekend</c>、<c>--no-backdrop</c>、<c>--log &lt;path&gt;</c>、<c>--dark</c> / <c>--light</c>、
    /// <c>--fixture &lt;path&gt;</c>、<c>--size WxH</c>（不传就用窗口系统给的默认尺寸，传了就精确设成它，
    /// 便于验证自适应）、<c>--now HH:mm</c>（覆盖"当前时刻"，只为验证时间线，见 <c>NowMinutes</c>）、
    /// <c>--import &lt;path&gt;</c>、<c>--fetch-check &lt;path&gt;</c>、<c>--settings-page &lt;n&gt;</c>、
    /// <c>--login</c>、<c>--login-check &lt;url&gt;</c>、<c>--update-check</c>、<c>--update-api &lt;url&gt;</c>。
    /// 未知参数被忽略（不崩在 CLI 上）。
    /// </summary>
    public static AppStartupOptions Parse(string[] args)
    {
        var smoke = false;
        bool? desktopLayer = null;
        bool? weekend = null;
        int? nowMinutes = null;
        Data.MaterialMode? material = null;
        var noBackdrop = false;
        string? logPath = null;
        var dark = (bool?)null;
        string? fixture = null;
        int? width = null;
        int? height = null;
        string? importPath = null;
        string? fetchCheck = null;
        int? settingsPage = null;
        var login = false;
        string? loginCheck = null;
        var updateCheck = false;
        string? updateApi = null;

        for (var i = 0; i < args.Length; i += 1)
        {
            var arg = args[i];
            if (Matches(arg, "smoke")) smoke = true;
            else if (Matches(arg, "desktop-layer")) desktopLayer = true;
            else if (Matches(arg, "no-desktop-layer")) desktopLayer = false;
            else if (Matches(arg, "weekend")) weekend = true;
            else if (Matches(arg, "no-weekend")) weekend = false;
            else if (Matches(arg, "no-backdrop")) noBackdrop = true;
            else if (Matches(arg, "log") && i + 1 < args.Length) logPath = args[++i];
            else if (Matches(arg, "dark")) dark = true;
            else if (Matches(arg, "light")) dark = false;
            else if (Matches(arg, "fixture") && i + 1 < args.Length) fixture = args[++i];
            else if (Matches(arg, "import") && i + 1 < args.Length) importPath = args[++i];
            else if (Matches(arg, "fetch-check") && i + 1 < args.Length) fetchCheck = args[++i];
            else if (Matches(arg, "login")) login = true;
            else if (Matches(arg, "login-check") && i + 1 < args.Length) loginCheck = args[++i];
            else if (Matches(arg, "update-check")) updateCheck = true;
            else if (Matches(arg, "update-api") && i + 1 < args.Length) updateApi = args[++i];
            else if (Matches(arg, "settings-page") && i + 1 < args.Length && int.TryParse(args[i + 1], out var page))
            {
                settingsPage = page;
                i += 1;
            }
            else if (Matches(arg, "material") && i + 1 < args.Length && ParseMaterial(args[i + 1]) is { } parsedMaterial)
            {
                material = parsedMaterial;
                i += 1;
            }
            else if (Matches(arg, "now") && i + 1 < args.Length && Tjt.Core.Time.ToMinutes(args[i + 1]) is { } minutes)
            {
                nowMinutes = minutes;
                i += 1;
            }
            else if (Matches(arg, "size") && i + 1 < args.Length && TryParseSize(args[i + 1], out var w, out var h))
            {
                width = w;
                height = h;
                i += 1;
            }
        }

        return new AppStartupOptions(
            Smoke: smoke,
            DesktopLayer: desktopLayer,
            Weekend: weekend,
            NowMinutes: nowMinutes,
            Material: material,
            NoBackdrop: noBackdrop,
            LogPath: logPath,
            FixturePath: fixture,
            Dark: dark,
            Width: width,
            Height: height,
            ImportPath: importPath,
            FetchCheckPath: fetchCheck,
            SettingsPage: settingsPage,
            Login: login,
            LoginCheckUrl: loginCheck,
            UpdateCheck: updateCheck,
            UpdateApiUrl: updateApi);
    }

    /// <summary>支持 <c>--flag</c> / <c>-flag</c> / <c>/flag</c> 三种前缀。</summary>
    private static bool Matches(string arg, string name) =>
        string.Equals(arg, $"--{name}", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, $"-{name}", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, $"/{name}", StringComparison.OrdinalIgnoreCase);

    /// <summary>材质名 → 枚举（认 <c>acrylic-thin</c> / <c>acrylicthin</c> / <c>thin</c> 三种写法；不认识返回 null）。</summary>
    private static Data.MaterialMode? ParseMaterial(string text) => text.ToLowerInvariant() switch
    {
        "solid" => Data.MaterialMode.Solid,
        "mica" => Data.MaterialMode.Mica,
        "mica-alt" or "micaalt" or "alt" => Data.MaterialMode.MicaAlt,
        "acrylic" => Data.MaterialMode.Acrylic,
        "acrylic-thin" or "acrylicthin" or "thin" => Data.MaterialMode.AcrylicThin,
        _ => null,
    };

    private static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        var parts = text.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }
}
