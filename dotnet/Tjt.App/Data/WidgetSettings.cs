using Tjt.Widget;

namespace Tjt.App.Data;

/// <summary>主题模式。</summary>
internal enum ThemeMode
{
    /// <summary>跟随系统（读注册表 <c>AppsUseLightTheme</c>）。</summary>
    Auto,

    /// <summary>强制深色。</summary>
    Dark,

    /// <summary>强制浅色。</summary>
    Light,
}

/// <summary>窗口材质（与 Electron 侧 <c>settings.material</c> 同一套取值语义）。</summary>
internal enum MaterialMode
{
    /// <summary>不透明实色底（最稳，拿不到材质时的观感）。</summary>
    Solid,

    /// <summary>Mica：桌面壁纸色调的单层材质，保住 DWM 圆角。</summary>
    Mica,

    /// <summary>Mica Alt：分层更明显（WinUI 的 <c>MicaKind.BaseAlt</c>）。</summary>
    MicaAlt,

    /// <summary>Acrylic：更"玻璃"，但需要 <c>transparent: true</c>，代价是拿不到 DWM 圆角。</summary>
    Acrylic,
}

/// <summary>
/// 挂件设置（落盘到 <c>%APPDATA%\TJDesktopTimetable\settings.json</c>）。
///
/// <para>路径与 Electron 侧同名目录，将来两端要共读同一份时不用搬家；字段只放**本实现真正会读写**的，
/// 不做"预先兼容"的空占位（那种字段过一轮就变成没人敢删的垃圾）。</para>
/// </summary>
internal sealed record WidgetSettings
{
    /// <summary>是否显示挂件。</summary>
    public bool ShowWidget { get; init; } = true;

    /// <summary>是否贴桌面层（owner 挂 <c>SHELLDLL_DefView</c>）。</summary>
    public bool DesktopLayer { get; init; } = true;

    /// <summary>
    /// 窗口**外框**位置与尺寸（DIP）；<c>null</c> = 首次启动，用默认右下角。
    ///
    /// <para>存外框而不是客户区：`AppWindow.ResizeClient` 收的是客户区尺寸，而 Windows 允许的
    /// 最小窗口尺寸是按**外框**算的 —— 存客户区会让"恢复 → 又被系统抬高 → 再存"循环放大。
    /// 恢复时用 <see cref="FrameCorrection"/> 把外框换算回客户区。</para>
    /// </summary>
    public WindowBounds? Bounds { get; init; }

    /// <summary>
    /// 外框尺寸 - 客户区尺寸（DIP，取实测值）。
    ///
    /// 用来把保存的外框尺寸换算成 <c>ResizeClient</c> 需要的客户区尺寸。
    /// 首次运行没有这个值（按 0 试一次），随后由 <see cref="WidgetSettings"/> 的记录自适应收敛。
    /// </summary>
    public WindowBounds? FrameCorrection { get; init; }

    /// <summary>是否跳过系统材质（排查材质问题用）。</summary>
    public bool NoBackdrop { get; init; }

    /// <summary>主题模式。</summary>
    public ThemeMode Theme { get; init; } = ThemeMode.Auto;

    /// <summary>窗口材质。</summary>
    public MaterialMode Material { get; init; } = MaterialMode.Mica;
}
