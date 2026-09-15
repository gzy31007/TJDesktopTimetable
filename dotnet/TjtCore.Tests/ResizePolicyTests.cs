using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 缩放策略的单测（对齐 <c>Tjt.App/Win32/WindowEdgeResize.cs</c> 的真机行为）。
///
/// <para>这是"缩放自实现"里唯一能在 Linux 上验证的部分，也是最容易错的部分：
/// 判边的优先级（先角后边）、贴左用绝对坐标而不是差值、以及缩到下限时"哪条边不动"。
/// 真机上出错的表现会非常难查 —— 例如角上只能单向缩放、或者拉到最小时窗口自己往右跑。</para>
/// </summary>
public class ResizePolicyTests
{
    /// <summary>一个 1000x700、左上角在 (100, 200) 的窗口（物理像素）。</summary>
    private static WindowBounds Window() => new(100, 200, 1000, 700);

    [Theory]
    [InlineData(ResizeGrip.None, 600, 500)]        // 正中
    [InlineData(ResizeGrip.Left, 100, 500)]        // 左边中点
    [InlineData(ResizeGrip.Right, 1099, 500)]      // 右边中点
    [InlineData(ResizeGrip.Top, 600, 200)]         // 上边中点
    [InlineData(ResizeGrip.Bottom, 600, 899)]      // 下边中点
    [InlineData(ResizeGrip.TopLeft, 100, 200)]     // 四个角：先角后边
    [InlineData(ResizeGrip.TopRight, 1099, 200)]
    [InlineData(ResizeGrip.BottomLeft, 100, 899)]
    [InlineData(ResizeGrip.BottomRight, 1099, 899)]
    [InlineData(ResizeGrip.Left, 105, 500)]        // 带内
    [InlineData(ResizeGrip.None, 107, 500)]        // 带外
    [InlineData(ResizeGrip.None, 50, 500)]         // 窗口左侧之外
    [InlineData(ResizeGrip.None, 94, 500)]         // 刚出界
    [InlineData(ResizeGrip.None, 1150, 500)]
    [InlineData(ResizeGrip.None, 600, 100)]
    [InlineData(ResizeGrip.None, 600, 1000)]
    public void 命中测试与吸附带模型一致(ResizeGrip expected, int x, int y)
    {
        // 抓取带 = **内缩矩形**的 6px：`x < left + band` 而不是 `(x - left) < band` ——
        // 后者会把窗口左侧外面一整片桌面也算成抓取带（x=50 也会命中）。
        // 判定顺序是**先角后边**：角点同时满足"贴左"和"贴上"，不优先判角的话
        // 角上永远只有一个方向能缩放。窗口外一律不抓（`x >= right` / `y >= bottom` 为开区间）。
        Assert.Equal(expected, ResizePolicy.HitTest(Window(), x, y));
    }

    [Fact]
    public void 窄带为非法输入时不抓()
    {
        // 带 0 / 负数会让每条边都"命中"或产生荒谬的内缩矩形，直接判不抓
        Assert.Equal(ResizeGrip.None, ResizePolicy.HitTest(Window(), 100, 500, band: 0));
        Assert.Equal(ResizeGrip.None, ResizePolicy.HitTest(Window(), 100, 500, band: -3));
    }

    [Fact]
    public void 右下角拖动只改尺寸不动位置()
    {
        var result = ResizePolicy.Resolve(Window(), ResizeGrip.BottomRight, 120, 80);
        Assert.Equal(100, result.X);
        Assert.Equal(200, result.Y);
        Assert.Equal(1120, result.Width);
        Assert.Equal(780, result.Height);
    }

    [Fact]
    public void 左上角拖动同时改位置并保持右下角固定()
    {
        var result = ResizePolicy.Resolve(Window(), ResizeGrip.TopLeft, -50, -30);
        Assert.Equal(50, result.X);
        Assert.Equal(170, result.Y);
        Assert.Equal(1050, result.Width);   // 右边界仍是 1100
        Assert.Equal(730, result.Height);   // 下边界仍是 900
    }

    [Fact]
    public void 拉左边往右推时右边界不动()
    {
        var result = ResizePolicy.Resolve(Window(), ResizeGrip.Left, 200, 0);
        Assert.Equal(300, result.X);
        Assert.Equal(800, result.Width);
        Assert.Equal(1100, result.X + result.Width);
    }

    [Fact]
    public void 缩到下限时窗口不会反向跑()
    {
        // 拉左边界一路往右：尺寸被夹在 320，位置必须跟着夹（右边界固定），
        // 否则窗口会在到达最小尺寸后继续往右漂 —— 真机上表现为"缩到最小就自己走了"
        var result = ResizePolicy.Resolve(Window(), ResizeGrip.Left, 5000, 0);
        Assert.Equal(ResizePolicy.MinWidth, result.Width);
        Assert.Equal(1100 - ResizePolicy.MinWidth, result.X);

        var top = ResizePolicy.Resolve(Window(), ResizeGrip.Top, 0, 5000);
        Assert.Equal(ResizePolicy.MinHeight, top.Height);
        Assert.Equal(900 - ResizePolicy.MinHeight, top.Y);
    }

    [Fact]
    public void 尺寸取整到DIP整数()
    {
        // settings.json 存的是 DIP。小数会让"存下去再恢复"每次磨掉一点（尺寸棘轮）
        var result = ResizePolicy.Resolve(Window(), ResizeGrip.BottomRight, 10.4, 20.6);
        Assert.Equal(1010, result.Width);
        Assert.Equal(721, result.Height);
    }

    [Fact]
    public void 边缘光标条覆盖四条边且与抓取带同宽()
    {
        var zones = CursorZones.ForWindow(1000, 700);

        Assert.Equal(4, zones.Count);
        // 宽度口径与抓取带一致：能拖到的范围 = 显示缩放光标的范围
        Assert.All(zones, z => Assert.True(z.Width == ResizePolicy.BorderWidth || z.Height == ResizePolicy.BorderWidth));
        // 左/右条竖着铺满，上/下条横着铺满
        Assert.Contains(zones, z => z.Grip == ResizeGrip.Left && z.X == 0 && z.Height == 700);
        Assert.Contains(zones, z => z.Grip == ResizeGrip.Right && z.X + z.Width == 1000);
        Assert.Contains(zones, z => z.Grip == ResizeGrip.Top && z.Y == 0 && z.Width == 1000);
        Assert.Contains(zones, z => z.Grip == ResizeGrip.Bottom && z.Y + z.Height == 700);
    }

    [Fact]
    public void 极小窗口不产生负尺寸的光标条()
    {
        var zones = CursorZones.ForWindow(3, 2, band: 6);
        Assert.Equal(4, zones.Count);
        Assert.All(zones, z =>
        {
            Assert.True(z.Width > 0 && z.Height > 0);
            Assert.True(z.Width <= 3 && z.Height <= 2);
        });

        Assert.Empty(CursorZones.ForWindow(0, 700));
        Assert.Empty(CursorZones.ForWindow(1000, 700, band: 0));
    }

    [Fact]
    public void 尺寸下限与窗口可用判据同口径()
    {
        var size = ResizePolicy.ClampSize(10, 10);
        Assert.Equal(ResizePolicy.MinWidth, size.Width);
        Assert.Equal(ResizePolicy.MinHeight, size.Height);
        Assert.True(size.IsUsable);
    }
}
