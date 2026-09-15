namespace Tjt.App.Rendering;

/// <summary>
/// Segoe Fluent Icons 字形常量（Windows 10/11 自带字体，不需要额外资源）。
///
/// <para>用字形而不是位图：一处色值即可跟随深浅主题，任意 DPI 都清晰；
/// 图标本身的语义写在调用点（<c>BuildIconButton(IconGlyph.Refresh, ...)</c>）。</para>
/// </summary>
internal static class IconGlyph
{
    /// <summary>刷新（<c>&#xE72C;</c>）。</summary>
    public const string Refresh = "\uE72C";

    /// <summary>导入（<c>&#xE896;</c>，Download）—— 导入课表用。</summary>
    public const string Import = "\uE896";

    /// <summary>文件夹（<c>&#xE8B7;</c>）—— 当前课表 / 数据目录用。</summary>
    public const string Folder = "\uE8B7";

    /// <summary>文档（<c>&#xE8A5;</c>）—— 本地 JSON 导入用。</summary>
    public const string Document = "\uE8A5";

    /// <summary>地球（<c>&#xE774;</c>）—— 从 1 系统抓取用。</summary>
    public const string Globe = "\uE774";

    /// <summary>更多（<c>&#xE712;</c>）—— 对应截图里那个 <c>⋯</c>。</summary>
    public const string More = "\uE712";

    /// <summary>设置（<c>&#xE713;</c>）。</summary>
    public const string Settings = "\uE713";

    /// <summary>显示器（<c>&#xE7F4;</c>）。</summary>
    public const string Monitor = "\uE7F4";

    /// <summary>日历（<c>&#xE787;</c>）—— 头部应用图标用，与托盘图标同源。</summary>
    public const string Calendar = "\uE787";

    /// <summary>固定（<c>&#xE718;</c>）。</summary>
    public const string Pin = "\uE718";

    /// <summary>显示周末（<c>&#xE8D2;</c>）—— 设置页与 <c>⋯</c> 菜单里那项用同一个字形。</summary>
    public const string Weekend = "\uE8D2";

    /// <summary>回到原位（<c>&#xE73F;</c>）。</summary>
    public const string Recenter = "\uE73F";

    /// <summary>隐藏（<c>&#xE738;</c>，Hide）。</summary>
    public const string Hide = "\uE738";

    /// <summary>关闭（<c>&#xE8BB;</c>）。</summary>
    public const string Close = "\uE8BB";
}
