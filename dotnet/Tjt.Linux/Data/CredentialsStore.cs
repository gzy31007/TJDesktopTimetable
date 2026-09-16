using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tjt.Linux.Data;

/// <summary>
/// 上次粘贴的抓取请求（Tjt.App/Data/CredentialsStore.cs 的移植）。
///
/// <para><b>这份文件里有 Cookie</b>：只在导入窗口里回显（方便用户再抓一次），
/// <b>任何日志都不得打印它的内容</b> —— 只记长度。文件结构与 Windows 版一字不差
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

    /// <summary>保存粘贴的请求；传空串等于清空（与 Windows 版一致）。</summary>
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
            RestrictPermissions();
            AppLog.Line($"[credentials] 已保存 {FilePath}（{trimmed.Length} 字符）");
        }
        catch (Exception ex)
        {
            AppLog.Line($"[credentials] 保存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 收紧落盘权限：文件 0600、数据目录 0700。Windows 侧的 %APPDATA% 天生按用户 ACL 保护，
    /// Linux 的 ~/.config 默认 umask 下同机可读，而这份文件里是含 cookie 的整条请求。
    /// 失败只记日志（收紧不了也不能把导入搞失败）。
    /// </summary>
    private static void RestrictPermissions()
    {
        try
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            new DirectoryInfo(SettingsStore.Directory).UnixFileMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }
        catch (Exception ex)
        {
            AppLog.Line($"[credentials] 收紧文件权限失败（{ex.GetType().Name}）");
        }
    }

    /// <summary>凭据文件的内容（字段名与 Windows 版相同，故意与它的 JSON 保持同形）。</summary>
    private sealed record Credentials
    {
        public string? TongjiRequest { get; init; }

        public string? SavedAt { get; init; }
    }
}
