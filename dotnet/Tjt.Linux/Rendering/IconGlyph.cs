namespace Tjt.Linux.Rendering;

/// <summary>
/// 图标字形（Tjt.App/Rendering/IconGlyph.cs 的 Linux 版）。
///
/// <para>Windows 版用 Segoe Fluent Icons（系统自带）；Linux 没有这款字体，
/// 改用 DejaVu / Noto 系字体里都有的 Unicode 几何字符。v1 只保留头部实际用到的三个。</para>
/// </summary>
internal static class IconGlyph
{
    /// <summary>日历（头部应用图标）。</summary>
    public const string Calendar = "\u25A6"; // ▦

    /// <summary>刷新。</summary>
    public const string Refresh = "\u27F3"; // ⟳

    /// <summary>更多（溢出菜单）。</summary>
    public const string More = "\u22EF"; // ⋯
}
