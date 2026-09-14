using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tjt.Core.Adapters;

/// <summary>
/// 通用适配器 —— 其他学校 / 手工整理课表的最低门槛入口（TS 侧 <c>adapters/generic.ts</c> 的移植）。
///
/// 支持两种自描述 JSON：
///
/// 1. 本项目导出格式（推荐）：
/// <code>
/// { "schemaVersion": 1,
///   "term": { "id": "2026-1", "year": 2026, "termNo": 1, "totalWeeks": 16, "startDate": "2026-09-14",
///             "slots": [{ "index": 1, "begin": "08:00", "end": "08:45" }] },
///   "courses": [{ "id": "c1", "name": "高等数学", "teachers": ["张三"],
///                 "sessions": [{ "day": 1, "startSlot": 1, "endSlot": 2, "weeks": 65535, "room": "南101" }] }] }
/// </code>
///
/// 2. 简化的"周次列表"写法（<c>weeks</c> 用数组，<c>day</c> 用 1-7）：
/// <code>
/// { "term": { "totalWeeks": 16 }, "courses": [{ "name": "大学物理", "sessions": [{ "day": 3, "startSlot": 5, "endSlot": 6, "weeks": [1,3,5] }] }] }
/// </code>
/// </summary>
public sealed class GenericJsonAdapter : ISchoolAdapter
{
    public const string AdapterId = "generic-json";
    public const string AdapterVersion = "1.0.0";

    /// <summary>单例：适配器无状态。</summary>
    public static GenericJsonAdapter Instance { get; } = new();

    private GenericJsonAdapter()
    {
    }

    public string Id => AdapterId;

    public string DisplayName => "通用 JSON（其他学校 / 手工整理）";

    public string Version => AdapterVersion;

    public string Description =>
        "解析 `{term, courses:[{sessions}]}` 或 `{term, classes}` 两种通用 JSON，可直接作为新学校适配器的过渡方案。";

    public bool CanFetch => false;

    private static readonly Regex IntegerRe = new(@"^-?[0-9]+$", RegexOptions.CultureInvariant);

    public double Detect(ImportInput input)
    {
        foreach (var (_, text) in AdapterInput.Texts(input))
        {
            using var doc = AdapterInput.TryParseJson(text);
            if (doc is null) continue;
            var parsed = doc.RootElement;
            if (parsed.ValueKind != JsonValueKind.Object) continue;

            if (ToInt(Prop(parsed, "schemaVersion")) == TimetableModel.SchemaVersion && IsTimetableShape(parsed)) return 0.9;
            if (IsTimetableShape(parsed)) return 0.6;
            if (PreviewHtmlAdapter.IsClassesShape(parsed)) return 0.4;
        }

        return 0;
    }

    public ImportResult Parse(ImportInput input, AdapterContext context)
    {
        var diagnostics = new List<Diagnostic>();

        JsonDocument? payloadDoc = null;
        foreach (var (_, text) in AdapterInput.Texts(input))
        {
            var doc = AdapterInput.TryParseJson(text);
            if (doc is null) continue;
            payloadDoc = doc;
            break;
        }

        try
        {
            var payload = payloadDoc?.RootElement ?? default;
            if (payload.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(AdapterInput.Diagnostic(DiagnosticLevel.Error, "generic.invalid", "不是合法的 JSON 对象。"));
                return EmptyResult(diagnostics);
            }

            var term = ParseTerm(Prop(payload, "term"));

            if (PreviewHtmlAdapter.IsClassesShape(payload))
            {
                var (parsedTerm, classes) = PreviewHtmlAdapter.ClassesToCourses(payload);
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Info,
                    "generic.classes",
                    $"按 classes 结构导入 {classes.Count} 个教学班。"));
                return new ImportResult
                {
                    AdapterId = AdapterId,
                    AdapterName = DisplayName,
                    AdapterVersion = AdapterVersion,
                    Term = parsedTerm,
                    Courses = [],
                    Candidates = classes,
                    Preselect = [],
                    Diagnostics = diagnostics,
                };
            }

            var courses = ParseCourses(Prop(payload, "courses"));
            if (courses.Count == 0)
            {
                diagnostics.Add(AdapterInput.Diagnostic(
                    DiagnosticLevel.Error,
                    "generic.empty",
                    "没有解析出任何含 `sessions` 或 `periods` 的课程。"));
            }
            else
            {
                diagnostics.Add(AdapterInput.Diagnostic(DiagnosticLevel.Info, "generic.summary", $"导入 {courses.Count} 门课程。"));
            }

            return new ImportResult
            {
                AdapterId = AdapterId,
                AdapterName = DisplayName,
                AdapterVersion = AdapterVersion,
                Term = term,
                Courses = courses,
                Preselect = [],
                Diagnostics = diagnostics,
            };
        }
        finally
        {
            payloadDoc?.Dispose();
        }
    }

    /// <summary>`{courses:[{sessions|periods}]}` 形状判定（不要求 <c>schemaVersion</c>）。</summary>
    private static bool IsTimetableShape(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var courses = AdapterInput.AsArray(Prop(value, "courses"));
        if (courses is null || courses.Value.GetArrayLength() == 0) return false;
        var first = courses.Value[0];
        return first.ValueKind == JsonValueKind.Object && (Has(first, "sessions") || Has(first, "periods"));
    }

    private static Term ParseTerm(JsonElement value)
    {
        var record = value.ValueKind == JsonValueKind.Object ? value : default;
        var totalWeeks = ToInt(Prop(record, "totalWeeks")) ?? 16;
        var id = Prop(record, "id");
        var name = Prop(record, "name");
        var startDate = Prop(record, "startDate");
        return new Term(
            IsNullish(id) ? "imported" : JsText(id),
            name.ValueKind == JsonValueKind.String ? name.GetString()! : "",
            ToInt(Prop(record, "year")) ?? DateTime.Now.Year,
            ToInt(Prop(record, "termNo")) ?? 1,
            totalWeeks,
            ParseSlots(Prop(record, "slots")) ?? TimetableDefaults.MakeDefaultSlots(),
            startDate.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(startDate.GetString()) ? startDate.GetString() : null);
    }

    private static List<Course> ParseCourses(JsonElement value)
    {
        var courses = new List<Course>();
        var list = AdapterInput.AsArray(value);
        if (list is null) return courses;

        var index = 0;
        foreach (var entry in list.Value.EnumerateArray())
        {
            var position = index;
            index += 1;
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var nameEl = Prop(entry, "name");
            var coursesName = nameEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nameEl.GetString())
                ? nameEl.GetString()!
                : $"课程 {position + 1}";
            var idEl = Prop(entry, "id");
            var courseId = IsNullish(idEl) ? $"{coursesName}-{position}" : JsText(idEl);

            var sessions = new List<Session>();
            var sessionList = AdapterInput.AsArray(Prop(entry, "sessions")) ??
                              AdapterInput.AsArray(Prop(entry, "periods"));
            foreach (var rawSession in Elements(sessionList))
            {
                if (rawSession.ValueKind != JsonValueKind.Object) continue;

                var day = ToInt(Prop(rawSession, "day"));
                var startSlot = ToInt(Prop(rawSession, "startSlot")) ?? ToInt(Prop(rawSession, "start"));
                var endSlot = ToInt(Prop(rawSession, "endSlot")) ?? ToInt(Prop(rawSession, "end"));

                var rawWeeks = Prop(rawSession, "weeks");
                if (IsNullish(rawWeeks)) rawWeeks = Prop(rawSession, "weeksMask");
                var weeks = ParseWeeks(rawWeeks);

                if (day is null || startSlot is null || endSlot is null || weeks == 0) continue;
                if (day is < 1 or > 7) continue;

                var roomEl = Prop(rawSession, "room");
                string? room = null;
                if (roomEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(roomEl.GetString())) room = roomEl.GetString();
                else
                {
                    var courseRoom = Prop(entry, "room");
                    if (courseRoom.ValueKind == JsonValueKind.String) room = courseRoom.GetString();
                }

                sessions.Add(new Session(
                    $"{courseId}-{day}-{startSlot}-{endSlot}-{weeks}",
                    (Weekday)day.Value,
                    startSlot.Value,
                    endSlot.Value,
                    weeks,
                    room));
            }

            if (sessions.Count == 0) continue;

            var courseCode = Prop(entry, "courseCode");
            var classCode = Prop(entry, "teachingClassCode");
            var faculty = Prop(entry, "faculty");
            var campus = Prop(entry, "campus");
            courses.Add(new Course(
                courseId,
                coursesName,
                ToStringArray(Prop(entry, "teachers")),
                sessions,
                IsNullish(courseCode) ? null : JsText(courseCode),
                IsNullish(classCode) ? null : JsText(classCode),
                faculty.ValueKind == JsonValueKind.String ? faculty.GetString() : null,
                campus.ValueKind == JsonValueKind.String ? campus.GetString() : null));
        }

        return courses;
    }

    private static Slot[]? ParseSlots(JsonElement value)
    {
        if (AdapterInput.AsArray(value) is null) return null;
        var slots = new List<Slot>();
        foreach (var entry in Elements(AdapterInput.AsArray(value)))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var index = ToInt(Prop(entry, "index"));
            if (index is null || index <= 0) continue;
            var begin = Prop(entry, "begin");
            var end = Prop(entry, "end");
            slots.Add(new Slot(
                index.Value,
                begin.ValueKind == JsonValueKind.String ? begin.GetString()! : "",
                end.ValueKind == JsonValueKind.String ? end.GetString()! : ""));
        }

        return slots.Count > 0 ? TimetableDefaults.SlotsFromList(slots) : null;
    }

    /// <summary>数组 = 周次列表（<c>[1,3,5]</c>）；数字 = 位掩码（<c>65535</c>）。</summary>
    private static uint ParseWeeks(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return Weeks.FromWeeks(value.EnumerateArray().Select(item => ToInt(item) ?? 0));
        }

        var mask = ToInt(value);
        return mask is null ? 0u : unchecked((uint)mask.Value);
    }

    private static List<string> ToStringArray(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return [.. value.EnumerateArray().Select(JsText)];
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return [];
        return [.. (value.GetString() ?? "")
            .Split(['、', ',', '，', ';', '；'], StringSplitOptions.None)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)];
    }

    private static ImportResult EmptyResult(List<Diagnostic> diagnostics) => new()
    {
        AdapterId = AdapterId,
        AdapterName = Instance.DisplayName,
        AdapterVersion = AdapterVersion,
        Term = new Term(
            "unknown",
            "",
            DateTime.Now.Year,
            1,
            16,
            TimetableDefaults.MakeDefaultSlots()),
        Courses = [],
        Preselect = [],
        Diagnostics = diagnostics,
    };

    private static int? ToInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            var truncated = Math.Truncate(number);
            if (truncated is > int.MaxValue or < int.MinValue) return null;
            return (int)truncated;
        }

        if (value.ValueKind != JsonValueKind.String) return null;
        var text = (value.GetString() ?? "").Trim();
        if (!IntegerRe.IsMatch(text)) return null;
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string JsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        _ => value.ToString(),
    };

    private static bool IsNullish(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static JsonElement Prop(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    private static bool Has(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _);

    private static IEnumerable<JsonElement> Elements(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}
