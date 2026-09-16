using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 新版本检查的纯逻辑（`UpdateCheck`）。
///
/// <para>这里钉的都是"错一个字符就静默失效"的点：tag 带 <c>v</c> 前缀、本机版本是四段、
/// 跳过版本只压住它自己、预发布不提示、没有本平台资产时退回 Release 页。</para>
/// </summary>
public class UpdateCheckTests
{
    /// <summary>真实 <c>/releases/latest</c> 响应的裁剪版（只留我们读的字段）。</summary>
    private const string SampleJson = """
    {
      "tag_name": "v1.3.0",
      "name": "TJDesktopTimetable v1.3.0",
      "html_url": "https://github.com/gzy31007/TJDesktopTimetable/releases/tag/v1.3.0",
      "published_at": "2026-10-01T08:00:00Z",
      "prerelease": false,
      "draft": false,
      "assets": [
        { "name": "TJDesktopTimetable-v1.3.0-linux-x64.tar.gz", "browser_download_url": "https://example.invalid/linux.tar.gz", "size": 41090705 },
        { "name": "TJDesktopTimetable-v1.3.0-win-x64.zip", "browser_download_url": "https://example.invalid/win.zip", "size": 90904721 }
      ]
    }
    """;

    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("V1.2.0", 1, 2, 0)]
    [InlineData("v1.2.0-rc1", 1, 2, 0)]
    [InlineData("1.2.0+build7", 1, 2, 0)]
    [InlineData("  v2.0.0  ", 2, 0, 0)]
    public void tag解析允许v前缀与预发布后缀(string tag, int major, int minor, int build)
    {
        var version = UpdateCheck.ParseVersion(tag);
        Assert.NotNull(version);
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData(null)]
    public void 解析不出的tag返回空(string? tag) => Assert.Null(UpdateCheck.ParseVersion(tag));

    [Fact]
    public void 版本比较只看前三段()
    {
        Assert.Equal(new Version(1, 2, 0), UpdateCheck.Normalize(new Version(1, 2, 0, 0)));
        Assert.False(UpdateCheck.IsNewer(new Version(1, 2, 0), new Version(1, 2, 0, 0)));
        Assert.True(UpdateCheck.IsNewer(new Version(1, 2, 1), new Version(1, 2, 0)));
        Assert.True(UpdateCheck.IsNewer(new Version(1, 3, 0), new Version(1, 2, 9)));
        Assert.True(UpdateCheck.IsNewer(new Version(2, 0, 0), new Version(1, 99, 99)));
        Assert.False(UpdateCheck.IsNewer(new Version(0, 9, 0), new Version(1, 0, 0)));
    }

    [Fact]
    public void 解析真实形状的release响应()
    {
        var release = UpdateCheck.ParseRelease(SampleJson);
        Assert.NotNull(release);
        Assert.Equal("v1.3.0", release!.TagName);
        Assert.Equal("TJDesktopTimetable v1.3.0", release.Title);
        Assert.Equal("https://github.com/gzy31007/TJDesktopTimetable/releases/tag/v1.3.0", release.HtmlUrl);
        Assert.False(release.Prerelease);
        Assert.Equal(new Version(1, 3, 0), release.Version);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(90904721, release.Assets[1].Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"no tag\"}")]
    [InlineData("{\"tag_name\":\"v1.3.0\"}")]
    public void 响应不可用时解析为空(string json) => Assert.Null(UpdateCheck.ParseRelease(json));

    [Fact]
    public void 有新版时给出本平台的包()
    {
        var result = UpdateCheck.Evaluate("1.2.0.0", SampleJson);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.True(result.HasUpdate);
        Assert.Equal(new Version(1, 3, 0), result.Latest);
        Assert.Equal("https://example.invalid/win.zip", result.Package?.DownloadUrl);
        Assert.Equal("https://example.invalid/win.zip", UpdateCheck.DownloadUrlFor(result));
    }

    [Fact]
    public void 本机就是最新时不提示()
    {
        var result = UpdateCheck.Evaluate("1.3.0.0", SampleJson);
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public void 预发布与草稿都不提示()
    {
        var prerelease = SampleJson.Replace("\"prerelease\": false", "\"prerelease\": true");
        var draft = SampleJson.Replace("\"draft\": false", "\"draft\": true");
        Assert.Equal(UpdateStatus.UpToDate, UpdateCheck.Evaluate("1.2.0.0", prerelease).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateCheck.Evaluate("1.2.0.0", draft).Status);
    }

    [Fact]
    public void 跳过只压住被跳过的那个版本()
    {
        // 跳过的正是线上这个版本 → 不再提示
        Assert.Equal(UpdateStatus.Skipped, UpdateCheck.Evaluate("1.2.0.0", SampleJson, "1.3.0").Status);
        Assert.Equal(UpdateStatus.Skipped, UpdateCheck.Evaluate("1.2.0.0", SampleJson, "v1.3.0.0").Status);
        // 跳过一个更老的版本 → 照样提示
        Assert.Equal(UpdateStatus.UpdateAvailable, UpdateCheck.Evaluate("1.2.0.0", SampleJson, "1.2.0").Status);
        // 跳过值不可解析 → 当作没跳过
        Assert.Equal(UpdateStatus.UpdateAvailable, UpdateCheck.Evaluate("1.2.0.0", SampleJson, "nightly").Status);
    }

    [Fact]
    public void 响应不可用时结论是InvalidResponse()
    {
        var result = UpdateCheck.Evaluate("1.2.0.0", "<html>404</html>");
        Assert.Equal(UpdateStatus.InvalidResponse, result.Status);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public void 没有本平台资产时退回Release页面()
    {
        var onlyLinux = SampleJson.Replace(
            "{ \"name\": \"TJDesktopTimetable-v1.3.0-win-x64.zip\", \"browser_download_url\": \"https://example.invalid/win.zip\", \"size\": 90904721 }",
            "{ \"name\": \"readme.txt\", \"browser_download_url\": \"https://example.invalid/readme.txt\", \"size\": 1 }");
        var result = UpdateCheck.Evaluate("1.2.0.0", onlyLinux);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Null(result.Package);
        Assert.Equal("https://github.com/gzy31007/TJDesktopTimetable/releases/tag/v1.3.0", UpdateCheck.DownloadUrlFor(result));
    }

    [Fact]
    public void 请求约定是稳定的()
    {
        Assert.Equal(
            "https://api.github.com/repos/gzy31007/TJDesktopTimetable/releases/latest",
            UpdateCheck.LatestReleaseApiUrl);
        Assert.Equal("TJDesktopTimetable/1.2.0", UpdateCheck.UserAgent("1.2.0"));
        Assert.Equal("https://github.com/gzy31007/TJDesktopTimetable", UpdateCheck.RepoUrl);
    }

    // ── 安装包优先（2026-09-17 起 Release 同时传 zip 与 setup.exe）──────────────────

    /// <summary>两个资产都在时，「发现新版本」给的下载链接应当是双击就能装的安装包。</summary>
    [Fact]
    public void 有安装包时优先给安装包链接()
    {
        const string json = """
        {
          "tag_name": "v9.9.9",
          "html_url": "https://github.com/gzy31007/TJDesktopTimetable/releases/tag/v9.9.9",
          "prerelease": false,
          "draft": false,
          "assets": [
            { "name": "TJDesktopTimetable-v9.9.9-win-x64.zip", "browser_download_url": "https://example.invalid/win.zip" },
            { "name": "TJDesktopTimetable-v9.9.9-setup.exe", "browser_download_url": "https://example.invalid/setup.exe" }
          ]
        }
        """;

        var result = UpdateCheck.Evaluate("1.3.1", json);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("https://example.invalid/setup.exe", UpdateCheck.DownloadUrlFor(result));
    }

    /// <summary>安装包是后来才有的资产：老 Release 只有 zip，那条路不能断。</summary>
    [Fact]
    public void 只有zip时退回zip链接()
    {
        const string json = """
        {
          "tag_name": "v9.9.9",
          "html_url": "https://github.com/gzy31007/TJDesktopTimetable/releases/tag/v9.9.9",
          "prerelease": false,
          "draft": false,
          "assets": [
            { "name": "TJDesktopTimetable-v9.9.9-win-x64.zip", "browser_download_url": "https://example.invalid/win.zip" }
          ]
        }
        """;

        Assert.Equal("https://example.invalid/win.zip", UpdateCheck.DownloadUrlFor(UpdateCheck.Evaluate("1.3.1", json)));
    }

    /// <summary>Linux 那条路不受影响：即便 Release 里有 setup.exe，也要挑 tar.gz。</summary>
    [Fact]
    public void Linux仍按tar_gz后缀挑包()
    {
        const string json = """
        {
          "tag_name": "v9.9.9",
          "html_url": "https://github.com/gzy31007/TJDesktopTimetable/releases/tag/v9.9.9",
          "prerelease": false,
          "draft": false,
          "assets": [
            { "name": "TJDesktopTimetable-v9.9.9-setup.exe", "browser_download_url": "https://example.invalid/setup.exe" },
            { "name": "TJDesktopTimetable-v9.9.9-linux-x64.tar.gz", "browser_download_url": "https://example.invalid/linux.tar.gz" }
          ]
        }
        """;

        var result = UpdateCheck.Evaluate("1.3.1", json, packageSuffix: UpdateCheck.LinuxPackageSuffix);

        Assert.Equal("https://example.invalid/linux.tar.gz", UpdateCheck.DownloadUrlFor(result));
    }
}
