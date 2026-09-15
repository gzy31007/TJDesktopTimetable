import {
  makeDefaultSlots,
  slotsFromList,
  type Course,
  type Session,
  type Slot,
  type Term,
  type Weekday,
} from '../model.js';
import { fullWeekMask, normalizeMask, weeksToMask } from '../weeks.js';
import { addDays, mondayOf, msToIsoDate } from '../time.js';
import { findTermPreset, termFromPreset } from '../tongji-terms.js';
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
 * 主数据源：选课服务接口 `POST /api/electionservice/student/{id}/getDataBk` 的响应，
 * 结构为 `data.selectedCourses[].course.times[]`：
 *
 * ```json
 * { "data": { "calendarId": 122,
 *     "selectedCourses": [{ "course": {
 *       "courseName": "大学物理B2(I)", "courseCode": "50002810095",
 *       "teachClassId": 1111111124960363, "teachClassCode": "5000281009505",
 *       "times": [{ "dayOfWeek": 4, "timeStart": 5, "timeEnd": 6, "weeks": [1,3,5],
 *                   "roomIdI18n": "南201", "teacherCodeI18n": "欧凯", "teacherCode": "21158" }] } }] } }
 * ```
 *
 * 与排课服务（`timetable/major`）的差异：`weeks` 是**周次数组**而不是 `weekState` 掩码，
 * 教室/教师在 `times[]` 里叫 `roomIdI18n` / `teacherCodeI18n`。旧扁平格式仍兼容。
 */

export const TONGJI_STUDENT_ADAPTER_ID = 'tongji-student';
export const TONGJI_STUDENT_ADAPTER_VERSION = '3.1.0';

export const TONGJI_ORIGIN = 'https://1.tongji.edu.cn';

/** 排课服务格式（扁平数组，`weekState` 掩码）—— 兼容保留。 */
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
}

/** 选课服务格式：`selectedCourses[].course.times[]`。 */
interface RawTime {
  dayOfWeek?: unknown;
  timeStart?: unknown;
  timeEnd?: unknown;
  weeks?: unknown;
  value?: unknown;
  newValue?: unknown;
  roomIdI18n?: unknown;
  teacherCodeI18n?: unknown;
  teacherCode?: unknown;
}

interface RawSelectedCourse {
  course?: {
    courseCode?: unknown;
    newCourseCode?: unknown;
    courseName?: unknown;
    credits?: unknown;
    calendarId?: unknown;
    calendarName?: unknown;
    teachClassId?: unknown;
    teachClassCode?: unknown;
    times?: unknown;
  };
}

/** 报表服务格式：`data[].timeTableList[]`（课表页真正调的那条接口，见 classify 注释）。 */
interface RawReportTime {
  dayOfWeek?: unknown;
  timeStart?: unknown;
  timeEnd?: unknown;
  weeks?: unknown;
  roomIdI18n?: unknown;
  roomLable?: unknown;
  classRoomName?: unknown;
  teacherName?: unknown;
  teacherCode?: unknown;
}

interface RawReportCourse {
  teachingClassId?: unknown;
  classCode?: unknown;
  courseCode?: unknown;
  courseName?: unknown;
  teacherName?: unknown;
  campus?: unknown;
  campusI18n?: unknown;
  timeTableList?: unknown;
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
  selected: RawSelectedCourse[];
  /** 报表服务（findStudentTimetab / findSchoolTimetab2）返回的课程数组。 */
  report: RawReportCourse[];
  flat: RawScheduleItem[];
  calendar: RawCalendarTerm[];
  calendarId: string | undefined;
  calendarName: string | undefined;
  sources: string[];
}

const TEACHER_RE = /([\u4e00-\u9fa5]{2,8})\((\d{3,6})\)/g;

/** 从 `value`（"课程(代码) 教师(工号) [周次] 教室"）里解析教师。 */
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

function toInt(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return Math.trunc(value);
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) return Number.parseInt(value, 10);
  return null;
}

function toStr(value: unknown): string | undefined {
  if (typeof value === 'string' && value.trim()) return value.trim();
  if (typeof value === 'number') return String(value);
  return undefined;
}

function classify(input: ImportInput): Classified {
  const result: Classified = { selected: [], report: [], flat: [], calendar: [], calendarId: undefined, calendarName: undefined, sources: [] };

  for (const { label, text } of inputTexts(input)) {
    const parsed = tryParseJson(text);
    if (parsed === null) continue;
    const raw = unwrapData(parsed);

    // 1) 选课服务：{ data: { calendarId, selectedCourses: [...] } }
    const container = asRecord(raw);
    const selected = asArray(container?.selectedCourses);
    if (selected?.length) {
      result.selected.push(...(selected.filter((x) => asRecord(x) !== null) as RawSelectedCourse[]));
      result.calendarId ??= toStr(container?.calendarId);
      for (const entry of result.selected) {
        const course = entry.course;
        result.calendarId ??= toStr(course?.calendarId);
        result.calendarName ??= toStr(course?.calendarName);
      }
      result.sources.push(`${label}:已选课程 ${selected.length} 门`);
      continue;
    }

    // 2) 报表服务：课表页真正调的那条接口。本科生
    //    `GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&studentCode=…`
    //    返回 `data: [课程…]`，每门课带 `timeTableList[]`（与选课服务的 `times[]` 同义）；
    //    研究生 `findSchoolTimetab2` 按前端源码是 `data.list`，同一套字段，这里一并认。
    const reportItems = collectReportItems(raw);
    if (reportItems.length) {
      result.report.push(...reportItems);
      result.sources.push(`${label}:报表课表 ${reportItems.length} 门`);
      continue;
    }

    const list = asArray(raw);
    if (list) {
      const objs = list.filter((x): x is Record<string, unknown> => asRecord(x) !== null);
      const scheduleItems = objs.filter((o) => 'weekState' in o && 'dayOfWeek' in o);
      if (scheduleItems.length) {
        result.flat.push(...(scheduleItems as RawScheduleItem[]));
        result.sources.push(`${label}:课表 ${scheduleItems.length} 条`);
        continue;
      }
      const calendarTerms = objs.filter((o) => 'noWeekendWorkTimes' in o || ('beginDay' in o && 'weekNum' in o));
      if (calendarTerms.length) {
        result.calendar.push(...(calendarTerms as RawCalendarTerm[]));
        result.sources.push(`${label}:校历 ${calendarTerms.length} 个学期`);
        continue;
      }
    }
  }

  return result;
}

/**
 * 从报表服务的响应里挑出"带排课时段的课程项"。
 *
 * 认两种容器：`data` 直接是数组（本科 `findStudentTimetab`，已实测），或 `data.list`
 * （研究生 `findSchoolTimetab2`，按前端源码推断，未实测）。判据是元素里有没有
 * `timeTableList` 数组 —— 它把报表格式与排课服务的扁平表区分开。
 */
function collectReportItems(raw: unknown): RawReportCourse[] {
  const list = asArray(raw) ?? asArray(asRecord(raw)?.list);
  if (!list) return [];
  return list.filter(
    (item): item is RawReportCourse =>
      asRecord(item) !== null && asArray((item as RawReportCourse).timeTableList) !== null,
  );
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

function resolveStartDate(term: RawCalendarTerm): string | undefined {
  const beginDay = toInt(term.beginDay);
  if (beginDay === null) return undefined;
  const monday = mondayOf(msToIsoDate(beginDay));
  const teachingWeekStart = toInt(term.teachingWeekStart) ?? 1;
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

/**
 * 学期解析优先级：显式校历 JSON > 内置学期表（按 calendarId）> 默认 16 周。
 */
function buildTerm(
  calendarTerm: RawCalendarTerm | null,
  calendarId: string | undefined,
  calendarName: string | undefined,
  diagnostics: Diagnostic[],
): Term {
  if (calendarTerm) {
    const startDate = resolveStartDate(calendarTerm);
    const teachingWeekEnd = toInt(calendarTerm.teachingWeekEnd);
    if (!startDate) {
      diagnostics.push(makeDiagnostic('warn', 'tongji.term.startDate', '校历缺少 beginDay，无法计算当前教学周。'));
    }
    return {
      id: String(toInt(calendarTerm.id) ?? 'unknown'),
      name: toStr(calendarTerm.fullName) ?? '',
      year: toInt(calendarTerm.year) ?? new Date().getFullYear(),
      termNo: toInt(calendarTerm.term) ?? 1,
      ...(startDate ? { startDate } : {}),
      totalWeeks: teachingWeekEnd && teachingWeekEnd > 0 ? teachingWeekEnd : (toInt(calendarTerm.weekNum) ?? 16),
      slots: toSlotList(calendarTerm),
    };
  }

  const preset = findTermPreset(calendarId);
  if (preset) return termFromPreset(preset);

  diagnostics.push(
    makeDiagnostic(
      'warn',
      'tongji.term.unknown',
      `学期 ${calendarId ?? '未知'} 不在内置学期表里：已按 16 教学周 + 内置节次时间解析，挂件不会显示"当前第几周"（可在设置里手动填开学日期）。`,
    ),
  );
  return {
    id: calendarId ?? 'unknown',
    name: calendarName ?? '',
    year: new Date().getFullYear(),
    termNo: 1,
    totalWeeks: 16,
    slots: makeDefaultSlots(),
  };
}

/** 选课服务格式 → 课程列表。 */
export function buildCoursesFromSelected(
  selected: RawSelectedCourse[],
  diagnostics: Diagnostic[],
): Course[] {
  const courses: Course[] = [];
  let skipped = 0;

  for (const entry of selected) {
    const course = entry.course;
    if (!course) continue;
    const times = asArray(course.times)?.filter((x): x is RawTime => asRecord(x) !== null) ?? [];
    if (!times.length) {
      skipped += 1;
      continue; // 军训等没有排课时段的课程不进课表
    }

    const courseCode = toStr(course.courseCode);
    const classCode = toStr(course.teachClassCode);
    const id = toStr(course.teachClassId) ?? classCode ?? courseCode ?? `course-${courses.length}`;
    const name = toStr(course.courseName) ?? '(未知课程)';

    const teachers = new Set<string>();
    // 同一格（同天 + 同起止节次 + 同教室）可能有多条 times：
    // 典型如"专业导论"按周次换老师（weeks=[10] / [11] / [1,2,3,4,13,14,15,16] …），
    // 必须合并成一块、周次取并集，否则课表上会出现一堆完全重叠的色块。
    const bySlot = new Map<string, Session>();

    for (const time of times) {
      const day = toInt(time.dayOfWeek);
      const startSlot = toInt(time.timeStart);
      const endSlot = toInt(time.timeEnd);
      if (day === null || startSlot === null || endSlot === null) continue;
      if (day < 1 || day > 7) continue;

      for (const teacher of parseTeachersFromValue(toStr(time.value) ?? toStr(time.newValue))) teachers.add(teacher);
      const single = toStr(time.teacherCodeI18n);
      const code = toStr(time.teacherCode);
      if (single && code) teachers.add(`${single}(${code})`);
      else if (single) teachers.add(single);

      const weekList = asArray(time.weeks);
      const weeks = weekList?.length ? weeksToMask(weekList.map((w) => toInt(w) ?? 0)) : fullWeekMask(16);
      const room = toStr(time.roomIdI18n);
      const key = `${day}-${startSlot}-${endSlot}-${room ?? ''}`;

      const existing = bySlot.get(key);
      if (existing) {
        existing.weeks = (existing.weeks | weeks) >>> 0;
        continue;
      }
      bySlot.set(key, {
        id: `${id}-${key}`,
        day: day as Weekday,
        startSlot,
        endSlot,
        weeks,
        ...(room ? { room } : {}),
      });
    }

    const sessions = [...bySlot.values()];
    if (!sessions.length) {
      skipped += 1;
      continue;
    }

    sessions.sort((a, b) => a.day - b.day || a.startSlot - b.startSlot);
    courses.push({
      id,
      name,
      ...(courseCode ? { courseCode } : {}),
      ...(classCode ? { teachingClassCode: classCode } : {}),
      teachers: [...teachers],
      sessions,
    });
  }

  if (skipped > 0) {
    diagnostics.push(
      makeDiagnostic('info', 'tongji.noSchedule', `有 ${skipped} 门课没有排课时段（如军训），已跳过。`),
    );
  }

  return courses.sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN') || a.id.localeCompare(b.id));
}

/**
 * 报表服务格式（`data[].timeTableList[]`，课表页真正调的那条接口）→ 课程列表。
 *
 * 与 `buildCoursesFromSelected` 的差异只有"包法"：课程在数组顶层而不是
 * `selectedCourses[].course`，排课数组叫 `timeTableList` 而不是 `times`；
 * `dayOfWeek` / `timeStart` / `timeEnd` / `weeks` 数组的语义完全一致。
 * 教室优先 `roomIdI18n`（如"北301"），空则退 `roomLable`（线上课堂 / 操场这类没有教室编号的场地）。
 */
export function buildCoursesFromReport(items: RawReportCourse[], diagnostics: Diagnostic[]): Course[] {
  const courses: Course[] = [];
  let skipped = 0;

  for (const item of items) {
    const times = (asArray(item.timeTableList) ?? []).filter((x): x is RawReportTime => asRecord(x) !== null);
    if (!times.length) {
      skipped += 1; // 军训等没有排课时段的课程不进课表
      continue;
    }

    const courseCode = toStr(item.courseCode);
    const classCode = toStr(item.classCode);
    const id = toStr(item.teachingClassId) ?? classCode ?? courseCode ?? `course-${courses.length}`;
    const name = toStr(item.courseName) ?? '(未知课程)';

    const teacherOrder: string[] = [];
    const teacherSeen = new Set<string>();
    const addTeacher = (text: string | undefined, code: string | undefined): void => {
      if (!text) return;
      // 文本里已经带工号就原样收下（不能无条件拼，否则会得到「张三(123)(123)」）
      const formatted = /\([0-9]{3,6}\)$/.test(text) || !code ? text : `${text}(${code})`;
      if (!teacherSeen.has(formatted)) {
        teacherSeen.add(formatted);
        teacherOrder.push(formatted);
      }
    };

    // 同一格（同天 + 同起止节次 + 同教室）合并、周次取并集 —— 与选课服务那条路同规则
    const bySlot = new Map<string, Session>();
    for (const time of times) {
      const day = toInt(time.dayOfWeek);
      const startSlot = toInt(time.timeStart);
      const endSlot = toInt(time.timeEnd);
      if (day === null || startSlot === null || endSlot === null) continue;
      if (day < 1 || day > 7) continue;

      addTeacher(toStr(time.teacherName), toStr(time.teacherCode));

      const weekList = asArray(time.weeks);
      const weeks = weekList?.length ? weeksToMask(weekList.map((w) => toInt(w) ?? 0)) : fullWeekMask(16);
      const room = toStr(time.roomIdI18n) ?? toStr(time.roomLable) ?? toStr(time.classRoomName);
      const key = `${day}-${startSlot}-${endSlot}-${room ?? ''}`;

      const existing = bySlot.get(key);
      if (existing) {
        existing.weeks = (existing.weeks | weeks) >>> 0;
        continue;
      }
      bySlot.set(key, {
        id: `${id}-${key}`,
        day: day as Weekday,
        startSlot,
        endSlot,
        weeks,
        ...(room ? { room } : {}),
      });
    }

    const sessions = [...bySlot.values()];
    if (!sessions.length) {
      skipped += 1;
      continue;
    }

    // 兜底教师：课程级 teacherName 是「张三,李四」这种纯名字列表（通常不带工号）
    if (!teacherOrder.length) {
      for (const teacher of (toStr(item.teacherName) ?? '').split(/[,，\s、;；]+/).filter(Boolean)) {
        if (!teacherSeen.has(teacher)) {
          teacherSeen.add(teacher);
          teacherOrder.push(teacher);
        }
      }
    }

    sessions.sort((a, b) => a.day - b.day || a.startSlot - b.startSlot);
    const campus = toStr(item.campusI18n) ?? toStr(item.campus);
    courses.push({
      id,
      name,
      ...(courseCode ? { courseCode } : {}),
      ...(classCode ? { teachingClassCode: classCode } : {}),
      teachers: teacherOrder,
      ...(campus ? { campus } : {}),
      sessions,
    });
  }

  if (skipped > 0) {
    diagnostics.push({
      level: 'info',
      code: 'tongji.noSchedule',
      message: `有 ${skipped} 门课没有排课时段（如军训），已跳过。`,
    });
  }

  // 排序规则与选课服务那条路一致（课程名 → id），两处不能走样
  return courses.sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN') || a.id.localeCompare(b.id));
}

/** 排课服务扁平格式 → 课程列表（兼容保留）。 */
export function buildCoursesFromFlat(items: RawScheduleItem[]): Course[] {
  const byId = new Map<string, Course>();

  for (const item of items) {
    const day = toInt(item.dayOfWeek);
    const startSlot = toInt(item.timeStart);
    const endSlot = toInt(item.timeEnd);
    const weeks = toInt(item.weekState);
    if (day === null || startSlot === null || endSlot === null || weeks === null) continue;
    if (day < 1 || day > 7) continue;

    const classCode = toStr(item.code);
    const courseCode = toStr(item.courseCode);
    const id = toStr(item.teachingClassId) ?? classCode ?? `${courseCode ?? 'unknown'}-${day}-${startSlot}`;
    const name = toStr(item.courseName) ?? '(未知课程)';
    const room = toStr(item.roomName);

    let course = byId.get(id);
    if (!course) {
      const fromValue = parseTeachersFromValue(toStr(item.value) ?? toStr(item.newValue));
      const codes = asArray(item.teacherCodes)?.map((c) => String(c)) ?? [];
      course = {
        id,
        name,
        ...(courseCode ? { courseCode } : {}),
        ...(classCode ? { teachingClassCode: classCode } : {}),
        teachers: fromValue.length ? fromValue : codes,
        faculty: toStr(item.facultyI18n),
        campus: toStr(item.campusI18n),
        sessions: [],
      };
      byId.set(id, course);
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

  for (const course of byId.values()) course.sessions.sort((a, b) => a.day - b.day || a.startSlot - b.startSlot);
  return [...byId.values()].sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN') || a.id.localeCompare(b.id));
}

/** 自动探测：像不像可识别的同济课表数据。 */
function detectScore(input: ImportInput): number {
  for (const { text } of inputTexts(input)) {
    if (!text.includes('selectedCourses') && !text.includes('weekState') && !text.includes('timeTableList')) continue;
    const parsed = tryParseJson(text);
    if (parsed === null) continue;
    const raw = unwrapData(parsed);
    const container = asRecord(raw);
    // 注意：用 Array.isArray 而不是 length —— 空数组也算"识别成功"，
    // 好让解析器给出"已识别但没有课表数据"这类更有用的诊断，而不是"无法识别格式"。
    if (Array.isArray(container?.selectedCourses)) return 0.98;
    if (collectReportItems(raw).length) return 0.97;
    const list = asArray(raw);
    if (
      list?.some((x) => {
        const r = asRecord(x);
        return r !== null && 'weekState' in r && 'dayOfWeek' in r;
      })
    ) {
      return 0.9;
    }
  }
  return 0;
}

export const tongjiStudentAdapter: SchoolAdapter = {
  id: TONGJI_STUDENT_ADAPTER_ID,
  displayName: '同济大学 · 1 系统个人课表',
  version: TONGJI_STUDENT_ADAPTER_VERSION,
  description:
    '解析 1 系统课表：个人课表（`data.selectedCourses[].course.times[]`）与课表页报表接口'
    + '（`data[].timeTableList[]`，如 `reportManagement/findStudentTimetab`），也兼容排课服务的扁平格式。',
  canFetch: true,

  detect(input: ImportInput): number {
    return detectScore(input);
  },

  parse(input: ImportInput, _ctx: AdapterContext): ImportResult {
    const diagnostics: Diagnostic[] = [];
    const classified = classify(input);

    // 学期 id：显式指定（抓取时从请求 URL 的 calendarId 取）优先于响应体里的 calendarId。
    // 报表格式的响应体里没有 calendarId（它只在 URL 上），所以这一条是它能拿到开学日期的前提。
    const termId = input.termId === undefined ? classified.calendarId : String(input.termId);

    const term = buildTerm(
      pickTerm(classified.calendar, termId),
      termId,
      classified.calendarName,
      diagnostics,
    );

    let courses: Course[] = [];
    if (classified.selected.length) {
      courses = buildCoursesFromSelected(classified.selected, diagnostics);
      diagnostics.push(
        makeDiagnostic('info', 'tongji.personal', `识别为个人课表：已选 ${classified.selected.length} 门课。`),
      );
    } else if (classified.report.length) {
      courses = buildCoursesFromReport(classified.report, diagnostics);
      diagnostics.push(
        makeDiagnostic('info', 'tongji.report', `识别为 1 系统课表（报表接口格式）：共 ${classified.report.length} 门课。`),
      );
    } else if (classified.flat.length) {
      courses = buildCoursesFromFlat(classified.flat);
      diagnostics.push(
        makeDiagnostic('info', 'tongji.flat', `按排课服务格式解析 ${classified.flat.length} 条记录。`),
      );
    } else {
      diagnostics.push(
        makeDiagnostic(
          'error',
          'tongji.schedule.missing',
          '没有找到课表数据：需要 `data.selectedCourses[].course.times[]`（个人课表）或含 `weekState/dayOfWeek` 的数组。',
        ),
      );
    }

    const sessionCount = courses.reduce((n, course) => n + course.sessions.length, 0);
    if (courses.length) {
      diagnostics.push(
        makeDiagnostic(
          'info',
          'tongji.summary',
          `导入 ${courses.length} 门课程 / ${sessionCount} 条上课安排（学期 ${term.name || term.id}，共 ${term.totalWeeks} 教学周）。`,
        ),
      );
    }

    return {
      adapterId: TONGJI_STUDENT_ADAPTER_ID,
      adapterName: tongjiStudentAdapter.displayName,
      adapterVersion: TONGJI_STUDENT_ADAPTER_VERSION,
      term,
      courses,
      preselect: [],
      diagnostics,
      meta: {
        sources: classified.sources,
        calendarId: classified.calendarId ?? null,
        calendarName: classified.calendarName ?? null,
      },
    };
  },
};
