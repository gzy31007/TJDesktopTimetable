using System.Text.Json;

namespace Tjt.Core;

/// <summary>更新检查的结论。</summary>
public enum UpdateStatus
{
    /// <summary>已经是最新（或线上版本不比本机新）。</summary>
    UpToDate,

    /// <summary>有更新的正式版本。</summary>
    UpdateAvailable,

    /// <summary>有更新，但用户之前明确"跳过这个版本"。</summary>
    Skipped,

    /// <summary>拿到的响应不像 GitHub 的 release（缺字段 / 不是 JSON）。</summary>
    InvalidResponse,
}

/// <summary>Release 里的一个下载资产。</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>GitHub Releases API（<c>/releases/latest</c>）里我们真正会用的那几个字段。</summary>
public sealed record LatestRelease(
    string TagName,
    string Title,
    string HtmlUrl,
    string PublishedAt,
    bool Prerelease,
    bool Draft,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>tag 解析出来的版本号；解析不出来是 <c>null</c>。</summary>
    public Version? Version => UpdateCheck.ParseVersion(TagName);
}

/// <summary>一次检查的完整结论（纯数据，便于日志与托盘 / 设置页共用）。</summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    Version Current,
    Version? Latest,
    LatestRelease? Release,
    ReleaseAsset? Package)
{
    /// <summary>是否该给用户提示。</summary>
    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>
/// 新版本检查的**纯逻辑**（解析 / 比较 / 跳过 / 挑资产 / 拼 URL）—— 网络与 UI 都在 Tjt.App。
///
/// <para>放这里的理由与别处一致：这些规则全是"错一个字符就静默失效"的东西
/// （tag 带 <c>v</c> 前缀、本机版本是四段、跳过版本要能比大小），在 Linux 上单测比在真机上试便宜。</para>
///
/// <para><b>只认正式 Release</b>：接口用 <c>/releases/latest</c>（GitHub 定义上就跳过
/// prerelease / draft）；万一响应里仍然带 <c>prerelease=true</c>，也按"不提示"处理。</para>
/// </summary>
public static class UpdateCheck
{
    /// <summary>仓库所有者（与远端一致）。</summary>
    public const string Owner = "gzy31007";

    /// <summary>仓库名。</summary>
    public const string Repo = "TJDesktopTimetable";

    /// <summary>Windows 资产的命名后缀（发布工作流生成的自包含 zip）。</summary>
    public const string WindowsPackageSuffix = "-win-x64.zip";

    /// <summary>Linux 资产的命名后缀（本 fork 的 tar.gz；Windows 版不做提示，留着备用）。</summary>
    public const string LinuxPackageSuffix = "-linux-x64.tar.gz";

    /// <summary>仓库主页（托盘 / 设置页里"打开项目页"用）。</summary>
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";

    /// <summary>最新正式 Release 的 API 地址。</summary>
    public static string LatestReleaseApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>
    /// GitHub 要求的 <c>User-Agent</c>（不带 UA 会被 403）。带上当前版本也方便对方排查。
    /// </summary>
    public static string UserAgent(string currentVersion) => $"TJDesktopTimetable/{currentVersion}";

    /// <summary>
    /// 把 tag 解析成版本号：允许 <c>v1.2.0</c> / <c>1.2.0</c> / <c>1.2</c>；
    /// 解析不出（<c>unknown</c>、空串、带预发布后缀的自定义 tag）返回 <c>null</c>。
    /// </summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        // 只取前三段：`1.2.0.0`、`1.2.0+meta`、`1.2.0-rc1` 都归一到能比较的形式
        var cut = text.IndexOfAny(['+', '-']);
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// 版本比较只看**前三段**：本机的 <c>AssemblyVersion</c> 是四段（<c>1.2.0.0</c>），
    /// tag 是三段（<c>v1.2.0</c>），第四段是构建号，不该参与"有没有新版本"的判断。
    /// </summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);

    /// <summary>线上版本是否比本机新（按前三段比较）。</summary>
    public static bool IsNewer(Version latest, Version current) => Normalize(latest) > Normalize(current);

    /// <summary>解析 GitHub 的 release JSON；缺关键字段返回 <c>null</c>（调用方当"响应不可用"）。</summary>
    public static LatestRelease? ParseRelease(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = ReadString(root, "tag_name");
            var htmlUrl = ReadString(root, "html_url");
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl)) return null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var name = ReadString(item, "name");
                    var url = ReadString(item, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    assets.Add(new ReleaseAsset(name, url, ReadLong(item, "size")));
                }
            }

            return new LatestRelease(
                tag.Trim(),
                ReadString(root, "name") ?? tag.Trim(),
                htmlUrl.Trim(),
                ReadString(root, "published_at") ?? "",
                ReadBool(root, "prerelease"),
                ReadBool(root, "draft"),
                assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>按资产名后缀挑出本平台的包（找不到返回 <c>null</c>，调用方退回 Release 页面）。</summary>
    public static ReleaseAsset? ChoosePackage(LatestRelease release, string suffix)
    {
        ArgumentNullException.ThrowIfNull(release);
        foreach (var asset in release.Assets)
        {
            if (asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return asset;
        }
        return null;
    }

    /// <summary>
    /// 把"本机版本 + API 响应 + 用户跳过的版本"合成一个结论。
    ///
    /// <para>顺序很重要：响应不可用 → 预发布 → 比大小 → 跳过。跳过放在比大小之后，
    /// 这样"跳过的版本"只压住它自己，更高的版本照样提示。</para>
    /// </summary>
    /// <param name="currentVersion">本机版本（<c>Assembly.GetName().Version</c> 的字符串，四段）。</param>
    /// <param name="json">GitHub Releases API 的 <c>/releases/latest</c> 响应体。</param>
    /// <param name="skippedVersion">用户"跳过此版本"记下的版本（可空 / 可解析失败）。</param>
    /// <param name="packageSuffix">本平台的资产后缀。</param>
    public static UpdateCheckResult Evaluate(
        string currentVersion,
        string? json,
        string? skippedVersion = null,
        string packageSuffix = WindowsPackageSuffix)
    {
        var current = ParseVersion(currentVersion) ?? new Version(0, 0, 0);
        var release = ParseRelease(json);
        if (release is null)
        {
            return new UpdateCheckResult(UpdateStatus.InvalidResponse, Normalize(current), null, null, null);
        }

        var latest = release.Version;
        if (latest is null || release.Prerelease || release.Draft)
        {
            // 解析不出 tag / 预发布 / 草稿：一律当作"不分发新版本"
            return new UpdateCheckResult(UpdateStatus.UpToDate, Normalize(current), latest, release, null);
        }

        var normalizedLatest = Normalize(latest);
        if (!IsNewer(latest, current))
        {
            return new UpdateCheckResult(UpdateStatus.UpToDate, Normalize(current), normalizedLatest, release, null);
        }

        var skipped = ParseVersion(skippedVersion);
        if (skipped is not null && normalizedLatest <= Normalize(skipped))
        {
            return new UpdateCheckResult(UpdateStatus.Skipped, Normalize(current), normalizedLatest, release, null);
        }

        return new UpdateCheckResult(
            UpdateStatus.UpdateAvailable,
            Normalize(current),
            normalizedLatest,
            release,
            ChoosePackage(release, packageSuffix));
    }

    /// <summary>
    /// 该给用户打开的链接：有本平台的包就直接给包的下载地址，否则退回 Release 页面。
    /// </summary>
    public static string DownloadUrlFor(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Package?.DownloadUrl ?? result.Release?.HtmlUrl ?? RepoUrl;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
}
