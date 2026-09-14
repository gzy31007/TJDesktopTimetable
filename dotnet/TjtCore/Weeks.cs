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

    public static uint OddMask(int totalWeeks = 16) =>
        FromWeeks(Enumerable.Range(1, totalWeeks).Where(w => w % 2 == 1));

    public static uint EvenMask(int totalWeeks = 16) =>
        FromWeeks(Enumerable.Range(1, totalWeeks).Where(w => w % 2 == 0));

    /// <summary>周次过滤器 → 掩码；<c>all</c> 返回 <c>null</c> 表示不过滤。</summary>
    public static uint? ResolveFilter(WeekFilter filter, int totalWeeks = 16) => filter switch
    {
        WeekFilter.Odd => OddMask(totalWeeks),
        WeekFilter.Even => EvenMask(totalWeeks),
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

public enum WeekFilter
{
    All,
    Odd,
    Even,
}
