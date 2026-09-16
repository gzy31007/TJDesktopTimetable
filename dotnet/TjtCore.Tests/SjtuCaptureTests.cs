using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 交大站点纯逻辑的验收：周次文本解析、教务日历、响应探测、请求判定与 URL 改写。
///
/// <para>这一层全在 <c>TjtCore</c>（平台无关），所以跟着本工程在 Linux/CI 上跑；
/// 外壳（WinUI / Avalonia）只剩"把请求发出去"和"把 WebView2 的事件喂进来"。</para>
/// </summary>
public class SjtuCaptureTests
{
    private static readonly string SemesterJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "sjtu-2026-1-semester.json"));

    private static readonly string CalendarJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "sjtu-2026-1-calendar.json"));

    // ── 周次文本 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 周次文本支持区间与逗号枚举()
    {
        Assert.Equal(Enumerable.Range(1, 16).ToArray(), Weeks.ToWeeks(SjtuTerms.ParseWeeks("1-16周", null, 21), 21));
        Assert.Equal(new[] { 5, 9, 13, 14, 15 }, Weeks.ToWeeks(SjtuTerms.ParseWeeks("5周,9周,13-15周", null, 21), 21));
        Assert.Equal(new[] { 3, 4 }, Weeks.ToWeeks(SjtuTerms.ParseWeeks("3-4周", null, 21), 21));
    }

    [Fact]
    public void 单双周在区间展开之后过滤()
    {
        // 顺序错了会只剩 5/9/13（13-15 没展开就先按单周筛）
        Assert.Equal(
            new[] { 5, 9, 13, 15 },
            Weeks.ToWeeks(SjtuTerms.ParseWeeks("5周,9周,13-15周", ["单周"], 21), 21));

        Assert.Equal(
            new[] { 2, 4, 6, 8 },
            Weeks.ToWeeks(SjtuTerms.ParseWeeks("1-8周", ["双周"], 21), 21));

        // 与"重修"这种角标共存时，角标不影响周次
        Assert.Equal(
            new[] { 1, 2, 3 },
            Weeks.ToWeeks(SjtuTerms.ParseWeeks("1-3周", ["重修"], 21), 21));
    }

    [Fact]
    public void 没有周次文本时按整学期处理()
    {
        Assert.Equal(Enumerable.Range(1, 16).ToArray(), Weeks.ToWeeks(SjtuTerms.ParseWeeks(null, null, 16), 16));
        Assert.Equal(Enumerable.Range(1, 21).ToArray(), Weeks.ToWeeks(SjtuTerms.ParseWeeks("", [], 21), 21));
    }

    [Fact]
    public void 周次超出总周数会被裁掉()
    {
        // 交大日历到第 21 周；课表偶尔会出现超出范围的周次
        Assert.Equal(Enumerable.Range(1, 21).ToArray(), Weeks.ToWeeks(SjtuTerms.ParseWeeks("1-25周", null, 21), 21));
    }

    // ── 学期 id 与日历 ────────────────────────────────────────────────────────────

    [Fact]
    public void 学期id解析覆盖交大与同济两种形态()
    {
        Assert.Equal((2026, 1), SjtuTerms.ParseTermId("2026-2027-1"));
        Assert.Equal((2025, 2), SjtuTerms.ParseTermId("2025-2026-2"));
        // 同济的 calendarId 是纯数字（122），不是学期 id
        Assert.Null(SjtuTerms.ParseTermId("122"));
        Assert.Null(SjtuTerms.ParseTermId(null));
        Assert.Null(SjtuTerms.ParseTermId(""));
        Assert.Equal("2026-2027-1", SjtuTerms.JoinTermId("2026-2027", "1"));
        Assert.Null(SjtuTerms.JoinTermId(null, "1"));
    }

    [Fact]
    public void 教务日历按逐日条目解析()
    {
        using var document = System.Text.Json.JsonDocument.Parse(CalendarJson);
        var info = SjtuTerms.ParseCalendar(document.RootElement.GetProperty("data"));

        Assert.Equal(154, info.Days.Count);
        Assert.Equal("2026-2027", info.Year);
        Assert.Equal("1", info.Semester);
        Assert.Equal("2026-09-07", info.StartDay);
        Assert.Equal("2027-02-07", info.EndDay);

        // 第 0 周是报到周，第 1 周周一才是开学日
        Assert.Equal("2026-09-07", info.Days.Single(day => day.Week == 0 && day.WeekDay == 1).Day);
        Assert.Equal("2026-09-14", info.Days.Single(day => day.Week == 1 && day.WeekDay == 1).Day);
        Assert.Equal(21, info.Days.Max(day => day.Week));
    }

    [Fact]
    public void 没有日历时学期退化成按周次推断且开学日为空()
    {
        var term = SjtuTerms.BuildTerm("2026-2027-1", SjtuTerms.CalendarInfo.Empty, fallbackTotalWeeks: 16);

        Assert.Equal("2026-2027-1", term.Id);
        Assert.Null(term.StartDate);
        Assert.Equal(16, term.TotalWeeks);
        Assert.Equal(13, term.Slots.Count);
    }

    // ── 响应探测 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 探测认得出交大课表响应()
    {
        var verdict = SjtuResponseProbe.Inspect(SemesterJson);

        Assert.True(verdict.Ok);
        Assert.Contains("15", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 探测认得出交大教务日历响应()
    {
        var verdict = SjtuResponseProbe.InspectCalendar(CalendarJson);

        Assert.True(verdict.Ok);
        Assert.Contains("154", verdict.Note, StringComparison.Ordinal);
        Assert.Contains("2026-09-14", verdict.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "响应为空")]
    [InlineData("<!doctype html><html></html>", "不是合法 JSON")]
    [InlineData("{\"errno\":\"500\",\"error\":\"系统繁忙，请稍后再试！\"}", "系统繁忙")]
    [InlineData("{\"errno\":\"0\",\"error\":\"成功\",\"data\":[]}", "空")]
    public void 探测把不像课表的响应说清楚(string body, string fragment)
    {
        var verdict = SjtuResponseProbe.Inspect(body);

        Assert.False(verdict.Ok);
        Assert.Contains(fragment, verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 探测认得出前端空课表的errno()
    {
        // 前端 WeekTable 对 99999 有专门分支（isDataEmpty）
        var verdict = SjtuResponseProbe.Inspect("{\"errno\":\"99999\",\"error\":\"\",\"data\":[]}");

        Assert.False(verdict.Ok);
        Assert.Contains("99999", verdict.Note, StringComparison.Ordinal);
    }

    // ── 站点判定与 URL 改写 ──────────────────────────────────────────────────────

    [Fact]
    public void 交大主机判定不误伤同济()
    {
        Assert.True(SjtuWebCapture.IsSjtuHost("j.sjtu.edu.cn"));
        Assert.True(SjtuWebCapture.IsSjtuHost("sjtu.edu.cn"));
        Assert.True(SjtuWebCapture.IsSjtuHost("jwc.sjtu.edu.cn"));
        Assert.False(SjtuWebCapture.IsSjtuHost("1.tongji.edu.cn"));
        Assert.False(SjtuWebCapture.IsSjtuHost("notsjtu.edu.cn"));
        Assert.False(SjtuWebCapture.IsSjtuHost(null));

        Assert.True(SjtuWebCapture.IsSjtuUrl("https://j.sjtu.edu.cn/app/ui/timetable"));
        Assert.False(SjtuWebCapture.IsSjtuUrl("https://j.sjtu.edu.cn.evil.com/app"));
    }

    [Fact]
    public void 课表接口与日历接口互不误判()
    {
        const string byWeek = "https://j.sjtu.edu.cn/app/stu/lesson/listByWeek?year=2026-2027&semester=1&week=1";
        const string bySemester = "https://j.sjtu.edu.cn/app/stu/lesson/listBySemester?year=2026-2027&semester=1";
        const string calendar = "https://j.sjtu.edu.cn/app/stu/school/semester/calendar?locale=zh";

        Assert.True(SjtuWebCapture.IsTimetableEndpoint(byWeek));
        Assert.True(SjtuWebCapture.IsTimetableEndpoint(bySemester));
        Assert.False(SjtuWebCapture.IsTimetableEndpoint(calendar));
        Assert.False(SjtuWebCapture.IsTimetableEndpoint(null));

        Assert.True(SjtuWebCapture.IsCalendarEndpoint(calendar));
        Assert.False(SjtuWebCapture.IsCalendarEndpoint(bySemester));

        Assert.Equal("按周课表 listByWeek", SjtuWebCapture.EndpointLabel(byWeek));
        Assert.Equal("整学期课表 listBySemester", SjtuWebCapture.EndpointLabel(bySemester));
        Assert.Equal("教务日历 semester/calendar", SjtuWebCapture.EndpointLabel(calendar));
    }

    [Fact]
    public void 从按周请求里取出学期参数并改写成整学期请求()
    {
        const string byWeek = "https://j.sjtu.edu.cn/app/stu/lesson/listByWeek?locale=zh&year=2026-2027&semester=1&week=7";

        Assert.Equal("2026-2027", SjtuWebCapture.YearOf(byWeek));
        Assert.Equal("1", SjtuWebCapture.SemesterOf(byWeek));
        Assert.Equal("7", SjtuWebCapture.WeekOf(byWeek));

        Assert.Equal(
            "https://j.sjtu.edu.cn/app/stu/lesson/listBySemester?locale=zh&year=2026-2027&semester=1",
            SjtuWebCapture.SemesterLessonsUrl(byWeek, "2026-2027", "1"));

        Assert.Equal(
            "https://j.sjtu.edu.cn/app/stu/school/semester/calendar?locale=zh",
            SjtuWebCapture.SemesterCalendarUrl(byWeek));

        // 缺 year/semester 时构造不出来（调用方据此放弃改写）
        Assert.Null(SjtuWebCapture.SemesterLessonsUrl(byWeek, null, "1"));
    }

    [Fact]
    public void 构造请求时固定用交大主机不跟随粘贴内容()
    {
        // 粘贴的 URL 里即便带了别的主机，重建出来的也必须指向 j.sjtu.edu.cn（防凭据外发）
        var built = SjtuWebCapture.SemesterLessonsUrl("https://evil.example.com/app/x", "2026-2027", "1");

        Assert.Equal(
            "https://j.sjtu.edu.cn/app/stu/lesson/listBySemester?locale=zh&year=2026-2027&semester=1",
            built);
    }

    [Fact]
    public void 捕获判定区分课表与日历()
    {
        var timetable = SjtuWebCapture.Inspect(
            "https://j.sjtu.edu.cn/app/stu/lesson/listBySemester?year=2026-2027&semester=1",
            SemesterJson);
        Assert.True(timetable.Ok);
        Assert.Equal(SjtuCaptureKind.Timetable, timetable.Kind);
        Assert.Equal("2026-2027-1", timetable.TermId);

        var calendar = SjtuWebCapture.Inspect(
            "https://j.sjtu.edu.cn/app/stu/school/semester/calendar?locale=zh",
            CalendarJson);
        Assert.True(calendar.Ok);
        Assert.Equal(SjtuCaptureKind.Calendar, calendar.Kind);

        var other = SjtuWebCapture.Inspect("https://j.sjtu.edu.cn/app/stu/exam/score/list", SemesterJson);
        Assert.False(other.Ok);
        Assert.Equal(SjtuCaptureKind.None, other.Kind);
    }

    [Fact]
    public void 日志描述不含响应体也不含凭据()
    {
        var text = SjtuWebCapture.Describe(
            "https://j.sjtu.edu.cn/app/stu/lesson/listBySemester?year=2026-2027&semester=1",
            4512);

        Assert.Contains("listBySemester", text, StringComparison.Ordinal);
        Assert.Contains("4512", text, StringComparison.Ordinal);
        Assert.Contains("2026-2027-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("name", text, StringComparison.Ordinal);
    }
}
