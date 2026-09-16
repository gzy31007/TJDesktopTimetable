using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Tjt.Linux.Data;

namespace Tjt.Linux;

/// <summary>应用入口（code-only，无 XAML）：主题挂载 + 主窗口创建。</summary>
internal sealed class App : Application
{
    /// <summary>命令行选项（Program 里解析，OnFrameworkInitializationCompleted 里消费）。</summary>
    public static AppStartupOptions Startup { get; set; } = new();

    public override void Initialize()
    {
        // FluentTheme 只为滚动条 / 按钮 / 菜单这些控件的默认观感服务；
        // 挂件自身的全部配色都由 Tjt.Widget/TintPalette 显式给出。
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow(App.Startup);
            desktop.MainWindow = main;
            main.Show();

            // 没有"真实课表"（挂件上是内置示例课表）时自动开导入窗口 —— 首次启动的引导。
            // Linux 没有内置登录窗口，导入窗口就是"粘贴一条浏览器请求"的入口（同济 / 交大通用），
            // 所以这里不需要、也不该替用户选学校。--import-window 可强制打开。
            // --fixture（Explicit）是显式自检/截图路径，不该被导入窗口盖住（与 Windows 线同口径）；
            // --no-import-window 一律不弹（脚本 / Xvfb 下要确定首屏）。
            var shouldOpenImport =
                (main.LoadedOrigin is TimetableOrigin.Demo || App.Startup.OpenImportWindow)
                && !App.Startup.SuppressImportWindow;
            if (shouldOpenImport)
            {
                AppLog.Line("[startup] 没有已导入的课表：自动打开导入窗口");
                main.OpenImportWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
