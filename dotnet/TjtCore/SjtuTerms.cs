using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tjt.Core;

/// <summary>
/// 上海交通大学（「学在交大」/ studyatsjtu，<c>j.sjtu.edu.cn</c>）的学期与节次事实。
///
/// <para>这里的每一条都来自可核对的来源，不是猜的：</para>
/// <list type="bullet">
///   <item>节次时间：教务处《上海交通大学学生上课时间表》（<c>https://jwc.sjtu.edu.cn/info/1041/1110.htm</c>）。
///   共 13 节，第 11-13 节是"晚上 3 节连上 18:00-20:20"，第 13 节因此没有独立的官方起止（按前两节
///   的 10 分钟间隔顺推 <c>19:50-20:20</c>）。</item>
///   <item>日历：<c>GET /app/stu/school/semester/calendar</c> 返回**逐日**课表日历
///   （<c>{week, weekDay, day}</c>，week 从 0 到 21），第 1 周周一才是开学日；
///   <c>startDay</c> 是第 0 周周一（报到周），<b>不能</b>直接当 <see cref="Term.StartDate"/>。</item>
///   <item>周次：课程条目的 <c>time</c> 文本（<c>"1-16周"</c> / <c>"5周,9周,13-15周"</c>），
///   再由 <c>suffix</c> 里的 <c>"单周"</c> / <c>"双周"</c> 过滤。</item>
/// </list>
///
/// <para>与 <see cref="TongjiTerms"/> 同层：这是"学校事实表"，不含任何解析流程；适配器只消费它。</para>
/// </summary>
public static class SjtuTerms
{
    /// <summary>交大应用主机（课表接口都在它下面）。</summary>
    public const string SjtuHost = "j.sjtu.edu.cn";

    /// <summary>站点根（内置登录窗口的初始导航目标）。</summary>
    public const string SjtuOrigin = "https://" + SjtuHost;

    /// <summary>节次总数（1..13）。前端把后端可能出现的第 14 节折算成第 13 节，这里同口径。</summary>
    public const int SlotCount = 13;

    /// <summary>交大作息时间表（13 节）。</summary>
    public static readonly Slot[] Slots =
    [
        new(1, "08:00", "08:45"),
        new(2, "08:55", "09:40"),
        new(3, "10:00", "10:45"),
        new(4, "10:55", "11:40"),
        new(5, "12:00", "12:45"),
        new(6, "12:55", "13:40"),
        new(7, "14:00", "14:45"),
        new(8, "14:55", "15:40"),
        new(9, "16:00", "16:45"),
        new(10, "16:55", "17:40"),
        new(11, "18:00", "18:45"),
        new(12, "18:55", "19:40"),
        new(13, "19:50", "20:20"),
    ];

    /// <summary>教务日历里的一天。</summary>
    public sealed record CalendarDay(int Week, int WeekDay, string Day);

    /// <summary>一份教务日历（<c>semester/calendar</c> 的 <c>data</c>）。</summary>
    public sealed record CalendarInfo(
        string? Year,
        string? Semester,
        string? StartDay,
        string? EndDay,
        IReadOnlyList<CalendarDay> Days)
    {
        public static CalendarInfo Empty { get; } = new(null, null, null, null, []);
    }

    /// <summary>suffix 里表示"单周上课"的标记。</summary>
    public const string OddWeekSuffix = "单周";

    /// <summary>suffix 里表示"双周上课"的标记。</summary>
    public const string EvenWeekSuffix = "双周";

    /// <summary>周次文本里的区间分隔与范围符号（<c>1-16周</c> / <c>5周,9周,13-15周</c>）。</summary>
    private static readonly Regex WeekNumberRe = new(
        @"(?<a>[0-9]{1,2})(?:\s*[-–—~至]\s*(?<b>[0-9]{1,2}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析 <c>semester/calendar</c> 的 <c>data</c> 数组。
    ///
    /// <para>条目形如 <c>{"year":"2026-2027","semester":"1","startDay":"2026-09-07",
    /// "endDay":"2027-02-07","day":"2026-09-14","week":"1","weekDay":"1"}</c>；
    /// 非法条目（缺 week / weekDay / day）直接跳过，不让一条脏数据毁掉整份日历。</para>
    /// </summary>
    public static CalendarInfo ParseCalendar(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return CalendarInfo.Empty;

        var days = new List<CalendarDay>();
        string? year = null;
        string? semester = null;
        string? startDay = null;
        string? endDay = null;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            year ??= Text(item, "year");
            semester ??= Text(item, "semester");
            startDay ??= Text(item, "startDay");
            endDay ??= Text(item, "endDay");

            var week = Number(item, "week");
            var weekDay = Number(item, "weekDay");
            var day = Text(item, "day");
            if (week is null || weekDay is null || string.IsNullOrWhiteSpace(day)) continue;
            if (weekDay is < 1 or > 7) continue;

            days.Add(new CalendarDay(week.Value, weekDay.Value, day!));
        }

        return new CalendarInfo(year, semester, startDay, endDay, days);
    }

    /// <summary>
    /// 把日历 + 学期 id 变成 <see cref="Term"/>。
    ///
    /// <para>三条口径（都是实测定下来的，别改）：</para>
    /// <list type="number">
    ///   <item><b>开学日</b> = 第 1 周周一的 <c>day</c>，不是 <c>startDay</c> —— 实测 <c>startDay=2026-09-07</c>
    ///   是第 0 周（报到周），第 1 周周一才是 <c>2026-09-14</c>，而 <c>listByWeek?week=1</c> 里
    ///   <c>detailTime</c> 正是 2026-09-14，两边对得上。</item>
    ///   <item><b>总周数</b> = 日历里最大的 <c>week</c>（实测 21，覆盖到 <c>endDay</c>）；没有日历时退 16。</item>
    ///   <item><b>学期 id</b> 形如 <c>2026-2027-1</c>（年份段 + 学期序号），解析不出来时退成日历里的
    ///   <c>year</c>/<c>semester</c> 字段。</item>
    /// </list>
    /// </summary>
    public static Term BuildTerm(string? termId, CalendarInfo calendar, int fallbackTotalWeeks = 16)
    {
        var (year, termNo) = ParseTermId(termId)
            ?? ParseTermId(JoinTermId(calendar.Year, calendar.Semester))
            ?? (0, 0);

        var totalWeeks = calendar.Days.Count > 0
            ? calendar.Days.Max(day => day.Week)
            : 0;
        if (totalWeeks <= 0) totalWeeks = fallbackTotalWeeks;

        var startDate = calendar.Days
            .Where(day => day.Week == 1 && day.WeekDay == 1)
            .Select(day => day.Day)
            .FirstOrDefault()
            ?? calendar.Days.Where(day => day.Week == 1).Select(day => day.Day).OrderBy(text => text, StringComparer.Ordinal).FirstOrDefault();

        var id = termId
            ?? JoinTermId(calendar.Year, calendar.Semester)
            ?? string.Empty;

        return new Term(id, string.Empty, year, termNo, totalWeeks, [.. Slots.Select(slot => slot with { })], startDate);
    }

    /// <summary>
    /// 解析课程条目的 <c>time</c> 周次文本 + <c>suffix</c> 单双周标记，返回周次掩码。
    ///
    /// <para>规则：<c>"1-16周"</c> → 1..16；<c>"5周,9周,13-15周"</c> → 5,9,13,14,15；
    /// <c>suffix</c> 含 <c>单周</c> 只留奇数周、含 <c>双周</c> 只留偶数周；<c>time</c> 为空视为全周。</para>
    ///
    /// <para>顺序很重要：<b>先展开区间再按单双周过滤</b>。实测"形势与政策"是
    /// <c>time="5周,9周,13-15周"</c> + <c>suffix=["单周"]</c>，正确结果是 5/9/13/15 ——
    /// 先过滤再展开会把 13-15 整段当成"第 13 周"处理。</para>
    /// </summary>
    public static uint ParseWeeks(string? time, IEnumerable<string>? suffix, int totalWeeks)
    {
        var odd = false;
        var even = false;
        var retake = false;
        foreach (var tag in suffix ?? [])
        {
            var value = (tag ?? string.Empty).Trim();
            if (value == OddWeekSuffix) odd = true;
            else if (value == EvenWeekSuffix) even = true;
            else if (value.Length > 0) retake = retake || value == "重修";
        }

        _ = retake; // "重修"/"中期退课"只影响前端角标，不改变周次

        var weeks = new SortedSet<int>();
        if (!string.IsNullOrWhiteSpace(time))
        {
            foreach (Match match in WeekNumberRe.Matches(time!))
            {
                var start = int.Parse(match.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var end = match.Groups["b"].Success
                    ? int.Parse(match.Groups["b"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : start;
                if (end < start) (start, end) = (end, start);

                for (var week = start; week <= end; week++) weeks.Add(week);
            }
        }

        // 单双周过滤（time 给不出周次时不做过滤：全周里筛单/双周没有意义，与前端"信息缺失就不显示"不同 ——
        // 挂件宁可多显示也不能整门课消失）
        if (weeks.Count > 0 && (odd || even))
        {
            var kept = weeks.Where(week => (odd && week % 2 == 1) || (even && week % 2 == 0));
            weeks = [.. kept];
        }

        if (weeks.Count == 0)
        {
            // 没有周次信息：按"整学期都上"处理（同济那条路的同一兜底）
            return Weeks.FullMask(totalWeeks);
        }

        var limit = totalWeeks > 0 ? totalWeeks : weeks.Max;
        return Weeks.FromWeeks(weeks.Where(week => week >= 1 && week <= limit));
    }

    /// <summary>把 <c>2026-2027-1</c> 拆成 <c>(2026, 1)</c>；解析不出返回 <c>null</c>。</summary>
    public static (int Year, int TermNo)? ParseTermId(string? termId)
    {
        if (string.IsNullOrWhiteSpace(termId)) return null;

        var parts = termId!.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        if (!int.TryParse(parts[0], out var year) || year < 1900 || year > 2200) return null;

        // 年份段可能是 "2026-2027"（三个 part）也可能是 "2026"（两个 part）
        var termPart = parts.Length >= 3 ? parts[2] : parts[1];
        if (!int.TryParse(termPart, out var termNo) || termNo is < 1 or > 3) return null;
        return (year, termNo);
    }

    /// <summary>拼学期 id（<c>2026-2027</c> + <c>1</c> → <c>2026-2027-1</c>）。</summary>
    public static string? JoinTermId(string? year, string? semester)
    {
        if (string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(semester)) return null;
        return $"{year!.Trim()}-{semester!.Trim()}";
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var n) ? n : null,
            JsonValueKind.String => int.TryParse(value.GetString(), out var s) ? s : null,
            _ => null,
        };
    }
}
