namespace Tjt.Widget;

/// <summary>
/// 边缘光标条 —— **纯计算，零依赖**（与 <see cref="ResizePolicy"/> 同层，可在 Linux 上单测）。
///
/// <para><b>为什么光标要放到 XAML 层而不是 Win32</b>：真机验收连续两轮"能缩放但看不到缩放光标"。
/// 原因链是这样的：窗口过程每次鼠标移动都会在 <c>WM_SETCURSOR</c> 里用**类光标**重置光标，
/// 而渲染层的子窗口又会把这条消息截走（实测 <c>WM_SETCURSOR</c> 到我们窗口过程的次数是 0）——
/// 于是从 Win32 侧设的光标总在别人的重设之后，赢不了。</para>
///
/// <para>改用 WinUI 的 <c>UIElement.ProtectedCursor</c>：光标由渲染层自己挂在元素上，
/// 鼠标进入哪个元素就显示哪个光标，绕开了"谁最后 SetCursor"的竞争。</para>
///
/// <para>这一层只负责算"该在哪些矩形上挂哪种光标"；把矩形摆到 XAML 上由外壳做
/// （与 <see cref="BoardVisual"/> 的分工一致）。</para>
/// </summary>
public static class CursorZones
{
    /// <summary>一条边缘光标条：矩形（DIP，相对窗口左上角）+ 该条的光标朝向。</summary>
    /// <param name="X">左。</param>
    /// <param name="Y">上。</param>
    /// <param name="Width">宽。</param>
    /// <param name="Height">高。</param>
    /// <param name="Grip">这条用的是哪种缩放光标。</param>
    public sealed record Zone(double X, double Y, double Width, double Height, ResizeGrip Grip);

    /// <summary>
    /// 按窗口尺寸算出四条边缘光标条（左/右/上/下）。
    ///
    /// <para>宽度/高度就是 <see cref="ResizePolicy.BorderWidth"/>（DIP）—— 与抓取带同一口径，
    /// 保证"能拖到的范围"与"显示缩放光标的范围"完全一致。</para>
    ///
    /// <para>四个角不单独出条：左条覆盖窗口全高、上条覆盖窗口全宽，它们的**交叠处**
    /// 由 XAML 的 z 序决定谁在上（先加左/右、后加上/下 → 角上是水平拉伸）。
    /// 这不追求与 <see cref="ResizePolicy.HitTest"/> 的角落判定逐像素一致：
    /// 视觉上角部本来就是斜向，差几个像素察觉不到，而拖拽判定仍以策略层为准。</para>
    /// </summary>
    /// <param name="width">窗口宽（DIP）。</param>
    /// <param name="height">窗口高（DIP）。</param>
    /// <param name="band">光标条宽度（DIP）。</param>
    public static IReadOnlyList<Zone> ForWindow(double width, double height, double band = ResizePolicy.BorderWidth)
    {
        if (width <= 0 || height <= 0 || band <= 0) return [];

        // 条不能比窗口还长（极小窗口时夹住，避免负宽高的元素）
        var w = Math.Min(band, width);
        var h = Math.Min(band, height);

        return
        [
            new Zone(0, 0, w, height, ResizeGrip.Left),
            new Zone(width - w, 0, w, height, ResizeGrip.Right),
            new Zone(0, 0, width, h, ResizeGrip.Top),
            new Zone(0, height - h, width, h, ResizeGrip.Bottom),
        ];
    }
}
