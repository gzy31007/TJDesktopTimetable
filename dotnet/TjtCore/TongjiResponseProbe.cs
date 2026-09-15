using System.Text.Json;

namespace Tjt.Core;

/// <summary>抓回来的响应该不该交给适配器（<c>Ok=false</c> 时 <see cref="Note"/> 直接给用户看）。</summary>
public sealed record TongjiResponseVerdict(bool Ok, string Note);

/// <summary>
/// 判断一段响应文本"像不像课表数据"（TS 侧 <c>apps/desktop/src/main/tongji.ts</c> 里
/// <c>looksLikeTimetable</c> 的移植）。
///
/// <para>移植到 core 的理由：这是**纯粹的"看响应长什么样"**，放这里就能在 Linux 上单测；
/// 外壳只剩"发请求"和"把结论显示给用户"。</para>
///
/// <para>它不是适配器的替代品：这里只回答"要不要往下走"，真正的字段解析仍在
/// <c>Adapters/TongjiStudentAdapter</c> 里。给用户的诊断信息（探测结果）也来自这里 ——
/// 用户粘错请求（拿到登录页 HTML、拿到别的接口）时，这句话就是他唯一的线索。</para>
/// </summary>
public static class TongjiResponseProbe
{
    /// <summary>诊断信息里字段名的最大展示个数（与 TS 侧一致）。</summary>
    private const int MaxFieldNames = 8;

    /// <summary>服务端 message 的最大展示长度（与 TS 侧一致）。</summary>
    private const int MaxMessageLength = 80;

    /// <summary>检查响应文本。</summary>
    public static TongjiResponseVerdict Inspect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TongjiResponseVerdict(false, "响应为空");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return new TongjiResponseVerdict(
                false,
                "响应不是合法 JSON（可能复制到了 HTML 页面请求，请换成 getDataBk 那条）。");
        }

        using (document)
        {
            return Inspect(document.RootElement);
        }
    }

    private static TongjiResponseVerdict Inspect(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return new TongjiResponseVerdict(false, "响应不是 JSON 对象");

        var hasData = payload.TryGetProperty("data", out var data);
        if (!hasData && payload.TryGetProperty("message", out var message))
        {
            // 同济教务失败的典型样子：{code: 500, message: "..."}，没有 data
            var text = message.ValueKind == JsonValueKind.String ? message.GetString() ?? string.Empty : message.ToString();
            return new TongjiResponseVerdict(false, $"服务端返回：{Truncate(text, MaxMessageLength)}");
        }

        if (hasData && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("selectedCourses", out var selected) && selected.ValueKind == JsonValueKind.Array)
            {
                return new TongjiResponseVerdict(true, $"selectedCourses {selected.GetArrayLength()} 门");
            }

            return new TongjiResponseVerdict(false, $"data 字段：{FieldNames(data)}");
        }

        if (hasData && data.ValueKind == JsonValueKind.Array)
        {
            return new TongjiResponseVerdict(true, $"data 数组 {data.GetArrayLength()} 条");
        }

        return new TongjiResponseVerdict(false, $"顶层字段：{FieldNames(payload)}");
    }

    private static string FieldNames(JsonElement element)
    {
        var names = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (names.Count >= MaxFieldNames) break;
            names.Add(property.Name);
        }

        return string.Join(", ", names);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];
}
