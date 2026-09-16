using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tjt.Core.Adapters;

/// <summary>
/// 上海交通大学「学在交大」课表适配器。
///
/// <para>数据源：<c>GET /app/stu/lesson/listBySemester?year=2026-2027&amp;semester=1</c>
/// （前端 bundle <c>chunk 3 = timetable</c> 里的 <c>sv()</c>），响应形如：</para>
///
/// <code>
/// { "errno": "0", "error": "成功", "data": [
///   { "name": "刑法总论", "code": "(2026-2027-1)-LAW1305-01", "jxbId": "2026202701LAW130501",
///     "address": "东中院1-109", "duration": ["9","10"], "repeat": false, "suffix": [],
///     "day": "2", "dayFormatted": "二", "detailTime": null, "weekNum": "1",
///     "time": "1-16周", "xqj": null, "credit": "4.0", "lessonClassCode": null } ] }
/// </code>
///
/// <para><b>为什么不用课表页默认的 <c>listByWeek</c></b>：按周那条响应的 <c>time</c> 是 <c>null</c>
/// （实测 14 条全是），拿不到"这门课在第几周上" —— 照它建挂件只会剩本周有课。
/// 整学期那条才带 <c>time="1-16周"</c> / <c>"5周,9周,13-15周"</c> 与 <c>suffix=["单周"]</c>。
/// 抓取侧因此把按周请求改写成整学期请求（见 <see cref="SjtuWebCapture.SemesterLessonsUrl"/>）。</para>
///
/// <para>两个坑写在 <see cref="SjtuTerms"/> 里：开学日是第 1 周周一（不是 <c>startDay</c>）、
/// 单双周要在展开区间**之后**过滤。</para>
///
/// <para>一张课表需要两份数据：课程（<c>listBySemester</c>）+ 教务日历（<c>semester/calendar</c>）。
/// 走 <see cref="ImportInput.Files"/> 传第二份（该字段本就是"课表 + 校历 + 学生信息"多份输入的设计）；
/// 只有课程时也能导入，只是学期退化成"从课表周次推断"，并给一条
/// <c>sjtu.term.startDate</c> 警告。</para>
/// </summary>
public sealed class SjtuStudentAdapter : ISchoolAdapter
{
    public const string AdapterId = "sjtu-student";
    public const string AdapterVersion = "1.0.0";

    /// <summary>单例：适配器无状态。</summary>
    public static SjtuStudentAdapter Instance { get; } = new();

    private SjtuStudentAdapter()
    {
    }

    public string Id => AdapterId;

    public string DisplayName => "上海交通大学 · 学在交大课表";

    public string Version => AdapterVersion;

    public string Description =>
        "解析交大课表（`data[]` 的 `name`/`day`/`duration`/`time`/`suffix`/`jxbId`），"
        + "并可由教务日历（`/stu/school/semester/calendar` 的逐日 `{week,weekDay,day}`）确定开学日与总周数。";

    public bool CanFetch => true;

    /// <summary>课程代码：<c>(2026-2027-1)-LAW1305-01</c> 里的 <c>LAW1305</c>。</summary>
    private static readonly Regex CodeRe = new(
        @"^\((?<term>[^)]+)\)-(?<rest>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>分类结果：课程条目 + 可能同时带进来的教务日历。</summary>
    private sealed class Classified
    {
        public List<JsonElement> Lessons { get; } = [];

        public List<SjtuTerms.CalendarInfo> Calendars { get; } = [];

        public List<string> Sources { get; } = [];
    }

    public double Detect(ImportInput input)
    {
        var best = 0d;
        foreach (var (_, text) in AdapterInput.Texts(input))
        {
            var score = Score(text);
            if (score > best) best = score;
        }

        return best;
    }

    public ImportResult Parse(ImportInput input, AdapterContext context)
    {
        _ = context;
        var diagnostics = new List<Diagnostic>();
        var keepAlive = new List<JsonDocument>();
        try
        {
            var classified = Classify(input, keepAlive);

            // 没有日历时，先用课表里的周次反推"至少多少教学周"，好让周次掩码装得下
            var inferredWeeks = InferTotalWeeks(classified.Lessons);

            var calendar = classified.Calendars.FirstOrDefault() ?? SjtuTerms.CalendarInfo.Empty;
            var termId = input.TermId ?? SjtuTerms.JoinTermId(calendar.Year, calendar.Semester);
            var term = SjtuTerms.BuildTerm(termId, calendar, inferredWeeks);

            if (calendar.Days.Count == 0)
            {
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Warn,
                    "sjtu.term.startDate",
                    "这次没有拿到教务日历（`/app/stu/school/semester/calendar`），学期开学日未知，"
                    + "「第 N 周」只能按周次文本反推；建议用内置登录窗口导入一次以补齐日历。"));
            }

            var courses = BuildCourses(classified.Lessons, term.TotalWeeks, diagnostics);

            if (courses.Count == 0 && classified.Lessons.Count == 0)
            {
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Error,
                    "sjtu.schedule.missing",
                    "没有找到课表数据：需要 `data[]` 里带 `name`/`day`/`duration` 的课程条目"
                    + "（交大课表页 `listBySemester` / `listByWeek` 的响应）。"));
            }
            else
            {
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Info,
                    "sjtu.lessons",
                    $"识别为交大课表（学在交大）：{classified.Lessons.Count} 条上课安排。"));
            }

            var sessionCount = courses.Sum(course => course.Sessions.Count);
            if (courses.Count > 0)
            {
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Info,
                    "sjtu.summary",
                    $"导入 {courses.Count} 门课程 / {sessionCount} 条上课安排"
                    + $"（学期 {term.Label}，共 {term.TotalWeeks} 教学周，开学 {term.StartDate ?? "未知"}）。"));
            }

            return new ImportResult
            {
                AdapterId = AdapterId,
                AdapterName = DisplayName,
                AdapterVersion = AdapterVersion,
                Term = term,
                Courses = courses,
                Preselect = [],
                Diagnostics = diagnostics,
                Meta = new Dictionary<string, object?>
                {
                    ["sources"] = classified.Sources.ToArray(),
                    ["termId"] = termId,
                    ["startDate"] = term.StartDate,
                },
            };
        }
        finally
        {
            foreach (var document in keepAlive) document.Dispose();
        }
    }

    /// <summary>
    /// 匹配度打分：交大响应的特征字段是 <c>errno</c> + <c>data[]</c> 里的
    /// <c>jxbId</c>/<c>duration</c>/<c>day</c>。
    ///
    /// <para>必须与同济的 <c>selectedCourses</c> / <c>timeTableList</c> / <c>weekState</c> 特征互斥，
    /// 否则同一份响应会被两个适配器抢（注册表同分时先登记的赢，但分数不同就按分数走）。</para>
    /// </summary>
    private static double Score(string text)
    {
        using var document = AdapterInput.TryParseJson(text);
        if (document is null) return 0;

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return 0;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return 0;
        if (data.GetArrayLength() == 0) return 0;

        var first = data[0];
        if (first.ValueKind != JsonValueKind.Object) return 0;

        var hasDuration = first.TryGetProperty("duration", out _);
        var hasDay = first.TryGetProperty("day", out _);
        var hasJxb = first.TryGetProperty("jxbId", out _);
        var hasErrno = root.TryGetProperty("errno", out _);
        if (!hasDuration || !hasDay) return 0;

        // jxbId 是交大教学班 id（同济没有这个字段）；errno 是交大的包装键
        if (hasJxb || hasErrno) return 0.98;
        return 0;
    }

    /// <summary>把输入里的所有文本分门别类（课程 / 日历）。</summary>
    private static Classified Classify(ImportInput input, List<JsonDocument> keepAlive)
    {
        var result = new Classified();

        foreach (var (label, text) in AdapterInput.Texts(input))
        {
            var document = AdapterInput.TryParseJson(text);
            if (document is null) continue;

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                document.Dispose();
                continue;
            }

            var first = data[0];
            if (first.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                continue;
            }

            if (first.TryGetProperty("week", out _) && first.TryGetProperty("weekDay", out _))
            {
                result.Calendars.Add(SjtuTerms.ParseCalendar(data));
                result.Sources.Add($"{label}:日历");
                keepAlive.Add(document);
                continue;
            }

            if (first.TryGetProperty("duration", out _) && first.TryGetProperty("day", out _))
            {
                result.Lessons.AddRange(data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object));
                result.Sources.Add($"{label}:课表");
                keepAlive.Add(document);
                continue;
            }

            document.Dispose();
        }

        return result;
    }

    /// <summary>没有日历时，从课程条目的周次文本反推总周数（至少 16，交大一学期不会更短）。</summary>
    private static int InferTotalWeeks(IReadOnlyList<JsonElement> lessons)
    {
        var max = 0;
        foreach (var lesson in lessons)
        {
            var time = Text(lesson, "time");
            if (string.IsNullOrWhiteSpace(time)) continue;

            // totalWeeks=0 → ParseWeeks 不裁剪，返回文本里出现过的最大周次
            var mask = SjtuTerms.ParseWeeks(time, null, 0);
            var highest = Weeks.Highest(mask);
            if (highest > max) max = highest;
        }

        return Math.Max(max, 16);
    }

    /// <summary>
    /// 课程条目 → 统一模型。
    ///
    /// <para>合并规则与交大前端 <c>WeekTable.mergeSameClass</c> 同口径：同一天、同一教学班（<c>jxbId</c>）、
    /// <b>节次相邻</b>的记录并成一块（实测"民法总论"是 <c>["6","6"]</c> + <c>["7","8"]</c>，
    /// 前端显示成 6-8 一整块）。前端合并时只保留第一条的字段（丢了周次），这里改成
    /// <b>周次取并集</b>、教室取第一条非空的 —— 视觉一致但信息不丢。</para>
    /// </summary>
    public static List<Course> BuildCourses(IReadOnlyList<JsonElement> lessons, int totalWeeks, List<Diagnostic> diagnostics)
    {
        var byCourse = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var codes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, List<Session>>(StringComparer.Ordinal);
        var skipped = 0;
        var noWeeks = 0;

        foreach (var lesson in lessons)
        {
            var name = Text(lesson, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var code = Text(lesson, "code");
            var id = Text(lesson, "jxbId");
            if (string.IsNullOrWhiteSpace(id)) id = code;
            if (string.IsNullOrWhiteSpace(id)) id = $"sjtu-{order.Count}";

            var day = Number(lesson, "day");
            var (start, end) = Duration(lesson);
            if (day is null || start is null || end is null)
            {
                skipped += 1;
                continue;
            }

            // day 越界（含 0）不进课表；交大 day 与同济同口径：1=周一 … 7=周日
            if (day is < 1 or > 7) continue;

            var time = Text(lesson, "time");
            if (string.IsNullOrWhiteSpace(time)) noWeeks += 1;
            var weeks = SjtuTerms.ParseWeeks(time, Suffix(lesson), totalWeeks);
            var room = Text(lesson, "address");

            if (!byCourse.ContainsKey(id!))
            {
                byCourse[id!] = order.Count;
                order.Add(id!);
                names[id!] = name!;
                codes[id!] = code;
                sessions[id!] = [];
            }

            AddSession(sessions[id!], id!, day.Value, start.Value, end.Value, weeks, room);
        }

        if (skipped > 0)
        {
            diagnostics.Add(AdapterInput.Diagnostic(
                DiagnosticLevel.Info,
                "sjtu.noSchedule",
                $"有 {skipped} 条课程记录缺少 day/duration（如军训、线上课），已跳过。"));
        }

        if (noWeeks > 0)
        {
            diagnostics.Add(AdapterInput.Diagnostic(
                DiagnosticLevel.Info,
                "sjtu.weeks.unknown",
                $"有 {noWeeks} 条记录没有周次文本（`time` 为空，通常是按周接口的响应），已按整学期处理。"));
        }

        var courses = new List<Course>();
        foreach (var id in order)
        {
            var list = sessions[id];
            if (list.Count == 0) continue;

            courses.Add(new Course(
                id,
                names[id],
                [],
                [.. list.OrderBy(session => (int)session.Day).ThenBy(session => session.StartSlot)],
                CourseCodeOf(codes[id]),
                codes[id]));
        }

        return SortCourses(courses);
    }

    /// <summary>
    /// 往同一门课里加一条时段：与已有块**同天且相邻（或重叠）**就并进去，否则新起一块。
    ///
    /// <para>相邻判定用 <c>下一段起始 == 当前段结束 + 1</c>；重叠（重复条目）也算命中，
    /// 这样后端返回重复记录时不会画出两层色块。</para>
    /// </summary>
    private static void AddSession(List<Session> sessions, string id, int day, int start, int end, uint weeks, string? room)
    {
        for (var index = 0; index < sessions.Count; index++)
        {
            var existing = sessions[index];
            if ((int)existing.Day != day) continue;
            if (start > existing.EndSlot + 1) continue;
            if (end < existing.StartSlot - 1) continue;

            var merged = existing with
            {
                Id = $"{id}-{day}-{Math.Min(existing.StartSlot, start)}-{Math.Max(existing.EndSlot, end)}",
                StartSlot = Math.Min(existing.StartSlot, start),
                EndSlot = Math.Max(existing.EndSlot, end),
                Weeks = existing.Weeks | weeks,
                Room = string.IsNullOrWhiteSpace(existing.Room) ? room : existing.Room,
            };
            sessions[index] = merged;
            return;
        }

        sessions.Add(new Session(
            $"{id}-{day}-{start}-{end}",
            (Weekday)day,
            start,
            end,
            weeks,
            room));
    }

    /// <summary>取 <c>duration</c> 的起止节次（元素可能是字符串或数字；后端偶尔给 14，折算成 13）。</summary>
    private static (int? Start, int? End) Duration(JsonElement lesson)
    {
        if (!lesson.TryGetProperty("duration", out var value) || value.ValueKind != JsonValueKind.Array) return (null, null);

        var numbers = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            var parsed = item.ValueKind switch
            {
                JsonValueKind.Number => item.TryGetInt32(out var n) ? n : (int?)null,
                JsonValueKind.String => int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
                _ => null,
            };
            if (parsed is not null) numbers.Add(parsed.Value);
        }

        if (numbers.Count == 0) return (null, null);

        var start = ClampSlot(numbers[0]);
        var end = ClampSlot(numbers.Count > 1 ? numbers[^1] : numbers[0]);
        if (start > end) (start, end) = (end, start);
        return (start, end);
    }

    /// <summary>节次折算到 1..13（前端 <c>14 == t &amp;&amp; (t = 13)</c> 的同款处理）。</summary>
    private static int ClampSlot(int slot)
    {
        if (slot == 14) return 13;
        return Math.Clamp(slot, 1, SjtuTerms.SlotCount);
    }

    /// <summary>课程条目的 <c>suffix</c> 数组（<c>["单周"]</c> / <c>["重修"]</c> …）。</summary>
    private static IReadOnlyList<string> Suffix(JsonElement lesson) => AdapterInput.StringList(lesson, "suffix");

    /// <summary>从 <c>(2026-2027-1)-LAW1305-01</c> 里取出课程代码 <c>LAW1305</c>。</summary>
    public static string? CourseCodeOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var match = CodeRe.Match(code!.Trim());
        if (!match.Success) return code;

        var rest = match.Groups["rest"].Value;
        var cut = rest.LastIndexOf('-');
        return cut > 0 ? rest[..cut] : rest;
    }

    /// <summary>把课程排成稳定顺序（学院/代码/名称），保证两次导入结果一致。</summary>
    private static List<Course> SortCourses(List<Course> courses) =>
        [.. courses
            .OrderBy(course => course.CourseCode ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(course => course.Name, StringComparer.Ordinal)
            .ThenBy(course => course.Id, StringComparer.Ordinal)];

    private static string? Text(JsonElement parent, string name) => AdapterInput.StringOrNull(parent, name);

    private static int? Number(JsonElement parent, string name) => AdapterInput.IntOrNull(parent, name);
}
