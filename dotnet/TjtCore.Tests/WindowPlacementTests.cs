using System.Text.Json;
using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

public class WindowPlacementTests
{
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 同DPI保存和恢复物理像素重复百次不产生棘轮(double scale)
    {
        var expected = new WindowBounds(-1921, -301, 1001, 701);
        var bounds = expected;
        for (var i = 0; i < 100; i++)
        {
            var saved = JsonSerializer.Deserialize<WindowBounds>(JsonSerializer.Serialize(bounds))!;
            bounds = WindowPlacement.RestorePhysicalBounds(saved, scale, scale);
            Assert.Equal(expected, bounds);
        }
    }

    [Theory]
    [InlineData(1.25, 1.5, 1001, 701, 1201, 841)]
    [InlineData(1.5, 2.0, 1001, 701, 1335, 935)]
    [InlineData(2.0, 1.25, 1001, 701, 626, 438)]
    [InlineData(1.25, 1.5, 1003, 703, 1204, 844)]
    [InlineData(2.0, 1.0, 1001, 701, 500, 350)]
    public void 跨DPI只按Round缩放尺寸并保留物理位置(
        double savedScale, double targetScale, int width, int height, int expectedWidth, int expectedHeight)
    {
        var saved = new WindowBounds(-1921, -301, width, height);

        Assert.Equal(new WindowBounds(-1921, -301, expectedWidth, expectedHeight),
            WindowPlacement.RestorePhysicalBounds(saved, savedScale, targetScale));
    }

    [Theory]
    [InlineData(1.25, 400, 300)]
    [InlineData(1.5, 480, 360)]
    [InlineData(2.0, 640, 480)]
    [InlineData(1.251, 401, 301)]
    public void 跨DPI恢复仅使用目标DPI的基本外框下限(double targetScale, int width, int height)
    {
        var saved = new WindowBounds(-1500, 50, 1, 1);

        Assert.Equal(new WindowBounds(-1500, 50, width, height),
            WindowPlacement.RestorePhysicalBounds(saved, 1, targetScale));
    }

    [Fact]
    public void 跨DPI只补不足的尺寸轴()
    {
        var saved = new WindowBounds(-1500, 50, 1001, 1);

        Assert.Equal(new WindowBounds(-1500, 50, 1502, 360),
            WindowPlacement.RestorePhysicalBounds(saved, 1, 1.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 保存与目标DPI比例都必须为有限正数(double scale)
    {
        var saved = new WindowBounds(0, 0, 1001, 701);

        Assert.Throws<ArgumentOutOfRangeException>(() => WindowPlacement.RestorePhysicalBounds(saved, scale, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowPlacement.RestorePhysicalBounds(saved, 1, scale));
    }

    [Theory]
    [InlineData(0, 701)]
    [InlineData(-1, 701)]
    [InlineData(1001, 0)]
    [InlineData(1001, -1)]
    public void 物理恢复拒绝非正保存尺寸(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowPlacement.RestorePhysicalBounds(new(0, 0, width, height), 1.25, 1.25));

    [Fact]
    public void 物理恢复拒绝空外框与溢出尺寸()
    {
        Assert.Throws<ArgumentNullException>(() => WindowPlacement.RestorePhysicalBounds(null!, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowPlacement.RestorePhysicalBounds(new(0, 0, int.MaxValue, 701), 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowPlacement.RestorePhysicalBounds(new(0, 0, 1001, 701), 1, double.MaxValue));
    }

    [Theory]
    [InlineData(0, 0, 1000, 700)]
    [InlineData(680, 460, 320, 240)]
    [InlineData(100, 200, 400, 260)]
    public void 工作区已能容纳时不改变外框(int x, int y, int width, int height)
    {
        var bounds = new WindowBounds(x, y, width, height);

        Assert.Equal(bounds, WindowPlacement.FitToWorkArea(bounds, new(0, 0, 1000, 700)));
    }

    [Theory]
    [InlineData(-1500, 100, -1280, 100)]
    [InlineData(-200, 100, -400, 100)]
    [InlineData(-1000, -400, -1000, -200)]
    [InlineData(-1000, 700, -1000, 460)]
    [InlineData(3000, 3000, -400, 460)]
    [InlineData(-3000, -3000, -1280, -200)]
    public void 负坐标工作区中的越界小窗只移动(int x, int y, int expectedX, int expectedY)
    {
        var bounds = new WindowBounds(x, y, 400, 300);
        var work = new WindowBounds(-1280, -200, 1280, 960);

        var result = WindowPlacement.FitToWorkArea(bounds, work);

        Assert.Equal(new WindowBounds(expectedX, expectedY, 400, 300), result);
    }

    [Theory]
    [InlineData(1400, 500, 1000, 500, -1000, 100)]
    [InlineData(500, 1200, 500, 700, -900, 40)]
    [InlineData(1400, 1200, 1000, 700, -1000, 40)]
    public void 大窗只缩小超出工作区的轴(
        int width, int height, int expectedWidth, int expectedHeight, int expectedX, int expectedY)
    {
        var bounds = new WindowBounds(-900, 100, width, height);
        var work = new WindowBounds(-1000, 40, 1000, 700);

        Assert.Equal(new WindowBounds(expectedX, expectedY, expectedWidth, expectedHeight),
            WindowPlacement.FitToWorkArea(bounds, work));
    }

    [Fact]
    public void 保存适配尺寸后回到大屏不恢复原尺寸()
    {
        var original = new WindowBounds(100, 100, 1600, 1000);
        var adapted = WindowPlacement.FitToWorkArea(original, new(0, 0, 1000, 700));
        // 仅内存序列化，验证持久化返回结果即可保留适配后的尺寸，不触碰真实设置。
        var saved = JsonSerializer.Deserialize<WindowBounds>(JsonSerializer.Serialize(adapted))!;

        var onLargeScreen = WindowPlacement.FitToWorkArea(saved, new(0, 0, 2560, 1440));

        Assert.Equal(new WindowBounds(0, 0, 1000, 700), adapted);
        Assert.Equal(adapted, onLargeScreen);
    }

    [Theory]
    [InlineData(320, 240)] // 100%
    [InlineData(400, 300)] // 125%
    [InlineData(480, 360)] // 150%
    [InlineData(640, 480)] // 200%
    public void 物理像素下限由调用壳按DPI传入(int minWidth, int minHeight)
    {
        var work = new WindowBounds(-2560, -100, 2560, 1440);
        var undersized = new WindowBounds(-2000, 100, 1, 1);

        var result = WindowPlacement.FitToWorkArea(undersized, work, minWidth, minHeight);

        Assert.Equal(new WindowBounds(-2000, 100, minWidth, minHeight), result);
        Assert.Equal(new WindowBounds(work.X, work.Y, minWidth, minHeight),
            WindowPlacement.FitToWorkArea(new(100, 100, 2000, 1500),
                new(work.X, work.Y, minWidth, minHeight), minWidth, minHeight));
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(400, 300)]
    [InlineData(480, 360)]
    [InlineData(640, 480)]
    public void 容不下基本窗口的工作区被明确排除(int minWidth, int minHeight)
    {
        var bounds = new WindowBounds(0, 0, 1000, 700);

        Assert.Throws<ArgumentOutOfRangeException>(() => WindowPlacement.FitToWorkArea(
            bounds, new(0, 0, minWidth - 1, 1200), minWidth, minHeight));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowPlacement.FitToWorkArea(
            bounds, new(0, 0, 2000, minHeight - 1), minWidth, minHeight));
    }

    [Fact]
    public void 重复适配和工作区切换不产生尺寸棘轮()
    {
        var small = new WindowBounds(-1200, -500, 1200, 800);
        var large = new WindowBounds(-2000, -800, 4000, 2400);
        var expected = new WindowBounds(-1200, -500, 1200, 800);
        var bounds = WindowPlacement.FitToWorkArea(new(3000, 2000, 1601, 1001), small);

        for (var i = 0; i < 100; i++)
        {
            bounds = WindowPlacement.FitToWorkArea(bounds, small);
            Assert.Equal(expected, bounds);
            bounds = WindowPlacement.FitToWorkArea(bounds, large);
            Assert.Equal(expected, bounds);
        }
    }

    [Fact]
    public void 极端屏幕坐标不因整数溢出夹到错误方向()
    {
        var work = new WindowBounds(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
        var bounds = new WindowBounds(int.MaxValue, int.MaxValue, 320, 240);

        Assert.Equal(new WindowBounds(-321, -241, 320, 240),
            WindowPlacement.FitToWorkArea(bounds, work));
    }

    [Fact]
    public void 右下边界超出整数范围时仍保留已容纳窗口()
    {
        var work = new WindowBounds(int.MaxValue - 100, int.MaxValue - 100, 1000, 700);
        var bounds = new WindowBounds(int.MaxValue - 50, int.MaxValue - 50, 320, 240);

        Assert.Equal(bounds, WindowPlacement.FitToWorkArea(bounds, work));
    }

    [Theory]
    [InlineData(0, 240)]
    [InlineData(-1, 240)]
    [InlineData(320, 0)]
    [InlineData(320, -1)]
    public void 非正尺寸和非正下限不进入几何计算(int width, int height)
    {
        var bounds = new WindowBounds(0, 0, width, height);
        var work = new WindowBounds(0, 0, 1000, 700);

        Assert.Throws<ArgumentOutOfRangeException>(() => WindowPlacement.FitToWorkArea(bounds, work));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowPlacement.FitToWorkArea(new(0, 0, 400, 300), work, width, height));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowPlacement.FitToWorkArea(new(0, 0, 400, 300), new(0, 0, width, height)));
    }

    [Fact]
    public void 空参数明确拒绝()
    {
        var bounds = new WindowBounds(0, 0, 400, 300);

        Assert.Throws<ArgumentNullException>(() => WindowPlacement.FitToWorkArea(null!, bounds));
        Assert.Throws<ArgumentNullException>(() => WindowPlacement.FitToWorkArea(bounds, null!));
    }
}
