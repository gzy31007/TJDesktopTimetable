using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tjt.Core.Adapters;

/// <summary>
/// 兼容器：解析本项目雏形阶段的 <c>select_preview.html</c> / <c>build_select_html.py</c> 产物
/// （TS 侧 <c>adapters/preview-html.ts</c> 的移植）。
///
/// 数据形状：
/// <code>
/// const DATA = {
///   term: { id: 122, year: 2026, termNo: 1 },
///   classes: [{ code, courseCode, name, teachers, room, faculty,
///               periods: [{ day, start, end, weeksMask, weeksLabel }] }]
/// };
/// </code>
/// 该结构同样用于"从网页版排课工具导出后导入桌面小组件"，并且被 <see cref="GenericJsonAdapter"/>
/// 复用（<c>{term, classes}</c> 是通用的候选池格式）。
/// </summary>
public sealed class PreviewHtmlAdapter : ISchoolAdapter
{
    public const string AdapterId = "preview-html";
    public const string AdapterVersion = "1.0.0";

    /// <summary>单例：适配器无状态，注册表与通用适配器都复用它。</summary>
    public static PreviewHtmlAdapter Instance { get; } = new();

    private PreviewHtmlAdapter()
    {
    }

    public string Id => AdapterId;

    public string DisplayName => "排课工具导出（select_preview / classes JSON）";

    public string Version => AdapterVersion;

    public string Description =>
        "解析 `select_preview.html`（或 `{term, classes}` 结构的 JSON），即网页版排课工具的导出结果。所有教学班都会进入候选池。";

    public bool CanFetch => false;

    /// <summary>`const DATA =` 的锚点（容忍 <c>const</c> / <c>var</c> / <c>let</c>）。</summary>
    private static readonly Regex DataAnchor = new(@"(?:const|var|let)\s+DATA\s*=", RegexOptions.CultureInvariant);

    /// <summary>`^-?\d+$` 的整数探测（TS 侧每个适配器各有一份 <c>toInt</c>，镜像同样的组织方式）。</summary>
    private static readonly Regex IntegerRe = new(@"^-?[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 从 HTML/JS 文本里提取 <c>const DATA = {...}</c>（做括号配对，容忍字符串内的花括号）。
    ///
    /// 与 TS 的差异：TS 返回"已经解析好的对象"，C# 的 <see cref="JsonElement"/> 绑定在
    /// <see cref="JsonDocument"/> 上，因此这里返回 document，由调用方 <c>using</c> 管理生命周期
    /// （直接 <c>doc.RootElement</c> 使用）。
    /// </summary>
    public static JsonDocument? ExtractDataObject(string text)
    {
        var direct = AdapterInput.TryParseJson(text);
        if (direct is not null) return direct;

        var anchor = DataAnchor.Match(text);
        var searchFrom = anchor.Success ? text.IndexOf('=', anchor.Index) : text.IndexOf('{');
        if (searchFrom < 0) return null;

        var start = text.IndexOf('{', searchFrom);
        if (start < 0) return null;

        var depth = 0;
        char? inString = null;
        var escaped = false;
        for (var i = start; i < text.Length; i += 1)
        {
            var ch = text[i];
            if (inString is not null)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == inString) inString = null;
                continue;
            }

            if (ch is '"' or '\'')
            {
                inString = ch;
                continue;
            }

            if (ch == '{')
            {
                depth += 1;
                continue;
            }

            if (ch != '}') continue;
            depth -= 1;
            if (depth != 0) continue;

            var raw = text[start..(i + 1)];
            try
            {
                return JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>`{classes: [{periods|courseCode, …}]}` 形状判定（空 <c>classes</c> 不算）。</summary>
    public static bool IsClassesShape(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var classes = AdapterInput.AsArray(Prop(value, "classes"));
        if (classes is null || classes.Value.GetArrayLength() == 0) return false;
        var first = classes.Value[0];
        return first.ValueKind == JsonValueKind.Object && (Has(first, "periods") || Has(first, "courseCode"));
    }

    /// <summary><c>{term, classes}</c> → 统一模型（被 preview 与 generic 两个适配器共用）。</summary>
    public static (Term Term, List<Course> Courses) ClassesToCourses(JsonElement data)
    {
        var termRecord = Prop(data, "term");
        var periodMasks = new List<uint>();

        var courses = new List<Course>();
        foreach (var cls in Elements(Prop(data, "classes")))
        {
            if (cls.ValueKind != JsonValueKind.Object) continue;

            var code = Prop(cls, "code");
            var id = "";
            if (code.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(code.GetString())) id = code.GetString()!;
            else if (code.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) id = JsText(code);
            if (id.Length == 0)
            {
                var courseCodePart = IsNullish(Prop(cls, "courseCode")) ? "unknown" : JsText(Prop(cls, "courseCode"));
                var namePart = IsNullish(Prop(cls, "name")) ? "" : JsText(Prop(cls, "name"));
                id = $"{courseCodePart}-{namePart}";
            }

            var sessions = new List<Session>();
            foreach (var period in Elements(Prop(cls, "periods")))
            {
                if (period.ValueKind != JsonValueKind.Object) continue;
                var day = ToInt(Prop(period, "day"));
                var startSlot = ToInt(Prop(period, "start"));
                var endSlot = ToInt(Prop(period, "end"));
                var mask = ToInt(Prop(period, "weeksMask"));
                if (day is null || startSlot is null || endSlot is null || mask is null) continue;
                if (day is < 1 or > 7) continue;

                var weeks = NormalizeMask(mask.Value);
                periodMasks.Add(weeks);
                var room = Prop(cls, "room");
                sessions.Add(new Session(
                    $"{id}-{day}-{startSlot}-{endSlot}-{weeks}",
                    (Weekday)day.Value,
                    startSlot.Value,
                    endSlot.Value,
                    weeks,
                    room.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(room.GetString()) ? room.GetString() : null));
            }

            if (sessions.Count == 0) continue;
            var name = Prop(cls, "name");
            var clsCourseCode = Prop(cls, "courseCode");
            var faculty = Prop(cls, "faculty");
            courses.Add(new Course(
                id,
                name.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(name.GetString()) ? name.GetString()! : "(未知课程)",
                SplitTeachers(Prop(cls, "teachers")),
                sessions,
                clsCourseCode.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? JsText(clsCourseCode) : null,
                code.ValueKind == JsonValueKind.String ? code.GetString() : null,
                faculty.ValueKind == JsonValueKind.String ? faculty.GetString() : null));
        }

        var inferredWeeks = periodMasks.Count == 0 ? 0 : periodMasks.Max(Weeks.Highest);
        var declaredWeeks = ToInt(Prop(termRecord, "totalWeeks")) ?? 0;
        var totalWeeks = Math.Max(16, Math.Max(declaredWeeks, inferredWeeks));

        var termId = Prop(termRecord, "id");
        var termName = Prop(termRecord, "name");
        var term = new Term(
            IsNullish(termId) ? "unknown" : JsText(termId),
            termName.ValueKind == JsonValueKind.String ? termName.GetString()! : "",
            ToInt(Prop(termRecord, "year")) ?? DateTime.Now.Year,
            ToInt(Prop(termRecord, "termNo")) ?? 1,
            totalWeeks,
            TimetableDefaults.MakeDefaultSlots());

        return (term, courses);
    }

    public double Detect(ImportInput input)
    {
        foreach (var (_, text) in AdapterInput.Texts(input))
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith('{'))
            {
                using var doc = AdapterInput.TryParseJson(trimmed);
                if (doc is not null && IsClassesShape(doc.RootElement)) return 0.75;
            }

            if (trimmed.Contains("<!DOCTYPE", StringComparison.Ordinal) ||
                trimmed.Contains("const DATA", StringComparison.Ordinal))
            {
                using var doc = ExtractDataObject(trimmed);
                if (doc is not null && IsClassesShape(doc.RootElement)) return 0.85;
            }
        }

        return 0;
    }

    public ImportResult Parse(ImportInput input, AdapterContext context)
    {
        var diagnostics = new List<Diagnostic>();
        JsonDocument? found = null;
        var from = "";

        foreach (var (label, text) in AdapterInput.Texts(input))
        {
            var candidate = ExtractDataObject(text);
            if (candidate is null) continue;
            if (!IsClassesShape(candidate.RootElement))
            {
                candidate.Dispose();
                continue;
            }

            found = candidate;
            from = label;
            break;
        }

        if (found is null)
        {
            diagnostics.Add(AdapterInput.Diagnostic(DiagnosticLevel.Error, "preview.notfound", "没有找到 `{term, classes}` 结构的数据。"));
            return new ImportResult
            {
                AdapterId = AdapterId,
                AdapterName = DisplayName,
                AdapterVersion = AdapterVersion,
                Term = UnknownTerm(),
                Courses = [],
                Candidates = [],
                Preselect = [],
                Diagnostics = diagnostics,
            };
        }

        try
        {
            var (term, courses) = ClassesToCourses(found.RootElement);
            diagnostics.Add(AdapterInput.Diagnostic(
                DiagnosticLevel.Info,
                "preview.summary",
                $"从 {from} 导入 {courses.Count} 个教学班（学期 {term.Id}）。"));

            return new ImportResult
            {
                AdapterId = AdapterId,
                AdapterName = DisplayName,
                AdapterVersion = AdapterVersion,
                Term = term,
                Courses = [],
                Candidates = courses,
                Preselect = [],
                Diagnostics = diagnostics,
                Meta = new Dictionary<string, object?>
                {
                    ["source"] = from,
                    ["schemaVersion"] = TimetableModel.SchemaVersion,
                },
            };
        }
        finally
        {
            found.Dispose();
        }
    }

    /// <summary>无法解析时的占位学期（对应 TS 里散落的 <c>{id:'unknown', …, totalWeeks:16}</c> 字面量）。</summary>
    private static Term UnknownTerm() => new(
        "unknown",
        "",
        DateTime.Now.Year,
        1,
        16,
        TimetableDefaults.MakeDefaultSlots());

    /// <summary>教师字段：数组直接取字符串，字符串按顿号/逗号/分号拆分。</summary>
    private static List<string> SplitTeachers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return [.. value.EnumerateArray().Select(JsText)];
        }

        if (value.ValueKind != JsonValueKind.String) return [];
        return [.. (value.GetString() ?? "")
            .Split(['、', ',', '，', ';', '；'], StringSplitOptions.None)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)];
    }

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

    /// <summary><c>normalizeMask</c>：把"已经是掩码"的数字规范成无符号 32 位（与 <c>Math.trunc(v) &gt;&gt;&gt; 0</c> 等价）。</summary>
    private static uint NormalizeMask(int value) => unchecked((uint)value);

    /// <summary>JS 的 <c>String(value)</c>（用于模板字符串里的 <c>??</c> 兜底）。</summary>
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

    private static IEnumerable<JsonElement> Elements(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
