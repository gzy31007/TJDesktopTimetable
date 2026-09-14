import type { Weeks } from './model.js';

/**
 * 周次位掩码工具。
 *
 * 约定：`Weeks` 是位掩码，bit0 = 第 1 周（与同济教务 `weekState` 一致，实测 65535 = 第 1-16 周）。
 */

/**
 * 第 1..32 周的合法掩码。
 *
 * 位掩码用 JS 32 位整数运算（`1 << (w - 1)`），因此上限是 32 周；
 * 国内高校一学期 16–23 周，32 足够。超过 32 周的周次会被忽略。
 */
export const MAX_WEEKS = 32;

/** 由周次列表构造掩码。非法周次（非整数 / 越界）被忽略。 */
export function weeksToMask(weeks: Iterable<number>): Weeks {
  let mask = 0;
  for (const w of weeks) {
    if (!Number.isInteger(w) || w < 1 || w > MAX_WEEKS) continue;
    mask |= 1 << (w - 1);
  }
  return mask >>> 0;
}

/** 掩码里出现过的最大周次（空掩码为 0）。 */
export function highestWeek(mask: Weeks): number {
  for (let w = MAX_WEEKS; w >= 1; w -= 1) {
    if (mask & (1 << (w - 1))) return w;
  }
  return 0;
}

/**
 * 把"已经是掩码"的数字规范成无符号 32 位。
 *
 * 注意与 `weeksToMask` 的区别：`weeksToMask([3])` 表示"第 3 周"，而
 * `normalizeMask(0b100)` 表示"第 3 周"——两者语义不同，别混用。
 */
export function normalizeMask(value: number): Weeks {
  if (!Number.isFinite(value)) return 0;
  return Math.trunc(value) >>> 0;
}

/**
 * 掩码 → 周次列表。
 *
 * `totalWeeks` 是学期总周数：周次展示范围取 `max(totalWeeks, 掩码最高位)`，
 * 这样掩码里出现第 17 周时不会因为校历写 16 周而丢数据。
 */
export function maskToWeeks(mask: Weeks, totalWeeks = 16): number[] {
  const limit = Math.max(totalWeeks, highestWeek(mask));
  const out: number[] = [];
  for (let w = 1; w <= limit; w += 1) {
    if (mask & (1 << (w - 1))) out.push(w);
  }
  return out;
}

/**
 * 掩码 → 人类可读标签，例如 `1-16`、`2, 4, 6`、`11-14`。
 *
 * 输出格式与 `build_timetable.py` 的 `fmt_weeks` 完全一致（区间用 `-`，分隔用 `, `），
 * 这是与既有数据（`tongji-2026-1-major.expected.json`）做黄金对比的前提。
 */
export function formatWeeksLabel(mask: Weeks, totalWeeks = 16): string {
  const weeks = maskToWeeks(mask, totalWeeks);
  if (weeks.length === 0) return '-';
  const parts: string[] = [];
  let start = weeks[0]!;
  let prev = weeks[0]!;
  for (let i = 1; i < weeks.length; i += 1) {
    const w = weeks[i]!;
    if (w === prev + 1) {
      prev = w;
      continue;
    }
    parts.push(start === prev ? `${start}` : `${start}-${prev}`);
    start = w;
    prev = w;
  }
  parts.push(start === prev ? `${start}` : `${start}-${prev}`);
  return parts.join(', ');
}

/** 学期全周掩码，例如 16 周 → 0xFFFF。 */
export function fullWeekMask(totalWeeks = 16): Weeks {
  return weeksToMask(Array.from({ length: totalWeeks }, (_, i) => i + 1));
}

/** 单周（1, 3, 5, …）掩码。 */
export function oddWeekMask(totalWeeks = 16): Weeks {
  return weeksToMask(Array.from({ length: totalWeeks }, (_, i) => i + 1).filter((w) => w % 2 === 1));
}

/** 双周（2, 4, 6, …）掩码。 */
export function evenWeekMask(totalWeeks = 16): Weeks {
  return weeksToMask(Array.from({ length: totalWeeks }, (_, i) => i + 1).filter((w) => w % 2 === 0));
}

export type WeekFilter = 'all' | 'odd' | 'even';

/** 周次过滤器 → 掩码；`all` 返回 `null` 表示不过滤。 */
export function resolveWeekFilter(filter: WeekFilter, totalWeeks = 16): Weeks | null {
  if (filter === 'odd') return oddWeekMask(totalWeeks);
  if (filter === 'even') return evenWeekMask(totalWeeks);
  return null;
}

/** 两个掩码是否有交集（用于判断同一格的两门课在周次上是否真的撞车）。 */
export function weeksOverlap(a: Weeks, b: Weeks): boolean {
  return (a & b) !== 0;
}

export function countWeeks(mask: Weeks): number {
  let n = 0;
  for (let w = 1; w <= MAX_WEEKS; w += 1) if (mask & (1 << (w - 1))) n += 1;
  return n;
}

/** 掩码是否覆盖整个学期（例如 16 周课表的 0xFFFF）——「全周上课」判定。 */
export function isAllWeeks(mask: Weeks, totalWeeks = 16): boolean {
  return mask !== 0 && mask === fullWeekMask(totalWeeks);
}
