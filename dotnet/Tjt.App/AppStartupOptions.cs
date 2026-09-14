namespace Tjt.App;

/// <summary>命令行选项（GUI 应用没有 TTY，参数是唯一的开关面）。</summary>
/// <param name="Smoke">自带窗口跑一遍渲染自检后退出（CI 用）。</param>
/// <param name="DesktopLayer">把窗口 owner 挂到桌面图标视图（贴桌面层）。</param>
/// <param name="NoBackdrop">跳过系统材质（无 GPU 的 CI runner 上 D3D 合成会挂住）。</param>
/// <param name="FixturePath">显式指定要导入的课表 JSON；为空时按约定位置探测。</param>
/// <param name="Dark">强制深色主题；<c>null</c> 表示跟随系统。</param>
/// <param name="Width">窗口宽度（DIP），默认 980。</param>
/// <param name="Height">窗口高度（DIP），默认 640。</param>
internal sealed record AppStartupOptions(
    bool Smoke = false,
    bool DesktopLayer = false,
    bool NoBackdrop = false,
    string? FixturePath = null,
    bool? Dark = null,
    int Width = 980,
    int Height = 640)
{
    /// <summary>
    /// 解析命令行。
    ///
    /// 支持的形态：<c>--smoke</c>、<c>--desktop-layer</c>、<c>--no-backdrop</c>、<c>--dark</c> / <c>--light</c>、
    /// <c>--fixture &lt;path&gt;</c>、<c>--size WxH</c>。未知参数被忽略（不崩在 CLI 上）。
    /// </summary>
    public static AppStartupOptions Parse(string[] args)
    {
        var smoke = false;
        var desktopLayer = false;
        var noBackdrop = false;
        var dark = (bool?)null;
        string? fixture = null;
        var width = 980;
        var height = 640;

        for (var i = 0; i < args.Length; i += 1)
        {
            var arg = args[i];
            if (Matches(arg, "smoke")) smoke = true;
            else if (Matches(arg, "desktop-layer")) desktopLayer = true;
            else if (Matches(arg, "no-backdrop")) noBackdrop = true;
            else if (Matches(arg, "dark")) dark = true;
            else if (Matches(arg, "light")) dark = false;
            else if (Matches(arg, "fixture") && i + 1 < args.Length) fixture = args[++i];
            else if (Matches(arg, "size") && i + 1 < args.Length && TryParseSize(args[i + 1], out var w, out var h))
            {
                width = w;
                height = h;
                i += 1;
            }
        }

        return new AppStartupOptions(smoke, desktopLayer, noBackdrop, fixture, dark, width, height);
    }

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
