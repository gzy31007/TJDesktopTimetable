namespace Tjt.Linux;

/// <summary>
/// 极简日志：同时写控制台与文件（Tjt.App/AppLog.cs 的移植）。
///
/// 不指定 --log 时写系统临时目录 tjt-linux.log；每次写都 Flush，强杀也能留下最后阶段。
/// </summary>
internal static class AppLog
{
    private static readonly Lock Gate = new();
    private static string? _path;

    /// <summary>日志文件路径（未初始化时返回 null）。</summary>
    public static string? Path => _path;

    /// <summary>指定日志文件（覆盖默认位置）。</summary>
    public static void UseFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _path = path;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, $"== Tjt.Linux {DateTimeOffset.Now:O} =={Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志本身不能成为失败原因
            _path = null;
        }
    }

    /// <summary>写一行（控制台 + 文件，均立即 Flush）。</summary>
    public static void Line(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();
        Write(message);
    }

    /// <summary>写一行错误（控制台 stderr + 文件）。</summary>
    public static void Error(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.Flush();
        Write(message);
    }

    private static void Write(string message)
    {
        if (_path is not { } path) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, message + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // 同上：日志失败不影响主流程
        }
    }
}
