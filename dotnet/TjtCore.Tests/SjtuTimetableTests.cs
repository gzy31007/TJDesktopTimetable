using Tjt.Core;
using Tjt.Core.Adapters;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 上海交通大学「学在交大」课表的黄金验收：真实抓包（已脱敏，见 fixture 说明）→ 导入 → 落成课表。
///
/// <para>两份 fixture 都来自 2026-09-16 的真实抓取（2026-2027 学年第 1 学期）：</para>
/// <list type="bullet">
///   <item><c>sjtu-2026-1-semester.json</c> —— <c>GET /app/stu/lesson/listBySemester</c>，15 条上课安排；</item>
///   <item><c>sjtu-2026-1-calendar.json</c> —— <c>GET /app/stu/school/semester/calendar</c>，154 天（第 0-21 周逐日）。</item>
/// </list>
///
/// <para>两份数据里没有学号 / 姓名 / cookie —— 交大课表接口本身就不返回教师（教师只在
/// <c>lesson/detail</c> 里，我们没调）。课程名与教室是本就不指向个人的信息，
/// 与同济 fixture 保留 <c>courseName</c> 的口径一致。</para>
/// </summary>
public class SjtuTimetableTests
{
    private static readonly string SemesterJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "sjtu-2026-1-semester.json"));

    private static readonly string CalendarJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "sjtu-2026-1-calendar.json"));

    /// <summary>带教务日历的一次导入（抓取侧会把两份一起交进来）。</summary>
    private static ImportResult WithCalendar(string? termId = "2026-2027-1") => ImportPipeline.ImportTimetable(new ImportInput
    {
        Text = SemesterJson,
        Files = [new ImportFile("sjtu-2026-1-calendar.json", CalendarJson)],
        TermId = termId,
        ImportedAt = "2026-09-16T00:00:00.000Z",
    });

    /// <summary>只有课表、没有日历（用户手粘一条 JSON 的样子）。</summary>
    private static ImportResult LessonsOnly() => ImportPipeline.ImportTimetable(new ImportInput
    {
        Text = SemesterJson,
        TermId = "2026-2027-1",
        ImportedAt = "2026-09-16T00:00:00.000Z",
    });

    private static Course Find(ImportResult result, string name) =>
        result.Courses.Single(course => course.Name == name);

    // ── 探针 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void 自动探测到交大适配器()
    {
        var result = WithCalendar();

        Assert.Equal("sjtu-student", result.AdapterId);
        Assert.Equal("上海交通大学 · 学在交大课表", result.AdapterName);
        Assert.Contains(result.Diagnostics, d => d.Code == "sjtu.lessons" && d.Level == DiagnosticLevel.Info);
        Assert.Contains(result.Diagnostics, d => d.Code == "sjtu.summary" && d.Level == DiagnosticLevel.Info);
        // 有日历 → 不该出现"开学日未知"的警告
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "sjtu.term.startDate");
    }

    /// <summary>
    /// 同济与交大都是 0.98，靠特征字段互斥：同济那份 fixture 必须仍归同济适配器。
    /// （注册表同分时先登记的赢，但这条用例守的是"分数别写成一个双方都拿满的粗暴值"。）
    /// </summary>
    [Fact]
    public void 交大适配器不会抢走同济课表()
    {
        var tongji = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tongji-2026-1-report.json"));
        var result = ImportPipeline.ImportTimetable(new ImportInput { Text = tongji });

        Assert.Equal("tongji-student", result.AdapterId);
    }

    // ── 学期 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void 学期来自教务日历_开学日取第一周周一而不是startDay()
    {
        var term = WithCalendar().Term;

        Assert.Equal("2026-2027-1", term.Id);
        Assert.Equal("2026-2027学年第1学期", term.Label);
        Assert.Equal(2026, term.Year);
        Assert.Equal(1, term.TermNo);

        // 日历里 startDay=2026-09-07 是第 0 周（报到周），第 1 周周一才是 2026-09-14 ——
        // listByWeek?week=1 的 detailTime 也是 2026-09-14，两边对得上
        Assert.Equal("2026-09-14", term.StartDate);

        // 日历覆盖第 0-21 周，取最大周次
        Assert.Equal(21, term.TotalWeeks);
    }

    [Fact]
    public void 节次表是交大官方十三节()
    {
        var term = WithCalendar().Term;

        Assert.Equal(13, term.Slots.Count);
        Assert.Equal(new Slot(1, "08:00", "08:45"), term.Slots[0]);
        Assert.Equal(new Slot(6, "12:55", "13:40"), term.Slots[5]);
        Assert.Equal(new Slot(11, "18:00", "18:45"), term.Slots[10]);
        Assert.Equal(new Slot(13, "19:50", "20:20"), term.Slots[12]);
    }

    [Fact]
    public void 学期id不传时从日历的year与semester拼出来()
    {
        Assert.Equal("2026-2027-1", WithCalendar(termId: null).Term.Id);
    }

    [Fact]
    public void 没有日历时给出开学日警告并按课表周次推断总周数()
    {
        var result = LessonsOnly();

        Assert.Equal("sjtu-student", result.AdapterId);
        Assert.Contains(result.Diagnostics, d => d.Code == "sjtu.term.startDate" && d.Level == DiagnosticLevel.Warn);
        Assert.Null(result.Term.StartDate);

        // 课表里最大周次是 16（"1-16周"），兜底取 Math.Max(16, 16)
        Assert.Equal(16, result.Term.TotalWeeks);
        Assert.Equal(11, result.Courses.Count);
    }

    // ── 课程与时段 ───────────────────────────────────────────────────────────────

    [Fact]
    public void 课程按教学班聚合_相邻节次并成一块()
    {
        var result = WithCalendar();

        // 15 条原始记录 → 11 门课；「民法总论」(6-6 + 7-8) 与「宪法（A类）」(6-6 + 7-8) 各并成一块
        Assert.Equal(11, result.Courses.Count);
        Assert.Equal(13, result.Courses.Sum(course => course.Sessions.Count));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "sjtu.noSchedule");

        var civil = Find(result, "民法总论");
        var session = Assert.Single(civil.Sessions);
        Assert.Equal(Weekday.Thursday, session.Day);
        Assert.Equal(6, session.StartSlot);
        Assert.Equal(8, session.EndSlot);

        var constitution = Assert.Single(Find(result, "宪法（A类）").Sessions);
        Assert.Equal(Weekday.Tuesday, constitution.Day);
        Assert.Equal(6, constitution.StartSlot);
        Assert.Equal(8, constitution.EndSlot);

        // 刑法总论两天两段、节次不相邻 → 保持两条，且按天排序
        var criminal = Find(result, "刑法总论");
        Assert.Equal(2, criminal.Sessions.Count);
        Assert.Equal(
            new[] { Weekday.Tuesday, Weekday.Thursday },
            criminal.Sessions.Select(s => s.Day).ToArray());
        Assert.Equal(
            new[] { "9-10", "3-4" },
            criminal.Sessions.Select(s => $"{s.StartSlot}-{s.EndSlot}").ToArray());
    }

    [Fact]
    public void 课程名称教室与教学班代码按实测映射()
    {
        var criminal = Find(WithCalendar(), "刑法总论");

        Assert.Equal("2026202701LAW130501", criminal.Id);
        Assert.Equal("LAW1305", criminal.CourseCode);
        Assert.Equal("(2026-2027-1)-LAW1305-01", criminal.TeachingClassCode);
        Assert.Equal("东中院1-109", criminal.Sessions[0].Room);
        // 交大课表接口不返回教师（教师只在 lesson/detail 里）→ 教师留空
        Assert.Empty(criminal.Teachers);
    }

    // ── 周次 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void 周次文本先展开区间再按单双周过滤()
    {
        var result = WithCalendar();
        var term = result.Term;

        // "5周,9周,13-15周" + suffix ["单周"] → 5 / 9 / 13 / 15
        // （顺序反了会只剩 5/9/13 —— 13-15 得先展开成 13,14,15 才轮到单周筛）
        var policy = Assert.Single(Find(result, "形势与政策").Sessions);
        Assert.Equal(new[] { 5, 9, 13, 15 }, Weeks.ToWeeks(policy.Weeks, term.TotalWeeks));

        Assert.Equal(Enumerable.Range(1, 16).ToArray(), Weeks.ToWeeks(Find(result, "刑法总论").Sessions[0].Weeks, term.TotalWeeks));
        Assert.Equal(Enumerable.Range(1, 8).ToArray(), Weeks.ToWeeks(Find(result, "国家安全教育").Sessions[0].Weeks, term.TotalWeeks));
    }

    [Fact]
    public void 周末课程与周日编号按交大day语义()
    {
        var military = Find(WithCalendar(), "军训");

        // 交大 day=7 是周日（与同济、与 Unix 的 0=周日 都不同）
        Assert.Equal(Weekday.Sunday, Assert.Single(military.Sessions).Day);
        Assert.Equal("不排教室", military.Sessions[0].Room);
    }

    // ── 端到端 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void 导入结果能落成课表并与周次查询对齐()
    {
        var result = WithCalendar();
        var timetable = ImportPipeline.MaterializeTimetable(result);

        Assert.Equal(11, timetable.Courses.Count);
        Assert.Equal("sjtu-student", timetable.Source.AdapterId);
        Assert.Equal("1.0.0", timetable.Source.AdapterVersion);

        // 第 1 周周一（2026-09-14）应当有「法学导论」7-8 节与「思政课（文化）」9-10 节
        var onMonday = Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-09-14");
        Assert.Equal(
            new[] { "思政课（文化）", "法学导论" },
            onMonday.Select(item => item.Course.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        // 第 1 周周二是「综合英语III」1-2 与「刑法总论」9-10（宪法 6-8 也在）
        var onTuesday = Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-09-15");
        Assert.Equal(3, onTuesday.Length);

        // 「形势与政策」只在第 5/9/13/15 周：第 2 周（2026-09-21）那天的周三不该出现它
        var week2Wednesday = Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-09-23");
        Assert.DoesNotContain(week2Wednesday, item => item.Course.Name == "形势与政策");

        // 第 5 周周三（2026-10-14）应当有
        Assert.Equal(5, Time.TermWeekAt(timetable.Term, "2026-10-14")!.Value);
        var week5Wednesday = Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-10-14");
        Assert.Contains(week5Wednesday, item => item.Course.Name == "形势与政策");
    }

    [Fact]
    public void 按周接口的响应没有周次时给出提示并按整学期处理()
    {
        // 按周接口（listByWeek）的条目 time 是 null —— 这正是抓取侧要改写成整学期请求的原因
        var weekly = """
        {"errno":"0","error":"成功","data":[{"name":"刑法总论","code":"(2026-2027-1)-LAW1305-01",
        "jxbId":"2026202701LAW130501","address":"东中院1-109","duration":["9","10"],"repeat":false,
        "suffix":[],"day":"2","dayFormatted":"二","detailTime":"2026-09-15","weekNum":"1","time":null,
        "xqj":null,"credit":"4.0","lessonClassCode":null}]}
        """;

        var result = ImportPipeline.ImportTimetable(new ImportInput { Text = weekly, TermId = "2026-2027-1" });

        Assert.Equal("sjtu-student", result.AdapterId);
        Assert.Contains(result.Diagnostics, d => d.Code == "sjtu.weeks.unknown");
        // `time` 为空 → 按整学期处理（宁可多显示，也不能整门课消失）
        Assert.Equal(Enumerable.Range(1, 16).ToArray(), Weeks.ToWeeks(Assert.Single(result.Courses).Sessions[0].Weeks, 16));
    }

    [Fact]
    public void 课程代码解析覆盖教学班序号缺失的形式()
    {
        Assert.Equal("LAW1305", SjtuStudentAdapter.CourseCodeOf("(2026-2027-1)-LAW1305-01"));
        Assert.Equal("MIL1202", SjtuStudentAdapter.CourseCodeOf("(2026-2027-1)-MIL1202-04"));
        Assert.Equal("PE003", SjtuStudentAdapter.CourseCodeOf("(2026-2027-1)-PE003"));
        Assert.Null(SjtuStudentAdapter.CourseCodeOf(null));
    }
}
