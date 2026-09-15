using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tjt.Core;

/// <summary>
/// 课表落盘 / 读取（<c>Timetable</c> ↔ JSON）。
///
/// <para><b>为什么放在 core 而不放在外壳</b>：序列化是纯计算，放这里就能在 Linux 上单测
/// （round-trip、字段名、容错）；外壳只负责"文件放在哪、什么时候写"。</para>
///
/// <para><b>字段名用 camelCase</b>：沿用已删除的 Electron 线 <c>Timetable</c> 接口的字段名，
/// 所以那边当年写出的 <c>%APPDATA%\TJDesktopTimetable\timetable.json</c> 仍然能读进来
/// （也是"文件可直接手工替换 / 备份"的前提）。</para>
/// </summary>
public static class TimetableJson
{
    /// <summary>落盘用的序列化选项（camelCase + 缩进 + 容忍注释与尾逗号，方便手工编辑）。</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>序列化课表（调用方负责写文件）。</summary>
    public static string Serialize(Timetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        return JsonSerializer.Serialize(timetable, Options);
    }

    /// <summary>
    /// 反序列化课表；内容不是合法 JSON、或缺了 <c>term</c> / <c>courses</c> 时返回 <c>null</c>。
    ///
    /// <para>容错是刻意的：课表文件是用户可能手工改过的普通 JSON，读坏了应当退回"没有导入过"，
    /// 而不是让应用起不来。</para>
    /// </summary>
    public static Timetable? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        Timetable? timetable;
        try
        {
            timetable = JsonSerializer.Deserialize<Timetable>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        // 位置记录缺字段时 STJ 不会报错（给 null），所以在出口处补一道校验。
        if (timetable?.Term is null || timetable.Courses is null) return null;

        // 集合字段缺失同样会得到 null（不报错），而下游（布局 / 渲染）会直接遍历它们。
        // 这里统一补成空集合：少写一个字段的文件退化成"空的那部分"，而不是渲染时崩。
        var term = timetable.Term.Slots is null ? timetable.Term with { Slots = [] } : timetable.Term;
        var courses = new List<Course>(timetable.Courses.Count);
        foreach (var course in timetable.Courses)
        {
            if (course is null) continue;
            var normalized = course;
            if (normalized.Sessions is null) normalized = normalized with { Sessions = [] };
            if (normalized.Teachers is null) normalized = normalized with { Teachers = [] };
            courses.Add(normalized);
        }

        return timetable with { Term = term, Courses = courses };
    }
}
