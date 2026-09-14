using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Tjt.App;

/// <summary>
/// 显式入口点。
///
/// 为什么自己写而不是用 XAML 自动生成的 <c>Main</c>：那个入口没有给我们"先解析命令行、
/// 再启动应用"的时机（<c>--smoke</c> / <c>--fixture</c> 必须在 <c>Application.Start</c>
/// 之前拿到，而 XAML 生成的 Main 只是 <c>Application.Start(_ =&gt; new App())</c>）。
///
/// 这里手工复刻那套流程：
/// 1. <c>Bootstrap.TryInitialize</c> 引导 Windows App SDK 运行时
///    （非打包应用必须显式做；.NET 项目下 WASDK 的 auto-initializer 不会自动生效）；
/// 2. 初始化 COM 包装器（CsWinRT）；
/// 3. <c>Application.Start</c> 起 XAML 消息循环。
/// </summary>
internal static class Program
{
    /// <summary>应用入口。</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        App.Startup = AppStartupOptions.Parse(args);

        // 非打包应用：显式引导 Windows App Runtime
        Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(0x00010008, out var error);
        if (error != 0)
        {
            Console.Error.WriteLine($"[fatal] Windows App SDK 引导失败：0x{error:X8}");
            Console.Error.WriteLine("[hint] 需要安装 Windows App Runtime 1.8（或改用自包含部署）。");
            Environment.Exit(1);
            return;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(_ =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            _ = new App();
        });
    }
}
