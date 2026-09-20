namespace Tjt.Widget;

/// <summary>
/// 周次视图的**用户可见文案**（`⋯` 菜单 / 托盘菜单 / 设置页共用一份）。
///
/// <para>为什么放在 <c>Tjt.Widget</c>：三处入口跨越两个外壳（WinUI 与 Avalonia），文案必须逐字一致
/// —— 各写一套迟早会出现"菜单叫『只看本周』、设置页叫『本周』"。这里是纯文本，没有视觉规则，
/// 也不引 WinUI / Win32，符合"纯计算层"的边界。</para>
///
/// <para>措辞是 2026-09-21 grilling 定稿的：子菜单「周次视图」，四项「全部周次 / 只看本周 /
/// 只看单周 / 只看双周」。</para>
/// </summary>
public static class WeekViewLabels
{
    /// <summary>菜单与下拉框里的固定顺序（全部 → 本周 → 单周 → 双周）。</summary>
    public static readonly (Tjt.Core.WeekView View, string Label)[] Ordered =
    [
        (Tjt.Core.WeekView.All, "全部周次"),
        (Tjt.Core.WeekView.Current, "只看本周"),
        (Tjt.Core.WeekView.Odd, "只看单周"),
        (Tjt.Core.WeekView.Even, "只看双周"),
    ];

    /// <summary>子菜单 / 设置页那一行的标题。</summary>
    public const string Title = "周次视图";

    /// <summary>
    /// 设置页的说明文字（**静态**写明兜底口径 —— ADR 0001 要求的代价补偿：
    /// "过滤开关打开了、看起来却没生效"必须能从界面上读懂）。
    /// </summary>
    public const string Description =
        "只看本周：开学日未知或放假期间显示全部周次；周次视图只决定画哪些课，不影响冲突判定与今日高亮";

    /// <summary>视图 → 文案（未知取值退回「全部周次」，与"不过滤"的实际行为一致）。</summary>
    public static string Label(Tjt.Core.WeekView view)
    {
        foreach (var (item, label) in Ordered)
        {
            if (item == view) return label;
        }

        return Ordered[0].Label;
    }
}