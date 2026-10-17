using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.App.Data;

/// <summary>
/// 一次导入 / 抓取的结果。<see cref="Message"/> 是给用户看的一句话；
/// <see cref="Probes"/> 是抓取阶段的探测行，<see cref="Diagnostics"/> 是适配器给的诊断。
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
/// 导入编排：**把"数据从哪来"和"数据怎么用"接起来**。
///
/// <para>它自己不碰窗口：拿到课表后通过构造时传进来的三个回调交给外壳
/// （<c>apply</c> = 落盘 + 重画、<c>reload</c> = 重新按载入顺序读一遍、<c>describe</c> = 当前课表摘要）。
/// 设置窗口只认识这个类，不认识 <c>MainWindow</c>；外壳也不认识"粘贴框长什么样"。</para>
///
/// <para>与 Electron 侧的分工一致：那边是"渲染层算，主进程只管窗口/文件/持久化"，
/// 这边是"导入逻辑在这里，窗口与渲染在 <c>MainWindow</c>/<c>Rendering</c>"。</para>
/// </summary>
internal sealed class ImportService
{
    private readonly Func<Timetable, string, bool> _apply;
    private readonly Func<bool> _reload;
    private readonly Func<string> _describe;

    /// <param name="apply">应用一份课表（落盘 + 重画），返回是否成功。</param>
    /// <param name="reload">按载入顺序重新读一遍课表（清空 / 重新载入用）。</param>
    /// <param name="describe">当前课表摘要（设置页"当前课表"那一行）。</param>
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

    /// <summary>数据目录（设置页里展示，便于用户备份/排查）。</summary>
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
        // 这之后的结论来自粘贴/文件，不再属于"内置登录捕获"：别让重新挂载的页用它覆盖新结果
        LastCapturedOutcome = null;

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
            AppLog.Error($"[import] 导入异常：{ex.GetType().Name}");
            return ImportOutcome.Failure($"导入失败（{ex.GetType().Name}），详见日志。");
        }
    }

    /// <summary>
    /// 从 1 系统抓取：把用户粘贴的浏览器请求原样发一次，成功则直接导入并应用。
    /// </summary>
    public async Task<ImportOutcome> FetchAsync(string requestText, CancellationToken cancellationToken = default)
    {
        LastCapturedOutcome = null; // 同上：这次抓取的结论自己会铺进本页，捕获缓存作废

        var effective = (requestText ?? string.Empty).Trim();
        if (effective.Length == 0) effective = CredentialsStore.LoadRequest();
        if (effective.Length == 0) return ImportOutcome.Failure("先粘贴一条浏览器请求（F12 → Copy as cURL），再点「重新获取课表」。");

        // 先存下来：下次打开设置窗口就能回显（内容含 Cookie，绝不进日志）
        CredentialsStore.SaveRequest(effective);

        // 这里刻意 **不** 用 ConfigureAwait(false)：await 之后要经由 _apply 回到挂件窗口重画，
        // 而 XAML 只能在 UI 线程上碰。网络层（SchoolFetcher / 各校 Fetcher）内部用 ConfigureAwait(false)
        // 没关系 —— 那是"不等回来就不碰 UI"，回到这里仍然会被调度回 UI 上下文。
        var fetched = await SchoolFetcher.FetchAsync(effective, cancellationToken);
        if (!fetched.Ok || fetched.TimetableText is null)
        {
            return ImportOutcome.Failure(fetched.Message, fetched.Probes);
        }

        try
        {
            // 抓回来的响应一定来自某所学校，直接用抓取侧点名的适配器
            // （同济不给这个字段 → 退回家门口那个；交大除了课表还带一份教务日历）
            var result = ImportPipeline.ImportTimetable(new ImportInput
            {
                Text = fetched.TimetableText,
                AdapterId = fetched.AdapterId ?? TongjiStudentAdapter.AdapterId,
                // 报表格式的响应里没有学期，只能从请求 URL 带过来（见 TongjiFetcher）
                TermId = fetched.TermId,
                Files = fetched.Files,
            });
            return Apply(result, fetched.Probes);
        }
        catch (ImportException ex)
        {
            AppLog.Line($"[import] 抓取结果解析失败 code={ex.Code}");
            return new ImportOutcome(false, ex.Message, null, null, [], fetched.Probes);
        }
    }

    /// <summary>
    /// 最近一次内置登录捕获的结果（成功、失败都算）。没有捕获过、或之后又做了粘贴/本地动作时为 <c>null</c>。
    ///
    /// <para>为什么留一份：设置页是"先构造页面（取一次摘要）→ 挂进可视树（Loaded）才订阅"，
    /// 内置窗口的结果完全可能落在这两步之间 —— 光靠事件会把那一次丢掉，页面上就一直是旧摘要。
    /// 挂载时先订阅、再读一次这里补上。</para>
    /// </summary>
    public ImportOutcome? LastCapturedOutcome { get; private set; }

    /// <summary>
    /// 内置登录窗口交回一条捕获结果时发一次（<see cref="ApplyCapturedResponse"/> 的**每个**返回结果都发，
    /// 成功、解析失败、空响应都算）。
    ///
    /// <para>为什么要有它：登录窗口自己的完成回调只写日志、不碰设置页，所以用户回到设置页时看到的是
    /// 「什么都没发生」——包括成功之后那一页还显示旧表摘要。设置页挂着这个事件，收到结果就刷新
    /// 「上次抓取结果」与「当前课表」两处。</para>
    ///
    /// <para><b>观察者抛异常不改变返回值</b>：这条结果可能已经把课表保存到本机了，
    /// 不能因为某个界面订阅者出错就对用户说失败（谁出错谁自己进日志）。</para>
    /// </summary>
    public event Action<ImportOutcome>? CapturedResponseCompleted;

    /// <summary>逐个通知订阅者，任何一个抛异常都只记类型名，不影响其它订阅者与返回值。</summary>
    private void RaiseCapturedResponseCompleted(ImportOutcome outcome)
    {
        var handlers = CapturedResponseCompleted;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ImportOutcome>)handler)(outcome);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[import] 捕获响应完成回调异常：{ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// 把**内置登录窗口捕获到的**课表响应直接导入（不经过粘贴框，也不写凭证文件）。
    ///
    /// <para>与 <see cref="FetchAsync"/> 的差别只在"响应从哪来"：那边是用户粘贴的请求、由我们发出去；
    /// 这边是登录窗口里页面自己发的那条请求被我们接住。**解析、诊断、落盘完全同一条路**
    /// （都落到 <see cref="Apply"/>），所以两种入口的行为不会分叉。</para>
    /// </summary>
    /// <param name="responseText">捕获到的响应体（课表接口的 JSON）。</param>
    /// <param name="termId">学期 id（同济报表接口只在 URL 上带 <c>calendarId</c>，见 <c>TongjiWebCapture</c>）。</param>
    /// <param name="probes">给用户看的探测行（从哪条接口、多大、哪个学期）。</param>
    /// <param name="adapterId">用哪个适配器解析；默认同济（旧调用点行为不变），交大传 <c>sjtu-student</c>。</param>
    /// <param name="files">随课表一起交进来的附加数据（交大的教务日历走这里）。</param>
    public ImportOutcome ApplyCapturedResponse(
        string responseText,
        string? termId,
        IReadOnlyList<FetchProbe>? probes = null,
        string adapterId = TongjiStudentAdapter.AdapterId,
        IReadOnlyList<ImportFile>? files = null)
    {
        var outcome = ApplyCapturedResponseCore(responseText, termId, probes, adapterId, files);
        LastCapturedOutcome = outcome; // 先落缓存再发事件：挂载中的页靠事件，正在挂载的页靠缓存
        RaiseCapturedResponseCompleted(outcome);
        return outcome;
    }

    private ImportOutcome ApplyCapturedResponseCore(
        string responseText,
        string? termId,
        IReadOnlyList<FetchProbe>? probes,
        string adapterId,
        IReadOnlyList<ImportFile>? files)
    {
        var rows = probes ?? [];
        if (string.IsNullOrWhiteSpace(responseText)) return ImportOutcome.Failure("捕获到的响应是空的。", rows);

        try
        {
            // 登录窗口里只有目标学校的页面，捕获到的响应一定来自它点名的适配器
            var result = ImportPipeline.ImportTimetable(new ImportInput
            {
                Text = responseText,
                AdapterId = adapterId,
                TermId = termId,
                Files = files,
            });
            return Apply(result, rows);
        }
        catch (ImportException ex)
        {
            AppLog.Line($"[login] 捕获的响应解析失败 code={ex.Code}");
            return new ImportOutcome(false, ex.Message, null, null, [], rows);
        }
    }

    /// <summary>清空已导入的课表：删文件 → 重新载入（回退到内置示例课表）。</summary>
    public ImportOutcome ClearTimetable()
    {
        LastCapturedOutcome = null;
        TimetableStore.Clear();
        var ok = _reload();
        return new ImportOutcome(
            ok,
            ok ? "已清空导入的课表；挂件回退到内置示例课表。" : "已删除课表文件，但重新载入本机课表失败（详见日志）",
            null,
            null,
            [],
            []);
    }

    /// <summary>
    /// 只读本机那份课表、重画一遍（「重新载入课表」按钮）。<b>不联网</b>：要向学校取新数据得走
    /// <see cref="FetchAsync"/> 或内置登录窗口 —— 两个按钮说的话必须是两件事。
    /// </summary>
    public ImportOutcome ReloadTimetable()
    {
        LastCapturedOutcome = null;

        var ok = _reload();
        return new ImportOutcome(
            ok,
            ok ? "已按本机保存的课表重新载入（没有向学校取新数据）" : "重新载入本机课表失败（详见日志）",
            null,
            null,
            [],
            []);
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
            return new ImportOutcome(
                false,
                "没有解析出任何课程：这次没有应用，当前课表保持不变（检查一下粘的是不是课表数据）。",
                result,
                null,
                result.Diagnostics,
                probes);
        }

        // 「应用」= 整份替换 + 落盘成功。落盘失败时旧课表还在（_apply 不会先清空），所以文案必须
        // 说"仍是旧表"，不能只说"解析成功"。
        var applied = _apply(timetable, result.AdapterName);
        AppLog.Line($"[import] adapter={result.AdapterId} {summary} applied={applied}");
        return new ImportOutcome(
            applied,
            applied ? $"已整份替换为 {summary}，并保存到本机（下次启动仍用它）" : "新表没有保存成功：当前课表仍是旧表（详见日志）",
            result,
            timetable,
            result.Diagnostics,
            probes);
    }
}
