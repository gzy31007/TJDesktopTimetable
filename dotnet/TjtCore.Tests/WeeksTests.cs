using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 周次掩码的移植验收：逐条对齐 TS 侧 <c>packages/core/test/weeks.spec.ts</c> 的期望值。
///
/// 这些断言不是"随手编的分支覆盖"，而是与既有 Python 实现（<c>fmt_weeks</c>）和真实教务数据
/// 绑定的黄金约定 —— 移植过程中任何一处语义偏差都会在这里暴露。
/// </summary>
public class WeeksTests
{
    [Fact]
    public void 周次列表与掩码往返()
    {
        Assert.Equal(0b111u, Weeks.FromWeeks([1, 2, 3]));
        Assert.Equal(1u << 15, Weeks.FromWeeks([16]));
        Assert.Equal([1, 2, 3], Weeks.ToWeeks(0b111, 16));
        Assert.Equal([16], Weeks.ToWeeks(1u << 15, 16));
    }

    [Fact]
    public void 忽略非法周次()
    {
        // TS 那边还要滤掉 1.5 这种非整数；C# 的 int 参数天然表达不了，剩下越界/非正在这里覆盖
        Assert.Equal(Weeks.FromWeeks([3]), Weeks.FromWeeks([0, -1, 33, 3]));
        Assert.Equal(0u, Weeks.FromWeeks([]));
    }

    [Fact]
    public void 第32周仍然可表达()
    {
        Assert.Equal(0x8000_0000u, Weeks.FromWeeks([32]));
        Assert.Equal([32], Weeks.ToWeeks(Weeks.FromWeeks([32]), 32));
    }

    [Fact]
    public void 全周掩码等于_0xFFFF()
    {
        Assert.Equal(65535u, Weeks.FullMask(16));
        Assert.Equal(16, Weeks.Count(Weeks.FullMask(16)));
        Assert.True(Weeks.IsAll(65535, 16));
        Assert.False(Weeks.IsAll(65535, 17));
    }

    [Fact]
    public void 周次标签与既有Python实现一致()
    {
        Assert.Equal("1-16", Weeks.FormatLabel(65535));
        Assert.Equal("1, 3, 5, 7, 9, 11, 13, 15", Weeks.FormatLabel(Weeks.FromWeeks([1, 3, 5, 7, 9, 11, 13, 15])));
        Assert.Equal("2, 4, 6, 8, 10, 12, 14, 16", Weeks.FormatLabel(Weeks.FromWeeks([2, 4, 6, 8, 10, 12, 14, 16])));
        Assert.Equal("11-14", Weeks.FormatLabel(Weeks.FromWeeks([11, 12, 13, 14])));
        Assert.Equal("9, 16", Weeks.FormatLabel(Weeks.FromWeeks([9, 16])));
        Assert.Equal("-", Weeks.FormatLabel(0));
    }

    [Fact]
    public void 掩码最高位超出校历周数时仍完整展开()
    {
        var mask = Weeks.FromWeeks([17, 18, 19]);
        Assert.Equal(19, Weeks.Highest(mask));
        Assert.Equal([17, 18, 19], Weeks.ToWeeks(mask, 16));
        Assert.Equal("17-19", Weeks.FormatLabel(mask, 16));
    }

    [Fact]
    public void 单双周掩码按总周数生成而不是硬编码()
    {
        Assert.Equal(0x5555u, Weeks.OddMask(16));
        Assert.Equal(0xaaaau, Weeks.EvenMask(16));
        Assert.Equal([1, 3, 5, 7, 9, 11, 13, 15], Weeks.ToWeeks(Weeks.OddMask(15), 15));
        Assert.Equal([2, 4, 6, 8, 10, 12, 14], Weeks.ToWeeks(Weeks.EvenMask(15), 15));
        // 20 周学期：硬编码 0x5555 会截断，泛化实现不会
        Assert.Equal([1, 3, 5, 7, 9, 11, 13, 15, 17, 19], Weeks.ToWeeks(Weeks.OddMask(20), 20));
    }

    [Fact]
    public void 单周掩码按周次生成且越界给零()
    {
        Assert.Equal(1u, Weeks.WeekMask(1));
        Assert.Equal(0b100u, Weeks.WeekMask(3));
        Assert.Equal(1u << 15, Weeks.WeekMask(16));
        Assert.Equal(0x8000_0000u, Weeks.WeekMask(32));
        // 越界一律 0（调用方据此判"这一周不存在"）
        Assert.Equal(0u, Weeks.WeekMask(0));
        Assert.Equal(0u, Weeks.WeekMask(-3));
        Assert.Equal(0u, Weeks.WeekMask(33));
    }

    [Fact]
    public void 周次视图解析_四态与当前周兜底()
    {
        // All / 当前周为 null → 不过滤（null 语义 = 全部周次）
        Assert.Null(Weeks.ResolveFilter(WeekView.All, 3, 16));
        Assert.Null(Weeks.ResolveFilter(WeekView.Current, null, 16));       // 假期 / 开学日未知
        Assert.Null(Weeks.ResolveFilter(WeekView.Current, 0, 16));          // 越界周 → 同样不过滤
        Assert.Null(Weeks.ResolveFilter(WeekView.Current, 33, 16));

        // Current 且有当前周 → 只命中这一周
        Assert.Equal(1u, Weeks.ResolveFilter(WeekView.Current, 1, 16));
        Assert.Equal(1u << 15, Weeks.ResolveFilter(WeekView.Current, 16, 16));

        // 单双周按 totalWeeks 生成（15 周学期的偶数是 2..14，不是 0xAAAA）
        Assert.Equal(0x5555u, Weeks.ResolveFilter(WeekView.Odd, null, 16));
        Assert.Equal(0xaaaau, Weeks.ResolveFilter(WeekView.Even, null, 16));
        Assert.Equal(Weeks.EvenMask(15), Weeks.ResolveFilter(WeekView.Even, null, 15));
    }

    [Fact]
    public void 过滤器解析与交集判断()
    {
        Assert.True(Weeks.Overlap(Weeks.FromWeeks([1, 3]), Weeks.FromWeeks([2, 3])));
        Assert.False(Weeks.Overlap(Weeks.FromWeeks([1, 3]), Weeks.FromWeeks([2, 4])));
    }

    [Fact]
    public void 默认节次表与同济校历一致()
    {
        Assert.Equal(11, TimetableDefaults.TongjiSlots.Length);
        Assert.Equal("08:00", TimetableDefaults.TongjiSlots[0].Begin);
        Assert.Equal("20:55", TimetableDefaults.TongjiSlots[^1].End);
        // 不打乱调用方传入的集合，并且按下标排序
        var sorted = TimetableDefaults.SlotsFromList([new Slot(3, "c", "d"), new Slot(1, "a", "b")]);
        Assert.Equal([1, 3], sorted.Select(s => s.Index));
    }
}
