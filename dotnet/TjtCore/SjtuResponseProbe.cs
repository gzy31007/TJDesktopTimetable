using System.Text.Json;

namespace Tjt.Core;

/// <summary>一条交大响应的探测结论（<c>Ok=false</c> 时 <see cref="Note"/> 直接给用户看）。</summary>
public sealed record SjtuProbeVerdict(bool Ok, string Note);

/// <summary>
/// 判断一段交大响应"是不是我们要的数据"（对应同济那条路的 <see cref="TongjiResponseProbe"/>）。
///
/// <para>交大接口的包装是 <c>{errno, error, data}</c>（<b>不是</b>同济的 <c>{code, msg, data}</c>），
/// 成功时 <c>errno="0"</c>、<c>error="成功"</c>；课表为空时前端会看到 <c>errno="99999"</c>
/// （<c>WeekTable.getClassData</c> 见它还特判了 <c>isDataEmpty</c>）。</para>
///
/// <para>它只回答"要不要往下走"，真正的字段解析在 <c>Adapters/SjtuStudentAdapter</c>。
/// 用户粘错请求（拿到登录页、拿到别的接口）时，这里的 <see cref="Note"/> 是他唯一的线索。</para>
/// </summary>
public static class SjtuResponseProbe
{
    /// <summary>诊断信息里字段名的最大展示个数。</summary>
    private const int MaxFieldNames = 8;

    /// <summary>服务端 message 的最大展示长度。</summary>
    private const int MaxMessageLength = 80;

    /// <summary>课表为空时服务端给的 errno（前端 <c>WeekTable</c> 同款特判）。</summary>
    public const string EmptyErrno = "99999";

    /// <summary>检查一段"课表"响应。</summary>
    public static SjtuProbeVerdict Inspect(string? text)
    {
        var (root, failure) = Open(text, "请换成课表页那条 listBySemester（或 listByWeek）请求。");
        if (failure is not null) return failure;

        using (root)
        {
            var payload = root!.RootElement;
            var errno = Text(payload, "errno");

            if (!payload.TryGetProperty("data", out var data))
            {
                return new SjtuProbeVerdict(false, ServerError(payload, errno));
            }

            if (data.ValueKind != JsonValueKind.Array)
            {
                return new SjtuProbeVerdict(false, $"data 字段不是数组：{FieldNames(data)}");
            }

            var count = data.GetArrayLength();
            if (count == 0)
            {
                var reason = errno == EmptyErrno ? "服务端标记本学期课表为空（errno=99999）" : "课表数组是空的";
                return new SjtuProbeVerdict(false, reason);
            }

            // 看第一条像不像课程条目：name + (day / duration)。字段名对不上就说明粘到别的接口了
            var first = data[0];
            if (first.ValueKind != JsonValueKind.Object || !LooksLikeLesson(first))
            {
                return new SjtuProbeVerdict(false, $"data 数组 {count} 条的字段不像课程条目：{FieldNames(first)}");
            }

            return new SjtuProbeVerdict(true, $"data 数组 {count} 条");
        }
    }

    /// <summary>检查一段"教务日历"响应（<c>semester/calendar</c>）。</summary>
    public static SjtuProbeVerdict InspectCalendar(string? text)
    {
        var (root, failure) = Open(text, "日历接口是 /app/stu/school/semester/calendar。");
        if (failure is not null) return failure;

        using (root)
        {
            var payload = root!.RootElement;
            if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new SjtuProbeVerdict(false, $"日历响应里没有 data 数组：{FieldNames(payload)}");
            }

            var count = data.GetArrayLength();
            if (count == 0) return new SjtuProbeVerdict(false, "日历数组是空的");

            var first = data[0];
            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("week", out _)
                || !first.TryGetProperty("weekDay", out _)
                || !first.TryGetProperty("day", out _))
            {
                return new SjtuProbeVerdict(false, $"日历条目字段不对：{FieldNames(first)}");
            }

            var info = SjtuTerms.ParseCalendar(data);
            var startDate = info.Days
                .Where(day => day.Week == 1 && day.WeekDay == 1)
                .Select(day => day.Day)
                .FirstOrDefault();
            var weeks = info.Days.Count > 0 ? info.Days.Max(day => day.Week) : 0;
            return new SjtuProbeVerdict(
                true,
                $"日历 {count} 天，共 {weeks} 教学周，第 1 周周一 {startDate ?? "未知"}");
        }
    }

    /// <summary>课程条目的最小特征：有课程名，且有 <c>day</c> 或 <c>duration</c>。</summary>
    private static bool LooksLikeLesson(JsonElement item)
    {
        var hasName = item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String;
        var hasSchedule = item.TryGetProperty("day", out _) || item.TryGetProperty("duration", out _);
        return hasName && hasSchedule;
    }

    /// <summary>统一的"打开 JSON + 空值/格式错误"前置检查。</summary>
    private static (JsonDocument? Root, SjtuProbeVerdict? Failure) Open(string? text, string hint)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, new SjtuProbeVerdict(false, "响应为空"));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text!);
        }
        catch (JsonException)
        {
            return (null, new SjtuProbeVerdict(false, $"响应不是合法 JSON（可能复制到了 HTML 页面请求）。{hint}"));
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return (null, new SjtuProbeVerdict(false, "响应不是 JSON 对象"));
        }

        return (document, null);
    }

    /// <summary>把服务端的 <c>error</c> 说人话（没有就退回 <c>errno</c>）。</summary>
    private static string ServerError(JsonElement payload, string? errno)
    {
        var error = Text(payload, "error");
        if (!string.IsNullOrWhiteSpace(error)) return $"服务端返回：{Truncate(error!, MaxMessageLength)}";
        if (!string.IsNullOrWhiteSpace(errno)) return $"服务端返回 errno={errno}";
        return $"顶层字段：{FieldNames(payload)}";
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FieldNames(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element.ValueKind.ToString();

        var names = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (names.Count >= MaxFieldNames) break;
            names.Add(property.Name);
        }

        return string.Join(", ", names);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
