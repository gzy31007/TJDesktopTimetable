// 仅测试用：把 dotnet/Tjt.App/Data/ImportService.cs **同源链接**进本工程时，补齐它引用的
// 那几处外壳静态类（日志 / 配置目录 / 凭据 / 课表文件 / 网络分派）。
//
// 为什么不新增生产抽象：ImportService 的编排逻辑本来就没有 WinUI 依赖，缺的只是"往哪儿写文件、
// 往哪儿打日志"这些外壳事实。生产代码里为测试开接口会把外壳职责漏进业务层；测试里桩掉这几处
// 则什么都不用改出货代码。桩只在测试工程编译期内存在，App 里那两个真实现仍然是唯一真源。

using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.App
{
    /// <summary>日志：测试里丢弃（真实现写 <c>logs/</c>，测试不该碰）。</summary>
    internal static class AppLog
    {
        public static void Line(string message) => _ = message;

        public static void Error(string message) => _ = message;
    }
}

namespace Tjt.App.Data
{

    /// <summary>配置目录：指向临时目录（ImportService 只把它显示出来，不读写）。</summary>
    internal static class SettingsStore
    {
        public static string Directory => Path.Combine(Path.GetTempPath(), "tjt-tests");
    }

    /// <summary>凭据：测试里永远"没有保存过请求"，也接受写入但丢弃。</summary>
    internal static class CredentialsStore
    {
        public static string LoadRequest() => string.Empty;

        public static void SaveRequest(string requestText) => _ = requestText;
    }

    /// <summary>课表文件：测试不落盘。Count 与真实现同一算法（门数 × 上课安排数），只用于摘要文案。</summary>
    internal static class TimetableStore
    {
        public static void Clear()
        {
        }

        public static int Count(Timetable timetable) => timetable.Courses.Sum(course => course.Sessions.Count);
    }

    /// <summary>探测行（真源：dotnet/Tjt.App/Data/TongjiFetcher.cs）。</summary>
    internal sealed record FetchProbe(string Label, string Value);

    /// <summary>抓取结果（真源：dotnet/Tjt.App/Data/TongjiFetcher.cs）。</summary>
    internal sealed record TongjiFetchOutcome(
        bool Ok,
        string Message,
        string? TimetableText,
        IReadOnlyList<FetchProbe> Probes,
        string? TermId = null,
        string? AdapterId = null,
        IReadOnlyList<ImportFile>? Files = null);

    /// <summary>网络分派：本工程的测试只走"粘贴 JSON"这条路，抓取路径一律视为不可用。</summary>
    internal static class SchoolFetcher
    {
        public static Task<TongjiFetchOutcome> FetchAsync(
            string? requestText,
            CancellationToken cancellationToken = default,
            string? sjtuBaseUrl = null)
        {
            _ = requestText;
            _ = cancellationToken;
            _ = sjtuBaseUrl;
            throw new NotSupportedException("这段测试不联网：只覆盖「粘贴 JSON → 应用」这一条路。");
        }
    }
}
