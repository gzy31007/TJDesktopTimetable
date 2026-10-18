using Tjt.Core;
using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

public partial class BoardVisualTests
{
    private static BoardState ContentBoard(params Course[] courses) => Layout.BuildBoard(courses, TestTerm,
        new BoardOptions { Today = "2026-09-14", WeekView = WeekView.All, FirstSlot = 1, LastSlot = 5 });

    private static Course ContentCourse(string id, int start = 1, int? end = null,
        Weekday day = Weekday.Monday, string? room = "测试楼101") =>
        new(id, id, [], [new Session("s-" + id, day, start, end ?? start, Weeks.FullMask(16), room)]);

    private static readonly BoardContentLayout MeasuredContent = new((font, semibold, width, text) =>
        semibold ? 32 : text == "1-16" ? 17 : 21);

    private static BoardVisual ContentVisual(BoardState state, double? height = null,
        bool summary = true, int? now = null, double width = 1000, BoardContentLayout? content = null) =>
        BoardVisualBuilder.Build(state, width, false, now, availableHeight: height,
            includeNextClassRow: summary, contentLayout: content ?? MeasuredContent);

    private static void AssertRowGeometry(BoardVisual visual)
    {
        Assert.Equal(visual.Slots.Select(s => s.Index), visual.Rows.Select(r => r.Index));
        var top = 0d;
        for (var i = 0; i < visual.Rows.Count; i++)
        {
            var row = visual.Rows[i];
            Assert.Equal(top, row.Top, 6);
            Assert.True(row.Height >= BoardContentLayout.BaseRowHeight);
            Assert.InRange(visual.Slots[i].Top, row.Top, row.Top + row.Height);
            top += row.Height;
        }
        Assert.Equal(top, visual.Grid.Height, 6);
    }

    [Fact]
    public void 内容入口单节按三行真实量测撑高而不是统一27()
    {
        var visual = ContentVisual(ContentBoard(ContentCourse("单节课")));
        var block = Assert.Single(visual.Blocks);
        var content = Assert.IsType<BlockContentGeometry>(block.Content);
        Assert.Equal(new BlockContentGeometry(32, 21, 17), content);
        Assert.True(block.Frame.Height >= content.NameHeight + content.RoomHeight + content.WeeksHeight + 8);
        Assert.True(visual.Rows[0].Height > BoardContentLayout.BaseRowHeight);
        Assert.Contains(visual.Rows, row => row.Height == BoardContentLayout.BaseRowHeight);
        Assert.Equal(visual.Grid.Top + visual.Rows[0].Top + 1, block.Frame.Top, 6);
        Assert.Equal(visual.Rows[0].Height - 2, block.Frame.Height, 6);
        Assert.Equal(11, BoardContentLayout.TimeFontSize);
        AssertRowGeometry(visual);
    }

    [Fact]
    public void 跨节需求由覆盖行共同承担且不重复计为每行需求()
    {
        var single = ContentVisual(ContentBoard(ContentCourse("单节课")));
        var span = ContentVisual(ContentBoard(ContentCourse("跨节课", 2, 3)));
        var block = Assert.Single(span.Blocks);
        var covered = span.Rows.Where(r => r.Index is 2 or 3).ToArray();
        Assert.Equal(span.Grid.Top + covered[0].Top + 1, block.Frame.Top, 6);
        Assert.Equal(covered.Sum(r => r.Height) - 2, block.Frame.Height, 6);
        Assert.True(block.Frame.Height >= 32 + 21 + 17 + 8);
        Assert.True(covered.Sum(r => r.Height) < single.Rows[0].Height * 2);
        Assert.All(span.Rows.Where(r => r.Index is not (2 or 3)),
            r => Assert.Equal(BoardContentLayout.BaseRowHeight, r.Height));
        AssertRowGeometry(span);
    }

    [Fact]
    public void 单节和交叠跨度都满足各自覆盖行的内容需求()
    {
        var board = ContentBoard(ContentCourse("局部", 2), ContentCourse("前跨", 1, 3, Weekday.Tuesday),
            ContentCourse("后跨", 2, 4, Weekday.Wednesday));
        var visual = ContentVisual(board, content: new((f, bold, w, text) =>
            bold && text.Contains('跨') ? 140 : bold ? 60 : 25));
        foreach (var block in visual.Blocks)
        {
            var source = board.Blocks.Single(b => b.CourseId == block.CourseId);
            var rows = visual.Rows.Where(r => r.Index >= source.StartSlot && r.Index <= source.EndSlot).ToArray();
            Assert.Equal(visual.Grid.Top + rows[0].Top + 1, block.Frame.Top, 6);
            Assert.Equal(rows.Sum(r => r.Height) - 2, block.Frame.Height, 6);
            var content = Assert.IsType<BlockContentGeometry>(block.Content);
            Assert.True(block.Frame.Height >= content.NameHeight + content.RoomHeight + content.WeeksHeight + 8 - 1e-8);
        }
        AssertRowGeometry(visual);
    }

    [Fact]
    public void 并排各块使用自身宽度量测并共享足够的行高()
    {
        var board = ContentBoard(ContentCourse("第一门"), ContentCourse("第二门", room: "高教室"), ContentCourse("第三门"));
        var calls = new List<(double Font, bool Bold, double Width, string Text)>();
        var visual = ContentVisual(board, width: 700, content: new((font, bold, width, text) =>
        {
            calls.Add((font, bold, width, text));
            return text == "高教室" ? 40 : 15;
        }));
        Assert.Equal(3, visual.Blocks.Count);
        foreach (var block in visual.Blocks)
        {
            Assert.True(block.IsStacked);
            Assert.InRange(block.FontSize, 9.5, 12);
            Assert.Equal(BoardVisualBuilder.FontSizeFor(block.Frame.Width), block.FontSize);
            Assert.Contains(calls, c => c.Bold && c.Font == block.FontSize
                && c.Width == block.Frame.Width - 14 && c.Text == block.DisplayName);
            Assert.Contains(calls, c => !c.Bold && c.Font == Math.Max(9, block.FontSize - 0.5)
                && c.Width == block.Frame.Width - 14 && c.Text == block.Room);
            Assert.Contains(calls, c => !c.Bold && c.Font == Math.Max(9, block.FontSize - 1)
                && c.Width == block.Frame.Width - 14 && c.Text == block.WeeksLabel);
            var content = Assert.IsType<BlockContentGeometry>(block.Content);
            Assert.True(block.Frame.Height >= content.NameHeight + content.RoomHeight + content.WeeksHeight + 8);
        }
        var sorted = visual.Blocks.OrderBy(b => b.Frame.Left).ToArray();
        for (var i = 1; i < sorted.Length; i++)
            Assert.True(sorted[i - 1].Frame.Left + sorted[i - 1].Frame.Width <= sorted[i].Frame.Left);
        AssertRowGeometry(visual);
    }

    [Fact]
    public void 稀疏课表保留所有空中间节次且后续块和标签按累加位置()
    {
        var visual = ContentVisual(ContentBoard(ContentCourse("首节"), ContentCourse("末节", 5)));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, visual.Rows.Select(r => r.Index));
        Assert.All(visual.Rows.Where(r => r.Index is 2 or 3 or 4),
            row => Assert.Equal(BoardContentLayout.BaseRowHeight, row.Height));
        var last = visual.Blocks.Single(b => b.CourseId == "末节");
        Assert.Equal(visual.Grid.Top + visual.Rows.Take(4).Sum(r => r.Height) + 1, last.Frame.Top, 6);
        AssertRowGeometry(visual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 无教室仍量测并保留空白中间文字行(string? room)
    {
        var texts = new List<string>();
        var visual = ContentVisual(ContentBoard(ContentCourse("无教室", room: room)),
            content: new((font, bold, width, text) => { texts.Add(text); return text.Length == 0 ? 0 : 20; }));
        var block = Assert.Single(visual.Blocks);
        Assert.Null(block.Room);
        Assert.Contains(" ", texts);
        Assert.Equal(3, texts.Count);
        Assert.Equal(20, block.Content!.RoomHeight);
        Assert.True(block.Frame.Height >= 20 * 3 + 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredHeight全额包含固定区与网格(bool summary)
    {
        var visual = ContentVisual(ContentBoard(ContentCourse("课程")), summary: summary);
        Assert.Equal(6 + 28 + visual.Grid.Height + 6, visual.CanvasHeight, 6);
        Assert.Equal(34 + (summary ? 30 : 0) + 6 + 28 + visual.Grid.Height + 6, visual.RequiredHeight, 6);
        var other = ContentVisual(ContentBoard(ContentCourse("课程")), summary: !summary);
        Assert.Equal(30, Math.Abs(visual.RequiredHeight - other.RequiredHeight));
        Assert.Equal(visual.Rows, other.Rows);
    }

    [Theory]
    [InlineData(-0.01, true)]
    [InlineData(0, false)]
    [InlineData(0.01, false)]
    [InlineData(300, false)]
    public void 低等高门槛只改变临时滚动不压行压字或拉伸(double delta, bool scroll)
    {
        var board = ContentBoard(ContentCourse("单节"), ContentCourse("跨节", 3, 5));
        var basis = ContentVisual(board);
        var visual = ContentVisual(board, height: basis.RequiredHeight + delta);
        Assert.Equal(scroll, visual.NeedsVerticalScroll);
        Assert.Equal(basis.RequiredHeight, visual.RequiredHeight);
        Assert.Equal(basis.CanvasHeight, visual.CanvasHeight);
        Assert.Equal(basis.Rows, visual.Rows);
        Assert.Equal(basis.Blocks, visual.Blocks);
        Assert.False(basis.NeedsVerticalScroll);
    }

    [Theory]
    [InlineData(-0.01, true)]
    [InlineData(0, false)]
    [InlineData(0.01, false)]
    public void 非整数客户区不可绘制留白全额进入需求与低等高门槛(double delta, bool scroll)
    {
        var board = ContentBoard(ContentCourse("单节"), ContentCourse("跨节", 3, 5));
        var basis = ContentVisual(board, now: 502);
        var content = MeasuredContent with { ClientInsetHeight = 2d / 3 };
        var required = basis.RequiredHeight + content.ClientInsetHeight;
        var visual = ContentVisual(board, height: required + delta, now: 502, content: content);
        Assert.Equal(required, visual.RequiredHeight);
        Assert.Equal(scroll, visual.NeedsVerticalScroll);
        Assert.Equal(basis.Rows, visual.Rows);
        Assert.Equal(basis.Geometry, visual.Geometry);
        Assert.Equal(basis.Grid, visual.Grid);
        Assert.Equal(basis.CanvasHeight, visual.CanvasHeight);
        Assert.Equal(basis.CanvasWidth, visual.CanvasWidth);
        Assert.Equal(basis.Slots, visual.Slots);
        Assert.Equal(basis.Blocks, visual.Blocks);
        Assert.Equal(basis.NowLineTop, visual.NowLineTop);
    }

    [Fact]
    public void 客户区留白缺省与显式零保持相同预算()
    {
        var board = ContentBoard(ContentCourse("课"));
        Assert.Equal(0, MeasuredContent.ClientInsetHeight);
        var basis = ContentVisual(board);
        var zero = ContentVisual(board, content: MeasuredContent with { ClientInsetHeight = 0 });
        Assert.Equal(basis.RequiredHeight, zero.RequiredHeight);
        Assert.Equal(basis.Rows, zero.Rows);
        Assert.Equal(basis.CanvasHeight, zero.CanvasHeight);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 客户区留白必须有限非负且空课表也验证(double inset)
    {
        var board = ContentBoard() with { Rows = [] };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ContentVisual(board,
            content: MeasuredContent with { ClientInsetHeight = inset }));
        Assert.Equal(nameof(BoardContentLayout.ClientInsetHeight), error.ParamName);
    }

    [Fact]
    public void 内容量测随列宽改变但不随窗口高度改变()
    {
        var calls = new List<double>();
        var measure = new BoardContentLayout((font, bold, width, text) =>
        { calls.Add(width); return bold ? font * 2 : font; });
        var board = ContentBoard(ContentCourse("宽度"));
        var wide = ContentVisual(board, width: 1200, content: measure);
        var wideCalls = calls.ToArray();
        calls.Clear();
        var narrow = ContentVisual(board, width: 400, content: measure);
        Assert.True(wideCalls.Min() > calls.Max());
        Assert.InRange(narrow.Blocks[0].FontSize, 9.5, wide.Blocks[0].FontSize);
        Assert.True(narrow.NeedsHorizontalScroll);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(502)]
    [InlineData(580)]
    [InlineData(620)]
    public void 非等高时间线节内按当前行插值课间停在上一行底边(int now)
    {
        var visual = ContentVisual(ContentBoard(ContentCourse("首节"), ContentCourse("第三节", 3)), now: now);
        var row = now < 525 ? visual.Rows[0] : now < 600 ? visual.Rows[1] : visual.Rows[2];
        var fraction = now < 525 ? (now - 480d) / 45 : now < 600 ? 1 : (now - 600d) / 45;
        Assert.Equal(visual.Grid.Top + row.Top + row.Height * fraction, visual.NowLineTop!.Value, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(479)]
    [InlineData(856)]
    public void 内容时间线范围外或未给时间不画(int? now)
    {
        var visual = ContentVisual(ContentBoard(ContentCourse("首节")), now: now);
        Assert.Null(visual.NowLineTop);
    }

    [Fact]
    public void 零行或时间缺失时仍可预算固定区且不画时间线()
    {
        var board = ContentBoard();
        var empty = ContentVisual(board with { Rows = [] }, now: 500);
        Assert.Empty(empty.Rows);
        Assert.Equal(0, empty.Grid.Height);
        Assert.Equal(34 + 30 + 6 + 28 + 6, empty.RequiredHeight);
        Assert.Null(empty.NowLineTop);
        var unknown = ContentVisual(board with
        { Rows = board.Rows.Select(r => r with { Begin = "", End = "" }).ToArray() }, now: 500);
        Assert.Null(unknown.NowLineTop);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void 量测返回非法高度时拒绝生成坏几何(double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContentVisual(ContentBoard(ContentCourse("课")),
            content: new((f, b, w, t) => height)));
    }

    [Fact]
    public void 显式内容接口缺少回调时拒绝调用()
    {
        Assert.Throws<ArgumentNullException>(() => ContentVisual(ContentBoard(ContentCourse("课")),
            content: new(null!)));
    }

    [Fact]
    public void 缺省与显式null保留旧等高压缩路径且公开行几何一致()
    {
        var board = ContentBoard(ContentCourse("课程", 1, 2));
        var original = BoardVisualBuilder.Build(board, 1000, false, availableHeight: 200);
        var explicitNull = BoardVisualBuilder.Build(board, 1000, false, availableHeight: 200, contentLayout: null);
        Assert.Equal(34, original.Geometry.RowHeight);
        Assert.All(original.Rows, row => Assert.Equal(34, row.Height));
        Assert.All(original.Blocks, block => Assert.Null(block.Content));
        Assert.Equal(original.Geometry, explicitNull.Geometry);
        Assert.Equal(original.Blocks, explicitNull.Blocks);
        Assert.Equal(original.Slots, explicitNull.Slots);
        Assert.Equal(original.CanvasHeight, explicitNull.CanvasHeight);
        Assert.Equal(original.RequiredHeight, explicitNull.RequiredHeight);
        Assert.Equal(original.CanvasHeight + 34, original.RequiredHeight);
        Assert.Equal(original.NeedsVerticalScroll, explicitNull.NeedsVerticalScroll);
    }
}
