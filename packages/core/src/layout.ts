import { colorForCourse, readableTextColor, withAlpha } from './colors.js';
import {
  isWeekend,
  slotBegin,
  slotEnd,
  termLabel,
  WEEKDAY_LABELS,
  type Course,
  type Session,
  type Term,
  type Weekday,
  type Weeks,
} from './model.js';
import { formatWeeksLabel, isAllWeeks, resolveWeekFilter, type WeekFilter } from './weeks.js';
import { isoToWeekday, localMinutesOfDay, localTodayIso, termWeekAt } from './time.js';

/**
 * 课表网格的纯数据布局（像素无关）。

 * 视觉规则对齐 `select_preview.html`：
 * - 7 列（周一…周日），节次从上到下；
 * - 同一格（同一天 + 同起止节次）的多门课横向并排，宽度 1/n；
 * - 非全周上课的课打 `special` 标记（渲染层画条纹虚线）。
 */

export interface BoardGeometry {
  /** 左侧节次标签列宽。 */
  gutterWidth: number;
  /** 单天列宽。 */
  cellWidth: number;
  /** 单节行高。 */
  rowHeight: number;
  /** 表头（星期）高度。 */
  headerHeight: number;
  rows: number;
  cols: number;
}

export const DEFAULT_GEOMETRY: Omit<BoardGeometry, 'rows' | 'cols'> = {
  gutterWidth: 74,
  cellWidth: 118,
  rowHeight: 52,
  headerHeight: 28,
};

/** 按可用宽度自适应列宽（桌面挂件用；`cellWidth` 不低于 `minCellWidth`）。 */
export function fitGeometry(
  availableWidth: number,
  rows: number,
  cols: number,
  base: Omit<BoardGeometry, 'rows' | 'cols'> = DEFAULT_GEOMETRY,
  minCellWidth = 64,
): BoardGeometry {
  const usable = Math.max(0, availableWidth - base.gutterWidth);
  const cellWidth = cols > 0 ? Math.max(minCellWidth, Math.floor(usable / cols)) : base.cellWidth;
  return { ...base, cellWidth, rows, cols };
}

export interface BoardOptions {
  /** 展示哪些天（默认全周；`showWeekend: false` 时只到周五）。 */
  showWeekend?: boolean;
  /** 周次过滤：全部 / 仅单周 / 仅双周。 */
  weekFilter?: WeekFilter;
  /** 只显示 `[firstSlot, lastSlot]` 范围内的节次；不传则自动收窄到有课的范围。 */
  trimEmptySlots?: boolean;
  firstSlot?: number;
  lastSlot?: number;
  /** 用于"今日"高亮与"当前周"标记。 */
  today?: string;
  /** 当前时刻（用于标记"正在上的节次"）。 */
  now?: Date;
  /** 时区偏移（分钟），默认北京时间。 */
  tzOffsetMinutes?: number;
  colorOf?: (course: Course) => string;
  /** 课程名压缩：去掉全角括号后缀（与基准版一致）。 */
  shortName?: (name: string) => string;
}

export interface BoardDay {
  day: Weekday;
  label: string;
  weekend: boolean;
  isToday: boolean;
}

export interface BoardRow {
  index: number;
  begin: string;
  end: string;
  label: string;
  isCurrent?: boolean;
}

export interface BoardBlock {
  courseId: string;
  courseCode?: string;
  teachingClassCode?: string;
  name: string;
  shortName: string;
  room?: string;
  teachers: string[];
  weeks: Weeks;
  weeksLabel: string;
  /** 非全周上课（单/双/特定周）→ 渲染层加条纹虚线。 */
  special: boolean;
  color: string;
  /** 半透明填充色，色块背景用。 */
  fill: string;
  textColor: string;
  day: Weekday;
  startSlot: number;
  endSlot: number;
  /** 同一格内并排位置。 */
  col: number;
  colCount: number;
  /** 该格内是否有其它并行块（渲染层用来决定是否加边框）。 */
  stacked: boolean;
}

export interface BoardState {
  term: Term;
  title: string;
  days: BoardDay[];
  rows: BoardRow[];
  blocks: BoardBlock[];
  /** 当前教学周（假期为 null）。 */
  currentWeek: number | null;
  today: string;
  /** 被周次过滤掉的课程数 / 时段数。 */
  hiddenSessions: number;
}

export function defaultShortName(name: string): string {
  return name.replace(/（.*）\s*$/, '').replace(/\(.*\)\s*$/, '').trim() || name;
}

interface PendingBlock {
  course: Course;
  session: Session;
}

export function buildBoard(courses: readonly Course[], term: Term, options: BoardOptions = {}): BoardState {
  const today = options.today ?? localTodayIso();
  const todayWeekday = isoToWeekday(today);
  const weekFilter = options.weekFilter ?? 'all';
  const filterMask = resolveWeekFilter(weekFilter, term.totalWeeks);
  const colorOf = options.colorOf ?? ((course: Course) => colorForCourse(course.name));
  const shortName = options.shortName ?? defaultShortName;

  const pending: PendingBlock[] = [];
  let hiddenSessions = 0;

  for (const course of courses) {
    for (const session of course.sessions) {
      if (filterMask !== null && (session.weeks & filterMask) === 0) {
        hiddenSessions += 1;
        continue;
      }
      if (session.weeks === 0) {
        hiddenSessions += 1;
        continue;
      }
      pending.push({ course, session });
    }
  }

  const days: BoardDay[] = ([1, 2, 3, 4, 5, 6, 7] as Weekday[])
    .filter((day) => (options.showWeekend === false ? !isWeekend(day) : true))
    .map((day) => ({
      day,
      label: WEEKDAY_LABELS[day],
      weekend: isWeekend(day),
      isToday: day === todayWeekday,
    }));
  const visibleDays = new Set(days.map((d) => d.day));

  const visible = pending.filter((p) => visibleDays.has(p.session.day));

  const slotNumbers = visible.flatMap((p) => [p.session.startSlot, p.session.endSlot]);
  const minSlot = options.firstSlot ?? (options.trimEmptySlots && slotNumbers.length ? Math.min(...slotNumbers) : 1);
  const maxSlot =
    options.lastSlot ??
    (options.trimEmptySlots && slotNumbers.length
      ? Math.max(...slotNumbers)
      : Math.max(11, ...(slotNumbers.length ? slotNumbers : [0])));

  const currentWeek = termWeekAt(term, today);
  const nowSlot = currentSlotIndex(term, localMinutesOfDay(options.now ?? new Date(), options.tzOffsetMinutes));

  const rows: BoardRow[] = [];
  for (let index = minSlot; index <= maxSlot; index += 1) {
    rows.push({
      index,
      begin: slotBegin(term, index),
      end: slotEnd(term, index),
      label: `${index}节`,
      isCurrent: nowSlot === index,
    });
  }

  // 同格并排
  const groups = new Map<string, PendingBlock[]>();
  for (const item of visible) {
    const key = `${item.session.day}|${item.session.startSlot}|${item.session.endSlot}`;
    const list = groups.get(key);
    if (list) list.push(item);
    else groups.set(key, [item]);
  }

  const blocks: BoardBlock[] = [];
  for (const group of groups.values()) {
    const colCount = group.length;
    group.forEach((item, col) => {
      const { course, session } = item;
      const color = course.color ?? colorOf(course);
      blocks.push({
        courseId: course.id,
        courseCode: course.courseCode,
        teachingClassCode: course.teachingClassCode,
        name: course.name,
        shortName: shortName(course.name),
        room: session.room ?? undefined,
        teachers: course.teachers,
        weeks: session.weeks,
        weeksLabel: formatWeeksLabel(session.weeks, term.totalWeeks),
        special: !isAllWeeks(session.weeks, term.totalWeeks),
        color,
        fill: withAlpha(color, 0.8),
        textColor: readableTextColor(color),
        day: session.day,
        startSlot: session.startSlot,
        endSlot: session.endSlot,
        col,
        colCount,
        stacked: colCount > 1,
      });
    });
  }

  blocks.sort(
    (a, b) => a.startSlot - b.startSlot || a.day - b.day || a.name.localeCompare(b.name) || a.col - b.col,
  );

  return {
    term,
    title: termLabel(term),
    days,
    rows,
    blocks,
    currentWeek,
    today,
    hiddenSessions,
  };
}

/** 当前时刻落在第几节（用于给节次标签加"正在上"标记）。 */
export function currentSlotIndex(term: Term, nowMinutes?: number): number | null {
  if (nowMinutes === undefined) return null;
  for (const slot of term.slots) {
    const begin = toMin(slot.begin);
    const end = toMin(slot.end);
    if (begin === null || end === null) continue;
    if (nowMinutes >= begin && nowMinutes <= end) return slot.index;
  }
  return null;
}

function toMin(hhmm: string): number | null {
  const m = /^(\d{1,2}):(\d{2})/.exec(hhmm.trim());
  if (!m) return null;
  return Number(m[1]) * 60 + Number(m[2]);
}

/** 网格需要的总尺寸（渲染层换算像素）。 */
export function boardSize(state: BoardState, geometry: BoardGeometry): { width: number; height: number } {
  return {
    width: geometry.gutterWidth + geometry.cellWidth * state.days.length,
    height: geometry.headerHeight + geometry.rowHeight * state.rows.length,
  };
}

export interface BlockRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

/** 色块的像素矩形（同一格并排按 1/n 均分）。 */
export function blockRect(state: BoardState, block: BoardBlock, geometry: BoardGeometry): BlockRect {
  const dayIndex = state.days.findIndex((d) => d.day === block.day);
  const rowIndex = state.rows.findIndex((r) => r.index === block.startSlot);
  const rowSpan = Math.max(1, block.endSlot - block.startSlot + 1);
  const dayPos = dayIndex < 0 ? 0 : dayIndex;
  const rowPos = rowIndex < 0 ? 0 : rowIndex;
  // 与基准版一致：先给整列留 6px 间隙，再按并排数均分，每块再收 2px。
  const columnWidth = (geometry.cellWidth - 6) / Math.max(1, block.colCount);
  return {
    left: geometry.gutterWidth + dayPos * geometry.cellWidth + 3 + block.col * columnWidth,
    top: geometry.headerHeight + rowPos * geometry.rowHeight + 1,
    width: Math.max(0, columnWidth - 2),
    height: Math.max(0, rowSpan * geometry.rowHeight - 2),
  };
}
