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

    /// <summary>Windows 安装包的命名后缀（Inno Setup 生成的 setup.exe）。</summary>
    public const string WindowsInstallerSuffix = "-setup.exe";

    /// <summary>
    /// Windows 平台按优先级尝试的资产后缀：**安装包优先于 zip**。
    ///
    /// <para>2026-09-17 起 Release 两个资产都传（zip 给想手动解压的人，setup.exe 给普通用户），
    /// 而"发现新版本"那个按钮应该给普通人最容易用的那个 —— 双击就能装完的安装包。</para>
    /// </summary>
    public static IReadOnlyList<string> WindowsPackageSuffixes { get; } =
        [WindowsInstallerSuffix, WindowsPackageSuffix];

    /// <summary>Linux 资产的命名后缀（本 fork 的 tar.gz；Windows 版不做提示，留着备用）。</summary>
    public const string LinuxPackageSuffix = "-linux-x64.tar.gz";

    /// <summary>
    /// 下载 / API 的代理前缀（<c>https://gh-proxy.org/&lt;原始 GitHub 地址&gt;</c>）。
    ///
    /// <para><b>为什么需要</b>：国内直连 <c>api.github.com</c> 与
    /// <c>github.com/.../releases/download/...</c> 基本不通，而 gh-proxy 类镜像在国内可直连
    /// （2026-09-17 实测：API JSON、Release 资产、Release 页、raw 文件都返回 200）。</para>
    ///
    /// <para><b>用途两处</b>：① 检查更新的 API 直连失败时的兜底（网络侧的
    /// <c>Tjt.App/Data/UpdateChecker.cs</c> 里那一轮重试）；② 「发现新版本」给用户的下载链接
    /// 本身就走镜像（国内点开就能下）。</para>
    /// </summary>
    public const string ProxyPrefix = "https://gh-proxy.org/";

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

    /// <summary>
    /// 挑本平台的包：Windows 上先找安装包（setup.exe）、再退到 zip；其它平台按传入的单一后缀。
    /// 都找不到返回 <c>null</c>，调用方退回 Release 页面。
    /// </summary>
    private static ReleaseAsset? ChoosePlatformPackage(LatestRelease release, string packageSuffix)
    {
        var suffixes = packageSuffix == WindowsPackageSuffix
            ? WindowsPackageSuffixes
            : new[] { packageSuffix };
        foreach (var suffix in suffixes)
        {
            var asset = ChoosePackage(release, suffix);
            if (asset is not null) return asset;
        }

        return null;
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
            ChoosePlatformPackage(release, packageSuffix));
    }

    /// <summary>
    /// 该给用户打开的链接：有本平台的包就直接给包的下载地址，否则退回 Release 页面；
    /// **能走镜像的一律走镜像**（见 <see cref="ProxiedUrl"/>）。
    /// </summary>
    public static string DownloadUrlFor(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ProxiedUrl(result.Package?.DownloadUrl ?? result.Release?.HtmlUrl ?? RepoUrl);
    }

    /* ---------------------------------------------------------- 系统通知（气泡 / Toast） */

    /// <summary>
    /// 通知去重用的版本键（<c>1.4.1</c>）；结论里没有可用版本时为 <c>null</c>。
    /// </summary>
    public static string? NotificationVersion(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Latest is { } latest ? Normalize(latest).ToString() : null;
    }

    /// <summary>
    /// 该不该为这次结论弹**系统通知**：有更新，且不是"同一个版本已经弹过一次"。
    ///
    /// <para>去重按"版本"而不是"这次查询"：手动点「检查新版本」不该再弹一遍同一个版本，
    /// 但用户跳过之后又出了更高的版本，还是要提醒。</para>
    /// </summary>
    /// <param name="result">本次结论。</param>
    /// <param name="lastNotifiedVersion">本进程内最近弹过通知的版本键（见 <see cref="NotificationVersion"/>）。</param>
    public static bool ShouldNotify(UpdateCheckResult result, string? lastNotifiedVersion)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasUpdate) return false;
        var version = NotificationVersion(result);
        return version is not null
            && !string.Equals(version, lastNotifiedVersion?.Trim(), StringComparison.Ordinal);
    }

    /// <summary>通知标题（尽量短，Windows 会截断过长标题）。</summary>
    public static string NotificationTitle(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var version = NotificationVersion(result);
        return version is null ? "发现新版本" : $"发现新版本 v{version}";
    }

    /// <summary>通知正文：说清当前版本与"点它做什么"。</summary>
    public static string NotificationBody(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"当前 v{result.Current}，点这条通知打开「关于」页下载。";
    }

    /// <summary>
    /// 该地址能不能交给 gh-proxy 类镜像去取。
    ///
    /// <para>实测（2026-09-17）镜像只认这些形状：<c>api.github.com/**</c>、
    /// <c>raw.githubusercontent.com/**</c>、<c>codeload.github.com/**</c>、
    /// 以及 <c>github.com</c> 下带 <c>/releases/</c> 的地址（tag 页与资产都行）。
    /// **仓库根 <c>https://github.com/owner/repo</c> 会被镜像回 404**，所以不能一律加前缀
    /// （没匹配到资产时 <see cref="DownloadUrlFor"/> 会退回 <see cref="RepoUrl"/>，那条得留原样）。</para>
    /// </summary>
    public static bool CanProxy(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        return uri.Host.ToLowerInvariant() switch
        {
            "api.github.com" or "raw.githubusercontent.com" or "codeload.github.com" => true,
            "github.com" => uri.AbsolutePath.Contains("/releases/", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// 能走镜像的地址加上默认前缀（<see cref="ProxyPrefix"/>）；其余原样返回。
    /// 给用户打开的下载链接与验收脚本之外的调用方用这个。
    /// </summary>
    public static string ProxiedUrl(string? url) =>
        CanProxy(url) ? WithProxyPrefix(url!, ProxyPrefix) : url ?? string.Empty;

    /// <summary>
    /// 无条件加前缀（幂等：已经带前缀的地址原样返回）。给 <c>--update-proxy</c> 覆盖前缀
    /// （验收脚本指向本地合成服务）与单测用 —— 它**不**做 <see cref="CanProxy"/> 判定。
    /// </summary>
    public static string WithProxyPrefix(string? url, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(prefix)) return url ?? string.Empty;
        var head = prefix.EndsWith('/') ? prefix : prefix + "/";
        return url.StartsWith(head, StringComparison.OrdinalIgnoreCase) ? url : head + url;
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
