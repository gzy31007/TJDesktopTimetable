using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.Linux.Data;

/// <summary>
/// 一次导入 / 抓取的结果（Tjt.App/Data/ImportService.cs 的移植）。
/// <see cref="Message"/> 是给用户看的一句话；<see cref="Probes"/> 是抓取阶段的探测行，
/// <see cref="Diagnostics"/> 是适配器给的诊断。
/// </summary>
internal sealed record ImportOutcome(
    bool Ok,
    string Message,
    ImportResult? Result,
    Timetable? Timetable,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<FetchProbe> Probes)
{
    /// <summary>造一个"没成"的结果（没有课表、没有诊断）。</summary>
    public static ImportOutcome Failure(string message, IReadOnlyList<FetchProbe>? probes = null) =>
        new(false, message, null, null, [], probes ?? []);
}

/// <summary>
/// 导入编排（Tjt.App/Data/ImportService.cs 的移植，去掉了内置登录窗口那条入口）：
/// 把"数据从哪来"和"数据怎么用"接起来。
///
/// <para>它自己不碰窗口：拿到课表后通过构造时传进来的三个回调交给外壳
/// （<c>apply</c> = 落盘 + 重画、<c>reload</c> = 重新按载入顺序读一遍、<c>describe</c> = 当前课表摘要）。</para>
/// </summary>
internal sealed class ImportService
{
    private readonly Func<Timetable, string, bool> _apply;
    private readonly Func<bool> _reload;
    private readonly Func<string> _describe;

    /// <param name="apply">应用一份课表（落盘 + 重画），返回是否成功。</param>
    /// <param name="reload">按载入顺序重新读一遍课表（清空 / 重新载入用）。</param>
    /// <param name="describe">当前课表摘要（导入窗口"当前课表"那一行）。</param>
    public ImportService(
        Func<Timetable, string, bool> apply,
        Func<bool> reload,
        Func<string> describe)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _reload = reload ?? throw new ArgumentNullException(nameof(reload));
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
    }

    /// <summary>可选的适配器（"自动探测"之外的下拉项）。</summary>
    public IReadOnlyList<ISchoolAdapter> Adapters => AdapterRegistry.BuiltinAdapters;

    /// <summary>数据目录（导入窗口里展示，便于用户备份/排查）。</summary>
    public static string DataDirectory => SettingsStore.Directory;

    /// <summary>当前课表摘要。</summary>
    public string DescribeCurrent() => _describe();

    /// <summary>上次粘贴的抓取请求（有 Cookie，只回显在输入框里）。</summary>
    public static string LoadSavedRequest() => CredentialsStore.LoadRequest();

    /// <summary>
    /// 导入粘贴的 JSON / 选中的文件（适配器自动探测，或用户指定）。
    /// </summary>
    public ImportOutcome ImportText(string? text, string? adapterId, IReadOnlyList<ImportFile>? files)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        var fileList = files?.Where(file => !string.IsNullOrWhiteSpace(file.Text)).ToList();
        if (!hasText && (fileList is null || fileList.Count == 0))
        {
            return ImportOutcome.Failure("先粘贴 JSON 或选一个文件，再点「导入并应用」。");
        }

        try
        {
            var input = new ImportInput
            {
                Text = hasText ? text : null,
                Files = fileList is { Count: > 0 } ? fileList : null,
                AdapterId = string.IsNullOrWhiteSpace(adapterId) ? null : adapterId,
            };
            AppLog.Line($"[import] 开始导入：text={hasText} files={fileList?.Count ?? 0} adapter={adapterId ?? "auto"}");
            return Apply(ImportPipeline.ImportTimetable(input), []);
        }
        catch (ImportException ex)
        {
            // code 是适配器层的稳定标识（adapter.nomatch / adapter.unknown …），日志里带上便于排查
            AppLog.Line($"[import] 导入失败 code={ex.Code}");
            return ImportOutcome.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[import] 导入异常：{ex}");
            return ImportOutcome.Failure($"导入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从 1 系统抓取：把用户粘贴的浏览器请求原样发一次，成功则直接导入并应用。
    /// Linux 版的主导入路径（没有内置登录窗口）。
    /// </summary>
    public async Task<ImportOutcome> FetchAsync(string requestText, CancellationToken cancellationToken = default)
    {
        var effective = (requestText ?? string.Empty).Trim();
        if (effective.Length == 0) effective = CredentialsStore.LoadRequest();
        if (effective.Length == 0) return ImportOutcome.Failure("先粘贴一条浏览器请求（F12 → Copy as cURL），再点「获取我的课表」。");

        // 先存下来：下次打开导入窗口就能回显（内容含 Cookie，绝不进日志）
        CredentialsStore.SaveRequest(effective);

        // 不用 ConfigureAwait(false)：await 之后要经由 _apply 回到挂件窗口重画（Avalonia UI 线程）。
        var fetched = await TongjiFetcher.FetchAsync(effective, cancellationToken);
        if (!fetched.Ok || fetched.TimetableText is null)
        {
            return ImportOutcome.Failure(fetched.Message, fetched.Probes);
        }

        try
        {
            // 抓回来的响应一定来自同济选课服务，直接指定适配器
            var result = ImportPipeline.ImportTimetable(new ImportInput
            {
                Text = fetched.TimetableText,
                AdapterId = TongjiStudentAdapter.AdapterId,
                // 报表格式的响应里没有学期，只能从请求 URL 带过来（见 TongjiFetcher）
                TermId = fetched.TermId,
            });
            return Apply(result, fetched.Probes);
        }
        catch (ImportException ex)
        {
            AppLog.Line($"[import] 抓取结果解析失败 code={ex.Code}");
            return new ImportOutcome(false, ex.Message, null, null, [], fetched.Probes);
        }
    }

    /// <summary>清空已导入的课表：删文件 → 重新载入（回退到 fixtures / 内置样例）。</summary>
    public ImportOutcome ClearTimetable()
    {
        TimetableStore.Clear();
        var ok = _reload();
        return new ImportOutcome(
            ok,
            ok ? "已清空导入的课表；挂件回退到内置样例（再导入一次即替换）" : "已删除课表文件，但重新载入失败（详见日志）",
            null,
            null,
            [],
            []);
    }

    /// <summary>按载入顺序重新读一遍课表并重画（"重新载入"按钮）。</summary>
    public ImportOutcome ReloadTimetable()
    {
        var ok = _reload();
        return new ImportOutcome(ok, ok ? "已重新载入课表" : "重新载入失败（详见日志）", null, null, [], []);
    }

    /// <summary>落成课表 → 交给外壳应用；顺带把诊断与探测行一起返回给 UI。</summary>
    private ImportOutcome Apply(ImportResult result, IReadOnlyList<FetchProbe> probes)
    {
        var timetable = ImportPipeline.MaterializeTimetable(result);
        var sessions = TimetableStore.Count(timetable);
        var summary = $"{timetable.Courses.Count} 门课程 / {sessions} 条上课安排（{result.AdapterName}）";

        if (timetable.Courses.Count == 0)
        {
            AppLog.Line($"[import] 解析出 0 门课程 adapter={result.AdapterId}");
            return new ImportOutcome(false, "没有解析出任何课程，请检查数据是否完整。", result, null, result.Diagnostics, probes);
        }

        var applied = _apply(timetable, result.AdapterName);
        AppLog.Line($"[import] adapter={result.AdapterId} {summary} applied={applied}");
        return new ImportOutcome(
            applied,
            applied ? $"已应用 {summary} 到桌面挂件" : "解析成功，但写入课表文件失败（详见日志）",
            result,
            timetable,
            result.Diagnostics,
            probes);
    }
}
