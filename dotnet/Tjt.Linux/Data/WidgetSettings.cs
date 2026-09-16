using Tjt.Widget;

namespace Tjt.Linux.Data;

/// <summary>主题模式（跟随系统 = 探测桌面环境配色，见 MainWindow.DetectSystemDark）。</summary>
internal enum ThemeMode
{
    /// <summary>跟随系统（KDE 读 kdedefaults / GNOME 读 gsettings）。</summary>
    Auto,

    /// <summary>强制深色。</summary>
    Dark,

    /// <summary>强制浅色。</summary>
    Light,
}

/// <summary>
/// 挂件设置（Tjt.App/Data/WidgetSettings.cs 的移植，去掉了 Windows 专属的材质字段）。
///
/// <para>文件名与核心字段和 Windows 版同形（PascalCase，settings.json）；
/// Linux 侧没有 Mica/Acrylic，材质与边框校正两个概念不存在，省略后两端互读互不影响。</para>
/// </summary>
internal sealed record WidgetSettings
{
    /// <summary>是否显示挂件。</summary>
    public bool ShowWidget { get; init; } = true;

    /// <summary>是否贴桌面层（Linux 上 = X11 <c>_NET_WM_STATE_BELOW</c>）。</summary>
    public bool DesktopLayer { get; init; } = true;

    /// <summary>
    /// 是否显示周末两列。关掉后课表只画周一到周五，周末的课**不占列**
    /// （<c>BoardOptions.ShowWeekend = false</c>），与 Windows 版语义一致。
    /// </summary>
    public bool ShowWeekend { get; init; } = true;

    /// <summary>窗口位置与尺寸（DIP）；<c>null</c> = 首次启动，用默认右下角。</summary>
    public WindowBounds? Bounds { get; init; }

    /// <summary>主题模式。</summary>
    public ThemeMode Theme { get; init; } = ThemeMode.Auto;
}
