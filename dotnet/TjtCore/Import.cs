using Tjt.Core.Adapters;

namespace Tjt.Core;

/// <summary>导入管线错误（TS 侧 <c>ImportError</c> 的移植），<see cref="Code"/> 供 UI 分支用。</summary>
public sealed class ImportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 导入管线：把任意来源的数据交给适配器，产出统一模型（TS 侧 <c>import.ts</c> 的移植）。
/// </summary>
public static class ImportPipeline
{
    /// <summary>
    /// 默认适配器注册表。
    ///
    /// 用**可注入的工厂**而不是直接引用具体适配器，是为了让"导入编排"与"适配器实现"解耦：
    /// 注册表由 <c>Adapters/Registry.cs</c> 在静态构造里装配（顺序：同济 &gt; 预览 HTML &gt; 通用 JSON）。
    /// </summary>
    public static Func<IAdapterRegistry>? DefaultRegistryFactory { get; set; }

    public static IAdapterRegistry ResolveRegistry(IAdapterRegistry? registry = null) =>
        registry
        ?? DefaultRegistryFactory?.Invoke()
        ?? throw new ImportException("adapter.noregistry", "尚未装配适配器注册表（Adapters/Registry.cs 未初始化）。");

    /// <summary>
    /// 导入课表。
    ///
    /// - 传 <c>AdapterId</c> 时使用指定适配器；
    /// - 否则自动探测（<c>Detect()</c> 打分最高者），全部不匹配时抛
    ///   <see cref="ImportException"/>（<c>adapter.nomatch</c>）。
    /// </summary>
    public static ImportResult ImportTimetable(ImportInput? input = null, IAdapterRegistry? registry = null)
    {
        input ??= new ImportInput();
        var context = new AdapterContext(
            input.Now ?? DateTimeOffset.Now,
            input.ImportedAt ?? DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));

        if (!string.IsNullOrEmpty(input.AdapterId))
        {
            var adapter = ResolveRegistry(registry).Get(input.AdapterId!)
                ?? throw new ImportException("adapter.unknown", $"未知适配器：{input.AdapterId}");
            return adapter.Parse(input, context);
        }

        var best = ResolveRegistry(registry).Best(input)
            ?? throw new ImportException(
                "adapter.nomatch",
                "无法识别导入的数据格式：请在导入面板手动选择适配器，或确认 JSON 完整（同济课表需要 weekState/dayOfWeek 字段）。");
        return best.Adapter.Parse(input, context);
    }

    /// <summary>
    /// 把导入结果 + 用户勾选的教学班落成最终课表（窗口层与存储层只消费这个结果）。
    ///
    /// - 有 <c>Candidates</c>（平行班池）时只保留被勾选的；
    /// - 没有 <c>Candidates</c>（个人课表型适配器）时，未指定勾选就原样全用。
    /// </summary>
    public static Timetable MaterializeTimetable(ImportResult result, IEnumerable<string>? selectedIds = null)
    {
        var selected = new List<string>();
        foreach (var id in selectedIds ?? [])
        {
            if (!selected.Contains(id)) selected.Add(id);
        }

        IReadOnlyList<Course> courses;
        if (result.Candidates is not null)
        {
            courses = [.. result.Candidates.Where(c => selected.Contains(c.Id))];
        }
        else if (selected.Count > 0)
        {
            courses = [.. result.Courses.Where(c => selected.Contains(c.Id))];
        }
        else
        {
            courses = [.. result.Courses];
        }

        return new Timetable(
            result.Term,
            courses,
            new TimetableSource(
                result.AdapterId,
                result.AdapterVersion,
                DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")));
    }

    /// <summary>勾选结果为空时的提示（UI 用）。</summary>
    public static string DescribeSelection(ImportResult result, IEnumerable<string> selectedIds)
    {
        var count = selectedIds.Count();
        var pool = result.Candidates?.Count ?? result.Courses.Count;
        return $"已选 {count} / {pool} 个教学班";
    }
}
