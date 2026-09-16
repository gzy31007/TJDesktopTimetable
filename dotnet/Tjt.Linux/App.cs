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

            // 没有"真实课表"（只有黄金数据 / 内置样例）时自动开导入窗口，
            // 对齐上游"首次启动引导导入"的行为；--import-window 可强制打开。
            if (main.LoadedOrigin != TimetableOrigin.Imported || App.Startup.OpenImportWindow)
            {
                AppLog.Line("[startup] 没有已导入的课表：自动打开导入窗口");
                main.OpenImportWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
