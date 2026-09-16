namespace Tjt.Core;

/// <summary>内置登录窗口从交大页面接住的一条响应属于哪一类。</summary>
public enum SjtuCaptureKind
{
    /// <summary>不是我们要的接口。</summary>
    None,

    /// <summary>课表（<c>listBySemester</c> / <c>listByWeek</c>）。</summary>
    Timetable,

    /// <summary>教务日历（<c>semester/calendar</c>）—— 开学日与总周数从它来。</summary>
    Calendar,
}

/// <summary>一条交大响应的判定结果（<c>Ok=false</c> 时 <see cref="Note"/> 给用户看）。</summary>
public sealed record SjtuCaptureVerdict(bool Ok, SjtuCaptureKind Kind, string Note, string? TermId = null);

/// <summary>
/// 交大（<c>j.sjtu.edu.cn</c>）站点纯逻辑：哪些 URL 是课表接口、学期参数在哪、
/// 该怎么**改写**成"整学期"那条请求。
///
/// <para>放 core 的理由与 <see cref="TongjiWebCapture"/> 完全一样：不碰窗口、不碰网络，
/// Linux 上就能单测；外壳只剩"把请求发出去 / 把 WebView2 的事件喂进来"。</para>
///
/// <para><b>为什么抓取时要改写请求</b>：交大课表页按周拉取
/// （<c>/app/stu/lesson/listByWeek</c>），而按周的响应里 <c>time</c> 是 <c>null</c>、
/// 没有"这门课上哪些周"的信息（实测 14 条全是 <c>time:null</c>）——
/// 照它建出来的挂件会变成"只有本周有课"。整学期的
/// <c>/app/stu/lesson/listBySemester</c> 才带 <c>time="1-16周"</c>。
/// 所以拿到用户粘的那条请求后，只取 <c>year</c>/<c>semester</c> 两个参数**重发整学期接口**，
/// 顺带再取一次教务日历（开学日 / 总周数）。</para>
/// </summary>
public static class SjtuWebCapture
{
    /// <summary>课表接口路径特征（<c>listBySemester</c> 优先于 <c>listByWeek</c> 只是标签问题，两个都收）。</summary>
    private static readonly (string Fragment, string Label)[] TimetableEndpoints =
    [
        ("/stu/lesson/listbysemester", "整学期课表 listBySemester"),
        ("/stu/lesson/listbyweek", "按周课表 listByWeek"),
    ];

    /// <summary>教务日历接口（学期第一周 / 总周数）。</summary>
    private const string CalendarEndpoint = "/stu/school/semester/calendar";

    /// <summary>应用接口前缀（前端 <c>baseURL:"/app"</c>）。</summary>
    public const string ApiPrefix = "/app";

    /// <summary>这个主机名是不是交大（<c>j.sjtu.edu.cn</c> 及其它 <c>*.sjtu.edu.cn</c>）。</summary>
    public static bool IsSjtuHost(string? host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('.');
        if (value.Length == 0) return false;
        return value.Equals("sjtu.edu.cn", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".sjtu.edu.cn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这条 URL 是不是交大站点（只认 http/https）。</summary>
    public static bool IsSjtuUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        return IsSjtuHost(uri.Host);
    }

    /// <summary>这条 URL 是不是课表接口（只看路径特征，大小写不敏感）。</summary>
    public static bool IsTimetableEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url!.ToLowerInvariant();
        foreach (var (fragment, _) in TimetableEndpoints)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>这条 URL 是不是教务日历接口。</summary>
    public static bool IsCalendarEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url!.ToLowerInvariant().Contains(CalendarEndpoint, StringComparison.Ordinal);
    }

    /// <summary>接口的可读名字（日志与探测行用）。</summary>
    public static string EndpointLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "未知接口";
        var lower = url!.ToLowerInvariant();
        foreach (var (fragment, label) in TimetableEndpoints)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal)) return label;
        }

        if (lower.Contains(CalendarEndpoint, StringComparison.Ordinal)) return "教务日历 semester/calendar";
        return "未知接口";
    }

    /// <summary>取查询参数（与 <see cref="TongjiWebCapture.QueryValue"/> 同一实现，避免两套 URL 解析）。</summary>
    public static string? QueryValue(string? url, string name) => TongjiWebCapture.QueryValue(url, name);

    /// <summary>从请求 URL 取学期参数 <c>year</c>（如 <c>2026-2027</c>）。</summary>
    public static string? YearOf(string? url) => QueryValue(url, "year");

    /// <summary>从请求 URL 取学期序号 <c>semester</c>（如 <c>1</c>）。</summary>
    public static string? SemesterOf(string? url) => QueryValue(url, "semester");

    /// <summary>
    /// 拼"整学期课表"请求地址。参数只有 <c>year</c>/<c>semester</c>（外加前端固定带的 <c>locale</c>），
    /// <c>userId</c> 实测前端自己也传 <c>undefined</c>（被 axios 丢掉），所以不传。
    /// </summary>
    public static string? SemesterLessonsUrl(string? originOrUrl, string? year, string? semester, string? baseUrl = null) =>
        Build(originOrUrl, "/stu/lesson/listBySemester", year, semester, withTerm: true, baseUrl: baseUrl);

    /// <summary>拼"按周课表"请求地址（保留着给诊断用；正式导入走整学期那条）。</summary>
    public static string? WeekLessonsUrl(string? originOrUrl, string? year, string? semester, string? week, string? baseUrl = null) =>
        Build(originOrUrl, "/stu/lesson/listByWeek", year, semester, withTerm: true, week: week, baseUrl: baseUrl);

    /// <summary>拼教务日历请求地址。</summary>
    public static string? SemesterCalendarUrl(string? originOrUrl, string? baseUrl = null) =>
        Build(originOrUrl, CalendarEndpoint, null, null, withTerm: false, baseUrl: baseUrl);

    /// <summary>拼"当前学期信息"请求地址（含 <c>weekNum</c> 与 <c>weekList</c>）。</summary>
    public static string? CalendarInfoUrl(string? originOrUrl, string? baseUrl = null) =>
        Build(originOrUrl, "/stu/school/calendar/info", null, null, withTerm: false, baseUrl: baseUrl);

    /// <summary>
    /// 判定一条被拦到的响应：URL 得是课表 / 日历接口，响应体得"像"那一类数据。
    /// </summary>
    public static SjtuCaptureVerdict Inspect(string? url, string? body)
    {
        if (IsCalendarEndpoint(url))
        {
            var parsed = SjtuResponseProbe.InspectCalendar(body);
            return new SjtuCaptureVerdict(
                parsed.Ok,
                parsed.Ok ? SjtuCaptureKind.Calendar : SjtuCaptureKind.None,
                parsed.Note,
                null);
        }

        if (!IsTimetableEndpoint(url)) return new SjtuCaptureVerdict(false, SjtuCaptureKind.None, "不是课表接口（已忽略）");

        var verdict = SjtuResponseProbe.Inspect(body);
        if (!verdict.Ok) return new SjtuCaptureVerdict(false, SjtuCaptureKind.None, verdict.Note);

        var termId = SjtuTerms.JoinTermId(YearOf(url), SemesterOf(url));
        return new SjtuCaptureVerdict(true, SjtuCaptureKind.Timetable, verdict.Note, termId);
    }

    /// <summary>给日志用的一句话（**不含 cookie、不含响应体**）。</summary>
    public static string Describe(string? url, int bodyLength)
    {
        var label = EndpointLabel(url);
        var termId = SjtuTerms.JoinTermId(YearOf(url), SemesterOf(url)) ?? "无";
        return $"{label}，{bodyLength} 字节，学期={termId}";
    }

    /// <summary>
    /// 拼一个交大接口 URL。
    ///
    /// <para>宿主机一律用 <see cref="SjtuTerms.SjtuHost"/>（**不跟随粘贴请求里的主机**，
    /// 否则粘错地址就会把登录态发去别处）。</para>
    ///
    /// <para><paramref name="baseUrl"/> 是给**验收脚本**的显式覆盖（<c>--sjtu-host</c>，指向本地合成
    /// 服务，与更新检查的 <c>--update-api</c> 同一套路）：只有调用方主动传值时才生效，
    /// 产品路径永远不传它。</para>
    /// </summary>
    private static string? Build(string? originOrUrl, string path, string? year, string? semester, bool withTerm, string? week = null, string? baseUrl = null)
    {
        // 允许传 origin、完整 URL 或 null；只取 scheme/host/port，路径与查询一律重建
        var scheme = "https";
        var authority = SjtuTerms.SjtuHost;

        if (!string.IsNullOrWhiteSpace(baseUrl)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var over)
            && over.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(over.Host))
        {
            scheme = over.Scheme;
            authority = over.IsDefaultPort ? over.Host : $"{over.Host}:{over.Port}";
        }
        else if (!string.IsNullOrWhiteSpace(originOrUrl))
        {
            var candidate = originOrUrl!.Contains("://", StringComparison.Ordinal)
                ? originOrUrl
                : "https://" + originOrUrl;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && IsSjtuHost(uri.Host))
            {
                scheme = uri.Scheme;
                authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            }
        }

        var query = new List<string>();
        if (withTerm)
        {
            if (string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(semester)) return null;
            query.Add("locale=zh");
            query.Add($"year={Uri.EscapeDataString(year!.Trim())}");
            query.Add($"semester={Uri.EscapeDataString(semester!.Trim())}");
            if (!string.IsNullOrWhiteSpace(week)) query.Add($"week={Uri.EscapeDataString(week!.Trim())}");
        }
        else
        {
            query.Add("locale=zh");
        }

        return $"{scheme}://{authority}{ApiPrefix}{path}?{string.Join('&', query)}";
    }

    /// <summary>从这个 URL 里取 <c>week</c>（按周接口才有）。</summary>
    public static string? WeekOf(string? url) => QueryValue(url, "week");
}
