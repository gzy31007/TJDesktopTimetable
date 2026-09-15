using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tjt.Core.Adapters;

/// <summary>
/// 同济大学 1 系统「个人课表」适配器（TS 侧 <c>adapters/tongji-student.ts</c> 的移植）。
///
/// 主数据源：选课服务接口 <c>POST /api/electionservice/student/{id}/getDataBk</c> 的响应，
/// 结构为 <c>data.selectedCourses[].course.times[]</c>：
///
/// <code>
/// { "data": { "calendarId": 122,
///     "selectedCourses": [{ "course": {
///       "courseName": "大学物理B2(I)", "courseCode": "50002810095",
///       "teachClassId": 1111111124960363, "teachClassCode": "5000281009505",
///       "times": [{ "dayOfWeek": 4, "timeStart": 5, "timeEnd": 6, "weeks": [1,3,5],
///                   "roomIdI18n": "南201", "teacherCodeI18n": "欧凯", "teacherCode": "21158" }] } }] } }
/// </code>
///
/// 与排课服务（<c>timetable/major</c>）的差异：<c>weeks</c> 是**周次数组**而不是 <c>weekState</c> 掩码，
/// 教室/教师在 <c>times[]</c> 里叫 <c>roomIdI18n</c> / <c>teacherCodeI18n</c>。旧扁平格式仍兼容。
///
/// 复用 <see cref="Time"/>（<c>mondayOf</c> / <c>msToIsoDate</c> / <c>addDays</c>）与
/// <see cref="TongjiTerms"/>（内置学期表）—— 与 TS 侧 <c>tongji-student.ts</c> 的两处 import 一一对应。
///
/// 注意 <c>beginDay</c> 是毫秒时间戳（如 1789315200000），**超出 int 范围**：这里走
/// <see cref="ToLong"/> 而不是 <see cref="ToInt"/>，否则校历学期会被误判成"缺少 beginDay"。
/// </summary>
public sealed class TongjiStudentAdapter : ISchoolAdapter
{
    public const string AdapterId = "tongji-student";
    public const string AdapterVersion = "3.1.0";
    public const string TongjiOrigin = "https://1.tongji.edu.cn";

    /// <summary>单例：适配器无状态。</summary>
    public static TongjiStudentAdapter Instance { get; } = new();

    private TongjiStudentAdapter()
    {
    }

    public string Id => AdapterId;

    public string DisplayName => "同济大学 · 1 系统个人课表";

    public string Version => AdapterVersion;

    public string Description =>
        "解析 1 系统课表：个人课表（`data.selectedCourses[].course.times[]`）与课表页报表接口"
        + "（`data[].timeTableList[]`，如 `reportManagement/findStudentTimetab`），也兼容排课服务的扁平格式。";

    /// <summary>目前仍是手动导入（Cookie 抓取在主进程侧），但接口本身支持抓取。</summary>
    public bool CanFetch => true;

    /// <summary>
    /// 教室/教师文本里的教师指纹：<c>姓名(工号)</c>。
    ///
    /// 与 TS 的 <c>/([\u4e00-\u9fa5]{2,8})\((\d{3,6})\)/g</c> 等价，两处细节必须保持一致：
    /// 中文用 BMP 汉字区间 <c>\u4e00-\u9fa5</c>；数字用 <c>[0-9]</c> 而不是 C# 的 <c>\d</c>
    /// （C# 的 <c>\d</c> 默认还匹配全角/其他 Unicode 数字，JS 的 <c>\d</c> 只匹配 ASCII）。
    /// </summary>
    private static readonly Regex TeacherRe = new(
        "([\u4e00-\u9fa5]{2,8})\\(([0-9]{3,6})\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IntegerRe = new(@"^-?[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>文本末尾是不是"（工号）"：<c>(12345)</c>（半角，实测就是这个形态）。</summary>
    private static readonly Regex TrailingCodeRe = new(@"\([0-9]{3,6}\)$", RegexOptions.CultureInvariant);

    /// <summary>分类结果：一个输入里可能同时含个人课表 / 报表课表 / 排课扁平表 / 校历。</summary>
    private sealed class Classified
    {
        public List<JsonElement> Selected { get; } = [];

        /// <summary>报表服务（<c>reportManagement/findStudentTimetab</c> 等）返回的课程数组。</summary>
        public List<JsonElement> Report { get; } = [];

        public List<JsonElement> Flat { get; } = [];

        public List<JsonElement> Calendar { get; } = [];

        public string? CalendarId { get; set; }

        public string? CalendarName { get; set; }

        public List<string> Sources { get; } = [];
    }

    /// <summary>扁平格式累积中的课程（<see cref="Course"/> 是不可变 record，这里先攒 sessions）。</summary>
    private sealed class MutableCourse
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public string? CourseCode { get; init; }

        public string? TeachingClassCode { get; init; }

        public required List<string> Teachers { get; init; }

        public string? Faculty { get; init; }

        public string? Campus { get; init; }

        public List<Session> Sessions { get; } = [];
    }

    /// <summary>从 <c>value</c>（"课程(代码) 教师(工号) [周次] 教室"）里解析教师。</summary>
    public static List<string> ParseTeachersFromValue(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(value)) return result;

        foreach (Match match in TeacherRe.Matches(value))
        {
            var name = match.Groups[1].Value;
            var code = match.Groups[2].Value;
            if (name.Length > 0 && code.Length > 0) result.Add($"{name}({code})");
        }

        return result;
    }

    /// <summary>选课服务格式 → 课程列表。</summary>
    public static List<Course> BuildCoursesFromSelected(IReadOnlyList<JsonElement> selected, List<Diagnostic> diagnostics)
    {
        var courses = new List<Course>();
        var skipped = 0;

        foreach (var entry in selected)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var course = Prop(entry, "course");
            if (course.ValueKind != JsonValueKind.Object) continue;

            var times = Elements(Prop(course, "times")).Where(item => item.ValueKind == JsonValueKind.Object).ToList();
            if (times.Count == 0)
            {
                // 军训等没有排课时段的课程不进课表
                skipped += 1;
                continue;
            }

            var courseCode = ToStr(Prop(course, "courseCode"));
            var classCode = ToStr(Prop(course, "teachClassCode"));
            var id = ToStr(Prop(course, "teachClassId")) ?? classCode ?? courseCode ?? $"course-{courses.Count}";
            var name = ToStr(Prop(course, "courseName")) ?? "(未知课程)";

            var teacherOrder = new List<string>();
            var teacherSeen = new HashSet<string>(StringComparer.Ordinal);

            // 同一格（同天 + 同起止节次 + 同教室）可能有多条 times：
            // 典型如"专业导论"按周次换老师（weeks=[10] / [11] / [1,2,3,4,13,14,15,16] …），
            // 必须合并成一块、周次取并集，否则课表上会出现一堆完全重叠的色块。
            var sessions = new List<Session>();
            var sessionIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var time in times)
            {
                var day = ToInt(Prop(time, "dayOfWeek"));
                var startSlot = ToInt(Prop(time, "timeStart"));
                var endSlot = ToInt(Prop(time, "timeEnd"));
                if (day is null || startSlot is null || endSlot is null) continue;
                if (day is < 1 or > 7) continue;

                foreach (var teacher in ParseTeachersFromValue(ToStr(Prop(time, "value")) ?? ToStr(Prop(time, "newValue"))))
                {
                    if (teacherSeen.Add(teacher)) teacherOrder.Add(teacher);
                }

                var single = ToStr(Prop(time, "teacherCodeI18n"));
                var teacherCode = ToStr(Prop(time, "teacherCode"));
                if (single is not null && teacherCode is not null)
                {
                    var withCode = $"{single}({teacherCode})";
                    if (teacherSeen.Add(withCode)) teacherOrder.Add(withCode);
                }
                else if (single is not null)
                {
                    if (teacherSeen.Add(single)) teacherOrder.Add(single);
                }

                var weekList = AdapterInput.AsArray(Prop(time, "weeks"));
                var weeks = weekList is not null && weekList.Value.GetArrayLength() > 0
                    ? Weeks.FromWeeks(weekList.Value.EnumerateArray().Select(item => ToInt(item) ?? 0))
                    : Weeks.FullMask(16);

                var room = ToStr(Prop(time, "roomIdI18n"));
                var key = $"{day}-{startSlot}-{endSlot}-{room ?? ""}";

                if (sessionIndex.TryGetValue(key, out var existing))
                {
                    sessions[existing] = sessions[existing] with { Weeks = sessions[existing].Weeks | weeks };
                    continue;
                }

                sessionIndex[key] = sessions.Count;
                sessions.Add(new Session(
                    $"{id}-{key}",
                    (Weekday)day.Value,
                    startSlot.Value,
                    endSlot.Value,
                    weeks,
                    room));
            }

            if (sessions.Count == 0)
            {
                skipped += 1;
                continue;
            }

            courses.Add(new Course(
                id,
                name,
                teacherOrder,
                [.. sessions.OrderBy(session => (int)session.Day).ThenBy(session => session.StartSlot)],
                courseCode,
                classCode));
        }

        if (skipped > 0)
        {
            diagnostics.Add(AdapterInput.Diagnostic(
                DiagnosticLevel.Info,
                "tongji.noSchedule",
                $"有 {skipped} 门课没有排课时段（如军训），已跳过。"));
        }

        return SortCourses(courses);
    }

    /// <summary>
    /// 报表服务格式（<c>data[].timeTableList[]</c>，课表页真正调的那条接口）→ 课程列表。
    ///
    /// <para>与 <see cref="BuildCoursesFromSelected"/> 的差异只有"包法"：课程在数组顶层而不是
    /// <c>selectedCourses[].course</c>，排课数组叫 <c>timeTableList</c> 而不是 <c>times</c>；
    /// <c>dayOfWeek</c> / <c>timeStart</c> / <c>timeEnd</c> / <c>weeks</c> 数组的语义完全一致。</para>
    ///
    /// <para>教室：优先 <c>roomIdI18n</c>（如"北301"），空则退 <c>roomLable</c>
    /// （线上课堂 / 操场这类没有教室编号的场地）—— 实测 27 条里 23 条是前者、4 条只有后者。</para>
    /// </summary>
    public static List<Course> BuildCoursesFromReport(IReadOnlyList<JsonElement> items, List<Diagnostic> diagnostics)
    {
        var courses = new List<Course>();
        var skipped = 0;

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var times = Elements(Prop(item, "timeTableList"))
                .Where(entry => entry.ValueKind == JsonValueKind.Object)
                .ToList();
            if (times.Count == 0)
            {
                // 军训等没有排课时段的课程不进课表
                skipped += 1;
                continue;
            }

            var courseCode = ToStr(Prop(item, "courseCode"));
            var classCode = ToStr(Prop(item, "classCode"));
            var id = ToStr(Prop(item, "teachingClassId")) ?? classCode ?? courseCode ?? $"course-{courses.Count}";
            var name = ToStr(Prop(item, "courseName")) ?? "(未知课程)";

            var teacherOrder = new List<string>();
            var teacherSeen = new HashSet<string>(StringComparer.Ordinal);

            // 同一格（同天 + 同起止节次 + 同教室）合并、周次取并集 —— 与选课服务那条路同规则
            var sessions = new List<Session>();
            var sessionIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var time in times)
            {
                var day = ToInt(Prop(time, "dayOfWeek"));
                var startSlot = ToInt(Prop(time, "timeStart"));
                var endSlot = ToInt(Prop(time, "timeEnd"));
                if (day is null || startSlot is null || endSlot is null) continue;
                if (day is < 1 or > 7) continue;

                // timeTableList[].teacherName 就是「姓名(工号)」；拿不到时退到 teacherCode 拼一个
                AddTeacher(teacherOrder, teacherSeen, ToStr(Prop(time, "teacherName")), ToStr(Prop(time, "teacherCode")));

                var weekList = AdapterInput.AsArray(Prop(time, "weeks"));
                var weeks = weekList is not null && weekList.Value.GetArrayLength() > 0
                    ? Weeks.FromWeeks(weekList.Value.EnumerateArray().Select(entry => ToInt(entry) ?? 0))
                    : Weeks.FullMask(16);

                var room = FirstNonEmpty(
                    ToStr(Prop(time, "roomIdI18n")),
                    ToStr(Prop(time, "roomLable")),
                    ToStr(Prop(time, "classRoomName")));
                var key = $"{day}-{startSlot}-{endSlot}-{room ?? ""}";

                if (sessionIndex.TryGetValue(key, out var existing))
                {
                    sessions[existing] = sessions[existing] with { Weeks = sessions[existing].Weeks | weeks };
                    continue;
                }

                sessionIndex[key] = sessions.Count;
                sessions.Add(new Session(
                    $"{id}-{key}",
                    (Weekday)day.Value,
                    startSlot.Value,
                    endSlot.Value,
                    weeks,
                    room));
            }

            if (sessions.Count == 0)
            {
                skipped += 1;
                continue;
            }

            // 兜底教师：课程级的 teacherName 是「张三,李四」这种纯名字列表（通常不带工号）
            if (teacherOrder.Count == 0)
            {
                foreach (var teacher in SplitNames(ToStr(Prop(item, "teacherName"))))
                {
                    if (teacherSeen.Add(teacher)) teacherOrder.Add(teacher);
                }
            }

            courses.Add(new Course(
                id,
                name,
                teacherOrder,
                [.. sessions.OrderBy(session => (int)session.Day).ThenBy(session => session.StartSlot)],
                courseCode,
                classCode,
                null,
                FirstNonEmpty(ToStr(Prop(item, "campusI18n")), ToStr(Prop(item, "campus")))));
        }

        if (skipped > 0)
        {
            diagnostics.Add(AdapterInput.Diagnostic(
                DiagnosticLevel.Info,
                "tongji.noSchedule",
                $"有 {skipped} 门课没有排课时段（如军训），已跳过。"));
        }

        return SortCourses(courses);
    }

    /// <summary>
    /// 登记一个教师：文本里**已经带工号**（"张三(12345)"）就原样收下，否则用同一行的
    /// <c>teacherCode</c> 拼成"姓名(工号)"（工号是教师指纹的一半，能拼就拼）。
    ///
    /// <para>不能无条件拼：那样会得到 <c>教师M(10008)(10008)</c>。这道判断也不能只靠
    /// <see cref="TeacherRe"/> —— 它只认"汉字姓名"，名字里带字母/数字时匹配不到，
    /// 于是"已经有工号"会被漏判（fixture 里的 <c>教师M(10008)</c> 就踩到了）。</para>
    /// </summary>
    private static void AddTeacher(List<string> order, HashSet<string> seen, string? nameWithCode, string? code)
    {
        if (string.IsNullOrWhiteSpace(nameWithCode)) return;
        var text = nameWithCode.Trim();

        var formatted = TrailingCodeRe.IsMatch(text) || string.IsNullOrEmpty(code) ? text : $"{text}({code})";
        if (seen.Add(formatted)) order.Add(formatted);
    }

    /// <summary>把「张三,李四 王五」这类纯名字列表切开（不带工号时的兜底）。</summary>
    private static List<string> SplitNames(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(value)) return result;

        foreach (var part in value.Split([',', '，', ' ', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }

        return result;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    /// <summary>排课服务扁平格式 → 课程列表（兼容保留）。</summary>
    public static List<Course> BuildCoursesFromFlat(IReadOnlyList<JsonElement> items)
    {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var accumulated = new List<MutableCourse>();

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var day = ToInt(Prop(item, "dayOfWeek"));
            var startSlot = ToInt(Prop(item, "timeStart"));
            var endSlot = ToInt(Prop(item, "timeEnd"));
            var weeks = ToInt(Prop(item, "weekState"));
            if (day is null || startSlot is null || endSlot is null || weeks is null) continue;
            if (day is < 1 or > 7) continue;

            var classCode = ToStr(Prop(item, "code"));
            var courseCode = ToStr(Prop(item, "courseCode"));
            var id = ToStr(Prop(item, "teachingClassId")) ?? classCode ?? $"{courseCode ?? "unknown"}-{day}-{startSlot}";
            var name = ToStr(Prop(item, "courseName")) ?? "(未知课程)";
            var room = ToStr(Prop(item, "roomName"));

            if (!byId.TryGetValue(id, out var position))
            {
                var fromValue = ParseTeachersFromValue(ToStr(Prop(item, "value")) ?? ToStr(Prop(item, "newValue")));
                var codes = Elements(AdapterInput.AsArray(Prop(item, "teacherCodes"))).Select(JsText).ToList();
                position = accumulated.Count;
                byId[id] = position;
                accumulated.Add(new MutableCourse
                {
                    Id = id,
                    Name = name,
                    CourseCode = courseCode,
                    TeachingClassCode = classCode,
                    Teachers = fromValue.Count > 0 ? fromValue : codes,
                    Faculty = ToStr(Prop(item, "facultyI18n")),
                    Campus = ToStr(Prop(item, "campusI18n")),
                });
            }

            var course = accumulated[position];
            var mask = unchecked((uint)weeks.Value);
            var session = new Session(
                $"{id}-{day}-{startSlot}-{endSlot}-{mask}",
                (Weekday)day.Value,
                startSlot.Value,
                endSlot.Value,
                mask,
                room);
            if (!course.Sessions.Any(existing => existing.Id == session.Id)) course.Sessions.Add(session);
        }

        return SortCourses([.. accumulated.Select(course => new Course(
            course.Id,
            course.Name,
            course.Teachers,
            [.. course.Sessions.OrderBy(session => (int)session.Day).ThenBy(session => session.StartSlot)],
            course.CourseCode,
            course.TeachingClassCode,
            course.Faculty,
            course.Campus))]);
    }

    public double Detect(ImportInput input) => DetectScore(input);

    public ImportResult Parse(ImportInput input, AdapterContext context)
    {
        var diagnostics = new List<Diagnostic>();
        var keepAlive = new List<JsonDocument>();
        try
        {
            var classified = Classify(input, keepAlive);

            // 学期 id：显式指定（抓取时从请求 URL 的 calendarId 取）优先于响应体里的 calendarId。
            // 报表格式的响应体里没有 calendarId（它只在 URL 上），所以这一条是它能拿到
            // 开学日期/教学周的前提 —— 内置学期表按 id 命中（见 TongjiTerms）。
            var termId = input.TermId ?? classified.CalendarId;

            var term = BuildTerm(
                PickTerm(classified.Calendar, termId),
                termId,
                classified.CalendarName,
                diagnostics);

            var courses = new List<Course>();
            if (classified.Selected.Count > 0)
            {
                courses = BuildCoursesFromSelected(classified.Selected, diagnostics);
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Info,
                    "tongji.personal",
                    $"识别为个人课表：已选 {classified.Selected.Count} 门课。"));
            }
            else if (classified.Report.Count > 0)
            {
                courses = BuildCoursesFromReport(classified.Report, diagnostics);
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Info,
                    "tongji.report",
                    $"识别为 1 系统课表（报表接口格式）：共 {classified.Report.Count} 门课。"));
            }
            else if (classified.Flat.Count > 0)
            {
                courses = BuildCoursesFromFlat(classified.Flat);
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Info,
                    "tongji.flat",
                    $"按排课服务格式解析 {classified.Flat.Count} 条记录。"));
            }
            else
            {
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Error,
                    "tongji.schedule.missing",
                    "没有找到课表数据：需要 `data.selectedCourses[].course.times[]`（个人课表）或含 `weekState/dayOfWeek` 的数组。"));
            }

            var sessionCount = courses.Sum(course => course.Sessions.Count);
            if (courses.Count > 0)
            {
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Info,
                    "tongji.summary",
                    $"导入 {courses.Count} 门课程 / {sessionCount} 条上课安排（学期 {(!string.IsNullOrEmpty(term.Name) ? term.Name : term.Id)}，共 {term.TotalWeeks} 教学周）。"));
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
                    ["calendarId"] = classified.CalendarId,
                    ["calendarName"] = classified.CalendarName,
                },
            };
        }
        finally
        {
            foreach (var doc in keepAlive) doc.Dispose();
        }
    }

    /// <summary>把输入里的文本片段按"选课服务 / 排课扁平表 / 校历"归类。</summary>
    private static Classified Classify(ImportInput input, List<JsonDocument> keepAlive)
    {
        var result = new Classified();

        foreach (var (label, text) in AdapterInput.Texts(input))
        {
            var doc = AdapterInput.TryParseJson(text);
            if (doc is null) continue;
            keepAlive.Add(doc);
            var raw = AdapterInput.UnwrapData(doc.RootElement);

            // 1) 选课服务：{ data: { calendarId, selectedCourses: [...] } }
            var selected = AdapterInput.AsArray(Prop(raw, "selectedCourses"));
            if (selected is not null && selected.Value.GetArrayLength() > 0)
            {
                // 先并入累积列表、再取 calendarId（container 优先），最后遍历**整个累积列表**
                // 找课程自带的 calendarId/calendarName —— 与 TS 的顺序逐条一致。
                foreach (var entry in selected.Value.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object) result.Selected.Add(entry);
                }

                result.CalendarId ??= ToStr(Prop(raw, "calendarId"));
                foreach (var entry in result.Selected)
                {
                    var course = Prop(entry, "course");
                    result.CalendarId ??= ToStr(Prop(course, "calendarId"));
                    result.CalendarName ??= ToStr(Prop(course, "calendarName"));
                }

                result.Sources.Add($"{label}:已选课程 {selected.Value.GetArrayLength()} 门");
                continue;
            }

            // 2) 报表服务：课表页真正调的那条接口。本科生
            //    `GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&studentCode=…`
            //    返回 `data: [课程…]`，每门课带 `timeTableList[]`（与选课服务的 `times[]` 同义）；
            //    研究生 `findSchoolTimetab2` 按前端源码是 `data.list`，同一套字段，这里一并认。
            var reportItems = CollectReportItems(raw);
            if (reportItems.Count > 0)
            {
                result.Report.AddRange(reportItems);
                result.Sources.Add($"{label}:报表课表 {reportItems.Count} 门");
                continue;
            }

            var list = AdapterInput.AsArray(raw);
            if (list is null) continue;

            var objects = list.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList();
            var scheduleItems = objects.Where(item => Has(item, "weekState") && Has(item, "dayOfWeek")).ToList();
            if (scheduleItems.Count > 0)
            {
                result.Flat.AddRange(scheduleItems);
                result.Sources.Add($"{label}:课表 {scheduleItems.Count} 条");
                continue;
            }

            var calendarTerms = objects
                .Where(item => Has(item, "noWeekendWorkTimes") || (Has(item, "beginDay") && Has(item, "weekNum")))
                .ToList();
            if (calendarTerms.Count > 0)
            {
                result.Calendar.AddRange(calendarTerms);
                result.Sources.Add($"{label}:校历 {calendarTerms.Count} 个学期");
            }
        }

        return result;
    }

    /// <summary>
    /// 从报表服务的响应里挑出"带排课时段的课程项"。
    ///
    /// <para>认两种容器：<c>data</c> 直接是数组（本科 <c>findStudentTimetab</c>，已实测），
    /// 或 <c>data.list</c>（研究生 <c>findSchoolTimetab2</c>，按前端源码推断，未实测）。
    /// 判据是元素里有没有 <c>timeTableList</c> 数组 —— 它把报表格式与排课服务的扁平表区分开。</para>
    /// </summary>
    private static List<JsonElement> CollectReportItems(JsonElement raw)
    {
        var list = AdapterInput.AsArray(raw) ?? AdapterInput.AsArray(Prop(raw, "list"));
        if (list is null) return [];

        return
        [
            .. list.Value.EnumerateArray().Where(item =>
                item.ValueKind == JsonValueKind.Object
                && AdapterInput.AsArray(Prop(item, "timeTableList")) is not null),
        ];
    }

    /// <summary>自动探测：像不像可识别的同济课表数据。</summary>
    private static double DetectScore(ImportInput input)
    {
        foreach (var (_, text) in AdapterInput.Texts(input))
        {
            if (!text.Contains("selectedCourses", StringComparison.Ordinal) &&
                !text.Contains("weekState", StringComparison.Ordinal) &&
                !text.Contains("timeTableList", StringComparison.Ordinal)) continue;

            using var doc = AdapterInput.TryParseJson(text);
            if (doc is null) continue;
            var raw = AdapterInput.UnwrapData(doc.RootElement);

            // 注意：用"是不是数组"而不是"长度" —— 空数组也算"识别成功"，
            // 好让解析器给出"已识别但没有课表数据"这类更有用的诊断，而不是"无法识别格式"。
            if (AdapterInput.AsArray(Prop(raw, "selectedCourses")) is not null) return 0.98;
            if (CollectReportItems(raw).Count > 0) return 0.97;

            var list = AdapterInput.AsArray(raw);
            if (list is not null &&
                list.Value.EnumerateArray().Any(item => Has(item, "weekState") && Has(item, "dayOfWeek"))) return 0.9;
        }

        return 0;
    }

    private static List<Slot> ToSlotList(JsonElement term)
    {
        var raw = AdapterInput.AsArray(Prop(term, "noWeekendWorkTimes")) ??
                  AdapterInput.AsArray(Prop(term, "weekendWorkTimes"));
        var slots = new List<Slot>();
        foreach (var entry in Elements(raw))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var index = ToInt(Prop(entry, "classNode"));
            var begin = Prop(entry, "beginTime");
            var end = Prop(entry, "endTime");
            if (index is null || index <= 0) continue;
            slots.Add(new Slot(
                index.Value,
                begin.ValueKind == JsonValueKind.String ? begin.GetString()! : "",
                end.ValueKind == JsonValueKind.String ? end.GetString()! : ""));
        }

        return slots.Count > 0 ? [.. TimetableDefaults.SlotsFromList(slots)] : [.. TimetableDefaults.MakeDefaultSlots()];
    }

    private static string? ResolveStartDate(JsonElement term)
    {
        // beginDay 是毫秒时间戳（如 1789315200000），必须用 long 接：int 会溢出成"缺少 beginDay"。
        var beginDay = ToLong(Prop(term, "beginDay"));
        if (beginDay is null) return null;

        string iso;
        try
        {
            iso = Time.MsToIsoDate(beginDay.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // TS 那边越界会得到 Invalid Date（"NaN-NaN-NaN"）；移植版按"缺少 beginDay"降级更安全。
            return null;
        }

        var monday = Time.MondayOf(iso);
        if (monday is null) return null;
        var teachingWeekStart = ToInt(Prop(term, "teachingWeekStart")) ?? 1;
        return teachingWeekStart > 1 ? Time.AddDays(monday, (teachingWeekStart - 1) * 7) : monday;
    }

    private static JsonElement? PickTerm(IReadOnlyList<JsonElement> calendar, string? termId)
    {
        if (calendar.Count == 0) return null;

        if (termId is not null)
        {
            var wanted = termId;
            foreach (var term in calendar)
            {
                if (TermKey(term) == wanted) return term;
            }
        }

        foreach (var term in calendar)
        {
            var current = Prop(term, "currentTermFlag");
            if (current.ValueKind == JsonValueKind.True) return term;
        }

        foreach (var term in calendar)
        {
            var next = Prop(term, "nextTermFlag");
            if (next.ValueKind == JsonValueKind.True) return term;
        }

        return calendar[0];
    }

    private static string TermKey(JsonElement term)
    {
        var id = Prop(term, "id");
        var numeric = ToInt(id);
        return numeric is not null ? numeric.Value.ToString(CultureInfo.InvariantCulture) : JsText(id);
    }

    /// <summary>学期解析优先级：显式校历 JSON &gt; 内置学期表（按 calendarId）&gt; 默认 16 周。</summary>
    private static Term BuildTerm(
        JsonElement? calendarTerm,
        string? calendarId,
        string? calendarName,
        List<Diagnostic> diagnostics)
    {
        if (calendarTerm is not null)
        {
            var term = calendarTerm.Value;
            var startDate = ResolveStartDate(term);
            var teachingWeekEnd = ToInt(Prop(term, "teachingWeekEnd"));
            if (startDate is null)
            {
                diagnostics.Add(AdapterInput.Diagnostic(DiagnosticLevel.Warn, "tongji.term.startDate", "校历缺少 beginDay，无法计算当前教学周。"));
            }

            var id = ToInt(Prop(term, "id"));
            var fullName = Prop(term, "fullName");
            return new Term(
                id is not null ? id.Value.ToString(CultureInfo.InvariantCulture) : "unknown",
                ToStr(fullName) ?? "",
                ToInt(Prop(term, "year")) ?? DateTime.Now.Year,
                ToInt(Prop(term, "term")) ?? 1,
                teachingWeekEnd is > 0 ? teachingWeekEnd.Value : ToInt(Prop(term, "weekNum")) ?? 16,
                ToSlotList(term),
                startDate);
        }

        var preset = TongjiTerms.FindPreset(calendarId);
        if (preset is not null) return TongjiTerms.TermFromPreset(preset);

        diagnostics.Add(AdapterInput.Diagnostic(
            DiagnosticLevel.Warn,
            "tongji.term.unknown",
            $"学期 {calendarId ?? "未知"} 不在内置学期表里：已按 16 教学周 + 内置节次时间解析，挂件不会显示\"当前第几周\"（可在设置里手动填开学日期）。"));

        return new Term(
            calendarId ?? "unknown",
            calendarName ?? "",
            DateTime.Now.Year,
            1,
            16,
            TimetableDefaults.MakeDefaultSlots());
    }

    /// <summary>
    /// 课程序列排序：TS 用 <c>localeCompare(name, 'zh-Hans-CN') || localeCompare(id)</c>。
    ///
    /// C# 侧项目开了 <c>InvariantGlobalization</c>，拿不到 ICU 的拼音排序，这里退化为不变文化比较
    /// （顺序可能与 TS 略有不同，但所有断言都按课程名检索，不受影响）。
    /// </summary>
    private static List<Course> SortCourses(List<Course> courses) =>
    [
        .. courses
            .OrderBy(course => course.Name, StringComparer.InvariantCulture)
            .ThenBy(course => course.Id, StringComparer.InvariantCulture),
    ];

    // ── JSON 取值小工具（TS 侧每个适配器各有一份 toInt/toStr，这里镜像同样的组织方式）────

    /// <summary>按名字取整数（字符串数字也接受）：用于节次/星期/掩码等小整数。</summary>
    private static int? ToInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            var truncated = Math.Truncate(number);
            if (truncated is > int.MaxValue or < int.MinValue) return null;
            return (int)truncated;
        }

        if (value.ValueKind != JsonValueKind.String) return null;
        var text = (value.GetString() ?? "").Trim();
        if (!IntegerRe.IsMatch(text)) return null;
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// 按名字取 64 位整数：<c>beginDay</c> 这类**毫秒时间戳**远超 int 范围，必须走这条。
    /// </summary>
    private static long? ToLong(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer)) return integer;
            if (value.TryGetDouble(out var number) && double.IsFinite(number))
            {
                var truncated = Math.Truncate(number);
                if (truncated is > long.MaxValue or < long.MinValue) return null;
                return (long)truncated;
            }
        }

        if (value.ValueKind != JsonValueKind.String) return null;
        var text = (value.GetString() ?? "").Trim();
        if (!IntegerRe.IsMatch(text)) return null;
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? ToStr(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return value.ValueKind == JsonValueKind.Number ? value.ToString() : null;
    }

    private static string JsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        _ => value.ToString(),
    };

    private static JsonElement Prop(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    private static bool Has(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _);

    private static IEnumerable<JsonElement> Elements(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}
