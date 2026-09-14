using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.App.Data;

/// <summary>课表数据的来源说明（日志与 smoke 输出要用）。</summary>
/// <param name="Source">人类可读的来源描述（fixture 路径或"内置样例"）。</param>
/// <param name="Timetable">落成的课表。</param>
internal sealed record LoadedTimetable(string Source, Timetable Timetable);

/// <summary>
/// 取"当前课表"：优先读脱敏黄金数据（<c>fixtures/tongji-2026-1-personal.json</c>），
/// 读不到就退回 <see cref="DemoData"/> 的内置样例。
///
/// 这一步刻意只在**壳**里做，不放进 <c>Tjt.Widget</c>：读文件不是纯计算，
/// 而后续的持久化（用户导入 / 设置）会替换掉这里的实现。
/// </summary>
internal static class AppHost
{
    /// <summary>约定位置：输出目录下的 <c>fixtures/</c>（csproj 用 Content Link 复制过去）。</summary>
    private static string DefaultFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tongji-2026-1-personal.json");

    /// <summary>
    /// 载入课表。<paramref name="explicitPath"/> 优先；给了路径但读不出来时**不**静默退回样例，
    /// 而是直接失败 —— 否则"显式指定了 fixture"这件事本身就没法验证。
    /// </summary>
    public static LoadedTimetable Load(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return new LoadedTimetable(explicitPath, Import(File.ReadAllText(explicitPath)));
        }

        var path = DefaultFixturePath;
        if (File.Exists(path))
        {
            return new LoadedTimetable(path, Import(File.ReadAllText(path)));
        }

        return new LoadedTimetable("内置样例（未找到 fixtures/tongji-2026-1-personal.json）", Import(DemoData.PersonalJson));
    }

    /// <summary>走核心库的导入管线：适配器探测 → 落成课表。</summary>
    private static Timetable Import(string json)
    {
        var result = ImportPipeline.ImportTimetable(new ImportInput { Text = json });
        var timetable = ImportPipeline.MaterializeTimetable(result);
        if (timetable.Courses.Count == 0)
        {
            throw new InvalidOperationException("导入结果里没有任何课程（课表为空或格式不匹配）。");
        }

        return timetable;
    }
}
