using System.Text.Json.Serialization;

namespace Tjt.Core;

/// <summary>
/// 周次位掩码工具（TS 侧 <c>packages/core/src/weeks.ts</c> 的移植）。
///
/// 约定：bit0 = 第 1 周（与同济教务 <c>weekState</c> 一致，实测 65535 = 第 1-16 周）。
/// 用 <see cref="uint"/> 而不是 int：TS 那边要额外 <c>&gt;&gt;&gt; 0</c> 才是无符号，
/// C# 用无符号类型可以直接表达第 32 周（0x8000_0000）。
/// </summary>
public static class Weeks
{
    /// <summary>第 1..32 周的合法掩码上限（国内高校一学期 16–23 周，32 足够）。</summary>
    public const int MaxWeeks = 32;

    /// <summary>由周次列表构造掩码。非法周次（越界 / 非整数用 int 表达）被忽略。</summary>
    public static uint FromWeeks(IEnumerable<int> weeks)
    {
        uint mask = 0;
        foreach (var w in weeks)
        {
            if (w < 1 || w > MaxWeeks) continue;
            mask |= 1u << (w - 1);
        }
        return mask;
    }

    /// <summary>掩码里出现过的最大周次（空掩码为 0）。</summary>
    public static int Highest(uint mask)
    {
        for (var w = MaxWeeks; w >= 1; w--)
        {
            if ((mask & (1u << (w - 1))) != 0) return w;
        }
        return 0;
    }

    /// <summary>
    /// 掩码 → 周次列表。展示范围取 <c>max(totalWeeks, 掩码最高位)</c>，
    /// 这样掩码里出现第 17 周时不会因为校历写 16 周而丢数据。
    /// </summary>
    public static int[] ToWeeks(uint mask, int totalWeeks = 16)
    {
        var limit = Math.Max(totalWeeks, Highest(mask));
        var list = new List<int>();
        for (var w = 1; w <= limit; w++)
        {
            if ((mask & (1u << (w - 1))) != 0) list.Add(w);
        }
        return [.. list];
    }

    /// <summary>
    /// 掩码 → 人类可读标签，例如 <c>1-16</c>、<c>2, 4, 6</c>、<c>11-14</c>。
    ///
    /// 输出格式必须与既有 Python 实现 <c>fmt_weeks</c> 完全一致（区间用 <c>-</c>、分隔用 <c>, </c>），
    /// 这是与既有数据做黄金对比的前提。
    /// </summary>
    public static string FormatLabel(uint mask, int totalWeeks = 16)
    {
        var weeks = ToWeeks(mask, totalWeeks);
        if (weeks.Length == 0) return "-";

        var parts = new List<string>();
        var start = weeks[0];
        var prev = weeks[0];
        for (var i = 1; i < weeks.Length; i++)
        {
            var w = weeks[i];
            if (w == prev + 1)
            {
                prev = w;
                continue;
            }
            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = w;
            prev = w;
        }
        parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
        return string.Join(", ", parts);
    }

    /// <summary>学期全周掩码，例如 16 周 → 0xFFFF。</summary>
    public static uint FullMask(int totalWeeks = 16) => FromWeeks(Enumerable.Range(1, totalWeeks));

    /// <summary>
    /// 单个教学周的掩码（<c>第 3 周 → 0b100</c>）；越界周次（&lt;1 或 &gt;<see cref="MaxWeeks"/>）返回 <c>0</c>。
    ///
    /// <para>「本周」视图用它把当前周折成一条"只命中这一周"的过滤条件，
    /// 免得调用方各自写 <c>1u &lt;&lt; (week - 1)</c> 而漏掉越界判断。</para>
    /// </summary>
    public static uint WeekMask(int week) =>
        week < 1 || week > MaxWeeks ? 0u : 1u << (week - 1);

    public static uint OddMask(int totalWeeks = 16) =>
        FromWeeks(Enumerable.Range(1, totalWeeks).Where(w => w % 2 == 1));

    public static uint EvenMask(int totalWeeks = 16) =>
        FromWeeks(Enumerable.Range(1, totalWeeks).Where(w => w % 2 == 0));

    /// <summary>
    /// 周次视图 → 掩码；<c>null</c> 表示**不过滤**（全部周次）。
    ///
    /// <para><see cref="WeekView.Current"/> 依赖 <paramref name="currentWeek"/>：给了当前教学周就是"只命中这一周"，
    /// 为 <c>null</c>（开学前 / 学期结束后 / 开学日未知）时返回 <c>null</c> —— 静默退回显示全部周次，
    /// 绝不让挂件变空（见 <c>docs/adr/0001-默认周次视图为本周.md</c>）。越界周次同样按"不过滤"处理。</para>
    /// </summary>
    public static uint? ResolveFilter(WeekView view, int? currentWeek, int totalWeeks = 16) => view switch
    {
        WeekView.Current => currentWeek is { } week && WeekMask(week) != 0 ? WeekMask(week) : null,
        WeekView.Odd => OddMask(totalWeeks),
        WeekView.Even => EvenMask(totalWeeks),
        _ => null,
    };

    /// <summary>两个掩码是否有交集（判断同一格的两门课在周次上是否真的撞车）。</summary>
    public static bool Overlap(uint a, uint b) => (a & b) != 0;

    public static int Count(uint mask)
    {
        var n = 0;
        for (var w = 1; w <= MaxWeeks; w++)
        {
            if ((mask & (1u << (w - 1))) != 0) n++;
        }
        return n;
    }

    /// <summary>掩码是否覆盖整个学期（例如 16 周课表的 0xFFFF）——「全周上课」判定。</summary>
    public static bool IsAll(uint mask, int totalWeeks = 16) =>
        mask != 0 && mask == FullMask(totalWeeks);
}

/// <summary>
/// 周次视图（用户可见概念：全部 / 本周 / 单周 / 双周），四态**互斥**。
///
/// <para>刻意不做正交组合（"本周 ∩ 单周"能推出空课表这种矛盾态）；冲突判定、今日高亮都不受它影响
/// —— 视图只决定画哪些（见 <c>CONTEXT.md</c> 与 <c>docs/adr/0001-默认周次视图为本周.md</c>）。</para>
///
/// <para>术语：旧文档里的「周次过滤」只指单双周，是另一个维度；用户可见概念统一叫「周次视图」，
/// 枚举因此从 <c>WeekFilter</c> 改名为 <c>WeekView</c>。</para>
///
/// <para><b>落盘用字符串</b>（<see cref="JsonStringEnumConverter"/>，见
/// <c>docs/adr/0002-周次视图用字符串枚举落盘.md</c>）：设置文件可读可手改，增删取值也不依赖枚举顺序。
/// 转换器只挂在这个类型上，<b>不</b>动全局 <c>JsonSerializerOptions</c> —— 那会把 <c>ThemeMode</c> /
/// <c>MaterialMode</c> 已发布的数字格式一起改掉。</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WeekView
{
    /// <summary>全部周次（不过滤）。</summary>
    All,

    /// <summary>只看当前教学周。</summary>
    Current,

    /// <summary>只看单周。</summary>
    Odd,

    /// <summary>只看双周。</summary>
    Even,
}
