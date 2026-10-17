using System.Globalization;
using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

public class NextClassesTests
{
    private static readonly Term Semester = new("sample", "示例学期", 2026, 1, 16,
        [new Slot(1, "08:00", "08:45"), new Slot(2, "10:00", "")], "2026-09-14");

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static Course WithSessions(params Session[] sessions) => new("sample", "示例课程", [], sessions);

    [Theory]
    [InlineData("2026-09-13T23:59:59.9999999Z", NextClassStatus.Found)]
    [InlineData("2026-09-14T00:00:00Z", NextClassStatus.NoFuture)]
    [InlineData("2026-09-14T00:00:00.0000001Z", NextClassStatus.NoFuture)]
    public void Find_UsesFullAbsoluteStartBoundary_AndKeepsEverySimultaneousSession(string now, NextClassStatus status)
    {
        var course = WithSessions(
            new Session("a", Weekday.Monday, 1, 2, 1, "示例101"),
            new Session("b", Weekday.Monday, 1, 1, 1, "示例102"),
            new Session("empty", Weekday.Monday, 99, 99, 0));

        var result = NextClasses.Find([course], Semester, At(now));

        Assert.Equal(status, result.Status);
        if (status == NextClassStatus.Found)
        {
            Assert.Equal(["a", "b"], result.Sessions.Select(x => x.Session.Id));
            Assert.Equal(["示例101", "示例102"], result.Sessions.Select(x => x.Session.Room));
        }
        else
        {
            Assert.Null(result.StartsAt);
            Assert.Empty(result.Sessions);
        }
    }

    [Theory]
    [InlineData("2026-09-13T23:00:00Z", 1, Weekday.Monday, NextClassStatus.InformationInsufficient)]
    [InlineData("2026-09-13T23:00:00Z", 1, Weekday.Tuesday, NextClassStatus.Found)]
    [InlineData("2026-09-20T23:00:00Z", 1, Weekday.Monday, NextClassStatus.Found)]
    [InlineData("2026-09-13T23:00:00Z", 1, (Weekday)0, NextClassStatus.InformationInsufficient)]
    public void Find_UnknownTimeBlocksOnlyWhenItCanChangeTheEarliestCompleteGroup(
        string now, int unknownWeek, Weekday unknownDay, NextClassStatus status)
    {
        var course = WithSessions(
            new Session("known", Weekday.Monday, 1, 1, Weeks.FromWeeks([1, 2])),
            new Session("unknown", unknownDay, 99, 99, Weeks.WeekMask(unknownWeek)));

        var result = NextClasses.Find([course], Semester, At(now));

        Assert.Equal(status, result.Status);
        if (status == NextClassStatus.InformationInsufficient)
        {
            Assert.Null(result.StartsAt);
            Assert.Empty(result.Sessions);
        }
        else Assert.Equal("known", Assert.Single(result.Sessions).Session.Id);
    }

    [Theory]
    [InlineData(1, NextClassStatus.InformationInsufficient)]
    [InlineData(2, NextClassStatus.Found)]
    public void Find_UnknownWeekdayWithKnownStartCanBeExcludedOnlyWhenEveryPossibleDayIsLater(
        int unknownSlot, NextClassStatus status)
    {
        var course = WithSessions(
            new Session("known", Weekday.Monday, 1, 1, 1),
            new Session("unknown-day", (Weekday)99, unknownSlot, unknownSlot, 1));

        var result = NextClasses.Find([course], Semester, At("2026-09-13T23:00:00Z"));

        Assert.Equal(status, result.Status);
        if (status == NextClassStatus.Found)
            Assert.Equal("known", Assert.Single(result.Sessions).Session.Id);
    }

    [Fact]
    public void Find_UnknownStartEntirelyInThePastDoesNotPreventNoFuture()
    {
        var result = NextClasses.Find(
            [WithSessions(new Session("past", Weekday.Monday, 99, 99, 1))],
            Semester, At("2026-09-14T16:00:00Z"));

        Assert.Equal(NextClassStatus.NoFuture, result.Status);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void Find_CalendarDateAndStartUseTheRequestedFixedTimezone()
    {
        var result = NextClasses.Find(
            [WithSessions(new Session("sample", Weekday.Monday, 1, 1, 1))],
            Semester, At("2026-09-13T23:00:00Z"), tzOffsetMinutes: 0);

        Assert.Equal("2026-09-13", result.LocalToday);
        Assert.Equal(NextClassStatus.Found, result.Status);
        Assert.Equal(At("2026-09-14T08:00:00Z"), result.StartsAt);
        Assert.Equal(TimeSpan.Zero, result.StartsAt!.Value.Offset);
    }

    [Fact]
    public void Find_MidnightCandidateCannotProveAnUnknownSameDayStartIsNotSimultaneous()
    {
        var midnight = Semester with { Slots = [new Slot(1, "00:00", "")] };
        var course = WithSessions(
            new Session("known", Weekday.Monday, 1, 1, 1),
            new Session("unknown", Weekday.Monday, 99, 99, 1));

        var result = NextClasses.Find([course], midnight, At("2026-09-13T15:59:59Z"));

        Assert.Equal(NextClassStatus.InformationInsufficient, result.Status);
        Assert.Empty(result.Sessions);
        Assert.Null(result.StartsAt);
    }

    [Fact]
    public void Find_UnknownStartWithoutKnownFutureCannotClaimNoFuture()
    {
        var result = NextClasses.Find(
            [WithSessions(new Session("unknown", Weekday.Monday, 99, 99, 1))],
            Semester, At("2026-09-13T23:00:00Z"));

        Assert.Equal(NextClassStatus.InformationInsufficient, result.Status);
    }

    [Theory]
    [InlineData(null, 16)]
    [InlineData("not-a-date", 16)]
    [InlineData("2026-02-30", 16)]
    [InlineData("9999-99-99", 16)]
    [InlineData("9999-12-31", 16)]
    [InlineData("2026-09-14", -1)]
    [InlineData("2026-09-14", 0)]
    [InlineData("2026-09-14", int.MaxValue)]
    public void Find_InvalidKeyCalendarDataReturnsInsufficientInsteadOfThrowing(string? startDate, int weeks)
    {
        var term = Semester with { StartDate = startDate, TotalWeeks = weeks };
        var result = NextClasses.Find(
            [WithSessions(new Session("sample", Weekday.Monday, 1, 1, Weeks.WeekMask(16)))],
            term, At("2026-09-13T23:00:00Z"));

        Assert.Equal(NextClassStatus.InformationInsufficient, result.Status);
        Assert.Null(result.StartsAt);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void Find_AllEmptyMasksHaveNoFutureEvenWithoutCalendarInformation()
    {
        var result = NextClasses.Find(
            [WithSessions(new Session("empty", (Weekday)99, 99, 99, 0))],
            Semester with { StartDate = null }, At("2026-09-13T23:00:00Z"));

        Assert.Equal(NextClassStatus.NoFuture, result.Status);
        Assert.Null(result.StartsAt);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void Layout_UsesOriginalCoursesAndNowWithoutOverwritingLegacyToday()
    {
        var course = WithSessions(new Session("weekend", Weekday.Sunday, 1, 1, Weeks.WeekMask(2)));
        var result = Layout.BuildBoard([course], Semester, new BoardOptions
        {
            Today = "2026-09-14", Now = At("2026-09-20T17:00:00Z"),
            WeekView = WeekView.Current, ShowWeekend = false
        });

        Assert.Equal("2026-09-14", result.Today);
        Assert.Equal(1, result.CurrentWeek);
        Assert.Empty(result.Blocks);
        Assert.Equal(NextClassStatus.Found, result.NextClass.Status);
        Assert.Equal("2026-09-21", result.NextClass.LocalToday);
        Assert.Equal(At("2026-09-27T08:00:00+08:00"), result.NextClass.StartsAt);
        Assert.Equal("weekend", Assert.Single(result.NextClass.Sessions).Session.Id);
    }

    [Theory]
    [InlineData(-1, WeekView.Odd)]
    [InlineData(int.MaxValue, WeekView.Even)]
    public void Layout_InvalidWeekCountDoesNotCrashOrLoopOverUnboundedWeeks(int totalWeeks, WeekView view)
    {
        var course = WithSessions(new Session("sample", Weekday.Monday, 1, 1, 1));
        var result = Layout.BuildBoard([course], Semester with { TotalWeeks = totalWeeks }, new BoardOptions
        {
            Today = "2026-09-14", Now = At("2026-09-13T23:00:00Z"), WeekView = view
        });

        Assert.Equal(NextClassStatus.InformationInsufficient, result.NextClass.Status);
    }

    [Fact]
    public void BoardState_ExistingConstructorDefaultsToUnknownNextClass()
    {
        var state = new BoardState(Semester, "sample", [], [], [], null, "", 0, 0);

        Assert.Equal(NextClassStatus.InformationInsufficient, state.NextClass.Status);
        Assert.Null(state.NextClass.StartsAt);
        Assert.Empty(state.NextClass.Sessions);
        Assert.Equal("", state.NextClass.LocalToday);
    }

    [Fact]
    public void Find_SelectsStrictlyFutureStartAcrossWholeTerm_WithoutRequiringEndTime()
    {
        var course = WithSessions(new Session("later", Weekday.Monday, 2, 2, Weeks.WeekMask(16)));

        var result = NextClasses.Find([course], Semester, At("2026-09-14T00:00:00Z"));

        Assert.Equal(NextClassStatus.Found, result.Status);
        Assert.Equal(At("2026-12-28T10:00:00+08:00"), result.StartsAt);
        Assert.Equal("2026-09-14", result.LocalToday);
        var occurrence = Assert.Single(result.Sessions);
        Assert.Same(course, occurrence.Course);
        Assert.Equal(16, occurrence.Week);
        Assert.Equal("later", occurrence.Session.Id);
    }
}
