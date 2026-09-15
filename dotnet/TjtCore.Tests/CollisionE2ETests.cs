using Tjt.Core;
using Tjt.Core.Adapters;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 端到端黄金验收：**同格撞车**（构造 fixture，非真实抓包）。
///
/// 期望值沿袭已删除的 TS 用例 <c>packages/core/test/e2e-timetable.spec.ts</c> 的「同格撞车」describe。
/// fixture 是 <c>dotnet/fixtures/tongji-2026-1-collision.json</c>（**唯一真源**，由 csproj 的
/// <c>Content Link</c> 复制到测试输出目录）。
///
/// 覆盖布局里唯一无法从真实个人课表稳定复现的路径——同一格真的挤了多门不同的课：
/// <list type="bullet">
/// <item><description>周一 1-2 节三课并排（全周 / 单周 / 双周）→ <c>ColCount = 3</c>、<c>Col = 0..2</c>；</description></item>
/// <item><description>周三 5-6 节两课周次完全不相交（1-8 / 9-16）→ 视觉上照样并排，<b>并排 ≠ 冲突</b>；</description></item>
/// <item><description>周一 1-3 节与 1-2 仅<b>部分</b>重叠 → 分组键是"同天 + 同起止节次"，各自 <c>ColCount = 1</c>；</description></item>
/// <item><description>同课程同格同教室的多条 times 合并成一块（周次取并集）；同格异教室则不合并。</description></item>
/// </list>
/// </summary>
public class CollisionE2ETests
{
    private static readonly string CollisionJson = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tongji-2026-1-collision.json"));

    private static readonly ImportResult Imported = ImportPipeline.ImportTimetable(new ImportInput
    {
        Text = CollisionJson,
        ImportedAt = "2026-09-01T00:00:00.000Z",
    });

    private static readonly Timetable Table = ImportPipeline.MaterializeTimetable(Imported);

    private static readonly BoardState Board = Layout.BuildBoard(Table.Courses, Table.Term, new BoardOptions
    {
        Today = "2026-09-14",
        TrimEmptySlots = true,
    });

    /// <summary>取某一格（同天 + 同起止节次）里的全部块。</summary>
    private static List<BoardBlock> Cell(Weekday day, int startSlot, int endSlot) =>
        [.. Board.Blocks.Where(b => b.Day == day && b.StartSlot == startSlot && b.EndSlot == endSlot)];

    [Fact]
    public void 整体形状_8门课_9条上课安排_10行节次()
    {
        Assert.Equal("tongji-student", Imported.AdapterId);
        Assert.Equal("2026-09-14", Imported.Term.StartDate);
        Assert.Equal(16, Imported.Term.TotalWeeks);
        Assert.Equal(8, Imported.Courses.Count);
        Assert.Equal(9, Table.Courses.Sum(c => c.Sessions.Count));
        Assert.Equal(9, Board.Blocks.Count);
        Assert.Equal(Enumerable.Range(1, 10).ToArray(), Board.Rows.Select(r => r.Index).ToArray());
        // 同格撞车不是诊断项：导入侧只报告门数/时段数，不报冲突
        Assert.Equal(
            new[] { "tongji.personal", "tongji.summary" },
            Imported.Diagnostics.Select(d => d.Code).ToArray());
    }

    [Fact]
    public void 周一1到2节_三门不同的课三列并排()
    {
        var three = Cell(Weekday.Monday, 1, 2).OrderBy(b => b.Col).ToList();
        Assert.Equal(
            new[] { "测试大学物理", "测试大学英语", "测试高等数学（工科类）" },
            three.Select(b => b.Name).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, three.Select(b => b.Col).ToArray());
        Assert.All(three, b =>
        {
            Assert.Equal(3, b.ColCount);
            Assert.True(b.Stacked);
        });
        // 周次互不相同 → 三块里只有全周那块不是 special
        Assert.False(three.Single(b => b.Name == "测试高等数学（工科类）").Special);
        Assert.Equal("1, 3, 5, 7, 9, 11, 13, 15", three.Single(b => b.Name == "测试大学物理").WeeksLabel);
        Assert.Equal("2, 4, 6, 8, 10, 12, 14, 16", three.Single(b => b.Name == "测试大学英语").WeeksLabel);
    }

    [Fact]
    public void 周一1到3节与1到2节只是部分重叠_不算同格()
    {
        var spanning = Assert.Single(Cell(Weekday.Monday, 1, 3));
        Assert.Equal("测试跨节实践（部分重叠）", spanning.Name);
        Assert.Equal(1, spanning.ColCount);
        Assert.False(spanning.Stacked);
        // 它确实和上面三块在同一时间段里（1-3 覆盖 1-2），说明"视觉重叠"不是分组条件
        Assert.Equal(3, Cell(Weekday.Monday, 1, 2).Count);
    }

    [Fact]
    public void 周三5到6节_两课周次完全不相交但视觉上照样并排()
    {
        var pair = Cell(Weekday.Wednesday, 5, 6);
        // 不按 col 顺序断言课名归属：同名次（同起始节次）的排序 tie-break 在 TS 是 localeCompare（ICU 区域），
        // 在 C# 是 InvariantCulture，中文课名的先后本来就可能不同（见 Layout 类文档）；
        // 这里按"课名 → 周次"映射断言，与 col 分配顺序彻底解耦。
        Assert.Equal(2, pair.Count);
        var byName = pair.ToDictionary(b => b.Name, b => b.WeeksLabel);
        Assert.Equal("1-8", byName["测试并行交替甲"]);
        Assert.Equal("9-16", byName["测试并行交替乙"]);
        Assert.Equal(new[] { 2, 2 }, pair.Select(b => b.ColCount).ToArray());
        Assert.Equal(new[] { 0, 1 }, pair.Select(b => b.Col).OrderBy(c => c).ToArray());
        Assert.All(pair, b =>
        {
            Assert.True(b.Stacked);
            Assert.True(b.Special);
        });
    }

    [Fact]
    public void 同课程同格同教室的多条times合并成一块()
    {
        var merged = Cell(Weekday.Friday, 9, 10).Single(b => b.Name == "测试同格多时段合并");
        Assert.Equal("1-16", merged.WeeksLabel);
        Assert.False(merged.Special);
        Assert.Equal(new[] { "甲(10007)", "乙(10008)", "丙(10009)" }, merged.Teachers.ToArray());
        // 三条 times 合并成一块：该课程只有 1 条 session
        Assert.Single(Table.Courses.Single(c => c.Name == "测试同格多时段合并").Sessions);
    }

    [Fact]
    public void 同课程同格但教室不同_不合并()
    {
        var room = Cell(Weekday.Friday, 9, 10)
            .Where(b => b.Name == "测试同格异教室不合并")
            .OrderBy(b => b.Col)
            .ToList();
        Assert.Equal(new[] { "北402", "北403" }, room.Select(b => b.Room).ToArray());
        Assert.Equal("1-16", room.Single(b => b.Room == "北402").WeeksLabel);
        Assert.Equal("1, 3, 5, 7, 9, 11, 13, 15", room.Single(b => b.Room == "北403").WeeksLabel);
    }

    [Fact]
    public void 周五9到10节共三列并排()
    {
        var five = Cell(Weekday.Friday, 9, 10);
        Assert.Equal(new[] { 3, 3, 3 }, five.Select(b => b.ColCount).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, five.Select(b => b.Col).OrderBy(c => c).ToArray());
    }

    [Fact]
    public void 三列并排的像素矩形_等宽不重叠且宽度为正()
    {
        var geometry = Layout.FitGeometry(1000, Board.Rows.Count, Board.Days.Count);
        var rects = Cell(Weekday.Monday, 1, 2)
            .OrderBy(b => b.Col)
            .Select(b => Layout.BlockRect(Board, b, geometry))
            .ToList();

        Assert.All(rects, r =>
        {
            Assert.True(r.Width > 0);
            Assert.True(r.Height > 0);
        });
        // 等宽
        Assert.Single(rects.Select(r => r.Width).Distinct());
        // 严格递增、且后一块的左边不小于前一块的右边 → 不重叠
        Assert.True(rects[1].Left > rects[0].Left);
        Assert.True(rects[2].Left > rects[1].Left);
        Assert.True(rects[1].Left >= rects[0].Left + rects[0].Width);
        Assert.True(rects[2].Left >= rects[1].Left + rects[1].Width);
        // 参考数值：cellWidth = floor((1000-64)/7) = 133 → 列宽 (133-6)/3 = 42.33，色块再收 2px
        Assert.Equal(40.333d, rects[0].Width, 3);
        Assert.Equal(new[] { 67d, 109.333d, 151.667d }, rects.Select(r => Math.Round(r.Left, 3)).ToArray());
        // 高度只由节次跨度决定（1-2 → 2 行）
        Assert.All(rects, r => Assert.Equal(102d, r.Height));
    }

    [Fact]
    public void 单双周过滤下三列并排各自只剩命中周次的块()
    {
        var odd = Layout.BuildBoard(Table.Courses, Table.Term, new BoardOptions
        {
            Today = "2026-09-14",
            WeekFilter = WeekFilter.Odd,
        });
        var oddCell = odd.Blocks
            .Where(b => b.Day == Weekday.Monday && b.StartSlot == 1 && b.EndSlot == 2)
            .ToList();
        Assert.Equal(
            new[] { "测试大学物理", "测试高等数学（工科类）" },
            oddCell.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(oddCell, b => Assert.Equal(2, b.ColCount));
        Assert.True(odd.HiddenSessions > 0);
    }
}
