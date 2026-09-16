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

    /// <summary>当前 Acrylic 是不是 Thin 档（主题切换 / 重绑时要按同一档给参数）。</summary>
    private bool _acrylicThin;

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
        MaterialMode.AcrylicThin when DesktopAcrylicController.IsSupported() => "acrylic-thin-controller",
        MaterialMode.Acrylic when DesktopAcrylicController.IsSupported() => "acrylic-controller",
        MaterialMode.AcrylicThin or MaterialMode.Acrylic => "acrylic-builtin-fallback",
        MaterialMode.MicaAlt when MicaController.IsSupported() => "mica-controller(alt)",
        MaterialMode.Mica when MicaController.IsSupported() => "mica-controller",
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
                case MaterialMode.AcrylicThin or MaterialMode.Acrylic when DesktopAcrylicController.IsSupported():
                    // Acrylic 走控制器（Base / Thin 两档），参数按 DeskBox 的机制显式给（见 ApplyAcrylicTint）。
                    // ⚠️ 验证时注意：Acrylic 透的是**窗口下面的内容**（不像 Mica 用壁纸色调）——
                    // 窗口下面压着黑窗口时照出来就是灰黑，别据此判定"Acrylic 不生效"（实测踩过）。
                    _acrylicThin = mode == MaterialMode.AcrylicThin;
                    var acrylic = new DesktopAcrylicController
                    {
                        Kind = _acrylicThin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base,
                    };
                    ApplyAcrylicTint(acrylic, IsDark, _acrylicThin);
                    acrylic.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
                    acrylic.SetSystemBackdropConfiguration(_configuration);
                    _acrylic = acrylic;
                    Mode = _acrylicThin ? "acrylic-thin-controller" : "acrylic-controller";
                    break;

                case MaterialMode.MicaAlt when MicaController.IsSupported():
                    _mica = BindMica(_window, _configuration, MicaKind.BaseAlt, IsDark, MaterialMode.MicaAlt);
                    Mode = "mica-controller(alt)";
                    break;

                case MaterialMode.Mica when MicaController.IsSupported():
                    _mica = BindMica(_window, _configuration, MicaKind.Base, IsDark, MaterialMode.Mica);
                    Mode = "mica-controller";
                    break;

                default:
                    // 控制器不可用：退回内置 backdrop（简单，但活跃状态交回系统）
                    var acrylicFallback = mode is MaterialMode.Acrylic or MaterialMode.AcrylicThin;
                    _window.SystemBackdrop = acrylicFallback
                        ? new DesktopAcrylicBackdrop()
                        : new MicaBackdrop { Kind = mode == MaterialMode.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base };
                    _builtInFallback = true;
                    Mode = acrylicFallback ? "acrylic-builtin-fallback" : "mica-builtin-fallback";
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
        // Acrylic 的浓淡随主题变（DeskBox 在主题签名变化时也是整份参数重发）；Mica 由配置对象自己跟主题走
        if (_acrylic is not null) ApplyAcrylicTint(_acrylic, dark, _acrylicThin);
        AppLog.Line($"[backdrop] 材质主题 → {(dark ? "深色" : "浅色")}");
    }

    /// <summary>
    /// Acrylic 两档（Base / Thin）的浓淡 —— 机制对齐 DeskBox：它把 <c>DesktopAcrylicController.Kind</c>
    /// 分成 <c>Base</c> / <c>Thin</c>，并且**两档都显式设** <c>TintColor</c> / <c>FallbackColor</c> /
    /// <c>TintOpacity</c> / <c>LuminosityOpacity</c>（Thin 明显更轻），而不是交给系统默认。
    ///
    /// <para>取值按它的插值机制取中档（材质强度 0.5）：Base 深色 tint 0.45 / 亮度 0.60（浅色 0.37 / 0.68）；
    /// Thin 深色 0.23 / 0.36（浅色 0.18 / 0.43）。TintColor 与 FallbackColor 用深灰 <c>#202226</c>
    /// （DeskBox 也就是这个基色再掺一点系统 accent）。</para>
    ///
    /// <para>⚠️ Acrylic 透的是**窗口下面的内容**：截图验证时窗口下面若压着黑窗口，照出来就是灰黑 ——
    /// 别据此判定"Acrylic 没生效"（实测踩过这个坑，`.tools/shot-top.ps1` 现在会先报告下方窗口类名）。</para>
    /// </summary>
    private static void ApplyAcrylicTint(DesktopAcrylicController controller, bool dark, bool thin)
    {
        var tint = Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26);
        controller.TintColor = tint;
        controller.FallbackColor = tint;
        controller.TintOpacity = (float)(thin ? (dark ? 0.23 : 0.18) : (dark ? 0.45 : 0.37));
        controller.LuminosityOpacity = (float)(thin ? (dark ? 0.36 : 0.43) : (dark ? 0.60 : 0.68));
    }

    /// <summary>建 MicaController 并绑到窗口（默认物料的活跃策略由 <c>IsInputActive</c> 接管）。</summary>
    private static MicaController BindMica(
        Window window,
        SystemBackdropConfiguration configuration,
        MicaKind kind,
        bool dark,
        MaterialMode mode)
    {
        var controller = new MicaController { Kind = kind };
        ApplyMicaTint(controller, dark, mode);
        // Window 的实现类型不是投影后的接口，必须用 WinRT 的 As<> 转换
        controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configuration);
        return controller;
    }

    /// <summary>
    /// Mica 的浓淡（深色主题）—— 机制对齐 DeskBox（GPL 只读参考，只取"用哪两个参数、朝哪个方向"这类事实，
    /// 具体数值由本机采样迭代定）。
    ///
    /// <para><b>关键事实：Mica Base 与 BaseAlt 的档位是相反的</b> —— DeskBox 里
    /// Base 是**低 tint + 高亮度**（tint 0.04→0.46、luminosity 深色 0.78→0.94，按"材质强度"插值），
    /// Alt 才是中等 tint（0.28→0.82）+ 中亮度（0.34→0.72）；它的 tint 色也**不是壁纸色**，
    /// 而是深灰基色（深色 `#202226`）只掺约 7% 系统 accent。</para>
    ///
    /// <para><b>2026-09-16 修正</b>：先前我们给的是 tint 0.6 + luminosity 0.5（自己按采样凑的），
    /// 方向与 DeskBox 的 Base 档正好相反 —— tint 层（中性灰）占大头，于是背景**又灰又亮**
    /// （用户实测："深色主题下 mica 的背景现在有点亮"）。现在四档都走这条低 tint / 高亮度机制：</para>
    /// <list type="bullet">
    /// <item><description><b>Mica</b>：tint 0.25 / 亮度 0.86（默认档）；</description></item>
    /// <item><description><b>Mica Alt</b>：tint 0.55 / 亮度 0.53（Alt 本来就是"更实、分层更明显"那一档）；</description></item>
    /// <item><description><b>Acrylic</b>：tint 0.35 / 亮度 0.72；<b>轻薄亚克力</b>：tint 0.10 / 亮度 0.95（最薄最透）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么 Acrylic 两档也用 Mica 控制器</b>（2026-09-16 实测结论）：WinUI 的
    /// <c>DesktopAcrylicController</c>（Base / Thin 都试过）与内置 <c>DesktopAcrylicBackdrop</c> 在
    /// 本机只拿到 <c>FallbackColor</c>（纯灰 <c>#222325</c> / <c>#2C2C2C</c>，壁纸完全透不出来），
    /// 换 Win32 的 <c>SetWindowCompositionAttribute</c> + <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c>
    /// （DeskBox 的机制）更糟：<c>#08141A</c> 近黑 —— WinUI 3 的合成层把 DWM 的 accent blur 挡掉了。
    /// 只有 Mica 控制器真能出材质，所以"轻薄"用它的低 tint 档实现，日志里的 mode 如实写
    /// <c>acrylic-mica</c> / <c>acrylic-thin-mica</c>（不假装是 Acrylic 合成）。</para>
    ///
    /// <para>浅色档**保持 WinUI 默认**：浅色下默认值本身不暗（实测底色 `#F9F1EF`），没有一并调
    /// （避免顺手改掉没验证过的观感）。</para>
    /// </summary>
    private static void ApplyMicaTint(MicaController controller, bool dark, MaterialMode mode)
    {
        if (!dark) return;
        var (r, g, b, tint, luminosity) = mode switch
        {
            MaterialMode.MicaAlt => ((byte)0x20, (byte)0x22, (byte)0x26, 0.55f, 0.53f),
            _ => ((byte)0x20, (byte)0x22, (byte)0x26, 0.25f, 0.86f),
        };
        controller.TintColor = Windows.UI.Color.FromArgb(0xFF, r, g, b);
        controller.TintOpacity = tint;
        controller.LuminosityOpacity = luminosity;
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
