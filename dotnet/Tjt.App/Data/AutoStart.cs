using Microsoft.Win32;
using Tjt.Core;

namespace Tjt.App.Data;

/// <summary>
/// 开机自启的落点：<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 下的一个字符串值。
///
/// <para>策略是"**设置即真相**"：<c>settings.json</c> 里的 <c>LaunchAtLogin</c> 说什么，注册表就
/// 该是什么 —— 启动时对一次账（幂等），设置一变也立刻同步。这样用户手工删了注册表项、或换了
/// 安装目录（exe 路径变了），下一次启动都会自动纠正，不需要"先关一次再开一次"。</para>
///
/// <para>只写 HKCU：不需要管理员权限，也不碰其它用户。失败（组策略锁定 / 权限异常）只记日志，
/// 不影响应用启动 —— 开机自启是锦上添花，不该拖垮主流程。</para>
/// </summary>
internal static class AutoStart
{
    /// <summary>读取当前登录项的值；没有这项 / 读不出来返回 <c>null</c>。</summary>
    public static string? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupEntry.RunKeyPath);
            return key?.GetValue(StartupEntry.ValueName) as string;
        }
        catch (Exception ex)
        {
            AppLog.Line($"[startup] 读取登录项失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把注册表按 <paramref name="enabled"/> 对齐（幂等，值没变就不写）。
    /// </summary>
    /// <returns>注册表最终是否处于期望状态。</returns>
    public static bool Sync(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            // 理论上不会发生（.NET 6+ 拿得到当前进程的可执行文件）——真发生了就明说，
            // 别写一个指向空路径的自启项，那会让开机时静默失败
            AppLog.Line("[startup] 拿不到当前进程路径，跳过开机自启同步");
            return false;
        }

        try
        {
            var desired = StartupEntry.DesiredValue(enabled, exe);
            var current = Read();

            if (desired is null)
            {
                if (current is null) return true; // 已经是干净的
                using var key = Registry.CurrentUser.OpenSubKey(StartupEntry.RunKeyPath, writable: true);
                key?.DeleteValue(StartupEntry.ValueName, throwOnMissingValue: false);
                AppLog.Line($"[startup] 开机自启 → 关（已删除 Run 值，原值={current}）");
                return Read() is null;
            }

            if (StartupEntry.Matches(current, desired)) return true; // 已经对上了，不重复写

            using (var key = Registry.CurrentUser.CreateSubKey(StartupEntry.RunKeyPath, writable: true))
            {
                if (key is null)
                {
                    AppLog.Line($"[startup] 打开/创建 {StartupEntry.RunKeyPath} 失败（返回 null）");
                    return false;
                }
                key.SetValue(StartupEntry.ValueName, desired, RegistryValueKind.String);
            }

            AppLog.Line($"[startup] 开机自启 → 开（写入 Run 值：{desired}）");
            return StartupEntry.Matches(Read(), desired);
        }
        catch (Exception ex)
        {
            AppLog.Line($"[startup] 同步开机自启失败（enabled={enabled}）：{ex.Message}");
            return false;
        }
    }
}
