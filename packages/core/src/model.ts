/**
 * @tjt/core 的统一课表模型。
 *
 * 所有学校适配器都必须把数据归一化成这里的结构；窗口层与渲染层只认识这套模型，
 * 因此新增学校不会影响 UI。
 */

export const TIMETABLE_SCHEMA_VERSION = 1 as const;

/** 同济校历时间戳按北京时间午夜记录，默认时区偏移（分钟）。 */
export const DEFAULT_TZ_OFFSET_MINUTES = 480;

/** 星期：1 = 周一 … 7 = 周日（与同济教务 dayOfWeek 一致；注意 JS `getDay()` 是 0 = 周日）。 */
export type Weekday = 1 | 2 | 3 | 4 | 5 | 6 | 7;

export const WEEKDAYS: readonly Weekday[] = [1, 2, 3, 4, 5, 6, 7];

export const WEEKDAY_LABELS: Readonly<Record<Weekday, string>> = {
  1: '周一',
  2: '周二',
  3: '周三',
  4: '周四',
  5: '周五',
  6: '周六',
  7: '周日',
};

export function weekdayLabel(day: Weekday): string {
  return WEEKDAY_LABELS[day] ?? `周${day}`;
}

export function isWeekend(day: Weekday): boolean {
  return day >= 6;
}

/** 周次位掩码：bit0 = 第 1 周。 */
export type Weeks = number;

/** 节次时间定义。 */
export interface Slot {
  index: number;
  begin: string;
  end: string;
}

/** 学期信息。`startDate` 表示第 1 周周一的日期（`YYYY-MM-DD`，按学期所在地时区的日历日）。 */
export interface Term {
  id: string;
  name: string;
  year: number;
  termNo: number;
  startDate?: string;
  totalWeeks: number;
  slots: Slot[];
}

/** 一次上课安排（同一天、连续节次、一组周次、一个教室）。 */
export interface Session {
  id: string;
  day: Weekday;
  startSlot: number;
  endSlot: number;
  weeks: Weeks;
  room?: string;
}

/** 教学班（用户视角的"一门课"）。 */
export interface Course {
  id: string;
  name: string;
  courseCode?: string;
  teachingClassCode?: string;
  teachers: string[];
  faculty?: string;
  campus?: string;
  color?: string;
  sessions: Session[];
}

export interface TimetableSource {
  adapterId: string;
  adapterVersion: string;
  importedAt: string;
}

export interface Timetable {
  schemaVersion: typeof TIMETABLE_SCHEMA_VERSION;
  term: Term;
  courses: Course[];
  source: TimetableSource;
}

/** 同济默认节次表（来自 2026-2027 学年第 1 学期校历，工作日与周末同表）。 */
export const TONGJI_DEFAULT_SLOTS: readonly Slot[] = [
  { index: 1, begin: '08:00', end: '08:45' },
  { index: 2, begin: '08:50', end: '09:35' },
  { index: 3, begin: '10:00', end: '10:45' },
  { index: 4, begin: '10:50', end: '11:35' },
  { index: 5, begin: '13:30', end: '14:15' },
  { index: 6, begin: '14:20', end: '15:05' },
  { index: 7, begin: '15:30', end: '16:15' },
  { index: 8, begin: '16:20', end: '17:05' },
  { index: 9, begin: '18:30', end: '19:15' },
  { index: 10, begin: '19:20', end: '20:05' },
  { index: 11, begin: '20:10', end: '20:55' },
];

/** 生成 1..n 的默认节次（用于没有节次表的通用导入）。 */
export function makeDefaultSlots(count = 11): Slot[] {
  if (count === TONGJI_DEFAULT_SLOTS.length) return TONGJI_DEFAULT_SLOTS.map((s) => ({ ...s }));
  return Array.from({ length: count }, (_, i) => ({ index: i + 1, begin: '', end: '' }));
}

export function slotsFromList(slots: readonly Slot[]): Slot[] {
  return [...slots]
    .filter((s) => Number.isFinite(s.index) && s.index > 0)
    .sort((a, b) => a.index - b.index)
    .map((s) => ({ ...s }));
}

/** 取节次的开始时间（找不到返回空串）。 */
export function slotBegin(term: Term, index: number): string {
  return term.slots.find((s) => s.index === index)?.begin ?? '';
}

export function slotEnd(term: Term, index: number): string {
  return term.slots.find((s) => s.index === index)?.end ?? '';
}

/** 学期展示名，例如 `2026-2027学年第1学期`。 */
export function termLabel(term: Term): string {
  if (term.name) return term.name;
  return `${term.year}-${term.year + 1}学年第${term.termNo}学期`;
}

export function emptyTimetable(term: Term, source?: Partial<TimetableSource>): Timetable {
  return {
    schemaVersion: TIMETABLE_SCHEMA_VERSION,
    term,
    courses: [],
    source: {
      adapterId: source?.adapterId ?? 'unknown',
      adapterVersion: source?.adapterVersion ?? '0.0.0',
      importedAt: source?.importedAt ?? new Date().toISOString(),
    },
  };
}
