using Tjt.Core;
using Tjt.Core.Adapters;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 同济**课表页报表接口**格式的验收（2026-09-16 补）。
///
/// <para>数据源是课表页真正调的那条接口：
/// <c>GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&amp;studentCode=…</c>
/// （研究生是 <c>findSchoolTimetab2</c>），响应是 <c>data: [课程…]</c>，每门课带 <c>timeTableList[]</c>。
/// 它和选课服务的 <c>data.selectedCourses[].course.times[]</c> 是**同一套语义、不同包法**：
/// <c>dayOfWeek</c> / <c>timeStart</c> / <c>timeEnd</c> / <c>weeks</c> 数组含义完全一致，
/// 只是课程在数组顶层、排课数组改名、教室多了一层 <c>roomLable</c>。</para>
///
/// <para>fixture 来自一次真实抓包并已脱敏（教师姓名与工号全部替换为「教师X(1xxxx)」，
/// 教学班 id 改为 9xxxxxxxxxxxxxxx；课程名与教室保留 —— 它们不指向个人）。</para>
///
/// <para><b>学期从哪来</b>：报表响应体里没有 <c>calendarId</c>（它只在请求 URL 上），
/// 所以抓取时要把 URL 里的 <c>calendarId</c> 通过 <see cref="ImportInput.TermId"/> 传进来，
/// 才能命中内置学期表拿到开学日期 —— 这正是下面两个用例的差别。</para>
/// </summary>
public class TongjiReportFormatTests
{
    private static readonly string ReportJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tongji-2026-1-report.json"));

    private static ImportResult Import(string? termId = null) => ImportPipeline.ImportTimetable(new ImportInput
    {
        Text = ReportJson,
        TermId = termId,
        ImportedAt = "2026-09-15T00:00:00.000Z",
    });

    private static Course Find(ImportResult result, string name) =>
        result.Courses.Single(course => course.Name == name);

    [Fact]
    public void 报表格式被识别为同济课表并跳过没有时段的课程()
    {
        var result = Import("122");

        Assert.Equal("tongji-student", result.AdapterId);
        Assert.Contains(result.Diagnostics, d => d.Code == "tongji.report" && d.Level == DiagnosticLevel.Info);
        Assert.Contains(result.Diagnostics, d => d.Code == "tongji.noSchedule" && d.Message.Contains("1 门", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == "tongji.summary");

        // fixture 里 15 门课、27 条 timeTableList；「军训」没有排课时段被跳过（-1 门），
        // 「专业导论」在同一格的 9 条按周次换老师被合并成 1 块（-8 条）→ 14 门 / 19 条。
        // （19 这个数正好和选课服务 getDataBk 那条路得到的条数一致 —— 两条接口同源。）
        Assert.Equal(14, result.Courses.Count);
        Assert.Equal(19, result.Courses.Sum(course => course.Sessions.Count));
        Assert.DoesNotContain(result.Courses, course => course.Name == "军训");
    }

    [Fact]
    public void 课程字段教室教师与校区按实测规则映射()
    {
        var result = Import("122");

        // 教室：roomIdI18n 优先（"北301"），空则退 roomLable（"线上课堂"）
        var linear = Find(result, "线性代数B");
        Assert.Equal("9000000000000001", linear.Id);
        Assert.Equal("12201006", linear.TeachingClassCode);
        Assert.Equal("002137", Find(result, "社会实践").CourseCode);
        Assert.Equal(["教师M(10008)"], linear.Teachers);
        Assert.Equal("四平路校区", linear.Campus);
        Assert.Equal(["北301", "北301"], linear.Sessions.OrderBy(s => (int)s.Day).Select(s => s.Room ?? "").ToArray());

        var practice = Find(result, "社会实践");
        Assert.Equal("线上课堂", practice.Sessions[0].Room);
        Assert.Equal(["教师W(10013)"], practice.Teachers);

        // 只有 roomLable 的场地（操场）也要拿到可读名字
        Assert.Equal("爱校路足球场（2号）", Find(result, "体育(1)").Sessions[0].Room);
    }

    [Fact]
    public void 周次数组落成掩码且同格多教师被合并()
    {
        var result = Import("122");

        // weeks 是绝对周次数组（不是掩码），落成 bit0 = 第 1 周
        var physics = Find(result, "大学物理B2(I)");
        var odd = physics.Sessions.Single(session => session.Day == Weekday.Thursday);
        Assert.Equal(Weeks.OddMask(16), odd.Weeks);

        var policy = Find(result, "形势与政策(1)");
        Assert.Equal(Weeks.FromWeeks([11, 12, 13, 14]), policy.Sessions[0].Weeks);

        var nutrition = Find(result, "运动营养与健康");
        Assert.Equal(Weeks.FromWeeks(Enumerable.Range(1, 12)), nutrition.Sessions[0].Weeks);

        // 「专业导论」同一格 9 条 timeTableList（按周次换老师）→ 合并成 1 块、周次取并集、教师按出现顺序
        var intro = Find(result, "专业导论（计算机与电子类）");
        var session = Assert.Single(intro.Sessions);
        Assert.Equal(Weekday.Wednesday, session.Day);
        Assert.Equal(9, session.StartSlot);
        Assert.Equal(10, session.EndSlot);
        Assert.Equal(Weeks.FullMask(16), session.Weeks);
        Assert.Equal(9, intro.Teachers.Count);
        Assert.Equal("教师G(10005)", intro.Teachers[0]);
        Assert.Contains("教师Z(10025)", intro.Teachers);
    }

    [Fact]
    public void 带上请求URL里的calendarId就能命中内置学期表()
    {
        var withTerm = Import("122");
        Assert.Equal("122", withTerm.Term.Id);
        Assert.Equal("2026-2027学年第1学期", withTerm.Term.Name);
        Assert.Equal("2026-09-14", withTerm.Term.StartDate);
        Assert.Equal(16, withTerm.Term.TotalWeeks);
        Assert.Equal(11, withTerm.Term.Slots.Count);
        Assert.Equal("08:00", withTerm.Term.SlotBegin(1));
        Assert.DoesNotContain(withTerm.Diagnostics, d => d.Code == "tongji.term.unknown");
    }

    [Fact]
    public void 没带calendarId时退化成16周并给出可读的诊断()
    {
        var without = Import();

        Assert.Null(without.Term.StartDate);
        Assert.Equal(16, without.Term.TotalWeeks);
        Assert.Contains(without.Diagnostics, d => d.Code == "tongji.term.unknown" && d.Level == DiagnosticLevel.Warn);
        Assert.Equal(14, without.Courses.Count); // 课表本身照常解析
    }

    [Fact]
    public void 报表格式能一路走到布局()
    {
        var result = Import("122");
        var timetable = ImportPipeline.MaterializeTimetable(result);
        // 显式「全部周次」+ 显式日期：这条钉的是"报表格式能一路走到布局"（19 块），
        // 不该随"今天是第几周"漂 —— 凡断言观感就必须显式给 --today 的同一条纪律。
        var board = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions
        {
            WeekView = WeekView.All,
            Today = "2026-09-21",
            TrimEmptySlots = true,
        });

        Assert.Equal(7, board.Days.Count);
        Assert.True(board.Rows.Count > 0);
        Assert.Equal(19, board.Blocks.Count);
    }
}
