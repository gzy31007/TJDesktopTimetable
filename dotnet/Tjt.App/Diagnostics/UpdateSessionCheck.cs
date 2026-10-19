using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tjt.App.Data;
using Tjt.App.Rendering;
using Tjt.App.Win32;
using Tjt.Core;
using WinRT.Interop;

namespace Tjt.App;

/// <summary>
/// 隔离 session driver 的 <c>update-*</c> 动作：只点生产入口，只记真实 XAML 与已挂载的几何。
///
/// <para>单独一条路径的原因：这些动作把设置窗口停在「关于」页，原位观察块接着按名字 <c>Find</c>
/// 导入页控件会抛异常，所以 loop 提前分叉（见 ImportSessionCheck.cs 的 update- 分支）。
/// 计划角度则是：update-* 之后若要跑 import / fetch / reload，先插一步 <c>rebuild-import</c>，
/// 或把 update 动作放最后。</para>
/// </summary>
public partial class App
{
    /// <summary>主代理在 OpenUpdatePage 诊断分支写出的下载地址（本文件只读，不写不删）。</summary>
    private const string SessionDownloadCaptureName = "update-download.txt";

    /// <summary>执行一步 <c>update-*</c>；观察由 loop 写 <c>step-i.json</c> 并汇总进 session.json。</summary>
    private async Task<object> RunUpdateSessionStep(MainWindow widget, SettingsWindow settings, SessionStep step, int index)
    {
        if (step.Lock is not null) throw new InvalidDataException("update 动作不支持 Lock");
        // 只有 update-check 出网：先校验，失败时还没切页、没动应用状态。
        if (step.Action == "update-check") RequireLoopbackUpdateSource();
        // 切页 / 重画之后，页内容要过一轮布局才 IsLoaded；导航是 ListView，不会误满足「页内有按钮」。
        bool AboutPageReady() => settings.ShownPageIndex == SettingsWindow.PageAbout
            && Descendants(settings.Content).OfType<Button>().Any(button => button.IsLoaded);

        settings.SelectPage(SettingsWindow.PageAbout);
        await Until(AboutPageReady);

        var capturedBefore = ReadSessionDownloadCapture();
        switch (step.Action)
        {
            case "update-snapshot":
                break;
            case "update-check":
                Invoke(Descendants(settings.Content).OfType<Button>()
                    .Single(button => button.Content as string == "检查更新"));
                await Until(() => AboutPageReady() && Descendants(settings.Content).OfType<Button>()
                    .Any(button => button.IsEnabled && button.Content as string == "检查更新"));
                break;
            case "update-skip":
                // 页上那颗「跳过此版本」只在 HasUpdate 时出现：先要同一个前提，否则记的是用户点不到的跳过。
                if (widget.CurrentUpdate is not { HasUpdate: true })
                    throw new InvalidDataException("update-skip 需要当前结论为 HasUpdate（先 update-check）");
                Invoke(Descendants(settings.Content).OfType<Button>()
                    .Single(button => button.Content as string == "跳过此版本"));
                await Until(() => AboutPageReady() && widget.CurrentUpdate is { HasUpdate: false });
                break;
            case "update-download":
                // 只点生产按钮：它的 Click 就是下载入口（诊断下写 update-download.txt，不开浏览器）。
                Invoke(Descendants(widget.Content).OfType<Button>().FirstOrDefault(button =>
                        AutomationProperties.GetAutomationId(button) == BoardRenderer.UpdateButtonId)
                    ?? throw new InvalidOperationException(
                        $"挂件上没有 AutomationId={BoardRenderer.UpdateButtonId} 的按钮：当前结论不是 HasUpdate"));
                await Task.Delay(100); // Invoke 同步写文件，留一拍给写方
                break;
            default:
                throw new InvalidDataException("Unknown session action");
        }

        await WaitForSessionBoard(widget);
        var update = widget.CurrentUpdate;
        var downloadButton = Descendants(widget.Content).OfType<Button>().FirstOrDefault(button =>
            AutomationProperties.GetAutomationId(button) == BoardRenderer.UpdateButtonId);
        var scale = SessionDpiScale(widget);
        var client = widget.AppWindow.ClientSize;
        return new
        {
            Index = index,
            step.Action,
            step.Url,
            Update = update is null ? null : new
            {
                Status = update.Status.ToString(),
                HasUpdate = update.HasUpdate,
                Latest = update.Latest?.ToString(),
            },
            // 同一条结论下按钮该打开的地址（纯规则函数），与下面捕获到的 URL 对照。
            ExpectedDownloadUrl = update is null ? null : UpdateCheck.DownloadUrlFor(update),
            SkippedVersion = widget.CurrentSettings.SkippedVersion,
            PersistedSkippedVersion = SettingsStore.Load().SkippedVersion,
            DownloadButton = new
            {
                Count = downloadButton is null ? 0 : 1,
                Loaded = downloadButton?.IsLoaded,
                Enabled = downloadButton?.IsEnabled,
                Name = downloadButton is null ? null : AutomationProperties.GetName(downloadButton),
                Geometry = downloadButton is null ? null : (object)ObserveSessionElement(downloadButton, widget.Content),
            },
            DownloadUrlBefore = capturedBefore,
            DownloadUrl = ReadSessionDownloadCapture(),
            // 关于页的真实 XAML：文字与按钮文案都读已加载的元素。
            SettingsText = Text(settings.Content),
            SettingsButtons = Descendants(settings.Content).OfType<Button>()
                .Select(button => button.Content is TextBlock block ? block.Text : button.Content as string)
                .ToList(),
            // 顶部条 / 标题 / 右动作区的已挂载几何：320 DIP 裁切就看这组数字。
            Header = ObserveSessionUpdateHeader(widget),
            ClientDip = new { Width = client.Width / scale, Height = client.Height / scale },
        };
    }

    /// <summary>顶部条 / 标题 / 右动作区（结构取自 BoardRenderer.BuildHeaderBar，不重算布局）。</summary>
    private static object? ObserveSessionUpdateHeader(MainWindow widget)
    {
        var scroller = BoardScroller(widget);
        if (VisualTreeHelper.GetParent(scroller) is not Grid body) return null;
        if (body.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetRow(child) == 0) is not Grid host)
            return null;
        var row = host.Children.OfType<Grid>().FirstOrDefault();
        if (row is null) return null;
        FrameworkElement? InRow(int column) => row.Children.OfType<FrameworkElement>()
            .FirstOrDefault(child => Grid.GetColumn(child) == column);
        // 左区第 1 列就是标题（第 0 列是应用字形），渲染层给它 CharacterEllipsis 与整行 ToolTip。
        var left = InRow(0) as Grid;
        var title = left is null
            ? null
            : left.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetColumn(child) == 1);
        var actions = InRow(1);
        return new
        {
            Geometry = ObserveSessionElement(host, widget.Content),
            Actions = actions is null ? null : (object)ObserveSessionElement(actions, widget.Content),
            Title = title is null ? null : (object)ObserveSessionElement(title, widget.Content),
        };
    }

    /// <summary>读一次 update-download.txt；<c>null</c> = 还没写 / 内容为空 / 读不到。</summary>
    private static string? ReadSessionDownloadCapture()
    {
        var path = Path.Combine(SettingsStore.Directory, SessionDownloadCaptureName);
        try
        {
            if (!File.Exists(path)) return null;
            var url = File.ReadAllText(path).Trim();
            return url.Length == 0 ? null : url;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>update-check 只许打 <c>--update-api</c> 的 loopback 合成服务；给了 <c>--update-proxy</c> 也要求 loopback。</summary>
    private void RequireLoopbackUpdateSource()
    {
        if (Startup.UpdateApiUrl is not { } api)
            throw new InvalidDataException("update-check 需要 --update-api 指向 loopback 合成服务");
        RequireLoopback(api);
        if (Startup.UpdateProxyUrl is { } proxy) RequireLoopback(proxy);
    }
}
