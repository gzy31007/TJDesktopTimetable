using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 内置登录窗口的纯逻辑（<see cref="TongjiWebCapture"/>）验收：认不认得出课表接口、
/// 会不会把别人的 cookie 发错域、学期 id 捞不捞得到。
///
/// <para>这几条都直接决定"登录窗口能不能抓到课表"，而且都**不碰窗口**，
/// 所以钉在这里（Linux 上也能跑）。</para>
/// </summary>
public class TongjiWebCaptureTests
{
    private const string ReportUrl =
        "https://1.tongji.edu.cn/api/electionservice/reportManagement/findStudentTimetab?calendarId=122&studentCode=abc";

    [Theory]
    [InlineData(ReportUrl)]
    [InlineData("https://1.tongji.edu.cn/api/electionservice/reportManagement/findSchoolTimetab2?id=1")]
    [InlineData("https://1.tongji.edu.cn/api/electionservice/student/123/getDataBk")]
    [InlineData("https://1.tongji.edu.cn/api/x/FINDSTUDENTTIMETAB?calendarId=1")]
    public void 认得出三条课表接口(string url) => Assert.True(TongjiWebCapture.IsEndpoint(url));

    [Theory]
    [InlineData("https://1.tongji.edu.cn/api/baseresservice/schoolCalendar/detail?calendarId=122")]
    [InlineData("https://1.tongji.edu.cn/")]
    [InlineData("https://1.tongji.edu.cn/static/js/app.1a2b3c.js")]
    [InlineData("")]
    [InlineData(null)]
    public void 别的请求一律不算课表接口(string? url) => Assert.False(TongjiWebCapture.IsEndpoint(url));

    [Fact]
    public void 接口名能说出来是哪一条()
    {
        Assert.Equal("报表接口 findStudentTimetab", TongjiWebCapture.EndpointLabel(ReportUrl));
        Assert.Equal("研究生报表接口 findSchoolTimetab2", TongjiWebCapture.EndpointLabel("https://x/findSchoolTimetab2"));
        Assert.Equal("选课服务 getDataBk", TongjiWebCapture.EndpointLabel("https://x/getDataBk"));
        Assert.Equal("未知接口", TongjiWebCapture.EndpointLabel("https://x/whatever"));
        Assert.Equal("未知接口", TongjiWebCapture.EndpointLabel(null));
    }

    [Theory]
    [InlineData("1.tongji.edu.cn", true)]
    [InlineData("tongji.edu.cn", true)]
    [InlineData("MyPortal.Tongji.Edu.Cn", true)]
    [InlineData("eviltongji.edu.cn", false)]
    [InlineData("tongji.edu.cn.evil.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 认得出同济域名(string? host, bool expected) =>
        Assert.Equal(expected, TongjiWebCapture.IsTongjiHost(host));

    [Theory]
    [InlineData("https://1.tongji.edu.cn/api/x", true)]
    [InlineData("http://ids.tongji.edu.cn:8443/nidp/idff/sso", true)]
    [InlineData("https://example.com/1.tongji.edu.cn", false)]
    [InlineData("file:///C:/tongji.edu.cn", false)]
    [InlineData("not a url", false)]
    public void 认得出同济站点(string? url, bool expected) =>
        Assert.Equal(expected, TongjiWebCapture.IsTongjiUrl(url));

    [Fact]
    public void 从报表接口的URL上取到学期id()
    {
        Assert.Equal("122", TongjiWebCapture.CalendarIdOf(ReportUrl));
        Assert.Equal("122", TongjiWebCapture.CalendarIdOf("https://x/y?foo=1&CALENDARID=122"));
        Assert.Null(TongjiWebCapture.CalendarIdOf("https://x/y?studentCode=abc"));
        Assert.Null(TongjiWebCapture.CalendarIdOf("https://x/y?calendarId="));
        Assert.Null(TongjiWebCapture.CalendarIdOf("https://x/y"));
        Assert.Null(TongjiWebCapture.CalendarIdOf(null));
    }

    [Fact]
    public void 查询参数会做URL解码()
    {
        Assert.Equal("a b/c", TongjiWebCapture.QueryValue("https://x/y?name=a%20b%2Fc", "name"));
        Assert.Equal("x y", TongjiWebCapture.QueryValue("https://x/y?name=x+y", "name"));
    }

    [Fact]
    public void cookie按域与路径筛出来拼成请求头()
    {
        var cookies = new[]
        {
            new WebCookie("JSESSIONID", "abc", "1.tongji.edu.cn", "/"),
            new WebCookie("tenantCode", "200092", ".tongji.edu.cn", "/"),
            new WebCookie("other", "nope", "example.com", "/"),
        };

        var header = TongjiWebCapture.CookieHeader(ReportUrl, cookies);
        Assert.Contains("JSESSIONID=abc", header, StringComparison.Ordinal);
        Assert.Contains("tenantCode=200092", header, StringComparison.Ordinal);
        Assert.DoesNotContain("other", header, StringComparison.Ordinal);
    }

    [Fact]
    public void 路径不匹配的cookie不发出去()
    {
        var cookies = new[]
        {
            new WebCookie("apiOnly", "1", "1.tongji.edu.cn", "/api"),
            new WebCookie("elsewhere", "2", "1.tongji.edu.cn", "/other"),
        };

        var header = TongjiWebCapture.CookieHeader(ReportUrl, cookies);
        Assert.Contains("apiOnly=1", header, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere", header, StringComparison.Ordinal);

        // /api 不该匹配 /apix（RFC 6265 的边界规则）
        var wrong = TongjiWebCapture.CookieHeader("https://1.tongji.edu.cn/apix/y", cookies);
        Assert.Equal(string.Empty, wrong);
    }

    [Fact]
    public void 同名cookie取路径更长的那个()
    {
        var cookies = new[]
        {
            new WebCookie("token", "generic", "1.tongji.edu.cn", "/"),
            new WebCookie("token", "specific", "1.tongji.edu.cn", "/api/electionservice"),
        };

        var header = TongjiWebCapture.CookieHeader(ReportUrl, cookies);
        Assert.Contains("token=specific", header, StringComparison.Ordinal);
        Assert.DoesNotContain("token=generic", header, StringComparison.Ordinal);
        Assert.Single(header.Split("; "));
    }

    [Fact]
    public void 没有cookie时请求头是空串() =>
        Assert.Equal(string.Empty, TongjiWebCapture.CookieHeader(ReportUrl, []));

    [Fact]
    public void 报表响应认出来并带上学期id()
    {
        var verdict = TongjiWebCapture.Inspect(ReportUrl, """{"code":200,"data":[{"courseName":"高数"}]}""");

        Assert.True(verdict.Ok);
        Assert.Equal("data 数组 1 条", verdict.Note);
        Assert.Equal("122", verdict.TermId);
    }

    [Fact]
    public void 旧选课服务的响应也认()
    {
        var verdict = TongjiWebCapture.Inspect(
            "https://1.tongji.edu.cn/api/electionservice/student/1/getDataBk",
            """{"data":{"calendarId":122,"selectedCourses":[{}]}}""");

        Assert.True(verdict.Ok);
        Assert.Equal("selectedCourses 1 门", verdict.Note);
    }

    [Fact]
    public void 不是课表接口的响应直接忽略()
    {
        var verdict = TongjiWebCapture.Inspect("https://1.tongji.edu.cn/static/js/app.js", "alert(1)");

        Assert.False(verdict.Ok);
        Assert.Equal("不是课表接口（已忽略）", verdict.Note);
        Assert.Null(verdict.TermId);
    }

    [Fact]
    public void 登录页HTML会被如实说成不是课表()
    {
        var verdict = TongjiWebCapture.Inspect(ReportUrl, "<!doctype html><html></html>");

        Assert.False(verdict.Ok);
        Assert.Contains("不是合法 JSON", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 描述里不含cookie也不含响应体()
    {
        var text = TongjiWebCapture.Describe(ReportUrl, 30261);

        Assert.Equal("报表接口 findStudentTimetab，30261 字节，calendarId=122", text);
    }
}
