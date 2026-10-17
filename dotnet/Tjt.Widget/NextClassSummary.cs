using System.Globalization;
using Tjt.Core;

namespace Tjt.Widget;

/// <summary>单条安排的完整内容及单行显示配额。完整内容永远不随宽度丢失。</summary>
public sealed record NextClassItem(string Name, string Room, string DisplayName, string DisplayRoom,
    double NameWidth, double RoomWidth);

/// <summary>下一次上课固定行。日期时间完整值供诊断和悬停使用，ShowAuxiliary 决定是否常驻。</summary>
public sealed record NextClassVisual(NextClassGroup Group, string? Marker, string DateText,
    string StartText, string? CountText, string? Message, string Tooltip,
    bool ShowAuxiliary, IReadOnlyList<NextClassItem> Items);

/// <summary>平台无关的摘要文案与宽度退让策略。外壳可提供实际字体测量。</summary>
public static class NextClassSummary
{
    public const double RowHeight = 30;
    public const double FontSize = 12;
    public const double MarkerWidth = 22;
    public const double HorizontalInsets = 20;
    public const double ItemGap = 8;
    public const double NameRoomGap = 4;

    public static NextClassVisual Build(NextClassGroup group, double availableWidth,
        Func<string, double>? measure = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        measure ??= Estimate;
        if (group.Status != NextClassStatus.Found || group.StartsAt is null || group.Sessions.Count == 0)
        {
            var message = group.Status == NextClassStatus.NoFuture
                ? "本学期无后续课程" : "信息不足，无法确定下一次上课";
            return new(group, null, "", "", null, message, message, false, []);
        }

        var date = group.StartsAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = group.StartsAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        var marker = date == group.LocalToday ? "今" : "后";
        var count = group.Sessions.Count > 1 ? $"{group.Sessions.Count}门同时开课" : null;
        // 换行只用于悬停。常驻字段洗掉数据内的换行，避免导入文本撑高单行。
        var names = group.Sessions.Select(s => SingleLine(s.Course.Name)).ToArray();
        var rooms = group.Sessions.Select(s => string.IsNullOrWhiteSpace(s.Session.Room)
            ? "教室未提供" : SingleLine(s.Session.Room!)).ToArray();
        var fullNames = names.Select(measure).ToArray();
        var fullRooms = rooms.Select(measure).ToArray();
        var tooltip = $"{marker} · {date} {time}" + (count is null ? "" : $"\n{count}")
            + "\n" + string.Join("\n", group.Sessions.Select((s, i) => $"{s.Course.Name} · {rooms[i]}"));
        var fixedWidth = HorizontalInsets + MarkerWidth + (count is null ? 0 : measure(count) + ItemGap)
            + ItemGap * Math.Max(0, names.Length - 1) + NameRoomGap * names.Length;
        var budget = Math.Max(0, (double.IsFinite(availableWidth) ? availableWidth : 0) - fixedWidth);
        var auxiliaryWidth = measure($"{date} {time}") + ItemGap;
        var showAuxiliary = fullNames.Sum() + fullRooms.Sum() + auxiliaryWidth <= budget;
        if (showAuxiliary) budget -= auxiliaryWidth;
        // 只有全部课名已让到零时，才允许任何教室被省略。不按课程设置优先级。
        var nameBudget = Math.Max(0, budget - fullRooms.Sum());
        var nameWidths = Allocate(fullNames, nameBudget);
        var roomWidths = Allocate(fullRooms, Math.Max(0, budget - nameWidths.Sum()));
        var items = names.Select((name, i) => new NextClassItem(name, rooms[i],
            Fit(name, nameWidths[i], measure), Fit(rooms[i], roomWidths[i], measure),
            nameWidths[i], roomWidths[i])).ToArray();
        return new(group, marker, date, time, count, null, tooltip, showAuxiliary, items);
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private static double[] Allocate(double[] full, double budget)
    {
        var total = full.Sum();
        var ratio = total <= budget || total == 0 ? 1 : budget / total;
        return full.Select(w => w * ratio).ToArray();
    }

    private static string Fit(string text, double width, Func<string, double> measure)
    {
        if (measure(text) <= width + 0.001) return text;
        if (measure("…") > width) return "";
        var result = "";
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var next = result + elements.GetTextElement();
            if (measure(next + "…") > width) break;
            result = next;
        }
        return result + "…";
    }

    private static double Estimate(string text) => text.EnumerateRunes()
        .Sum(r => r.Value < 0x100 ? FontSize * 0.55 : FontSize);
}
