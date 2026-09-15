namespace Tjt.Widget;

/// <summary>
/// 缩放的抓取边（挂在窗口边缘的哪一条 / 哪一个角）。
///
/// <para>取值刻意与 Win32 的命中测试码一一对应（<c>HTLEFT=10</c> … <c>HTBOTTOMRIGHT=17</c>），
/// 但不引用任何 Win32 类型 —— 这一层要能在 Linux 上编译与单测。</para>
/// </summary>
public enum ResizeGrip
{
    /// <summary>不在任何抓取边上。</summary>
    None = 0,

    /// <summary>左边缘（<c>HTLEFT</c>=10）。</summary>
    Left = 10,

    /// <summary>右边缘（<c>HTRIGHT</c>=11）。</summary>
    Right = 11,

    /// <summary>上边缘（<c>HTTOP</c>=12）。</summary>
    Top = 12,

    /// <summary>左上角（<c>HTTOPLEFT</c>=13）。</summary>
    TopLeft = 13,

    /// <summary>右上角（<c>HTTOPRIGHT</c>=14）。</summary>
    TopRight = 14,

    /// <summary>下边缘（<c>HTBOTTOM</c>=15）。</summary>
    Bottom = 15,

    /// <summary>左下角（<c>HTBOTTOMLEFT</c>=16）。</summary>
    BottomLeft = 16,

    /// <summary>右下角（<c>HTBOTTOMRIGHT</c>=17）。</summary>
    BottomRight = 17,
}

/// <summary>
/// 缩放策略 —— **纯函数，零依赖，可单测**（与 <see cref="RestingPolicy"/> 同一层）。
///
/// <para><b>为什么要自实现</b>：窗口去掉了 <c>WS_CAPTION | WS_BORDER | WS_DLGFRAME |
/// WS_THICKFRAME</c>（右上角那三个系统按钮必须消失），而系统默认的缩放命中带
/// （<c>SM_CXSIZEFRAME</c> + <c>SM_CXPADDEDBORDER</c> ≈ 8px）**完全落在窗口之外**：
/// 在那条带上按下去什么反应都没有，观感就是"边缘有一圈抓不住的隐形边"。所以由我们自己在
/// <b>客户区可见的描边内侧</b>回答 <c>WM_NCHITTEST</c>，把系统丢失的那圈抓取带补回来。</para>
///
/// <para>判据顺序（与 <c>DefWindowProc</c> 一致）：**先角后边** —— 左上角同时满足"贴左"与
/// "贴上"，必须优先归到角上，否则角上永远只有一个方向能缩放。判定用的是**内缩矩形**：
/// 贴左判定写成 <c>x &lt; band</c>（而不是 <c>x - left &lt; band</c>），因为 <c>WM_NCHITTEST</c>
/// 传进来的坐标是物理像素、且可能落在窗口外一点点（-1、width+1 是常见值）。</para>
/// </summary>
public static class ResizePolicy
{
    /// <summary>
    /// 抓取带宽度（物理像素）。6px 与系统 <c>SM_CXSIZEFRAME + SM_CXPADDEDBORDER</c> 接近，
    /// 在 150% 缩放下 ≈ 4 DIP —— 够好抓，又不会把顶部条压得点不动按钮。
    /// </summary>
    public const int BorderWidth = 6;

    /// <summary>窗口最小尺寸（**外框 DIP**），与 <see cref="WindowBounds.IsUsable"/> 同一口径。</summary>
    public const int MinWidth = 320;

    /// <summary>窗口最小高度（外框 DIP）。</summary>
    public const int MinHeight = 240;

    /// <summary>
    /// 给定窗口外框与光标物理坐标，返回抓取边。
    /// </summary>
    /// <param name="window">窗口外框（物理像素）。</param>
    /// <param name="cursorX">光标 X（物理像素，屏幕坐标）。</param>
    /// <param name="cursorY">光标 Y（物理像素，屏幕坐标）。</param>
    /// <param name="band">抓取带宽度（物理像素）。</param>
    public static ResizeGrip HitTest(WindowBounds window, int cursorX, int cursorY, int band = BorderWidth)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (band <= 0) return ResizeGrip.None;

        // 内缩矩形（左闭右开）。用窗口自身尺寸算，而不是拿 cursor-left 比 —— 见类注释。
        var left = window.X;
        var top = window.Y;
        var right = window.X + window.Width;
        var bottom = window.Y + window.Height;

        // 完全在窗口之外：不认（否则挂件旁边的桌面区域也会变成我们的缩放手柄）
        if (cursorX < left || cursorX >= right || cursorY < top || cursorY >= bottom) return ResizeGrip.None;

        var nearLeft = cursorX < left + band;
        var nearRight = cursorX >= right - band;
        var nearTop = cursorY < top + band;
        var nearBottom = cursorY >= bottom - band;

        if (nearTop) return nearLeft ? ResizeGrip.TopLeft : nearRight ? ResizeGrip.TopRight : ResizeGrip.Top;
        if (nearBottom) return nearLeft ? ResizeGrip.BottomLeft : nearRight ? ResizeGrip.BottomRight : ResizeGrip.Bottom;
        if (nearLeft) return ResizeGrip.Left;
        if (nearRight) return ResizeGrip.Right;
        return ResizeGrip.None;
    }

    /// <summary>
    /// 缩放目标尺寸（DIP 整数）。窗口尺寸<strong>一律取整 DIP</strong>：
    /// <c>settings.json</c> 存的就是 DIP，小数会让"存下去 → 恢复回来"每次磨掉一点，
    /// 也就是 Electron 侧踩过的尺寸棘轮。
    /// </summary>
    /// <param name="width">当前宽（DIP）。</param>
    /// <param name="height">当前高（DIP）。</param>
    public static WindowBounds ClampSize(double width, double height) =>
        new(0, 0,
            Math.Max(MinWidth, (int)Math.Round(width)),
            Math.Max(MinHeight, (int)Math.Round(height)));

    /// <summary>
    /// 从一个角/边解算出**期望外框**（DIP）。
    ///
    /// <para>缩放"从哪条边拉"决定哪条边不动：拉左/上边时右下角固定，拉右/下边时左上角固定。
    /// 位置同样取整 DIP —— 否则 <c>AppWindow.Move</c> 每帧都被四舍五入一次，
    /// 拖动过程中窗口会以 1px 为单位抖动。</para>
    /// </summary>
    /// <param name="start">手势开始时的外框（DIP）。</param>
    /// <param name="grip">抓取边。</param>
    /// <param name="dx">光标位移 X（DIP）。</param>
    /// <param name="dy">光标位移 Y（DIP）。</param>
    public static WindowBounds Resolve(WindowBounds start, ResizeGrip grip, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(start);

        // 固定边：拉左/上边时右/下边不动
        var right = start.X + start.Width;
        var bottom = start.Y + start.Height;

        var left = (double)start.X;
        var top = (double)start.Y;
        var width = (double)start.Width;
        var height = (double)start.Height;

        if (grip is ResizeGrip.Left or ResizeGrip.TopLeft or ResizeGrip.BottomLeft)
        {
            left = start.X + dx;
            width = right - left;
        }
        else if (grip is ResizeGrip.Right or ResizeGrip.TopRight or ResizeGrip.BottomRight)
        {
            width = start.Width + dx;
        }

        if (grip is ResizeGrip.Top or ResizeGrip.TopLeft or ResizeGrip.TopRight)
        {
            top = start.Y + dy;
            height = bottom - top;
        }
        else if (grip is ResizeGrip.Bottom or ResizeGrip.BottomLeft or ResizeGrip.BottomRight)
        {
            height = start.Height + dy;
        }

        // 尺寸先夹到下限，再按"哪条边不动"把被夹掉的部分还回去
        var clamped = ClampSize(width, height);
        if (grip is ResizeGrip.Left or ResizeGrip.TopLeft or ResizeGrip.BottomLeft) left = right - clamped.Width;
        if (grip is ResizeGrip.Top or ResizeGrip.TopLeft or ResizeGrip.TopRight) top = bottom - clamped.Height;

        return clamped with
        {
            X = (int)Math.Round(left),
            Y = (int)Math.Round(top),
        };
    }
}
