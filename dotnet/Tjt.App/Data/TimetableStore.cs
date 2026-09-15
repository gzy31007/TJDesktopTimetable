using Tjt.Core;

namespace Tjt.App.Data;

/// <summary>
/// 已导入课表的落盘（<c>%APPDATA%\TJDesktopTimetable\timetable.json</c>）。
///
/// <para>与 Electron 侧**同一个文件名、同一套字段名**（camelCase，见 <see cref="TimetableJson"/>），
/// 所以两端可以互相读对方写出的文件；用户手工替换/备份这份 JSON 也仍然有效。</para>
///
/// <para>写入用"临时文件 + 改名"原子替换：直接覆盖时断电/崩溃会留下半个文件，
/// 而这份文件正是每次启动都读的东西（Electron 侧的 store.ts 用了同样的手法）。</para>
/// </summary>
internal static class TimetableStore
{
    private const string FileName = "timetable.json";

    /// <summary>课表文件路径（与 <c>settings.json</c> 同目录）。</summary>
    public static string FilePath => Path.Combine(SettingsStore.Directory, FileName);

    /// <summary>
    /// 读取已导入的课表；没有文件 / 读坏了 / 里面没有课程时返回 <c>null</c>
    /// （调用方据此回退到 fixtures 或内置样例）。
    /// </summary>
    public static Timetable? Load()
    {
        var path = FilePath;
        if (!File.Exists(path)) return null;

        try
        {
            var timetable = TimetableJson.Deserialize(File.ReadAllText(path));
            if (timetable is null)
            {
                AppLog.Line($"[timetable] {path} 解析失败（不是合法课表 JSON），按「没有导入过」处理");
                return null;
            }

            if (timetable.Courses.Count == 0)
            {
                AppLog.Line($"[timetable] {path} 里没有课程，按「没有导入过」处理");
                return null;
            }

            AppLog.Line($"[timetable] 已读取导入的课表 {path}：{timetable.Courses.Count} 门 / {Count(timetable)} 条上课安排");
            return timetable;
        }
        catch (Exception ex)
        {
            AppLog.Line($"[timetable] 读取 {path} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>保存课表；失败只记日志（不把异常抛回 UI 线程）。</summary>
    public static bool Save(Timetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        var path = FilePath;
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            var tmp = $"{path}.tmp";
            File.WriteAllText(tmp, TimetableJson.Serialize(timetable));
            File.Move(tmp, path, overwrite: true);
            AppLog.Line($"[timetable] 已保存 {path}：{timetable.Courses.Count} 门 / {Count(timetable)} 条上课安排");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Line($"[timetable] 保存 {path} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>删掉已导入的课表（"清空"）：之后启动会回退到 fixtures / 内置样例。</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                AppLog.Line($"[timetable] 已删除 {FilePath}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Line($"[timetable] 删除 {FilePath} 失败：{ex.Message}");
        }
    }

    /// <summary>上课安排条数（日志与界面文案共用）。</summary>
    public static int Count(Timetable timetable) => timetable.Courses.Sum(course => course.Sessions.Count);
}
