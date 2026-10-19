using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tjt.App.Data;
using Tjt.App.Win32;
using Tjt.Core;
using WinRT.Interop;

namespace Tjt.App;

public partial class App
{
    private string? _sessionLoginStartUrl;
    private (bool Ok, string Message)? _sessionLoginResult;

    // Diagnostic-only driver. Uses the mounted settings page and real button event handlers,
    // never an alternative import/apply implementation. All observations are synthetic data.
    private async void StartSessionCheck(MainWindow widget, string planPath)
    {
        using var watchdog = new Timer(_ => Environment.Exit(2), null, TimeSpan.FromMinutes(4), Timeout.InfiniteTimeSpan);
        try
        {
            var steps = JsonSerializer.Deserialize<SessionStep[]>(File.ReadAllText(planPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Missing session steps");
            // Manual verification is opt-in and remains under the paired diagnostic directory.
            // Give the human time to change display settings without relaxing automatic runs.
            if (steps.Any(step => step.Action == "board-manual"))
                watchdog.Change(TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan);
            // Keep board diagnostics behind the existing paired, isolated startup path.
            if (Startup.SessionCheckPath is null || Startup.DiagnosticDirectory is null || !SettingsStore.IsDiagnostic)
                throw new InvalidOperationException("Session checks require paired diagnostic isolation");
            RequireLoopback(Startup.SjtuHost ?? throw new InvalidDataException("Missing loopback host"));
            if (steps.Any(step => step.Action == "board-view") && Startup.WeekView is not null)
                throw new InvalidDataException("board-view cannot run with a CLI week-view override");
            if (steps.Any(step => step.Action == "board-theme") && Startup.Dark is not null)
                throw new InvalidDataException("board-theme cannot run with a CLI dark/light override");
            await Task.Delay(700); // Let both real windows mount before any action or observation.
            await WaitForSessionBoard(widget);
            var observations = new List<object>();
            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i];
                // 更新动作有自己的观察路径（会切到「关于」页，见 RunUpdateSessionStep）：必须在下面
                // 按导入页控件名取值之前分叉 —— 否则 Find ImportResultStatus 会去找一页不存在的控件。
                if (step.Action is { } action && action.StartsWith("update-", StringComparison.Ordinal))
                {
                    var updateObservation = await RunUpdateSessionStep(widget, _settingsWindows.Single(), step, i);
                    observations.Add(updateObservation);
                    File.WriteAllText(Path.Combine(SettingsStore.Directory, $"step-{i}.json"),
                        JsonSerializer.Serialize(updateObservation));
                    AppLog.Line($"[session-check] step={i} action={action} observed (update)");
                    continue;
                }
                var settings = _settingsWindows.Single();
                FileStream? fileLock = null;
                object? boardAction = null;
                try
                {
                    if (step.Lock is not null)
                    {
                        var target = step.Lock switch
                        {
                            "temp" => TimetableStore.FilePath + ".tmp",
                            "replace" => TimetableStore.FilePath,
                            _ => throw new InvalidDataException("Unknown lock kind"),
                        };
                        Directory.CreateDirectory(SettingsStore.Directory);
                        // FileShare.Read allows observing the old bytes, but denies write/delete.
                        fileLock = new FileStream(target, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    }
                    switch (step.Action)
                    {
                        case "import":
                            Find<TextBox>(settings.Content, "ImportJson").Text = File.ReadAllText(step.Input!);
                            Invoke(Find<Button>(settings.Content, "ImportApply"));
                            await Until(() => Text(Find<FrameworkElement>(settings.Content, "ImportResultStatus")).Count > 0);
                            break;
                        case "fetch":
                            var request = File.ReadAllText(step.Input!);
                            var spec = HttpRequestParser.Parse(request) ?? throw new InvalidDataException("Invalid synthetic request");
                            RequireLoopback(spec.Url);
                            Find<TextBox>(settings.Content, "ImportRequest").Text = request;
                            var fetch = Find<Button>(settings.Content, "ImportFetch");
                            Invoke(fetch);
                            await Until(() => fetch.IsEnabled
                                && Text(Find<FrameworkElement>(settings.Content, "ImportFetchStatus")).Count > 0);
                            break;
                        case "reload":
                            Invoke(Find<Button>(settings.Content, "ImportReload"));
                            await Until(() => Text(Find<FrameworkElement>(settings.Content, "ImportDataStatus")).Count > 0);
                            break;
                        case "login":
                            RequireLoopback(step.Url!);
                            _sessionLoginStartUrl = step.Url;
                            _sessionLoginResult = null;
                            Find<ComboBox>(settings.Content, "ImportSchool").SelectedIndex = step.School == "sjtu" ? 1 : 0;
                            Invoke(Find<Button>(settings.Content, "ImportLogin"));
                            await Until(() => step.UntilText is { } until
                                ? _loginWindow is { } open && Text(open.Content).Any(text => text.Contains(until, StringComparison.Ordinal))
                                : _sessionLoginResult is not null, 60_000);
                            break;
                        case "close-login":
                            (_loginWindow ?? throw new InvalidOperationException("No failed login window to close")).Close();
                            await Until(() => _loginWindow is null);
                            break;
                        case "rebuild-import":
                            settings.SelectPage(SettingsWindow.PageGeneral);
                            settings.SelectPage(SettingsWindow.PageImport);
                            await Task.Delay(300);
                            break;
                        case "board-resize":
                            boardAction = await ResizeSessionBoard(widget, step);
                            break;
                        case "board-scroll":
                            if (step.VerticalOffset is null && step.HorizontalOffset is null)
                                throw new InvalidDataException("Missing scroll offset");
                            foreach (var offset in new[] { step.VerticalOffset, step.HorizontalOffset })
                                if (offset is { } value && (!double.IsFinite(value) || value < 0))
                                    throw new InvalidDataException("Scroll offset must be a nonnegative absolute DIP offset");
                            var scroller = BoardScroller(widget);
                            boardAction = new { RequestedVerticalOffset = step.VerticalOffset,
                                RequestedHorizontalOffset = step.HorizontalOffset,
                                Accepted = scroller.ChangeView(step.HorizontalOffset, step.VerticalOffset, null, disableAnimation: true) };
                            break;
                        case "board-view":
                            var view = step.View?.ToLowerInvariant() switch
                            {
                                "all" => WeekView.All,
                                "current" => WeekView.Current,
                                "odd" => WeekView.Odd,
                                "even" => WeekView.Even,
                                _ => throw new InvalidDataException("Unknown board view"),
                            };
                            (widget.BuildActions().SetWeekView
                                ?? throw new InvalidOperationException("Week view action unavailable"))(view);
                            break;
                        case "board-theme":
                            var theme = step.Theme?.ToLowerInvariant() switch
                            {
                                "auto" => ThemeMode.Auto,
                                "light" => ThemeMode.Light,
                                "dark" => ThemeMode.Dark,
                                _ => throw new InvalidDataException("Unknown board theme"),
                            };
                            widget.ApplySettings(widget.CurrentSettings with { Theme = theme });
                            break;
                        case "board-refresh":
                            boardAction = new { Reloaded = widget.ReloadTimetable() };
                            break;
                        case "board-weekend":
                            (widget.BuildActions().ToggleShowWeekend
                                ?? throw new InvalidOperationException("Weekend action unavailable"))();
                            break;
                        case "board-workarea-check":
                            // Exercise the ordinary notification path against the real, unchanged work area.
                            // This is not evidence that a monitor was removed or its DPI changed.
                            boardAction = new { Posted = NativeMethods.PostMessage(WindowNative.GetWindowHandle(widget),
                                NativeMethods.Constants.WmDisplayChange, nint.Zero, nint.Zero) };
                            break;
                        case "board-double-render":
                            var before = BoardScroller(widget);
                            boardAction = new { BeforeVerticalOffset = before.VerticalOffset,
                                BeforeHorizontalOffset = before.HorizontalOffset, BeforeLoaded = before.IsLoaded };
                            // Intentionally no await: replace the scroller twice before its Loaded event.
                            widget.ShowWidget();
                            widget.ShowWidget();
                            break;
                        case "board-manual":
                            settings.AppWindow.Hide();
                            AppLog.Line($"[session-check] manual wait step={i} dpi={SessionDpiScale(widget) * 96:0}");
                            // The controller writes this marker only after the human confirms completion.
                            // Never accept a marker path from the plan or outside the isolated directory.
                            await Until(() => File.Exists(Path.Combine(SettingsStore.Directory,
                                $"manual-continue-{i}.txt")), 1_800_000);
                            break;
                        case "snapshot": break;
                        default: throw new InvalidDataException("Unknown session action");
                    }
                    await Task.Delay(300);
                    await WaitForSessionBoard(widget);
                }
                finally { fileLock?.Dispose(); }

                // During manual verification the human may close/rebuild the settings window.
                // Observe only the live board here; old settings controls may already be disposed.
                if (step.Action == "board-manual")
                {
                    var manualObservation = new { Index = i, step.Action, Board = ObserveSessionBoard(widget) };
                    observations.Add(manualObservation);
                    File.WriteAllText(Path.Combine(SettingsStore.Directory, $"step-{i}.json"),
                        JsonSerializer.Serialize(manualObservation));
                    AppLog.Line($"[session-check] step={i} action={step.Action} observed");
                    if (i + 1 < steps.Length)
                    {
                        ShowSettings(widget.CurrentSettings, widget.CurrentIsDark, SettingsWindow.PageImport);
                        await Task.Delay(300);
                    }
                    continue;
                }
                // These are the currently attached XAML trees and loaded instance, not recomputed models.
                var observation = new
                {
                    Index = i, step.Action, step.Lock,
                    Requested = step,
                    BoardAction = boardAction,
                    Board = ObserveSessionBoard(widget),
                    Loaded = widget.LoadedTimetableJson,
                    Persisted = File.Exists(TimetableStore.FilePath) ? File.ReadAllText(TimetableStore.FilePath) : null,
                    PersistedSha256 = File.Exists(TimetableStore.FilePath)
                        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(TimetableStore.FilePath))) : null,
                    Temporary = File.Exists(TimetableStore.FilePath + ".tmp") ? File.ReadAllText(TimetableStore.FilePath + ".tmp") : null,
                    View = Text(widget.Content),
                    ViewElements = Descendants(widget.Content).OfType<TextBlock>()
                        .Where(element => element.IsLoaded && element.Visibility == Visibility.Visible && element.Text.Length > 0)
                        .Select(element =>
                        {
                            var point = element.TransformToVisual(widget.Content).TransformPoint(new Windows.Foundation.Point(0, 0));
                            return new { element.Text, point.X, point.Y, Width = element.ActualWidth, Height = element.ActualHeight };
                        }).ToList(),
                    ImportStatus = Text(Find<FrameworkElement>(settings.Content, "ImportResultStatus")),
                    FetchStatus = Text(Find<FrameworkElement>(settings.Content, "ImportFetchStatus")),
                    ReloadStatus = Text(Find<FrameworkElement>(settings.Content, "ImportDataStatus")),
                    Summary = Find<TextBlock>(settings.Content, "ImportSummary").Text,
                    LoginOk = _sessionLoginResult?.Ok,
                    LoginMessage = _sessionLoginResult?.Message,
                    LoginView = _loginWindow is { } login ? Text(login.Content) : [],
                    LoginOpen = _loginWindow is not null,
                };
                observations.Add(observation);
                File.WriteAllText(Path.Combine(SettingsStore.Directory, $"step-{i}.json"), JsonSerializer.Serialize(observation));
                AppLog.Line($"[session-check] step={i} action={step.Action} lock={step.Lock ?? "none"} observed");
            }
            File.WriteAllText(Path.Combine(SettingsStore.Directory, "session.json"), JsonSerializer.Serialize(observations));
            AppLog.Line("[session-check] completed");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            // Do not dump exception messages containing request text or query parameters.
            AppLog.Line($"[session-check] failed type={ex.GetType().Name}");
            Environment.Exit(1);
        }
    }

    // Width/Height/scroll offsets are client-area DIP, not outer-window pixels.
    // A null dimension preserves that physical dimension. ThresholdDelta uses the mounted
    // extent and fixed client chrome at the requested width; no model/layout recomputation.
    private sealed record SessionStep(string Action, string? Input = null, string? Lock = null,
        string? Url = null, string? School = null, string? UntilText = null,
        double? Width = null, double? Height = null, double? VerticalOffset = null,
        string? View = null, string? Theme = null, int? ThresholdDelta = null, double? HorizontalOffset = null);

    private static ScrollViewer BoardScroller(MainWindow widget) => Descendants(widget.Content)
        .OfType<ScrollViewer>().Single(element => AutomationProperties.GetAutomationId(element) == "BoardScrollViewer");

    private static async Task WaitForSessionBoard(MainWindow widget)
    {
        await Until(() => Descendants(widget.Content).OfType<ScrollViewer>()
            .SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == "BoardScrollViewer")
                is { IsLoaded: true, ViewportWidth: > 0, ViewportHeight: > 0, Content: Canvas { IsLoaded: true } });
        ((FrameworkElement)widget.Content).UpdateLayout();
    }

    private static double SessionDpiScale(MainWindow widget)
    {
        var dpi = NativeMethods.GetDpiForWindow(WindowNative.GetWindowHandle(widget));
        return dpi > 0 ? dpi / 96.0 : throw new InvalidOperationException("Window DPI unavailable");
    }

    private static void ResizeSessionClient(MainWindow widget, double? width, double? height)
    {
        static int Pixels(double dip, double scale)
        {
            if (!double.IsFinite(dip) || dip <= 0 || Math.Ceiling(dip * scale) > int.MaxValue)
                throw new InvalidDataException("Client dimensions must be finite positive DIP");
            return checked((int)Math.Ceiling(dip * scale));
        }
        var scale = SessionDpiScale(widget);
        var client = widget.AppWindow.ClientSize;
        widget.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
            width is { } w ? Pixels(w, scale) : client.Width,
            height is { } h ? Pixels(h, scale) : client.Height));
    }

    private static async Task<object> ResizeSessionBoard(MainWindow widget, SessionStep step)
    {
        if (step.ThresholdDelta is not { } delta)
        {
            if (step.Width is null && step.Height is null)
                throw new InvalidDataException("board-resize requires a dimension or ThresholdDelta");
            ResizeSessionClient(widget, step.Width, step.Height);
            return new { step.Width, step.Height };
        }
        if (delta is < -1 or > 1 || step.Height is not null)
            throw new InvalidDataException("ThresholdDelta must be -1, 0 or 1 and cannot be combined with Height");
        // Use 1080/1280 DIP in the plan so a horizontal bar cannot contaminate the fixed height.
        if (step.Width is not null)
        {
            ResizeSessionClient(widget, step.Width, null);
            await Task.Delay(300);
        }
        await WaitForSessionBoard(widget);
        var scroller = BoardScroller(widget);
        if (scroller.ScrollableWidth > 0 || scroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
            throw new InvalidOperationException("Threshold measurement requires a wider client without horizontal scrolling");
        var scale = SessionDpiScale(widget);
        var clientHeight = widget.AppWindow.ClientSize.Height / scale;
        var fixedHeight = clientHeight - scroller.ActualHeight;
        var canvas = (Canvas)scroller.Content;
        // Disabled scrolling may constrain ExtentHeight to the viewport. Actual canvas geometry
        // remains independent of that switch, so derive the threshold from it instead.
        var requiredHeight = canvas.ActualHeight + fixedHeight;
        var requestedHeight = requiredHeight + delta;
        var measurement = new
        {
            ThresholdDelta = delta, MeasuredDpiScale = scale,
            MeasuredClientHeight = clientHeight, MeasuredViewportHeight = scroller.ViewportHeight,
            MeasuredExtentHeight = scroller.ExtentHeight, MeasuredCanvasHeight = canvas.Height,
            FixedHeight = fixedHeight, RequiredClientHeight = requiredHeight,
            RequestedClientHeight = requestedHeight,
        };
        ResizeSessionClient(widget, step.Width, requestedHeight);
        return measurement;
    }

    private static object ObserveSessionBoard(MainWindow widget)
    {
        var scale = SessionDpiScale(widget);
        var hwnd = WindowNative.GetWindowHandle(widget);
        if (!NativeMethods.GetWindowRect(hwnd, out var outer))
            throw new InvalidOperationException("Window rectangle unavailable");
        var client = widget.AppWindow.ClientSize;
        var scroller = BoardScroller(widget);
        var canvas = scroller.Content as Canvas ?? throw new InvalidOperationException("Board canvas unavailable");
        if (!scroller.IsLoaded || !canvas.IsLoaded)
            throw new InvalidOperationException("Board tree is not mounted");
        var viewportPoint = scroller.TransformToVisual(widget.Content).TransformPoint(new Windows.Foundation.Point(0, 0));
        var body = VisualTreeHelper.GetParent(scroller) as Grid;
        var weekdayHeader = Descendants(widget.Content).OfType<FrameworkElement>()
            .SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == "BoardWeekdayHeader");
        return new
        {
            DpiScale = scale, Dpi = NativeMethods.GetDpiForWindow(hwnd),
            Root = ObserveSessionElement((FrameworkElement)widget.Content, widget.Content),
            XamlSize = ((FrameworkElement)widget.Content).XamlRoot.Size,
            ClientPx = new { client.Width, client.Height },
            ClientDip = new { Width = client.Width / scale, Height = client.Height / scale },
            OuterPx = new { outer.Left, outer.Top, outer.Right, outer.Bottom,
                Width = outer.Right - outer.Left, Height = outer.Bottom - outer.Top },
            WeekView = MainWindow.WeekViewName(widget.WeekViewEnabled),
            Theme = widget.CurrentSettings.Theme.ToString(), Dark = widget.CurrentIsDark,
            FixedHeight = client.Height / scale - scroller.ViewportHeight,
            RequiredClientHeight = canvas.ActualHeight + client.Height / scale - scroller.ActualHeight,
            FixedRows = body?.RowDefinitions.Take(Grid.GetRow(scroller))
                .Select((row, index) => new { Index = index, row.ActualHeight }).ToList(),
            Scroll = new
            {
                AutomationId = AutomationProperties.GetAutomationId(scroller),
                Geometry = ObserveSessionElement(scroller, widget.Content),
                scroller.ViewportWidth, scroller.ViewportHeight, scroller.ExtentWidth, scroller.ExtentHeight,
                scroller.ScrollableWidth, scroller.ScrollableHeight, scroller.HorizontalOffset, scroller.VerticalOffset,
                HorizontalScrollBarVisibility = scroller.HorizontalScrollBarVisibility.ToString(),
                VerticalScrollBarVisibility = scroller.VerticalScrollBarVisibility.ToString(),
                ComputedHorizontalScrollBarVisibility = scroller.ComputedHorizontalScrollBarVisibility.ToString(),
                ComputedVerticalScrollBarVisibility = scroller.ComputedVerticalScrollBarVisibility.ToString(),
                HorizontalScrollMode = scroller.HorizontalScrollMode.ToString(),
                VerticalScrollMode = scroller.VerticalScrollMode.ToString(),
            },
            WeekdayHeader = weekdayHeader is null ? null : new
            {
                Geometry = ObserveSessionElement(weekdayHeader, widget.Content),
                Strip = Descendants(weekdayHeader).OfType<Canvas>()
                    .Select(element => ObserveSessionElement(element, widget.Content)).Single(),
                TextElements = Descendants(weekdayHeader).OfType<TextBlock>()
                    .Select(element => ObserveSessionText(element, widget.Content, viewportPoint, scroller)).ToList(),
            },
            Canvas = ObserveSessionElement(canvas, widget.Content),
            TextElements = Descendants(widget.Content).OfType<TextBlock>()
                .Select(element => ObserveSessionText(element, widget.Content, viewportPoint, scroller)).ToList(),
            // Identify actual rendered three-line cards structurally; no private layout model access.
            Blocks = canvas.Children.OfType<Border>()
                .Where(card => card.Child is StackPanel stack && stack.Children.OfType<TextBlock>().Count() == 3)
                .Select(card => new
                {
                    Geometry = ObserveSessionElement(card, widget.Content),
                    Padding = new { card.Padding.Left, card.Padding.Top, card.Padding.Right, card.Padding.Bottom },
                    Stack = ObserveSessionElement((StackPanel)card.Child, widget.Content),
                    Rows = ((StackPanel)card.Child).Children.OfType<TextBlock>().Select((text, index) => new
                    {
                        Role = index switch { 0 => "name", 1 => "room", _ => "weeks" },
                        Observation = ObserveSessionText(text, widget.Content, viewportPoint, scroller),
                    }).ToList(),
                }).ToList(),
        };
    }

    private static object ObserveSessionElement(FrameworkElement element, UIElement root)
    {
        var point = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new
        {
            Type = element.GetType().Name, element.Name, element.IsLoaded,
            Visibility = element.Visibility.ToString(), AncestorsVisible = SessionAncestorsVisible(element),
            element.Opacity, point.X, point.Y,
            Width = double.IsFinite(element.Width) ? (double?)element.Width : null,
            Height = double.IsFinite(element.Height) ? (double?)element.Height : null,
            element.ActualWidth, element.ActualHeight,
            Clip = element.Clip is RectangleGeometry clip ? new
            {
                clip.Rect.X, clip.Rect.Y, clip.Rect.Width, clip.Rect.Height,
            } : null,
            DesiredSize = new { element.DesiredSize.Width, element.DesiredSize.Height },
        };
    }

    private static object ObserveSessionText(TextBlock element, UIElement root,
        Windows.Foundation.Point viewportPoint, ScrollViewer scroller)
    {
        var point = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new
        {
            element.Text, Blank = string.IsNullOrWhiteSpace(element.Text), element.FontSize,
            element.MaxLines, TextTrimming = element.TextTrimming.ToString(),
            Geometry = ObserveSessionElement(element, root),
            // Intersection is not a claim of full visibility; actual bounds let the reader check clipping.
            IntersectsBoardViewport = point.X < viewportPoint.X + scroller.ViewportWidth
                && point.X + element.ActualWidth > viewportPoint.X
                && point.Y < viewportPoint.Y + scroller.ViewportHeight
                && point.Y + element.ActualHeight > viewportPoint.Y,
        };
    }

    private static bool SessionAncestorsVisible(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity <= 0)) return false;
        return true;
    }

    private static void RequireLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidDataException("Session checks only access loopback HTTP services");
    }

    private static void Invoke(Button button)
    {
        if (!button.IsLoaded || !button.IsEnabled) throw new InvalidOperationException("Button not available");
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task Until(Func<bool> predicate, int timeoutMs = 30_000)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        // Yield once: AutomationPeer posts the click rather than running the handler inline.
        await Task.Delay(100);
        while (!predicate())
        {
            if (started.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Session action did not complete");
            await Task.Delay(50);
        }
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(element => element.Name == name);

    private static List<string> Text(DependencyObject root) => Descendants(root).OfType<TextBlock>()
        .Where(element => element.IsLoaded && element.Visibility == Visibility.Visible && element.Text.Length > 0)
        .Select(element => element.Text).ToList();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
