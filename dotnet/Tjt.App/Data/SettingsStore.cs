using System.Text.Json;
using System.Text.Json.Serialization;
using Tjt.Widget;

namespace Tjt.App.Data;

/// <summary>
/// 设置的读写（JSON 落盘）。
///
/// <para>位置：<c>%APPDATA%\TJDesktopTimetable\settings.json</c> —— 与 Electron 侧的 userData
/// 同名目录（那个是安装版应用的数据目录），将来两端共用一份设置时不用搬家。</para>
///
/// <para>容错：文件缺失 / 损坏 / 字段越界都退回默认值，**绝不因为设置读不出来就起不来**
/// —— 设置是"记住上次的样子"，不是启动的必要条件。</para>
/// </summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TJDesktopTimetable");
    private static string? _diagnosticDirectory;

    /// <summary>设置、课表、凭据与 WebView profile 共用的目录；隔离验收不触碰用户目录。</summary>
    public static string Directory => _diagnosticDirectory ?? DefaultDirectory;
    internal static bool IsDiagnostic => _diagnosticDirectory is not null;

    /// <summary>只在显式 session-check 模式启动、任何存储读取前调用。</summary>
    internal static void UseDiagnosticDirectory(string path)
    {
        // Keep this opt-in seam on ordinary local paths: Windows device/UNC, short-name
        // and trailing-dot/space aliases would defeat string overlap checks below.
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Contains('~') || path.Replace('/', '\\').Split('\\').Skip(1)
                .Any(part => part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':')))
            throw new ArgumentException("验收目录必须为无别名的本机绝对路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultDirectory));
        if (full == Path.GetPathRoot(full)
            || full.Equals(real, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || real.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("验收目录不能覆盖或包含用户数据目录。");
        // Reject aliases before touching stores or WebView profiles, including links below
        // an existing diagnostic directory on a restart. Do not traverse a reparse point.
        for (var ancestor = new DirectoryInfo(full); ancestor is not null; ancestor = ancestor.Parent)
            RejectLink(ancestor);
        var pending = new Stack<DirectoryInfo>();
        if (System.IO.Directory.Exists(full)) pending.Push(new DirectoryInfo(full));
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                RejectLink(entry);
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
        const string markerText = "TJDesktopTimetable synthetic session v1";
        var marker = Path.Combine(full, ".tjt-session-check");
        if (System.IO.Directory.Exists(full) && System.IO.Directory.EnumerateFileSystemEntries(full).Any()
            && (!File.Exists(marker) || File.ReadAllText(marker) != markerText))
            throw new ArgumentException("首次验收必须使用空目录；只允许复用本工具创建的隔离目录。");
        System.IO.Directory.CreateDirectory(full);
        if (!File.Exists(marker)) File.WriteAllText(marker, markerText);
        _diagnosticDirectory = full;
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new ArgumentException("验收目录及其上下级不能包含符号链接或 junction。");
    }

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
