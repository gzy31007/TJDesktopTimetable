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
/// 显式接管：<see cref="SystemBackdropConfiguration.IsInputActive"/> 由我们自己设置，
/// 于是窗口哪怕不被激活也能拿到材质（这正是 DeskBox 的做法）。
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
    /// 尝试给窗口挂上材质；返回是否成功。
    ///
    /// <paramref name="mode"/> 决定材质种类：<c>Mica</c> / <c>MicaAlt</c> 用 <see cref="MicaController"/>，
    /// <c>Acrylic</c> 用 <see cref="DesktopAcrylicController"/>（需要窗口 <c>transparent: true</c>，
    /// 否则糊不出来），<c>Solid</c> 什么都不挂。
    ///
    /// 控制器路径优先（能显式接管活跃策略），失败退回内置 backdrop，再失败就是无材质 ——
    /// 任何一步都不该让挂件起不来。
    /// </summary>
    public static BackdropHelper Apply(Window window, MaterialMode mode = MaterialMode.Mica)
    {
        ArgumentNullException.ThrowIfNull(window);

        var configuration = new SystemBackdropConfiguration { IsInputActive = true };
        var helper = new BackdropHelper(window, configuration);

        if (mode == MaterialMode.Solid)
        {
            helper.Mode = "solid";
            return helper;
        }

        try
        {
            switch (mode)
            {
                case MaterialMode.Acrylic when DesktopAcrylicController.IsSupported():
                    var acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
                    acrylic.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
                    acrylic.SetSystemBackdropConfiguration(configuration);
                    helper._acrylic = acrylic;
                    helper.Mode = "acrylic-controller";
                    break;

                case MaterialMode.MicaAlt when MicaController.IsSupported():
                    helper._mica = BindMica(window, configuration, MicaKind.BaseAlt);
                    helper.Mode = "mica-controller(alt)";
                    break;

                case MaterialMode.Acrylic:
                case MaterialMode.MicaAlt:
                case MaterialMode.Mica when MicaController.IsSupported():
                    helper._mica = BindMica(window, configuration, MicaKind.Base);
                    helper.Mode = "mica-controller";
                    break;

                default:
                    // 控制器不可用：退回内置 backdrop（简单，但活跃状态交回系统）
                    window.SystemBackdrop = mode == MaterialMode.Acrylic
                        ? new DesktopAcrylicBackdrop()
                        : new MicaBackdrop { Kind = mode == MaterialMode.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base };
                    helper._builtInFallback = true;
                    helper.Mode = mode == MaterialMode.Acrylic ? "acrylic-builtin-fallback" : "mica-builtin-fallback";
                    break;
            }

            window.Activated += helper.OnWindowActivated;
        }
        catch (Exception ex)
        {
            helper._mica?.Dispose();
            helper._mica = null;
            helper._acrylic?.Dispose();
            helper._acrylic = null;
            helper.Mode = $"none({ex.GetType().Name})";
        }

        return helper;
    }

    /// <summary>建 MicaController 并绑到窗口（默认物料的活跃策略由 <c>IsInputActive</c> 接管）。</summary>
    private static MicaController BindMica(Window window, SystemBackdropConfiguration configuration, MicaKind kind)
    {
        var controller = new MicaController { Kind = kind };
        // Window 的实现类型不是投影后的接口，必须用 WinRT 的 As<> 转换
        controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configuration);
        return controller;
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

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _configuration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _window.Activated -= OnWindowActivated;
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
