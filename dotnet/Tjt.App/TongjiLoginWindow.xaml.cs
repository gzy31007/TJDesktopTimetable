using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Tjt.App.Data;
using Tjt.App.Rendering;
using Tjt.App.Win32;
using Tjt.Core;
using Tjt.Core.Adapters;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Tjt.App;

/// <summary>内置登录窗口这次服务哪所学校（决定起始页、接口判定与捕获策略）。</summary>
internal enum LoginSchool
{
    /// <summary>同济 1 系统：粘的/拦到的那条课表请求原样解析。</summary>
    Tongji,

    /// <summary>交大「学在交大」：页面按周拉取，我们改用登录态取整学期课表 + 教务日历。</summary>
    Sjtu,
}

/// <summary>
/// 内置登录窗口：在应用自己的 WebView2 里打开学校网站，用户走学校自己的统一身份认证，
/// **课表页那条接口的响应由我们在一旁接住**。
///
/// <para>两所学校共用这一个窗口，差别收在 <see cref="LoginSchool"/> 上：</para>
/// <list type="bullet">
///   <item><b>同济（1 系统）</b>：课表页那条接口要 <c>studentCode</c>（前端加密的 uid，算法在 bundle 里、
///   随发版变）—— 所以只做旁观者，页面自己发请求，我们接住响应。</item>
///   <item><b>交大（学在交大）</b>：登录是标准 jAccount OAuth2，课表接口只需要 <c>year</c>/<c>semester</c>
///   两个明文参数 —— 所以拿到页面自己的 cookie 后，我们**主动**取整学期课表与教务日历
///   （页面默认的按周接口不带周次信息，见 <see cref="SjtuWebCapture"/>）。</item>
/// </list>
///
/// <para><b>为什么用独立 user data folder</b>：会话数据放在
/// <c>%APPDATA%\TJDesktopTimetable\WebView2</c>，与用户自己的 Edge / 其它 WebView2 应用完全隔离；
/// 顺带让"下次打开还在登录态"成为自然结果（cookie 存在这个 profile 里）。两校共用同一个 profile
/// （域名不同，cookie 互不干扰）。</para>
///
/// <para><b>安全</b>：cookie 只在内存里过一遍（拼请求头），**任何日志都不打印它的内容**；
/// 日志只记"撞上了哪条接口、多少字节、学期 id"。</para>
/// </summary>
internal sealed partial class TongjiLoginWindow : Window
{
    /// <summary>响应体的体量上限：课表 JSON 实测 30 KB 级，超过这个数说明撞上了别的东西（不读，省内存）。</summary>
    private const long MaxBodyBytes = 8L * 1024 * 1024;

    private readonly ImportService _imports;
    private readonly Action<bool, string> _finished;
    private readonly string _startUrl;
    private readonly LoginSchool _school;
    private readonly string? _sjtuBaseUrl;

    private WebView2? _view;
    private TextBlock? _status;
    private bool _done;
    private bool _webViewReady;

    /// <summary>交大那条路的"只抓一次"闸门（页面会连发几条接口，别重复导入）。</summary>
    private bool _sjtuCapturing;

    /// <summary>构造登录窗口。</summary>
    /// <param name="imports">导入编排（捕获成功后就落到它手里）。</param>
    /// <param name="dark">当前是否深色主题（只影响这一窗的主题）。</param>
    /// <param name="finished">结束回调（成功 / 关闭都会调一次，<b>只调一次</b>）。</param>
    /// <param name="school">服务哪所学校（决定起始页与捕获策略）。</param>
    /// <param name="startUrl">初始导航目标；<c>null</c> = 该校的默认入口（验收脚本会指向本地合成服务）。</param>
    /// <param name="sjtuBaseUrl">
    /// 覆盖交大接口主机（<c>--sjtu-host</c>，只给验收脚本用）；<c>null</c> = 真 <c>j.sjtu.edu.cn</c>。
    /// </param>
    internal TongjiLoginWindow(
        ImportService imports,
        bool dark,
        Action<bool, string> finished,
        LoginSchool school = LoginSchool.Tongji,
        string? startUrl = null,
        string? sjtuBaseUrl = null)
    {
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _finished = finished ?? throw new ArgumentNullException(nameof(finished));
        _school = school;
        _sjtuBaseUrl = sjtuBaseUrl;
        _startUrl = string.IsNullOrWhiteSpace(startUrl) ? DefaultStartUrl(school) : startUrl.Trim();

        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        // 这一窗是**系统标题栏**（刻意不 ExtendsContentIntoTitleBar），图标不设就是系统默认那个
        WindowIcon.Apply(this);
        if (Content is FrameworkElement root) root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        Host.Children.Add(BuildLayout());
        ResizeForDpi(1180, 820);
        CenterOnScreen();

        Closed += (_, _) => Complete(false, "登录窗口已关闭：这次没有捕获到课表数据。");
        _ = InitializeWebViewAsync();
    }

    /// <summary>用户点「取消」或直接关窗时，把窗口关掉（<see cref="Complete"/> 只认第一次）。</summary>
    private void CloseWindow() => Close();

    /// <summary>该校的默认入口：同济是 1 系统首页，交大直接进课表页（登录后页面自己会拉课表）。</summary>
    private static string DefaultStartUrl(LoginSchool school) => school == LoginSchool.Sjtu
        ? SjtuTerms.SjtuOrigin + "/app/ui/timetable"
        : TongjiWebCapture.TongjiOrigin;

    /* ------------------------------------------------------------------ 界面 */

    private Grid BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var sjtu = _school == LoginSchool.Sjtu;
        var header = new StackPanel { Spacing = 4, Padding = new Thickness(16, 14, 16, 10) };
        header.Children.Add(new TextBlock
        {
            Text = sjtu ? "登录交大「学在交大」" : "登录 1 系统，然后点开「我的课表」",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = sjtu
                ? "下面就是交大自己的登录页面（jAccount）—— 本应用不接触你的密码。\n"
                  + "登录后课表页会自己加载；程序随即用这份登录态取一次整学期课表与教务日历，"
                  + "拿到就自动导入并关窗，没拿到就正常关掉、什么都不会改。"
                : "下面就是学校自己的登录页面（统一身份认证，短信验证也在这里完成）—— 本应用不接触你的密码。\n"
                  + "课表一加载出来，程序会自动抓取并导入，然后这个窗口自己关上；没抓到就正常关掉、什么都不会改。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        _status = new TextBlock
        {
            Text = $"正在打开 {_startUrl} …",
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };
        header.Children.Add(_status);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _view = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetRow(_view, 1);
        root.Children.Add(_view);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(16, 10, 16, 14),
        };

        var reload = new Button { Content = "重新加载页面", MinWidth = 120 };
        reload.Click += (_, _) =>
        {
            SetStatus("正在重新加载…");
            _view?.CoreWebView2?.Reload();
        };

        var cancel = new Button { Content = "取消", MinWidth = 88 };
        cancel.Click += (_, _) => CloseWindow();

        bottom.Children.Add(reload);
        bottom.Children.Add(cancel);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        return root;
    }

    private void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
    }

    /* ------------------------------------------------------------------ WebView2 */

    private async Task InitializeWebViewAsync()
    {
        if (_view is null) return;

        try
        {
            // 会话数据放应用自己的目录：与用户的 Edge / 其它 WebView2 应用隔离
            var dataFolder = Path.Combine(SettingsStore.Directory, "WebView2");
            Directory.CreateDirectory(dataFolder);

            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, dataFolder, null);
            await _view.EnsureCoreWebView2Async(environment);

            var core = _view.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            // 不保存密码 / 不自动填充：登录态只以 cookie 形式留在本应用自己的 profile 里
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            // 登录流程里若有 window.open（部分 SSO / 短信页会这么干），就在同一个窗口里继续：
            // 默认行为是弹一个新窗，用户会觉得"点了没反应"（新窗可能被挡在后台）
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                core.Navigate(args.Uri);
            };

            // 页面自己 window.close() 时把窗口收掉，别留一个空壳
            core.WindowCloseRequested += (_, _) => CloseWindow();

            core.WebResourceResponseReceived += OnResponseReceived;
            core.NavigationCompleted += (_, args) =>
            {
                // 导航结果进日志：真机上"窗口一片白"时，这是唯一能分辨"网络不通 / 页面报错 / 登录成功"的线索
                AppLog.Line($"[login] 导航完成 ok={args.IsSuccess} status={args.WebErrorStatus}");
                if (_done) return;
                SetStatus(args.IsSuccess
                    ? "页面已加载。登录后点开「我的课表」——程序会自动接住那条课表请求。"
                    : $"页面加载失败：{args.WebErrorStatus}（可点「重新加载页面」再试）");
            };

            _webViewReady = true;
            AppLog.Line($"[login] WebView2 已就绪（profile={dataFolder}），开始导航 {_startUrl}");
            core.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[login] WebView2 初始化失败：{ex}");
            SetStatus($"内置浏览器初始化失败：{ex.Message}（需要 WebView2 运行时可再用此功能）");
        }
    }

    /// <summary>
    /// 页面每收到一个响应都会到这里。只看目标学校课表相关的那几条 —— 认出来就把响应体读走。
    /// </summary>
    private async void OnResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        try
        {
            if (_done) return;

            var url = args.Request.Uri;

            // 交大：页面拉到课表（或日历）说明登录态已就绪 —— 不读它的响应体，
            // 而是用同一份 cookie 去取"整学期课表 + 教务日历"（按周响应没有周次信息）
            if (_school == LoginSchool.Sjtu)
            {
                if (!SjtuWebCapture.IsTimetableEndpoint(url) && !SjtuWebCapture.IsCalendarEndpoint(url)) return;

                AppLog.Line($"[login] 撞上交大接口：{SjtuWebCapture.EndpointLabel(url)}（HTTP {args.Response.StatusCode}）");
                SetStatus("登录成功，正在用这份登录态取整学期课表…");
                await CaptureSjtuAsync(url);
                return;
            }

            if (!TongjiWebCapture.IsEndpoint(url)) return;

            AppLog.Line($"[login] 撞上课表接口：{TongjiWebCapture.EndpointLabel(url)}（HTTP {args.Response.StatusCode}）");
            SetStatus("抓到课表接口了，正在读取数据…");

            // 只有 GET 的响应体一定读得到；POST（旧 getDataBk）读不到就走下面的"cookie 重发"
            var body = args.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                ? await TryReadBodyAsync(args)
                : null;

            var verdict = TongjiWebCapture.Inspect(url, body);
            if (verdict.Ok && body is not null)
            {
                Succeed(url, body, verdict.TermId);
                return;
            }

            if (body is null && args.Response.StatusCode == 200)
            {
                if (await TryResendAsync(url)) return;
                return;
            }

            SetStatus($"这条响应还不能用：{verdict.Note}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"[login] 处理捕获到的响应时异常：{ex}");
        }
    }

    /// <summary>读响应体；读不到（POST / 已释放 / 太大）返回 <c>null</c>，由调用方决定兜底。</summary>
    private static async Task<string?> TryReadBodyAsync(CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        try
        {
            using var stream = await args.Response.GetContentAsync();
            if (stream is null) return null;

            var size = (long)stream.Size;
            if (size <= 0 || size > MaxBodyBytes) return null;

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size);
            return reader.ReadString((uint)size);
        }
        catch (Exception ex)
        {
            // 已知：POST 响应取不到内容；也有"内容已被释放"的情况。都不算错，交给重发路径。
            AppLog.Line($"[login] 响应体读不到（{ex.GetType().Name}），改用 cookie 重发一次");
            return null;
        }
    }

    /// <summary>
    /// 兜底：用页面自己的 cookie 把同一个 GET 重发一次。
    ///
    /// <para>触发场景实测有两类：① 旧 <c>getDataBk</c> 是 POST，响应体读不出来；
    /// ② 页面用的是 XHR/fetch，偶发"内容已被释放"。重发走的是同一条
    /// <see cref="TongjiFetcher.FetchSpecAsync"/>，解析与提示不会分叉。</para>
    /// </summary>
    private async Task<bool> TryResendAsync(string url)
    {
        if (_view?.CoreWebView2 is not { } core) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        var cookies = await core.CookieManager.GetCookiesAsync(url);
        var header = TongjiWebCapture.CookieHeader(
            url,
            cookies.Select(cookie => new WebCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path)));

        if (header.Length == 0)
        {
            SetStatus("没读到可用的登录 cookie（登录态可能还没生效）：请先完成登录，再点开「我的课表」。");
            return false;
        }

        // 只记条数，不记内容
        AppLog.Line($"[login] 响应体不可读，用 {cookies.Count} 条 cookie 重发一次：{TongjiWebCapture.EndpointLabel(url)}");
        SetStatus("响应体读不到，正在用登录态重发一次请求…");

        var spec = new HttpRequestSpec(
            url,
            "GET",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["cookie"] = header },
            null,
            "内置登录窗口");

        var outcome = await TongjiFetcher.FetchSpecAsync(spec);
        if (!outcome.Ok || outcome.TimetableText is null)
        {
            SetStatus(outcome.Message);
            return false;
        }

        Succeed(url, outcome.TimetableText, outcome.TermId);
        return true;
    }

    /// <summary>
    /// 交大：用页面自己的 cookie **主动**取一次整学期课表 + 教务日历，然后导入。
    ///
    /// <para>为什么要主动取而不是像同济那样接住页面的响应：交大课表页按周拉取，
    /// 那条响应里的 <c>time</c> 是 <c>null</c>（没有"上哪些周"），照它建挂件只会剩本周有课。
    /// 整学期接口 <c>listBySemester</c> 只要 <c>year</c>/<c>semester</c> 两个明文参数，
    /// 不存在同济那种"前端加密 uid"的障碍。三条请求与解析全在 <see cref="SjtuFetcher"/>。</para>
    ///
    /// <para>页面一次加载会连发好几条接口，用 <see cref="_sjtuCapturing"/> 保证只抓一次。</para>
    /// </summary>
    private async Task CaptureSjtuAsync(string triggerUrl)
    {
        if (_sjtuCapturing || _done) return;
        if (_view?.CoreWebView2 is not { } core) return;

        _sjtuCapturing = true;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(triggerUrl);
            var header = TongjiWebCapture.CookieHeader(
                triggerUrl,
                cookies.Select(cookie => new WebCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path)));

            if (header.Length == 0)
            {
                SetStatus("还没读到交大的登录 cookie：请先在这个窗口里完成 jAccount 登录，再打开课表页。");
                _sjtuCapturing = false;
                return;
            }

            // 只记条数，不记内容
            AppLog.Line($"[login] 交大：用 {cookies.Count} 条 cookie 取整学期课表（触发自 {SjtuWebCapture.EndpointLabel(triggerUrl)}）");
            SetStatus("正在取整学期课表与教务日历…");

            var spec = new HttpRequestSpec(
                triggerUrl,
                "GET",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["cookie"] = header },
                null,
                "内置登录窗口");

            var fetched = await SjtuFetcher.FetchAsync(spec, default, _sjtuBaseUrl);
            if (!fetched.Ok || fetched.TimetableText is null)
            {
                SetStatus(fetched.Message);
                _sjtuCapturing = false;
                return;
            }

            SucceedSjtu(triggerUrl, fetched);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[login] 交大抓取异常：{ex}");
            _sjtuCapturing = false;
        }
    }

    /// <summary>交大捕获成功：把课表（外加教务日历）交给导入编排，然后关窗。</summary>
    private void SucceedSjtu(string triggerUrl, TongjiFetchOutcome fetched)
    {
        var probes = new List<FetchProbe>(fetched.Probes)
        {
            new("来源", "内置登录窗口"),
            new("接口", SjtuWebCapture.EndpointLabel(triggerUrl)),
        };

        var applied = _imports.ApplyCapturedResponse(
            fetched.TimetableText!,
            fetched.TermId,
            probes,
            fetched.AdapterId ?? SjtuStudentAdapter.AdapterId,
            fetched.Files);

        AppLog.Line($"[login] 交大捕获：{SjtuWebCapture.Describe(triggerUrl, fetched.TimetableText!.Length)} applied={applied.Ok}");

        SetStatus(applied.Message);
        Complete(applied.Ok, applied.Message);
        if (applied.Ok) Close();
    }

    /// <summary>捕获成功：交给导入编排落盘 + 重画，然后关窗。</summary>
    private void Succeed(string url, string body, string? termId)
    {
        var probes = new List<FetchProbe>
        {
            new("来源", "内置登录窗口"),
            new("接口", TongjiWebCapture.EndpointLabel(url)),
            new("响应大小", $"{body.Length} 字节"),
        };
        if (!string.IsNullOrEmpty(termId)) probes.Add(new FetchProbe("学期", termId));

        var outcome = _imports.ApplyCapturedResponse(body, termId, probes);
        AppLog.Line($"[login] 捕获成功：{TongjiWebCapture.Describe(url, body.Length)} applied={outcome.Ok}");

        SetStatus(outcome.Message);
        Complete(outcome.Ok, outcome.Message);
        if (outcome.Ok) Close();
    }

    /// <summary>结束回调只发一次（成功、取消、直接关窗三条路都会到这里）。</summary>
    private void Complete(bool ok, string message)
    {
        if (_done) return;
        _done = true;
        try
        {
            _finished(ok, message);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[login] 结束回调里异常：{ex}");
        }
    }

    /* ------------------------------------------------------------------ 窗口尺寸 */

    /// <summary>按 DIP 意图设客户区（<c>ResizeClient</c> 收的是物理像素，150% 缩放下会缩水一圈）。</summary>
    private void ResizeForDpi(int widthDip, int heightDip)
    {
        var handle = WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / NativeMethods.DefaultDpi : 1.0;
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is not null)
        {
            width = Math.Min(width, area.WorkArea.Width - 40);
            height = Math.Min(height, area.WorkArea.Height - 40);
        }

        AppWindow.ResizeClient(new SizeInt32(width, height));
    }

    /// <summary>把窗口摆到工作区中间。</summary>
    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null) return;
        var work = area.WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.X + ((work.Width - size.Width) / 2),
            work.Y + ((work.Height - size.Height) / 2)));
    }

    /// <summary>WebView2 是否已经起来（自检脚本读它）。</summary>
    internal bool WebViewReady => _webViewReady;
}
