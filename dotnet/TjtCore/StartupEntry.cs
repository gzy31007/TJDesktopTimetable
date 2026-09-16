namespace Tjt.Core;

/// <summary>
/// 开机自启的**约定与字符串规则**（纯逻辑，平台无关；注册表读写留在 Tjt.App 的 <c>Data/AutoStart.cs</c>）。
///
/// <para>为什么要单独一个类型：命令行拼错一个引号，症状是"开机后什么都没发生"，而在注册表里
/// 肉眼看不出差别 —— 把这部分放到能单测的地方（Linux/CI 上也跑），比在真机上试错便宜。</para>
/// </summary>
/// <remarks>
/// <b>值名沿用已删除的 Electron 线</b>：那一版的 <c>productName</c> 就是 <c>TJDesktopTimetable</c>，
/// <c>app.setLoginItemSettings()</c> 写的也是这个名字。同名意味着新版**接管**旧配置
/// （写进去的就是我们自己的 exe），不会留下两条互相打架的自启项。
/// </remarks>
public static class StartupEntry
{
    /// <summary>登录项所在的键（相对 <c>HKEY_CURRENT_USER</c>）。用 HKCU 而不是 HKLM：不需要管理员权限。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>登录项的值名。</summary>
    public const string ValueName = "TJDesktopTimetable";

    /// <summary>
    /// 期望写进登录项的命令行：exe 路径**总是**加引号。
    ///
    /// <para>路径里有空格（<c>C:\Program Files\...</c>）或中文都很常见，不加引号时 Windows 会把
    /// 命令行截到第一个空格 —— 结果是"自启项看着在、开机什么也不发生"。已经带引号的输入原样返回，
    /// 避免出现 <c>""C:\...""</c>。</para>
    /// </summary>
    public static string CommandLine(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var trimmed = exePath.Trim();
        return trimmed.StartsWith('"') ? trimmed : $"\"{trimmed}\"";
    }

    /// <summary>
    /// 现有值与期望值是否等价：去首尾空白 + 忽略大小写（Windows 路径不区分大小写）。
    /// 相等就不必再写注册表 —— 每次启动都写一遍既无意义，也会让"自启项被动过"这类排查失去线索。
    /// </summary>
    public static bool Matches(string? current, string expected) =>
        string.Equals(current?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 设置 → 该写进注册表的值（<c>null</c> = **删掉**这一项）。
    ///
    /// <para>关掉时必须删干净：只改 <c>settings.json</c> 不删注册表项的话，开机照样会起 ——
    /// 那种"关了但没关"最难被用户理解。</para>
    /// </summary>
    public static string? DesiredValue(bool enabled, string exePath) => enabled ? CommandLine(exePath) : null;
}
