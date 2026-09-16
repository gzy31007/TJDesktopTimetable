using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tjt.App.Data;
using Tjt.Core;
using Tjt.Core.Adapters;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Tjt.App.Rendering;

/// <summary>
/// 设置窗口的「导入课表」页（照 Electron 侧管理窗口的导入面板那一套功能做的 C# 版）：
/// ① 从 1 系统抓取（粘贴 F12 复制出来的浏览器请求）；② 本地 JSON 导入（粘贴 / 选文件 / 选适配器）；
/// ③ 当前课表摘要与"清空 / 重新载入 / 打开数据目录"。
///
/// <para>为什么整页单独一个文件：设置窗口本体只有"导航 + 三张卡"那么简单，而导入页有
/// 输入框、异步抓取、文件选择、结果面板四类东西 —— 混在一起会让 <c>SettingsWindow</c>
/// 从"搭骨架"变成"什么都干"。</para>
///
/// <para><b>只发意图</b>：本页不认识 <c>MainWindow</c>，所有动作都通过 <see cref="ImportService"/>
/// 走（它再回调外壳落盘/重画）。窗口与会话状态因此不需要泄漏到渲染层。</para>
/// </summary>
internal static class ImportPage
{
    private const double StatusFontSize = 12;

    /// <summary>构造整页。</summary>
    /// <param name="imports">导入编排（落盘 / 抓取 / 重新载入都从这里走）。</param>
    /// <param name="dark">深色主题（用色）。</param>
    /// <param name="windowHandle">设置窗口句柄（文件选择器要 <c>InitializeWithWindow</c>）。</param>
    /// <param name="xamlRoot">取当前 XamlRoot（<see cref="ContentDialog"/> 需要）。</param>
    /// <param name="openLogin">打开同济内置登录窗口（外壳提供；为 <c>null</c> 时按钮禁用）。</param>
    /// <param name="openSjtuLogin">打开交大内置登录窗口（同上）。</param>
    public static UIElement Build(
        ImportService imports,
        bool dark,
        nint windowHandle,
        Func<XamlRoot?> xamlRoot,
        Action? openLogin = null,
        Action? openSjtuLogin = null)
    {
        ArgumentNullException.ThrowIfNull(imports);

        var panel = new StackPanel { Spacing = 14, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(SettingsView.PageTitle("导入课表"));

        // ── 卡 1：当前课表
        var summary = new TextBlock
        {
            Text = imports.DescribeCurrent(),
            FontSize = 12,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var reload = new Button { Content = "重新载入", MinWidth = 96 };
        var clear = new Button { Content = "清空课表", MinWidth = 96 };
        var openFolder = new Button { Content = "打开数据目录", MinWidth = 120 };
        var dataButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        dataButtons.Children.Add(reload);
        dataButtons.Children.Add(clear);
        dataButtons.Children.Add(openFolder);

        var dataStatus = StatusPanel();

        var dataBody = new StackPanel { Spacing = 10 };
        dataBody.Children.Add(summary);
        dataBody.Children.Add(dataButtons);
        dataBody.Children.Add(dataStatus);

        var dataCard = SettingsView.Block(IconGlyph.Folder, "当前课表", null, dataBody, dark);

        // ── 卡 2：从 1 系统获取
        var requestBox = new TextBox
        {
            Text = ImportService.LoadSavedRequest(),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 112,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            PlaceholderText =
                "在这里粘贴从浏览器复制的请求（F12 → Network → 右键该请求 → Copy → Copy as PowerShell）。\n"
                + "也支持 Copy as cURL / 直接贴一条 URL。整条请求自带登录态，程序只做这一次请求。",
        };

        // 主操作放在卡片标题行右侧：不用滚动就能看见（放输入框下面会掉到首屏之外，截图实测过）
        var fetch = new Button { Content = "获取我的课表", MinWidth = 132, Style = AccentButtonStyle() };
        var fetchRing = new ProgressRing { IsActive = false, Width = 20, Height = 20, Margin = new Thickness(4, 0, 0, 0) };
        var fetchAction = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fetchAction.Children.Add(fetchRing);
        fetchAction.Children.Add(fetch);

        var help = new TextBlock
        {
            FontSize = StatusFontSize,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            Text =
                "怎么复制这条请求？（一次即可，课表变了再重来一次）\n"
                + "1. 浏览器登录学校课表页：同济 1.tongji.edu.cn（点开「我的课表」）或交大 j.sjtu.edu.cn（课表页）。\n"
                + "2. 按 F12 → Network → 刷新页面。\n"
                + "3. 找到返回 200、内容是课程列表的那条 → 右键 → Copy → Copy as PowerShell，整段粘贴到上面的框里。\n"
                + "   · 同济：课表页现在调的是 /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&studentCode=…\n"
                + "     （旧接口 /api/electionservice/student/xxxx/getDataBk 同样支持）；别复制成校历那条\n"
                + "     /api/baseresservice/schoolCalendar/detail —— 那只是学期起止，里面没有课程。\n"
                + "   · 交大：任意一条 listBySemester / listByWeek 都行 —— 程序只取其中的学期参数，\n"
                + "     改用登录态取整学期课表与教务日历（课表页默认那条按周请求不带周次信息）。\n"
                + "   · PowerShell 那份把登录态放在 $session.Cookies.Add(...) 行里，连请求一起复制过来即可；\n"
                + "     只复制了地址（没有 cookie）时程序会明确提示你。\n"
                + "4. 点「获取我的课表」。同济的学期 id 从请求里的 calendarId 自动取，所以「现在第几周」也是准的。\n"
                + "粘贴内容只保存在本机 " + ImportService.DataDirectory + "\\credentials.json，不上传、不进日志。",
        };

        var fetchStatus = StatusPanel();

        // ── 推荐路径：内置登录窗口（选学校 → 一个按钮；不用去浏览器抓请求，也不读别人的 cookie）
        // 学校用下拉框选，而不是每个学校一个按钮：按钮写死校名，再加一所学校就要再加一个按钮 + 一列回调。
        var schoolCombo = new ComboBox { MinWidth = 220, SelectedIndex = 0 };
        schoolCombo.Items.Add("同济大学（1 系统）");
        schoolCombo.Items.Add("上海交通大学（学在交大）");

        var loginButton = new Button
        {
            Content = "登录并获取课表",
            MinWidth = 148,
            Style = AccentButtonStyle(),
            IsEnabled = openLogin is not null || openSjtuLogin is not null,
        };
        loginButton.Click += (_, _) =>
        {
            if (schoolCombo.SelectedIndex == 1) openSjtuLogin?.Invoke();
            else openLogin?.Invoke();
        };

        var loginRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        loginRow.Children.Add(schoolCombo);
        loginRow.Children.Add(loginButton);

        // ⚠️ 这段说明**不能**塞进上面那个水平 StackPanel：水平 StackPanel 用无限宽度测量子元素，
        // TextWrapping 直接失效，窗口一窄文字就横着溢出（2026-09-17 用户实测"提示没有折叠"）。
        var loginHint = new TextBlock
        {
            Text = "在应用自己的窗口里打开所选学校的登录页（同济统一身份认证含短信 / 交大 jAccount），"
                 + "课表随即自动抓取导入 —— 本应用不接触你的密码。",
            FontSize = StatusFontSize,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
        };

        var loginStack = new StackPanel { Spacing = 8 };
        loginStack.Children.Add(loginRow);
        loginStack.Children.Add(loginHint);

        var advanced = new TextBlock
        {
            Text = "或者：粘贴一条浏览器请求（高级 —— 不想在本应用里登录时用）",
            FontSize = StatusFontSize,
            Opacity = 0.6,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var fetchBody = new StackPanel { Spacing = 10 };
        fetchBody.Children.Add(loginStack);
        fetchBody.Children.Add(advanced);
        fetchBody.Children.Add(requestBox);
        fetchBody.Children.Add(fetchStatus);
        fetchBody.Children.Add(help);

        var fetchCard = SettingsView.Block(
            IconGlyph.Globe,
            "从学校系统获取",
            "推荐内置登录（同济 / 交大）；也可以粘贴浏览器请求，用它的登录态抓一次",
            fetchBody,
            dark,
            fetchAction);

        // ── 卡 3：本地 JSON
        var adapters = new List<(string Id, string Name)> { (string.Empty, "自动探测（推荐）") };
        foreach (var adapter in imports.Adapters) adapters.Add((adapter.Id, adapter.DisplayName));
        var adapterCombo = new ComboBox
        {
            ItemsSource = adapters.Select(item => item.Name).ToList(),
            SelectedIndex = 0,
            MinWidth = 240,
        };

        var jsonBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 96,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            PlaceholderText =
                "把课表接口的响应 JSON 直接粘贴到这里，或点「选择 JSON 文件…」。\n"
                + "支持：同济 1 系统 / 上海交大课表 / 课表预览页 HTML / 通用 JSON（courses[].sessions[]）。",
        };

        var pick = new Button { Content = "选择 JSON 文件…", MinWidth = 132 };
        var runImport = new Button { Content = "导入并应用", MinWidth = 108, Style = AccentButtonStyle() };
        var clearInput = new Button { Content = "清空输入", MinWidth = 96 };
        var importRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        importRow.Children.Add(pick);
        importRow.Children.Add(clearInput);

        var fileList = new StackPanel { Spacing = 4 };
        var importStatus = StatusPanel();
        var importBody = new StackPanel { Spacing = 10 };
        importBody.Children.Add(adapterCombo);
        importBody.Children.Add(jsonBox);
        importBody.Children.Add(importRow);
        importBody.Children.Add(fileList);
        importBody.Children.Add(importStatus);

        var importCard = SettingsView.Block(IconGlyph.Document, "本地 JSON 导入", "解析结果直接落盘并替换桌面挂件上的课表", importBody, dark, runImport);

        panel.Children.Add(SettingsView.Card([dataCard], dark));
        panel.Children.Add(SettingsView.Card([fetchCard], dark));
        panel.Children.Add(SettingsView.Card([importCard], dark));

        // ── 已选文件：整页范围内共享（抓取与本地导入是两条独立的路，不共用这个列表）
        var picked = new List<ImportFile>();

        void RefreshFileList()
        {
            fileList.Children.Clear();
            foreach (var file in picked)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = file.Name,
                    FontSize = StatusFontSize,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Width = 420,
                });
                row.Children.Add(new TextBlock
                {
                    Text = $"{file.Text.Length / 1024.0:0.0} KB",
                    FontSize = StatusFontSize,
                    Opacity = 0.6,
                });
                fileList.Children.Add(row);
            }
        }

        // ── 动作
        reload.Click += (_, _) =>
        {
            Report(dataStatus, imports.ReloadTimetable(), dark);
            summary.Text = imports.DescribeCurrent();
        };

        openFolder.Click += (_, _) => OpenDataDirectory();

        clear.Click += async (_, _) =>
        {
            var root = xamlRoot();
            if (root is null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "清空已保存的课表？",
                Content = $"将删除 {TimetableStore.FilePath}，挂件回退到内置示例课表（再导入一次即替换）。",
                PrimaryButtonText = "清空",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            Report(dataStatus, imports.ClearTimetable(), dark);
            summary.Text = imports.DescribeCurrent();
        };

        fetch.Click += async (_, _) =>
        {
            fetch.IsEnabled = false;
            fetchRing.IsActive = true;
            Clear(fetchStatus);
            try
            {
                var outcome = await imports.FetchAsync(requestBox.Text);
                Report(fetchStatus, outcome, dark);
                summary.Text = imports.DescribeCurrent();
            }
            catch (Exception ex)
            {
                AppLog.Error($"[import] 抓取时异常：{ex}");
                Report(fetchStatus, ImportOutcome.Failure($"抓取时出错：{ex.Message}"), dark);
            }
            finally
            {
                fetch.IsEnabled = true;
                fetchRing.IsActive = false;
            }
        };

        pick.Click += async (_, _) =>
        {
            try
            {
                var files = await PickJsonFiles(windowHandle);
                if (files.Count == 0) return;
                picked.AddRange(files);
                RefreshFileList();
            }
            catch (Exception ex)
            {
                AppLog.Error($"[import] 选择文件失败：{ex}");
                Report(importStatus, ImportOutcome.Failure($"选择文件失败：{ex.Message}"), dark);
            }
        };

        runImport.Click += (_, _) =>
        {
            var adapterId = adapterCombo.SelectedIndex > 0 ? adapters[adapterCombo.SelectedIndex].Id : null;
            var outcome = imports.ImportText(jsonBox.Text, adapterId, picked);
            Report(importStatus, outcome, dark);
            summary.Text = imports.DescribeCurrent();
        };

        clearInput.Click += (_, _) =>
        {
            jsonBox.Text = string.Empty;
            picked.Clear();
            RefreshFileList();
            Clear(importStatus);
        };

        return panel;
    }

    /// <summary>用系统的文件选择对话框挑若干 JSON / HTML（返回全文，失败的文件跳过）。</summary>
    private static async Task<IReadOnlyList<ImportFile>> PickJsonFiles(nint windowHandle)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitializeWithWindow.Initialize(picker, windowHandle);
        foreach (var extension in new[] { ".json", ".html", ".htm", "*" }) picker.FileTypeFilter.Add(extension);

        var files = await picker.PickMultipleFilesAsync();
        var result = new List<ImportFile>();
        foreach (var file in files)
        {
            try
            {
                result.Add(new ImportFile(file.Name, await FileIO.ReadTextAsync(file)));
            }
            catch (Exception ex)
            {
                AppLog.Line($"[import] 读取所选文件失败：{ex.GetType().Name}");
            }
        }

        return result;
    }

    /// <summary>用资源管理器打开数据目录（用户要备份 / 手工替换 timetable.json 时最省事）。</summary>
    private static void OpenDataDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ImportService.DataDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Line($"[import] 打开数据目录失败：{ex.Message}");
        }
    }

    /* ------------------------------------------------------------------ 结果面板 */

    private static StackPanel StatusPanel() => new() { Spacing = 6 };

    private static void Clear(Panel target) => target.Children.Clear();

    /// <summary>把一次导入/抓取的结果铺进面板：一句话结论 + 探测行 + 适配器诊断。</summary>
    private static void Report(Panel target, ImportOutcome outcome, bool dark)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        target.Children.Clear();
        target.Children.Add(Banner(outcome.Message, outcome.Ok, dark));

        if (outcome.Probes.Count > 0)
        {
            var probes = new StackPanel { Spacing = 2 };
            foreach (var probe in outcome.Probes)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = probe.Label, FontSize = StatusFontSize, Opacity = 0.6, Width = 76 });
                row.Children.Add(new TextBlock
                {
                    Text = probe.Value,
                    FontSize = StatusFontSize,
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 620,
                });
                probes.Children.Add(row);
            }

            target.Children.Add(probes);
        }

        if (outcome.Diagnostics.Count > 0)
        {
            var diagnostics = new StackPanel { Spacing = 4 };
            foreach (var diagnostic in outcome.Diagnostics)
            {
                diagnostics.Children.Add(Banner(
                    $"[{Level(diagnostic.Level)}] {diagnostic.Message}",
                    diagnostic.Level != DiagnosticLevel.Error,
                    dark));
            }

            target.Children.Add(diagnostics);
        }
    }

    /// <summary>一句话结论的横幅（成功偏中性、失败偏红；与 Electron 侧导入面板的语义一致）。</summary>
    private static Border Banner(string text, bool ok, bool dark)
    {
        // 成功色：深色主题下要亮一档，否则压在深底上读不清
        var accent = ok
            ? (dark ? Color.FromArgb(255, (byte)108, (byte)203, (byte)108) : Color.FromArgb(255, (byte)15, (byte)123, (byte)15))
            : Color.FromArgb(255, (byte)196, (byte)43, (byte)28);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Background = new SolidColorBrush(Color.FromArgb(ok ? (byte)18 : (byte)23, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(ok ? (byte)44 : (byte)56, accent.R, accent.G, accent.B)),
            Child = new TextBlock
            {
                Text = text,
                FontSize = StatusFontSize,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        };
    }

    private static string Level(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Error => "错误",
        DiagnosticLevel.Warn => "警告",
        _ => "提示",
    };

    /// <summary>主按钮样式（WinUI 的强调色按钮在资源字典里，直接取来用，不自己配色）。</summary>
    private static Style? AccentButtonStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) ? style as Style : null;
}
