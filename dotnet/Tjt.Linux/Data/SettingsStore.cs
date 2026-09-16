using System.Text.Json;
using System.Text.Json.Serialization;
using Tjt.Widget;

namespace Tjt.Linux.Data;

/// <summary>
/// 设置的读写（JSON 落盘，Tjt.App/Data/SettingsStore.cs 的移植）。
///
/// <para>位置：Linux 上 <see cref="Environment.SpecialFolder.ApplicationData"/> 映射 XDG
/// （<c>~/.config/TJDesktopTimetable/settings.json</c>），文件名与字段和 Windows 版同形。</para>
///
/// <para>容错：文件缺失 / 损坏 / 字段越界都退回默认值，**绝不因为设置读不出来就起不来**。</para>
/// </summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>设置文件所在目录（XDG：~/.config/TJDesktopTimetable）。</summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TJDesktopTimetable");

    /// <summary>设置文件路径。</summary>
    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>读取设置；任何异常都退回默认值（并留一行日志）。</summary>
    public static WidgetSettings Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            AppLog.Line($"[settings] 未找到 {path}，使用默认设置");
            return new WidgetSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<WidgetSettings>(json, Options);
            if (settings is null)
            {
                AppLog.Line($"[settings] {path} 反序列化为空，使用默认设置");
                return new WidgetSettings();
            }

            AppLog.Line($"[settings] 已读取 {path}：desktopLayer={settings.DesktopLayer} bounds={Describe(settings.Bounds)}");
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Line($"[settings] 读取失败，使用默认设置：{ex.Message}");
            return new WidgetSettings();
        }
    }

    /// <summary>保存设置；失败只记日志（不影响运行）。</summary>
    public static void Save(WidgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            AppLog.Line($"[settings] 已保存 {FilePath}：bounds={Describe(settings.Bounds)}");
        }
        catch (Exception ex)
        {
            AppLog.Line($"[settings] 保存失败：{ex.Message}");
        }
    }

    private static string Describe(WindowBounds? bounds) => bounds is null ? "default" : bounds.ToString();
}
