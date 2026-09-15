using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 布局的移植验收：逐条对齐 TS 侧 <c>packages/core/test/layout.spec.ts</c> 的期望值，
/// 并额外钉死"正在上的节次"、学期前日期、周末课被隐藏等边界。
///
/// 这些断言与真实校历数据（2026-2027 学年第 1 学期）绑定，移植过程中任何一处
/// 节次收窄 / 并排分组 / 周次过滤的语义偏差都会在这里暴露。
///
/// 注意：课程字段刻意不叫 <c>Math</c>（那会遮蔽 <see cref="System.Math"/>，让 <c>Math.Floor</c> 无法解析）。
/// </summary>
public class LayoutTests
{
    private static readonly Term TestTerm = new(
        Id: "122",
        Name: "2026-2027学年第1学期",
        Year: 2026,
        TermNo: 1,
        TotalWeeks: 16,
        Slots: TimetableDefaults.TongjiSlots.Select(s => s with { }).ToArray(),
        StartDate: "2026-09-14");

    private static readonly uint All = Weeks.FullMask(16);
    private static readonly uint Odd = Weeks.OddMask(16);
    private static readonly uint Even = Weeks.EvenMask(16);

    private static readonly Course MathCourse = new(
        Id: "m1",
        Name: "高等数学（工科类）",
        Teachers: ["张三(12345)"],
        Sessions: [new Session("s1", Weekday.Monday, 1, 2, All, "南101")],
        CourseCode: "M1");

    private static readonly Course PhysicsCourse = new(
        Id: "p1",
        Name: "大学物理",
        Teachers: [],
        Sessions:
        [
            new Session("s2", Weekday.Monday, 1, 2, Odd, "北201"),
            new Session("s3", Weekday.Wednesday, 7, 8, All, "北202"),
        ],
        CourseCode: "P1");

    private static readonly Course EnglishCourse = new(
        Id: "e1",
        Name: "大学英语",
        Teachers: [],
        Sessions: [new Session("s4", Weekday.Monday, 1, 2, Even, "一教101")],
        CourseCode: "E1");

    [Fact]
    public void 默认参数_七列且节次范围覆盖到11节()
    {
        var board = Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { Today = "2026-09-14" });
        Assert.Equal(
            new[] { Weekday.Monday, Weekday.Tuesday, Weekday.Wednesday, Weekday.Thursday, Weekday.Friday, Weekday.Saturday, Weekday.Sunday },
            board.Days.Select(d => d.Day).ToArray());
        Assert.Equal(Enumerable.Range(1, 11).ToArray(), board.Rows.Select(r => r.Index).ToArray());
        Assert.Single(board.Blocks);
        Assert.Equal<int?>(1, board.CurrentWeek);
        Assert.True(board.Days[0].IsToday);
    }

    [Fact]
    public void 隐藏周末()
    {
        var board = Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { ShowWeekend = false, Today = "2026-09-14" });
        Assert.Equal(
            new[] { Weekday.Monday, Weekday.Tuesday, Weekday.Wednesday, Weekday.Thursday, Weekday.Friday },
            board.Days.Select(d => d.Day).ToArray());
    }

    [Fact]
    public void trimEmptySlots收窄到有课的节次()
    {
        var board = Layout.BuildBoard([PhysicsCourse], TestTerm, new BoardOptions { TrimEmptySlots = true, Today = "2026-09-14" });
        Assert.Equal(Enumerable.Range(1, 8).ToArray(), board.Rows.Select(r => r.Index).ToArray());
    }

    [Fact]
    public void 同格多课并排并标记special()
    {
        var board = Layout.BuildBoard([MathCourse, PhysicsCourse, EnglishCourse], TestTerm, new BoardOptions { Today = "2026-09-14" });
        var cell = board.Blocks.Where(b => b.Day == Weekday.Monday && b.StartSlot == 1).ToList();
        Assert.Equal(3, cell.Count);
        Assert.Equal(new[] { 0, 1, 2 }, cell.Select(b => b.Col).OrderBy(c => c).ToArray());
        Assert.All(cell, b =>
        {
            Assert.Equal(3, b.ColCount);
            Assert.True(b.Stacked);
        });

        var mathBlock = cell.Single(b => b.CourseId == "m1");
        var physicsBlock = cell.Single(b => b.CourseId == "p1");
        Assert.False(mathBlock.Special); // 1-16 周 → 全周
        Assert.Equal("1-16", mathBlock.WeeksLabel);
        Assert.True(physicsBlock.Special); // 单周 → 条纹
        Assert.Equal("1, 3, 5, 7, 9, 11, 13, 15", physicsBlock.WeeksLabel);
    }

    [Fact]
    public void 周次过滤只保留命中周次的块并统计隐藏数()
    {
        var odd = Layout.BuildBoard(
            [MathCourse, PhysicsCourse, EnglishCourse],
            TestTerm,
            new BoardOptions { WeekFilter = WeekFilter.Odd, Today = "2026-09-14" });
        var oddIds = odd.Blocks.Select(b => b.CourseId).ToHashSet();
        Assert.DoesNotContain("e1", oddIds); // 双周课被过滤
        Assert.Equal(1, odd.HiddenSessions); // 仅 e1 的那一块被过滤
        Assert.Contains(odd.Blocks, b => b.CourseId == "p1" && b.Day == Weekday.Wednesday);
    }

    [Fact]
    public void 双周过滤同样只统计被丢弃的时段()
    {
        var even = Layout.BuildBoard(
            [MathCourse, PhysicsCourse, EnglishCourse],
            TestTerm,
            new BoardOptions { WeekFilter = WeekFilter.Even, Today = "2026-09-14" });
        var evenIds = even.Blocks.Select(b => b.CourseId).ToHashSet();
        Assert.Contains("e1", evenIds);
        // 物理周三那块是全周（与双周有交集）→ 保留；周一那块是单周 → 被过滤
        Assert.Contains(even.Blocks, b => b.CourseId == "p1" && b.Day == Weekday.Wednesday);
        Assert.DoesNotContain(even.Blocks, b => b.CourseId == "p1" && b.Day == Weekday.Monday);
        Assert.Equal(1, even.HiddenSessions);
    }

    [Fact]
    public void 课程名压缩去掉括号后缀()
    {
        Assert.Equal("高等数学", Layout.DefaultShortName("高等数学（工科类）"));
        Assert.Equal("体育", Layout.DefaultShortName("体育(1)"));
        Assert.Equal("大学物理", Layout.DefaultShortName("大学物理"));
        // 压缩后为空时退回原名（TS 的 `|| name`）
        Assert.Equal("（）", Layout.DefaultShortName("（）"));
        // 两段括号都被吞掉后变空 → 同样退回原名
        Assert.Equal("（a）（b）", Layout.DefaultShortName("（a）（b）"));
        Assert.Equal("  ", Layout.DefaultShortName("  "));
        // 后缀不在末尾时不处理（两端都是 end-anchored 正则）
        Assert.Equal("（前缀）课程", Layout.DefaultShortName("（前缀）课程"));
        Assert.Equal("课程（", Layout.DefaultShortName("课程（"));
        // 末尾空白属于后缀的一部分，会被一起去掉
        Assert.Equal("A", Layout.DefaultShortName("A（B）   "));
    }

    [Fact]
    public void 几何计算_自适应列宽与色块矩形()
    {
        var board = Layout.BuildBoard([MathCourse, PhysicsCourse, EnglishCourse], TestTerm, new BoardOptions { Today = "2026-09-14" });
        var geo = Layout.FitGeometry(1000, board.Rows.Count, board.Days.Count);
        Assert.Equal(Math.Floor((1000 - Layout.DefaultGeometry.GutterWidth) / 7), geo.CellWidth);

        var size = Layout.BoardSize(board, geo);
        Assert.Equal(geo.GutterWidth + (geo.CellWidth * 7), size.Width);

        var block = board.Blocks.Single(b => b.CourseId == "m1");
        var rect = Layout.BlockRect(board, block, geo);
        Assert.True(rect.Left >= geo.GutterWidth);
        Assert.True(rect.Width > 0);
        Assert.Equal((2 * geo.RowHeight) - 2, rect.Height);
    }

    [Fact]
    public void 并排色块按1比n均分列宽()
    {
        var board = Layout.BuildBoard([MathCourse, PhysicsCourse, EnglishCourse], TestTerm, new BoardOptions { Today = "2026-09-14" });
        var geo = Layout.FitGeometry(1000, board.Rows.Count, board.Days.Count);
        var cell = board.Blocks
            .Where(b => b.Day == Weekday.Monday && b.StartSlot == 1)
            .OrderBy(b => b.Col)
            .ToList();
        var rects = cell.Select(b => Layout.BlockRect(board, b, geo)).ToArray();

        // cellWidth = floor((1000 - 64) / 7) = 133 → 整列留 6px、三块均分 = 42.33，每块再收 2px
        Assert.All(rects, r =>
        {
            Assert.Equal(40.333d, r.Width, 3);
            Assert.Equal(102d, r.Height);
            Assert.Equal(29d, r.Top);
        });
        Assert.Equal(new[] { 67d, 109.333d, 151.667d }, rects.Select(r => Math.Round(r.Left, 3)).ToArray());
    }

    [Fact]
    public void 列宽不低于下限且可用宽度不足时取最小值()
    {
        // 可用宽度 100：usable = 36，36/7 = 5 < 64 → 取 64（不是 5，也不是负数）
        Assert.Equal(64d, Layout.FitGeometry(100, 11, 7).CellWidth);
        // 可用宽度小于 gutter：usable 被 Math.max(0, …) 夹到 0，仍然不会出现负列宽
        Assert.Equal(64d, Layout.FitGeometry(10, 11, 7).CellWidth);
        // cols = 0 时不做除法，直接用基座列宽（TS 的 cols > 0 分支）
        Assert.Equal(Layout.DefaultGeometry.CellWidth, Layout.FitGeometry(1000, 0, 0).CellWidth);
        Assert.Equal(0, Layout.FitGeometry(1000, 0, 0).Cols);
    }

    [Fact]
    public void 当前节次按闭区间判定()
    {
        Assert.Null(Layout.CurrentSlotIndex(TestTerm));
        Assert.Equal<int?>(1, Layout.CurrentSlotIndex(TestTerm, 8 * 60)); // 08:00 起点
        Assert.Equal<int?>(1, Layout.CurrentSlotIndex(TestTerm, (8 * 60) + 45)); // 08:45 终点也算"正在上"
        Assert.Equal<int?>(2, Layout.CurrentSlotIndex(TestTerm, (9 * 60) + 35)); // 09:35
        Assert.Null(Layout.CurrentSlotIndex(TestTerm, (9 * 60) + 36)); // 09:36 课间
        Assert.Null(Layout.CurrentSlotIndex(TestTerm, 23 * 60)); // 晚间无课
    }

    [Fact]
    public void 学期开始前判定为假期且今日高亮只看星期()
    {
        var board = Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { Today = "2026-09-07" });
        Assert.Null(board.CurrentWeek); // 开学前：教学周为 null
        // 但 isToday 在 TS 里只看星期（`day === todayWeekday`），与教学周无关：
        // 2026-09-07 是周一，所以周一照样高亮，其余各天不高亮。
        var today = Assert.Single(board.Days, d => d.IsToday);
        Assert.Equal(Weekday.Monday, today.Day);
    }

    [Fact]
    public void 隐藏周末时周末的课被丢弃但不计入过滤数()
    {
        var weekend = new Course(
            Id: "w1",
            Name: "周末实践",
            Teachers: [],
            Sessions: [new Session("sw", Weekday.Saturday, 3, 4, All, "实验楼")]);
        var board = Layout.BuildBoard([weekend], TestTerm, new BoardOptions { ShowWeekend = false, Today = "2026-09-14" });
        Assert.Empty(board.Blocks);
        Assert.Equal(0, board.HiddenSessions);
    }

    [Fact]
    public void 自定义取色与课程名压缩生效()
    {
        var board = Layout.BuildBoard(
            [MathCourse],
            TestTerm,
            new BoardOptions
            {
                Today = "2026-09-14",
                ColorOf = _ => "#123456",
                ShortName = name => name[..2],
            });
        var block = Assert.Single(board.Blocks);
        Assert.Equal("#123456", block.Color);
        Assert.Equal("#123456cc", block.Fill);
        Assert.Equal("高等", block.ShortName);
    }

    [Fact]
    public void 课程自带颜色优先于取色函数()
    {
        // TS：course.color ?? colorOf(course) —— 空串不是 nullish，会照原样使用
        var painted = MathCourse with { Color = "#000000" };
        var board = Layout.BuildBoard([painted], TestTerm, new BoardOptions { Today = "2026-09-14" });
        Assert.Equal("#000000", Assert.Single(board.Blocks).Color);

        var blank = MathCourse with { Color = "" };
        var blankBoard = Layout.BuildBoard([blank], TestTerm, new BoardOptions { Today = "2026-09-14" });
        Assert.Equal(string.Empty, Assert.Single(blankBoard.Blocks).Color);
    }

    [Fact]
    public void 节次范围可被首尾节次覆盖()
    {
        var board = Layout.BuildBoard(
            [MathCourse],
            TestTerm,
            new BoardOptions { FirstSlot = 3, LastSlot = 5, Today = "2026-09-14" });
        Assert.Equal(new[] { 3, 4, 5 }, board.Rows.Select(r => r.Index).ToArray());
        // 第 1-2 节的课仍在 blocks 里（TS 不按行范围过滤块，只影响行）
        Assert.Single(board.Blocks);
        Assert.Equal("10:00", board.Rows[0].Begin);
        Assert.Equal("14:15", board.Rows[^1].End);
    }

    [Fact]
    public void 没有任何可见课程时trimEmptySlots仍给默认节次范围()
    {
        // slotNumbers 为空 → minSlot 走 1，maxSlot 走 Math.max(11, 0) = 11
        // （TS：`...(slotNumbers.length ? slotNumbers : [0])`）
        var board = Layout.BuildBoard([], TestTerm, new BoardOptions { TrimEmptySlots = true, Today = "2026-09-14" });
        Assert.Equal(Enumerable.Range(1, 11).ToArray(), board.Rows.Select(r => r.Index).ToArray());
        Assert.Empty(board.Blocks);
        Assert.Equal(0, board.HiddenSessions);
    }

    [Fact]
    public void 空周次掩码的时段总是被隐藏()
    {
        var empty = new Course("z1", "无周次课", [], [new Session("sz", Weekday.Monday, 1, 2, 0u, "南101")]);
        var board = Layout.BuildBoard([empty], TestTerm, new BoardOptions { Today = "2026-09-14" });
        Assert.Empty(board.Blocks);
        Assert.Equal(1, board.HiddenSessions);
    }

    [Fact]
    public void 找不到天或行时色块按第0位摆放()
    {
        var board = Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { ShowWeekend = false, Today = "2026-09-14" });
        var geo = Layout.FitGeometry(1000, board.Rows.Count, board.Days.Count);
        // 手工构造一个"周六"的块：周被隐藏 → findIndex = -1 → 按 0 处理（TS 的 dayIndex < 0 ? 0）
        var ghost = Assert.Single(board.Blocks) with { Day = Weekday.Saturday };
        var rect = Layout.BlockRect(board, ghost, geo);
        Assert.Equal(geo.GutterWidth + 3, rect.Left);
        Assert.Equal(geo.HeaderHeight + 1, rect.Top);
    }

    [Fact]
    public void 不传today时取系统日期()
    {
        // buildBoard 内部等价于 TS 的 localTodayIso()：忽略 options.Now/TzOffsetMinutes，用系统时间 + 北京时区
        var board = Layout.BuildBoard([MathCourse], TestTerm);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", board.Today);
    }

    [Fact]
    public void 当前时刻标记正在上的节次()
    {
        // 2026-09-14T00:30:00Z + 北京时区 = 08:30 → 落在第 1 节（08:00-08:45）
        var beijing = Layout.BuildBoard(
            [MathCourse],
            TestTerm,
            new BoardOptions
            {
                Today = "2026-09-14",
                Now = new DateTimeOffset(2026, 9, 14, 0, 30, 0, TimeSpan.Zero),
            });
        Assert.Equal(new[] { 1 }, beijing.Rows.Where(r => r.IsCurrent).Select(r => r.Index).ToArray());

        // 同一时刻换 UTC+0：00:30 不在任何节次内 → 没有任何行标记"正在上"
        var utc = Layout.BuildBoard(
            [MathCourse],
            TestTerm,
            new BoardOptions
            {
                Today = "2026-09-14",
                Now = new DateTimeOffset(2026, 9, 14, 0, 30, 0, TimeSpan.Zero),
                TzOffsetMinutes = 0,
            });
        Assert.DoesNotContain(utc.Rows, r => r.IsCurrent);
    }

    [Fact]
    public void 节次表缺失时间时跳过该节()
    {
        var sparse = TestTerm with
        {
            Slots = [new Slot(1, string.Empty, string.Empty), new Slot(2, "09:00", "09:45")],
        };
        // 第 1 节没有时间 → 跳过；09:10 落在第 2 节
        Assert.Equal<int?>(2, Layout.CurrentSlotIndex(sparse, (9 * 60) + 10));
        Assert.Null(Layout.CurrentSlotIndex(sparse, 8 * 60));
    }

    // ── 时间推算复用 Time（见 Layout 类文档）─────────────────────────────────────

    [Fact]
    public void 当前周与开学日判定复用Time的语义()
    {
        // 直接对齐 Time.TermWeekAt：同一组输入在布局与时间模块里必须给出同一个答案。
        // 这里只覆盖"开学日 + 开学前"两极：往后的周次推算由 TimeTests 铺满。
        // （不测"学期结束后"——那需要构造 TotalWeeks 比实际跨度小的学期，属于 Time 的职责。）
        foreach (var iso in new[] { "2026-08-31", "2026-09-07", "2026-09-14", "2026-09-20", "2026-09-21" })
        {
            var expected = Time.TermWeekAt(TestTerm, iso);
            Assert.Equal(expected, Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { Today = iso }).CurrentWeek);
        }

        // 开学前的周日（09-13）与开学当天同属"第 1 周之前"
        Assert.Null(Time.TermWeekAt(TestTerm, "2026-09-13"));
        // 开学日 2026-09-14 正是第 1 周周一
        Assert.Equal(1, Time.TermWeekAt(TestTerm, "2026-09-14"));
    }

    [Fact]
    public void 今日高亮按星期复用Time的解析()
    {
        // 2026-09-16 是周三 → 只有周三被标今日（与教学周无关）
        var board = Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { Today = "2026-09-16" });
        Assert.Equal(Weekday.Wednesday, Assert.Single(board.Days, d => d.IsToday).Day);
    }

    [Fact]
    public void 畸形与越界日期不会让布局抛异常()
    {
        // 语义差异点：Time.IsoToDayNumber 对越界日期抛 ArgumentOutOfRangeException，
        // 但布局把异常收敛成 null  —— 等价 TS 侧拿到 NaN 后的表现，绝不能因为一个坏日期整个崩掉。
        foreach (var bad in new[] { "not-a-date", "", "2026-9-14", "9999-99-99" })
        {
            var board = Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { Today = bad });
            Assert.Null(board.CurrentWeek);
            Assert.DoesNotContain(board.Days, d => d.IsToday);
            // 课表内容本身照常渲染（今日解析失败不影响色块）
            Assert.Single(board.Blocks);
        }
    }

    [Fact]
    public void 同一日历日在两端给出同一个答案()
    {
        // 布局的"今日/星期"与 Time 的公开 API 必须逐条一致（含闰年 2 月）
        foreach (var iso in new[] { "2026-09-14", "2026-02-29", "2024-02-29", "1999-12-31", "2026-01-04" })
        {
            var board = Layout.BuildBoard([MathCourse], TestTerm, new BoardOptions { Today = iso });
            Assert.Equal(Time.IsoToWeekday(iso), board.Days.SingleOrDefault(d => d.IsToday)?.Day);
            Assert.Equal(Time.TermWeekAt(TestTerm, iso), board.CurrentWeek);
        }
    }
}
