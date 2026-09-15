using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tjt.App.Data;

/// <summary>
/// 上次粘贴的抓取请求（<c>%APPDATA%\TJDesktopTimetable\credentials.json</c>）。
///
/// <para><b>这份文件里有 Cookie</b>：只在设置窗口里回显（方便用户再抓一次），
/// <b>任何日志都不得打印它的内容</b> —— 只记长度。文件结构与 Electron 侧一字不差
/// （<c>{ tongjiRequest, savedAt }</c>），两端的"上次粘贴"可以互相接手。</para>
/// </summary>
internal static class CredentialsStore
{
    private const string FileName = "credentials.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>凭据文件路径。</summary>
    public static string FilePath => Path.Combine(SettingsStore.Directory, FileName);

    /// <summary>读上次粘贴的请求全文；没有就返回空串。</summary>
    public static string LoadRequest()
    {
        var path = FilePath;
        if (!File.Exists(path)) return string.Empty;

        try
        {
            var stored = JsonSerializer.Deserialize<Credentials>(File.ReadAllText(path), Options);
            var text = stored?.TongjiRequest ?? string.Empty;
            // 只记长度：内容含 Cookie
            AppLog.Line($"[credentials] 已读取上次粘贴的请求（{text.Trim().Length} 字符）");
            return text;
        }
        catch (Exception ex)
        {
            AppLog.Line($"[credentials] 读取失败（{ex.GetType().Name}），按没有处理");
            return string.Empty;
        }
    }

    /// <summary>保存粘贴的请求；传空串等于清空（与 Electron 侧一致）。</summary>
    public static void SaveRequest(string requestText)
    {
        var trimmed = (requestText ?? string.Empty).Trim();
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            var payload = trimmed.Length == 0
                ? new Credentials()
                : new Credentials { TongjiRequest = trimmed, SavedAt = DateTimeOffset.Now.ToString("o") };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, Options));
            AppLog.Line($"[credentials] 已保存 {FilePath}（{trimmed.Length} 字符）");
        }
        catch (Exception ex)
        {
            AppLog.Line($"[credentials] 保存失败：{ex.Message}");
        }
    }

    /// <summary>凭据文件的内容（字段名与 Electron 侧相同，故意与它的 JSON 保持同形）。</summary>
    private sealed record Credentials
    {
        public string? TongjiRequest { get; init; }

        public string? SavedAt { get; init; }
    }
}
