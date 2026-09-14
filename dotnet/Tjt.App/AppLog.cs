namespace Tjt.App;

/// <summary>
/// 极简日志：同时写控制台与文件。
///
/// 为什么必须有文件通道：本项目是 <c>WinExe</c>（Windows GUI 子系统），进程**不附加控制台**，
/// 于是 `Start-Process -RedirectStandardOutput` 抓到的文件是空的 —— CI 上一度因此完全看不到
/// 应用输出，只能看到"90 秒超时"这种没信息量的结论。
///
/// 用法：`--log &lt;path&gt;` 指定文件；不指定时写 <c>%TEMP%\tjt-app.log</c>。
/// 每次 <see cref="Line"/> 都 Flush，进程被强杀时也能留下最后到达的阶段。
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
            File.WriteAllText(path, $"== Tjt.App {DateTimeOffset.Now:O} =={Environment.NewLine}");
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
