using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Tjt.App.Rendering;

/// <summary>
/// 带光标的透明边缘条（挂在窗口四条边上）。
///
/// <para>为什么用 <see cref="Grid"/> 而不是 <c>Border</c>：WinUI 3 里 <c>Border</c> 是
/// <c>sealed</c>，而 <c>UIElement.ProtectedCursor</c> 是 <c>protected</c> —— 必须继承才能赋值。
/// <c>Grid</c> 未密封、能设 <c>Background</c>（透明也可命中）且开销最小。</para>
///
/// <para>用 <see cref="InputSystemCursor"/> 而不是位图光标：跟随系统光标方案与 DPI，
/// 150% 缩放下同样清晰。</para>
///
/// <para>为什么绕这一圈：Win32 侧 <c>SetCursor</c> 会被窗口过程在 <c>WM_SETCURSOR</c> 里
/// 按类光标重置（真机实测"能缩放但看不到缩放光标"）。挂在元素上的光标由渲染层自己管，
/// 不参与那场竞争。</para>
/// </summary>
internal sealed class CursorStrip : Grid
{
    /// <summary>构造一条边缘条：透明背景（保证可命中）+ 指定系统光标。</summary>
    /// <param name="shape">系统光标形状。</param>
    public CursorStrip(InputSystemCursorShape shape)
    {
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
