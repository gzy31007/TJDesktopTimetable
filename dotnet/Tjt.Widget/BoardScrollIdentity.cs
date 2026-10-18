using System.Security.Cryptography;
using System.Text.Json;
using Tjt.Core;

namespace Tjt.Widget;

/// <summary>网格滚动位置的内容身份；与窗口几何、主题及随时间变化的强调状态无关。</summary>
public static class BoardScrollIdentity
{
    /// <summary>
    /// 从实际行列、课程内容及范围参数生成稳定比较键。序列顺序与同格并排位置均参与比较，
    /// 但内部 id、导入元数据、今日/当前周/当前节次、摘要与颜色不参与。
    /// </summary>
    /// <remarks>仅供渲染器在内存中比较；不得写入日志或配置，也不作为跨版本持久化协议。</remarks>
    public static string Create(BoardState state, WeekView weekView, bool showWeekend)
    {
        ArgumentNullException.ThrowIfNull(state);

        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            WeekView = weekView,
            ShowWeekend = showWeekend,
            Days = state.Days.Select(day => new { day.Day, day.Label, day.Weekend }).ToArray(),
            Rows = state.Rows.Select(row => new { row.Index, row.Begin, row.End, row.Label }).ToArray(),
            Blocks = state.Blocks.Select(block => new
            {
                block.Name,
                block.ShortName,
                Room = string.IsNullOrEmpty(block.Room) ? null : block.Room,
                Teachers = block.Teachers.ToArray(),
                block.Weeks,
                block.WeeksLabel,
                block.Special,
                block.Day,
                block.StartSlot,
                block.EndSlot,
                block.Col,
                block.ColCount,
                block.Stacked,
            }).ToArray(),
        });

        // 返回紧凑摘要，不把课程全文作为比较键向渲染层传播。
        return Convert.ToHexString(SHA256.HashData(content));
    }
}
