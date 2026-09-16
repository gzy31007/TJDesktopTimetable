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

    /// <summary>
    /// 轻薄亚克力（<c>DesktopAcrylicKind.Thin</c>）：比 Acrylic 更薄更透，壁纸几乎原样透出来
    /// （DeskBox 的文件夹 / 待办窗口就是这一档观感）。
    ///
    /// <para>⚠️ **必须追加在末尾**：枚举值直接落进 <c>settings.json</c>（STJ 默认按数字序列化），
    /// 插在中间会让老配置的材质整体错位。</para>
    /// </summary>
    AcrylicThin,
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
    /// 是否开机自启（登录时自动拉起挂件）。
    ///
    /// <para>落点是 <c>HKCU\...\CurrentVersion\Run</c> 下的 <c>TJDesktopTimetable</c> 值
    /// （见 <see cref="AutoStart"/>）；这里只记"用户想要什么"，启动时会以它为准把注册表对齐一遍。</para>
    /// </summary>
    public bool LaunchAtLogin { get; init; }

    /// <summary>
    /// 是否在启动时检查新版本（默认开）。
    ///
    /// <para>查询只做一次 GET（GitHub 的 <c>/releases/latest</c>），**不上传任何本机数据**；
    /// 关掉之后应用就完全不联网（README 的「离线」口径据此改写）。</para>
    /// </summary>
    public bool CheckUpdates { get; init; } = true;

    /// <summary>
    /// 用户点过「跳过此版本」的那个版本号（如 <c>1.3.0</c>）。
    /// 只压住它自己：更高的版本照样提示（语义见 <see cref="Tjt.Core.UpdateCheck.Evaluate"/>）。
    /// </summary>
    public string? SkippedVersion { get; init; }

    /// <summary>
    /// 是否显示周末两列（周六 / 周日）。
    ///
    /// <para>默认 <c>true</c>（与历史行为一致）。关掉后课表只画周一到周五，周末的课**不占列**
    /// —— 它们被布局层直接丢弃（<c>BoardOptions.ShowWeekend = false</c>），
    /// 且不计入"被周次过滤隐藏的时段"统计（见 <c>Layout.BuildBoard</c>）。</para>
    /// </summary>
    public bool ShowWeekend { get; init; } = true;

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
