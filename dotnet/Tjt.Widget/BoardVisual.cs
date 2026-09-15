using System.Linq;
using Tjt.Core;

namespace Tjt.Widget;

/// <summary>挂件网格几何（TS 侧 <see cref="Tjt.Core.Layout.FitGeometry"/> 的返回结构；行列数保留以便调用方自查）。</summary>
/// <param name="GutterWidth">左侧节次标签列宽。</param>
/// <param name="CellWidth">单天列宽。</param>
/// <param name="RowHeight">单节行高。</param>
/// <param name="HeaderHeight">表头（星期）高度。</param>
/// <param name="Rows">网格行数。</param>
/// <param name="Cols">网格列数。</param>
public sealed record BoardGeometry(
    double GutterWidth,
    double CellWidth,
    double RowHeight,
    double HeaderHeight,
    int Rows,
    int Cols)
{
    /// <summary>转成核心库的几何类型（<see cref="Layout.BlockRect"/> 只认它）。</summary>
    internal Tjt.Core.BoardGeometry Core => new(GutterWidth, CellWidth, RowHeight, HeaderHeight, Rows, Cols);
}

/// <summary>一个色块在画布上的矩形（与核心库 <see cref="BlockRect"/> 同字段，改个名字避免与 XAML 的 Rect 混淆）。</summary>
/// <param name="Left">左边距。</param>
/// <param name="Top">上边距。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public sealed record BoardFrame(double Left, double Top, double Width, double Height);

/// <summary>列头（星期）一项。</summary>
/// <param name="Day">星期（1 = 周一 … 7 = 周日）。</param>
/// <param name="Label">显示名，例如"周一"。</param>
/// <param name="Left">左边界。</param>
/// <param name="Width">宽度。</param>
/// <param name="IsWeekend">是否周末。</param>
/// <param name="IsToday">是否"今日"。</param>
public sealed record DayHeader(Weekday Day, string Label, double Left, double Width, bool IsWeekend, bool IsToday);

/// <summary>左侧节次标签一项。</summary>
/// <param name="Index">节次序号。</param>
/// <param name="Text">标签全文（如 <c>3 · 09:50</c>；节次表缺时间时为 <c>3</c>）。</param>
/// <param name="Top">标签顶边（在网格内的偏移，已含 0.32 行高下移）。</param>
/// <param name="IsCurrent">是否"正在上"。</param>
public sealed record SlotLabel(int Index, string Text, double Top, bool IsCurrent);

/// <summary>一个课程色块的完整呈现数据（几何 + 文案 + 染色一次算清，渲染层只搬砖）。</summary>
/// <param name="Key">稳定键（画布元素的 key / 调试用）。</param>
/// <param name="CourseId">教学班 id（点击回调用）。</param>
/// <param name="Name">课程全名（tooltip 用）。</param>
/// <param name="DisplayName">按块宽压缩后的显示名（超长带省略号）。</param>
/// <param name="Room">教室（可能为空）。</param>
/// <param name="WeeksLabel">周次标签（如 <c>1-16</c>）。</param>
/// <param name="Tooltip">悬停提示（课程 / 教师 / 教室 / 周次四行）。</param>
/// <param name="Frame">矩形。</param>
/// <param name="FontSize">课程名字号（随块宽自适应）。</param>
/// <param name="IsSpecial">非全周上课 → 虚线描边 + 条纹。</param>
/// <param name="IsStacked">同格并排（&gt;1 块）→ 描边更实，便于分辨边界。</param>
/// <param name="Tint">染色。</param>
public sealed record BlockVisual(
    string Key,
    string CourseId,
    string Name,
    string DisplayName,
    string? Room,
    string WeeksLabel,
    string Tooltip,
    BoardFrame Frame,
    double FontSize,
    bool IsSpecial,
    bool IsStacked,
    BlockTint Tint);

/// <summary>顶部信息条（对应渲染层 WidgetApp.vue 的 <c>.widget-bar</c>）。</summary>
/// <param name="Title">学期名（如 <c>2026-2027学年第1学期</c>）。</param>
/// <param name="WeekText">周次文案（<c>第 3 周</c> / <c>假期</c>）。</param>
/// <param name="TodayText">今日节数文案（如 <c>今日 4 节</c>；今天没课时为 <c>null</c>）。</param>
/// <param name="IsHoliday">是否处于假期（周次为 null）——标题会用弱化色。</param>
public sealed record BoardHeader(string Title, string WeekText, string? TodayText, bool IsHoliday);

/// <summary>
/// 一整块课表的**呈现模型**：从核心库的 <see cref="BoardState"/> 再走一步，算出画布需要的全部
/// 坐标、文案与染色。
///
/// 为什么单独立一层：XAML 渲染代码只能在 Windows 上编译运行，是反馈最慢的一段；
/// 把"算"与"画"拆开后，所有会算错的规则（并排宽度、今日列、字号档位、名称压缩）都在这里，
/// 由 Linux 上的单测钉住，渲染层只剩"照着坐标摆控件"。
/// </summary>
public sealed record BoardVisual(
    string Title,
    BoardHeader Header,
    IReadOnlyList<DayHeader> Days,
    IReadOnlyList<SlotLabel> Slots,
    IReadOnlyList<BlockVisual> Blocks,
    BoardFrame Grid,
    BoardGeometry Geometry,
    bool HasToday,
    double? NowLineTop,
    double CanvasWidth,
    double CanvasHeight,
    bool NeedsHorizontalScroll,
    bool NeedsVerticalScroll);

/// <summary>把 <see cref="BoardState"/> 编译成 <see cref="BoardVisual"/>。</summary>
public static class BoardVisualBuilder
{
    /// <summary>节次标签在行内的下移比例（渲染层 <c>slotTop()</c> 用的 0.32）。</summary>
    private const double SlotLabelTopRatio = 0.32;

    /// <summary>网格下方留白（边框 + 呼吸位，渲染层 canvasHeight 里的 +2）。</summary>
    private const double GridBottomPadding = 6;

    /// <summary>顶部信息条高度（对应渲染层 <c>.widget-bar</c> 的高度）。</summary>
    public const double HeaderHeight = 34;

    /// <summary>行高下限：再小就没法读课程名，宁可让外层滚动。</summary>
    public const double MinRowHeight = 34;

    /// <summary>
    /// 构造呈现模型。
    /// </summary>
    /// <param name="state">核心库的布局结果。</param>
    /// <param name="availableWidth">可用总宽（窗口客户区宽度）。</param>
    /// <param name="dark">是否深色主题。</param>
    /// <param name="nowMinutes">当前分钟数；给了且落在节次范围内才画"当前时间线"。</param>
    /// <param name="minCellWidth">列宽下限（与核心库一致）。</param>
    /// <param name="availableHeight">
    /// 可用总高（含顶部条）；<c>null</c> 表示"不限高"（按列宽算出的行高为准）。
    /// 给了就按它压缩行高，把课表铺满窗口高度。
    /// </param>
    public static BoardVisual Build(
        BoardState state,
        double availableWidth,
        bool dark,
        int? nowMinutes = null,
        double minCellWidth = 72,
        double? availableHeight = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var geometry = FitGeometry(availableWidth, state.Rows.Count, state.Days.Count, minCellWidth, availableHeight);
        var gridHeight = geometry.RowHeight * state.Rows.Count;

        var days = new List<DayHeader>(state.Days.Count);
        for (var index = 0; index < state.Days.Count; index += 1)
        {
            var day = state.Days[index];
            days.Add(new DayHeader(
                day.Day,
                day.Label,
                geometry.GutterWidth + (index * geometry.CellWidth),
                geometry.CellWidth,
                day.Weekend,
                day.IsToday));
        }

        var slots = new List<SlotLabel>(state.Rows.Count);
        for (var position = 0; position < state.Rows.Count; position += 1)
        {
            var row = state.Rows[position];
            var text = string.IsNullOrEmpty(row.Begin) ? $"{row.Index}" : $"{row.Index} · {row.Begin}";
            slots.Add(new SlotLabel(row.Index, text, (position * geometry.RowHeight) + (geometry.RowHeight * SlotLabelTopRatio), row.IsCurrent));
        }

        var blocks = new List<BlockVisual>(state.Blocks.Count);
        foreach (var block in state.Blocks)
        {
            var frame = FrameOf(state, block, geometry.Core);
            var font = FontSizeFor(frame.Width);
            blocks.Add(new BlockVisual(
                // key 必须唯一：同一门课可能在同格有多块（不同教室），所以把上课周次的标签也带上
                Key: $"{block.Day}-{block.StartSlot}-{block.EndSlot}-{block.Col}-{block.CourseId}-{block.WeeksLabel}",
                CourseId: block.CourseId,
                Name: block.Name,
                DisplayName: ShortenName(block.ShortName, frame.Width),
                Room: string.IsNullOrEmpty(block.Room) ? null : block.Room,
                WeeksLabel: block.WeeksLabel,
                Tooltip: TooltipOf(block),
                Frame: frame,
                FontSize: font,
                IsSpecial: block.Special,
                IsStacked: block.Stacked,
                Tint: TintPalette.ForBlock(block.Color, dark)));
        }

        var today = state.Days.FirstOrDefault(day => day.IsToday);
        var canvasWidth = geometry.GutterWidth + (geometry.CellWidth * state.Days.Count);
        var canvasHeight = geometry.HeaderHeight + gridHeight + GridBottomPadding;

        return new BoardVisual(
            Title: state.Title,
            Header: BuildHeader(state, nowMinutes),
            Days: days,
            Slots: slots,
            Blocks: blocks,
            Grid: new BoardFrame(geometry.GutterWidth, geometry.HeaderHeight, geometry.CellWidth * state.Days.Count, gridHeight),
            Geometry: geometry,
            HasToday: today is not null,
            NowLineTop: NowLineTop(state, geometry.Core, nowMinutes),
            CanvasWidth: canvasWidth,
            CanvasHeight: canvasHeight,
            // 横向：可用宽度装不下最小列宽时才滚（与渲染层的 minCellWidth=72 语义一致）；
            // 纵向：行高已经压到下限、还是装不下才滚。
            NeedsHorizontalScroll: canvasWidth > availableWidth + 0.5,
            NeedsVerticalScroll: availableHeight is { } h && canvasHeight + HeaderHeight > h + 0.5);
    }

    /// <summary>
    /// 顶部信息条：学期名 + 周次 + 今日节数（对应渲染层 <c>.widget-bar</c> 的
    /// <c>termName</c> / <c>第 N 周</c> / <c>今日 N 节</c> 三段）。
    /// </summary>
    /// <param name="state">布局结果。</param>
    /// <param name="nowMinutes">当前分钟数；给了才算"今日还剩几节"。</param>
    public static BoardHeader BuildHeader(BoardState state, int? nowMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var week = state.CurrentWeek;
        var weekText = week is null ? "假期" : $"第 {week} 周";

        // 今日节数 = 今天开始的课（与渲染层 todaysSessions(...).length 同义：只看有没有课）
        var today = state.Blocks.Count(block => block.Day == (Weekday)WeekdayOf(state.Today));
        var todayText = today > 0 ? $"今日 {today} 节" : null;
        _ = nowMinutes; // 保留参数：将来要做"还剩 N 节"时用，当前文案与渲染层一致，不依赖当前时刻
        return new BoardHeader(state.Title, weekText, todayText, week is null);
    }

    /// <summary><c>YYYY-MM-DD</c> → 星期（1 = 周一 … 7 = 周日）；解析失败返回 0（不会命中任何天）。</summary>
    private static int WeekdayOf(string iso)
    {
        var weekday = Time.IsoToWeekday(iso);
        return weekday is null ? 0 : (int)weekday.Value;
    }

    /// <summary>
    /// 按可用宽度自适应列宽 —— 与核心库 <see cref="Tjt.Core.Layout.FitGeometry"/> 同一套算式，
    /// 基座几何直接取核心库的 <see cref="Tjt.Core.Layout.DefaultGeometry"/>（与渲染层 <c>DEFAULT_GEOMETRY</c> 同值），
    /// 因此 widget 侧不另立一份几何常量，避免两边悄悄漂移。
    ///
    /// <para><b>纵向自适应是 C# 侧新增的</b>（渲染层只做横向 fit、纵向交给滚动）：窗口高度给了
    /// <paramref name="availableHeight"/> 时，把行高压到刚好铺满可用高度，下限 <see cref="MinRowHeight"/>
    /// —— 行高再小就没法读课程名了，那种情况交给外层滚动。</para>
    /// </summary>
    /// <param name="availableWidth">可用总宽。</param>
    /// <param name="rows">行数（节次数）。</param>
    /// <param name="cols">列数（天数）。</param>
    /// <param name="minCellWidth">列宽下限。</param>
    /// <param name="availableHeight">可用总高（含顶部条）；<c>null</c> = 不限高。</param>
    public static BoardGeometry FitGeometry(
        double availableWidth,
        int rows,
        int cols,
        double minCellWidth = 72,
        double? availableHeight = null)
    {
        var basis = Layout.FitGeometry(availableWidth, rows, cols, baseGeometry: null, minCellWidth: minCellWidth);
        var rowHeight = basis.RowHeight;

        if (availableHeight is { } height && rows > 0)
        {
            var usable = height - HeaderHeight - GridBottomPadding;
            var fitted = Math.Floor(usable / rows);
            if (fitted > 0 && fitted < rowHeight)
            {
                rowHeight = Math.Max(MinRowHeight, fitted);
            }
        }

        return new BoardGeometry(basis.GutterWidth, basis.CellWidth, rowHeight, basis.HeaderHeight, basis.Rows, basis.Cols);
    }

    /// <summary>
    /// 色块矩形 —— 直接复用核心库的 <see cref="Tjt.Core.Layout.BlockRect"/>，保证与 Electron 侧像素一致。
    ///
    /// <see cref="Tjt.Core.Layout.BlockRect"/> 只读几何里的 <c>GutterWidth/CellWidth/HeaderHeight/RowHeight</c>
    /// （行列数只被 <see cref="Tjt.Core.Layout.BoardSize"/> 用），所以可以直接把本类型的几何传进去。
    /// </summary>
    internal static BoardFrame FrameOf(BoardState state, BoardBlock block, Tjt.Core.BoardGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(geometry);
        var rect = Layout.BlockRect(state, block, geometry);
        return new BoardFrame(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>
    /// 课程名字号：<c>clamp(width / 7, 9.5, 12)</c>（渲染层 <c>blockFontSize()</c>）。
    /// 宽度越小字号越小，避免并排的窄块文字溢出。
    /// </summary>
    public static double FontSizeFor(double width) => Math.Max(9.5, Math.Min(12, width / 7));

    /// <summary>
    /// 按块宽压缩课程名（渲染层 <c>blockName()</c> 的档位表）：
    /// &lt;62 → 2 字、&lt;80 → 4 字、&lt;100 → 6 字、否则 8 字，超出加省略号。
    /// 完整名称仍在 tooltip 里，所以这里可以放心截断。
    /// </summary>
    public static string ShortenName(string name, double width)
    {
        ArgumentNullException.ThrowIfNull(name);
        var max = width < 62 ? 2 : width < 80 ? 4 : width < 100 ? 6 : 8;
        return name.Length > max ? string.Concat(name.AsSpan(0, max), "…") : name;
    }

    /// <summary>悬停提示（渲染层 <c>title</c> 属性的四行格式，教师为空时显示破折号）。</summary>
    public static string TooltipOf(BoardBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var teachers = block.Teachers.Count > 0 ? string.Join("、", block.Teachers) : "—";
        var room = string.IsNullOrEmpty(block.Room) ? "—" : block.Room;
        return $"{block.Name}\n教师：{teachers}\n教室：{room}\n周次：{block.WeeksLabel}";
    }

    /// <summary>
    /// "当前时间线"距网格顶部的偏移；不在任何节次范围内（或没有节次）时返回 <c>null</c>。
    ///
    /// 算式与渲染层 <c>nowFraction</c> 一致：按"首节开始 → 末节结束"线性插值，
    /// 而不是按节次分段 —— 课间时间也照样平滑推进。
    /// </summary>
    internal static double? NowLineTop(BoardState state, Tjt.Core.BoardGeometry geometry, int? nowMinutes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(geometry);
        if (nowMinutes is not { } minutes) return null;
        if (state.Rows.Count == 0) return null;
        var first = Time.ToMinutes(state.Rows[0].Begin);
        var last = Time.ToMinutes(state.Rows[^1].End);
        if (first is null || last is null || last <= first) return null;
        if (minutes < first.Value || minutes > last.Value) return null;
        var gridHeight = geometry.RowHeight * state.Rows.Count;
        var fraction = (double)(minutes - first.Value) / (last.Value - first.Value);
        return geometry.HeaderHeight + (fraction * gridHeight);
    }
}
