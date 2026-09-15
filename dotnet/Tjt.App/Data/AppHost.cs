using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.App.Data;

/// <summary>这份课表是从哪来的（日志、设置页文案、以及"重新载入"的语义都看它）。</summary>
internal enum TimetableOrigin
{
    /// <summary>命令行 <c>--fixture &lt;path&gt;</c> 显式指定（自检与排查用）。</summary>
    Explicit,

    /// <summary>用户导入并落盘的 <c>timetable.json</c>。</summary>
    Imported,

    /// <summary>随应用分发的脱敏黄金数据 <c>fixtures/tongji-2026-1-personal.json</c>。</summary>
    Fixture,

    /// <summary>内置样例（只有 5 门课，读不到 fixtures 时兜底）。</summary>
    Demo,
}

/// <summary>课表数据的来源说明（日志与 smoke 输出要用）。</summary>
/// <param name="Source">人类可读的来源描述（文件路径或"内置样例"）。</param>
/// <param name="Timetable">落成的课表。</param>
/// <param name="Origin">来源分类。</param>
internal sealed record LoadedTimetable(string Source, Timetable Timetable, TimetableOrigin Origin);

/// <summary>
/// 取"当前课表"，按固定优先级：
///
/// <list type="number">
/// <item><c>--fixture &lt;path&gt;</c> 显式指定（**不**静默退回，读不出来直接失败 —— 否则"显式指定了"这件事没法验证）；</item>
/// <item>用户导入的 <c>%APPDATA%\TJDesktopTimetable\timetable.json</c>（真正的日常路径）；</item>
/// <item>输出目录下的脱敏黄金数据 <c>fixtures/tongji-2026-1-personal.json</c>；</item>
/// <item>内置样例 <see cref="DemoData"/>。</item>
/// </list>
///
/// <para>这一段刻意只在**壳**里做，不放进 <c>Tjt.Widget</c>：读文件不是纯计算。</para>
/// </summary>
internal static class AppHost
{
    /// <summary>约定位置：输出目录下的 <c>fixtures/</c>（csproj 用 Content Link 复制过去）。</summary>
    private static string DefaultFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tongji-2026-1-personal.json");

    /// <summary>
    /// 载入课表（"重新载入"与启动走的是同一个入口 —— 语义只有一处，不会两边走偏）。
    /// </summary>
    public static LoadedTimetable Load(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var text = File.ReadAllText(explicitPath);
            return new LoadedTimetable(explicitPath, Import(text), TimetableOrigin.Explicit);
        }

        var imported = TimetableStore.Load();
        if (imported is not null)
        {
            return new LoadedTimetable(TimetableStore.FilePath, imported, TimetableOrigin.Imported);
        }

        var path = DefaultFixturePath;
        if (File.Exists(path))
        {
            return new LoadedTimetable(path, Import(File.ReadAllText(path)), TimetableOrigin.Fixture);
        }

        return new LoadedTimetable("内置样例（未找到 fixtures/tongji-2026-1-personal.json）", Import(DemoData.PersonalJson), TimetableOrigin.Demo);
    }

    /// <summary>人类可读的来源分类（设置页与日志用）。</summary>
    public static string OriginLabel(TimetableOrigin origin) => origin switch
    {
        TimetableOrigin.Explicit => "命令行指定",
        TimetableOrigin.Imported => "已导入的课表",
        TimetableOrigin.Fixture => "内置黄金数据",
        TimetableOrigin.Demo => "内置样例",
        _ => "未知",
    };

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
