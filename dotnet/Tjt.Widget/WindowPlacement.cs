namespace Tjt.Widget;

/// <summary>
/// 窗口外框的工作区适配。只计算几何，不读取课表、屏幕或设置，也不保留适配前的尺寸。
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// 从持久化的物理外框恢复尺寸。同 DPI 精确保留像素；跨 DPI 只缩放宽高，
    /// 保留全局物理坐标，再约束到目标 DPI 下的基本外框下限，不按内容增高。
    /// </summary>
    /// <remarks>外壳先按保存位置定位目标屏幕并读取其 DPI；工作区适配另用 FitToWorkArea。</remarks>
    /// <exception cref="ArgumentNullException">保存的外框为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非正、比例非有限正数，或缩放结果超出整数尺寸范围。</exception>
    public static WindowBounds RestorePhysicalBounds(WindowBounds savedPixels, double savedScale, double targetScale)
    {
        ArgumentNullException.ThrowIfNull(savedPixels);
        if (savedPixels.Width <= 0 || savedPixels.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(savedPixels), "窗口外框尺寸必须为正。");
        if (!double.IsFinite(savedScale) || savedScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(savedScale), "DPI 比例必须为有限正数。");
        if (!double.IsFinite(targetScale) || targetScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetScale), "DPI 比例必须为有限正数。");

        // 不经 DIP 整数中转，否则 125% 的 1001px 重启后会变成 1002px。
        if (savedScale == targetScale)
            return savedPixels;

        var width = Math.Max(Math.Ceiling(ResizePolicy.MinWidth * targetScale),
            Math.Round(savedPixels.Width / savedScale * targetScale));
        var height = Math.Max(Math.Ceiling(ResizePolicy.MinHeight * targetScale),
            Math.Round(savedPixels.Height / savedScale * targetScale));
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > int.MaxValue || height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(targetScale), "缩放后的外框超出整数尺寸范围。");

        return new WindowBounds(savedPixels.X, savedPixels.Y, (int)width, (int)height);
    }

    /// <summary>
    /// 必要时缩小外框，再以最小位移把它完整移入工作区。
    /// 已能容纳的尺寸保持不变；低于基本下限的输入只补到下限，不按内容增高。
    /// </summary>
    /// <param name="bounds">当前外框，使用屏幕坐标。</param>
    /// <param name="workArea">目标屏幕工作区，与外框使用同一坐标系和单位。</param>
    /// <param name="minWidth">外框基本最小宽度。默认单位为 DIP；传物理像素时由外壳先换算。</param>
    /// <param name="minHeight">外框基本最小高度。默认单位为 DIP；传物理像素时由外壳先换算。</param>
    /// <returns>适配后的外框。外壳负责应用并保存此结果，回到大屏时继续以此结果为输入。</returns>
    /// <exception cref="ArgumentNullException">外框或工作区为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">尺寸或下限非正，或工作区容不下基本窗口。</exception>
    public static WindowBounds FitToWorkArea(
        WindowBounds bounds,
        WindowBounds workArea,
        int minWidth = ResizePolicy.MinWidth,
        int minHeight = ResizePolicy.MinHeight)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(workArea);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "窗口外框尺寸必须为正。");
        if (workArea.Width < minWidth || workArea.Height < minHeight)
            throw new ArgumentOutOfRangeException(nameof(workArea), "工作区容不下基本窗口，不属于支持范围。");

        var width = Math.Clamp(bounds.Width, minWidth, workArea.Width);
        var height = Math.Clamp(bounds.Height, minHeight, workArea.Height);

        // 先扩成 long 再相加，负坐标与远端屏幕都不能让右下边界溢出。
        var x = Math.Clamp((long)bounds.X, workArea.X, (long)workArea.X + workArea.Width - width);
        var y = Math.Clamp((long)bounds.Y, workArea.Y, (long)workArea.Y + workArea.Height - height);
        return new WindowBounds((int)x, (int)y, width, height);
    }
}
