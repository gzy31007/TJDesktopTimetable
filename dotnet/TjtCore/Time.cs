using System.Globalization;

namespace Tjt.Core;

/// <summary>
/// 时间推算（TS 侧 <c>packages/core/src/time.ts</c> 的移植）：当前教学周、某天上什么课、下一节课、头部摘要。
///
/// <para><b>日期表示</b>：一律用 <c>YYYY-MM-DD</c> 字符串表示「学期所在地的日历日」，
/// 内部换算成「UTC 日序号」（1970-01-01 = 0）做纯日期算术，等价 TS 的
/// <c>Math.floor(Date.UTC(y, mo - 1, d) / 86400000)</c>。</para>
///
/// <para><b>时区选择（移植要点，刻意不依赖宿主时区）</b>：本实现<b>绝不</b>使用
/// <c>DateTime.Now</c> / <see cref="TimeZoneInfo.Local"/> / <c>ToLocalTime()</c>，分两条互不干扰的路径：</para>
/// <list type="number">
/// <item><description>纯日期算术（<see cref="IsoToDayNumber"/>、<see cref="DayNumberToIso"/>、
/// <see cref="MondayOf"/>、<see cref="AddDays"/>、<see cref="TermWeekAt"/>）用
/// <see cref="DateOnly"/> + <see cref="int"/> 完成：<see cref="DateOnly"/> 本身没有时刻与时区概念，
/// 就是「日历日」，不存在夏令时或偏移问题。</description></item>
/// <item><description>绝对时刻 → 日历日 / 当天分钟数（<see cref="LocalTodayIso"/>、<see cref="LocalMinutesOfDay"/>、
/// <see cref="MsToIsoDate"/>）用 <see cref="DateTimeOffset"/>（UTC 绝对时刻）+ 固定
/// <c>tzOffsetMinutes</c>（默认 <see cref="TimetableModel.DefaultTzOffsetMinutes"/> = 480，即 +08:00）：
/// 先 <c>ToUniversalTime()</c> 归一，再加偏移，最后取 UTC 日历字段 —— 与 TS 的
/// <c>new Date(t + tz * 60000)</c> + <c>getUTC*()</c> 逐位等价。结果只由「绝对时刻 + 固定偏移」决定，
/// 因此在 WSL（Asia/Shanghai）、CI（UTC）或任何宿主时区下都得到同一答案。</description></item>
/// </list>
///
/// <para><b>与 JS 行为对齐处 / 有意偏离处</b>：</para>
/// <list type="bullet">
/// <item><description>TS 用 <c>NaN</c> 表示「解析失败」，这里统一映射为 <c>null</c>
/// （<see cref="IsoToDayNumber"/>、<see cref="MondayOf"/>、<see cref="AddDays"/>、
/// <see cref="IsoToWeekday"/>、<see cref="ToMinutes"/>、<see cref="TermWeekAt"/>），
/// 由可空类型在编译期挡住。TS 的 <c>dayNumberToIso(NaN)</c> 会打印字符串 <c>"NaN-NaN-NaN"</c>，
/// 在 C# 里这种调用写不出来（入参是 <see cref="int"/>）。</description></item>
/// <item><description>TS 的两个正则（<c>^(\d{4})-(\d{2})-(\d{2})</c>、<c>^(\d{1,2}):(\d{2})</c>）是<b>前缀</b>匹配：
/// 允许尾随字符（<c>"2026-09-14T00:00"</c> 可用），月/日/分必须是定长两位。这里手工复刻，
/// 连「先贪心试两位小时、失败再回退一位」的回溯顺序都保持一致。</description></item>
/// <item><description>JS <c>Date.UTC</c> 的进位规则被保留：月/日越界自动进位
/// （<c>"2026-02-30"</c> ≡ 2026-03-02），且 0..99 的年份按 <c>1900 + y</c> 解释
/// （<c>"0099-01-01"</c> ≡ 1999-01-01）——用
/// <c>new DateOnly(y, 1, 1).AddMonths(mo - 1).AddDays(d - 1)</c> 复刻（先归一月、再归一日，与 MakeDay 同序）。
/// 代价：<see cref="DateOnly"/> 的可表示范围是 0001-01-01 … 9999-12-31，超出该范围
/// （如 <c>"9999-99-99"</c> 或极端日序号）会抛 <see cref="ArgumentOutOfRangeException"/>，
/// 而 JS 的日期域大得多；本项目的合法输入恒为 1900–9999 年的真实日期，不受影响。</description></item>
/// <item><description>周次掩码判定用 <c>1u &lt;&lt; (week - 1)</c>：C# 对 <see cref="uint"/> 的移位位数按 32 取模，
/// 与 JS 的 32 位移位同构（<c>1 &lt;&lt; 32</c> 两侧都得 1），所以超长学期的边界行为也一致。</description></item>
/// <item><description>同一起始节次的排序 tie-break：TS 用 <c>name.localeCompare(name)</c>（ICU 区域敏感排序），
/// 这里用 <see cref="StringComparer.Ordinal"/>（UTF-16 码元序）。ASCII 与 BMP 汉字的相对次序通常一致，
/// 但 ICU 的完整区域排序无法在平台无关库里逐字节复刻 —— 只有「同节次 + 不同课名」的排序结果可能不同。
/// 主排序键（起始节次）完全一致，且两者都是稳定排序。</description></item>
/// <item><description><see cref="DateOnly"/> 的 <see cref="DayOfWeek"/> 是 0 = 周日，这里转成
/// 1 = 周一 … 7 = 周日（<see cref="Weekday"/>），与同济教务 <c>dayOfWeek</c> 及 JS 侧约定一致。</description></item>
/// </list>
/// </summary>
public static class Time
{
    /// <summary>1970-01-01 在 <see cref="DateOnly.DayNumber"/> 体系里的值（= 719162），用于两套日序号互转。</summary>
    private static readonly int UnixEpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    /// <summary>
    /// <c>YYYY-MM-DD</c> → UTC 日序号（1970-01-01 = 0）。允许尾随字符（同 TS 的前缀正则）；
    /// 解析失败返回 <c>null</c>（对应 TS 的 <c>Number.NaN</c>）。
    ///
    /// 月/日越界按 JS <c>Date.UTC</c> 的进位规则归一；<c>0000</c>–<c>0099</c> 的年份按 <c>1900 + y</c> 解释。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">归一后的日期落到 <see cref="DateOnly"/> 范围之外（畸形输入，如年份 9999 配月份 99）。</exception>
    public static int? IsoToDayNumber(string iso)
    {
        if (!TryParseIsoPrefix(iso, out var year, out var month, out var day)) return null;
        // JS Date.UTC：0..99 的年按 1900 + y 解释
        if (year <= 99) year += 1900;
        // JS Date.UTC：先归一月（MakeDay(y, m, 1)）再归一日（+ d - 1），越界自动进位
        var date = new DateOnly(year, 1, 1).AddMonths(month - 1).AddDays(day - 1);
        return date.DayNumber - UnixEpochDayNumber;
    }

    /// <summary>UTC 日序号 → <c>YYYY-MM-DD</c>。年份不补零（TS 写的是 <c>${getUTCFullYear()}</c>）；本项目恒为 1900–9999 四位。</summary>
    /// <exception cref="ArgumentOutOfRangeException">日序号落到 <see cref="DateOnly"/> 可表示范围之外。</exception>
    public static string DayNumberToIso(int day)
    {
        var date = DateFromDayNumber(day);
        return $"{date.Year}-{date.Month:D2}-{date.Day:D2}";
    }

    /// <summary>
    /// 某个绝对时刻在指定时区（默认 UTC+8）下的日历日。
    ///
    /// <paramref name="now"/> 缺省取 <see cref="DateTimeOffset.UtcNow"/>，对应 TS 的 <c>new Date()</c>：
    /// 两者都只是「当前绝对时刻」，不掺宿主的本地时区。
    /// </summary>
    public static string LocalTodayIso(DateTimeOffset? now = null, int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes)
    {
        var shifted = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().AddMinutes(tzOffsetMinutes);
        return $"{shifted.Year}-{shifted.Month:D2}-{shifted.Day:D2}";
    }

    /// <summary>指定时区（默认 UTC+8）下的当前时刻分钟数（0..1439）。</summary>
    public static int LocalMinutesOfDay(DateTimeOffset? now = null, int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes)
    {
        var shifted = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().AddMinutes(tzOffsetMinutes);
        return (shifted.Hour * 60) + shifted.Minute;
    }

    /// <summary>日序号 → 星期（1 = 周一 … 7 = 周日）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">日序号落到 <see cref="DateOnly"/> 可表示范围之外。</exception>
    public static Weekday DayNumberToWeekday(int day)
    {
        var dow = DateFromDayNumber(day).DayOfWeek; // 0 = 周日
        return dow == DayOfWeek.Sunday ? Weekday.Sunday : (Weekday)dow;
    }

    /// <summary>日期 → 星期；解析失败返回 <c>null</c>（对应 TS 的 <c>NaN</c>）。</summary>
    public static Weekday? IsoToWeekday(string iso)
    {
        var day = IsoToDayNumber(iso);
        return day is null ? null : DayNumberToWeekday(day.Value);
    }

    /// <summary>
    /// 把毫秒时间戳转成指定时区（默认 UTC+8）的 <c>YYYY-MM-DD</c>。
    /// 同济校历的毫秒时间戳是北京时间午夜，等价 TS 的 <c>localTodayIso(new Date(ms), tz)</c>。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">毫秒值超出 <see cref="DateTimeOffset"/> 可表示范围（TS 那边是 <c>Invalid Date</c>）。</exception>
    public static string MsToIsoDate(long ms, int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes) =>
        LocalTodayIso(DateTimeOffset.FromUnixTimeMilliseconds(ms), tzOffsetMinutes);

    /// <summary>某天所在周的周一（按周一为一周起点）；日期解析失败返回 <c>null</c>。</summary>
    public static string? MondayOf(string iso)
    {
        var day = IsoToDayNumber(iso);
        if (day is null) return null;
        return DayNumberToIso(day.Value - ((int)DayNumberToWeekday(day.Value) - 1));
    }

    /// <summary>日期加减天数；日期解析失败返回 <c>null</c>。</summary>
    public static string? AddDays(string iso, int days)
    {
        var day = IsoToDayNumber(iso);
        return day is null ? null : DayNumberToIso(day.Value + days);
    }

    /// <summary>
    /// 当前是第几教学周；开学前或学期结束后返回 <c>null</c>。
    ///
    /// <paramref name="term"/> 的 <see cref="Term.StartDate"/> 必须是第 1 周周一（空/未设置同样返回 <c>null</c>）；
    /// 即使给了别的日期，也会先按 <see cref="MondayOf"/> 归到所在周的周一再算。
    /// <see cref="Term.TotalWeeks"/> 为 0 表示不限周数（不做学期结束判定）。
    /// </summary>
    public static int? TermWeekAt(Term term, string iso)
    {
        if (string.IsNullOrEmpty(term.StartDate)) return null;
        var startIso = MondayOf(term.StartDate);
        var start = startIso is null ? null : IsoToDayNumber(startIso);
        var today = IsoToDayNumber(iso);
        if (start is null || today is null) return null;
        var diff = today.Value - start.Value;
        if (diff < 0) return null;
        // diff >= 0，整数除法即 Math.floor(diff / 7)
        var week = (diff / 7) + 1;
        if (term.TotalWeeks > 0 && week > term.TotalWeeks) return null;
        return week;
    }

    /// <summary>指定日期该上哪些课（按开始节次排序，同节次再按课名序）。</summary>
    public static SessionOccurrence[] SessionsOnDate(IEnumerable<Course> courses, Term term, string iso)
    {
        var week = TermWeekAt(term, iso);
        if (week is null) return [];
        // week 非 null 说明 iso 可解析，星期必然有值
        var weekday = IsoToWeekday(iso)!.Value;
        var hits = new List<SessionOccurrence>();
        foreach (var course in courses)
        {
            foreach (var session in course.Sessions)
            {
                if (session.Day != weekday) continue;
                if ((session.Weeks & (1u << (week.Value - 1))) == 0) continue;
                hits.Add(new SessionOccurrence(course, session, week.Value));
            }
        }
        // 稳定排序：主键起始节次，次键课名（TS 是 localeCompare，这里 Ordinal，见类文档）
        return [.. hits.OrderBy(o => o.Session.StartSlot).ThenBy(o => o.Course.Name, StringComparer.Ordinal)];
    }

    /// <summary>今天哪些课（<see cref="SessionsOnDate"/> 的语义化封装）。</summary>
    public static SessionOccurrence[] TodaysSessions(
        IEnumerable<Course> courses,
        Term term,
        DateTimeOffset? now = null,
        int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes) =>
        SessionsOnDate(courses, term, LocalTodayIso(now, tzOffsetMinutes));

    /// <summary>下一节课（含正在进行中的课）；未来 14 天内都没有则返回 <c>null</c>。</summary>
    public static UpcomingSession? NextSession(
        IEnumerable<Course> courses,
        Term term,
        DateTimeOffset? now = null,
        int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes)
    {
        var today = LocalTodayIso(now, tzOffsetMinutes);
        var nowMinutes = LocalMinutesOfDay(now, tzOffsetMinutes);

        // 兜底候选：今天已经上完的最后一节（TS 里同名的 inProgressFallback）
        UpcomingSession? inProgressFallback = null;

        for (var offset = 0; offset < 14; offset += 1)
        {
            var date = AddDays(today, offset)!; // today 来自 LocalTodayIso，必然合法
            foreach (var occurrence in SessionsOnDate(courses, term, date))
            {
                var begin = ToMinutes(term.SlotBegin(occurrence.Session.StartSlot));
                var end = ToMinutes(term.SlotEnd(occurrence.Session.EndSlot));
                if (begin is null || end is null) continue;
                var minutesUntil = begin.Value - nowMinutes + (offset * 1440);
                var inProgress = offset == 0 && nowMinutes >= begin.Value && nowMinutes <= end.Value;
                var item = new UpcomingSession(
                    occurrence.Course,
                    occurrence.Session,
                    occurrence.Week,
                    inProgress ? 0 : minutesUntil,
                    date,
                    inProgress);
                if (inProgress) return item;
                if (minutesUntil > 0) return item;
                inProgressFallback ??= item;
            }
        }
        return inProgressFallback;
    }

    /// <summary>
    /// <c>HH:mm</c> → 当天分钟数；非法（含 <c>24:00</c>、<c>08:60</c>）返回 <c>null</c>。
    /// 同 TS：允许尾随字符，小时可写一位（<c>"8:00"</c> 合法），分钟必须是定长两位。
    /// </summary>
    public static int? ToMinutes(string hhmm)
    {
        if (!TryParseHhmm(hhmm, out var hours, out var minutes)) return null;
        if (hours > 23 || minutes > 59) return null;
        return (hours * 60) + minutes;
    }

    /// <summary>分钟数 → <c>HH:mm</c>（按 24 小时取模，负数也能落到 0..1439）。</summary>
    public static string MinutesToClock(int minutes)
    {
        var total = ((minutes % 1440) + 1440) % 1440;
        return $"{total / 60:D2}:{total % 60:D2}";
    }

    /// <summary>相对时间描述：<c>23 分钟后</c> / <c>1 小时 5 分钟后</c> / <c>正在上课</c>。</summary>
    public static string DescribeCountdown(int minutesUntil, bool inProgress)
    {
        if (inProgress) return "正在上课";
        if (minutesUntil <= 0) return "即将开始";
        if (minutesUntil < 60) return $"{minutesUntil} 分钟后";
        var h = minutesUntil / 60;
        var m = minutesUntil % 60;
        return m == 0 ? $"{h} 小时后" : $"{h} 小时 {m} 分钟后";
    }

    /// <summary>相对时间描述（对应 TS 的 <c>Pick&lt;UpcomingSession, 'minutesUntil' | 'inProgress'&gt;</c> 入参形态）。</summary>
    public static string DescribeCountdown(UpcomingSession item) => DescribeCountdown(item.MinutesUntil, item.InProgress);

    /// <summary>课表头部一行摘要，例如 <c>2026-2027学年第1学期 · 第 3 周 · 周三</c>；假期时周次文案为 <c>假期</c>。</summary>
    public static HeaderSummary SummarizeNow(
        Term term,
        DateTimeOffset? now = null,
        int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes)
    {
        var date = LocalTodayIso(now, tzOffsetMinutes);
        var week = TermWeekAt(term, date);
        var weekday = IsoToWeekday(date);
        var weekText = week is null ? "假期" : $"第 {week} 周";
        return new HeaderSummary($"{term.Label} · {weekText} · {WeekdayLabelOf(weekday)}", week, date);
    }

    /// <summary>某个 session 在给定学期下展开的上课周次（便于 UI 展示）。</summary>
    public static int[] SessionWeeks(Session session, Term term) => Weeks.ToWeeks(session.Weeks, term.TotalWeeks);

    /// <summary>
    /// 星期文案。TS 的私有 <c>weekdayLabelOf</c> 在越界时返回空串；这里的入参只能来自
    /// <see cref="IsoToWeekday"/>（1..7 或 <c>null</c>），<c>null</c> 对应 TS 的空串分支，
    /// 其余走模型层的 <see cref="TimetableModel.WeekdayLabel"/>（与 TS 的 <c>WEEKDAY_LABELS</c> 同源）。
    /// </summary>
    private static string WeekdayLabelOf(Weekday? day) =>
        day is null ? string.Empty : TimetableModel.WeekdayLabel(day.Value);

    /// <summary>日序号 → <see cref="DateOnly"/>，并把越界转成明确的异常（C# 没有 TS 的 Invalid Date）。</summary>
    private static DateOnly DateFromDayNumber(int day)
    {
        var total = (long)day + UnixEpochDayNumber;
        if (total < 0 || total > DateOnly.MaxValue.DayNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(day), day, "日序号超出 DateOnly 的可表示范围（0001-01-01 … 9999-12-31）。");
        }
        return DateOnly.FromDayNumber((int)total);
    }

    /// <summary>
    /// 复刻 TS 的 <c>^(\d{4})-(\d{2})-(\d{2})</c>：只匹配前缀（尾随字符忽略），
    /// 月与日是定长两位（<c>"2026-9-14"</c> 不匹配）。
    /// </summary>
    private static bool TryParseIsoPrefix(string iso, out int year, out int month, out int day)
    {
        year = 0;
        month = 0;
        day = 0;
        var s = iso.Trim();
        if (s.Length < 10 || s[4] != '-' || s[7] != '-') return false;
        for (var i = 0; i < 10; i++)
        {
            if (i is 4 or 7) continue;
            if (!char.IsAsciiDigit(s[i])) return false;
        }
        year = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
        month = int.Parse(s.AsSpan(5, 2), CultureInfo.InvariantCulture);
        day = int.Parse(s.AsSpan(8, 2), CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// 复刻 TS 的 <c>^(\d{1,2}):(\d{2})</c>：先贪心取两位小时，不成立再回退到一位
    /// （与正则回溯顺序一致），分钟定长两位。
    /// </summary>
    private static bool TryParseHhmm(string hhmm, out int hours, out int minutes)
    {
        hours = 0;
        minutes = 0;
        var s = hhmm.Trim();
        for (var hourDigits = 2; hourDigits >= 1; hourDigits -= 1)
        {
            var needed = hourDigits + 3; // 小时 + ':' + 两位分钟
            if (s.Length < needed) continue;
            var digitsOk = true;
            for (var i = 0; i < hourDigits; i++)
            {
                if (!char.IsAsciiDigit(s[i]))
                {
                    digitsOk = false;
                    break;
                }
            }
            if (!digitsOk || s[hourDigits] != ':') continue;
            if (!char.IsAsciiDigit(s[hourDigits + 1]) || !char.IsAsciiDigit(s[hourDigits + 2])) continue;
            hours = int.Parse(s.AsSpan(0, hourDigits), CultureInfo.InvariantCulture);
            minutes = int.Parse(s.AsSpan(hourDigits + 1, 2), CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }
}

/// <summary>某个 <see cref="Session"/> 在某一天的一次实际开课（对应 TS 的 <c>SessionOccurrence</c>）。</summary>
/// <param name="Course">所属课程。</param>
/// <param name="Session">该课程命中的上课安排。</param>
/// <param name="Week">该日期落在第几教学周（周次掩码已命中）。</param>
public record SessionOccurrence(Course Course, Session Session, int Week);

/// <summary>下一节课的信息（对应 TS 的 <c>UpcomingSession</c>，字段与基类一一对应）。</summary>
/// <param name="Course">所属课程。</param>
/// <param name="Session">命中的上课安排。</param>
/// <param name="Week">该日期落在第几教学周。</param>
/// <param name="MinutesUntil">距离该课开始还有多少分钟（正在上课时为 0；跨天时含整天数 × 1440）。</param>
/// <param name="Date">该课在校历上的日期（<c>YYYY-MM-DD</c>）。</param>
/// <param name="InProgress">是否正在上课。</param>
public sealed record UpcomingSession(
    Course Course,
    Session Session,
    int Week,
    int MinutesUntil,
    string Date,
    bool InProgress) : SessionOccurrence(Course, Session, Week);

/// <summary>课表头部摘要（对应 TS <c>summarizeNow</c> 返回的 <c>{ label, week, date }</c>）。</summary>
/// <param name="Label">整行文案，形如 <c>2026-2027学年第1学期 · 第 3 周 · 周三</c>。</param>
/// <param name="Week">当前教学周；开学前/学期结束后为 <c>null</c>（文案显示「假期」）。</param>
/// <param name="Date">今天的日期（<c>YYYY-MM-DD</c>，按传入时区）。</param>
public sealed record HeaderSummary(string Label, int? Week, string Date);
