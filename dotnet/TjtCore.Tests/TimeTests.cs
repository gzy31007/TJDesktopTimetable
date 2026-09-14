using System.Globalization;
using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 时间推算的移植验收：逐条对齐 TS 侧 <c>packages/core/test/time.spec.ts</c> 的 7 个用例
/// （同名同参数），并补上 TS 测试没覆盖到的纯函数（<see cref="Time.ToMinutes"/>、
/// <see cref="Time.MinutesToClock"/>、<see cref="Time.DescribeCountdown(int, bool)"/>、
/// <see cref="Time.SessionWeeks"/>、<see cref="Time.TodaysSessions"/>）。
///
/// 所有 <see cref="DateTimeOffset"/> 都用显式的 <c>Z</c> 字面量构造，断言因此与宿主时区无关 ——
/// 这正是移植的第一约束：东八区是「固定 +08:00 的纯日期算术」，不是「跑在什么时区上」。
/// </summary>
public class TimeTests
{
    // ── 与 TS 测试同参数的夹具（注意：静态字段按声明顺序初始化，掩码必须排在建课之前） ──

    private static readonly uint All = Weeks.FromWeeks(Enumerable.Range(1, 16));
    private static readonly uint Odd = Weeks.FromWeeks([1, 3, 5, 7, 9, 11, 13, 15]);

    /// <summary>2026-2027 学年第 1 学期，第 1 周周一 = 2026-09-14，共 16 周。</summary>
    private static readonly Term Term1 = new(
        "122",
        "2026-2027学年第1学期",
        2026,
        1,
        16,
        [.. TimetableDefaults.TongjiSlots.Select(s => s with { })],
        "2026-09-14");

    private static readonly Course MathCourse = new(
        "c1",
        "高等数学",
        ["张三(12345)"],
        [new Session("s1", Weekday.Monday, 1, 2, All, "南101")],
        CourseCode: "M1");

    private static readonly Course PhysicsCourse = new(
        "c2",
        "大学物理",
        [],
        [new Session("s2", Weekday.Wednesday, 5, 6, Odd)],
        CourseCode: "P1");

    /// <summary>构造绝对时刻：显式 <c>Z</c>，不受宿主时区影响（对应 TS 的 <c>new Date('…Z')</c>）。</summary>
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    [Fact]
    public void 纯日期算术不受时区影响()
    {
        Assert.Equal("2026-09-14", Time.DayNumberToIso(Time.IsoToDayNumber("2026-09-14")!.Value));
        Assert.Equal(7, Time.IsoToDayNumber("2026-09-21")!.Value - Time.IsoToDayNumber("2026-09-14")!.Value);
        // TS 断言的是 Number.isNaN(...)；C# 的可空返回把 NaN 换成了 null
        Assert.Null(Time.IsoToDayNumber("not-a-date"));
        Assert.Equal("2026-09-21", Time.AddDays("2026-09-14", 7));
        Assert.Equal("2026-09-14", Time.MondayOf("2026-09-16"));
        Assert.Equal("2026-09-14", Time.MondayOf("2026-09-20")); // 周日仍属本周
        Assert.Equal(Weekday.Monday, Time.IsoToWeekday("2026-09-14"));
        Assert.Equal(Weekday.Sunday, Time.IsoToWeekday("2026-09-20"));
        Assert.Equal(Weekday.Saturday, Time.DayNumberToWeekday(Time.IsoToDayNumber("2026-09-19")!.Value));
    }

    [Fact]
    public void 校历毫秒时间戳按北京时间换算_2026_09_14_00_00_CST()
    {
        Assert.Equal("2026-09-14", Time.MsToIsoDate(1789315200000));
    }

    [Fact]
    public void localTodayIso使用东八区()
    {
        // 2026-09-13 16:30 UTC = 2026-09-14 00:30 CST
        var instant = At("2026-09-13T16:30:00Z");
        Assert.Equal("2026-09-14", Time.LocalTodayIso(instant));
        Assert.Equal("2026-09-13", Time.LocalTodayIso(instant, 0));
    }

    [Fact]
    public void 教学周推算与边界()
    {
        Assert.Null(Time.TermWeekAt(Term1, "2026-09-07")); // 开学前
        Assert.Equal(1, Time.TermWeekAt(Term1, "2026-09-14"));
        Assert.Equal(1, Time.TermWeekAt(Term1, "2026-09-20"));
        Assert.Equal(2, Time.TermWeekAt(Term1, "2026-09-21"));
        Assert.Equal(16, Time.TermWeekAt(Term1, "2027-01-03"));
        Assert.Null(Time.TermWeekAt(Term1, "2027-01-11")); // 学期结束
        Assert.Null(Time.TermWeekAt(Term1 with { StartDate = null }, "2026-09-14"));
    }

    [Fact]
    public void 按日期取当天课程_含周次过滤()
    {
        var monday = Time.SessionsOnDate([MathCourse, PhysicsCourse], Term1, "2026-09-14");
        Assert.Equal(["c1"], monday.Select(o => o.Course.Id));

        // 第 3 周周三（单周）有课；第 4 周周三（双周）没有
        Assert.Single(Time.SessionsOnDate([PhysicsCourse], Term1, "2026-09-30"));
        Assert.Empty(Time.SessionsOnDate([PhysicsCourse], Term1, "2026-10-07"));
    }

    [Fact]
    public void 下一节课_含正在上课与跨天()
    {
        var inClass = Time.NextSession([MathCourse], Term1, At("2026-09-14T00:10:00Z")); // 08:10 CST
        Assert.True(inClass!.InProgress);
        Assert.Equal("正在上课", Time.DescribeCountdown(inClass));

        var before = Time.NextSession([MathCourse], Term1, At("2026-09-14T00:00:00Z")); // 08:00 CST 整
        Assert.True(before!.InProgress);

        var earlyMorning = Time.NextSession([MathCourse], Term1, At("2026-09-13T22:00:00Z")); // 06:00 CST
        Assert.Equal(120, earlyMorning!.MinutesUntil);
        Assert.Equal("2 小时后", Time.DescribeCountdown(earlyMorning));

        var nextWeek = Time.NextSession([MathCourse], Term1, At("2026-09-14T04:00:00Z")); // 周一 12:00 CST
        Assert.Equal("2026-09-21", nextWeek!.Date);
        Assert.False(nextWeek.InProgress);
        // 跨天时 minutesUntil 含整天数 × 1440（TS 的 `+ offset * 1440`）：08:00 - 12:00 + 7 天
        Assert.Equal(9840, nextWeek.MinutesUntil);
    }

    [Fact]
    public void 头部摘要()
    {
        var summary = Time.SummarizeNow(Term1, At("2026-09-30T02:00:00Z"));
        Assert.Equal(3, summary.Week);
        Assert.Equal("2026-09-30", summary.Date);
        Assert.Contains("第 3 周", summary.Label);
        Assert.Contains("周三", summary.Label);
        // 与 TS 的模板串逐字符一致（含全角间隔号两侧的空格）
        Assert.Equal("2026-2027学年第1学期 · 第 3 周 · 周三", summary.Label);
    }

    // ── 以下为 TS spec 未覆盖、但同属 time.ts 公开契约的分支 ──

    [Fact]
    public void 解析失败统一映射为null而不是NaN()
    {
        Assert.Null(Time.IsoToDayNumber("2026-9-14")); // 月必须是定长两位
        Assert.Null(Time.IsoToDayNumber(""));
        Assert.Null(Time.IsoToWeekday("bad"));
        Assert.Null(Time.MondayOf("bad"));
        Assert.Null(Time.AddDays("bad", 1));
        Assert.Null(Time.TermWeekAt(Term1, "bad"));
        Assert.Empty(Time.SessionsOnDate([MathCourse], Term1, "bad"));
        // 前缀匹配：尾随字符被忽略（与 TS 正则无 $ 锚点一致）
        Assert.Equal(Time.IsoToDayNumber("2026-09-14"), Time.IsoToDayNumber("  2026-09-14T00:00:00+08:00 "));
    }

    [Fact]
    public void 日期进位规则与JS的Date_UTC一致()
    {
        // 月/日越界自动进位（MakeDay 语义）：2026-02-30 ≡ 2026-03-02
        Assert.Equal(Time.IsoToDayNumber("2026-03-02"), Time.IsoToDayNumber("2026-02-30"));
        // 月份越界进位：2026-13-01 ≡ 2027-01-01
        Assert.Equal(Time.IsoToDayNumber("2027-01-01"), Time.IsoToDayNumber("2026-13-01"));
        // 0..99 的年份按 1900 + y 解释
        Assert.Equal(Time.IsoToDayNumber("1999-01-01"), Time.IsoToDayNumber("0099-01-01"));
    }

    [Fact]
    public void toMinutes与minutesToClock()
    {
        Assert.Equal(480, Time.ToMinutes("08:00"));
        Assert.Equal(480, Time.ToMinutes(" 8:00 ")); // 小时可一位 + 两侧空白被 trim
        Assert.Equal(1439, Time.ToMinutes("23:59"));
        Assert.Equal(480, Time.ToMinutes("08:00:00")); // 前缀匹配，秒被忽略
        Assert.Null(Time.ToMinutes("24:00"));
        Assert.Null(Time.ToMinutes("08:60"));
        Assert.Null(Time.ToMinutes("8:0"));
        Assert.Null(Time.ToMinutes("abc"));
        Assert.Null(Time.ToMinutes(""));

        Assert.Equal("00:00", Time.MinutesToClock(0));
        Assert.Equal("23:59", Time.MinutesToClock(1439));
        Assert.Equal("00:00", Time.MinutesToClock(1440));
        Assert.Equal("23:59", Time.MinutesToClock(-1));
    }

    [Fact]
    public void describeCountdown各分支()
    {
        Assert.Equal("正在上课", Time.DescribeCountdown(0, true));
        Assert.Equal("即将开始", Time.DescribeCountdown(0, false));
        Assert.Equal("即将开始", Time.DescribeCountdown(-5, false));
        Assert.Equal("23 分钟后", Time.DescribeCountdown(23, false));
        Assert.Equal("1 小时后", Time.DescribeCountdown(60, false));
        Assert.Equal("1 小时 5 分钟后", Time.DescribeCountdown(65, false));
        Assert.Equal("2 小时后", Time.DescribeCountdown(120, false));
    }

    [Fact]
    public void 今天哪些课与从课表取周次()
    {
        // 2026-09-14 08:10 CST（周一，第 1 周）
        var today = Time.TodaysSessions([MathCourse, PhysicsCourse], Term1, At("2026-09-14T00:10:00Z"));
        Assert.Equal(["c1"], today.Select(o => o.Course.Id));
        Assert.Equal(1, today[0].Week);

        // 周日（2026-09-20）没课；假期（开学前）也没课
        Assert.Empty(Time.TodaysSessions([MathCourse], Term1, At("2026-09-20T02:00:00Z")));
        Assert.Empty(Time.TodaysSessions([MathCourse], Term1, At("2026-09-07T02:00:00Z")));

        Assert.Equal(Enumerable.Range(1, 16), Time.SessionWeeks(MathCourse.Sessions[0], Term1));
        Assert.Equal([1, 3, 5, 7, 9, 11, 13, 15], Time.SessionWeeks(PhysicsCourse.Sessions[0], Term1));
    }

    [Fact]
    public void 同节次的两门课按课名稳定排序()
    {
        var a = MathCourse with { Id = "z", Name = "AAA" };
        var b = MathCourse with { Id = "a", Name = "ZZZ" };
        // 起始节次相同 → 次键课名（Ordinal：ASCII 下与 localeCompare 同序）
        Assert.Equal(["z", "a"], Time.SessionsOnDate([b, a], Term1, "2026-09-14").Select(o => o.Course.Id));
    }

    [Fact]
    public void localMinutesOfDay按东八区()
    {
        // 2026-09-13 16:30 UTC = 2026-09-14 00:30 CST → 30 分钟
        Assert.Equal(30, Time.LocalMinutesOfDay(At("2026-09-13T16:30:00Z")));
        Assert.Equal(990, Time.LocalMinutesOfDay(At("2026-09-13T16:30:00Z"), 0)); // 16:30 UTC
        Assert.Equal(490, Time.LocalMinutesOfDay(At("2026-09-14T00:10:00Z"))); // 08:10 CST
    }

    [Fact]
    public void 没有未来课时返回当天的兜底候选或null()
    {
        // 第 3 周周三 12:00 CST：当天的课（第 5-6 节 13:30 开始）还没开始 → 就是它
        var atNoon = Time.NextSession([PhysicsCourse], Term1, At("2026-09-30T04:00:00Z"));
        Assert.Equal("2026-09-30", atNoon!.Date);
        Assert.False(atNoon.InProgress);
        Assert.Equal(90, atNoon.MinutesUntil);

        // 第 4 周周三没有课 → 顺延到下一节（第 5 周周三 2026-10-14，跨 7 天）
        var nextOddWeek = Time.NextSession([PhysicsCourse], Term1, At("2026-10-07T04:00:00Z"));
        Assert.Equal("2026-10-14", nextOddWeek!.Date);
        Assert.Equal(7 * 1440 + (810 - 720), nextOddWeek.MinutesUntil);

        // 最后一节单周课（第 15 周周三）之后，14 天内再无该课 → null
        Assert.Null(Time.NextSession([PhysicsCourse], Term1, At("2026-12-24T04:00:00Z")));

        // 单周课全部上完后的兜底：第 3 周周三 20:00 CST，14 天内无后续课 → 返回今天那节已结束的课
        var afterClass = Time.NextSession([PhysicsCourse], Term1, At("2026-09-30T12:00:00Z"));
        Assert.Equal("2026-09-30", afterClass!.Date);
        Assert.False(afterClass.InProgress);
        Assert.True(afterClass.MinutesUntil < 0);
        Assert.Equal("即将开始", Time.DescribeCountdown(afterClass));
    }

    [Fact]
    public void 日期超出DateOnly可表示范围时明确抛异常()
    {
        // TS 在极端入参下返回 "NaN-NaN-NaN" / Invalid Date；C# 里改成显式异常（见 Time.cs 类文档）
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.DayNumberToIso(int.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.DayNumberToWeekday(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => Time.IsoToDayNumber("9999-99-99"));
    }
}
