using Tjt.Core;
using Tjt.Core.Adapters;

namespace Tjt.Linux.Data;

/// <summary>这份课表是从哪来的（日志、导入窗口文案、以及"重新载入"的语义都看它）。</summary>
internal enum TimetableOrigin
{
    /// <summary>命令行 <c>--fixture &lt;path&gt;</c> 显式指定（自检与排查用）。</summary>
    Explicit,

    /// <summary>用户导入并落盘的 <c>timetable.json</c>。</summary>
    Imported,

    /// <summary>内置示例课表（虚构的演示数据 —— 表示"还没有真实课表"）。</summary>
    Demo,
}

/// <summary>课表数据的来源说明（日志与 smoke 输出要用）。</summary>
/// <param name="Source">人类可读的来源描述（文件路径或"内置示例课表"）。</param>
/// <param name="Timetable">落成的课表。</param>
/// <param name="Origin">来源分类。</param>
internal sealed record LoadedTimetable(string Source, Timetable Timetable, TimetableOrigin Origin);

/// <summary>
/// 取"当前课表"，按固定优先级：
///
/// <list type="number">
/// <item><c>--fixture &lt;path&gt;</c> 显式指定（**不**静默退回，读不出来直接失败 —— 否则"显式指定了"这件事没法验证）；</item>
/// <item>用户导入的 <c>~/.config/TJDesktopTimetable/timetable.json</c>（真正的日常路径）；</item>
/// <item>内置示例课表 <see cref="DemoData"/>（虚构数据）。</item>
/// </list>
///
/// <para><b>刻意没有"回退到 fixtures"这一步</b>：<c>dotnet/fixtures/</c> 里是脱敏过的**真实抓包**，
/// 它是测试与 <c>--fixture</c> 的黄金基准，不是演示数据 —— 早先把它当默认课表，用户会看到一张
/// 真实课表并以为"程序怎么有我的数据"。运行时只认"用户导入的"或"虚构的示例"。</para>
///
/// <para>Tjt.App/Data/AppHost.cs 的移植，口径完全一致。</para>
/// </summary>
internal static class AppHost
{
    /// <summary>内置示例课表的来源描述（设置页与日志会显示它）。</summary>
    public const string DemoSource = "内置示例课表（虚构数据，可用「导入课表」替换）";

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

        return new LoadedTimetable(DemoSource, Import(DemoData.PersonalJson), TimetableOrigin.Demo);
    }

    /// <summary>人类可读的来源分类（导入窗口与日志用）。</summary>
    public static string OriginLabel(TimetableOrigin origin) => origin switch
    {
        TimetableOrigin.Explicit => "命令行指定",
        TimetableOrigin.Imported => "已导入的课表",
        TimetableOrigin.Demo => "内置示例课表",
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
