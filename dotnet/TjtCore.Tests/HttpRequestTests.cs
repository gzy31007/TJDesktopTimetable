using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 粘贴请求解析（<see cref="HttpRequestParser"/>）的验收。
///
/// 前 5 个用例**逐条对齐** TS 侧 <c>packages/core/test/http-request.spec.ts</c>（同一份输入、
/// 同一份期望），后面几条补上 TS 没覆盖但抓取流程会遇到的边界（<c>-b</c> 形式的 Cookie、
/// 多个 <c>-d</c> 的合并、单引号内不转义、纯空白）。
/// </summary>
public class HttpRequestTests
{
    /// <summary>用户实际提供的 PowerShell 片段（Cookie 已替换为占位值）。</summary>
    private const string PowerShellSample = """
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$session.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/152.0.0.0 Safari/537.36 Edg/152.0.0.0"
$session.Cookies.Add((New-Object System.Net.Cookie("language", "cn", "/", "1.tongji.edu.cn")))
$session.Cookies.Add((New-Object System.Net.Cookie("JSESSIONID", "AAA111", "/", "1.tongji.edu.cn")))
$session.Cookies.Add((New-Object System.Net.Cookie("sessionid", "BBB222", "/", "1.tongji.edu.cn")))
Invoke-WebRequest -UseBasicParsing -Uri "https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk" `
-Method "POST" `
-WebSession $session `
-Headers @{
"authority"="1.tongji.edu.cn"
  "method"="POST"
  "accept"="application/json, text/plain, */*"
  "origin"="https://1.tongji.edu.cn"
  "referer"="https://1.tongji.edu.cn/studentElect?t=1789359218896"
  "x-token"="BBB222"
}
""";

    /// <summary>F12 → Copy as cURL (bash) 的典型形态。</summary>
    private const string CurlSample = """
curl 'https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk' \
  -X POST \
  -H 'accept: application/json, text/plain, */*' \
  -H 'cookie: language=cn; JSESSIONID=AAA111; sessionid=BBB222' \
  -H 'referer: https://1.tongji.edu.cn/studentElect' \
  -H 'x-token: BBB222' \
  --data-raw '{"a":1}' \
  --compressed
""";

    [Fact]
    public void Tokenize尊重引号与转义()
    {
        Assert.Equal(["a", "b c", "d e"], HttpRequestParser.Tokenize("a 'b c' \"d e\""));
        Assert.Equal(["-H", "k: v", "-H", "x: y"], HttpRequestParser.Tokenize("-H 'k: v' -H \"x: y\""));
        // 双引号内的 \" 是转义（留下裸引号）；单引号内的反斜杠原样保留
        Assert.Equal(["a\"b"], HttpRequestParser.Tokenize("\"a\\\"b\""));
        Assert.Equal(["a\\b"], HttpRequestParser.Tokenize("'a\\b'"));
    }

    [Fact]
    public void 解析PowerShell的InvokeWebRequest与Cookie容器()
    {
        var spec = HttpRequestParser.Parse(PowerShellSample);
        Assert.NotNull(spec);
        Assert.Equal("powershell", spec!.Source);
        Assert.Equal("https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk", spec.Url);
        Assert.Equal("POST", spec.Method);
        Assert.Equal("BBB222", spec.Headers["x-token"]);
        // 小写键写入、大小写都能取（外壳要用它填 HttpClient 的请求头）
        Assert.Contains("studentElect", spec.Headers["referer"], StringComparison.Ordinal);
        Assert.Contains("studentElect", spec.Headers["Referer"], StringComparison.Ordinal);
        Assert.Equal("language=cn; JSESSIONID=AAA111; sessionid=BBB222", HttpRequestParser.CookieOf(spec));
        Assert.Null(spec.Body);
    }

    [Fact]
    public void 解析curl且URL不误取header里的referer()
    {
        var spec = HttpRequestParser.Parse(CurlSample);
        Assert.NotNull(spec);
        Assert.Equal("curl", spec!.Source);
        Assert.Equal("https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk", spec.Url);
        Assert.Equal("POST", spec.Method);
        Assert.Equal("""{"a":1}""", spec.Body);
        Assert.Contains("sessionid=BBB222", spec.Headers["cookie"], StringComparison.Ordinal);
        Assert.Equal("BBB222", spec.Headers["x-token"]);
    }

    [Fact]
    public void 只给URL时退化为GET()
    {
        var spec = HttpRequestParser.Parse("https://1.tongji.edu.cn/api/x");
        Assert.NotNull(spec);
        Assert.Equal("https://1.tongji.edu.cn/api/x", spec!.Url);
        Assert.Equal("GET", spec.Method);
        Assert.Empty(spec.Headers);
        Assert.Equal("url", spec.Source);
        Assert.Null(spec.Body);
    }

    [Fact]
    public void 无法识别的内容返回null()
    {
        Assert.Null(HttpRequestParser.Parse("   "));
        Assert.Null(HttpRequestParser.Parse(null));
    }

    [Fact]
    public void 支持_b形式的Cookie与多个_data合并()
    {
        var spec = HttpRequestParser.Parse("""curl https://x/y -b 'a=1; b=2' -d 'p=1' -d 'q=2'""");
        Assert.NotNull(spec);
        Assert.Equal("a=1; b=2", HttpRequestParser.CookieOf(spec!));
        Assert.Equal("p=1&q=2", spec.Body);
        Assert.Equal("POST", spec.Method);
    }

    [Fact]
    public void PowerShell的Body会被取到且保留引号内的换行()
    {
        var spec = HttpRequestParser.Parse("""
Invoke-RestMethod -Uri "https://1.tongji.edu.cn/api/y" -Method POST -Body '{"a": 1}'
""");
        Assert.NotNull(spec);
        Assert.Equal("POST", spec!.Method);
        Assert.Equal("""{"a": 1}""", spec.Body);
    }
}
