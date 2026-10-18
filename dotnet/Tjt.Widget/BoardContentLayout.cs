using Tjt.Core;

namespace Tjt.Widget;

/// <summary>一行节次的实际几何。Top 相对网格顶边，不包含星期头。</summary>
public sealed record BoardRowGeometry(int Index, double Top, double Height);

/// <summary>色块三行文字的量测高度，均不含边框和内边距。空教室仍占一行。</summary>
public sealed record BlockContentGeometry(double NameHeight, double RoomHeight, double WeeksHeight);

/// <summary>
/// 显式启用内容布局。外壳以实际字体量测单行、不换行的文字高度，返回有限正数 DIP。
/// 参数依次为字号、是否半粗体、文字可用宽度、显示文本；空教室传单空格以保留中间行。
/// 本库只计算几何，不依赖任何 UI 框架，也不根据可用窗口高度缩字或压缩内容。
/// </summary>
/// <param name="MeasureLineHeight">外壳提供的单行文字高度量测。</param>
/// <param name="ClientInsetHeight">外壳量测的客户区不可绘制纵向留白，单位 DIP，有限且非负。只增加客户区高度需求，不改变画布几何。</param>
public sealed record BoardContentLayout(Func<double, bool, double, string, double> MeasureLineHeight,
    double ClientInsetHeight = 0)
{
    /// <summary>未被内容需求撑大的节次基础候选高度，不是所有行的统一高度。</summary>
    public const double BaseRowHeight = 27;

    /// <summary>内容布局的节次时间字号，外壳固定使用，不随窗口或行高缩小。</summary>
    public const double TimeFontSize = 11;
}

public static partial class BoardVisualBuilder
{
    // BorderThickness 1 + Padding 3，上下各一份。块本身另留上下各 1 DIP 网格间隙。
    private const double BlockVerticalInsets = 8;
    private const double BlockVerticalGap = 2;

    private static BoardVisual BuildContent(BoardState state, double availableWidth, bool dark,
        int? nowMinutes, double minCellWidth, double? availableHeight, bool includeNextClassRow,
        BoardContentLayout contentLayout)
    {
        ArgumentNullException.ThrowIfNull(contentLayout.MeasureLineHeight);
        if (!double.IsFinite(contentLayout.ClientInsetHeight) || contentLayout.ClientInsetHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(BoardContentLayout.ClientInsetHeight),
                "客户区不可绘制留白必须是有限非负数。");
        // 横向与文案共用旧路径；不把窗口高度带入旧的压缩分支。
        var basis = Build(state, availableWidth, dark, nowMinutes, minCellWidth,
            includeNextClassRow: includeNextClassRow);
        var heights = Enumerable.Repeat(BoardContentLayout.BaseRowHeight, state.Rows.Count).ToArray();
        var measured = new List<(BlockVisual Visual, int[] Covered, BlockContentGeometry Content)>();
        for (var index = 0; index < state.Blocks.Count; index++)
        {
            var source = state.Blocks[index];
            var covered = Enumerable.Range(0, state.Rows.Count).Where(i =>
                state.Rows[i].Index >= source.StartSlot && state.Rows[i].Index <= source.EndSlot).ToArray();
            if (covered.Length == 0) continue;
            var block = basis.Blocks[index];
            var width = Math.Max(0, block.Frame.Width - BlockTextInsets);
            double Measure(double font, bool semibold, string text)
            {
                var result = contentLayout.MeasureLineHeight(font, semibold, width, text);
                if (!double.IsFinite(result) || result <= 0)
                    throw new ArgumentOutOfRangeException(nameof(contentLayout),
                        "文字单行量测高度必须是有限正数。");
                return result;
            }
            var content = new BlockContentGeometry(
                Measure(block.FontSize, true, block.DisplayName),
                Measure(Math.Max(9, block.FontSize - 0.5), false, string.IsNullOrEmpty(block.Room) ? " " : block.Room),
                Measure(Math.Max(9, block.FontSize - 1), false, block.WeeksLabel));
            measured.Add((block, covered, content));
        }

        // 先满足局部需求，再满足跨节需求。跨度只约束覆盖行的总预算，不把整块需求重复塞进每行。
        foreach (var item in measured.OrderBy(item => item.Covered.Length))
        {
            var required = item.Content.NameHeight + item.Content.RoomHeight + item.Content.WeeksHeight
                + BlockVerticalInsets + BlockVerticalGap;
            var deficit = required - item.Covered.Sum(i => heights[i]);
            if (deficit <= 0) continue;
            foreach (var i in item.Covered) heights[i] += deficit / item.Covered.Length;
        }

        var rows = new List<BoardRowGeometry>(state.Rows.Count);
        var gridHeight = 0d;
        for (var i = 0; i < heights.Length; i++)
        {
            rows.Add(new BoardRowGeometry(state.Rows[i].Index, gridHeight, heights[i]));
            gridHeight += heights[i];
        }
        var blocks = measured.Select(item => item.Visual with
        {
            Frame = item.Visual.Frame with
            {
                Top = basis.Grid.Top + rows[item.Covered[0]].Top + 1,
                Height = item.Covered.Sum(i => heights[i]) - BlockVerticalGap,
            },
            Content = item.Content,
        }).ToArray();
        var slots = basis.Slots.Select((slot, i) => slot with
        {
            Top = rows[i].Top + rows[i].Height * SlotLabelTopRatio,
        }).ToArray();
        var canvasHeight = CanvasTopPadding + basis.Geometry.HeaderHeight + gridHeight + GridBottomPadding;
        var requiredHeight = HeaderHeight + (includeNextClassRow ? NextClassSummary.RowHeight : 0)
            + canvasHeight + contentLayout.ClientInsetHeight;
        return basis with
        {
            Geometry = basis.Geometry with { RowHeight = BoardContentLayout.BaseRowHeight },
            Rows = rows,
            Slots = slots,
            Blocks = blocks,
            Grid = basis.Grid with { Height = gridHeight },
            CanvasHeight = canvasHeight,
            RequiredHeight = requiredHeight,
            NeedsVerticalScroll = availableHeight is { } height && height < requiredHeight,
            NowLineTop = ContentNowLineTop(state, rows, basis.Grid.Top, nowMinutes),
        };
    }

    private static double? ContentNowLineTop(BoardState state, IReadOnlyList<BoardRowGeometry> rows,
        double gridTop, int? nowMinutes)
    {
        if (nowMinutes is not { } minutes || rows.Count == 0) return null;
        var firstBegin = Time.ToMinutes(state.Rows[0].Begin);
        var lastEnd = Time.ToMinutes(state.Rows[^1].End);
        if (firstBegin is null || lastEnd is null || lastEnd <= firstBegin
            || minutes < firstBegin.Value || minutes > lastEnd.Value) return null;
        double? previousEnd = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var begin = Time.ToMinutes(state.Rows[i].Begin);
            var end = Time.ToMinutes(state.Rows[i].End);
            if (begin is null || end is null || end <= begin) continue;
            if (minutes >= begin.Value && minutes <= end.Value)
                return gridTop + rows[i].Top + (double)(minutes - begin.Value) / (end.Value - begin.Value) * rows[i].Height;
            if (end.Value <= minutes) previousEnd = gridTop + rows[i].Top + rows[i].Height;
        }
        return previousEnd;
    }
}
