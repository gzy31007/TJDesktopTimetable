import type { Course, Session } from './model.js';
import { weeksOverlap } from './weeks.js';

/**
 * 冲突检测 —— 与 `select_preview.html` 的行为保持一致：
 * 同一天、节次区间相交、且周次掩码有交集，才算冲突；同一门课的不同教学班互不冲突。
 */

export function sessionsOverlap(a: Session, b: Session): boolean {
  if (a.day !== b.day) return false;
  if (!(a.startSlot <= b.endSlot && b.startSlot <= a.endSlot)) return false;
  return weeksOverlap(a.weeks, b.weeks);
}

/** 是否为「同一门课」（课程代码相同，或本就是同一个教学班）。 */
export function isSameCourse(a: Course, b: Course): boolean {
  if (a.id === b.id) return true;
  return Boolean(a.courseCode) && a.courseCode === b.courseCode;
}

export function coursesConflict(a: Course, b: Course): boolean {
  if (isSameCourse(a, b)) return false;
  for (const s1 of a.sessions) {
    for (const s2 of b.sessions) {
      if (sessionsOverlap(s1, s2)) return true;
    }
  }
  return false;
}

/** 返回与目标课程冲突的所有已选课程。 */
export function findConflicts(target: Course, selected: Iterable<Course>): Course[] {
  const hits: Course[] = [];
  for (const other of selected) {
    if (coursesConflict(target, other)) hits.push(other);
  }
  return hits;
}

export type ToggleAction = 'added' | 'removed' | 'switched' | 'blocked';

export interface ToggleResult {
  action: ToggleAction;
  /** 操作后的已选教学班 id 列表。 */
  selected: string[];
  /** `blocked` 时与之冲突的已选课程。 */
  conflict?: Course;
  /** `switched` 时被替换掉的同课程教学班。 */
  replaced?: Course;
}

/**
 * 勾选 / 取消 / 切换一个教学班（复刻 `select_preview.html` 的 `tryToggle` 语义）。
 *
 * - 已选 → 取消；
 * - 同 `courseCode` 已选其它教学班 → 切换（切换前先验证与其余课程不冲突，冲突则整体不动）；
 * - 与已选课程时间冲突 → `blocked`，已选集合不变。
 */
export function toggleCourse(
  target: Course,
  selectedIds: Iterable<string>,
  pool: Iterable<Course> = [],
): ToggleResult {
  const selected = new Set(selectedIds);
  const byId = new Map<string, Course>();
  for (const c of pool) byId.set(c.id, c);
  byId.set(target.id, target);

  if (selected.has(target.id)) {
    selected.delete(target.id);
    return { action: 'removed', selected: [...selected] };
  }

  const sameCourse: Course[] = [];
  for (const id of selected) {
    const c = byId.get(id);
    if (c && isSameCourse(c, target)) sameCourse.push(c);
  }

  const replaced = sameCourse[0];
  if (replaced) {
    const rest = new Set(selected);
    rest.delete(replaced.id);
    for (const id of rest) {
      const other = byId.get(id);
      if (other && coursesConflict(target, other)) {
        return { action: 'blocked', selected: [...selected], conflict: other };
      }
    }
    rest.add(target.id);
    return { action: 'switched', selected: [...rest], replaced };
  }

  for (const id of selected) {
    const other = byId.get(id);
    if (other && coursesConflict(target, other)) {
      return { action: 'blocked', selected: [...selected], conflict: other };
    }
  }

  selected.add(target.id);
  return { action: 'added', selected: [...selected] };
}
