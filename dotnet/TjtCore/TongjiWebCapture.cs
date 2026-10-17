namespace Tjt.Core;

/// <summary>内置登录窗口里读到的一条 cookie（只在内存里拼请求头，<b>绝不落日志</b>）。</summary>
public sealed record WebCookie(string Name, string Value, string Domain, string Path);

/// <summary>一次"从内置登录窗口捕获课表响应"的判定结果（<c>Ok=false</c> 时 <see cref="Note"/> 给用户看）。</summary>
public sealed record WebCaptureVerdict(bool Ok, string Note, string? TermId);

/// <summary>
/// 这一份课表是从哪个入口取的。两条入口的**登录态来源完全不同**，所以"登录失效了该怎么办"也不同：
/// <see cref="PastedRequest"/> 的登录态在用户的浏览器里（只能请回浏览器重新登录、重新复制请求）；
/// <see cref="BuiltInLogin"/> 的登录态在应用内那个登录窗口里（只能关掉重开）。
/// </summary>
/// <remarks>交大与同济共用这一套入口语义（见 <see cref="TongjiWebCapture.RecoveryHint"/>）。</remarks>
public enum FetchEntry
{
    /// <summary>用户从浏览器粘贴进来的一条请求（<c>Copy as cURL</c>）。</summary>
    PastedRequest,

    /// <summary>应用内 WebView2 登录窗口（自动捕获，或用窗口里的 cookie 主动取）。</summary>
    BuiltInLogin,
}

/// <summary>
/// 内置登录窗口（WebView2）背后的**纯逻辑**：这条响应是不是课表、学期 id 在不在 URL 上、
/// cookie 该拼成什么请求头。
///
/// <para>放 core 的理由与 <see cref="TongjiResponseProbe"/> 一样：这些判断不碰窗口、不碰网络，
/// 放这里就能在 Linux 上单测；外壳（<c>Tjt.App</c>）只剩"把 WebView2 的事件喂进来"。</para>
///
/// <para><b>为什么不自己拼课表请求</b>：课表页那条接口要 <c>studentCode</c>（前端加密过的 uid），
/// 算法在前端 bundle 里、会随发版变。所以这里**只做旁观者** —— 用户在页面里点开「我的课表」，
/// 页面自己发那条请求，我们从响应里把数据接过来（URL 上的 <c>calendarId</c> 顺手一起接）。</para>
/// </summary>
public static class TongjiWebCapture
{
    /// <summary>1 系统主机名（课表接口都在它下面）。</summary>
    public const string TongjiHost = "1.tongji.edu.cn";

    /// <summary>1 系统站点根（WebView2 的初始导航目标）。</summary>
    public const string TongjiOrigin = "https://" + TongjiHost;

    /// <summary>
    /// 内置登录窗口里任何一次获取失败后的恢复路径（**只有这一条**：关掉窗口重来）。
    ///
    /// <para>刻意不提"点「重新加载页面」"——那个按钮只是让页面重新加载，不会从头开始一次获取；
    /// 把重新加载当恢复手段，用户会在一个已经失效的窗口里反复点。</para>
    /// </summary>
    public const string BuiltInLoginRestartGuidance =
        "请关掉这个登录窗口，再从「导入课表」页点「登录并获取课表」重新打开一个窗口重新登录。";

    /// <summary>
    /// 这个 HTTP 状态码算不算"有证据的登录态失效"。
    ///
    /// <para>只有 401/403 才算。**只看状态码，不看响应体文案**：正文里出现"登录""失效"之类的字样
    /// 可能只是接口自己的提示或一段 HTML 错误页，据此断言"你的登录过期了"会把网络故障、
    /// 选错了请求等问题都误导成登录问题。</para>
    /// </summary>
    public static bool IsLoginExpired(int status) => status is 401 or 403;

    /// <summary>
    /// 失败后"接下来该做什么"。按入口分岔：粘贴入口用调用方给的提示（回到学校网页重新登录、
    /// 重新复制请求）；内置登录入口一律返回 <see cref="BuiltInLoginRestartGuidance"/>。
    /// </summary>
    /// <param name="entry">这份课表是从哪个入口取的。</param>
    /// <param name="pastedHint">粘贴入口专属的下一步（例如"请到 1.tongji.edu.cn 重新登录并重新复制请求"）。</param>
    public static string RecoveryHint(FetchEntry entry, string pastedHint) =>
        entry == FetchEntry.BuiltInLogin ? BuiltInLoginRestartGuidance : pastedHint;

    /// <summary>课表接口的路径特征（小写比较）。三条都是实测/前端源码里的真路径（详见 AGENTS.md「两条接口两种包法」）。</summary>
    private static readonly (string Fragment, string Label)[] Endpoints =
    [
        ("findstudenttimetab", "报表接口 findStudentTimetab"),
        ("findschooltimetab2", "研究生报表接口 findSchoolTimetab2"),
        ("getdatabk", "选课服务 getDataBk"),
    ];

    /// <summary>这条 URL 是不是课表接口（大小写不敏感，只看路径特征）。</summary>
    public static bool IsEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.ToLowerInvariant();
        foreach (var (fragment, _) in Endpoints)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>接口的可读名字（日志与探测行用；认不出时给"未知接口"）。</summary>
    public static string EndpointLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "未知接口";
        var lower = url.ToLowerInvariant();
        foreach (var (fragment, label) in Endpoints)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal)) return label;
        }

        return "未知接口";
    }

    /// <summary>这个主机名是不是同济（<c>1.tongji.edu.cn</c> 及其它 <c>*.tongji.edu.cn</c>）。</summary>
    public static bool IsTongjiHost(string? host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('.');
        if (value.Length == 0) return false;
        return value.Equals("tongji.edu.cn", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".tongji.edu.cn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这条 URL 是不是同济站点（含 http/https 之外的协议一律不算）。</summary>
    public static bool IsTongjiUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        return IsTongjiHost(uri.Host);
    }

    /// <summary>取 <c>calendarId</c>（学期 id）：报表接口把它放在 URL 上，响应体里没有。</summary>
    public static string? CalendarIdOf(string? url) => QueryValue(url, "calendarId");

    /// <summary>取查询参数（URL 解码后；参数不存在或值为空时返回 <c>null</c>）。</summary>
    public static string? QueryValue(string? url, string name)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var query = uri.Query;
        if (query.Length <= 1) return null;

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var key = index < 0 ? pair : pair[..index];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            var raw = index < 0 ? string.Empty : pair[(index + 1)..];
            var value = Uri.UnescapeDataString(raw.Replace('+', ' ')).Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>
    /// 把 WebView2 报来的 cookie 拼成 <c>Cookie</c> 请求头（RFC 6265 的简化版：域匹配 + 路径匹配 +
    /// 同名取路径更长的那个）。**返回值含登录态，只许进请求头，不许进日志。**
    /// </summary>
    public static string CookieHeader(string? url, IEnumerable<WebCookie>? cookies)
    {
        if (cookies is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;

        var host = uri.Host;
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;

        var matched = cookies
            .Where(cookie => !string.IsNullOrEmpty(cookie.Name))
            .Where(cookie => DomainMatches(host, cookie.Domain))
            .Where(cookie => PathMatches(path, cookie.Path))
            .OrderByDescending(cookie => (cookie.Path ?? "/").Length)
            .ToList();

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cookie in matched)
        {
            // 同名 cookie 只发一个：上面按路径长度降序排过，先到的就是"更具体"的那个
            if (!seen.Add(cookie.Name)) continue;
            parts.Add($"{cookie.Name}={cookie.Value}");
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// 判定一条被 WebView2 拦到的响应：URL 得是课表接口，响应体得"像课表数据"。
    /// </summary>
    public static WebCaptureVerdict Inspect(string? url, string? body)
    {
        if (!IsEndpoint(url)) return new WebCaptureVerdict(false, "不是课表接口（已忽略）", null);

        var verdict = TongjiResponseProbe.Inspect(body);
        if (!verdict.Ok) return new WebCaptureVerdict(false, verdict.Note, null);
        return new WebCaptureVerdict(true, verdict.Note, CalendarIdOf(url));
    }

    /// <summary>给日志用的一句话（**不含 cookie、不含响应体**）。</summary>
    public static string Describe(string? url, int bodyLength)
    {
        var label = EndpointLabel(url);
        var calendarId = CalendarIdOf(url) ?? "无";
        return $"{label}，{bodyLength} 字节，calendarId={calendarId}";
    }

    private static bool DomainMatches(string host, string? domain)
    {
        var value = (domain ?? string.Empty).Trim().TrimStart('.');
        if (value.Length == 0) return false;
        return host.Equals(value, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathMatches(string requestPath, string? cookiePath)
    {
        var value = string.IsNullOrEmpty(cookiePath) ? "/" : cookiePath;
        if (value == "/") return true;
        if (!requestPath.StartsWith(value, StringComparison.Ordinal)) return false;

        // RFC 6265：前缀相同还不算命中，下一个字符得是 '/'（/api 不匹配 /apix）
        return requestPath.Length == value.Length || value.EndsWith('/') || requestPath[value.Length] == '/';
    }
}
