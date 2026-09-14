using System.Globalization;
using System.Text.RegularExpressions;

namespace Tjt.Core;

/// <summary>
/// 课程配色 —— 按课程名稳定取色（TS 侧 <c>packages/core/src/colors.ts</c> 的移植）。
///
/// 与 <c>select_preview.html</c> 基准版的差异（TS 侧有意改进，这里原样保留）：
/// 基准版按出现顺序分配颜色，同一门课在不同导入顺序 / 不同会话下会换色；
/// 这里对课程名做稳定哈希，只要课程名不变，颜色永远一致。色板沿用基准版的 12 色。
///
/// 移植要点：哈希必须与 TS **逐位一致**，否则同一门课在 Electron 侧与 WinUI 侧会取到不同颜色。
/// TS 的哈希跑在 UTF-16 码元上并用 <c>Math.imul</c> 截断到 32 位；C# 的 <see cref="char"/>
/// 同样是 UTF-16 码元，<see cref="uint"/> 乘法在 <c>unchecked</c> 下同样回绕，因此结果完全相同。
/// </summary>
public static partial class Colors
{
    /// <summary>12 色课程色板（TS 侧 <c>COURSE_PALETTE</c>，顺序即哈希取模的下标顺序）。</summary>
    public static readonly string[] CoursePalette =
    [
        "#2563eb",
        "#7c3aed",
        "#be185d",
        "#b45309",
        "#15803d",
        "#0e7490",
        "#a16207",
        "#c2410c",
        "#4338ca",
        "#1d4ed8",
        "#9d174d",
        "#166534",
    ];

    /// <summary>空色板 / 取不到颜色时的兜底色（TS 侧字面量 <c>'#2563eb'</c>）。</summary>
    public const string FallbackColor = "#2563eb";

    /// <summary>
    /// FNV-1a 32 位哈希（TS 侧 <c>hashString</c>）。
    ///
    /// 逐位对齐：先 <c>hash ^= 码元</c>，再 <c>hash = (hash * 0x01000193) mod 2^32</c>。
    /// TS 那边结尾还要 <c>&gt;&gt;&gt; 0</c> 转无符号，C# 的 <see cref="uint"/> 天然就是无符号。
    /// </summary>
    public static uint HashString(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hash = 0x811c9dc5u;
        foreach (var ch in input)
        {
            hash ^= ch;
            hash = unchecked(hash * 0x01000193u);
        }
        return hash;
    }

    /// <summary>
    /// 课程名 → 颜色。同名稳定同色；<paramref name="palette"/> 为 <c>null</c> 时用
    /// <see cref="CoursePalette"/>（TS 侧的默认参数在 C# 里用 null 表达）。
    /// </summary>
    /// <remarks>
    /// TS 的 <c>palette[hash % length] ?? fallback</c> 里的 <c>?? fallback</c> 只在数组有空洞时
    /// 才可能命中；C# 的数组/列表没有空洞，取模结果必然落在范围内，因此该分支不可达，只保留空色板分支。
    /// </remarks>
    public static string ColorForCourse(string name, IReadOnlyList<string>? palette = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var colors = palette ?? CoursePalette;
        if (colors.Count == 0) return FallbackColor;
        return colors[(int)(HashString(name) % (uint)colors.Count)];
    }

    /// <summary>
    /// <c>#rrggbb</c> + 透明度 → <c>#rrggbbaa</c>（基准版用 <c>color + 'cc'</c> 的半透明色块）。
    /// </summary>
    /// <remarks>
    /// 两个容易跑偏的点，都按 TS 原样复刻：
    /// 1. TS 用 <c>Math.round</c>（四舍五入、<c>.5</c> 进位），C# 的 <see cref="Math.Round(double)"/>
    ///    默认是银行家舍入，因此这里手工写成 <c>floor(x + 0.5)</c>；
    /// 2. TS 的 <c>hex.slice(0, 7)</c> 对短串返回整串，这里用 <c>Math.Min(7, hex.Length)</c> 对齐。
    /// <paramref name="alpha"/> 为 <c>NaN</c> 时 TS 会拼出 <c>"...NaN"</c>，C# 表达不了这种字符串，
    /// 按 0 处理（正常调用不可达）。
    /// </remarks>
    public static string WithAlpha(string hex, double alpha)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var clamped = Math.Clamp(alpha, 0, 1);
        var scaled = Math.Floor((clamped * 255) + 0.5);
        var value = double.IsNaN(scaled) ? 0 : (int)scaled;
        var byteText = value.ToString("x2", CultureInfo.InvariantCulture);
        return string.Concat(hex.AsSpan(0, Math.Min(7, hex.Length)), byteText);
    }

    /// <summary>
    /// 返回黑或白，保证在给定背景色上可读（TS 侧 <c>readableTextColor</c>）。
    ///
    /// 用 WCAG 相对亮度：先做 sRGB 反伽马，再按 0.2126/0.7152/0.0722 加权，
    /// 亮度 &gt; 0.45 用深色 <c>#111827</c>，否则用白 <c>#ffffff</c>（阈值与 TS 一致，不是 0.5）。
    /// </summary>
    public static string ReadableTextColor(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var r = ParseChannel(hex, 1) / 255d;
        var g = ParseChannel(hex, 3) / 255d;
        var b = ParseChannel(hex, 5) / 255d;
        var luminance = (0.2126 * Linearize(r)) + (0.7152 * Linearize(g)) + (0.0722 * Linearize(b));
        return luminance > 0.45 ? "#111827" : "#ffffff";
    }

    /// <summary>
    /// 深色主题下把色块提亮一档（<c>amount</c> 0..1），**保证返回 <c>#rrggbb</c> 字符串**。
    /// </summary>
    /// <remarks>
    /// TS 侧的 <c>lift()</c> 不在核心库 colors.ts 里，而在渲染层
    /// <c>apps/desktop/src/renderer/shared/TimetableBoard.vue</c>；这里随配色工具一起移植，
    /// 因为 C# 侧染色同样需要它，而且要守住同一份契约 —— 渲染层注释写明：
    /// **返回 <c>rgb()</c> 会让下游混色算出 NaN，色块直接变透明**。
    ///
    /// 语义差异（均为不可达路径，调用方只传 <c>#rrggbb</c> 或标准 <c>rgb(r, g, b)</c>）：
    /// 解析不出通道时 TS 会拼出含 <c>NaN</c> 的串，这里按 0 处理；<c>amount</c> 越界到负数时
    /// TS 的 <c>toString(16)</c> 会带负号，C# 的 <c>"x2"</c> 会输出补码，两者都属未定义用法。
    /// </remarks>
    public static string Lift(string color, double amount)
    {
        ArgumentNullException.ThrowIfNull(color);
        var (r, g, b) = Channels(color);
        var mix = (double value) => value + ((255 - value) * amount);
        return $"#{LiftChannel(mix(r))}{LiftChannel(mix(g))}{LiftChannel(mix(b))}";
    }

    /// <summary>
    /// TS <c>Number.parseInt(hex.slice(start, start + 2), 16)</c> 的等价实现。
    ///
    /// parseInt 的行为：从 <paramref name="start"/> 起最多取 2 个字符、遇到非法字符就停，
    /// 一个十六进制位都没取到才是 <c>NaN</c>。取不到字符（短串）同样返回 <c>NaN</c>。
    /// 唯一未模拟的是 parseInt 会跳过前导空白与正负号 —— 输入是 <c>#rrggbb</c> 时不可达。
    /// </summary>
    private static double ParseChannel(string hex, int start)
    {
        if (start >= hex.Length) return double.NaN;
        var end = Math.Min(start + 2, hex.Length);
        var value = 0d;
        var digits = 0;
        for (var i = start; i < end; i++)
        {
            var digit = HexDigit(hex[i]);
            if (digit < 0) break;
            value = (value * 16) + digit;
            digits++;
        }
        return digits == 0 ? double.NaN : value;
    }

    private static int HexDigit(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => -1,
    };

    /// <summary>sRGB 反伽马（TS 侧内联的 <c>lin()</c>；<c>** 2.4</c> = <see cref="Math.Pow"/>）。</summary>
    private static double Linearize(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    /// <summary>同时认 <c>#rrggbb</c> 与 <c>rgb(r, g, b)</c>；都认不出时按中性灰 <c>128</c>（与 TS 一致）。</summary>
    private static (double R, double G, double B) Channels(string color)
    {
        var text = color.Trim();
        if (text.StartsWith('#')) return (ParseChannel(text, 1), ParseChannel(text, 3), ParseChannel(text, 5));
        var match = RgbPattern().Match(text);
        if (!match.Success) return (128, 128, 128);
        return (ParseNumber(match.Groups[1].Value), ParseNumber(match.Groups[2].Value), ParseNumber(match.Groups[3].Value));
    }

    private static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    /// <summary>单个通道 → 两位十六进制；<c>Math.round</c> 语义与 <see cref="WithAlpha"/> 一致。</summary>
    private static string LiftChannel(double value)
    {
        if (double.IsNaN(value)) return "0";
        var rounded = (int)Math.Floor(value + 0.5);
        return rounded.ToString("x2", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(\d+)\D+(\d+)\D+(\d+)")]
    private static partial Regex RgbPattern();
}
