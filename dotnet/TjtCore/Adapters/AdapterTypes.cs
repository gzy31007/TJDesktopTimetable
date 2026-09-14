using System.Text.Json;

namespace Tjt.Core.Adapters;

/// <summary>
/// 学校适配器契约（TS 侧 <c>adapters/types.ts</c> 的移植）——这是"兼容其他学校"的扩展点。
///
/// 新增一所学校：实现一个 <see cref="ISchoolAdapter"/> 并在注册表里登记；
/// 核心算法（周次、冲突、布局、时间）与全部 UI 都不需要改动。
///
/// JSON 一律用 <see cref="JsonElement"/> 传递（而不是反序列化成具体类型）：
/// 适配器要处理的是**异构、字段名不确定**的教务数据，弱类型探测比强类型绑定更贴合，
/// 也和 TS 那边 <c>unknown</c> 的语义一致。
/// </summary>
public sealed record ImportFile(string Name, string Text);

public sealed record ImportInput
{
    /// <summary>直接粘贴的 JSON / HTML 文本。</summary>
    public string? Text { get; init; }

    /// <summary>选择的文件（可多份：课表 + 校历 + 学生信息）。</summary>
    public IReadOnlyList<ImportFile>? Files { get; init; }

    /// <summary>用户显式指定的适配器 id，优先于自动探测。</summary>
    public string? AdapterId { get; init; }

    /// <summary>校历里有多个学期时，指定要用的学期 id。</summary>
    public string? TermId { get; init; }

    /// <summary>覆盖"现在"（测试用）。</summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>写入 <c>Timetable.Source.ImportedAt</c>（测试用）。</summary>
    public string? ImportedAt { get; init; }
}

public enum DiagnosticLevel
{
    Info,
    Warn,
    Error,
}

public sealed record Diagnostic(DiagnosticLevel Level, string Code, string Message, object? Detail = null);

public sealed record ImportResult
{
    public required string AdapterId { get; init; }
    public required string AdapterName { get; init; }
    public required string AdapterVersion { get; init; }
    public required Term Term { get; init; }

    /// <summary>
    /// 已经确定要显示的课程。面向"个人已选课表"的适配器直接填充它；
    /// 面向"培养计划 / 平行班清单"的适配器把它留空，改用 <see cref="Candidates"/>。
    /// </summary>
    public required IReadOnlyList<Course> Courses { get; init; }

    /// <summary>
    /// 需要用户勾选的候选教学班池（含平行班）。
    /// 同济 <c>timetable/major</c> 返回的是专业培养计划里的全部平行班，必须走勾选流程。
    /// </summary>
    public IReadOnlyList<Course>? Candidates { get; init; }

    /// <summary>默认勾选的教学班 id。</summary>
    public IReadOnlyList<string> Preselect { get; init; } = [];

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>适配器附带的元信息（学生信息、学期来源等，UI 可展示）。</summary>
    public IReadOnlyDictionary<string, object?>? Meta { get; init; }
}

public sealed record AdapterContext(DateTimeOffset Now, string ImportedAt);

public interface ISchoolAdapter
{
    /// <summary>稳定 id，落盘与排查日志用。</summary>
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    string Description { get; }

    /// <summary>是否支持程序内自动抓取（本阶段全部为 false，手动导入）。</summary>
    bool CanFetch => false;

    /// <summary>匹配度：0 = 不匹配，1 = 确定。</summary>
    double Detect(ImportInput input);

    ImportResult Parse(ImportInput input, AdapterContext context);
}

/// <summary>适配器注册表：新增学校 = 实现 <see cref="ISchoolAdapter"/> + 在这里登记。</summary>
public interface IAdapterRegistry
{
    IReadOnlyList<ISchoolAdapter> List();

    ISchoolAdapter? Get(string id);

    /// <summary>返回匹配度最高的适配器（<c>score &gt; 0</c> 才算命中）。</summary>
    (ISchoolAdapter Adapter, double Score)? Best(ImportInput input);
}

/// <summary>适配器共用的输入处理工具（对应 TS 侧 <c>types.ts</c> 里的那几个函数）。</summary>
public static class AdapterInput
{
    /// <summary>收集输入里所有可解析的文本片段（粘贴的 + 各文件）。</summary>
    public static IReadOnlyList<(string Label, string Text)> Texts(ImportInput input)
    {
        var list = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(input.Text)) list.Add(("<粘贴内容>", input.Text!));
        foreach (var file in input.Files ?? [])
        {
            if (!string.IsNullOrWhiteSpace(file.Text)) list.Add((file.Name, file.Text));
        }
        return list;
    }

    /// <summary>安全解析 JSON，失败返回 <c>null</c>。</summary>
    public static JsonDocument? TryParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? AsRecord(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value : null;

    public static JsonElement? AsArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value : null;

    /// <summary>解开同济教务常见的 <c>{code, msg, data}</c> 包装；也接受裸数组。</summary>
    public static JsonElement UnwrapData(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("data", out var data)) return data;
        return value;
    }

    public static Diagnostic Diagnostic(DiagnosticLevel level, string code, string message, object? detail = null) =>
        new(level, code, message, detail);

    /// <summary>小工具：按名字取字符串（兼容数字字段被写成 number 的情况）。</summary>
    public static string? StringOrNull(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    /// <summary>小工具：按名字取整数（字符串数字也接受）。</summary>
    public static int? IntOrNull(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var n) ? n : (int?)null,
            JsonValueKind.String => int.TryParse(value.GetString(), out var s) ? s : null,
            _ => null,
        };
    }

    /// <summary>小工具：按名字取"可能是字符串也可能是数组"的文本列表。</summary>
    public static IReadOnlyList<string> StringList(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single!];
        }
        if (value.ValueKind != JsonValueKind.Array) return [];
        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(text => !string.IsNullOrWhiteSpace(text))];
    }
}
