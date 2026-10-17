using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tjt.App.Data;
using Tjt.Core;

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
            if (Startup.SjtuHost is { } host) RequireLoopback(host);
            await Task.Delay(700); // Let both real windows mount before any action or observation.
            var observations = new List<object>();
            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i];
                var settings = _settingsWindows.Single();
                FileStream? fileLock = null;
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
                        case "snapshot": break;
                        default: throw new InvalidDataException("Unknown session action");
                    }
                    await Task.Delay(300);
                }
                finally { fileLock?.Dispose(); }

                // These are the currently attached XAML trees and loaded instance, not recomputed models.
                var observation = new
                {
                    Index = i, step.Action, step.Lock,
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

    private sealed record SessionStep(string Action, string? Input = null, string? Lock = null,
        string? Url = null, string? School = null, string? UntilText = null);

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
