using System.Globalization;
using Tjt.Core;

namespace Tjt.Widget;

/// <summary>将诊断日期/时分覆盖合成为同一完整绝对时刻，不读系统时钟。</summary>
public static class BoardClock
{
    public static DateTimeOffset Resolve(DateTimeOffset sampledNow, string? today = null,
        int? nowMinutes = null, int tzOffsetMinutes = TimetableModel.DefaultTzOffsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(tzOffsetMinutes);
        var local = sampledNow.ToOffset(offset);
        if (today is null && nowMinutes is null) return sampledNow;
        var date = today is null ? DateOnly.FromDateTime(local.DateTime)
            : DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (nowMinutes is < 0 or >= 1440) throw new ArgumentOutOfRangeException(nameof(nowMinutes));
        var ticks = nowMinutes is { } minutes ? TimeSpan.FromMinutes(minutes).Ticks : local.TimeOfDay.Ticks;
        return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue).AddTicks(ticks), offset);
    }
}
