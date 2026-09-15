using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 课表落盘（<see cref="TimetableJson"/>）的验收。
///
/// 关键约束有两条，都在这里钉住：
/// ① **round-trip 不丢信息**（含 <c>weeks</c> 位掩码、可选字段、节次表）；
/// ② **字段名与 TS 侧逐字段同名**（camelCase），这样 Electron 线写出的
/// <c>timetable.json</c> WinUI 线能直接读，反之亦然。
/// </summary>
public class TimetableJsonTests
{
    private static Timetable Sample() => new(
        new Term(
            "2026-1",
            "2026-2027学年第1学期",
            2026,
            1,
            16,
            [
                new Slot(1, "08:00", "08:45"),
                new Slot(2, "08:50", "09:35"),
            ],
            "2026-09-14"),
        [
            new Course(
                "c1",
                "线性代数",
                ["李四(22334)"],
                [
                    new Session("s1", Weekday.Tuesday, 3, 4, 0b1111_1111_1111_1111, "南202"),
                    new Session("s2", Weekday.Thursday, 1, 2, 0x5555, null),
                ],
                "320001",
                "5000295005501",
                "数学科学学院",
                "四平路校区",
                "#4C8DFF"),
        ],
        new TimetableSource("tongji-student", "1.0.0", "2026-09-15T10:00:00.000Z"));

    [Fact]
    public void 序列化后能原样读回()
    {
        var original = Sample();
        var json = TimetableJson.Serialize(original);
        var parsed = TimetableJson.Deserialize(json);

        Assert.NotNull(parsed);
        Assert.Equal(original.Term.Id, parsed!.Term.Id);
        Assert.Equal(original.Term.Name, parsed.Term.Name);
        Assert.Equal(original.Term.Year, parsed.Term.Year);
        Assert.Equal(original.Term.TermNo, parsed.Term.TermNo);
        Assert.Equal(original.Term.TotalWeeks, parsed.Term.TotalWeeks);
        Assert.Equal(original.Term.StartDate, parsed.Term.StartDate);
        Assert.Equal(original.Term.Slots, parsed.Term.Slots);

        Assert.Equal(original.Courses.Count, parsed.Courses.Count);
        var course = parsed.Courses[0];
        Assert.Equal("线性代数", course.Name);
        Assert.Equal(["李四(22334)"], course.Teachers);
        Assert.Equal("320001", course.CourseCode);
        Assert.Equal("5000295005501", course.TeachingClassCode);
        Assert.Equal("数学科学学院", course.Faculty);
        Assert.Equal("四平路校区", course.Campus);
        Assert.Equal("#4C8DFF", course.Color);

        Assert.Equal(2, course.Sessions.Count);
        Assert.Equal(Weekday.Tuesday, course.Sessions[0].Day);
        Assert.Equal(3, course.Sessions[0].StartSlot);
        Assert.Equal(4, course.Sessions[0].EndSlot);
        Assert.Equal(0xFFFFu, course.Sessions[0].Weeks);
        Assert.Equal("南202", course.Sessions[0].Room);
        Assert.Equal(0x5555u, course.Sessions[1].Weeks);
        Assert.Null(course.Sessions[1].Room);

        Assert.Equal("tongji-student", parsed.Source.AdapterId);
        Assert.Equal("1.0.0", parsed.Source.AdapterVersion);
        Assert.Equal("2026-09-15T10:00:00.000Z", parsed.Source.ImportedAt);
    }

    [Fact]
    public void 字段名是camelCase且不写多余的label()
    {
        var json = TimetableJson.Serialize(Sample());

        Assert.Contains("\"totalWeeks\"", json, StringComparison.Ordinal);
        Assert.Contains("\"startSlot\"", json, StringComparison.Ordinal);
        Assert.Contains("\"weeks\"", json, StringComparison.Ordinal);
        Assert.Contains("\"adapterId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TotalWeeks\"", json, StringComparison.Ordinal);
        // TS 侧 Term 没有 label；多写一个会让人工编辑的文件与 TS 形状不一致
        Assert.DoesNotContain("\"label\"", json, StringComparison.Ordinal);
        // 可选字段为空时不写 null（保持文件干净）
        Assert.DoesNotContain("\"room\": null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 能读TS侧写出的形状并忽略未知字段()
    {
        // 与 packages/core/test/e2e-timetable.spec.ts 里落盘的那份同形（含手工加的未知字段）
        var json = """
        {
          "schemaVersion": 1,
          "term": {"id": "2026-1", "name": "2026-2027学年第1学期", "year": 2026, "termNo": 1,
                   "startDate": "2026-09-14", "totalWeeks": 16},
          "courses": [{"id": "c1", "name": "线性代数", "teachers": ["李四(22334)"],
            "sessions": [{"id": "s1", "day": 2, "startSlot": 3, "endSlot": 4, "weeks": 65535, "room": "南202"}]}],
          "source": {"adapterId": "tongji-student", "adapterVersion": "1.0.0", "importedAt": "2026-09-15T10:00:00.000Z"},
          "手工加的字段": true
        }
        """;

        var parsed = TimetableJson.Deserialize(json);
        Assert.NotNull(parsed);
        Assert.Equal(16, parsed!.Term.TotalWeeks);
        Assert.Empty(parsed.Term.Slots); // 没写 slots → 空表（外壳按需补默认节次）
        Assert.Equal(Weekday.Tuesday, parsed.Courses[0].Sessions[0].Day);
        Assert.Equal(65535u, parsed.Courses[0].Sessions[0].Weeks);
        Assert.Equal("tongji-student", parsed.Source.AdapterId);
    }

    [Fact]
    public void 读坏了就退回没有课表而不是抛异常()
    {
        Assert.Null(TimetableJson.Deserialize(""));
        Assert.Null(TimetableJson.Deserialize("   "));
        Assert.Null(TimetableJson.Deserialize(null));
        Assert.Null(TimetableJson.Deserialize("{ 这不是 JSON"));
        Assert.Null(TimetableJson.Deserialize("""{"schemaVersion":1}"""));
        Assert.Null(TimetableJson.Deserialize("""{"term":null,"courses":null}"""));
    }

    [Fact]
    public void 容忍注释与尾逗号()
    {
        var json = """
        {
          // 手工编辑时留下的注释
          "term": {"id": "t", "name": "n", "year": 2026, "termNo": 1, "totalWeeks": 16},
          "courses": [],
        }
        """;

        var parsed = TimetableJson.Deserialize(json);
        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Courses);
    }
}
