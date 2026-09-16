using System.Net.Http;
using Tjt.Core;

namespace Tjt.App.Data;

/// <summary>
/// 新版本检查的**网络侧**（规则全在 <see cref="UpdateCheck"/>：解析 / 比较 / 跳过 / 挑资产）。
///
/// <para>策略与 DeskBox 同款：**失败一律静默**（只写一行日志）。检查更新是"顺带做的事"，
/// 没网、被限流、公司代理拦了都不该让用户看到任何错误 —— 更不该影响启动。</para>
/// </summary>
internal static class UpdateChecker
{
    /// <summary>超时按 DeskBox 的口径（20 秒）：够慢网络拉一个 JSON，又不至于让用户等到以为卡死。</summary>
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>本机版本（三段，如 <c>1.2.0</c>）—— 与 Release tag 同形，便于比较与展示。</summary>
    public static string CurrentVersion()
    {
        var version = typeof(UpdateChecker).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// 查一次最新正式 Release。
    /// </summary>
    /// <param name="apiUrl">覆盖 API 地址（<c>--update-api</c>，验收脚本指向本地合成服务）；<c>null</c> = 真 GitHub。</param>
    /// <param name="skippedVersion">用户"跳过此版本"记下的版本号。</param>
    /// <returns>结论；网络失败 / 非 2xx / 响应不可解析时返回 <c>null</c>（调用方当作"这次没查到"）。</returns>
    public static async Task<UpdateCheckResult?> CheckAsync(string? apiUrl = null, string? skippedVersion = null)
    {
        var url = string.IsNullOrWhiteSpace(apiUrl) ? UpdateCheck.LatestReleaseApiUrl : apiUrl.Trim();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub 要求带 User-Agent（不带直接 403）；再带一版当前版本，方便出问题时对方排查
            request.Headers.TryAddWithoutValidation("User-Agent", UpdateCheck.UserAgent(CurrentVersion()));
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Line($"[update] HTTP {(int)response.StatusCode}（{body.Length} 字节）");
                return null;
            }

            var result = UpdateCheck.Evaluate(CurrentVersion(), body, skippedVersion);
            AppLog.Line(
                $"[update] 结果 {result.Status}：本机 {result.Current} / 线上 {result.Latest?.ToString() ?? "?"}（{body.Length} 字节）");
            return result;
        }
        catch (Exception ex)
        {
            // 静默：无网 / 超时 / DNS 失败都走这里，只留一行日志
            AppLog.Line($"[update] 检查失败（静默）：{ex.Message}");
            return null;
        }
    }
}
