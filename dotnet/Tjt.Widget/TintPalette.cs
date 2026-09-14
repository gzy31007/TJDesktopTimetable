using Tjt.Core;

namespace Tjt.Widget;

/// <summary>
/// 色块染色 —— 与 Electron 侧 <c>apps/desktop/src/renderer/shared/TimetableBoard.vue</c>
/// 的 <c>tintStyle()</c> 逐条对齐（那边用 CSS 变量，这边返回可直接赋给 XAML 画刷的色值）。
///
/// 差异只在表达方式：CSS 侧把 <c>--ink</c> 写成 <c>rgba(...)</c>，XAML 的 <c>Brush</c> 也能吃
/// <c>#aarrggbb</c>，所以这里统一产出十六进制，避免渲染层再写一次字符串拼接。
/// </summary>
/// <param name="Tint">色块底色（浅色主题 13%、深色主题 30% 透明度）。</param>
/// <param name="TintHover">悬停底色（20% / 40%）。</param>
/// <param name="Edge">描边（26% / 45%）。</param>
/// <param name="EdgeStrong">虚线描边（非全周课用；50% / 70%）。</param>
/// <param name="Ink">课程名文字色。</param>
/// <param name="InkSoft">教室 / 周次文字色。</param>
/// <param name="Stripe">条纹色（浅色主题用淡白，深色主题用极淡白）。</param>
public sealed record BlockTint(
    string Tint,
    string TintHover,
    string Edge,
    string EdgeStrong,
    string Ink,
    string InkSoft,
    string Stripe);

/// <summary>
/// 主题相关的染色与底色 —— 对应渲染层 <c>tintStyle()</c> 与 <c>.widget-shell</c> / <c>.grid-bg</c>
/// 用到的几个令牌（Fluent 令牌的 C# 侧最小副本；完整令牌表仍在 TS 的 <c>fluent.css</c>）。
/// </summary>
public static class TintPalette
{
    /// <summary>浅色主题的网格分隔线（渲染层 <c>--grid-line: rgba(0,0,0,.055)</c>）。</summary>
    public const string LightGridLine = "#0E000000";

    /// <summary>深色主题的网格分隔线（深色下网格同样走极低对比白线）。</summary>
    public const string DarkGridLine = "#14FFFFFF";

    /// <summary>浅色主题的网格外框。</summary>
    public const string LightStroke = "#12000000";

    /// <summary>深色主题的网格外框。</summary>
    public const string DarkStroke = "#1FFFFFFF";

    /// <summary>周末列的淡染（浅色 <c>rgba(0,0,0,.022)</c>）。</summary>
    public const string LightWeekendCell = "#06000000";

    /// <summary>周末列的淡染（深色）。</summary>
    public const string DarkWeekendCell = "#0AFFFFFF";

    /// <summary>今日列淡染（浅色 <c>rgba(0,103,192,.055)</c>）。</summary>
    public const string LightTodayCell = "#0E0067C0";

    /// <summary>今日列淡染（深色）。</summary>
    public const string DarkTodayCell = "#1A4CC2FF";

    /// <summary>强调色（列头今日 / 当前时间线）。</summary>
    public const string LightAccent = "#0067C0";

    /// <summary>强调色（深色主题用更亮的蓝）。</summary>
    public const string DarkAccent = "#4CC2FF";

    /// <summary>浅色主题的正文与次级文字色（对齐渲染层 <c>--text</c> / <c>--text-2</c>）。</summary>
    public const string LightText = "#1A1A1A";

    /// <summary>浅色主题的次级文字色。</summary>
    public const string LightTextSoft = "#5D5D5D";

    /// <summary>深色主题的正文文字色。</summary>
    public const string DarkText = "#F2F2F2";

    /// <summary>深色主题的次级文字色。</summary>
    public const string DarkTextSoft = "#B8B8B8";

    /// <summary>网格线的实际取值。</summary>
    public static string GridLine(bool dark) => dark ? DarkGridLine : LightGridLine;

    /// <summary>网格外框的实际取值。</summary>
    public static string Stroke(bool dark) => dark ? DarkStroke : LightStroke;

    /// <summary>周末列染色的实际取值。</summary>
    public static string WeekendCell(bool dark) => dark ? DarkWeekendCell : LightWeekendCell;

    /// <summary>今日列染色的实际取值。</summary>
    public static string TodayCell(bool dark) => dark ? DarkTodayCell : LightTodayCell;

    /// <summary>强调色的实际取值。</summary>
    public static string Accent(bool dark) => dark ? DarkAccent : LightAccent;

    /// <summary>正文文字色。</summary>
    public static string Text(bool dark) => dark ? DarkText : LightText;

    /// <summary>次级文字色。</summary>
    public static string TextSoft(bool dark) => dark ? DarkTextSoft : LightTextSoft;

    /// <summary>
    /// 按主题给一个色块算出全套染色（复刻渲染层 <c>tintStyle()</c>）。
    ///
    /// 深色主题先把课程色 <c>lift(color, 0.45)</c> 提亮，再整体加大透明度 —— 深底上直接压原色
    /// 会糊成一块，这是渲染层当初的结论，这里保持一致。
    /// </summary>
    /// <param name="color">课程主色（<c>#rrggbb</c>）。</param>
    /// <param name="dark">是否深色主题。</param>
    public static BlockTint ForBlock(string color, bool dark)
    {
        ArgumentNullException.ThrowIfNull(color);
        var baseColor = dark ? Colors.Lift(color, 0.45) : color;
        return new BlockTint(
            Tint: Rgba(baseColor, dark ? 0.30 : 0.13),
            TintHover: Rgba(baseColor, dark ? 0.40 : 0.20),
            Edge: Rgba(baseColor, dark ? 0.45 : 0.26),
            EdgeStrong: Rgba(baseColor, dark ? 0.70 : 0.50),
            // 深色下必须白字；浅色下用固定的深墨字（比按亮度算更稳，与渲染层一致）
            Ink: dark ? "#FFFFFF" : "#17223A",
            InkSoft: dark ? Rgba("#FFFFFF", 0.76) : Rgba("#0F172A", 0.62),
            // 条纹：浅色主题是"极淡白"叠在染色上，深色主题几乎不可见
            Stripe: dark ? Rgba("#FFFFFF", 0.06) : Rgba("#FFFFFF", 0.10));
    }

    /// <summary>
    /// <c>#rrggbb</c> + 透明度 → <c>#aarrggbb</c>（XAML 画刷用的 ARGB 顺序）。
    ///
    /// 透明度换算与核心库 <see cref="Colors.WithAlpha"/> 同一套 <c>round(a * 255)</c> 语义，
    /// 只是把 alpha 放到前面 —— 两处若不一致，同一门课在 Electron 与 WinUI 上浓淡会不同。
    /// </summary>
    public static string Rgba(string hex, double alpha)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var clamped = Math.Clamp(alpha, 0, 1);
        var value = (int)Math.Floor((clamped * 255) + 0.5);
        var start = hex.StartsWith('#') ? 1 : 0;
        var digits = Math.Min(6, hex.Length - start);
        return string.Concat("#", value.ToString("x2"), hex.AsSpan(start, digits));
    }
}
