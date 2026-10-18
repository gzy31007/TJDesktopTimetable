using System.Text.Json;
using Tjt.Core;
using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

public class BoardScrollIdentityTests
{
    private static readonly Term TestTerm = new(
        "scroll-term", "示例学期", 2026, 1, 16,
        TimetableDefaults.MakeDefaultSlots(), "2026-09-14");

    private static readonly Course TestCourse = new(
        "scroll-course", "示例课程（甲）", ["示例教师甲", "示例教师乙"],
        [new Session("scroll-session", Weekday.Monday, 1, 2, Weeks.FullMask(16), "示例楼101")]);

    private static readonly BoardOptions Options = new()
    {
        WeekView = WeekView.All,
        ShowWeekend = true,
        Today = "2026-09-14",
        Now = new DateTimeOffset(2026, 9, 14, 7, 0, 0, TimeSpan.FromHours(8)),
    };

    private static BoardState Build(
        Course? course = null, Term? term = null, BoardOptions? options = null) =>
        Layout.BuildBoard([course ?? TestCourse], term ?? TestTerm, options ?? Options);

    private static string Key(BoardState state, WeekView view = WeekView.All, bool weekend = true) =>
        BoardScrollIdentity.Create(state, view, weekend);

    [Fact]
    public void 相同数据反序列化和重新构建不改变身份()
    {
        var original = Build();
        var course = JsonSerializer.Deserialize<Course>(JsonSerializer.Serialize(TestCourse))!;
        var term = JsonSerializer.Deserialize<Term>(JsonSerializer.Serialize(TestTerm))!;
        var rebuilt = Build(course, term);

        Assert.NotSame(original, rebuilt);
        Assert.NotSame(original.Rows, rebuilt.Rows);
        Assert.NotSame(original.Blocks[0].Teachers, rebuilt.Blocks[0].Teachers);
        Assert.Equal(Key(original), Key(rebuilt));
        Assert.Equal(Key(rebuilt), Key(Build(course, term)));
    }

    [Fact]
    public void 普通时钟推进与当前周今日当前节次摘要变化不改变身份()
    {
        var before = Build();
        var inClass = Build(options: Options with { Now = Options.Now!.Value.AddMinutes(70) });
        var nextDay = Build(options: Options with
        {
            Today = "2026-09-15",
            Now = Options.Now!.Value.AddDays(1).AddHours(3),
        });
        var nextWeek = Build(options: Options with
        {
            Today = "2026-09-21",
            Now = Options.Now!.Value.AddDays(7),
        });

        Assert.False(before.Rows[0].IsCurrent);
        Assert.True(inClass.Rows[0].IsCurrent);
        Assert.NotEqual(before.NextClass.StartsAt, inClass.NextClass.StartsAt);
        Assert.NotEqual(before.Days[0].IsToday, nextDay.Days[0].IsToday);
        Assert.NotEqual(before.TodaySessionCount, nextDay.TodaySessionCount);
        Assert.NotEqual(before.CurrentWeek, nextWeek.CurrentWeek);
        Assert.Equal(Key(before), Key(inClass));
        Assert.Equal(Key(before), Key(nextDay));
        Assert.Equal(Key(before), Key(nextWeek));
    }

    [Fact]
    public void 本周视图跨周但实际块未变时保留身份()
    {
        var options = Options with { WeekView = WeekView.Current };
        var first = Build(options: options);
        var next = Build(options: options with { Today = "2026-09-21" });

        Assert.NotEqual(first.CurrentWeek, next.CurrentWeek);
        Assert.Equal(Key(first, WeekView.Current), Key(next, WeekView.Current));
    }

    [Fact]
    public void 本周视图跨周使实际可见块改变时重置身份()
    {
        var oddCourse = TestCourse with
        {
            Sessions = [TestCourse.Sessions[0] with { Weeks = Weeks.OddMask(16) }],
        };
        var options = Options with { WeekView = WeekView.Current };
        var first = Build(oddCourse, options: options);
        var next = Build(oddCourse, options: options with { Today = "2026-09-21" });

        Assert.Single(first.Blocks);
        Assert.Empty(next.Blocks);
        Assert.NotEqual(Key(first, WeekView.Current), Key(next, WeekView.Current));
    }

    [Fact]
    public void Resize和主题只改变视觉几何及染色而不改变身份()
    {
        var board = Build();
        var key = Key(board);
        var compact = BoardVisualBuilder.Build(board, 400, dark: false, availableHeight: 300);
        var expanded = BoardVisualBuilder.Build(board, 1200, dark: true, availableHeight: 900);
        var recolored = Build(options: Options with { ColorOf = _ => "#abcdef" });

        Assert.NotEqual(compact.Geometry.CellWidth, expanded.Geometry.CellWidth);
        Assert.NotEqual(board.Blocks[0].Color, recolored.Blocks[0].Color);
        Assert.Equal(key, Key(board));
        Assert.Equal(key, Key(recolored));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("teachers")]
    [InlineData("room")]
    [InlineData("weeks")]
    [InlineData("day")]
    [InlineData("start")]
    [InlineData("end")]
    public void 实际课程内容或日期节次位置变化重置身份(string field)
    {
        var session = TestCourse.Sessions[0];
        var changed = field switch
        {
            // 两个名称有相同 ShortName，必须比较完整名称而非压缩后缀。
            "name" => TestCourse with { Name = "示例课程（乙）" },
            "teachers" => TestCourse with { Teachers = ["示例教师丙", "示例教师乙"] },
            "room" => TestCourse with { Sessions = [session with { Room = "示例楼202" }] },
            "weeks" => TestCourse with { Sessions = [session with { Weeks = Weeks.OddMask(16) }] },
            "day" => TestCourse with { Sessions = [session with { Day = Weekday.Tuesday }] },
            "start" => TestCourse with { Sessions = [session with { StartSlot = 2 }] },
            "end" => TestCourse with { Sessions = [session with { EndSlot = 3 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.NotEqual(Key(Build()), Key(Build(changed)));
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("end")]
    [InlineData("first")]
    [InlineData("last")]
    public void 节次时间和可见范围变化重置身份(string field)
    {
        var term = TestTerm;
        var options = Options;
        if (field is "begin" or "end")
        {
            term = term with
            {
                Slots = term.Slots.Select(slot => slot.Index == 1
                    ? field == "begin" ? slot with { Begin = "08:05" } : slot with { End = "08:40" }
                    : slot).ToArray(),
            };
        }
        else
        {
            options = field == "first" ? options with { FirstSlot = 2 } : options with { LastSlot = 5 };
        }

        Assert.NotEqual(Key(Build()), Key(Build(term: term, options: options)));
    }

    [Fact]
    public void 显示压缩名变化也重置身份()
    {
        var original = Build();
        var changed = Build(options: Options with { ShortName = _ => "替代显示名" });

        Assert.Equal(original.Blocks[0].Name, changed.Blocks[0].Name);
        Assert.NotEqual(Key(original), Key(changed));
    }

    [Fact]
    public void 同格并排顺序改变重置身份()
    {
        var other = TestCourse with { Id = "other-course", Name = "另一门示例课" };
        var first = Layout.BuildBoard([TestCourse, other], TestTerm, Options);
        var reversed = Layout.BuildBoard([other, TestCourse], TestTerm, Options);
        var single = Build();

        Assert.Equal(2, first.Blocks[0].ColCount);
        Assert.NotEqual(Key(first), Key(reversed));
        Assert.NotEqual(Key(first), Key(single));
        Assert.NotEqual(Key(first), Key(first with { Blocks = first.Blocks.Reverse().ToArray() }));
    }

    [Fact]
    public void 过滤参数操作即使得到相同可见块也重置身份()
    {
        var all = Build();
        var current = Build(options: Options with { WeekView = WeekView.Current });
        var weekdays = Build(options: Options with { ShowWeekend = false });

        Assert.Equal(all.Blocks.Count, current.Blocks.Count);
        Assert.NotEqual(Key(all), Key(current, WeekView.Current));
        Assert.NotEqual(Key(all), Key(all, WeekView.All, weekend: false));
        Assert.NotEqual(Key(all), Key(weekdays, weekend: false));
    }

    [Fact]
    public void 不显示的内部标识和导入来源变化不重置身份()
    {
        var course = TestCourse with
        {
            Id = "reloaded-course",
            CourseCode = "reloaded-code",
            TeachingClassCode = "reloaded-class",
            Sessions = [TestCourse.Sessions[0] with { Id = "reloaded-session" }],
        };
        var term = TestTerm with { Id = "reloaded-term" };

        Assert.Equal(Key(Build()), Key(Build(course, term)));
    }

    [Fact]
    public void 空串与null教室显示一致时保留身份()
    {
        var absent = TestCourse with { Sessions = [TestCourse.Sessions[0] with { Room = null }] };
        var empty = TestCourse with { Sessions = [TestCourse.Sessions[0] with { Room = string.Empty }] };

        Assert.Equal(Key(Build(absent)), Key(Build(empty)));
        Assert.NotEqual(Key(Build(absent)), Key(Build()));
    }

    [Fact]
    public void 结构化字段不会因简单分隔符拼接产生碰撞()
    {
        var twoTeachers = TestCourse with { Teachers = ["甲", "乙"] };
        var oneTeacher = TestCourse with { Teachers = ["甲、乙"] };

        Assert.NotEqual(Key(Build(twoTeachers)), Key(Build(oneTeacher)));
    }

    [Fact]
    public void 空状态明确拒绝() =>
        Assert.Throws<ArgumentNullException>(() => BoardScrollIdentity.Create(null!, WeekView.All, true));
}
