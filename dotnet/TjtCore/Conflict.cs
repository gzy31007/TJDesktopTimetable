namespace Tjt.Core;

/// <summary>
/// 冲突检测（TS 侧 <c>packages/core/src/conflict.ts</c> 的移植）—— 与历史基准
/// <c>select_preview.html</c> 的行为保持一致：
/// 同一天、节次区间相交、且周次掩码有交集，才算冲突；同一门课的不同教学班互不冲突。
/// </summary>
public static class Conflict
{
    /// <summary>
    /// 两个上课安排是否在同一时段撞车（<c>sessionsOverlap</c>）。
    ///
    /// 三个条件必须同时成立：同一天、节次区间相交（闭区间，相接不算重叠）、
    /// 周次掩码有交集（走 <see cref="Weeks.Overlap"/>，对应 TS 的 <c>weeksOverlap</c>）。
    /// </summary>
    public static bool SessionsOverlap(Session a, Session b)
    {
        if (a.Day != b.Day) return false;
        if (!(a.StartSlot <= b.EndSlot && b.StartSlot <= a.EndSlot)) return false;
        return Weeks.Overlap(a.Weeks, b.Weeks);
    }

    /// <summary>
    /// 是否为「同一门课」（课程代码相同，或本就是同一个教学班）。
    ///
    /// TS 那边写的是 <c>Boolean(a.courseCode) &amp;&amp; a.courseCode === b.courseCode</c>：
    /// 空串与 <c>undefined</c> 一样属于「假值」，因此这里用
    /// <see cref="string.IsNullOrEmpty"/> 判定，保持与 TS 的假值语义逐条一致。
    /// </summary>
    public static bool IsSameCourse(Course a, Course b)
    {
        if (a.Id == b.Id) return true;
        return !string.IsNullOrEmpty(a.CourseCode) && a.CourseCode == b.CourseCode;
    }

    /// <summary>两门课是否存在任一时段撞车；同一门课（<see cref="IsSameCourse"/>）直接不算冲突。</summary>
    public static bool CoursesConflict(Course a, Course b)
    {
        if (IsSameCourse(a, b)) return false;
        foreach (var s1 in a.Sessions)
        {
            foreach (var s2 in b.Sessions)
            {
                if (SessionsOverlap(s1, s2)) return true;
            }
        }
        return false;
    }

    /// <summary>返回与目标课程冲突的所有已选课程（保持 <paramref name="selected"/> 的枚举顺序）。</summary>
    public static List<Course> FindConflicts(Course target, IEnumerable<Course> selected)
    {
        var hits = new List<Course>();
        foreach (var other in selected)
        {
            if (CoursesConflict(target, other)) hits.Add(other);
        }
        return hits;
    }

    /// <summary>
    /// 勾选 / 取消 / 切换一个教学班的结果（复刻基准版 <c>tryToggle</c> 的三态契约）。
    /// </summary>
    /// <param name="Action">本次操作的实际语义。</param>
    /// <param name="Selected">操作后的已选教学班 id 列表（顺序与传入顺序一致，新选中的排在末尾）。</param>
    /// <param name="Conflict"><see cref="ToggleAction.Blocked"/> 时与之冲突的已选课程。</param>
    /// <param name="Replaced"><see cref="ToggleAction.Switched"/> 时被替换掉的同课程教学班。</param>
    public sealed record ToggleResult(
        ToggleAction Action,
        IReadOnlyList<string> Selected,
        Course? Conflict = null,
        Course? Replaced = null);

    /// <summary>
    /// 勾选 / 取消 / 切换一个教学班（复刻 <c>select_preview.html</c> 的 <c>tryToggle</c> 语义）。
    ///
    /// - 已选 → <see cref="ToggleAction.Removed"/>（取消，结果里不再含该 id）；
    /// - 同 <c>courseCode</c> 已选其它教学班 → <see cref="ToggleAction.Switched"/>
    ///   （先摘掉被替换的那个，再验证与其余课程不冲突，冲突则整体不动 = <see cref="ToggleAction.Blocked"/>）；
    /// - 与已选课程时间冲突 → <see cref="ToggleAction.Blocked"/>，已选集合原样返回；
    /// - 其余 → <see cref="ToggleAction.Added"/>。
    ///
    /// <paramref name="pool"/> 用于把 id 还原成课程对象，语义同 TS：<c>pool</c> 里找不到的 id
    /// 在冲突检测中被跳过（不抛异常），但仍原样留在结果集合里。
    /// </summary>
    public static ToggleResult ToggleCourse(
        Course target,
        IEnumerable<string> selectedIds,
        IEnumerable<Course>? pool = null)
    {
        // 保留传入顺序并按 id 去重（对应 TS 的 new Set(selectedIds)），
        // 这样 Blocked/Removed 返回的 Selected 与输入顺序逐条一致。
        var selected = new List<string>();
        foreach (var id in selectedIds)
        {
            if (!selected.Contains(id)) selected.Add(id);
        }

        var byId = new Dictionary<string, Course>();
        foreach (var c in pool ?? []) byId[c.Id] = c;
        byId[target.Id] = target;

        if (selected.Contains(target.Id))
        {
            selected.Remove(target.Id);
            return new ToggleResult(ToggleAction.Removed, selected);
        }

        // 同课程教学班：按已选顺序找第一个（对应 TS 的 sameCourse[0]）
        Course? replaced = null;
        foreach (var id in selected)
        {
            if (byId.TryGetValue(id, out var c) && IsSameCourse(c, target))
            {
                replaced = c;
                break;
            }
        }

        if (replaced is not null)
        {
            var rest = new List<string>(selected);
            rest.Remove(replaced.Id);
            foreach (var id in rest)
            {
                if (byId.TryGetValue(id, out var other) && CoursesConflict(target, other))
                {
                    return new ToggleResult(ToggleAction.Blocked, selected, other);
                }
            }
            rest.Add(target.Id);
            return new ToggleResult(ToggleAction.Switched, rest, Replaced: replaced);
        }

        foreach (var id in selected)
        {
            if (byId.TryGetValue(id, out var other) && CoursesConflict(target, other))
            {
                return new ToggleResult(ToggleAction.Blocked, selected, other);
            }
        }

        selected.Add(target.Id);
        return new ToggleResult(ToggleAction.Added, selected);
    }
}

/// <summary>单次勾选操作的四态语义（与 TS 的 <c>ToggleAction</c> 字符串字面量一一对应）。</summary>
public enum ToggleAction
{
    /// <summary>目标课程原本未选、且不冲突 → 加入。</summary>
    Added,

    /// <summary>目标课程原本已选 → 取消。</summary>
    Removed,

    /// <summary>同课程换教学班（替换成功）。</summary>
    Switched,

    /// <summary>会与已选课程撞车 → 拦截，已选集合不变。</summary>
    Blocked,
}
