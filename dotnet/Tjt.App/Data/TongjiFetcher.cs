using System.Net.Http;
using System.Text;
using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.App.Data;

/// <summary>抓取过程里给用户看的一条探测结果（如"HTTP 200"、"响应 128 KB"）。</summary>
internal sealed record FetchProbe(string Label, string Value);

/// <summary>
/// 一次抓取的结果：失败时 <see cref="Message"/> 直接显示给用户。
///
/// <para><see cref="AdapterId"/> 与 <see cref="Files"/> 是为**交大**加的：交大一张课表要两份数据
/// （课程 + 教务日历），而且必须显式点名适配器（响应形状与同济完全不同，靠自动探测也能中，但没必要赌）。
/// 同济那条路保持默认值，行为不变。</para>
/// </summary>
internal sealed record TongjiFetchOutcome(
    bool Ok,
    string Message,
    string? TimetableText,
    IReadOnlyList<FetchProbe> Probes,
    string? TermId = null,
    string? AdapterId = null,
    IReadOnlyList<ImportFile>? Files = null);

/// <summary>
/// 用「用户从浏览器复制出来的请求」抓取个人课表（TS 侧 <c>apps/desktop/src/main/tongji.ts</c> 的移植）。
///
/// <para>为什么不猜接口路径：1 系统的个人课表来自选课服务
/// <c>POST /api/electionservice/student/{id}/getDataBk</c>，其中 <c>{id}</c> 是选课批次相关的内部 id，
/// 无法稳定构造。让用户 F12 → Copy as cURL 粘一次最可靠，也天然带上了 Cookie 与 <c>x-token</c>。</para>
///
/// <para><b>安全</b>：Cookie 只在内存里过一遍，<b>任何日志都不打印它的内容</b>；
/// 请求头也不进日志（只记 URL、HTTP 状态、响应大小）。</para>
///
/// <para>解析请求用 core 的 <see cref="HttpRequestParser"/>（纯函数、有单测），
/// 这里只负责"把解析出来的东西发出去"和"把响应说清楚"。</para>
/// </summary>
internal static class TongjiFetcher
{
    /// <summary>粘贴内容里没带 user-agent 时的兜底（与 Electron 侧同一串）。</summary>
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>整次抓取的硬超时。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 把**已经解析好的**请求发出去（<see cref="SchoolFetcher"/> 与内置登录窗口共用这一条）。
    ///
    /// <para>内置登录窗口为什么要用它：课上那条报表接口被 WebView2 拦到时，多数情况能直接读到响应体，
    /// 但 POST 响应（旧 <c>getDataBk</c>）读不到 body —— 那时就用页面自己的 cookie 把同一个 GET
    /// 重发一次。两条路都只在这里发请求，解析/探测/文案不会分叉。</para>
    /// </summary>
    /// <param name="spec">请求规格（<c>Source</c> 只进探测行与日志，用于说清"这条请求从哪来"）。</param>
    /// <param name="cancellationToken">调用方的取消。</param>
    public static async Task<TongjiFetchOutcome> FetchSpecAsync(HttpRequestSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!Uri.TryCreate(spec.Url, UriKind.Absolute, out var uri))
        {
            return Fail($"解析出来的请求地址不合法：{spec.Url}");
        }

        // 只记"要打哪里"，不记请求头（里面有 Cookie 与 x-token）
        AppLog.Line($"[tongji] 开始抓取：{spec.Method} {uri.Host}{uri.AbsolutePath}（来源 {spec.Source}）");

        var probes = new List<FetchProbe>
        {
            new("请求", $"{spec.Method} {spec.Url}"),
            new("来源", spec.Source),
        };

        string text;
        int status;
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true })
            {
                Timeout = Timeout,
            };
            using var request = BuildRequest(spec, uri);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail($"请求超时（{(int)Timeout.TotalSeconds} 秒）：网络不通或代理拦截了 1 系统。", probes);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Line($"[tongji] 请求异常：{ex.GetType().Name}");
            return Fail($"请求失败：{ex.Message}", probes);
        }

        probes.Add(new("HTTP", status.ToString()));
        probes.Add(new("响应大小", $"{text.Length} 字节"));

        if (status != 200)
        {
            var detail = text.Length > 0 ? $"：{Truncate(text, 120)}" : string.Empty;
            var message = status is 401 or 403
                ? "登录态已失效（HTTP 401/403）：请重新登录 1 系统，再复制一次请求。"
                : $"服务端返回 HTTP {status}{detail}";
            return Fail(message, probes);
        }

        var verdict = TongjiResponseProbe.Inspect(text);
        probes.Add(new("数据识别", verdict.Note));
        if (!verdict.Ok)
        {
            return Fail(
                $"这份响应里没有个人课表数据（{verdict.Note}）。请在课表/选课页面刷新后，复制其中那条返回 200 且体积较大的请求。",
                probes);
        }

        // 学期 id 在 URL 上（报表接口的响应体里没有 calendarId），带上它才能定位开学日期
        var termId = HttpRequestParser.QueryValue(spec, "calendarId");
        if (!string.IsNullOrEmpty(termId)) probes.Add(new FetchProbe("学期", termId));
        AppLog.Line($"[tongji] 抓取成功：{verdict.Note}，{text.Length} 字节，calendarId={termId ?? "无"}");
        return new TongjiFetchOutcome(true, $"获取成功：{verdict.Note}，已交给解析器。", text, probes, termId);
    }

    /// <summary>
    /// 把解析出来的请求规格变成一次真实的 HTTP 请求。
    ///
    /// <para>细节：<c>host</c> / <c>content-length</c> / <c>accept-encoding</c> 一律丢掉
    /// （HttpClient 自己算，照抄反而会打架）；<c>content-type</c> 属于内容头，
    /// 必须挂在 <c>Content</c> 上 —— 用 <c>StringContent</c> 的默认值会让服务端按
    /// <c>text/plain</c> 收 JSON（与 Electron 侧"照抄请求头"的语义保持一致）。</para>
    /// </summary>
    internal static HttpRequestMessage BuildRequest(HttpRequestSpec spec, Uri uri)
    {
        var request = new HttpRequestMessage(new HttpMethod(spec.Method), uri);

        foreach (var (key, value) in spec.Headers)
        {
            if (key is "host" or "content-length" or "accept-encoding" or "content-type") continue;
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (!request.Headers.Contains("user-agent")) request.Headers.TryAddWithoutValidation("user-agent", DefaultUserAgent);
        if (!request.Headers.Contains("accept")) request.Headers.TryAddWithoutValidation("accept", "application/json, text/plain, */*");

        if (spec.Body is not null)
        {
            var content = new StringContent(spec.Body, Encoding.UTF8);
            content.Headers.Remove("Content-Type");
            if (spec.Headers.TryGetValue("content-type", out var contentType))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            request.Content = content;
        }

        return request;
    }

    internal static TongjiFetchOutcome Fail(string message, IReadOnlyList<FetchProbe>? probes = null) =>
        new(false, message, null, probes ?? []);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
