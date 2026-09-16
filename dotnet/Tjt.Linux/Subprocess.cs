using System.Diagnostics;

namespace Tjt.Linux;

/// <summary>
/// 带超时的子进程读取。以前 fc-match（Program）/ gsettings（MainWindow.DetectSystemDark）
/// 都是阻塞 ReadToEnd：helper 一挂住，启动就挂死在窗口构造函数里。
/// 超时或启动失败一律返回 null，不抛 —— 调用方各有兜底。
/// </summary>
internal static class Subprocess
{
    public static string? Output(string fileName, params string[] args) => Output(fileName, args, TimeoutMilliseconds);

    private const int TimeoutMilliseconds = 2000;

    private static string? Output(string fileName, string[] args, int timeoutMilliseconds)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null) return null;

            var read = process.StandardOutput.ReadToEndAsync();
            if (!read.Wait(timeoutMilliseconds))
            {
                TryKill(process);
                return null;
            }

            if (!process.WaitForExit(timeoutMilliseconds)) TryKill(process);
            return read.Result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 杀不掉就算了：调用方拿到的是 null
        }
    }
}
