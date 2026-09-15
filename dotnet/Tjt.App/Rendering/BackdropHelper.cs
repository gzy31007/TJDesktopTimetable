using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Tjt.App.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace Tjt.App.Rendering;

/// <summary>
/// 窗口材质（Mica / Mica Alt / Acrylic）—— 这是走 WinUI 而不是继续用 Electron 的**核心理由**。
///
/// 背景：Electron 侧三条系统材质路径实测都拿不到 DeskBox 那种质感 —— DWM 对"从未被激活的窗口"
/// 一律降级成近黑平色（mica / mica-alt / acrylic 三个设置测出同一个值 20,21,22），
/// 而本项目的挂件正是"挂桌面、几乎不被激活"的窗口。
///
/// WinUI 的 <see cref="MicaController"/> + <see cref="SystemBackdropConfiguration"/> 能把这层策略
/// 显式接管：<see cref="SystemBackdropConfiguration.IsInputActive"/> 由我们自己设置、**恒为 true**，
/// 于是窗口哪怕不被激活也能拿到材质（这正是 DeskBox 的做法：它只在绑定时无条件置 true，
/// 从不按 <c>WindowActivatedEventArgs</c> 去改这个值）。
///
/// <para><b>2026-09-15 修的一处观感缺陷</b>：早先这里挂了 <c>Window.Activated</c>，回调里按
/// <c>WindowActivationState</c> 把 <see cref="SystemBackdropConfiguration.IsInputActive"/> 置回 <c>false</c>
/// —— 挂件贴桌面层、几乎从不被激活，于是材质**长期处于"非激活"档**，被 DWM 降级成近黑平灰
/// （用户实测："鼠标没选中窗口时背景发灰，DeskBox 无论选中与否都是透亮的"）。现在没有任何代码
/// 会把它改回 false。</para>
///
/// 因此首选控制器路径；只有控制器不可用（系统不支持 / API 异常）时才退回
/// <see cref="Window.SystemBackdrop"/> 的内置 <see cref="MicaBackdrop"/>，
/// 后者简单但会把"活跃状态判定"交回系统。
/// </summary>
internal sealed class BackdropHelper : IDisposable
{
    private readonly Window _window;
    private readonly SystemBackdropConfiguration _configuration;
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    private bool _builtInFallback;

    private BackdropHelper(Window window, SystemBackdropConfiguration configuration)
    {
        _window = window;
        _configuration = configuration;
    }

    /// <summary>材质模式（日志/自检用；<c>none</c> 表示系统压根不支持）。</summary>
    public string Mode { get; private set; } = "none";

    /// <summary>
    /// 材质当前跟随的主题。
    ///
    /// <para><b>必须显式设置，不能靠系统默认</b>：<see cref="SystemBackdropConfiguration.Theme"/> 不设时
    /// 材质跟随**系统**主题 —— 用户在设置里选"浅色"而系统是深色时，材质仍是深色，
    /// 而色块是半透明的（浅色 13% 透明度），叠在深底上就"完全不是浅色"
    /// （真机实测：浅色模式顶部像素 `#261E1C`，与深色模式一模一样，但色块反而更暗）。</para>
    /// </summary>
    public bool IsDark { get; private set; } = true;

    /// <summary>
    /// 尝试给窗口挂上材质；返回是否成功。
    ///
    /// <paramref name="mode"/> 决定材质种类：<c>Mica</c> / <c>MicaAlt</c> 用 <see cref="MicaController"/>，
    /// <c>Acrylic</c> 用 <see cref="DesktopAcrylicController"/>（需要窗口 <c>transparent: true</c>，
    /// 否则糊不出来），<c>Solid</c> 什么都不挂。
    ///
    /// 控制器路径优先（能显式接管活跃策略），失败退回内置 backdrop，再失败就是无材质 ——
    /// 任何一步都不该让挂件起不来。
    /// </summary>
    /// <param name="dark">材质跟随深色还是浅色（由窗口主题决定，见 <see cref="IsDark"/>）。</param>
    public static BackdropHelper Apply(Window window, MaterialMode mode = MaterialMode.Mica, bool dark = true)
    {
        ArgumentNullException.ThrowIfNull(window);

        var configuration = new SystemBackdropConfiguration
        {
            // 恒为 true：挂件从不指望"被激活"，见类注释。
            // ⚠️ WASDK 1.8 的 SystemBackdropConfiguration **只有 IsInputActive**（没有 IsActive；
            // 真机试过加 IsActive 会直接编译错 CS0117），所以"始终用激活外观"只能靠这一个开关
            // —— 而它并不足以让 Mica 变亮，真正的浓淡在 MicaController 的 TintOpacity/LuminosityOpacity
            // 上（见 ApplyMicaTint）。
            IsInputActive = true,
            Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        var helper = new BackdropHelper(window, configuration) { IsDark = dark };
        helper.Bind(mode);
        return helper;
    }

    /// <summary>
    /// 运行时换材质（**即时生效**，不重建窗口）。
    ///
    /// <para>与 DeskBox 的 <c>ApplyBackdropPreference()</c> 同一个思路：它也是在运行时按
    /// 材质签名重新绑控制器。这里先 <see cref="Dispose"/> 掉旧的再绑新的 ——
    /// **复用 <see cref="SystemBackdropConfiguration"/>**，只换控制器。</para>
    ///
    /// <para><b>Acrylic 的固有代价仍然成立</b>：它要求窗口 <c>transparent: true</c>（Electron 侧的
    /// 硬结论，WinUI 这里同样是合成要求）。我们的窗口按 mica/solid 创建（非透明），
    /// 所以运行时切到 Acrylic 可能拿不到该有的糊感 —— 这时如实记日志，不假装成功。</para>
    /// </summary>
    /// <param name="mode">目标材质。</param>
    /// <returns>是否真的绑上了（<c>Solid</c> 恒为 true）。</returns>
    public bool SetMaterial(MaterialMode mode)
    {
        if (Mode == Describe(mode)) return true;

        Dispose();
        Bind(mode);
        var ok = Mode == Describe(mode);
        AppLog.Line(ok
            ? $"[backdrop] 材质已切换 → {Mode}（运行时，未重建窗口）"
            : $"[backdrop] 材质切换失败 → mode={mode}（实际 {Mode}），保留原观感");
        return ok;
    }

    /// <summary>把材质模式映射成 <see cref="Mode"/> 的取值（两边要保持一致，供切换判定用）。</summary>
    /// <param name="mode">材质模式。</param>
    private static string Describe(MaterialMode mode) => mode switch
    {
        MaterialMode.Solid => "solid",
        MaterialMode.Acrylic when DesktopAcrylicController.IsSupported() => "acrylic-controller",
        MaterialMode.MicaAlt when MicaController.IsSupported() => "mica-controller(alt)",
        MaterialMode.Mica when MicaController.IsSupported() => "mica-controller",
        MaterialMode.Acrylic => "acrylic-builtin-fallback",
        MaterialMode.MicaAlt => "mica-builtin-fallback",
        _ => "none",
    };

    /// <summary>按材质模式绑定控制器（失败则退回内置 backdrop，再失败就是无材质）。</summary>
    /// <param name="mode">材质模式。</param>
    private void Bind(MaterialMode mode)
    {
        _builtInFallback = false;

        if (mode == MaterialMode.Solid)
        {
            Mode = "solid";
            return;
        }

        try
        {
            switch (mode)
            {
                case MaterialMode.Acrylic when DesktopAcrylicController.IsSupported():
                    var acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
                    acrylic.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
                    acrylic.SetSystemBackdropConfiguration(_configuration);
                    _acrylic = acrylic;
                    Mode = "acrylic-controller";
                    break;

                case MaterialMode.MicaAlt when MicaController.IsSupported():
                    _mica = BindMica(_window, _configuration, MicaKind.BaseAlt, IsDark);
                    Mode = "mica-controller(alt)";
                    break;

                case MaterialMode.Acrylic:
                case MaterialMode.MicaAlt:
                case MaterialMode.Mica when MicaController.IsSupported():
                    _mica = BindMica(_window, _configuration, MicaKind.Base, IsDark);
                    Mode = "mica-controller";
                    break;

                default:
                    // 控制器不可用：退回内置 backdrop（简单，但活跃状态交回系统）
                    _window.SystemBackdrop = mode == MaterialMode.Acrylic
                        ? new DesktopAcrylicBackdrop()
                        : new MicaBackdrop { Kind = mode == MaterialMode.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base };
                    _builtInFallback = true;
                    Mode = mode == MaterialMode.Acrylic ? "acrylic-builtin-fallback" : "mica-builtin-fallback";
                    break;
            }
        }
        catch (Exception ex)
        {
            _mica?.Dispose();
            _mica = null;
            _acrylic?.Dispose();
            _acrylic = null;
            Mode = $"none({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// 主题切换时更新材质主题（材质控制器复用，不重建 —— 重建会泄漏原生合成内存，DeskBox 的注释也这么说）。
    /// </summary>
    /// <param name="dark">是否深色。</param>
    public void UpdateTheme(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        _configuration.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        AppLog.Line($"[backdrop] 材质主题 → {(dark ? "深色" : "浅色")}");
    }

    /// <summary>建 MicaController 并绑到窗口（默认物料的活跃策略由 <c>IsInputActive</c> 接管）。</summary>
    private static MicaController BindMica(Window window, SystemBackdropConfiguration configuration, MicaKind kind, bool dark)
    {
        var controller = new MicaController { Kind = kind };
        ApplyMicaTint(controller, dark);
        // Window 的实现类型不是投影后的接口，必须用 WinRT 的 As<> 转换
        controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configuration);
        return controller;
    }

    /// <summary>
    /// Mica 的浓淡（深色主题）—— 对齐 DeskBox 的"无论是否选中都透亮"。
    ///
    /// <para>WinUI 的默认 <c>TintOpacity = 0.8</c> 会把阴影层压得很实：真机实测（2560×1600 深色壁纸，
    /// 窗口摆在 DeskBox 面板同一区域）背景是 <c>#221F1F</c>（纯暗灰，看不出壁纸色），而 DeskBox 的面板是
    /// <c>#3B2321~#4C2B29</c>（暗，但壁纸的暖色透得出来）。变体矩阵实测：</para>
    /// <list type="bullet">
    /// <item><description><c>TintOpacity = 0.8</c>（WinUI 默认，且 TintColor 未设）→ <c>#221F1F</c>，太闷；</description></item>
    /// <item><description>只把 <c>TintOpacity</c> 调到 0.0 / 0.6（TintColor 仍是默认的透明）→ <c>#AA999A</c> / <c>#A39C9D</c>，
    /// 透过头 —— 说明**没有颜色的 tint 层等于不存在**，光调不透明度不管用；</description></item>
    /// <item><description><b><c>TintColor = #202020</c> + <c>TintOpacity = 0.6</c></b> → 与 DeskBox 的 <c>#3B~#4C</c> 同一档。</description></item>
    /// </list>
    /// <para>亮度层留在 0.5（WinUI 默认）：Mica 的"壁纸色调"就是这一层，不动它才不至于变成另一种材质。</para>
    ///
    /// <para>浅色档**保持 WinUI 默认**：浅色下默认值本身不暗（实测底色 `#F9F1EF`），没有一并调
    /// （避免顺手改掉没验证过的观感）。</para>
    /// </summary>
    private static void ApplyMicaTint(MicaController controller, bool dark)
    {
        if (!dark) return;
        controller.TintColor = Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
        controller.TintOpacity = 0.6f;
        controller.LuminosityOpacity = 0.5f;
    }

    /// <summary>
    /// 系统当前是不是深色主题（读注册表，避免为一个值引 Win32 主题 API）。
    /// 注册表读不到时按浅色处理。
    /// </summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);
            return value is int raw && raw == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _mica?.Dispose();
        _mica = null;
        _acrylic?.Dispose();
        _acrylic = null;
        if (_builtInFallback)
        {
            _window.SystemBackdrop = null;
            _builtInFallback = false;
        }
    }
}
