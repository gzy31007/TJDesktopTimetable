using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 缩放策略的单测（对齐 <c>Tjt.App/Win32/WindowResize.cs</c> 的真机行为）。
///
/// <para>这是"缩放自实现"里唯一能在 Linux 上验证的部分，也是最容易错的部分：
/// 判边的优先级（先角后边）、贴左用绝对坐标而不是差值、以及缩到下限时"哪条边不动"。
/// 真机上出错的表现会非常难查 —— 例如角上只能单向缩放、或者拉到最小时窗口自己往右跑。</para>
/// </summary>
public class ResizePolicyTests
{
    /// <summary>一个 1000x700、左上角在 (100, 200) 的窗口（物理像素）。</summary>
    private static WindowBounds Window() => new(100, 200, 1000, 700);

    private static ResizeGrip Hit(int x, int y) => ResizePolicy.HitTest(Window(), x, y);

    [Fact]
    public void 正中不抓()
    {
        Assert.Equal(ResizeGrip.None, Hit(600, 500));
    }

    [Fact]
    public void 四条边的中点各抓一条边()
    {
        Assert.Equal(ResizeGrip.Left, Hit(100, 500));
        Assert.Equal(ResizeGrip.Right, Hit(1099, 500));
        Assert.Equal(ResizeGrip.Top, Hit(600, 200));
        Assert.Equal(ResizeGrip.Bottom, Hit(600, 899));
    }

    [Fact]
    public void 四个角优先于边_先角后边()
    {
        // 角点同时满足"贴左"与"贴上"：必须判成角，否则角上永远只有一个方向能缩放
        Assert.Equal(ResizeGrip.TopLeft, Hit(100, 200));
        Assert.Equal(ResizeGrip.TopRight, Hit(1099, 200));
        Assert.Equal(ResizeGrip.BottomLeft, Hit(100, 899));
        Assert.Equal(ResizeGrip.BottomRight, Hit(1099, 899));
    }

    [Fact]
    public void 抓取带宽度就是六像素()
    {
        // 带内（第 5 个像素）算命中
        Assert.Equal(ResizeGrip.Left, Hit(105, 500));
        // 带外（第 7 个像素）不算
        Assert.Equal(ResizeGrip.None, Hit(107, 500));
        // 右边同理（右边界 1100 是开区间）
        Assert.Equal(ResizeGrip.Right, Hit(1094, 500));
        Assert.Equal(ResizeGrip.None, Hit(1092, 500));
    }

    [Fact]
    public void 贴左用绝对坐标而不是差值()
    {
        // 关键回归：判定必须是 x < left + band，不能写成 (x - left) < band。
        // 窗口左上角在 (100,200)，若用差值判定，"窗口左侧外面"（x = 50 → 差值 -50）
        // 也会满足 < band，于是挂件左边一整片桌面都成了我们的缩放手柄。
        Assert.Equal(ResizeGrip.Left, Hit(100, 500));   // 边缘那一像素：必须命中
        Assert.Equal(ResizeGrip.Left, Hit(105, 500));   // 带内
        Assert.Equal(ResizeGrip.None, Hit(50, 500));    // 窗口左侧之外：不命中
        Assert.Equal(ResizeGrip.None, Hit(94, 500));    // 刚出界
    }

    [Fact]
    public void 窗口外一律不抓()
    {
        Assert.Equal(ResizeGrip.None, Hit(50, 500));
        Assert.Equal(ResizeGrip.None, Hit(1150, 500));
        Assert.Equal(ResizeGrip.None, Hit(600, 100));
        Assert.Equal(ResizeGrip.None, Hit(600, 1000));
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
    public void 尺寸下限与窗口可用判据同口径()
    {
        var size = ResizePolicy.ClampSize(10, 10);
        Assert.Equal(ResizePolicy.MinWidth, size.Width);
        Assert.Equal(ResizePolicy.MinHeight, size.Height);
        Assert.True(size.IsUsable);
    }
}
