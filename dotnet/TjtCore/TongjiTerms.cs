namespace Tjt.Core;

/// <summary>
/// 同济学期表（内置快照）——TS 侧 <c>tongji-terms.ts</c> 的移植。
///
/// 个人课表响应里只带 <c>calendarId</c> / <c>calendarName</c>，没有开学日期，而"当前第几周"
/// 必须要它；校历接口需要复杂参数（实测 POST 返回"系统繁忙"）。所以把已知学期做成内置表：
/// 命中即用，未命中则退化为"16 周 + 内置节次、不显示周次"。
///
/// 数据来源：1 系统校历接口响应快照（<c>packages/core/fixtures/tongji-school-calendar.json</c>）。
/// </summary>
public sealed record TermPreset(string CalendarId, string Name, int Year, int TermNo, string StartDate, int TotalWeeks);

public static class TongjiTerms
{
    public static readonly TermPreset[] Presets =
    [
        new("122", "2026-2027学年第1学期", 2026, 1, "2026-09-14", 16),
        new("124", "2027-2028学年第1学期", 2027, 1, "2027-09-06", 16),
    ];

    /// <summary>按 calendarId 找内置学期；接受字符串或数字（教务两种都出现过）。</summary>
    public static TermPreset? FindPreset(string? calendarId)
    {
        if (string.IsNullOrEmpty(calendarId)) return null;
        var wanted = calendarId.Trim();
        return Presets.FirstOrDefault(preset => preset.CalendarId == wanted);
    }

    public static TermPreset? FindPreset(int? calendarId) =>
        calendarId is null ? null : FindPreset(calendarId.Value.ToString());

    /// <summary>由内置学期表构造 <see cref="Term"/>（节次时间用同济默认表）。</summary>
    public static Term TermFromPreset(TermPreset preset) => new(
        preset.CalendarId,
        preset.Name,
        preset.Year,
        preset.TermNo,
        preset.TotalWeeks,
        TimetableDefaults.TongjiSlots.Select(slot => slot with { }).ToArray(),
        preset.StartDate);
}
