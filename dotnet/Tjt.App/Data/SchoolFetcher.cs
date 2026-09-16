using Tjt.Core;

namespace Tjt.App.Data;

/// <summary>
/// 「用户粘一条浏览器请求」这条路的**分派器**：先解析请求，再按**主机**交给对应学校的抓取实现。
///
/// <para>为什么要分：同济与交大的抓取方式根本不同 —— 同济是把粘的那条请求**原样重发**
/// （接口 id 无法构造），交大则是从请求里取出 <c>year</c>/<c>semester</c> 后**改写成整学期接口**
/// （课表页默认的按周接口不带周次信息，见 <see cref="SjtuWebCapture"/>）。
/// 唯一共用的是"解析粘贴内容"与"没有 Cookie 时怎么把用户说明白"这两段。</para>
///
/// <para><b>安全</b>：Cookie 只在内存里过一遍，任何日志都不打印它的内容；请求头也不进日志
/// （只记 URL、HTTP 状态、响应大小）。</para>
/// </summary>
internal static class SchoolFetcher
{
    /// <summary>抓取：解析粘贴的请求 → 按主机分派 → 检查响应像不像课表数据。</summary>
    /// <param name="requestText">粘贴的请求全文（含 Cookie）。</param>
    /// <param name="cancellationToken">调用方的取消（设置窗口关掉时用）。</param>
    /// <param name="sjtuBaseUrl">
    /// 覆盖交大接口主机（只给验收脚本用：指向本地合成服务）；<c>null</c> = 真 <c>j.sjtu.edu.cn</c>。
    /// </param>
    public static async Task<TongjiFetchOutcome> FetchAsync(
        string? requestText,
        CancellationToken cancellationToken = default,
        string? sjtuBaseUrl = null)
    {
        var text = requestText ?? string.Empty;
        var spec = HttpRequestParser.Parse(text);
        if (spec is null)
        {
            return TongjiFetcher.Fail("没能从粘贴的内容里解析出请求。请在 F12 → Network 里右键该请求 → Copy → Copy as cURL，然后原样粘贴到上面。");
        }

        if (HttpRequestParser.CookieOf(spec).Length == 0)
        {
            // 实测用户最容易犯的错：只把第一行（URL）复制进去了，于是"没有 Cookie"——
            // 这句话必须把他引回"整条命令"，否则他会以为是程序不认这条请求。
            var onlyUrl = !text.Contains("-H", StringComparison.Ordinal)
                && !text.Contains("--header", StringComparison.Ordinal)
                && !text.Contains("-b ", StringComparison.Ordinal)
                && !text.Contains("Invoke-", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("$session", StringComparison.Ordinal);

            return TongjiFetcher.Fail(onlyUrl
                ? "粘贴的内容里只有地址、没有请求头，所以没有 Cookie。看起来只复制了第一行 —— "
                  + "请在该请求上右键 → Copy → Copy as cURL，把整条命令（含 -H 'cookie: …' 那些行）一起粘贴过来。"
                : "粘贴的请求里没有 Cookie：请用「Copy as cURL」（会带上全部请求头），而不是只复制 URL。");
        }

        if (SjtuWebCapture.IsSjtuUrl(spec.Url))
        {
            // 只记接口名与主机，不记请求头
            AppLog.Line($"[sjtu] 粘贴的是交大请求：{SjtuWebCapture.EndpointLabel(spec.Url)}（{HostOf(spec.Url)}）");
            return await SjtuFetcher.FetchAsync(spec, cancellationToken, sjtuBaseUrl).ConfigureAwait(false);
        }

        AppLog.Line($"[tongji] 粘贴的是同济请求：{HostOf(spec.Url)}");
        return await TongjiFetcher.FetchSpecAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只取主机名（日志里够用，且不带路径参数）。</summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(无法解析)";
}
