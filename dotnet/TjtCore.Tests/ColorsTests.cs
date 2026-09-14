using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 配色的移植验收：逐条对齐 TS 侧 <c>packages/core/test/colors.spec.ts</c> 的期望值。
///
/// 这里额外钉死了若干**跨语言黄金值**（哈希整数、课程名 → 颜色、lift 输出）：
/// 这些数字取自真实运行的 TS 实现，任何一位哈希偏差都会让"同一门课在两端同色"失效。
/// </summary>
public class ColorsTests
{
    [Fact]
    public void 同名课程两次取色一致且来自色板()
    {
        var a = Colors.ColorForCourse("高等数学");
        var b = Colors.ColorForCourse("高等数学");
        Assert.Equal(a, b);
        Assert.Contains(a, Colors.CoursePalette);
    }

    [Fact]
    public void 不同课程名分布到不同颜色_抽样20门()
    {
        var names = Enumerable.Range(0, 20).Select(i => $"课程-{i}").ToArray();
        var used = names.Select(n => Colors.ColorForCourse(n)).Distinct().Count();
        Assert.True(used > 5, $"20 门课只用到 {used} 种颜色，色板分布退化");
    }

    [Fact]
    public void 哈希稳定且逐位对齐TS的黄金值()
    {
        Assert.Equal(Colors.HashString("abc"), Colors.HashString("abc"));
        Assert.NotEqual(Colors.HashString("abc"), Colors.HashString("abd"));

        // 黄金值来自真实 TS 实现（FNV-1a over UTF-16 码元 + Math.imul 截断到 32 位）
        Assert.Equal(440920331u, Colors.HashString("abc"));
        Assert.Equal(524808426u, Colors.HashString("abd"));
        Assert.Equal(1110759768u, Colors.HashString("高等数学"));
        // 中文（代理对之外的 BMP 字符）与 ASCII 走同一条路径；uint 天然非负
        Assert.True(Colors.HashString("体育(1)") == 1183382038u);
    }

    [Fact]
    public void 课程取色与TS黄金值一致()
    {
        // 同色判定依赖"哈希 → 取模 → 色板下标"整条链路，不是只看哈希
        Assert.Equal("#2563eb", Colors.ColorForCourse("高等数学"));
        Assert.Equal("#166534", Colors.ColorForCourse("大学物理"));
        Assert.Equal("#9d174d", Colors.ColorForCourse("大学英语"));
        Assert.Equal("#166534", Colors.ColorForCourse("abc"));
    }

    [Fact]
    public void 空色板与自定义色板()
    {
        // TS：palette.length === 0 时返回 fallback（palette[0] ?? '#2563eb'）
        Assert.Equal("#2563eb", Colors.ColorForCourse("高等数学", []));
        Assert.Equal("#123456", Colors.ColorForCourse("任意课程", ["#123456"]));
        // 传 null 等价于 TS 的默认参数
        Assert.Equal(Colors.ColorForCourse("高等数学"), Colors.ColorForCourse("高等数学", null));
    }

    [Fact]
    public void withAlpha生成8位十六进制()
    {
        Assert.Equal("#2563ebcc", Colors.WithAlpha("#2563eb", 0.8));
        Assert.Equal("#2563eb00", Colors.WithAlpha("#2563eb", 0));
        Assert.Equal("#2563ebff", Colors.WithAlpha("#2563eb", 1));
    }

    [Fact]
    public void withAlpha按JS的四舍五入进位并夹取区间()
    {
        // 0.5 → 127.5：JS Math.round 进位到 128（0x80），银行家舍入会错成 0x7f
        Assert.Equal("#abcdef80", Colors.WithAlpha("#abcdef", 0.5));
        Assert.Equal("#2563eb80", Colors.WithAlpha("#2563eb", 0.5019607843137255));
        Assert.Equal("#2563eb01", Colors.WithAlpha("#2563eb", 1.0 / 255));
        // 越界先 clamp，再取整
        Assert.Equal("#2563ebff", Colors.WithAlpha("#2563eb", 2));
        Assert.Equal("#2563eb00", Colors.WithAlpha("#2563eb", -1));
    }

    [Fact]
    public void 文字颜色对比度()
    {
        Assert.Equal("#111827", Colors.ReadableTextColor("#ffffff"));
        Assert.Equal("#ffffff", Colors.ReadableTextColor("#000000"));
        Assert.Equal("#ffffff", Colors.ReadableTextColor("#15803d"));
        Assert.Equal("#ffffff", Colors.ReadableTextColor("#808080"));
        // 阈值是 0.45（不是 0.5 也不是 WCAG 的 4.5），任何色板颜色都必须落到黑或白之一
        Assert.All(Colors.CoursePalette, c => Assert.Contains(Colors.ReadableTextColor(c), new[] { "#ffffff", "#111827" }));
    }

    [Fact]
    public void lift必须返回rrggbb字符串()
    {
        // 黄金值与渲染层 TS 实现一致；返回 rgb() 会让下游混色算出 NaN、色块直接变透明
        Assert.Equal("#87a9f4", Colors.Lift("#2563eb", 0.45));
        Assert.Equal("#7eb994", Colors.Lift("#15803d", 0.45));
        Assert.Equal("#737373", Colors.Lift("#000000", 0.45));
        Assert.Equal("#ffffff", Colors.Lift("#ffffff", 0.45));
        Assert.All(Colors.CoursePalette, c => Assert.Matches("^#[0-9a-f]{6}$", Colors.Lift(c, 0.45)));
        // 渲染层的 channels() 同时认 rgb(r, g, b)
        Assert.Equal("#87a9f4", Colors.Lift("rgb(37, 99, 235)", 0.45));
    }

    [Fact]
    public void 短或非法十六进制不抛异常()
    {
        // TS：slice 取不到通道 → parseInt 得 NaN → 亮度 NaN → 判为白（NaN > 0.45 为 false）
        Assert.Equal("#ffffff", Colors.ReadableTextColor("#12"));
        Assert.Equal("#ffffff", Colors.ReadableTextColor(""));
        // withAlpha 只取前 7 个字符（TS 的 hex.slice(0, 7)）
        Assert.Equal("#abcff", Colors.WithAlpha("#abc", 1));
    }
}
