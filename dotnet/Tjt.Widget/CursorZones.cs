namespace Tjt.Widget;

/// <summary>
/// 边缘缩放热区 —— **纯计算，零依赖**（与 <see cref="ResizePolicy"/> 同层，可在 Linux 上单测）。
///
/// <para><b>为什么要有独立的四角</b>：只有"左右条 + 上下条"时，四角被其中一条盖住，
/// 只能单方向缩放 —— 真机验收就是"没有边角的缩放"。角必须是自己的一块区域，
/// 才能同时改宽和高。</para>
///
/// <para><b>尺寸口径对齐 DeskBox</b>（`.refs/DeskBox/Views/ContentWidgetWindow.xaml` 的 3×3 网格）：
/// 左右带 8px、上下带 8px、四角 8×8，但**顶部只留 4px** —— 那 4px 是给标题栏拖动区让位的，
/// 免得"贴着最上沿按下去"变成缩放而不是拖窗口（我们上一版正因为顶带 6px 而抢了拖动）。</para>
///
/// <para>这一层只算热区与朝向；把热区摆到 XAML 上、挂光标、报方向由外壳做
/// （与 <see cref="BoardVisual"/> 的分工一致）。</para>
/// </summary>
public static class CursorZones
{
    /// <summary>左右与下方热区宽度（DIP），对齐 DeskBox 的 8。</summary>
    public const double Band = 8;

    /// <summary>上方热区高度（DIP）：只留 4，其余让给顶部条的拖动区。</summary>
    public const double TopBand = 4;

    /// <summary>一条热区：矩形（DIP，相对窗口左上角）+ 它对应的缩放方向。</summary>
    /// <param name="X">左。</param>
    /// <param name="Y">上。</param>
    /// <param name="Width">宽。</param>
    /// <param name="Height">高。</param>
    /// <param name="Grip">按在这块区域上代表缩放哪个方向（四角即斜向）。</param>
    public sealed record Zone(double X, double Y, double Width, double Height, ResizeGrip Grip);

    /// <summary>
    /// 按窗口尺寸算出八块热区（四边中点 + 四角）。
    ///
    /// <para>布局与 DeskBox 的 3×3 网格等价：左右带铺满高、上下带铺满宽、四角 8×8 独立，
    /// 但它们**不重叠**（角从带里让出去），所以"按在角上"只会命中角那一条，
    /// 方向不会退化成单轴。</para>
    /// </summary>
    /// <param name="width">窗口宽（DIP）。</param>
    /// <param name="height">窗口高（DIP）。</param>
    public static IReadOnlyList<Zone> ForWindow(double width, double height)
    {
        if (width <= 0 || height <= 0) return [];

        // 带不能超过窗口本身（极小窗口时夹住，避免负宽高的元素）
        var hb = Math.Min(Band, width / 2);          // 左右带
        var vb = Math.Min(Band, height / 2);         // 下方带
        var tb = Math.Min(TopBand, height / 2);      // 上方带
        var cw = Math.Min(Band, width / 2 - hb / 2); // 角宽
        var ch = Math.Min(Band, height / 2 - vb / 2); // 角高
        if (cw <= 0 || ch <= 0) return [];

        var midW = width - (2 * hb);
        var midH = height - tb - vb;
        if (midW <= 0 || midH <= 0) return [];

        return
        [
            // 顶部只到 tb，角高与它对齐（角也只用 tb，免得盖住拖动区）
            new Zone(0, 0, cw, tb, ResizeGrip.TopLeft),
            new Zone(hb, 0, midW, tb, ResizeGrip.Top),
            new Zone(width - cw, 0, cw, tb, ResizeGrip.TopRight),

            new Zone(0, tb, hb, midH, ResizeGrip.Left),
            new Zone(width - hb, tb, hb, midH, ResizeGrip.Right),

            new Zone(0, height - ch, cw, ch, ResizeGrip.BottomLeft),
            new Zone(hb, height - vb, midW, vb, ResizeGrip.Bottom),
            new Zone(width - cw, height - ch, cw, ch, ResizeGrip.BottomRight),
        ];
    }
}
