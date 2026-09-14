using Tjt.Core;
using Xunit;

namespace Tjt.Core.Tests;

/// <summary>
/// 冲突检测与勾选三态语义的移植验收：逐条对齐 TS 侧
/// <c>packages/core/test/conflict.spec.ts</c> 的期望值。
///
/// <c>toggleCourse</c> 的 <c>selected / switched / blocked</c> 是与历史 HTML 基准
/// <c>select_preview.html</c> 的 <c>tryToggle</c> 绑定的契约，这里的断言即为该契约的黄金副本 ——
/// 任何一条语义漂移都必须在这里暴露。
/// </summary>
public class ConflictTests
{
    private static readonly uint All = Weeks.FromWeeks(Enumerable.Range(1, 16));
    private static readonly uint Odd = Weeks.FromWeeks([1, 3, 5, 7, 9, 11, 13, 15]);
    private static readonly uint Even = Weeks.FromWeeks([2, 4, 6, 8, 10, 12, 14, 16]);

    /// <summary>构造测试课程：<paramref name="sessions"/> 为 (星期, 起始节, 结束节, 周次掩码)。</summary>
    private static Course MakeCourse(string id, string name, string courseCode, params (Weekday Day, int StartSlot, int EndSlot, uint Weeks)[] sessions) =>
        new(
            id,
            name,
            [],
            [.. sessions.Select((s, i) => new Session($"{id}-{i}", s.Day, s.StartSlot, s.EndSlot, s.Weeks))],
            CourseCode: courseCode);

    // ── 基准版 toggleCourse 的固定夹具（与 TS 测试同名同参数） ──

    private static readonly Course Math1 = MakeCourse("math-1", "高等数学", "M1", (Weekday.Monday, 1, 2, All));
    private static readonly Course Math2 = MakeCourse("math-2", "高等数学", "M1", (Weekday.Monday, 3, 4, All));
    private static readonly Course Physics = MakeCourse("phy-1", "大学物理", "P1", (Weekday.Monday, 1, 2, All));
    private static readonly Course[] Pool = [Math1, Math2, Physics];

    [Fact]
    public void 同天同节次同周次_冲突()
    {
        var a = MakeCourse("a", "高等数学", "M1", (Weekday.Monday, 1, 2, All));
        var b = MakeCourse("b", "大学物理", "P1", (Weekday.Monday, 2, 3, All));
        Assert.True(Conflict.SessionsOverlap(a.Sessions[0], b.Sessions[0]));
        Assert.True(Conflict.CoursesConflict(a, b));
    }

    [Fact]
    public void 单双周错开不算冲突_与基准版一致()
    {
        var a = MakeCourse("a", "体育", "PE", (Weekday.Wednesday, 5, 6, Odd));
        var b = MakeCourse("b", "英语", "EN", (Weekday.Wednesday, 5, 6, Even));
        Assert.False(Conflict.CoursesConflict(a, b));
    }

    [Fact]
    public void 同一门课的不同教学班不算冲突()
    {
        var a1 = MakeCourse("a1", "高等数学", "M1", (Weekday.Monday, 1, 2, All));
        var a2 = MakeCourse("a2", "高等数学", "M1", (Weekday.Monday, 1, 2, All));
        Assert.False(Conflict.CoursesConflict(a1, a2));
    }

    [Fact]
    public void 节次相接但不重叠_不冲突()
    {
        var a = MakeCourse("a", "A", "A", (Weekday.Tuesday, 1, 2, All));
        var b = MakeCourse("b", "B", "B", (Weekday.Tuesday, 3, 4, All));
        Assert.False(Conflict.CoursesConflict(a, b));
    }

    [Fact]
    public void findConflicts_返回全部冲突课程()
    {
        var target = MakeCourse("t", "T", "T", (Weekday.Thursday, 5, 7, All));
        var a = MakeCourse("a", "A", "A", (Weekday.Thursday, 5, 6, All));
        var b = MakeCourse("b", "B", "B", (Weekday.Thursday, 7, 8, All));
        var c = MakeCourse("c", "C", "C", (Weekday.Friday, 5, 6, All));
        Assert.Equal(["a", "b"], Conflict.FindConflicts(target, [a, b, c]).Select(x => x.Id));
    }

    // ── toggleCourse（复刻基准版 tryToggle） ──

    [Fact]
    public void 选中()
    {
        var added = Conflict.ToggleCourse(Math1, [], Pool);
        Assert.Equal(ToggleAction.Added, added.Action);
        Assert.Equal(["math-1"], added.Selected);
        Assert.Null(added.Conflict);
        Assert.Null(added.Replaced);
    }

    [Fact]
    public void 取消()
    {
        var added = Conflict.ToggleCourse(Math1, [], Pool);
        var removed = Conflict.ToggleCourse(Math1, added.Selected, Pool);
        Assert.Equal(ToggleAction.Removed, removed.Action);
        Assert.Empty(removed.Selected);
        Assert.Null(removed.Replaced);
    }

    [Fact]
    public void 冲突时拦截且已选不变()
    {
        var result = Conflict.ToggleCourse(Physics, ["math-1"], Pool);
        Assert.Equal(ToggleAction.Blocked, result.Action);
        Assert.Equal("math-1", result.Conflict?.Id);
        Assert.Equal(["math-1"], result.Selected);
        Assert.Null(result.Replaced);
    }

    [Fact]
    public void 同课程换班_switched_无冲突时()
    {
        var result = Conflict.ToggleCourse(Math2, ["math-1"], Pool);
        Assert.Equal(ToggleAction.Switched, result.Action);
        Assert.Equal("math-1", result.Replaced?.Id);
        Assert.Equal(["math-2"], result.Selected);
        Assert.Null(result.Conflict);
    }

    [Fact]
    public void 换班会撞其它课_整体不动()
    {
        var other = MakeCourse("chem-1", "大学化学", "C1", (Weekday.Monday, 3, 4, All));
        var result = Conflict.ToggleCourse(Math2, ["math-1", "chem-1"], [.. Pool, other]);
        Assert.Equal(ToggleAction.Blocked, result.Action);
        Assert.Equal("chem-1", result.Conflict?.Id);
        Assert.Equal(["chem-1", "math-1"], result.Selected.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Null(result.Replaced);
    }

    [Fact]
    public void 池里找不到的已选id被跳过且不抛异常()
    {
        // 对应 TS 的 `const other = byId.get(id); if (other && …)`：幽灵 id 不参与冲突判定
        // （所以即便它其实与目标同天同节次也不会误判 blocked），也不抛异常；
        // 它仍原样留在结果集合里，且结果保留传入顺序、新选中的排在末尾。
        var lonely = MakeCourse("lonely", "L", "L", (Weekday.Monday, 1, 2, All));
        var result = Conflict.ToggleCourse(Physics, ["ghost"], [lonely]);
        Assert.Equal(ToggleAction.Added, result.Action);
        Assert.Equal(["ghost", "phy-1"], result.Selected);
    }
}
