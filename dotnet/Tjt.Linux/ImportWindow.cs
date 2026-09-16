using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Tjt.Core.Adapters;
using Tjt.Linux.Data;

namespace Tjt.Linux;

/// <summary>
/// 导入窗口（Tjt.App/Rendering/ImportPage.cs 的精简移植）：
/// 粘贴浏览器请求抓取 / 粘贴或选择本地 JSON 导入 / 清空与重新载入。
///
/// <para>Linux 版没有 WebView2 内置登录，"粘贴一条 1 系统的请求"就是主路径，
/// 与 Windows 版的入口 B 完全同一条管线（TjtCore 的解析 + 探测 + 适配器）。</para>
/// </summary>
internal sealed class ImportWindow : Window
{
    private const double ContentWidth = 560;

    private static readonly Color OkGreen = Color.FromRgb(0x1B, 0x7F, 0x3B);
    private static readonly Color FailRed = Color.FromRgb(0xC3, 0x36, 0x28);

    private readonly ImportService _service;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBox _requestBox;
    private readonly TextBox _jsonBox;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _probes = new() { Spacing = 2 };
    private readonly StackPanel _diagnostics = new() { Spacing = 6 };
    private Control _diagnosticsSection = new Control();
    private readonly TextBlock _currentLine = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private Button? _fetchButton;

    public ImportWindow(MainWindow owner, ImportService service)
    {
        _service = service;

        Title = "导入课表 · TJDesktopTimetable";
        Width = 620;
        Height = 620;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = owner.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

        _requestBox = new TextBox
        {
            PlaceholderText = "浏览器 F12 → Network → 右键课表请求 → Copy → Copy as PowerShell，整段粘贴到这里",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            MaxHeight = 180,
            Text = ImportService.LoadSavedRequest(),
        };
        _jsonBox = new TextBox
        {
            PlaceholderText = "或把课表接口的 JSON 响应粘贴到这里（也可用下面的按钮选文件）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxHeight = 160,
        };

        Content = BuildLayout();
        RefreshCurrentLine();

        Closed += (_, _) =>
        {
            _cancel.Cancel();
            _cancel.Dispose();
        };
    }

    private Control BuildLayout()
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(BuildFetchSection());
        panel.Children.Add(BuildJsonSection());
        panel.Children.Add(BuildProbesSection());
        panel.Children.Add(BuildDiagnosticsSection());
        panel.Children.Add(BuildManageSection());

        return new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(16),
                Child = panel,
            },
        };
    }

    /// <summary>从 1 系统抓取（粘贴浏览器请求）。</summary>
    private Control BuildFetchSection()
    {
        _fetchButton = new Button
        {
            Content = "获取我的课表",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _fetchButton.Click += OnFetch;

        return BuildSection("从学校系统获取", "粘贴一条浏览器请求（F12 → Copy as PowerShell，同济 / 交大通用），程序只发这一次请求。请求只存本机，不上传。", new Control[]
        {
            _requestBox,
            _fetchButton,
        });
    }

    /// <summary>本地 JSON 导入。</summary>
    private Control BuildJsonSection()
    {
        var browse = new Button { Content = "选择文件…" };
        browse.Click += OnBrowseJson;

        var apply = new Button { Content = "导入并应用" };
        apply.Click += OnImportJson;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { browse, apply },
        };

        return BuildSection("本地 JSON 导入", "把课表接口的响应另存为 JSON 后导入（可选再存一份校历响应）。", new Control[]
        {
            _jsonBox,
            row,
        });
    }

    private Control BuildProbesSection()
    {
        return BuildSection("探测结果", "最近一次抓取的请求 / 状态 / 数据识别。", new Control[] { _probes });
    }

    private Control BuildDiagnosticsSection()
    {
        _diagnosticsSection = BuildSection(
            "适配器诊断",
            "学期 / 节次解析的告警 —— 导入「看起来成功」但学期或节次不对时，先看这里。",
            new Control[] { _diagnostics });
        // 没有诊断时不占版面（与 Windows 版 ImportPage 的口径一致）
        _diagnosticsSection.IsVisible = false;
        return _diagnosticsSection;
    }

    private Control BuildManageSection()
    {
        var clear = new Button { Content = "清空课表" };
        clear.Click += OnClear;

        var reload = new Button { Content = "重新载入" };
        reload.Click += OnReload;

        var openDir = new Button { Content = "打开数据目录" };
        openDir.Click += OnOpenDataDirectory;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { clear, reload, openDir },
        };

        var dir = new TextBlock
        {
            Text = $"数据目录：{ImportService.DataDirectory}",
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };

        return BuildSection("当前课表", null, new Control[] { _currentLine, row, dir, _message });
    }

    private static Control BuildSection(string title, string? description, IEnumerable<Control> children)
    {
        var panel = new StackPanel { Spacing = 8, Width = ContentWidth, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        });
        if (description is { Length: > 0 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = description,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private async void OnFetch(object? sender, RoutedEventArgs e)
    {
        if (_fetchButton is null) return;
        _fetchButton.IsEnabled = false;
        _fetchButton.Content = "获取中…";
        SetMessage(null, "正在请求…");
        try
        {
            var outcome = await _service.FetchAsync(_requestBox.Text ?? string.Empty, _cancel.Token);
            ShowOutcome(outcome);
        }
        catch (OperationCanceledException)
        {
            // 窗口关掉：不用回填
        }
        finally
        {
            _fetchButton.IsEnabled = true;
            _fetchButton.Content = "获取我的课表";
        }
    }

    private async void OnBrowseJson(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择课表 JSON",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        try
        {
            var texts = new List<string>();
            foreach (var file in files)
            {
                await using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                texts.Add(await reader.ReadToEndAsync());
            }

            _jsonBox.Text = texts.Count == 1 ? texts[0] : string.Join("\n", texts);
        }
        catch (Exception ex)
        {
            SetMessage(false, $"读文件失败：{ex.Message}");
        }
    }

    private void OnImportJson(object? sender, RoutedEventArgs e)
    {
        var outcome = _service.ImportText(_jsonBox.Text, adapterId: null, files: null);
        ShowOutcome(outcome);
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        ShowOutcome(_service.ClearTimetable());
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        ShowOutcome(_service.ReloadTimetable());
    }

    private void OnOpenDataDirectory(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ImportService.DataDirectory);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
            {
                Arguments = ImportService.DataDirectory,
                UseShellExecute = false,
            });
            AppLog.Line($"[import] 已请求打开数据目录 {ImportService.DataDirectory}");
        }
        catch (Exception ex)
        {
            SetMessage(false, $"打开目录失败：{ex.Message}（目录：{ImportService.DataDirectory}）");
        }
    }

    private void ShowOutcome(ImportOutcome outcome)
    {
        SetMessage(outcome.Ok, outcome.Message);
        _probes.Children.Clear();
        foreach (var probe in outcome.Probes)
        {
            _probes.Children.Add(new TextBlock
            {
                Text = $"{probe.Label}：{probe.Value}",
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        // 诊断（tongji.term.unknown / tongji.schedule.missing …）是"导入看似成功、
        // 其实学期或节次解析不对"的唯一提示：不渲染不落日志的话，Linux 用户永远看不到。
        _diagnostics.Children.Clear();
        _diagnosticsSection.IsVisible = outcome.Diagnostics.Count > 0;
        foreach (var diagnostic in outcome.Diagnostics)
        {
            AppLog.Line($"[import] 诊断 {diagnostic.Level} {diagnostic.Code}：{diagnostic.Message}");
            _diagnostics.Children.Add(new TextBlock
            {
                Text = $"[{Level(diagnostic.Level)}] {diagnostic.Code}：{diagnostic.Message}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = diagnostic.Level == DiagnosticLevel.Error ? new SolidColorBrush(FailRed) : null,
            });
        }

        RefreshCurrentLine();
    }

    private static string Level(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Error => "错误",
        DiagnosticLevel.Warn => "警告",
        _ => "提示",
    };

    private void SetMessage(bool? ok, string message)
    {
        _message.Text = message;
        _message.Foreground = ok switch
        {
            true => new SolidColorBrush(OkGreen),
            false => new SolidColorBrush(FailRed),
            _ => Foreground ?? Brushes.Gray,
        };
    }

    private void RefreshCurrentLine()
    {
        _currentLine.Text = $"当前：{_service.DescribeCurrent()}";
    }
}
