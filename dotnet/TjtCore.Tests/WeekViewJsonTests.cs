using System.Text.Json;
using Tjt.Core;
using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 周次视图的**落盘口径**与**用户文案**验收。
///
/// <para>落盘：<see cref="WeekView"/> 上挂了 <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>，
/// 因此写出来是字符串（ADR 0002）；未知取值会抛 <see cref="JsonException"/> → 整份设置回落默认，
/// 与既有的"坏 JSON 就回落"同一条兜底路径。</para>
///
/// <para>文案：三个入口（挂件 ⋯ 菜单 / 托盘 / 设置页）跨两个外壳，共用 <see cref="WeekViewLabels"/>，
/// 这里钉住顺序与措辞 —— 各写一套迟早会漂。</para>
/// </summary>
public class WeekViewJsonTests
{
    [Fact]
    public void 周次视图落盘是字符串而不是数字()
    {
        Assert.Equal("\"Current\"", JsonSerializer.Serialize(WeekView.Current));
        Assert.Equal("\"All\"", JsonSerializer.Serialize(WeekView.All));
        Assert.Equal("\"Odd\"", JsonSerializer.Serialize(WeekView.Odd));
        Assert.Equal("\"Even\"", JsonSerializer.Serialize(WeekView.Even));

        // 读回来认枚举名（大小写不敏感：手改设置写 "current" 也能读）
        Assert.Equal(WeekView.Current, JsonSerializer.Deserialize<WeekView>("\"Current\""));
        Assert.Equal(WeekView.Current, JsonSerializer.Deserialize<WeekView>("\"current\""));

        // 未知取值 → 抛异常（调用方 SettingsStore 靠它整份回落默认值）
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<WeekView>("\"whatever\""));

        // ⚠️ 数字仍然被接受：`JsonStringEnumConverter` 的 `allowIntegerValues` 默认为 true，
        // 于是 "2" 按枚举**位置**解释成 Odd。这是该转换器的既有行为，我们不再加严
        // （Q15 定的就是 JsonStringEnumConverter；手写设置里写数字属于自担风险）。
        Assert.Equal(WeekView.Odd, JsonSerializer.Deserialize<WeekView>("2"));
    }

    [Fact]
    public void 周次视图文案四态齐全且顺序固定()
    {
        Assert.Equal(
            new[] { WeekView.All, WeekView.Current, WeekView.Odd, WeekView.Even },
            WeekViewLabels.Ordered.Select(item => item.View).ToArray());
        Assert.Equal(
            new[] { "全部周次", "只看本周", "只看单周", "只看双周" },
            WeekViewLabels.Ordered.Select(item => item.Label).ToArray());

        Assert.Equal("只看本周", WeekViewLabels.Label(WeekView.Current));
        Assert.Equal("周次视图", WeekViewLabels.Title);
        // 说明文字必须写明兜底口径（ADR 0001 的代价补偿：界面要能解释"为什么看起来没生效"）
        Assert.Contains("显示全部周次", WeekViewLabels.Description, StringComparison.Ordinal);
    }
}