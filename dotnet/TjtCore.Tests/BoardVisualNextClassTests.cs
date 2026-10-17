using System.Globalization;
using Tjt.Core;
using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

public partial class BoardVisualTests
{
    private static BoardVisual NextVisual(Course[] courses, string now, Term? term = null,
        double width = 1000, WeekView view = WeekView.Current, bool weekends = true,
        string? gridToday = null, int tz = 480, double height = 700)
    {
        var instant = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture);
        var board = Layout.BuildBoard(courses, term ?? TestTerm, new BoardOptions
        {
            Now = instant, Today = gridToday ?? Time.LocalTodayIso(instant, tz),
            TzOffsetMinutes = tz, WeekView = view, ShowWeekend = weekends,
        });
        return BoardVisualBuilder.Build(board, width, false,
            Time.LocalMinutesOfDay(instant, tz), availableHeight: height, includeNextClassRow: true);
    }

    private static Course NextCourse(string name = "测试课程", Weekday day = Weekday.Monday,
        int start = 1, uint weeks = 1, string? room = "测试楼101") => new("c", name, [],
            [new Session("s", day, start, start, weeks, room)]);

    [Theory]
    [InlineData("2026-09-14T07:59:59.9999999+08:00", "08:00")]
    [InlineData("2026-09-14T08:00:00+08:00", "10:00")]
    [InlineData("2026-09-14T08:00:00.0000001+08:00", "10:00")]
    [InlineData("2026-09-14T08:30:00+08:00", "10:00")]
    [InlineData("2026-09-14T09:35:00+08:00", "10:00")]
    public void 摘要按完整时刻严格排除已开始及已结束安排(string now, string expected)
    {
        var first = NextCourse();
        var visual = NextVisual([first with { Sessions = [first.Sessions[0] with { EndSlot = 2 },
            new Session("later", Weekday.Monday, 3, 4, 1, "测试楼202")] }], now);
        Assert.Equal(expected, visual.NextClass.StartText);
        Assert.Equal("今", visual.NextClass.Marker);
        Assert.Single(visual.NextClass.Items);
    }

    [Theory]
    [InlineData("2026-08-01T12:00:00+08:00", 1u, "2026-09-14")]
    [InlineData("2026-09-14T12:00:00+08:00", 8u, "2026-10-05")]
    [InlineData("2026-09-20T23:59:59+08:00", 2u, "2026-09-21")]
    public void 摘要跨日跨周及开学前均查完整已知学期(string now, uint weeks, string date)
    {
        var next = NextVisual([NextCourse(weeks: weeks)], now).NextClass;
        Assert.Equal("后", next.Marker);
        Assert.Equal(date, next.DateText);
        Assert.Equal("08:00", next.StartText);
    }

    [Fact]
    public void 同课同刻不同教室均计数且悬停保留完整组()
    {
        var first = NextCourse("同一课程");
        var next = NextVisual([first with { Sessions = [first.Sessions[0],
            first.Sessions[0] with { Id = "s2", Room = "测试楼202" }] },
            NextCourse("另一课程", room: "测试楼303")], "2026-09-14T07:59:59+08:00", width: 320).NextClass;
        Assert.Equal("3门同时开课", next.CountText);
        Assert.Equal(3, next.Group.Sessions.Count);
        Assert.Equal(3, next.Items.Count);
        foreach (var room in new[] { "测试楼101", "测试楼202", "测试楼303" }) Assert.Contains(room, next.Tooltip);
        Assert.Equal(2, next.Items.Count(i => i.Name == "同一课程"));
    }

    [Theory]
    [InlineData(WeekView.All)]
    [InlineData(WeekView.Current)]
    [InlineData(WeekView.Odd)]
    [InlineData(WeekView.Even)]
    public void 摘要不受网格周次筛选隐藏周末和网格定位日期影响(WeekView view)
    {
        var course = NextCourse(day: Weekday.Saturday);
        var next = NextVisual([course], "2026-09-14T09:00:00+08:00", view: view,
            weekends: false, gridToday: "2026-10-12").NextClass;
        Assert.Equal("后", next.Marker);
        Assert.Equal("2026-09-19", next.DateText);
        Assert.Single(next.Items);
    }

    [Fact]
    public void 缺教室不丢候选结束时间缺失也不参与开始判定()
    {
        var next = NextVisual([NextCourse(room: null)], "2026-09-14T07:00:00+08:00",
            TestTerm with { Slots = [new Slot(1, "08:00", "")] }).NextClass;
        Assert.Equal(NextClassStatus.Found, next.Group.Status);
        Assert.Equal("教室未提供", Assert.Single(next.Items).Room);
        Assert.Contains("教室未提供", next.Tooltip);
    }

    [Theory]
    [InlineData("2026-09-15T00:00:00+08:00")]
    [InlineData("2027-03-01T00:00:00+08:00")]
    public void 可靠无未来使用专门空状态且不退回最近结束课程(string now)
    {
        var next = NextVisual([NextCourse()], now).NextClass;
        Assert.Equal(NextClassStatus.NoFuture, next.Group.Status);
        Assert.Equal("本学期无后续课程", next.Message);
        Assert.Null(next.Marker);
        Assert.Empty(next.Items);
    }

    [Fact]
    public void 缺开学日或开始时间返回信息不足而不猜候选()
    {
        var badCalendar = NextVisual([NextCourse()], "2026-09-14T07:00:00+08:00",
            TestTerm with { StartDate = null }).NextClass;
        var badTime = NextVisual([NextCourse()], "2026-09-14T07:00:00+08:00",
            TestTerm with { Slots = [new Slot(1, "", "08:45")] }).NextClass;
        foreach (var next in new[] { badCalendar, badTime })
        {
            Assert.Equal(NextClassStatus.InformationInsufficient, next.Group.Status);
            Assert.Equal("信息不足，无法确定下一次上课", next.Message);
            Assert.Null(next.Marker);
            Assert.Empty(next.Items);
        }
    }

    [Fact]
    public void 空周次不制造候选或信息不足()
    {
        var next = NextVisual([NextCourse(weeks: 0)], "2026-09-14T07:00:00+08:00",
            TestTerm with { StartDate = null, Slots = [] }).NextClass;
        Assert.Equal(NextClassStatus.NoFuture, next.Group.Status);
    }

    [Fact]
    public void 同日午夜未知开始可能属于同刻组不能误报已知候选完整()
    {
        var known = NextCourse(start: 1);
        var unknown = NextCourse("未知开始", start: 2);
        var next = NextVisual([known, unknown], "2026-09-13T23:59:59+08:00",
            TestTerm with { Slots = [new Slot(1, "00:00", ""), new Slot(2, "", "")] }).NextClass;
        Assert.Equal(NextClassStatus.InformationInsufficient, next.Group.Status);
    }

    [Fact]
    public void 完整绝对时刻按课表时区而非传入offset确定今后()
    {
        var next = NextVisual([NextCourse()], "2026-09-13T23:59:59Z").NextClass;
        Assert.Equal("今", next.Marker);
        Assert.Equal("2026-09-14", next.DateText);
    }

    [Fact]
    public void 默认诊断单日期单时分及组合均合成为同一有效时刻()
    {
        var sample = DateTimeOffset.Parse("2026-09-13T23:59:59.1234567Z", CultureInfo.InvariantCulture);
        Assert.Equal(sample, BoardClock.Resolve(sample));
        Assert.Equal("2026-10-01T07:59:59.1234567+08:00", BoardClock.Resolve(sample, "2026-10-01").ToString("O"));
        Assert.Equal("2026-09-14T08:00:00.0000000+08:00", BoardClock.Resolve(sample, nowMinutes: 480).ToString("O"));
        var combined = BoardClock.Resolve(sample, "2026-09-21", 480);
        var visual = NextVisual([NextCourse(weeks: 3)], combined.ToString("O"));
        Assert.Equal(NextClassStatus.NoFuture, visual.NextClass.Group.Status);
        Assert.Equal("2026-09-21", visual.NextClass.Group.LocalToday);
        Assert.NotNull(visual.NowLineTop);
        Assert.Throws<ArgumentOutOfRangeException>(() => BoardClock.Resolve(sample, nowMinutes: 1440));
        Assert.Throws<FormatException>(() => BoardClock.Resolve(sample, "not-a-date"));
    }

    [Fact]
    public void 窄化先隐藏辅助再省课名最后省教室且组数量不变()
    {
        var group = NextVisual([NextCourse("很长的课程名称甲"), NextCourse("很长的课程名称乙", room: "测试楼202")],
            "2026-09-14T07:00:00+08:00").NextClass.Group;
        var wide = NextClassSummary.Build(group, 2000, s => s.Length * 10);
        var mid = NextClassSummary.Build(group, 450, s => s.Length * 10);
        var narrow = NextClassSummary.Build(group, 320, s => s.Length * 10);
        var tiny = NextClassSummary.Build(group, 160, s => s.Length * 10);
        Assert.True(wide.ShowAuxiliary);
        Assert.False(mid.ShowAuxiliary);
        Assert.All(mid.Items, i => Assert.Equal(i.Name, i.DisplayName));
        Assert.All(narrow.Items, i => { Assert.NotEqual(i.Name, i.DisplayName); Assert.Equal(i.Room, i.DisplayRoom); });
        Assert.All(tiny.Items, i => { Assert.Equal("", i.DisplayName); Assert.NotEqual(i.Room, i.DisplayRoom); });
        foreach (var next in new[] { wide, mid, narrow, tiny })
        {
            Assert.Equal("今", next.Marker);
            Assert.Equal("2门同时开课", next.CountText);
            Assert.Equal(wide.Tooltip, next.Tooltip);
        }
    }

    [Fact]
    public void 原顶栏计数不变且摘要高度只在外壳显式预留时占用()
    {
        var course = NextCourse();
        var board = Layout.BuildBoard([course], TestTerm, new BoardOptions
        { Today = "2026-09-14", Now = DateTimeOffset.Parse("2026-09-14T08:30:00+08:00", CultureInfo.InvariantCulture) });
        var original = BoardVisualBuilder.Build(board, 560, false, availableHeight: 700);
        var withSummary = BoardVisualBuilder.Build(board, 560, false, availableHeight: 700, includeNextClassRow: true);
        Assert.Equal(original.Header, withSummary.Header);
        Assert.Equal("今日 1 节", withSummary.Header.TodayText);
        Assert.Equal(NextClassStatus.NoFuture, withSummary.NextClass.Group.Status);
        Assert.True(withSummary.Geometry.RowHeight <= original.Geometry.RowHeight);
    }

    [Fact]
    public void 午夜日期范围变化且不同节次同刻开始仍为完整组()
    {
        var term = TestTerm with { Slots = [new Slot(1, "08:00", ""), new Slot(2, "08:00", "")] };
        var courses = new[] { NextCourse(start: 1), NextCourse("同刻另一节次", start: 2) };
        var before = NextVisual(courses, "2026-09-13T23:59:59.9999999+08:00", term).NextClass;
        var after = NextVisual(courses, "2026-09-14T00:00:00+08:00", term).NextClass;
        Assert.Equal("后", before.Marker);
        Assert.Equal("今", after.Marker);
        Assert.Equal("2门同时开课", after.CountText);
        Assert.Equal(2, after.Group.Sessions.Count);
        Assert.Equal(before.Group.StartsAt, after.Group.StartsAt);
        foreach (var summary in new[] { before, after })
        {
            var allText = string.Join(" ", summary.Marker, summary.DateText, summary.StartText,
                summary.CountText, summary.Message, summary.Tooltip,
                string.Join(" ", summary.Items.Select(i => i.DisplayName + i.DisplayRoom)));
            foreach (var countdown in new[] { "倒计时", "分钟后", "小时后", "天后", "秒后", "距开课", "正在上" })
                Assert.DoesNotContain(countdown, allText);
        }
    }

    [Fact]
    public void 过去缺时间安排或晚于已知最早组的未知安排不妨碍可靠摘要()
    {
        var term = TestTerm with { Slots = [new Slot(1, "08:00", ""), new Slot(2, "", "")] };
        var pastUnknown = NextCourse("过去缺时刻", start: 2);
        var known = NextCourse("已知下一次", Weekday.Tuesday, 1);
        var laterUnknown = NextCourse("以后缺时刻", Weekday.Wednesday, 2);
        var next = NextVisual([pastUnknown, known, laterUnknown], "2026-09-15T07:00:00+08:00", term).NextClass;
        Assert.Equal(NextClassStatus.Found, next.Group.Status);
        Assert.Equal("已知下一次", Assert.Single(next.Items).Name);
    }

    [Fact]
    public void 纯调用缺省Now会采样真实时刻而不被诊断Today冻结()
    {
        var before = Time.LocalTodayIso();
        var board = Layout.BuildBoard([NextCourse()], TestTerm, new BoardOptions { Today = "2026-09-14" });
        var visual = BoardVisualBuilder.Build(board, 800, false);
        var after = Time.LocalTodayIso();
        Assert.Contains(visual.NextClass.Group.LocalToday, new[] { before, after });
        Assert.Equal("今日 1 节", visual.Header.TodayText);
    }

    [Fact]
    public void 长西文课名窄化不破坏代理对或丢失悬停全名()
    {
        var course = NextCourse("Very long laboratory course name 🧪 repeat repeat repeat", room: "LAB-TEST-101");
        var next = NextVisual([course], "2026-09-14T07:00:00+08:00", width: 200).NextClass;
        Assert.Contains(course.Name, next.Tooltip);
        Assert.Equal("LAB-TEST-101", next.Items[0].DisplayRoom);
        Assert.NotEqual(course.Name, next.Items[0].DisplayName);
        Assert.DoesNotContain('\uFFFD', next.Items[0].DisplayName);
    }

    [Fact]
    public void 下一次上课摘要通过原布局视觉管线展示完整信息()
    {
        var visual = NextVisual([Maths], "2026-09-14T07:59:59+08:00");
        var next = visual.NextClass;
        Assert.Equal(NextClassStatus.Found, next.Group.Status);
        Assert.Equal("今", next.Marker);
        Assert.Equal("2026-09-14", next.DateText);
        Assert.Equal("08:00", next.StartText);
        Assert.Equal(Maths.Name, Assert.Single(next.Items).Name);
        Assert.Equal("南101", next.Items[0].Room);
        Assert.Contains(Maths.Name, next.Tooltip);
        Assert.Contains("南101", next.Tooltip);
        Assert.Equal("今日 1 节", visual.Header.TodayText);
    }
}
