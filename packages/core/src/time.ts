import {
  DEFAULT_TZ_OFFSET_MINUTES,
  slotBegin,
  slotEnd,
  termLabel,
  type Course,
  type Session,
  type Term,
  type Weekday,
} from './model.js';
import { maskToWeeks } from './weeks.js';

/**
 * 时间推算：当前教学周、今天上什么课、下一节课。
 *
 * 关键约定：日期一律用 `YYYY-MM-DD` 字符串表示"学期所在地的日历日"，
 * 内部换算成"UTC 日序号"做纯日期算术，避免时区/夏令时带来的偏移。
 * （同济校历的毫秒时间戳是北京时间午夜，适配器负责用它算出 `startDate`。）
 */

const MS_PER_DAY = 86_400_000;

/** `YYYY-MM-DD` → UTC 日序号（无时区的纯日期）。 */
export function isoToDayNumber(iso: string): number {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso.trim());
  if (!m) return Number.NaN;
  const [, y, mo, d] = m;
  return Math.floor(Date.UTC(Number(y), Number(mo) - 1, Number(d)) / MS_PER_DAY);
}

/** UTC 日序号 → `YYYY-MM-DD`。 */
export function dayNumberToIso(day: number): string {
  const date = new Date(day * MS_PER_DAY);
  const y = date.getUTCFullYear();
  const m = `${date.getUTCMonth() + 1}`.padStart(2, '0');
  const d = `${date.getUTCDate()}`.padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/** 某个 `Date` 在指定时区（默认 UTC+8）下的日历日。 */
export function localTodayIso(now: Date = new Date(), tzOffsetMinutes = DEFAULT_TZ_OFFSET_MINUTES): string {
  const shifted = new Date(now.getTime() + tzOffsetMinutes * 60_000);
  const y = shifted.getUTCFullYear();
  const m = `${shifted.getUTCMonth() + 1}`.padStart(2, '0');
  const d = `${shifted.getUTCDate()}`.padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/** 指定时区下的当前时刻分钟数（0..1439）。 */
export function localMinutesOfDay(now: Date = new Date(), tzOffsetMinutes = DEFAULT_TZ_OFFSET_MINUTES): number {
  const shifted = new Date(now.getTime() + tzOffsetMinutes * 60_000);
  return shifted.getUTCHours() * 60 + shifted.getUTCMinutes();
}

/** 日序号 → 星期（1 = 周一 … 7 = 周日）。 */
export function dayNumberToWeekday(day: number): Weekday {
  const js = new Date(day * MS_PER_DAY).getUTCDay(); // 0 = 周日
  return (js === 0 ? 7 : js) as Weekday;
}

export function isoToWeekday(iso: string): Weekday {
  return dayNumberToWeekday(isoToDayNumber(iso));
}

/** 把毫秒时间戳（北京时间午夜）转成 `YYYY-MM-DD`。 */
export function msToIsoDate(ms: number, tzOffsetMinutes = DEFAULT_TZ_OFFSET_MINUTES): string {
  return localTodayIso(new Date(ms), tzOffsetMinutes);
}

/** 某天所在周的周一（按周一为一周起点）。 */
export function mondayOf(iso: string): string {
  const day = isoToDayNumber(iso);
  const weekday = dayNumberToWeekday(day);
  return dayNumberToIso(day - (weekday - 1));
}

export function addDays(iso: string, days: number): string {
  return dayNumberToIso(isoToDayNumber(iso) + days);
}

/**
 * 当前是第几教学周；开学前或学期结束后返回 `null`。
 *
 * `term.startDate` 必须是第 1 周周一。
 */
export function termWeekAt(term: Term, iso: string): number | null {
  if (!term.startDate) return null;
  const start = isoToDayNumber(mondayOf(term.startDate));
  const today = isoToDayNumber(iso);
  if (Number.isNaN(start) || Number.isNaN(today)) return null;
  const diff = today - start;
  if (diff < 0) return null;
  const week = Math.floor(diff / 7) + 1;
  if (term.totalWeeks > 0 && week > term.totalWeeks) return null;
  return week;
}

export interface SessionOccurrence {
  course: Course;
  session: Session;
  /** 该时段在指定日期是否真的上课（周次掩码命中）。 */
  week: number;
}

/** 指定日期该上哪些课（按开始节次排序）。 */
export function sessionsOnDate(courses: readonly Course[], term: Term, iso: string): SessionOccurrence[] {
  const week = termWeekAt(term, iso);
  if (week === null) return [];
  const weekday = isoToWeekday(iso);
  const out: SessionOccurrence[] = [];
  for (const course of courses) {
    for (const session of course.sessions) {
      if (session.day !== weekday) continue;
      if ((session.weeks & (1 << (week - 1))) === 0) continue;
      out.push({ course, session, week });
    }
  }
  out.sort((a, b) => a.session.startSlot - b.session.startSlot || a.course.name.localeCompare(b.course.name));
  return out;
}

/** 今天哪些课（`sessionsOnDate` 的语义化封装）。 */
export function todaysSessions(
  courses: readonly Course[],
  term: Term,
  now: Date = new Date(),
  tzOffsetMinutes = DEFAULT_TZ_OFFSET_MINUTES,
): SessionOccurrence[] {
  return sessionsOnDate(courses, term, localTodayIso(now, tzOffsetMinutes));
}

export interface UpcomingSession extends SessionOccurrence {
  /** 距离该课开始还有多少分钟（正在上课时为负）。 */
  minutesUntil: number;
  /** 该课在校历上的日期。 */
  date: string;
  /** 是否正在上课。 */
  inProgress: boolean;
}

/** 下一节课（含正在进行中的课）。找不到返回 `null`。 */
export function nextSession(
  courses: readonly Course[],
  term: Term,
  now: Date = new Date(),
  tzOffsetMinutes = DEFAULT_TZ_OFFSET_MINUTES,
): UpcomingSession | null {
  const today = localTodayIso(now, tzOffsetMinutes);
  const nowMinutes = localMinutesOfDay(now, tzOffsetMinutes);
  let inProgressFallback: UpcomingSession | null = null;

  for (let offset = 0; offset < 14; offset += 1) {
    const date = addDays(today, offset);
    for (const occurrence of sessionsOnDate(courses, term, date)) {
      const begin = toMinutes(slotBegin(term, occurrence.session.startSlot));
      const end = toMinutes(slotEnd(term, occurrence.session.endSlot));
      if (begin === null || end === null) continue;
      const minutesUntil = begin - nowMinutes + offset * 1440;
      const inProgress = offset === 0 && nowMinutes >= begin && nowMinutes <= end;
      const item: UpcomingSession = { ...occurrence, minutesUntil: inProgress ? 0 : minutesUntil, date, inProgress };
      if (inProgress) return item;
      if (minutesUntil > 0) return item;
      if (!inProgressFallback) inProgressFallback = item;
    }
  }
  return inProgressFallback;
}

/** `HH:mm` → 当天分钟数；非法返回 `null`。 */
export function toMinutes(hhmm: string): number | null {
  const m = /^(\d{1,2}):(\d{2})/.exec(hhmm.trim());
  if (!m) return null;
  const h = Number(m[1]);
  const min = Number(m[2]);
  if (h > 23 || min > 59) return null;
  return h * 60 + min;
}

export function minutesToClock(minutes: number): string {
  const total = ((minutes % 1440) + 1440) % 1440;
  const h = `${Math.floor(total / 60)}`.padStart(2, '0');
  const m = `${total % 60}`.padStart(2, '0');
  return `${h}:${m}`;
}

/** 相对时间描述：`23 分钟后` / `1 小时 5 分钟后` / `正在上课`。 */
export function describeCountdown(item: Pick<UpcomingSession, 'minutesUntil' | 'inProgress'>): string {
  if (item.inProgress) return '正在上课';
  if (item.minutesUntil <= 0) return '即将开始';
  if (item.minutesUntil < 60) return `${item.minutesUntil} 分钟后`;
  const h = Math.floor(item.minutesUntil / 60);
  const m = item.minutesUntil % 60;
  return m === 0 ? `${h} 小时后` : `${h} 小时 ${m} 分钟后`;
}

/** 课表头部一行摘要，例如 `2026-2027学年第1学期 · 第 3 周 · 周三`。 */
export function summarizeNow(
  term: Term,
  now: Date = new Date(),
  tzOffsetMinutes = DEFAULT_TZ_OFFSET_MINUTES,
): { label: string; week: number | null; date: string } {
  const date = localTodayIso(now, tzOffsetMinutes);
  const week = termWeekAt(term, date);
  const weekday = isoToWeekday(date);
  const weekText = week === null ? '假期' : `第 ${week} 周`;
  return { label: `${termLabel(term)} · ${weekText} · ${weekdayLabelOf(weekday)}`, week, date };
}

function weekdayLabelOf(day: Weekday): string {
  return ['周一', '周二', '周三', '周四', '周五', '周六', '周日'][day - 1] ?? '';
}

/** 某个 session 在给定学期下展开的上课周次（便于 UI 展示）。 */
export function sessionWeeks(session: Session, term: Term): number[] {
  return maskToWeeks(session.weeks, term.totalWeeks);
}
