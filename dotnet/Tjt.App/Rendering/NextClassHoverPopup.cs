using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Tjt.App.Rendering;

/// <summary>摘要与详情共用悬停寿命。只在浮层内滚动，不改变 owner 的布局。</summary>
internal sealed class NextClassHoverPopup
{
    private const double RootInset = 8;
    private const double OwnerGap = 4;
    private const double ContentInsets = 26; // 两侧 padding 12 + border 1。
    private readonly FrameworkElement _owner;
    private readonly Popup _popup;
    private readonly Border _surface;
    private readonly ScrollViewer _scroller;
    private readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private XamlRoot? _openRoot;
    private bool _attached;
    private bool _overOwner;
    private bool _overPopup;

    internal static void Attach(FrameworkElement owner, string details, bool dark,
        Brush foreground, Brush borderBrush) => _ = new NextClassHoverPopup(owner, details, dark, foreground, borderBrush);

    private NextClassHoverPopup(FrameworkElement owner, string details, bool dark,
        Brush foreground, Brush borderBrush)
    {
        _owner = owner;
        _scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            IsTabStop = false,
            Content = new TextBlock
            {
                Text = details,
                FontSize = 13,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        _surface = new Border
        {
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Background = new SolidColorBrush(dark
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 250, 250, 250)),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = _scroller,
        };
        AutomationProperties.SetAutomationId(_surface, "NextClassHoverDetails");
        _popup = new Popup { Child = _surface, IsLightDismissEnabled = false };
        // Loaded/Unloaded 只由 owner 持有，不订阅全局源；旧 Render 树可以整体回收。
        _owner.Loaded += OnLoaded;
        _owner.Unloaded += OnUnloaded;
        if (_owner.IsLoaded) Connect();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Connect();

    private void Connect()
    {
        if (_attached) return;
        _attached = true;
        _owner.PointerEntered += OnOwnerEntered;
        _owner.PointerExited += OnOwnerExited;
        _owner.SizeChanged += OnOwnerSizeChanged;
        _surface.PointerEntered += OnPopupEntered;
        _surface.PointerExited += OnPopupExited;
        _popup.Closed += OnPopupClosed;
        _openTimer.Tick += OnOpenTick;
        _closeTimer.Tick += OnCloseTick;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _attached = false;
        _overOwner = _overPopup = false;
        Close();
        _owner.PointerEntered -= OnOwnerEntered;
        _owner.PointerExited -= OnOwnerExited;
        _owner.SizeChanged -= OnOwnerSizeChanged;
        _surface.PointerEntered -= OnPopupEntered;
        _surface.PointerExited -= OnPopupExited;
        _popup.Closed -= OnPopupClosed;
        // Stop 之外也解绑定 Tick，不能让调度器保留上一棵 Render 树。
        _openTimer.Tick -= OnOpenTick;
        _closeTimer.Tick -= OnCloseTick;
        // WinUI 不允许清空已经绑定的 XamlRoot。关闭并解绑事件即可；这棵 owner 树不再有全局引用。
    }

    private void OnOwnerEntered(object sender, PointerRoutedEventArgs e)
    {
        _overOwner = true;
        _closeTimer.Stop();
        if (!_popup.IsOpen) _openTimer.Start();
    }

    private void OnOwnerExited(object sender, PointerRoutedEventArgs e)
    {
        _overOwner = false;
        _openTimer.Stop();
        ScheduleClose();
    }

    private void OnPopupEntered(object sender, PointerRoutedEventArgs e)
    {
        _overPopup = true;
        _closeTimer.Stop();
    }

    private void OnPopupExited(object sender, PointerRoutedEventArgs e)
    {
        _overPopup = false;
        ScheduleClose();
    }

    private void ScheduleClose()
    {
        if (!_overOwner && !_overPopup && _popup.IsOpen)
        {
            // 允许指针跨过 owner 与浮层之间的空隙；移入任一侧都会取消关闭。
            _closeTimer.Stop();
            _closeTimer.Start();
        }
    }

    private void OnOpenTick(object? sender, object e)
    {
        _openTimer.Stop();
        if (!_attached || !_overOwner || !_owner.IsLoaded || _owner.XamlRoot is not { } root
            || !root.IsHostVisible) return;
        if (_popup.XamlRoot is null) _popup.XamlRoot = root;
        if (!Place(root)) return;
        _scroller.ChangeView(null, 0, null, disableAnimation: true);
        _openRoot = root;
        root.Changed += OnRootChanged;
        _popup.IsOpen = true;
    }

    private void OnCloseTick(object? sender, object e)
    {
        _closeTimer.Stop();
        if (!_overOwner && !_overPopup) Close();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!sender.IsHostVisible || !ReferenceEquals(_owner.XamlRoot, sender) || !Place(sender)) Close();
    }

    private void OnOwnerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_popup.IsOpen && _openRoot is { } root && !Place(root)) Close();
    }

    private bool Place(XamlRoot root)
    {
        var size = root.Size;
        if (size.Width <= RootInset * 2 + ContentInsets || size.Height <= RootInset * 2 + ContentInsets
            || root.Content is not UIElement content) return false;
        var ownerRect = _owner.TransformToVisual(content).TransformBounds(
            new Rect(0, 0, _owner.ActualWidth, _owner.ActualHeight));
        var belowTop = Math.Clamp(ownerRect.Bottom + OwnerGap, RootInset, size.Height - RootInset);
        var aboveBottom = Math.Clamp(ownerRect.Top - OwnerGap, RootInset, size.Height - RootInset);
        var below = size.Height - RootInset - belowTop;
        var above = aboveBottom - RootInset;
        var width = Math.Min(560, size.Width - RootInset * 2);
        // 先按根视口测量完整内容，再挑能放下的一侧，优先放在摘要下方。
        var maxHeight = Math.Min(400, size.Height - RootInset * 2);
        _surface.Width = width;
        _surface.MaxHeight = maxHeight;
        _scroller.MaxHeight = maxHeight - ContentInsets;
        _surface.Measure(new Size(width, maxHeight));
        var useBelow = below >= _surface.DesiredSize.Height || below >= above;
        maxHeight = Math.Min(maxHeight, useBelow ? below : above);
        if (maxHeight <= ContentInsets) return false;
        _surface.MaxHeight = maxHeight;
        _scroller.MaxHeight = maxHeight - ContentInsets;
        _surface.Measure(new Size(width, maxHeight));
        _popup.HorizontalOffset = Math.Clamp(ownerRect.Left, RootInset, size.Width - RootInset - width);
        _popup.VerticalOffset = useBelow ? belowTop : aboveBottom - _surface.DesiredSize.Height;
        return true;
    }

    private void OnPopupClosed(object? sender, object e)
    {
        _overPopup = false;
        _closeTimer.Stop();
        UnhookRoot();
    }

    private void Close()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _overPopup = false;
        _popup.IsOpen = false;
        UnhookRoot();
    }

    private void UnhookRoot()
    {
        if (_openRoot is not { } root) return;
        root.Changed -= OnRootChanged;
        _openRoot = null;
    }
}
