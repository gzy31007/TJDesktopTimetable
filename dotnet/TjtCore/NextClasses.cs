using System.Globalization;

namespace Tjt.Core;

public enum NextClassStatus { InformationInsufficient, NoFuture, Found }

/// <summary>严格未来的最早完整同刻组；LocalToday 仅由查询时刻与课表时区决定。</summary>
public sealed record NextClassGroup(
    NextClassStatus Status,
    DateTimeOffset? StartsAt,
    IReadOnlyList<SessionOccurrence> Sessions,
    string LocalToday);

/// <summary>从原始上课安排中选择下一次上课，不受网格视图影响。</summary>
public static class NextClasses
{
    public static NextClassGroup Find(
        IReadOnlyList<Course> courses,
        Term term,
        DateTimeOffset now,
        int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes)
    {
        ArgumentNullException.ThrowIfNull(courses);
        ArgumentNullException.ThrowIfNull(term);
        var today = string.Empty;
        try
        {
            today = Time.LocalTodayIso(now, tzOffsetMinutes);
            return FindKnownCalendar(courses, term, now, tzOffsetMinutes, today);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 畸形校历或日期越界只能使摘要不确定，不应使整张课表消失。
            return new NextClassGroup(NextClassStatus.InformationInsufficient, null, [], today);
        }
    }

    private static NextClassGroup FindKnownCalendar(
        IReadOnlyList<Course> courses, Term term, DateTimeOffset now, int tzOffsetMinutes, string today)
    {
        if (!courses.Any(course => course.Sessions.Any(session => session.Weeks != 0)))
            return new NextClassGroup(NextClassStatus.NoFuture, null, [], today);
        if (term.TotalWeeks is < 1 or > Weeks.MaxWeeks ||
            !DateOnly.TryParseExact(term.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monday))
            return new NextClassGroup(NextClassStatus.InformationInsufficient, null, [], today);

        DateTimeOffset? earliest = null;
        var group = new List<SessionOccurrence>();
        var unknownStarts = new List<(DateTimeOffset Earliest, DateTimeOffset Latest)>();
        monday = monday.AddDays(-(((int)monday.DayOfWeek + 6) % 7));
        var offset = TimeSpan.FromMinutes(tzOffsetMinutes);
        foreach (var course in courses)
        {
            foreach (var session in course.Sessions)
            {
                if (session.Weeks == 0) continue;
                var begin = Time.ToMinutes(term.SlotBegin(session.StartSlot));
                var knownDay = (int)session.Day is >= 1 and <= 7;
                for (var week = 1; week <= Math.Min(term.TotalWeeks, Weeks.MaxWeeks); week++)
                {
                    if ((session.Weeks & Weeks.WeekMask(week)) == 0) continue;
                    // 非法星期可能是这一教学周的任意一天，不能当成另一周的日期。
                    var firstDay = knownDay ? (int)session.Day : 1;
                    var lastDay = knownDay ? (int)session.Day : 7;
                    for (var day = firstDay; day <= lastDay; day++)
                    {
                        var date = monday.AddDays((week - 1) * 7 + day - 1);
                        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), offset);
                        if (begin is null || !knownDay)
                        {
                            // 缺开始时间时，这一天最早和最晚的合法开始分钟均有可能。
                            unknownStarts.Add((midnight.AddMinutes(begin ?? 0), midnight.AddMinutes(begin ?? 1439)));
                            continue;
                        }
                        var start = midnight.AddMinutes(begin.Value);
                        if (start <= now || earliest is { } known && start > known) continue;
                        if (earliest is null || start < earliest)
                        {
                            earliest = start;
                            group.Clear();
                        }
                        group.Add(new SessionOccurrence(course, session, week));
                    }
                }
            }
        }
        // 相等也会影响完整组。尤其 00:00 的候选不能排除同日未知开始的安排。
        if (unknownStarts.Any(range => range.Latest > now && (earliest is null || range.Earliest <= earliest)))
            return new NextClassGroup(NextClassStatus.InformationInsufficient, null, [], today);
        return new NextClassGroup(earliest is null ? NextClassStatus.NoFuture : NextClassStatus.Found,
            earliest, group.ToArray(), today);
    }
}
