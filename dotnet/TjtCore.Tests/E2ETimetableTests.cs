using Tjt.Core;
using Tjt.Core.Adapters;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 端到端黄金验收：真实的个人课表响应（选课服务 <c>getDataBk</c>）→ 导入 → 落成课表。
///
/// 逐条对齐 TS 侧 <c>packages/core/test/e2e-timetable.spec.ts</c>。fixture 来自一次真实抓包，
/// 已脱敏（剔除学生姓名/学号与无关字段），由 csproj 复制到 <c>AppContext.BaseDirectory/fixtures</c>。
///
/// **阶段一**（本文件当前内容）：只做不依赖 <c>Time</c> / <c>Layout</c> 的断言。
/// **阶段二**（等布局/时间模块落地后补齐，见文件末尾待办）：导入 → <c>BuildBoard</c> →
/// <c>TermWeekAt</c> / <c>SessionsOnDate</c> → 单双周过滤。
/// </summary>
public class E2ETimetableTests
{
    private static readonly string PersonalJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tongji-2026-1-personal.json"));

    private static readonly string CalendarJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tongji-school-calendar.json"));

    /// <summary>对应 TS 里 describe 顶部那一次 <c>importTimetable({...})</c>（只跑一次）。</summary>
    private static readonly ImportResult Personal = ImportPipeline.ImportTimetable(new ImportInput
    {
        Text = PersonalJson,
        ImportedAt = "2026-09-01T00:00:00.000Z",
    });

    // ── 端到端：个人课表（selectedCourses 格式）──────────────────────────────────────

    [Fact]
    public void 自动探测到同济个人课表适配器()
    {
        Assert.Equal("tongji-student", Personal.AdapterId);
        Assert.Contains(Personal.Diagnostics, d => d.Code == "tongji.personal" && d.Level == DiagnosticLevel.Info);
        Assert.Contains(Personal.Diagnostics, d => d.Code == "tongji.summary" && d.Level == DiagnosticLevel.Info);
    }

    [Fact]
    public void 学期来自calendarId与内置学期表()
    {
        Assert.Equal("122", Personal.Term.Id);
        Assert.Equal("2026-2027学年第1学期", Personal.Term.Name);
        Assert.Equal("2026-09-14", Personal.Term.StartDate);
        Assert.Equal(16, Personal.Term.TotalWeeks);
        Assert.Equal(11, Personal.Term.Slots.Count);
        Assert.Equal(new Slot(1, "08:00", "08:45"), Personal.Term.Slots[0]);
        Assert.Equal(new Slot(11, "20:10", "20:55"), Personal.Term.Slots[10]);
        Assert.Equal("122", Personal.Meta!["calendarId"] as string);
        // 走内置学期表 → 不该有"缺少 beginDay"或"未知学期"警告
        Assert.DoesNotContain(Personal.Diagnostics, d => d.Code == "tongji.term.unknown");
        Assert.DoesNotContain(Personal.Diagnostics, d => d.Code == "tongji.term.startDate");
    }

    [Fact]
    public void 没有排课时段的课程军训被跳过并给出提示()
    {
        var noSchedule = Personal.Diagnostics.FirstOrDefault(d => d.Code == "tongji.noSchedule");
        Assert.NotNull(noSchedule);
        Assert.Contains("1 门课", noSchedule!.Message, StringComparison.Ordinal);
        Assert.Equal(14, Personal.Courses.Count);
        Assert.DoesNotContain(Personal.Courses, c => c.Name == "军训");
        // 被跳过的课程不该有 Teacher/Session 残留
        Assert.All(Personal.Courses, course => Assert.NotEmpty(course.Sessions));
    }

    [Fact]
    public void 课程字段映射正确()
    {
        var physics = Personal.Courses.FirstOrDefault(c => c.Name == "大学物理B2(I)");
        Assert.NotNull(physics);
        Assert.Equal("50002810095", physics!.CourseCode);
        Assert.Equal("5000281009505", physics.TeachingClassCode);
        Assert.Equal(["欧凯(21158)"], physics.Teachers);

        var session = physics.Sessions.FirstOrDefault(s => s.Day == Weekday.Thursday && s.StartSlot == 5);
        Assert.NotNull(session);
        Assert.Equal(6, session!.EndSlot);
        Assert.Equal("南201", session.Room);
        // 响应里 weeks 是数组 [1,3,5,...]，适配器转成掩码
        Assert.Equal(Weeks.FromWeeks([1, 3, 5, 7, 9, 11, 13, 15]), session.Weeks);
    }

    [Fact]
    public void 多时段课程合并成一门课()
    {
        var math = Personal.Courses.FirstOrDefault(c => c.Name.Contains("高等数学", StringComparison.Ordinal));
        Assert.NotNull(math);
        Assert.True(math!.Sessions.Count > 1);
        // sessions 按天、节次排序
        Assert.Equal(
            math.Sessions.OrderBy(s => (int)s.Day).ThenBy(s => s.StartSlot).Select(s => s.Id),
            math.Sessions.Select(s => s.Id));
    }

    [Fact]
    public void 同一格不同周次不同老师的多条times合并为一块()
    {
        var intro = Personal.Courses.FirstOrDefault(c => c.Name.Contains("专业导论", StringComparison.Ordinal));
        Assert.NotNull(intro);
        // 原始响应里是 9 条（weeks 分别为 [10]/[11]/[9]/... 与一条 1-4+13-16），合并后应为 1 块
        Assert.Single(intro!.Sessions);
        var session = intro.Sessions[0];
        Assert.Equal(Weekday.Wednesday, session.Day);
        Assert.Equal(9, session.StartSlot);
        Assert.Equal("北201", session.Room);
        // 周次取并集 → 1-16 周全周
        Assert.Equal(Weeks.FromWeeks(Enumerable.Range(1, 16)), session.Weeks);
        // 九位授课老师都要保留
        Assert.Equal(9, intro.Teachers.Count);
    }

    [Fact]
    public void 没有教室的课room为空而不是0()
    {
        var pe = Personal.Courses.FirstOrDefault(c => c.Name == "体育(1)");
        Assert.NotNull(pe);
        Assert.Null(pe!.Sessions[0].Room);
    }

    [Fact]
    public void 落成课表保留全部课程与来源()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);
        Assert.Equal(14, timetable.Courses.Count);
        Assert.Equal("tongji-student", timetable.Source.AdapterId);
        Assert.Equal("3.0.0", timetable.Source.AdapterVersion);
        Assert.Equal(Personal.Term.Id, timetable.Term.Id);
        Assert.Equal(TimetableModel.SchemaVersion, timetable.SchemaVersion);
    }

    [Fact]
    public void 校历fixture的毫秒时间戳与节次表()
    {
        // 校历 fixture 是真实抓包：{code, msg, data:[学期1, 学期2]}，122 学期 currentTermFlag=true。
        // beginDay = 1789315200000（北京时间午夜）→ 2026-09-14（周一）；weekBenginDay=2 表示周一起周。
        var imported = ImportPipeline.ImportTimetable(new ImportInput
        {
            Files =
            [
                new ImportFile("tongji-school-calendar.json", CalendarJson),
                new ImportFile("tongji-2026-1-personal.json", PersonalJson),
            ],
        });

        Assert.Equal("122", imported.Term.Id);
        Assert.Equal("2026-2027学年第1学期", imported.Term.Name);
        Assert.Equal("2026-09-14", imported.Term.StartDate);
        Assert.Equal(16, imported.Term.TotalWeeks);
        Assert.Equal(11, imported.Term.Slots.Count);
        Assert.Equal(new Slot(1, "08:00", "08:45"), imported.Term.Slots[0]);
        Assert.Equal(14, imported.Courses.Count);
        Assert.DoesNotContain(imported.Diagnostics, d => d.Code == "tongji.term.startDate");
        var sources = Assert.IsType<string[]>(imported.Meta!["sources"]);
        Assert.Contains(sources, source => source.Contains("校历 2 个学期", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Contains("已选课程", StringComparison.Ordinal));
    }

    // ── 兼容与降级 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 排课服务扁平格式weekState掩码仍可解析()
    {
        var flat = """
        {"code": 200, "data": [
          {"teachingClassId": 111, "code": "X01", "courseCode": "X", "courseName": "大学化学",
           "dayOfWeek": 2, "timeStart": 3, "timeEnd": 4, "weekState": 65535, "roomName": "南202",
           "value": "大学化学 李四(22334) 星期二3-4节 [1-16] 南202"}
        ]}
        """;
        var imported = ImportPipeline.ImportTimetable(new ImportInput { Text = flat });
        Assert.Equal("tongji-student", imported.AdapterId);
        Assert.Single(imported.Courses);
        Assert.Equal("南202", imported.Courses[0].Sessions[0].Room);
        Assert.Equal(["李四(22334)"], imported.Courses[0].Teachers);
        Assert.Equal(Weeks.FullMask(16), imported.Courses[0].Sessions[0].Weeks);
        Assert.Contains(imported.Diagnostics, d => d.Code == "tongji.flat");
    }

    [Fact]
    public void 未知calendarId时退化为默认16周并提示()
    {
        var unknown = """
        {"code": 200, "data": {"calendarId": 999999, "selectedCourses": [
          {"course": {"courseName": "某课程", "courseCode": "Z", "teachClassId": 1, "teachClassCode": "Z01",
           "times": [{"dayOfWeek": 1, "timeStart": 1, "timeEnd": 2, "weeks": [1, 2, 3], "roomIdI18n": "A101"}]}}
        ]}}
        """;
        var imported = ImportPipeline.ImportTimetable(new ImportInput { Text = unknown });
        Assert.Single(imported.Courses);
        Assert.Null(imported.Term.StartDate);
        Assert.Equal(16, imported.Term.TotalWeeks);
        Assert.Contains(imported.Diagnostics, d => d.Code == "tongji.term.unknown" && d.Level == DiagnosticLevel.Warn);
        Assert.Equal(Weeks.FromWeeks([1, 2, 3]), imported.Courses[0].Sessions[0].Weeks);
    }

    [Fact]
    public void 完全不含课表数据的输入报错()
    {
        var empty = """{"code": 200, "data": {"selectedCourses": []}}""";
        var imported = ImportPipeline.ImportTimetable(new ImportInput { Text = empty });
        Assert.Contains(imported.Diagnostics, d => d.Code == "tongji.schedule.missing" && d.Level == DiagnosticLevel.Error);
        Assert.Empty(imported.Courses);
        Assert.Equal("tongji-student", imported.AdapterId);
    }

    // ── 阶段二待办（依赖 Time / Layout，等模块落地后补）────────────────────────────
    //
    // 1. 落成课表 → BuildBoard(TrimEmptySlots: true)：
    //    board.Blocks.Count == Σcourses[].Sessions.Count、board.CurrentWeek == 1、board.Days.Count == 7。
    // 2. Time.TermWeekAt(term, "2026-09-14") == 1、Time.TermWeekAt(term, "2026-08-31") == null。
    // 3. Time.SessionsOnDate(courses, term, "2026-09-14").Length > 0。
    // 4. 单双周过滤：BuildBoard(WeekFilter: Even).Blocks.Count < BuildBoard(默认).Blocks.Count。
    // 5. 并排：BuildBoard().Blocks 全部 ColCount >= 1（专业导论合并后仍是单块）。
}
