using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tjt.Widget;

namespace Tjt.App.Rendering;

/// <summary>
/// 边缘缩放热区条：透明背景（保证可命中）+ 系统缩放光标 + **按下时报告方向**。
///
/// <para>为什么用 <see cref="Grid"/> 而不是 <c>Border</c>：WinUI 3 里 <c>Border</c> 是
/// <c>sealed</c>，而 <c>UIElement.ProtectedCursor</c> 是 <c>protected</c> —— 必须继承才能赋值。</para>
///
/// <para>为什么按下要自己报告方向：四角是**独立**的热区（对齐 DeskBox 的 3×3 网格），
/// 只有元素自己知道"按在角上"，外壳靠坐标反推在角上容易退化成单轴。
/// 报告出去的 <see cref="ResizeGrip"/> 由外壳用作缩放的初始方向。</para>
///
/// <para>用 <see cref="InputSystemCursor"/> 而不是位图光标：跟随系统光标方案与 DPI，
/// 任意缩放下都清晰。光标由框架按元素算，不参与 Win32 <c>SetCursor</c> 那场
/// "谁最后设置"的竞争（真机实测过：从 Win32 侧设总被类光标覆盖）。</para>
/// </summary>
internal sealed class CursorStrip : Grid
{
    /// <summary>构造一条热区。</summary>
    /// <param name="zone">热区矩形与方向。</param>
    /// <param name="report">按下时把方向报给外壳（可为 <c>null</c>，例如设置窗口预览）。</param>
    public CursorStrip(CursorZones.Zone zone, Action<ResizeGrip>? report)
    {
        Width = zone.Width;
        Height = zone.Height;
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(CursorFor(zone.Grip));

        if (report is not null)
        {
            PointerPressed += (_, e) =>
            {
                report(zone.Grip);
                e.Handled = false;   // 仍让事件继续传播（拖动区/按钮不受影响）
            };
        }
    }

    /// <summary>方向 → 系统光标形状。</summary>
    /// <param name="grip">缩放方向。</param>
    private static InputSystemCursorShape CursorFor(ResizeGrip grip) => grip switch
    {
        ResizeGrip.Left or ResizeGrip.Right => InputSystemCursorShape.SizeWestEast,
        ResizeGrip.Top or ResizeGrip.Bottom => InputSystemCursorShape.SizeNorthSouth,
        ResizeGrip.TopLeft or ResizeGrip.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
        _ => InputSystemCursorShape.SizeNortheastSouthwest,
    };
}
