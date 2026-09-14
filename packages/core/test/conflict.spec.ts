import { describe, expect, it } from 'vitest';
import { coursesConflict, findConflicts, sessionsOverlap, toggleCourse, weeksToMask, type Course } from '../src/index.js';

function course(
  id: string,
  name: string,
  courseCode: string,
  sessions: { day: 1 | 2 | 3 | 4 | 5 | 6 | 7; startSlot: number; endSlot: number; weeks: number }[],
): Course {
  return {
    id,
    name,
    courseCode,
    teachers: [],
    sessions: sessions.map((s, i) => ({ id: `${id}-${i}`, ...s })),
  };
}

const ALL = weeksToMask([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
const ODD = weeksToMask([1, 3, 5, 7, 9, 11, 13, 15]);
const EVEN = weeksToMask([2, 4, 6, 8, 10, 12, 14, 16]);

describe('conflict', () => {
  it('同天同节次同周次 → 冲突', () => {
    const a = course('a', '高等数学', 'M1', [{ day: 1, startSlot: 1, endSlot: 2, weeks: ALL }]);
    const b = course('b', '大学物理', 'P1', [{ day: 1, startSlot: 2, endSlot: 3, weeks: ALL }]);
    expect(sessionsOverlap(a.sessions[0]!, b.sessions[0]!)).toBe(true);
    expect(coursesConflict(a, b)).toBe(true);
  });

  it('单双周错开不算冲突（与基准版一致）', () => {
    const a = course('a', '体育', 'PE', [{ day: 3, startSlot: 5, endSlot: 6, weeks: ODD }]);
    const b = course('b', '英语', 'EN', [{ day: 3, startSlot: 5, endSlot: 6, weeks: EVEN }]);
    expect(coursesConflict(a, b)).toBe(false);
  });

  it('同一门课的不同教学班不算冲突', () => {
    const a = course('a1', '高等数学', 'M1', [{ day: 1, startSlot: 1, endSlot: 2, weeks: ALL }]);
    const a2 = course('a2', '高等数学', 'M1', [{ day: 1, startSlot: 1, endSlot: 2, weeks: ALL }]);
    expect(coursesConflict(a, a2)).toBe(false);
  });

  it('节次相接但不重叠 → 不冲突', () => {
    const a = course('a', 'A', 'A', [{ day: 2, startSlot: 1, endSlot: 2, weeks: ALL }]);
    const b = course('b', 'B', 'B', [{ day: 2, startSlot: 3, endSlot: 4, weeks: ALL }]);
    expect(coursesConflict(a, b)).toBe(false);
  });

  it('findConflicts 返回全部冲突课程', () => {
    const target = course('t', 'T', 'T', [{ day: 4, startSlot: 5, endSlot: 7, weeks: ALL }]);
    const a = course('a', 'A', 'A', [{ day: 4, startSlot: 5, endSlot: 6, weeks: ALL }]);
    const b = course('b', 'B', 'B', [{ day: 4, startSlot: 7, endSlot: 8, weeks: ALL }]);
    const c = course('c', 'C', 'C', [{ day: 5, startSlot: 5, endSlot: 6, weeks: ALL }]);
    expect(findConflicts(target, [a, b, c]).map((x) => x.id)).toEqual(['a', 'b']);
  });

  describe('toggleCourse（复刻基准版 tryToggle）', () => {
    const math1 = course('math-1', '高等数学', 'M1', [{ day: 1, startSlot: 1, endSlot: 2, weeks: ALL }]);
    const math2 = course('math-2', '高等数学', 'M1', [{ day: 1, startSlot: 3, endSlot: 4, weeks: ALL }]);
    const physics = course('phy-1', '大学物理', 'P1', [{ day: 1, startSlot: 1, endSlot: 2, weeks: ALL }]);
    const pool = [math1, math2, physics];

    it('选中 / 取消', () => {
      const added = toggleCourse(math1, [], pool);
      expect(added).toMatchObject({ action: 'added', selected: ['math-1'] });
      const removed = toggleCourse(math1, added.selected, pool);
      expect(removed).toMatchObject({ action: 'removed', selected: [] });
    });

    it('冲突时拦截且已选不变', () => {
      const result = toggleCourse(physics, ['math-1'], pool);
      expect(result.action).toBe('blocked');
      expect(result.conflict?.id).toBe('math-1');
      expect(result.selected).toEqual(['math-1']);
    });

    it('同课程换班 → switched（无冲突时）', () => {
      const result = toggleCourse(math2, ['math-1'], pool);
      expect(result.action).toBe('switched');
      expect(result.replaced?.id).toBe('math-1');
      expect(result.selected).toEqual(['math-2']);
    });

    it('换班会撞其它课 → 整体不动', () => {
      const other = course('chem-1', '大学化学', 'C1', [{ day: 1, startSlot: 3, endSlot: 4, weeks: ALL }]);
      const result = toggleCourse(math2, ['math-1', 'chem-1'], [...pool, other]);
      expect(result.action).toBe('blocked');
      expect(result.conflict?.id).toBe('chem-1');
      expect(result.selected.sort()).toEqual(['chem-1', 'math-1']);
    });
  });
});
