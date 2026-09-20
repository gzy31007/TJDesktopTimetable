using System.Globalization;
using System.Text.RegularExpressions;

namespace Tjt.Core;

/// <summary>
/// 课表网格几何（TS 侧 <c>BoardGeometry</c>）。
///
/// <see cref="Rows"/>/<see cref="Cols"/> 给了默认值，因此同一个类型也能表达 TS 的
/// <c>DEFAULT_GEOMETRY: Omit&lt;BoardGeometry, 'rows' | 'cols'&gt;</c>（见 <see cref="Layout.DefaultGeometry"/>，
/// 行列留 0）—— 这样 <c>fitGeometry</c> 的基座参数只需要一个类型，调用方不必认识第二个 record。
///
/// 像素量一律用 <see cref="double"/>：TS 的 number 就是双精度，渲染层量出来的可用宽度可能是小数，
/// 换成 int 会在 <c>fitGeometry</c> 的 <c>Math.floor</c> 之前就截断，两端列宽会不一致。
/// </summary>
/// <param name="GutterWidth">左侧节次标签列宽。</param>
/// <param name="CellWidth">单天列宽。</param>
/// <param name="RowHeight">单节行高。</param>
/// <param name="HeaderHeight">表头（星期）高度。</param>
/// <param name="Rows">网格行数（几何基座里留 0）。</param>
/// <param name="Cols">网格列数（几何基座里留 0）。</param>
public sealed record BoardGeometry(double GutterWidth, double CellWidth, double RowHeight, double HeaderHeight, int Rows = 0, int Cols = 0);

/// <summary>布局选项（TS 侧 <c>BoardOptions</c>）；不传字段时按 TS 的默认值处理。</summary>
public sealed record BoardOptions
{
    /// <summary>展示哪些天（默认全周；<c>false</c> 时只到周五）。</summary>
    public bool? ShowWeekend { get; init; }

    /// <summary>
    /// 周次视图：全部 / 本周 / 单周 / 双周（默认 <see cref="WeekView.Current"/>，即"只看本周"）。
    ///
    /// <para>默认值是产品决策（ADR 0001）：课表就该是"这一周"。开学前 / 学期结束后 / 开学日未知时，
    /// <see cref="Weeks.ResolveFilter"/> 返回 <c>null</c> → 静默退回显示全部周次，挂件绝不空。</para>
    /// </summary>
    public WeekView WeekView { get; init; } = WeekView.Current;

    /// <summary>是否把节次范围自动收窄到"有课的范围"。</summary>
    public bool TrimEmptySlots { get; init; }

    /// <summary>只显示从第几节开始（优先于自动收窄）。</summary>
    public int? FirstSlot { get; init; }

    /// <summary>只显示到第几节为止（优先于自动收窄）。</summary>
    public int? LastSlot { get; init; }

    /// <summary>用于"今日"高亮的 <c>YYYY-MM-DD</c>；不传则取系统当前日期（北京时区）。</summary>
    public string? Today { get; init; }

    /// <summary>当前时刻（用于标记"正在上的节次"）；不传则取系统当前时刻。</summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>时区偏移（分钟），默认北京时间（+480）。</summary>
    public int TzOffsetMinutes { get; init; } = TimetableModel.DefaultTzOffsetMinutes;

    /// <summary>课程取色函数；不传则用 <see cref="Colors.ColorForCourse"/>（按课程名稳定哈希）。</summary>
    public Func<Course, string>? ColorOf { get; init; }

    /// <summary>课程名压缩；不传则用 <see cref="Layout.DefaultShortName"/>（去掉全角/半角括号后缀）。</summary>
    public Func<string, string>? ShortName { get; init; }
}

/// <summary>表头中的一天。</summary>
/// <param name="Day">星期（1 = 周一 … 7 = 周日）。</param>
/// <param name="Label">显示名，例如"周一"。</param>
/// <param name="Weekend">是否周末。</param>
/// <param name="IsToday">是否"今日"。</param>
public sealed record BoardDay(Weekday Day, string Label, bool Weekend, bool IsToday);

/// <summary>一行节次。</summary>
/// <param name="Index">节次序号。</param>
/// <param name="Begin">开始时间（<c>HH:mm</c>，节次表里没有则为空串）。</param>
/// <param name="End">结束时间。</param>
/// <param name="Label">显示名，例如"3节"。</param>
/// <param name="IsCurrent">是否"正在上"。</param>
public sealed record BoardRow(int Index, string Begin, string End, string Label, bool IsCurrent);

/// <summary>
/// 一个色块（TS 侧 <c>BoardBlock</c>）。
///
/// <c>Weeks</c> 是周次位掩码（TS 字段 <c>weeks</c>）。该属性名与同命名空间下的周次工具类
/// <c>Tjt.Core.Weeks</c> 同名，但二者不冲突：成员名只在 <see cref="BoardBlock"/> 内部遮蔽类型名，
/// 而本类型没有成员体，静态工具类照常在别处按 <c>Weeks.FormatLabel</c> 这样引用。
/// 可选字段（<see cref="CourseCode"/> / <see cref="TeachingClassCode"/> / <see cref="Room"/>）
/// 用 <c>null</c> 表达 TS 的 <c>undefined</c>。
/// </summary>
/// <param name="CourseId">教学班 id。</param>
/// <param name="CourseCode">课程代码。</param>
/// <param name="TeachingClassCode">教学班代码。</param>
/// <param name="Name">课程全名。</param>
/// <param name="ShortName">压缩后的课程名。</param>
/// <param name="Room">教室。</param>
/// <param name="Teachers">教师列表。</param>
/// <param name="Weeks">周次位掩码。</param>
/// <param name="WeeksLabel">周次标签，例如"1-16"。</param>
/// <param name="Special">非全周上课（单/双/特定周）→ 渲染层加条纹虚线。</param>
/// <param name="Color">主色 <c>#rrggbb</c>。</param>
/// <param name="Fill">半透明填充色 <c>#rrggbbaa</c>，色块背景用。</param>
/// <param name="TextColor">在 <see cref="Color"/> 上可读的文字色。</param>
/// <param name="Day">星期。</param>
/// <param name="StartSlot">起始节次。</param>
/// <param name="EndSlot">结束节次。</param>
/// <param name="Col">同一格内并排位置（0 起）。</param>
/// <param name="ColCount">同一格内并排块数。</param>
/// <param name="Stacked">该格内是否有其它并行块（渲染层用来决定是否加边框）。</param>
public sealed record BoardBlock(
    string CourseId,
    string? CourseCode,
    string? TeachingClassCode,
    string Name,
    string ShortName,
    string? Room,
    IReadOnlyList<string> Teachers,
    uint Weeks,
    string WeeksLabel,
    bool Special,
    string Color,
    string Fill,
    string TextColor,
    Weekday Day,
    int StartSlot,
    int EndSlot,
    int Col,
    int ColCount,
    bool Stacked);

/// <summary>整块课表的布局结果（TS 侧 <c>BoardState</c>）。</summary>
/// <param name="Term">学期。</param>
/// <param name="Title">学期展示名。</param>
/// <param name="Days">表头各天。</param>
/// <param name="Rows">各行节次。</param>
/// <param name="Blocks">全部色块（已排序）。</param>
/// <param name="CurrentWeek">当前教学周（假期为 <c>null</c>）。</param>
/// <param name="Today">"今日"的 <c>YYYY-MM-DD</c>。</param>
/// <param name="HiddenSessions">被周次过滤掉的时段数。</param>
/// <param name="TodaySessionCount">
/// 今天在**当前教学周**里实际要上的安排条数（header 的「今日 N 节」读它）。
///
/// <para>口径与视图无关：<see cref="WeekView.All"/> 下也按当前周算（旧实现数色块，会把别的周的课算进今天）；
/// 假期 / 开学日未知时退回"今天有几条安排"。不数 <see cref="Blocks"/> 的另一个原因：色块还受
/// "隐藏周末"与视图影响，而今日节数不该受它们影响。</para>
/// </param>
public sealed record BoardState(
    Term Term,
    string Title,
    IReadOnlyList<BoardDay> Days,
    IReadOnlyList<BoardRow> Rows,
    IReadOnlyList<BoardBlock> Blocks,
    int? CurrentWeek,
    string Today,
    int HiddenSessions,
    int TodaySessionCount);

/// <summary>网格需要的总尺寸（TS 侧 <c>boardSize</c> 的返回值）。</summary>
/// <param name="Width">总宽。</param>
/// <param name="Height">总高。</param>
public sealed record BoardSize(double Width, double Height);

/// <summary>色块的像素矩形（TS 侧 <c>BlockRect</c>）。</summary>
/// <param name="Left">左边距。</param>
/// <param name="Top">上边距。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public sealed record BlockRect(double Left, double Top, double Width, double Height);

/// <summary>
/// 课表网格的纯数据布局，像素无关（TS 侧 <c>packages/core/src/layout.ts</c> 的移植）。
///
/// 视觉规则对齐 <c>select_preview.html</c>：
/// 7 列（周一…周日），节次从上到下；同一格（同一天 + 同起止节次）的多门课横向并排、宽度 1/n；
/// 非全周上课的课打 <see cref="BoardBlock.Special"/> 标记（渲染层画条纹虚线）。
///
/// <para><b>日期推算复用 <see cref="Time"/></b>：几何与"今日/当前周/当前节次"用的
/// <see cref="Time.IsoToWeekday"/> / <see cref="Time.LocalTodayIso"/> /
/// <see cref="Time.LocalMinutesOfDay"/> / <see cref="Time.TermWeekAt"/> 全部来自 <see cref="Time"/>，
/// 本文件不再内联任何时间实现（TS 侧 layout.ts 从 <c>time.ts</c> import 也是这个形状）。</para>
///
/// <para>唯一有意的语义差异在<b>畸形输入</b>上：<see cref="Time.IsoToDayNumber"/> 对
/// "越界到 <see cref="DateOnly"/> 范围之外"的日期抛 <see cref="ArgumentOutOfRangeException"/>，
/// 而布局绝不该因为一个坏日期就整个崩掉 —— 所以 <see cref="SafeDayNumber"/> /
/// <see cref="SafeWeekday"/> 把异常收敛成 <c>null</c>，等价 TS 侧拿到 <c>NaN</c> 后
/// <c>dayNumberToWeekday(NaN) → null</c> / <c>termWeekAt → null</c> 的表现。</para>
///
/// <para><b>刻意<b>不</b>复用</b> <see cref="Time.ToMinutes"/> 的那一处：节次表时间解析要走
/// TS 侧 layout.ts 私有 <c>toMin</c> 的宽松语义（不校验 23/59 上限），
/// <see cref="Time.ToMinutes"/> 则是 <c>time.ts</c> 的严格语义。两者在
/// <c>"25:00"</c> 这类输入上结果不同，保持各自一致。</para>
/// </summary>
public static partial class Layout
{
    /// <summary>
    /// 默认几何（TS 侧 <c>DEFAULT_GEOMETRY</c>，行列留 0）。
    ///
    /// <para>左侧时间列（gutter）**64**：正好容下 <c>12 · 18:30</c> 这类标签（标签按
    /// <c>GutterWidth - 8</c> 居中，右侧留给"正在上"竖条），再宽就是白占网格的地方
    /// —— 2026-09-15 观感调整把 74 收到 64，列宽随之 +1。</para>
    /// </summary>
    public static readonly BoardGeometry DefaultGeometry = new(64, 118, 52, 28);

    /// <summary>
    /// 按可用宽度自适应列宽（桌面挂件用；列宽不低于 <paramref name="minCellWidth"/>）。
    ///
    /// 完全照抄 TS 的算式：<c>usable = max(0, availableWidth - gutterWidth)</c>，
    /// <c>cellWidth = cols &gt; 0 ? max(minCellWidth, floor(usable / cols)) : base.cellWidth</c>。
    /// </summary>
    /// <param name="availableWidth">可用总宽。</param>
    /// <param name="rows">网格行数。</param>
    /// <param name="cols">网格列数。</param>
    /// <param name="baseGeometry">几何基座；<c>null</c> 用 <see cref="DefaultGeometry"/>。行列数会被参数覆盖。</param>
    /// <param name="minCellWidth">列宽下限。</param>
    public static BoardGeometry FitGeometry(
        double availableWidth,
        int rows,
        int cols,
        BoardGeometry? baseGeometry = null,
        double minCellWidth = 64)
    {
        var basis = baseGeometry ?? DefaultGeometry;
        var usable = Math.Max(0, availableWidth - basis.GutterWidth);
        var cellWidth = cols > 0 ? Math.Max(minCellWidth, Math.Floor(usable / cols)) : basis.CellWidth;
        return new BoardGeometry(basis.GutterWidth, cellWidth, basis.RowHeight, basis.HeaderHeight, rows, cols);
    }

    /// <summary>
    /// 课程名压缩：去掉全角/半角括号后缀并 trim，压缩后为空则退回原名（TS 侧 <c>defaultShortName</c>）。
    /// </summary>
    /// <remarks>
    /// TS 的两条正则都以 <c>$</c>（无 m 标志 = 输入绝对末尾）收尾，C# 里对应的写法是 <c>\z</c>：
    /// 用 <c>$</c> 的话 .NET 还会在末尾换行符之前匹配，语义就偏了。贪心 <c>.*</c> 两端一致
    /// （都匹配到最后一个括号且不跨行）。
    /// <c>Trim()</c> 与 JS <c>trim()</c> 仅在 <c>U+FEFF</c> 这类字符上不同：.NET 的
    /// <c>char.IsWhiteSpace('\uFEFF')</c> 为 false 而 JS 会去掉它（课程名里不可达）。
    /// </remarks>
    public static string DefaultShortName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = FullWidthSuffix().Replace(name, string.Empty);
        trimmed = HalfWidthSuffix().Replace(trimmed, string.Empty).Trim();
        return trimmed.Length > 0 ? trimmed : name;
    }

    /// <summary>
    /// 构建整块课表（TS 侧 <c>buildBoard</c>）。
    ///
    /// 流程与 TS 一一对应：周次过滤并统计隐藏时段 → 生成表头（可隐藏周末）→ 丢掉不在表头里的天
    /// → 算节次范围（可选收窄）→ 生成行 → 同格分组并排 → 生成色块 → 排序。
    /// </summary>
    /// <param name="courses">课程列表。</param>
    /// <param name="term">学期（提供节次表与总周数）。</param>
    /// <param name="options">布局选项；<c>null</c> 等价于 TS 的 <c>{}</c>。</param>
    public static BoardState BuildBoard(IReadOnlyList<Course> courses, Term term, BoardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(courses);
        ArgumentNullException.ThrowIfNull(term);
        var opts = options ?? new BoardOptions();

        // 注意：TS 这里写的是 localTodayIso()（无参），**不读** options.now / options.tzOffsetMinutes，
        // 即"今日"永远按系统时间 + 默认北京时区算。C# 照抄这个既有行为，避免两端"今日"高亮不一致。
        var today = opts.Today ?? Time.LocalTodayIso(DateTimeOffset.UtcNow, TimetableModel.DefaultTzOffsetMinutes);
        var todayWeekday = SafeWeekday(today);
        // 当前教学周要在过滤**之前**算出来：「本周」视图靠它生成掩码（假期为 null → 不过滤，见 Weeks.ResolveFilter）
        var currentWeek = SafeTermWeekAt(term, today);
        var filterMask = Weeks.ResolveFilter(opts.WeekView, currentWeek, term.TotalWeeks);
        var colorOf = opts.ColorOf ?? (course => Colors.ColorForCourse(course.Name));
        var shortName = opts.ShortName ?? DefaultShortName;

        // 今日节数：与视图无关（永远按当前教学周；假期退回"今天的全部安排"），因此单独从 courses 数
        var todaySessionCount = CountTodaySessions(courses, todayWeekday, currentWeek);

        var pending = new List<PendingBlock>();
        var hiddenSessions = 0;
        foreach (var course in courses)
        {
            foreach (var session in course.Sessions)
            {
                if (filterMask is { } mask && (session.Weeks & mask) == 0)
                {
                    hiddenSessions++;
                    continue;
                }
                if (session.Weeks == 0)
                {
                    hiddenSessions++;
                    continue;
                }
                pending.Add(new PendingBlock(course, session));
            }
        }

        var days = new List<BoardDay>();
        foreach (var day in TimetableModel.Weekdays)
        {
            if (opts.ShowWeekend == false && TimetableModel.IsWeekend(day)) continue;
            days.Add(new BoardDay(day, TimetableModel.WeekdayLabel(day), TimetableModel.IsWeekend(day), todayWeekday == day));
        }

        var visibleDays = days.Select(d => d.Day).ToHashSet();
        var visible = pending.Where(p => visibleDays.Contains(p.Session.Day)).ToList();

        var slotNumbers = visible.SelectMany(p => new[] { p.Session.StartSlot, p.Session.EndSlot }).ToList();
        var minSlot = opts.FirstSlot ?? (opts.TrimEmptySlots && slotNumbers.Count > 0 ? slotNumbers.Min() : 1);
        var maxSlot = opts.LastSlot
            ?? (opts.TrimEmptySlots && slotNumbers.Count > 0
                ? slotNumbers.Max()
                : Math.Max(11, slotNumbers.Count > 0 ? slotNumbers.Max() : 0));

        var nowSlot = CurrentSlotIndex(term, Time.LocalMinutesOfDay(opts.Now ?? DateTimeOffset.UtcNow, opts.TzOffsetMinutes));

        var rows = new List<BoardRow>();
        for (var index = minSlot; index <= maxSlot; index++)
        {
            rows.Add(new BoardRow(index, term.SlotBegin(index), term.SlotEnd(index), $"{index}节", nowSlot == index));
        }

        // 同格并排。TS 的 Map 保证按键首次出现的顺序迭代；.NET 的 Dictionary 枚举顺序无契约，
        // 因此额外用 groupOrder 显式记住插入顺序，保证色块生成顺序（连带 col 分配）与 TS 完全一致。
        var groups = new Dictionary<string, List<PendingBlock>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        foreach (var item in visible)
        {
            var key = $"{(int)item.Session.Day}|{item.Session.StartSlot}|{item.Session.EndSlot}";
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                groupOrder.Add(key);
            }
            list.Add(item);
        }

        var blocks = new List<BoardBlock>();
        foreach (var key in groupOrder)
        {
            var group = groups[key];
            var colCount = group.Count;
            for (var col = 0; col < colCount; col++)
            {
                var item = group[col];
                var course = item.Course;
                var session = item.Session;
                var color = course.Color ?? colorOf(course);
                blocks.Add(new BoardBlock(
                    CourseId: course.Id,
                    CourseCode: course.CourseCode,
                    TeachingClassCode: course.TeachingClassCode,
                    Name: course.Name,
                    ShortName: shortName(course.Name),
                    Room: session.Room,
                    Teachers: course.Teachers,
                    Weeks: session.Weeks,
                    WeeksLabel: Weeks.FormatLabel(session.Weeks, term.TotalWeeks),
                    Special: !Weeks.IsAll(session.Weeks, term.TotalWeeks),
                    Color: color,
                    Fill: Colors.WithAlpha(color, 0.8),
                    TextColor: Colors.ReadableTextColor(color),
                    Day: session.Day,
                    StartSlot: session.StartSlot,
                    EndSlot: session.EndSlot,
                    Col: col,
                    ColCount: colCount,
                    Stacked: colCount > 1));
            }
        }

        // TS 的比较器用 || 串起多级键，等价于"按元组排序 + 稳定排序"；LINQ 的 OrderBy/ThenBy 同样是稳定排序。
        // 唯一的差异在 name：JS 的 localeCompare 走 ICU 默认区域，C# 在 InvariantGlobalization 下走
        // InvariantCulture，中文等非拉丁字符的先后可能与 Node 不同（测试不依赖块顺序）。
        var sorted = blocks
            .OrderBy(b => b.StartSlot)
            .ThenBy(b => (int)b.Day)
            .ThenBy(b => b.Name, StringComparer.InvariantCulture)
            .ThenBy(b => b.Col)
            .ToList();

        return new BoardState(term, term.Label, days, rows, sorted, currentWeek, today, hiddenSessions, todaySessionCount);
    }

    /// <summary>
    /// 今天该上几条课（header「今日 N 节」的唯一口径）。
    ///
    /// <para>语义（方案决策 9）：<b>永远按当前教学周算</b>，与周次视图无关；当前周不存在
    /// （假期 / 开学日未知）或越界时退回"今天有几条安排"。空掩码（<c>weeks == 0</c>）的安排永远不算
    /// —— 它在本项目里等于"从不发生"（与 <see cref="BuildBoard"/> 的丢弃口径一致）。</para>
    ///
    /// <para>数的是**安排条数**而不是色块：色块会被"隐藏周末"丢掉、被视图过滤，而今日节数不该受它们影响。</para>
    /// </summary>
    private static int CountTodaySessions(IReadOnlyList<Course> courses, Weekday? todayWeekday, int? currentWeek)
    {
        if (todayWeekday is not { } day) return 0;
        var weekMask = currentWeek is { } week ? Weeks.WeekMask(week) : 0u;
        var filterByWeek = weekMask != 0;

        var count = 0;
        foreach (var course in courses)
        {
            foreach (var session in course.Sessions)
            {
                if (session.Day != day) continue;
                if (session.Weeks == 0) continue;
                if (filterByWeek && (session.Weeks & weekMask) == 0) continue;
                count += 1;
            }
        }

        return count;
    }

    /// <summary>
    /// 当前时刻落在第几节（用于给节次标签加"正在上"标记）；<paramref name="nowMinutes"/> 为
    /// <c>null</c>（TS 的 <c>undefined</c>）时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 这里的时间解析刻意比 <c>time.ts</c> 的 <c>toMinutes</c> 宽松：只认 <c>^(\d{1,2}):(\d{2})</c>，
    /// 不校验 23/59 上限（TS 侧 layout.ts 的私有 <c>toMin</c> 就是这样，与其保持逐条一致）。
    /// </remarks>
    /// <param name="term">学期（提供节次表）。</param>
    /// <param name="nowMinutes">当天分钟数。</param>
    public static int? CurrentSlotIndex(Term term, int? nowMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (nowMinutes is not { } now) return null;
        foreach (var slot in term.Slots)
        {
            var begin = LenientMinutes(slot.Begin);
            var end = LenientMinutes(slot.End);
            if (begin is not { } from || end is not { } to) continue;
            if (now >= from && now <= to) return slot.Index;
        }
        return null;
    }

    /// <summary>网格需要的总尺寸（TS 侧 <c>boardSize</c>）：宽 = 标签列 + 单天列宽 × 天数，高 = 表头 + 行高 × 行数。</summary>
    /// <param name="state">布局结果。</param>
    /// <param name="geometry">网格几何。</param>
    public static BoardSize BoardSize(BoardState state, BoardGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(geometry);
        return new BoardSize(
            geometry.GutterWidth + (geometry.CellWidth * state.Days.Count),
            geometry.HeaderHeight + (geometry.RowHeight * state.Rows.Count));
    }

    /// <summary>
    /// 色块的像素矩形（同一格并排按 1/n 均分），TS 侧 <c>blockRect</c>。
    ///
    /// 与基准版一致：先给整列留 6px 间隙，再按并排数均分，每块再收 2px；找不到天/行时按下标 0 处理。
    /// </summary>
    /// <param name="state">布局结果。</param>
    /// <param name="block">目标色块。</param>
    /// <param name="geometry">网格几何。</param>
    public static BlockRect BlockRect(BoardState state, BoardBlock block, BoardGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(geometry);
        var dayIndex = FindIndex(state.Days, d => d.Day == block.Day);
        var rowIndex = FindIndex(state.Rows, r => r.Index == block.StartSlot);
        var rowSpan = Math.Max(1, block.EndSlot - block.StartSlot + 1);
        var dayPos = dayIndex < 0 ? 0 : dayIndex;
        var rowPos = rowIndex < 0 ? 0 : rowIndex;
        var columnWidth = (geometry.CellWidth - 6) / Math.Max(1, block.ColCount);
        return new BlockRect(
            Left: geometry.GutterWidth + (dayPos * geometry.CellWidth) + 3 + (block.Col * columnWidth),
            Top: geometry.HeaderHeight + (rowPos * geometry.RowHeight) + 1,
            Width: Math.Max(0, columnWidth - 2),
            Height: Math.Max(0, (rowSpan * geometry.RowHeight) - 2));
    }

    /* ------------------------------------------------------------------ 内部类型 */

    /// <summary>待分组的"课程 + 时段"（TS 侧 <c>PendingBlock</c>）。</summary>
    private sealed record PendingBlock(Course Course, Session Session);

    /// <summary>线性查找下标，找不到返回 -1（等价 TS 的 <c>Array.prototype.findIndex</c>）。</summary>
    private static int FindIndex<T>(IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (predicate(list[i])) return i;
        }
        return -1;
    }

    /* ------------------------------------------------ 时间推算：全部委托给 Time（见类文档） */

    /// <summary>
    /// <c>YYYY-MM-DD</c> → UTC 日序号；解析失败<b>或越界</b>返回 <c>null</c>
    /// （把 <see cref="Time.IsoToDayNumber"/> 的 <see cref="ArgumentOutOfRangeException"/> 收敛掉）。
    /// </summary>
    private static int? SafeDayNumber(string iso)
    {
        try
        {
            return Time.IsoToDayNumber(iso);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// 日期 → 星期；解析失败<b>或越界</b>返回 <c>null</c>。
    ///
    /// 走 <see cref="SafeDayNumber"/> 而不是 <see cref="Time.IsoToWeekday"/>，唯一的区别就是
    /// 越界输入在这里不抛异常（同 TS 的 <c>NaN</c> → <c>null</c>）。
    /// </summary>
    private static Weekday? SafeWeekday(string iso)
    {
        var day = SafeDayNumber(iso);
        return day is null ? null : Time.DayNumberToWeekday(day.Value);
    }

    /// <summary>
    /// 当前是第几教学周；开学前、学期结束后、日期畸形/越界都返回 <c>null</c>。
    ///
    /// 归一化与判定逻辑<b>完全</b>走 <see cref="Time.TermWeekAt"/>（包括 <c>StartDate</c> 空值判定），
    /// 这里只负责把它对越界日期的异常收敛成 <c>null</c> —— 布局不该因为一个坏日期整个崩掉。
    /// </summary>
    private static int? SafeTermWeekAt(Term term, string iso)
    {
        try
        {
            return Time.TermWeekAt(term, iso);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>HH:mm</c> → 当天分钟数；不匹配返回 <c>null</c>。
    /// 只做 layout.ts 私有 <c>toMin</c> 的宽松解析（不校验 23/59 上限），**有意**不复用
    /// <see cref="Time.ToMinutes"/>（那是严格语义，见类文档）。
    /// </summary>
    private static int? LenientMinutes(string hhmm)
    {
        var match = HhmmPattern().Match(hhmm.Trim());
        if (!match.Success) return null;
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60)
            + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})")]
    private static partial Regex HhmmPattern();

    [GeneratedRegex(@"（.*）\s*\z")]
    private static partial Regex FullWidthSuffix();

    [GeneratedRegex(@"\(.*\)\s*\z")]
    private static partial Regex HalfWidthSuffix();
}
