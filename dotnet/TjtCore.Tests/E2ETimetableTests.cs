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
/// **阶段一**：不依赖 <c>Time</c> / <c>Layout</c> 的断言（导入 → 学期 → 课程字段 → 落成课表）。
/// **阶段二**：导入 → <c>MaterializeTimetable</c> → <c>BuildBoard</c> → <c>TermWeekAt</c> /
/// <c>SessionsOnDate</c> → 单双周过滤 → 同格并排（含 <c>WeeksLabel</c> / <c>Special</c> / 几何矩形）。
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
        Assert.Equal("3.1.0", timetable.Source.AdapterVersion);
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

    // ── 阶段二：导入 → 布局 → 时间 → 过滤（依赖 Time / Layout）─────────────────────

    [Fact]
    public void 落成课表到网格布局到当日课程与教学周()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);
        var sessionTotal = timetable.Courses.Sum(course => course.Sessions.Count);

        var board = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions
        {
            // 显式「全部周次」：这条走的是"导入 → 布局"整条链路，不是周次视图
            WeekView = WeekView.All,
            Today = "2026-09-16",
            TrimEmptySlots = true,
        });

        // 每个上课时段都落成一个色块（fixture 里没有 weeks=0 的脏数据，也没有被过滤掉的块）
        Assert.Equal(sessionTotal, board.Blocks.Count);
        Assert.Equal(0, board.HiddenSessions);
        Assert.Equal(14, timetable.Courses.Count);
        Assert.Equal("tongji-student", timetable.Source.AdapterId);

        // 2026-09-16 是第 1 周三 → 当前第 1 教学周
        Assert.Equal(1, board.CurrentWeek!.Value);
        Assert.Equal("2026-09-16", board.Today);
        Assert.Equal(7, board.Days.Count);   // 默认展示全周

        // TrimEmptySlots：行范围收窄到实际有课的节次
        Assert.Equal(board.Blocks.Min(block => block.StartSlot), board.Rows[0].Index);
        Assert.Equal(board.Blocks.Max(block => block.EndSlot), board.Rows[^1].Index);

        // 时间推算：开学日 2026-09-14 是第 1 周周一，8-31 在开学前
        Assert.Equal(1, Time.TermWeekAt(timetable.Term, "2026-09-14")!.Value);
        Assert.Null(Time.TermWeekAt(timetable.Term, "2026-08-31"));

        var monday = Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-09-14");
        Assert.True(monday.Length > 0);
        Assert.All(monday, occurrence => Assert.Equal(Weekday.Monday, occurrence.Session.Day));
        Assert.All(monday, occurrence => Assert.Equal(1, occurrence.Week));
        Assert.Contains(timetable.Courses, course => ReferenceEquals(course, monday[0].Course));
        // 开学前那天没有任何课
        Assert.Empty(Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-08-31"));
    }

    [Fact]
    public void 单双周过滤生效()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);
        var all = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions { WeekView = WeekView.All, Today = "2026-09-16" });
        var even = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions
        {
            Today = "2026-09-16",
            WeekView = WeekView.Even,
        });

        Assert.True(even.Blocks.Count < all.Blocks.Count);
        Assert.Equal(0, all.HiddenSessions);
        // 被隐藏的时段数 = 总时段数 - 双周仍显示的块数（且至少隐藏了单周课）
        Assert.Equal(all.Blocks.Count - even.Blocks.Count, even.HiddenSessions);
        Assert.True(even.HiddenSessions > 0);
        // 双周视图里不该出现"只在单周上课"的块
        Assert.All(even.Blocks, block => Assert.True((block.Weeks & Weeks.EvenMask(timetable.Term.TotalWeeks)) != 0));
        // 全周上课的块（专业导论）在两种过滤下都在
        Assert.Contains(all.Blocks, block => !block.Special);
        Assert.Contains(even.Blocks, block => !block.Special);
    }

    [Fact]
    public void 同一格多门课并排且矩形不重叠()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);
        var board = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions { WeekView = WeekView.All, Today = "2026-09-16" });
        var geometry = Layout.FitGeometry(1280, board.Rows.Count, board.Days.Count);

        Assert.All(board.Blocks, block => Assert.True(block.ColCount >= 1));

        foreach (var group in board.Blocks.GroupBy(block => (block.Day, block.StartSlot, block.EndSlot)))
        {
            // 同格的并排块共享同一个 colCount，col 取 0..n-1
            Assert.Single(group.Select(block => block.ColCount).Distinct());
            Assert.Equal(group.Count(), group.First().ColCount);
            Assert.Equal(
                Enumerable.Range(0, group.Count()),
                group.Select(block => block.Col).OrderBy(col => col));
            Assert.All(group, block => Assert.Equal(group.Count() > 1, block.Stacked));

            var rects = group.Select(block => Layout.BlockRect(board, block, geometry)).ToList();
            for (var i = 0; i < rects.Count; i += 1)
            {
                Assert.True(rects[i].Width > 0);
                Assert.True(rects[i].Height > 0);
                for (var j = i + 1; j < rects.Count; j += 1)
                {
                    var disjoint = rects[i].Left + rects[i].Width <= rects[j].Left ||
                                   rects[j].Left + rects[j].Width <= rects[i].Left;
                    Assert.True(disjoint, $"同格并排块 {i}/{j} 的矩形重叠");
                }
            }
        }
    }

    [Fact]
    public void 布局块的周次标签与全周标记()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);
        var board = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions { WeekView = WeekView.All, Today = "2026-09-16" });

        Assert.All(board.Blocks, block =>
        {
            Assert.Equal(Weeks.FormatLabel(block.Weeks, timetable.Term.TotalWeeks), block.WeeksLabel);
            Assert.Equal(!Weeks.IsAll(block.Weeks, timetable.Term.TotalWeeks), block.Special);
        });

        // 大学物理B2(I) 单周上课 → 条纹特殊块；专业导论并集后是全周 → 普通块
        var physics = board.Blocks.First(block => block.Name == "大学物理B2(I)");
        Assert.True(physics.Special);
        Assert.Equal("1, 3, 5, 7, 9, 11, 13, 15", physics.WeeksLabel);
        Assert.Equal(Weeks.OddMask(16), physics.Weeks);

        var intro = board.Blocks.First(block => block.Name.Contains("专业导论", StringComparison.Ordinal));
        Assert.False(intro.Special);
        Assert.Equal("1-16", intro.WeeksLabel);
        Assert.Equal(Weeks.FullMask(16), intro.Weeks);
        Assert.Equal("北201", intro.Room);
        Assert.Equal(9, intro.Teachers.Count);
    }

    [Fact]
    public void 布局尺寸与色块矩形()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);
        var board = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions { WeekView = WeekView.All, Today = "2026-09-16" });
        var geometry = Layout.FitGeometry(1280, board.Rows.Count, board.Days.Count);

        Assert.Equal(board.Days.Count, geometry.Cols);
        Assert.Equal(board.Rows.Count, geometry.Rows);
        Assert.Equal(7, geometry.Cols);

        var size = Layout.BoardSize(board, geometry);
        Assert.Equal(geometry.GutterWidth + (geometry.CellWidth * board.Days.Count), size.Width, 6);
        Assert.Equal(geometry.HeaderHeight + (geometry.RowHeight * board.Rows.Count), size.Height, 6);

        // 每个块都落在自己那一列里（列宽不越界）
        var lastDayRight = geometry.GutterWidth + (geometry.CellWidth * board.Days.Count);
        Assert.All(board.Blocks, block =>
        {
            var rect = Layout.BlockRect(board, block, geometry);
            Assert.True(rect.Left >= geometry.GutterWidth);
            Assert.True(rect.Left + rect.Width <= lastDayRight);
            Assert.True(rect.Top >= geometry.HeaderHeight);
        });
    }

    // ── 阶段三：周次视图（2026-09-21 定稿；权威验收日期 = 第 2 周）────────────────────

    /// <summary>
    /// 真实个人课表上的「只看本周」：权威验收日期 <c>--today 2026-09-21</c>（第 2 周周一）。
    ///
    /// <para>这些数字就是 Windows 冒烟与 <c>.tools/verify-weekview.ps1</c> 的基准 —— 改 fixture /
    /// 改过滤语义都会在这里和那边同时暴露。</para>
    /// </summary>
    [Fact]
    public void 本周视图在真实个人课表上的块数与隐藏数()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);

        var all = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions
        {
            WeekView = WeekView.All,
            Today = "2026-09-21",
        });
        var current = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions
        {
            Today = "2026-09-21", // 默认就是 Current（只看本周）
        });

        Assert.Equal(2, current.CurrentWeek);
        Assert.Equal(19, all.Blocks.Count);      // 黄金基准：全部 19 条
        Assert.Equal(14, current.Blocks.Count);  // 第 2 周：14 条
        Assert.Equal(5, current.HiddenSessions);
        Assert.Equal(all.Blocks.Count - current.Blocks.Count, current.HiddenSessions);
        Assert.Equal(0, all.HiddenSessions);

        // 本周视图里每一块都必须真的覆盖第 2 周（不过滤的那部分原样保留）
        Assert.All(current.Blocks, block => Assert.True((block.Weeks & Weeks.WeekMask(2)) != 0));

        // 今日（周一，第 2 周）2 条：与 SessionsOnDate 一致
        Assert.Equal(2, current.TodaySessionCount);
        Assert.Equal(
            Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-09-21").Length,
            current.TodaySessionCount);

        // 换个周（第 3 周，2026-09-28）块数与第 2 周不同 → 真的在跟着周走
        var week3 = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions
        {
            Today = "2026-09-28",
        });
        Assert.Equal(3, week3.CurrentWeek);
        Assert.NotEqual(current.Blocks.Count, week3.Blocks.Count);
    }

    [Fact]
    public void 本周视图在假期静默退回全部周次()
    {
        var timetable = ImportPipeline.MaterializeTimetable(Personal);

        var holiday = Layout.BuildBoard(timetable.Courses, timetable.Term, new BoardOptions
        {
            Today = "2026-09-07", // 开学前一周
        });

        Assert.Null(holiday.CurrentWeek);
        Assert.Equal(19, holiday.Blocks.Count); // 绝不空：退回显示全部
        Assert.Equal(0, holiday.HiddenSessions);
        // 假期的今日节数退回"今天有几条安排"（2026-09-07 也是周一）
        Assert.Equal(
            Time.SessionsOnDate(timetable.Courses, timetable.Term, "2026-09-14").Length,
            holiday.TodaySessionCount);
    }
}
