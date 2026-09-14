import {
  makeDefaultSlots,
  slotsFromList,
  type Course,
  type Session,
  type Slot,
  type Term,
  type Weekday,
} from '../model.js';
import { normalizeMask } from '../weeks.js';
import { addDays, mondayOf, msToIsoDate } from '../time.js';
import {
  asArray,
  asRecord,
  inputTexts,
  makeDiagnostic,
  tryParseJson,
  unwrapData,
  type AdapterContext,
  type Diagnostic,
  type ImportInput,
  type ImportResult,
  type SchoolAdapter,
} from './types.js';

/**
 * 同济大学 1 系统「个人课表」适配器。
 *
 * 数据来源：1 系统个人课表接口的 JSON 响应（`api/arrangementservice/timetable/...`），
 * 也就是「我已选的课」，**导入即用，不需要再挑教学班**。
 *
 * 与已移除的「专业课表 `timetable/major`」的区别：那份是专业培养计划里的全部平行教学班
 * （同一门课多个 `teachingClassId`），必须勾选；这里同一门课通常只有一个教学班。
 *
 * 可同时喂入：
 * 1. 个人课表：`{code, msg, data:[{ dayOfWeek, timeStart, timeEnd, weekState, courseName, roomName, ... }]}`
 * 2. 校历：`{code, msg, data:[{ id, year, term, beginDay, teachingWeekStart, teachingWeekEnd, noWeekendWorkTimes }]}`
 *    —— 用来算节次时间与"当前第几周"，缺省时退化为内置节次表 + 16 周（不显示周次）。
 */

export const TONGJI_STUDENT_ADAPTER_ID = 'tongji-student';
export const TONGJI_STUDENT_ADAPTER_VERSION = '2.0.0';

/** 1 系统接口基址（网络抓取用）。 */
export const TONGJI_ORIGIN = 'https://1.tongji.edu.cn';

interface RawScheduleItem {
  teachingClassId?: unknown;
  code?: unknown;
  courseCode?: unknown;
  courseName?: unknown;
  dayOfWeek?: unknown;
  timeStart?: unknown;
  timeEnd?: unknown;
  weekState?: unknown;
  roomName?: unknown;
  facultyI18n?: unknown;
  campusI18n?: unknown;
  value?: unknown;
  newValue?: unknown;
  teacherCodes?: unknown;
  majors?: unknown;
}

interface RawCalendarTerm {
  id?: unknown;
  year?: unknown;
  term?: unknown;
  beginDay?: unknown;
  weekNum?: unknown;
  teachingWeekStart?: unknown;
  teachingWeekEnd?: unknown;
  currentTermFlag?: unknown;
  nextTermFlag?: unknown;
  fullName?: unknown;
  noWeekendWorkTimes?: unknown;
  weekendWorkTimes?: unknown;
}

interface Classified {
  schedule: RawScheduleItem[];
  calendar: RawCalendarTerm[];
  student: Record<string, unknown> | null;
  sources: string[];
}

const TEACHER_RE = /([\u4e00-\u9fa5]{2,8})\((\d{3,6})\)/g;

/** 从 `value` 字段（"课程(代码) 教师(工号) 星期三5-7节 [1-16] 教室"）解析教师。 */
export function parseTeachersFromValue(value: string | undefined): string[] {
  if (!value) return [];
  TEACHER_RE.lastIndex = 0;
  const out: string[] = [];
  let m: RegExpExecArray | null;
  while ((m = TEACHER_RE.exec(value)) !== null) {
    const name = m[1];
    const code = m[2];
    if (name && code) out.push(`${name}(${code})`);
  }
  return out;
}

export function parseTeachers(item: RawScheduleItem): string[] {
  const fromValue = parseTeachersFromValue(
    typeof item.value === 'string' ? item.value : typeof item.newValue === 'string' ? item.newValue : undefined,
  );
  if (fromValue.length) return fromValue;
  const codes = asArray(item.teacherCodes);
  if (codes) {
    const list = codes.filter((c): c is string | number => typeof c === 'string' || typeof c === 'number');
    if (list.length) return list.map((c) => String(c));
  }
  return [];
}

function classify(input: ImportInput): Classified {
  const result: Classified = { schedule: [], calendar: [], student: null, sources: [] };

  for (const { label, text } of inputTexts(input)) {
    const parsed = tryParseJson(text);
    if (parsed === null) continue;
    const data = unwrapData(parsed);
    const list = asArray(data);

    if (list) {
      const objs = list.filter((x): x is Record<string, unknown> => asRecord(x) !== null);
      const scheduleItems = objs.filter((o) => 'weekState' in o && 'dayOfWeek' in o);
      if (scheduleItems.length) {
        result.schedule.push(...(scheduleItems as RawScheduleItem[]));
        result.sources.push(`${label}:课表 ${scheduleItems.length} 条`);
        continue;
      }
      const calendarTerms = objs.filter((o) => 'noWeekendWorkTimes' in o || ('beginDay' in o && 'weekNum' in o));
      if (calendarTerms.length) {
        result.calendar.push(...(calendarTerms as RawCalendarTerm[]));
        result.sources.push(`${label}:校历 ${calendarTerms.length} 个学期`);
        continue;
      }
      continue;
    }

    const record = asRecord(data);
    if (record && ('studentCode' in record || 'profession' in record || 'trainingLevel' in record)) {
      result.student = record;
      result.sources.push(`${label}:学生信息`);
    }
  }

  return result;
}

function toInt(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return Math.trunc(value);
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) return Number.parseInt(value, 10);
  return null;
}

function toSlotList(term: RawCalendarTerm): Slot[] {
  const raw = asArray(term.noWeekendWorkTimes) ?? asArray(term.weekendWorkTimes) ?? [];
  const slots: Slot[] = [];
  for (const entry of raw) {
    const record = asRecord(entry);
    if (!record) continue;
    const index = toInt(record.classNode);
    const begin = typeof record.beginTime === 'string' ? record.beginTime : '';
    const end = typeof record.endTime === 'string' ? record.endTime : '';
    if (index === null || index <= 0) continue;
    slots.push({ index, begin, end });
  }
  return slots.length ? slotsFromList(slots) : makeDefaultSlots();
}

/**
 * 学期第 1 周周一。
 *
 * 校历 `beginDay` 是"北京时间午夜的毫秒时间戳"（实测 2026-2027 学年第 1 学期为 2026-09-14，周一）。
 * 若 `teachingWeekStart > 1`（开学前有第 0 周），再补上相应的周数偏移。
 */
function resolveStartDate(term: RawCalendarTerm): string | undefined {
  const beginDay = toInt(term.beginDay);
  if (beginDay === null) return undefined;
  const dayIso = msToIsoDate(beginDay);
  const teachingWeekStart = toInt(term.teachingWeekStart) ?? 1;
  const monday = mondayOf(dayIso);
  return teachingWeekStart > 1 ? addDays(monday, (teachingWeekStart - 1) * 7) : monday;
}

function pickTerm(calendar: RawCalendarTerm[], termId: string | number | undefined): RawCalendarTerm | null {
  if (!calendar.length) return null;
  if (termId !== undefined) {
    const wanted = String(termId);
    const hit = calendar.find((t) => String(toInt(t.id) ?? t.id) === wanted);
    if (hit) return hit;
  }
  return (
    calendar.find((t) => t.currentTermFlag === true) ??
    calendar.find((t) => t.nextTermFlag === true) ??
    calendar[0] ??
    null
  );
}

function buildTerm(term: RawCalendarTerm | null, diagnostics: Diagnostic[]): Term {
  if (!term) {
    diagnostics.push(
      makeDiagnostic(
        'warn',
        'tongji.term.missing',
        '没有校历数据：已按 16 教学周 + 内置节次时间解析。挂件将无法显示"当前第几周"，可在设置里手动填开学日期。',
      ),
    );
    return {
      id: 'unknown',
      name: '',
      year: new Date().getFullYear(),
      termNo: 1,
      totalWeeks: 16,
      slots: makeDefaultSlots(),
    };
  }

  const startDate = resolveStartDate(term);
  const slots = toSlotList(term);
  const year = toInt(term.year) ?? new Date().getFullYear();
  const termNo = toInt(term.term) ?? 1;
  const teachingWeekEnd = toInt(term.teachingWeekEnd);
  const totalWeeks = teachingWeekEnd && teachingWeekEnd > 0 ? teachingWeekEnd : (toInt(term.weekNum) ?? 16);

  if (!startDate) {
    diagnostics.push(makeDiagnostic('warn', 'tongji.term.startDate', '校历缺少 beginDay，无法计算当前教学周。'));
  }

  return {
    id: String(toInt(term.id) ?? 'unknown'),
    name: typeof term.fullName === 'string' ? term.fullName : '',
    year,
    termNo,
    startDate,
    totalWeeks,
    slots,
  };
}

/** 个人课表响应 → 课程列表（同一教学班的多个时段合并）。 */
export function buildCourses(items: RawScheduleItem[]): Course[] {
  const byId = new Map<string, Course>();

  for (const item of items) {
    const day = toInt(item.dayOfWeek);
    const startSlot = toInt(item.timeStart);
    const endSlot = toInt(item.timeEnd);
    const weeks = toInt(item.weekState);
    if (day === null || startSlot === null || endSlot === null || weeks === null) continue;
    if (day < 1 || day > 7) continue;

    const classId = toInt(item.teachingClassId);
    const classCode = typeof item.code === 'string' ? item.code : item.code != null ? String(item.code) : undefined;
    const courseCode =
      typeof item.courseCode === 'string'
        ? item.courseCode
        : item.courseCode != null
          ? String(item.courseCode)
          : undefined;
    const id = classId !== null ? String(classId) : (classCode ?? `${courseCode ?? 'unknown'}-${day}-${startSlot}`);
    const name = typeof item.courseName === 'string' ? item.courseName : '(未知课程)';
    const room = typeof item.roomName === 'string' && item.roomName ? item.roomName : undefined;

    let course = byId.get(id);
    if (!course) {
      course = {
        id,
        name,
        courseCode,
        teachingClassCode: classCode,
        teachers: parseTeachers(item),
        faculty: typeof item.facultyI18n === 'string' ? item.facultyI18n : undefined,
        campus: typeof item.campusI18n === 'string' ? item.campusI18n : undefined,
        sessions: [],
      };
      byId.set(id, course);
    } else if (!course.teachers.length) {
      course.teachers = parseTeachers(item);
    }

    const mask = normalizeMask(weeks);
    const session: Session = {
      id: `${id}-${day}-${startSlot}-${endSlot}-${mask}`,
      day: day as Weekday,
      startSlot,
      endSlot,
      weeks: mask,
      ...(room ? { room } : {}),
    };
    if (!course.sessions.some((s) => s.id === session.id)) course.sessions.push(session);
  }

  for (const course of byId.values()) {
    course.sessions.sort((a, b) => a.day - b.day || a.startSlot - b.startSlot);
  }

  return [...byId.values()].sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN') || a.id.localeCompare(b.id));
}

/** 看起来像"培养计划"而不是"个人课表"的特征：同一门课出现多个教学班。 */
function looksLikeMajorPlan(items: RawScheduleItem[]): boolean {
  const byCourse = new Map<string, Set<string>>();
  for (const item of items) {
    const courseCode = item.courseCode != null ? String(item.courseCode) : null;
    const classId = item.teachingClassId != null ? String(item.teachingClassId) : null;
    if (!courseCode || !classId) continue;
    const set = byCourse.get(courseCode) ?? new Set<string>();
    set.add(classId);
    byCourse.set(courseCode, set);
  }
  let multi = 0;
  for (const set of byCourse.values()) if (set.size > 1) multi += 1;
  return multi >= 3;
}

/** 快速判断这段文本像不像同济课表数据（供自动探测使用）。 */
function looksLikeTimetable(input: ImportInput): number {
  for (const { text } of inputTexts(input)) {
    const trimmed = text.trim();
    if (!trimmed.includes('weekState') || !trimmed.includes('dayOfWeek')) continue;
    const parsed = tryParseJson(trimmed);
    if (parsed === null) continue;
    const list = asArray(unwrapData(parsed));
    if (
      list?.some((x) => {
        const r = asRecord(x);
        return r !== null && 'weekState' in r && 'dayOfWeek' in r;
      })
    ) {
      return 0.95;
    }
  }
  return 0;
}

export const tongjiStudentAdapter: SchoolAdapter = {
  id: TONGJI_STUDENT_ADAPTER_ID,
  displayName: '同济大学 · 1 系统个人课表',
  version: TONGJI_STUDENT_ADAPTER_VERSION,
  description:
    '解析 1 系统「我的课表」接口响应（含 dayOfWeek / weekState / timeStart 等字段），导入即用；可同时导入校历 JSON 以显示节次时间与当前周次。',
  canFetch: true,

  detect(input: ImportInput): number {
    return looksLikeTimetable(input);
  },

  parse(input: ImportInput, _ctx: AdapterContext): ImportResult {
    const diagnostics: Diagnostic[] = [];
    const classified = classify(input);

    if (!classified.schedule.length) {
      diagnostics.push(
        makeDiagnostic(
          'error',
          'tongji.schedule.missing',
          '没有在输入里找到课表数据（需要含 weekState / dayOfWeek 的 JSON 数组）。',
        ),
      );
    }

    const term = buildTerm(pickTerm(classified.calendar, input.termId), diagnostics);
    const courses = buildCourses(classified.schedule);

    const slotCount = courses.reduce((n, c) => n + c.sessions.length, 0);
    diagnostics.push(
      makeDiagnostic(
        'info',
        'tongji.summary',
        `导入 ${courses.length} 门课程 / ${slotCount} 条上课安排（学期 ${term.name || term.id}，共 ${term.totalWeeks} 教学周）。`,
      ),
    );
    if (classified.schedule.length && !classified.calendar.length) {
      diagnostics.push(
        makeDiagnostic('warn', 'tongji.calendar.missing', '建议同时提供校历数据，以获得准确的节次时间与开学日期。'),
      );
    }
    if (looksLikeMajorPlan(classified.schedule)) {
      diagnostics.push(
        makeDiagnostic(
          'warn',
          'tongji.looksLikePlan',
          '这份数据里同一门课出现了多个教学班，看起来更像"专业培养计划"而不是个人课表；导入后可能包含平行班，请核对。',
        ),
      );
    }

    const meta: Record<string, unknown> = { sources: classified.sources };
    if (classified.student) {
      meta.student = {
        profession: classified.student.profession ?? null,
        professionName: classified.student.professionI18n ?? null,
        grade: classified.student.grade ?? null,
      };
    }

    return {
      adapterId: TONGJI_STUDENT_ADAPTER_ID,
      adapterName: tongjiStudentAdapter.displayName,
      adapterVersion: TONGJI_STUDENT_ADAPTER_VERSION,
      term,
      // 个人课表：直接给出课程，不走勾选流程
      courses,
      preselect: [],
      diagnostics,
      meta,
    };
  },
};
