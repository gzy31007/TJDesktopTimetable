using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Tjt.App.Rendering;

/// <summary>
/// 窗口图标 —— 把 <c>Assets/app.ico</c> 挂到窗口上（标题栏 / 任务栏 / Alt-Tab 都用它）。
///
/// <para><b>为什么必须显式设</b>：WinUI 3 的 <see cref="Window"/> 没有 <c>Icon</c> 属性，
/// 它注册的窗口类也不带图标 —— 不设就由系统兜底成"空白应用"那个默认图标。
/// 实测症状：内置登录窗口的标题栏与任务栏都是那个默认图标，而托盘图标一直是对的
/// （它走 <c>LoadImage</c> 加载同一份 ico）。官方通道是
/// <see cref="AppWindow.SetIcon(string)"/>：它把 ico 里的各个尺寸分别设为窗口的
/// <c>ICON_SMALL</c> / <c>ICON_BIG</c>，标题栏与任务栏因此同时正确。</para>
///
/// <para><b>只设 exe 的 <c>ApplicationIcon</c> 不够</b>：那只改 PE 资源（资源管理器 / 任务管理器
/// 看到的图标），窗口自身仍会回落到 <c>IDI_APPLICATION</c>，所以两件都要做 ——
/// exe 那份在 <c>Tjt.App.csproj</c>，窗口这份在这里。</para>
///
/// <para>图标是**运行时**从输出目录读的（与托盘共用一份 <c>Assets/app.ico</c>，csproj 里是
/// <c>Content</c> 复制，不进资源索引）：所以 exe 旁边缺这个文件时只是"没有图标"，
/// 绝不能让窗口起不来 —— <see cref="Apply"/> 一律不抛，失败只记一行日志。</para>
/// </summary>
internal static class WindowIcon
{
    /// <summary>图标文件路径（<c>Assets/app.ico</c>，与托盘图标同一份）。</summary>
    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    /// <summary>
    /// 把应用图标挂到窗口上；返回是否成功。**任何失败都只记日志、不抛异常** ——
    /// 图标属于外观，缺了它窗口仍须正常显示。
    /// </summary>
    internal static bool Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                AppLog.Line($"[icon] 跳过窗口图标：找不到 {path}");
                return false;
            }

            window.AppWindow.SetIcon(path);
            AppLog.Line($"[icon] 窗口图标已设置 {path}");
            return true;
        }
        catch (Exception ex)
        {
            // 个别环境下 SetIcon 会因图标格式/句柄问题失败；那不该影响窗口本身
            AppLog.Error($"[icon] 设置窗口图标失败（{path}）：{ex.Message}");
            return false;
        }
    }
}
