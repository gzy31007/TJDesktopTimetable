using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
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
    private MicaController? _controller;
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
    /// <paramref name="micaAlt"/> 为 <c>true</c> 时用 <see cref="MicaKind.BaseAlt"/>（Mica Alt）。
    /// </summary>
    public static BackdropHelper Apply(Window window, bool micaAlt = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        var configuration = new SystemBackdropConfiguration { IsInputActive = true };
        var helper = new BackdropHelper(window, configuration);

        try
        {
            if (MicaController.IsSupported())
            {
                var controller = new MicaController { Kind = micaAlt ? MicaKind.BaseAlt : MicaKind.Base };
                // WinUI 的 Window 实现了 ICompositionSupportsSystemBackdrop（就是材质的目标）
                // Window 的实现类型不是"投影后的接口"，要用 WinRT 的 As<> 做投影转换（官方样例写法）。
                // 直接传 window 会得到 CS1503：无法把 Window 转成 ICompositionSupportsSystemBackdrop。
                controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
                controller.SetSystemBackdropConfiguration(configuration);
                helper._controller = controller;
                helper.Mode = micaAlt ? "mica-controller(alt)" : "mica-controller";
            }
            else
            {
                window.SystemBackdrop = new MicaBackdrop { Kind = micaAlt ? MicaKind.BaseAlt : MicaKind.Base };
                helper._builtInFallback = true;
                helper.Mode = "mica-builtin-fallback";
            }

            // 跟随窗口激活状态：失焦时 DWM 会切到"不活跃"物料（灰一点），这是正常行为。
            window.Activated += helper.OnWindowActivated;
        }
        catch (Exception ex)
        {
            // 材质失败不该让挂件起不来：退回内置 Mica，再不行就是纯色底。
            helper._controller?.Dispose();
            helper._controller = null;
            try
            {
                window.SystemBackdrop = new MicaBackdrop();
                helper._builtInFallback = true;
                helper.Mode = $"mica-builtin-fallback({ex.GetType().Name})";
            }
            catch (Exception inner)
            {
                helper.Mode = $"none({inner.GetType().Name})";
            }
        }

        return helper;
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
        _controller?.Dispose();
        _controller = null;
        if (_builtInFallback)
        {
            _window.SystemBackdrop = null;
            _builtInFallback = false;
        }
    }
}
