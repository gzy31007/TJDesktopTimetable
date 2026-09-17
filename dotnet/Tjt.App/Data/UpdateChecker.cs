using System.Net.Http;
using Tjt.Core;

namespace Tjt.App.Data;

/// <summary>
/// 新版本检查的**网络侧**（规则全在 <see cref="UpdateCheck"/>：解析 / 比较 / 跳过 / 挑资产）。
///
/// <para>策略与 DeskBox 同款：**失败一律静默**（只写一行日志）。检查更新是"顺带做的事"，
/// 没网、被限流、公司代理拦了都不该让用户看到任何错误 —— 更不该影响启动。</para>
///
/// <para><b>gh-proxy.org 兜底</b>（2026-09-17）：国内直连 <c>api.github.com</c> 基本不通，
/// 所以直连失败后用 <see cref="UpdateCheck.ProxyPrefix"/> 前缀重试一次。直连那一轮超时压到
/// <see cref="DirectTimeout"/>（6 秒），免得先白等 20 秒才轮到兜底。</para>
/// </summary>
internal static class UpdateChecker
{
    /// <summary>超时按 DeskBox 的口径（20 秒）：够慢网络拉一个 JSON，又不至于让用户等到以为卡死。</summary>
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>直连那一轮的超时：被墙时通常秒级失败，6 秒足够，也保住启动体验。</summary>
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(6);

    /// <summary>镜像那一轮的超时：镜像在国内可直连，慢也慢不到哪去，给宽一点。</summary>
    private static readonly TimeSpan ProxyTimeout = TimeSpan.FromSeconds(20);

    /// <summary>本机版本（三段，如 <c>1.2.0</c>）—— 与 Release tag 同形，便于比较与展示。</summary>
    public static string CurrentVersion()
    {
        var version = typeof(UpdateChecker).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// 查一次最新正式 Release：先直连，失败则经 gh-proxy.org 兜底重试一轮。
    /// </summary>
    /// <param name="apiUrl">
    /// 覆盖 API 地址（<c>--update-api</c>，验收脚本指向本地合成服务）；<c>null</c> = 真 GitHub。
    /// </param>
    /// <param name="skippedVersion">用户"跳过此版本"记下的版本号。</param>
    /// <param name="proxyPrefix">
    /// 覆盖兜底用的代理前缀（<c>--update-proxy</c>）；<c>null</c> = 默认 gh-proxy.org。
    /// 给出它就**强制启用兜底**（验收脚本用本地合成服务假扮镜像）。
    /// </param>
    /// <returns>结论；网络失败 / 非 2xx / 响应不可解析时返回 <c>null</c>（调用方当作"这次没查到"）。</returns>
    public static async Task<UpdateCheckResult?> CheckAsync(
        string? apiUrl = null,
        string? skippedVersion = null,
        string? proxyPrefix = null)
    {
        var explicitApi = !string.IsNullOrWhiteSpace(apiUrl);
        var url = explicitApi ? apiUrl!.Trim() : UpdateCheck.LatestReleaseApiUrl;

        var (body, source) = await FetchWithFallbackAsync(url, explicitApi, proxyPrefix).ConfigureAwait(false);
        if (body is null) return null;

        var result = UpdateCheck.Evaluate(CurrentVersion(), body, skippedVersion);
        AppLog.Line(
            $"[update] 结果 {result.Status}：本机 {result.Current} / 线上 {result.Latest?.ToString() ?? "?"}"
            + $"（{body.Length} 字节，来源 {source}）");
        return result;
    }

    /// <summary>
    /// 取回响应体：直连一次，失败且在"该兜底"的情况下用镜像前缀再试一次。
    /// </summary>
    /// <returns>响应体与来源标签（日志用）；两轮都失败返回 <c>null</c>。</returns>
    private static async Task<(string? Body, string Source)> FetchWithFallbackAsync(
        string url,
        bool explicitApi,
        string? proxyPrefix)
    {
        var body = await FetchAsync(url, DirectTimeout, "直连").ConfigureAwait(false);
        if (body is not null) return (body, "直连");

        // `--update-api` 是诊断 / 验收开关，指哪打哪，不替它加兜底（否则 D 例那种"故意连不上"
        // 会跑去打真镜像，依赖真网络）。显式给了 `--update-proxy` 才强制兜底。
        if (explicitApi && string.IsNullOrWhiteSpace(proxyPrefix)) return (null, "直连");

        var proxyUrl = string.IsNullOrWhiteSpace(proxyPrefix)
            ? UpdateCheck.ProxiedUrl(url)
            : UpdateCheck.WithProxyPrefix(url, proxyPrefix);
        if (string.Equals(proxyUrl, url, StringComparison.OrdinalIgnoreCase)) return (null, "直连");

        AppLog.Line($"[update] 直连未拿到响应，改用镜像兜底：{proxyUrl}");
        body = await FetchAsync(proxyUrl, ProxyTimeout, "镜像").ConfigureAwait(false);
        return (body, "镜像");
    }

    /// <summary>GET 一次；非 2xx、超时、DNS 失败都返回 <c>null</c>，只留一行日志。</summary>
    private static async Task<string?> FetchAsync(string url, TimeSpan timeout, string label)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub 要求带 User-Agent（不带直接 403）；再带一版当前版本，方便出问题时对方排查
            request.Headers.TryAddWithoutValidation("User-Agent", UpdateCheck.UserAgent(CurrentVersion()));
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Line($"[update] {label} HTTP {(int)response.StatusCode}（{body.Length} 字节）");
                return null;
            }

            return body;
        }
        catch (Exception ex)
        {
            // 静默：无网 / 超时 / DNS 失败都走这里，只留一行日志
            AppLog.Line($"[update] {label}失败（静默）：{ex.Message}");
            return null;
        }
    }
}
