using Tjt.Core;
using Tjt.Widget;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 挂件视觉层的纯计算验收（`Tjt.Widget`）。
///
/// 这些用例跑在**平台无关**的 net10.0 上，所以能在 Linux 上直接验证 —— 这正是把
/// 几何 / 染色 / 文案从 WinUI 外壳里拆出来的目的：算错的代价是"推 CI 才发现"，
/// 而并排宽度、今日列、字号档位这些是纯数学，本地就该钉死。
///
/// 期望值沿袭已删除的 Electron 渲染层（`TimetableBoard.vue` 的 `blockRect` / `blockFontSize` /
/// `blockName` / `tintStyle`，2026-09-16 随该线删除）与基准版 `select_preview.html`。
/// **这些期望值现在就是挂件视觉的真源**：改 `Tjt.Widget` 的几何/文案/染色时，改的就是这里。
/// </summary>
public class BoardVisualTests
{
    private static readonly Term TestTerm = new(
        Id: "122",
        Name: "2026-2027学年第1学期",
        Year: 2026,
        TermNo: 1,
        TotalWeeks: 16,
        Slots: TimetableDefaults.TongjiSlots.Select(s => s with { }).ToArray(),
        StartDate: "2026-09-14");

    /// <summary>BlockVisual.Key 的前缀：<c>{星期枚举名}-{起节}-{止节}-{并排列}-</c>（今日是周一）。</summary>
    private const string CellKey = "Monday-1-2-";

    private static readonly Course Maths = new(
        Id: "m1",
        Name: "测试高等数学（工科类）",
        Teachers: ["张三(10001)"],
        Sessions: [new Session("s1", Weekday.Monday, 1, 2, Weeks.FullMask(16), "南101")],
        CourseCode: "TST1001");

    private static readonly Course Physics = new(
        Id: "p1",
        Name: "测试大学物理",
        Teachers: ["李四(10002)"],
        Sessions: [new Session("s2", Weekday.Monday, 1, 2, Weeks.OddMask(16), "北201")],
        CourseCode: "TST1002");

    private static readonly Course English = new(
        Id: "e1",
        Name: "测试大学英语",
        Teachers: ["王五(10003)"],
        Sessions: [new Session("s3", Weekday.Monday, 1, 2, Weeks.EvenMask(16), "南305")],
        CourseCode: "TST1003");

    private static BoardVisual Visual(double width, bool dark = false, int? nowMinutes = null) =>
        BoardVisualBuilder.Build(
            Layout.BuildBoard([Maths, Physics, English], TestTerm, new BoardOptions { Today = "2026-09-14" }),
            width,
            dark,
            nowMinutes);

    [Fact]
    public void 几何按可用宽度自适应_与核心库同一套算式()
    {
        var visual = Visual(1000);
        // cellWidth = floor((1000 - 74) / 7) = 132
        Assert.Equal(74d, visual.Geometry.GutterWidth);
        Assert.Equal(132d, visual.Geometry.CellWidth);
        Assert.Equal(52d, visual.Geometry.RowHeight);
        Assert.Equal(28d, visual.Geometry.HeaderHeight);
        Assert.Equal(7, visual.Geometry.Cols);
        Assert.Equal(11, visual.Geometry.Rows);

        // 网格区域：左边距 = gutter，宽 = 列宽 × 天数；顶边 = 画布顶部呼吸位 + 表头行高
        Assert.Equal(74d, visual.Grid.Left);
        Assert.Equal(6d, visual.HeaderTop);
        Assert.Equal(6d + 28d, visual.Grid.Top);
        Assert.Equal(132d * 7, visual.Grid.Width);
        Assert.Equal(52d * 11, visual.Grid.Height);
    }

    [Fact]
    public void 表头与顶部之间留呼吸位且色块随之平移()
    {
        var visual = Visual(1000);

        // 表头文字顶边 = 呼吸位；它下面才是表头行（28dip）与网格线
        Assert.Equal(6d, visual.HeaderTop);
        Assert.Equal(visual.HeaderTop + visual.Geometry.HeaderHeight, visual.Grid.Top);

        // 色块 / 时间线都在同一条平移线上：网格顶 + 行偏移，不会有人漏掉呼吸位
        Assert.All(visual.Blocks, block => Assert.True(block.Frame.Top >= visual.Grid.Top));
        if (visual.NowLineTop is { } now)
        {
            Assert.True(now >= visual.Grid.Top);
            Assert.True(now <= visual.Grid.Top + visual.Grid.Height);
        }
    }

    [Fact]
    public void 列宽有下限_窗口极窄时不塌成零()
    {
        // usable = 100 - 74 = 26 → 26/7 = 3 < 72（默认下限，与渲染层 minCellWidth 同值），取 72
        Assert.Equal(72d, BoardVisualBuilder.FitGeometry(100, 11, 7).CellWidth);
        // 可用宽度小于 gutter 时 usable 被夹到 0，仍走下限
        Assert.Equal(72d, BoardVisualBuilder.FitGeometry(10, 11, 7).CellWidth);
        // 显式给更小的下限时按参数走
        Assert.Equal(64d, BoardVisualBuilder.FitGeometry(100, 11, 7, minCellWidth: 64).CellWidth);
        // cols = 0 时不做除法，直接用核心库基座的列宽
        Assert.Equal(
            Tjt.Core.Layout.DefaultGeometry.CellWidth,
            BoardVisualBuilder.FitGeometry(1000, 0, 0).CellWidth);
    }

    [Fact]
    public void 列头左边界按天递增且今日只有一天()
    {
        var visual = Visual(1000);
        Assert.Equal(7, visual.Days.Count);
        for (var i = 0; i < visual.Days.Count; i += 1)
        {
            Assert.Equal(74 + (132 * i), visual.Days[i].Left);
            Assert.Equal(132d, visual.Days[i].Width);
        }

        Assert.Equal(Weekday.Monday, visual.Days[0].Day);
        Assert.Equal(Weekday.Sunday, visual.Days[6].Day);
        Assert.True(visual.Days[6].IsWeekend);
        // Today = 2026-09-14 是周一
        Assert.True(visual.Days[0].IsToday);
        Assert.True(visual.HasToday);
        Assert.Single(visual.Days, d => d.IsToday);
    }

    [Fact]
    public void 节次标签为序号加开始时间并含下移()
    {
        var visual = Visual(1000);
        Assert.Equal(11, visual.Slots.Count);
        Assert.Equal("1 · 08:00", visual.Slots[0].Text);
        Assert.Equal("3 · 10:00", visual.Slots[2].Text);
        // 顶边 = 行下标 × 行高 + 0.32 行高（渲染层 slotTop）
        Assert.Equal(52 * 0.32, visual.Slots[0].Top, 6);
        Assert.Equal((52 * 10) + (52 * 0.32), visual.Slots[10].Top, 6);
    }

    [Fact]
    public void 节次表缺时间时标签只留序号()
    {
        var sparse = TestTerm with
        {
            Slots = [new Slot(1, string.Empty, string.Empty), new Slot(2, "09:00", "09:45")],
        };
        var visual = BoardVisualBuilder.Build(
            Layout.BuildBoard([Maths], sparse, new BoardOptions { Today = "2026-09-14", FirstSlot = 1, LastSlot = 2 }),
            900,
            dark: false);
        Assert.Equal("1", visual.Slots[0].Text);
        Assert.Equal("2 · 09:00", visual.Slots[1].Text);
    }

    [Fact]
    public void 三块同格并排的矩形等宽不重叠且与基准值一致()
    {
        var visual = Visual(1000);
        var cell = visual.Blocks.Where(b => b.Key.StartsWith(CellKey, StringComparison.Ordinal)).ToList();
        Assert.Equal(3, cell.Count);

        // 按并排列排序：展示顺序统一由 col 决定，与色块的排序键（同起始节次 + 中文课名）解耦
        var rects = cell.OrderBy(b => b.Key.Split('-')[3]).Select(b => b.Frame).ToList();
        Assert.All(rects, r =>
        {
            Assert.Equal(40d, r.Width);
            Assert.Equal(102d, r.Height); // 2 行 × 52 - 2
            Assert.Equal(35d, r.Top);     // 顶部呼吸位 6 + 表头 28 + 1
        });
        // 每块 42px 宽再收 2px，三块依次右移 42
        Assert.Equal(new[] { 77d, 119d, 161d }, rects.Select(r => r.Left).ToArray());
        Assert.All(cell, b => Assert.True(b.IsStacked));
        Assert.Equal(3, cell.Select(b => b.Key).Distinct().Count());
    }

    [Fact]
    public void 色块字号随宽度收敛到上下限()
    {
        // 渲染层 blockFontSize：clamp(width / 7, 9.5, 12)
        Assert.Equal(70d / 7, BoardVisualBuilder.FontSizeFor(70), 6); // 10 → 区间内
        Assert.Equal(9.5d, BoardVisualBuilder.FontSizeFor(10));      // 下限（10/7 ≈ 1.43）
        Assert.Equal(12d, BoardVisualBuilder.FontSizeFor(200));      // 上限
        Assert.Equal(12d, BoardVisualBuilder.FontSizeFor(84));       // 84 / 7 = 12，刚好贴上限

        var visual = Visual(1000);
        // 三块并排宽 40 → 字号 40/7 ≈ 5.71 被夹到 9.5
        Assert.All(visual.Blocks, b => Assert.Equal(9.5d, b.FontSize));
    }

    [Fact]
    public void 课程名按块宽分档压缩()
    {
        var name = "测试高等数学";
        // 档位：<62 → 2；<80 → 4；<100 → 6；否则 8
        Assert.Equal("测试…", BoardVisualBuilder.ShortenName(name, 40));
        Assert.Equal("测试高等…", BoardVisualBuilder.ShortenName(name, 70));
        Assert.Equal("测试高等数学", BoardVisualBuilder.ShortenName(name, 90)); // 6 字刚好放得下
        Assert.Equal("测试高等数学", BoardVisualBuilder.ShortenName(name, 120));
        // 压缩用的是**短名**（已去括号后缀）：12 字 > 8 → 截 8 字
        Assert.Equal("测试高等数学（工…", BoardVisualBuilder.ShortenName("测试高等数学（工科类）", 120));
    }

    [Fact]
    public void 悬停提示含四行且空值用破折号()
    {
        var visual = Visual(1000);
        var maths = visual.Blocks.Single(b => b.CourseId == "m1");
        Assert.Equal("测试高等数学（工科类）\n教师：张三(10001)\n教室：南101\n周次：1-16", maths.Tooltip);

        var noRoom = new Course("n1", "无教室课", [], [new Session("s", Weekday.Monday, 1, 2, Weeks.FullMask(16))]);
        var board = Layout.BuildBoard([noRoom], TestTerm, new BoardOptions { Today = "2026-09-14" });
        var only = BoardVisualBuilder.Build(board, 900, dark: false).Blocks.Single();
        Assert.Null(only.Room);
        Assert.Contains("教室：—", only.Tooltip, StringComparison.Ordinal);
        Assert.Contains("教师：—", only.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void 染色深浅主题取值不同且都是八位十六进制()
    {
        var light = Visual(1000, dark: false).Blocks.Single(b => b.CourseId == "m1").Tint;
        var dark = Visual(1000, dark: true).Blocks.Single(b => b.CourseId == "m1").Tint;

        Assert.NotEqual(light.Tint, dark.Tint);
        Assert.NotEqual(light.Ink, dark.Ink);
        // 深色主题必须白字（深底压原色会糊）
        Assert.Equal("#FFFFFF", dark.Ink);
        Assert.Equal("#17223A", light.Ink);
        // 带透明度的令牌一律 #aarrggbb（8 位）；Ink 是不透明文字色，与渲染层一样保持 #rrggbb（6 位）
        foreach (var value in new[]
                 {
                     light.Tint, light.TintHover, light.Edge, light.EdgeStrong, light.InkSoft, light.Stripe,
                     dark.Tint, dark.TintHover, dark.Edge, dark.EdgeStrong, dark.InkSoft, dark.Stripe,
                 })
        {
            Assert.Matches("^#[0-9A-Fa-f]{8}$", value);
        }

        Assert.Matches("^#[0-9A-Fa-f]{6}$", light.Ink);
        Assert.Matches("^#[0-9A-Fa-f]{6}$", dark.Ink);

        // 透明度按 round(a × 255)：0.13 → 33 / 0.26 → 66 / 0.50 → 128
        // alpha 段大小写固定为小写（同 TS），断言统一转大写再比
        Assert.Equal("21", light.Tint.ToUpperInvariant().Substring(1, 2));
        Assert.Equal("42", light.Edge.ToUpperInvariant().Substring(1, 2));
        Assert.Equal("80", light.EdgeStrong.ToUpperInvariant().Substring(1, 2));
        // 深色主题 0.30 → 77 / 0.45 → 115
        Assert.Equal("4D", dark.Tint.ToUpperInvariant().Substring(1, 2));
        Assert.Equal("73", dark.Edge.ToUpperInvariant().Substring(1, 2));
    }

    [Fact]
    public void 染色基于课程主色_深色主题先提亮()
    {
        var color = Colors.ColorForCourse(Maths.Name).ToUpperInvariant();
        var light = TintPalette.ForBlock(color, dark: false).Tint;
        var dark = TintPalette.ForBlock(color, dark: true).Tint;

        // 浅色：原色 + 21（0.13 × 255）
        Assert.Equal($"#21{color[1..]}", light.ToUpperInvariant());
        // 深色：先 lift(0.45) 再 4D
        var lifted = Colors.Lift(color, 0.45).ToUpperInvariant();
        Assert.Equal($"#4D{lifted[1..]}", dark.ToUpperInvariant());
        // 深色主题确实经过提亮：通道值不小于原色
        Assert.True(string.CompareOrdinal(lifted, color) > 0);
    }

    [Fact]
    public void rgba把透明度放到最前且支持无井号输入()
    {
        // 逐位同 TS（hex 部分保持原样大小写、alpha 小写），断言按大小写无关比较
        Assert.Equal("#802563EB", TintPalette.Rgba("#2563eb", 128d / 255d).ToUpperInvariant());
        Assert.Equal("#FF2563EB", TintPalette.Rgba("2563eb", 1).ToUpperInvariant());
        Assert.Equal("#002563EB", TintPalette.Rgba("#2563eb", 0).ToUpperInvariant());
        // 越界透明度被夹住
        Assert.Equal("#FF2563EB", TintPalette.Rgba("#2563eb", 5).ToUpperInvariant());
        Assert.Equal("#002563EB", TintPalette.Rgba("#2563eb", -5).ToUpperInvariant());
    }

    [Fact]
    public void 主题令牌按深浅切换()
    {
        Assert.NotEqual(TintPalette.GridLine(dark: false), TintPalette.GridLine(dark: true));
        Assert.NotEqual(TintPalette.Accent(dark: false), TintPalette.Accent(dark: true));
        Assert.NotEqual(TintPalette.Text(dark: false), TintPalette.Text(dark: true));
        Assert.Equal("#0067C0", TintPalette.Accent(dark: false));
        Assert.Equal("#4CC2FF", TintPalette.Accent(dark: true));
    }

    [Fact]
    public void 当前时间线按首末节次线性插值_范围外不画()
    {
        var visual = Visual(1000, nowMinutes: 8 * 60); // 第 1 节起点
        Assert.Equal(34d, visual.NowLineTop!.Value, 6); // 网格顶部（顶部呼吸位 6 + 表头 28）

        visual = Visual(1000, nowMinutes: (8 * 60) + 45); // 第 1 节终点，仍在网格内
        Assert.NotNull(visual.NowLineTop);

        // 早于首节 / 晚于末节 / 未给时间 → 不画
        Assert.Null(Visual(1000, nowMinutes: 6 * 60).NowLineTop);
        Assert.Null(Visual(1000, nowMinutes: 23 * 60).NowLineTop);
        Assert.Null(Visual(1000).NowLineTop);
    }

    [Fact]
    public void 呈现模型保留主色与周次标签()
    {
        var visual = Visual(1000);
        var maths = visual.Blocks.Single(b => b.CourseId == "m1");
        Assert.Equal("1-16", maths.WeeksLabel);
        Assert.False(maths.IsSpecial);
        Assert.Equal("测试高等数学（工科类）", maths.Name);
        // DisplayName 是按块宽（40px）压缩后的显示名，不是原始短名；完整名在 Name/Tooltip 里
        Assert.Equal("测试…", maths.DisplayName);
        Assert.Equal("测试高等数学（工科类）", maths.Name);

        var physics = visual.Blocks.Single(b => b.CourseId == "p1");
        Assert.Equal("1, 3, 5, 7, 9, 11, 13, 15", physics.WeeksLabel);
        Assert.True(physics.IsSpecial);
    }
    // ── 顶部信息条与自适应（2026-09-15 新增）────────────────────────────────────

    [Fact]
    public void 顶部条给出学期周次与今日节数()
    {
        // 2026-09-14 是第 1 周周一：三块课都在周一（1-2 节同格三块）
        var visual = BoardVisualBuilder.Build(
            Layout.BuildBoard([Maths, Physics, English], TestTerm, new BoardOptions { Today = "2026-09-14" }),
            1000,
            dark: false);

        Assert.Equal("2026-2027学年第1学期", visual.Header.Title);
        Assert.Equal("第 1 周", visual.Header.WeekText);
        Assert.Equal("今日 3 节", visual.Header.TodayText);
        Assert.False(visual.Header.IsHoliday);
    }

    [Fact]
    public void 顶部条_假期与没课的今天()
    {
        // 开学前 → 假期
        var holiday = BoardVisualBuilder.Build(
            Layout.BuildBoard([Maths], TestTerm, new BoardOptions { Today = "2026-08-31" }),
            1000,
            dark: false);
        Assert.Equal("假期", holiday.Header.WeekText);
        Assert.True(holiday.Header.IsHoliday);

        // 周六没课 → 不显示"今日 N 节"，但仍然是第 1 周
        var saturday = BoardVisualBuilder.Build(
            Layout.BuildBoard([Maths], TestTerm, new BoardOptions { Today = "2026-09-19" }),
            1000,
            dark: false);
        Assert.Equal("第 1 周", saturday.Header.WeekText);
        Assert.Null(saturday.Header.TodayText);
        Assert.False(saturday.Header.IsHoliday);
    }

    [Fact]
    public void 给了可用高度就把行高压到刚好铺满()
    {
        var board = Layout.BuildBoard([Maths, Physics, English], TestTerm, new BoardOptions { Today = "2026-09-14" });
        // 可用高度 590 → 每行 (590 - 顶部信息条 34 - 画布顶部呼吸位 6 - 底部留白 6) / 11 = 49
        // （表头 28dip 不入这个算式 —— 既有语义是"尽力铺满、略溢出交给外层滚动"）
        var geometry = BoardVisualBuilder.FitGeometry(1000, board.Rows.Count, board.Days.Count, availableHeight: 590);
        Assert.Equal(49d, geometry.RowHeight);

        // 行高只受高度约束影响，列宽不受影响
        Assert.Equal(132d, geometry.CellWidth);
    }

    [Fact]
    public void 行高被压到下限后不再变窄()
    {
        var board = Layout.BuildBoard([Maths], TestTerm, new BoardOptions { Today = "2026-09-14" });
        // 可用高度 200：算出 (200 - 34 - 6 - 6) / 11 = 14 < 下限 34 → 取 34，超出的部分交给外层滚动
        var geometry = BoardVisualBuilder.FitGeometry(1000, board.Rows.Count, board.Days.Count, availableHeight: 200);
        Assert.Equal(BoardVisualBuilder.MinRowHeight, geometry.RowHeight);

        var visual = BoardVisualBuilder.Build(board, 1000, dark: false, availableHeight: 200);
        Assert.True(visual.NeedsVerticalScroll);
        Assert.Equal(34 * 11, visual.Grid.Height);
    }

    [Fact]
    public void 不给可用高度时行高保持基座值()
    {
        var board = Layout.BuildBoard([Maths], TestTerm, new BoardOptions { Today = "2026-09-14" });
        Assert.Equal(
            Tjt.Core.Layout.DefaultGeometry.RowHeight,
            BoardVisualBuilder.FitGeometry(1000, board.Rows.Count, board.Days.Count).RowHeight);
        Assert.False(BoardVisualBuilder.Build(board, 1000, dark: false).NeedsVerticalScroll);
    }

    [Fact]
    public void 高度充裕时不回弹超过基座行高()
    {
        var board = Layout.BuildBoard([Maths], TestTerm, new BoardOptions { Today = "2026-09-14" });
        // 可用高度很高：只压缩、不拉伸 —— 拉伸会让色块变成大色板，与渲染层观感不一致
        var geometry = BoardVisualBuilder.FitGeometry(1000, board.Rows.Count, board.Days.Count, availableHeight: 4000);
        Assert.Equal(Tjt.Core.Layout.DefaultGeometry.RowHeight, geometry.RowHeight);
    }

    [Fact]
    public void 画布尺寸与滚动标记()
    {
        var board = Layout.BuildBoard([Maths, Physics, English], TestTerm, new BoardOptions { Today = "2026-09-14" });

        // 宽 1000 → 列宽 132，画布宽 74 + 132*7 = 998 ≤ 1000 → 不需要横向滚动
        var wide = BoardVisualBuilder.Build(board, 1000, dark: false, availableHeight: 760);
        Assert.Equal(74d + (132d * 7), wide.CanvasWidth);
        // 高 = 顶部呼吸位 + 表头 + 网格 + 底部留白
        Assert.Equal(6d + 28d + (52d * 11) + 6d, wide.CanvasHeight);
        Assert.False(wide.NeedsHorizontalScroll);
        Assert.False(wide.NeedsVerticalScroll);

        // 窗口被拖窄：列宽撞到下限 72，画布 74 + 72*7 = 578 > 400 → 需要横向滚动
        var narrow = BoardVisualBuilder.Build(board, 400, dark: false, availableHeight: 760);
        Assert.Equal(72d, narrow.Geometry.CellWidth);
        Assert.True(narrow.NeedsHorizontalScroll);
    }

    [Fact]
    public void 窗口变矮时行高随之下调且画布高度同步()
    {
        var board = Layout.BuildBoard([Maths], TestTerm, new BoardOptions { Today = "2026-09-14" });
        var tall = BoardVisualBuilder.Build(board, 1000, dark: false, availableHeight: 700);
        var compact = BoardVisualBuilder.Build(board, 1000, dark: false, availableHeight: 500);

        Assert.True(compact.Geometry.RowHeight < tall.Geometry.RowHeight);
        Assert.True(compact.CanvasHeight < tall.CanvasHeight);
        // 色块高度也跟着行高走（2 行 - 2px）
        Assert.True(compact.Blocks[0].Frame.Height < tall.Blocks[0].Frame.Height);
    }
}
