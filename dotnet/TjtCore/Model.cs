using System.Text.Json.Serialization;

namespace Tjt.Core;

/// <summary>
/// 统一课表模型（TS 侧 <c>packages/core/src/model.ts</c> 的镜像）。
///
/// 所有学校适配器都必须把数据归一化成这里的结构；窗口层与渲染层只认识这套模型。
/// 位掩码一律用 <see cref="uint"/>：TS 那边靠 32 位整数运算 + <c>&gt;&gt;&gt; 0</c>，
/// C# 这里用无符号类型，天然没有负数/溢出的坑。
/// </summary>
public static class TimetableModel
{
    public const int SchemaVersion = 1;

    /// <summary>同济校历时间戳按北京时间午夜记录，默认时区偏移（分钟）。</summary>
    public const int DefaultTzOffsetMinutes = 480;

    public static readonly Weekday[] Weekdays = [Weekday.Monday, Weekday.Tuesday, Weekday.Wednesday, Weekday.Thursday, Weekday.Friday, Weekday.Saturday, Weekday.Sunday];

    public static string WeekdayLabel(Weekday day) => day switch
    {
        Weekday.Monday => "周一",
        Weekday.Tuesday => "周二",
        Weekday.Wednesday => "周三",
        Weekday.Thursday => "周四",
        Weekday.Friday => "周五",
        Weekday.Saturday => "周六",
        Weekday.Sunday => "周日",
        _ => $"周{(int)day}",
    };

    public static bool IsWeekend(Weekday day) => (int)day >= 6;
}

/// <summary>星期：1 = 周一 … 7 = 周日（与同济教务 dayOfWeek 一致）。</summary>
public enum Weekday
{
    Monday = 1,
    Tuesday = 2,
    Wednesday = 3,
    Thursday = 4,
    Friday = 5,
    Saturday = 6,
    Sunday = 7,
}

/// <summary>节次时间定义。</summary>
public sealed record Slot(int Index, string Begin, string End);

/// <summary>学期信息。<c>StartDate</c> 是第 1 周周一的日期（<c>YYYY-MM-DD</c>）。</summary>
public sealed record Term(
    string Id,
    string Name,
    int Year,
    int TermNo,
    int TotalWeeks,
    IReadOnlyList<Slot> Slots,
    string? StartDate = null)
{
    /// <summary>人类可读的学期标签（TS 侧没有这个字段，落盘时忽略它，保持两端文件同形）。</summary>
    [JsonIgnore]
    public string Label => string.IsNullOrEmpty(Name) ? $"{Year}-{Year + 1}学年第{TermNo}学期" : Name;

    public string SlotBegin(int index) => Slots.FirstOrDefault(s => s.Index == index)?.Begin ?? string.Empty;

    public string SlotEnd(int index) => Slots.FirstOrDefault(s => s.Index == index)?.End ?? string.Empty;
}

/// <summary>一次上课安排（同一天、连续节次、一组周次、一个教室）。</summary>
public sealed record Session(string Id, Weekday Day, int StartSlot, int EndSlot, uint Weeks, string? Room = null);

/// <summary>教学班（用户视角的"一门课"）。</summary>
public sealed record Course(
    string Id,
    string Name,
    IReadOnlyList<string> Teachers,
    IReadOnlyList<Session> Sessions,
    string? CourseCode = null,
    string? TeachingClassCode = null,
    string? Faculty = null,
    string? Campus = null,
    string? Color = null);

public sealed record TimetableSource(string AdapterId, string AdapterVersion, string ImportedAt);

public sealed record Timetable(Term Term, IReadOnlyList<Course> Courses, TimetableSource Source)
{
    public int SchemaVersion => TimetableModel.SchemaVersion;
}

public static class TimetableDefaults
{
    /// <summary>同济默认节次表（2026-2027 学年第 1 学期校历，工作日与周末同表）。</summary>
    public static readonly Slot[] TongjiSlots =
    [
        new(1, "08:00", "08:45"),
        new(2, "08:50", "09:35"),
        new(3, "10:00", "10:45"),
        new(4, "10:50", "11:35"),
        new(5, "13:30", "14:15"),
        new(6, "14:20", "15:05"),
        new(7, "15:30", "16:15"),
        new(8, "16:20", "17:05"),
        new(9, "18:30", "19:15"),
        new(10, "19:20", "20:05"),
        new(11, "20:10", "20:55"),
    ];

    /// <summary>生成 1..n 的默认节次（没有节次表时的通用导入）。</summary>
    public static Slot[] MakeDefaultSlots(int count = 11) =>
        count == TongjiSlots.Length
            ? TongjiSlots.Select(s => s with { }).ToArray()
            : Enumerable.Range(1, count).Select(i => new Slot(i, string.Empty, string.Empty)).ToArray();

    /// <summary>过滤非法节次并按下标排序。</summary>
    public static Slot[] SlotsFromList(IEnumerable<Slot> slots) =>
        slots.Where(s => s.Index > 0).OrderBy(s => s.Index).Select(s => s with { }).ToArray();
}
