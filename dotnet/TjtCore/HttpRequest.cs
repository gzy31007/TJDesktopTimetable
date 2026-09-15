using System.Text;
using System.Text.RegularExpressions;

namespace Tjt.Core;

/// <summary>
/// 一条"从浏览器复制出来的请求"（TS 侧 <c>HttpRequestSpec</c> 的移植）。
///
/// <para><see cref="Headers"/> 的键一律小写（与 TS 侧一致），取值查找用
/// <see cref="StringComparer.OrdinalIgnoreCase"/>，两种写法都能取到。</para>
/// </summary>
public sealed record HttpRequestSpec(
    string Url,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    string Source);

/// <summary>
/// 解析"用户从浏览器复制出来的请求"，用于替代猜接口路径
/// （TS 侧 <c>packages/core/src/http-request.ts</c> 的逐行移植）。
///
/// <para>支持三种输入：① F12 → Copy as cURL（bash）粘贴的命令行；② PowerShell 的
/// <c>Invoke-WebRequest ... -WebSession $session</c> 片段（含 <c>$session.Cookies.Add(...)</c>
/// 与 <c>-Headers @{...}</c>）；③ 只贴一个 <c>https://...</c> 地址（无 Cookie，实际会 401，仅作兜底）。</para>
///
/// <para><b>这是纯函数</b>，方便单测；真正的网络请求由外壳（<c>Tjt.App</c>）负责 ——
/// 与 TS 侧"core 只解析、main 只发请求"的分工完全一致。</para>
/// </summary>
public static partial class HttpRequestParser
{
    /// <summary>只贴一个 URL 时用的来源标记。</summary>
    public const string SourceUrl = "url";

    /// <summary>cURL 命令行的来源标记。</summary>
    public const string SourceCurl = "curl";

    /// <summary>PowerShell 片段的来源标记。</summary>
    public const string SourcePowerShell = "powershell";

    private static readonly string[] DataFlags = ["-d", "--data", "--data-raw", "--data-binary", "--data-urlencode"];

    /// <summary>
    /// 解析请求文本；无法识别时返回 <c>null</c>（调用方据此提示"没解析出请求"）。
    /// </summary>
    public static HttpRequestSpec? Parse(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return null;
        if (IsUrl(trimmed)) return new HttpRequestSpec(trimmed, "GET", new Dictionary<string, string>(), null, SourceUrl);

        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("invoke-webrequest", StringComparison.Ordinal)
            || lower.Contains("invoke-restmethod", StringComparison.Ordinal)
            || lower.Contains("$session", StringComparison.Ordinal))
        {
            return ParsePowerShell(trimmed);
        }

        if (lower.Contains("curl", StringComparison.Ordinal)) return ParseCurl(trimmed);
        return ParsePowerShell(trimmed) ?? ParseCurl(trimmed);
    }

    /// <summary>从请求里取 Cookie（用于判断是否携带登录态）。</summary>
    public static string CookieOf(HttpRequestSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Headers.TryGetValue("cookie", out var cookie) ? cookie : string.Empty;
    }

    /// <summary>续行符（bash <c>\</c>、PowerShell 反引号、cmd <c>^</c>）统一成空格。</summary>
    private static string Normalize(string text) => text
        .Replace("\\\r\n", " ", StringComparison.Ordinal)
        .Replace("\\\n", " ", StringComparison.Ordinal)
        .Replace("`\r\n", " ", StringComparison.Ordinal)
        .Replace("`\n", " ", StringComparison.Ordinal)
        .Replace("^\r\n", " ", StringComparison.Ordinal)
        .Replace("^\n", " ", StringComparison.Ordinal)
        .Trim();

    /// <summary>按 shell 规则切分 token（尊重单/双引号与反斜杠转义）。</summary>
    public static IReadOnlyList<string> Tokenize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        for (var i = 0; i < input.Length; i += 1)
        {
            var ch = input[i];
            if (quote is not null)
            {
                // 双引号内的反斜杠是转义：吃掉反斜杠，原样留下下一个字符（单引号内不转义）
                if (ch == '\\' && quote == '"')
                {
                    if (i + 1 < input.Length) current.Append(input[i + 1]);
                    i += 1;
                    continue;
                }

                if (ch == quote)
                {
                    quote = null;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static bool IsUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static HttpRequestSpec? ParseCurl(string text)
    {
        var tokens = Tokenize(Normalize(text));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? url = null;
        var method = "GET";
        string? body = null;

        for (var i = 0; i < tokens.Count; i += 1)
        {
            var token = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;

            if (token is "-H" or "--header")
            {
                if (next is not null)
                {
                    var index = next.IndexOf(':', StringComparison.Ordinal);
                    if (index > 0) headers[next[..index].Trim().ToLowerInvariant()] = next[(index + 1)..].Trim();
                    i += 1;
                }

                continue;
            }

            if (token is "-X" or "--request")
            {
                if (next is not null)
                {
                    method = next.ToUpperInvariant();
                    i += 1;
                }

                continue;
            }

            if (DataFlags.Contains(token, StringComparer.Ordinal))
            {
                if (next is not null)
                {
                    // 多个 -d 用 & 连接（与 curl 对 urlencoded 表单的处理一致）
                    body = body is null ? next : $"{body}&{next}";
                    if (method == "GET") method = "POST";
                    i += 1;
                }

                continue;
            }

            if (token is "-b" or "--cookie")
            {
                if (next is not null)
                {
                    headers["cookie"] = next;
                    i += 1;
                }

                continue;
            }

            // 只认"以 http 开头"的裸 token：header 里的 `referer: https://...` 整段不以 http 开头，
            // 因此不会把 referer 当成请求 URL（TS 侧同一处判断）。
            if (url is null && IsUrl(token)) url = token;
        }

        return url is null ? null : new HttpRequestSpec(url, method, headers, body, SourceCurl);
    }

    private static HttpRequestSpec? ParsePowerShell(string text)
    {
        var flat = Normalize(text);
        var uriMatch = UriRegex().Match(flat);
        var bareUrl = BareUrlRegex().Match(flat);
        var url = uriMatch.Success ? uriMatch.Groups[1].Value : bareUrl.Success ? bareUrl.Value : null;
        if (string.IsNullOrEmpty(url)) return null;

        var methodMatch = MethodRegex().Match(flat);
        var method = methodMatch.Success ? methodMatch.Groups[1].Value.ToUpperInvariant() : "GET";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerBlock = HeaderBlockRegex().Match(flat);
        if (headerBlock.Success)
        {
            foreach (Match match in HeaderPairRegex().Matches(headerBlock.Groups[1].Value))
            {
                headers[match.Groups[1].Value.ToLowerInvariant()] = match.Groups[2].Value;
            }
        }

        // $session.Cookies.Add((New-Object System.Net.Cookie("name", "value", "/", "domain")))
        var cookies = new List<string>();
        foreach (Match match in CookieRegex().Matches(flat))
        {
            cookies.Add($"{match.Groups[1].Value}={match.Groups[2].Value}");
        }

        if (cookies.Count > 0) headers["cookie"] = string.Join("; ", cookies);

        var bodyMatch = BodyRegex().Match(flat);
        var body = bodyMatch.Success && bodyMatch.Groups[1].Value.Length > 0 ? bodyMatch.Groups[1].Value : null;

        return new HttpRequestSpec(url, method, headers, body, SourcePowerShell);
    }

    [GeneratedRegex("""-Uri\s+["']([^"']+)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriRegex();

    [GeneratedRegex("""https?://[^\s"')]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex("""-Method\s+["']?([A-Za-z]+)["']?""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MethodRegex();

    [GeneratedRegex("""-Headers\s+@\{([\s\S]*?)\}\s*(?=-|$)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderBlockRegex();

    [GeneratedRegex("""["']([^"']+)["']\s*=\s*["']([^"']*)["']""")]
    private static partial Regex HeaderPairRegex();

    [GeneratedRegex("""New-Object\s+System\.Net\.Cookie\(\s*["']([^"']+)["']\s*,\s*["']([^"']*)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CookieRegex();

    [GeneratedRegex("""-Body\s+["']([\s\S]*?)["']\s*(?=-|$)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyRegex();
}
