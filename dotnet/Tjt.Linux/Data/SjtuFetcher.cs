using System.Net.Http;
using System.Text.Json;
using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.Linux.Data;

/// <summary>
/// 交大抓取：拿用户粘的那条请求里的 **URL 参数 + Cookie**，改写成"整学期课表 + 教务日历"。
///
/// <para><b>为什么不原样重发</b>：交大课表页按周拉取
/// （<c>/app/stu/lesson/listByWeek?year=…&amp;semester=…&amp;week=…</c>），而按周响应里
/// <c>time</c> 是 <c>null</c> —— 没有"这门课上哪些周"，照它建挂件只会剩本周有课（实测 14 条全如此）。
/// 所以这里只从粘贴的请求里取 <c>year</c>/<c>semester</c> 与 Cookie，然后依次取三份数据：</para>
///
/// <list type="number">
///   <item><c>/app/stu/school/calendar/info</c> —— 当前学期（<c>year</c>/<c>semester</c>/<c>weekNum</c>），
///   粘贴的是按周请求时这两个参数本来就有，拿它只是为了"粘日历请求也能用"；</item>
///   <item><c>/app/stu/lesson/listBySemester</c> —— 整学期课表（带 <c>time</c> 周次文本）；</item>
///   <item><c>/app/stu/school/semester/calendar</c> —— 逐日教务日历（开学日 + 总周数），
///   作为附加输入文件交给适配器（拿不到不算失败，适配器会给一条
///   <c>sjtu.term.startDate</c> 警告并按课表周次反推）。</item>
/// </list>
///
/// <para>请求默认一律打到 <see cref="SjtuTerms.SjtuHost"/>（<see cref="SjtuWebCapture"/> 构造 URL 时
/// 不跟随粘贴内容里的主机）—— 粘错了地址也不会把登录态发去别处。唯一的例外是
/// <c>baseUrl</c> 参数：只有验收脚本（<c>--sjtu-host</c>）会传，用于把三条请求指向本地合成服务。</para>
///
/// <para><b>安全</b>：Cookie 只跟请求头走，日志只记接口名、HTTP 状态、响应大小。</para>
/// </summary>
internal static class SjtuFetcher
{
    /// <summary>单条请求的硬超时（整次抓取最多三条，串行）。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>抓取交大课表（<paramref name="origin"/> 是用户粘贴的那条请求）。</summary>
    /// <param name="origin">用户粘贴的请求（只取 <c>year</c>/<c>semester</c> 与请求头里的 Cookie）。</param>
    /// <param name="cancellationToken">调用方的取消。</param>
    /// <param name="baseUrl">
    /// 覆盖接口主机（只给验收脚本用，<c>--sjtu-host</c> 指向本地合成服务）；<c>null</c> = 真 <c>j.sjtu.edu.cn</c>。
    /// </param>
    public static async Task<TongjiFetchOutcome> FetchAsync(
        HttpRequestSpec origin,
        CancellationToken cancellationToken = default,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(origin);

        var probes = new List<FetchProbe>
        {
            new("请求", $"{origin.Method} {origin.Url}"),
            new("来源", origin.Source),
        };

        var year = SjtuWebCapture.YearOf(origin.Url);
        var semester = SjtuWebCapture.SemesterOf(origin.Url);

        // ① 当前学期信息：粘的是按周请求时 year/semester 已经在 URL 上，这里主要是兜住"粘的是日历请求"
        var infoUrl = SjtuWebCapture.CalendarInfoUrl(origin.Url, baseUrl);
        if (infoUrl is not null)
        {
            var info = await SendAsync(origin, infoUrl, cancellationToken).ConfigureAwait(false);
            probes.Add(new("学期信息 HTTP", info.Status > 0 ? info.Status.ToString() : "失败"));
            if (info.Error is null && info.Status == 200)
            {
                var (infoYear, infoSemester) = ReadTermInfo(info.Text);
                year ??= infoYear;
                semester ??= infoSemester;
            }
        }

        if (string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(semester))
        {
            return TongjiFetcher.Fail(
                "没能确定学期（请求里没有 year/semester，当前学期接口也没返回）。"
                + "请改粘课表页里那条 listBySemester 或 listByWeek 请求。",
                probes);
        }

        probes.Add(new("学期", $"{year}-{semester}"));

        // ② 整学期课表
        var lessonsUrl = SjtuWebCapture.SemesterLessonsUrl(origin.Url, year, semester, baseUrl);
        if (lessonsUrl is null) return TongjiFetcher.Fail("构造整学期课表请求失败。", probes);

        AppLog.Line($"[sjtu] 开始抓取：GET {SjtuWebCapture.ApiPrefix}/stu/lesson/listBySemester（学期 {year}-{semester}）");
        var lessons = await SendAsync(origin, lessonsUrl, cancellationToken).ConfigureAwait(false);
        if (lessons.Error is not null) return TongjiFetcher.Fail(lessons.Error, probes);

        probes.Add(new("课表 HTTP", lessons.Status.ToString()));
        probes.Add(new("响应大小", $"{lessons.Text.Length} 字节"));
        if (lessons.Status != 200)
        {
            return TongjiFetcher.Fail(StatusMessage(lessons.Status, lessons.Text), probes);
        }

        var verdict = SjtuResponseProbe.Inspect(lessons.Text);
        probes.Add(new("数据识别", verdict.Note));
        if (!verdict.Ok)
        {
            return TongjiFetcher.Fail(
                $"这份响应里没有课表数据（{verdict.Note}）。请确认登录态还有效，并在课表页刷新后重试。",
                probes);
        }

        // ③ 教务日历（可选：拿不到只是学期退化，不影响课程本身）
        IReadOnlyList<ImportFile>? files = null;
        var calendarUrl = SjtuWebCapture.SemesterCalendarUrl(origin.Url, baseUrl);
        if (calendarUrl is not null)
        {
            var calendar = await SendAsync(origin, calendarUrl, cancellationToken).ConfigureAwait(false);
            if (calendar.Error is null && calendar.Status == 200)
            {
                var calendarVerdict = SjtuResponseProbe.InspectCalendar(calendar.Text);
                probes.Add(new("教务日历", calendarVerdict.Note));
                if (calendarVerdict.Ok)
                {
                    files = [new ImportFile("sjtu-semester-calendar.json", calendar.Text)];
                }
            }
            else
            {
                probes.Add(new("教务日历", "拿不到（学期按课表周次反推）"));
            }
        }

        var termId = SjtuTerms.JoinTermId(year, semester);
        AppLog.Line($"[sjtu] 抓取成功：{verdict.Note}，{lessons.Text.Length} 字节，学期={termId ?? "无"}，日历={(files is null ? "无" : "有")}");
        return new TongjiFetchOutcome(
            true,
            $"获取成功：{verdict.Note}，已交给解析器。",
            lessons.Text,
            probes,
            termId,
            SjtuStudentAdapter.AdapterId,
            files);
    }

    /// <summary>
    /// 发一条 GET（沿用粘贴请求的全部请求头 —— 里面就有 Cookie，另有 UA / Referer 等）。
    ///
    /// <para>请求头里 <c>host</c>/<c>content-length</c>/<c>accept-encoding</c>/<c>content-type</c>
    /// 由 <see cref="TongjiFetcher.BuildRequest"/> 统一丢掉（HttpClient 自己算），与同济那条路同规则。</para>
    /// </summary>
    private static async Task<(int Status, string Text, string? Error)> SendAsync(
        HttpRequestSpec origin,
        string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (-1, string.Empty, $"请求地址不合法：{url}");

        var headers = new Dictionary<string, string>(origin.Headers, StringComparer.OrdinalIgnoreCase);
        var spec = new HttpRequestSpec(url, "GET", headers, null, origin.Source);

        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true })
            {
                Timeout = Timeout,
            };
            using var request = TongjiFetcher.BuildRequest(spec, uri);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, text, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (-2, string.Empty, $"请求超时（{(int)Timeout.TotalSeconds} 秒）：网络不通或交大站点不可达。");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Line($"[sjtu] 请求异常：{ex.GetType().Name}");
            return (-3, string.Empty, $"请求失败：{ex.Message}");
        }
    }

    /// <summary>读 <c>calendar/info</c> 的 <c>data.year</c> / <c>data.semester</c>。</summary>
    private static (string? Year, string? Semester) ReadTermInfo(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return (null, null);

            var year = data.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.String ? y.GetString() : null;
            var semester = data.TryGetProperty("semester", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return (year, semester);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>非 200 时说人话（401/403 单独提"登录态"）。</summary>
    private static string StatusMessage(int status, string text)
    {
        if (status is 401 or 403)
        {
            return "登录态已失效（HTTP 401/403）：请在浏览器里重新登录交大「学在交大」，再复制一次请求。";
        }

        var detail = text.Length > 0 ? $"：{Truncate(text, 120)}" : string.Empty;
        return $"服务端返回 HTTP {status}{detail}";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
