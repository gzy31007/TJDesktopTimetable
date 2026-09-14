import {
  makeDefaultSlots,
  slotsFromList,
  TIMETABLE_SCHEMA_VERSION,
  type Course,
  type Session,
  type Slot,
  type Term,
  type Timetable,
  type Weekday,
} from '../model.js';
import { normalizeMask, weeksToMask } from '../weeks.js';
import { asArray, asRecord, inputTexts, makeDiagnostic, tryParseJson, type ImportInput, type ImportResult, type SchoolAdapter } from './types.js';
import { classesToCourses, isClassesShape } from './preview-html.js';

/**
 * 通用适配器 —— 其他学校 / 手工整理课表的最低门槛入口。
 *
 * 支持两种自描述 JSON：
 *
 * 1. 本项目导出格式（推荐）：
 * ```json
 * { "schemaVersion": 1,
 *   "term": { "id": "2026-1", "year": 2026, "termNo": 1, "totalWeeks": 16, "startDate": "2026-09-14",
 *             "slots": [{ "index": 1, "begin": "08:00", "end": "08:45" }] },
 *   "courses": [{ "id": "c1", "name": "高等数学", "teachers": ["张三"],
 *                 "sessions": [{ "day": 1, "startSlot": 1, "endSlot": 2, "weeks": 65535, "room": "南101" }] }] }
 * ```
 *
 * 2. 简化的"周次列表"写法（`weeks` 用数组，`day` 用 1-7）：
 * ```json
 * { "term": { "totalWeeks": 16 }, "courses": [{ "name": "大学物理", "sessions": [{ "day": 3, "startSlot": 5, "endSlot": 6, "weeks": [1,3,5] }] }] }
 * ```
 */

export const GENERIC_ADAPTER_ID = 'generic-json';
export const GENERIC_ADAPTER_VERSION = '1.0.0';

function toInt(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return Math.trunc(value);
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) return Number.parseInt(value, 10);
  return null;
}

function toStringArray(value: unknown): string[] {
  if (Array.isArray(value)) return value.map((v) => String(v));
  if (typeof value === 'string' && value.trim()) return value.split(/[、,，;；]/).map((s) => s.trim()).filter(Boolean);
  return [];
}

function parseWeeks(value: unknown): number {
  // 数组 = 周次列表（[1,3,5]）；数字 = 位掩码（65535）
  if (Array.isArray(value)) return weeksToMask(value.map((v) => toInt(v) ?? 0));
  const mask = toInt(value);
  if (mask !== null) return normalizeMask(mask);
  return 0;
}

function parseSlots(value: unknown): Slot[] | null {
  const list = asArray(value);
  if (!list) return null;
  const slots: Slot[] = [];
  for (const entry of list) {
    const record = asRecord(entry);
    if (!record) continue;
    const index = toInt(record.index);
    if (index === null || index <= 0) continue;
    slots.push({
      index,
      begin: typeof record.begin === 'string' ? record.begin : '',
      end: typeof record.end === 'string' ? record.end : '',
    });
  }
  return slots.length ? slotsFromList(slots) : null;
}

function parseTerm(value: unknown): Term {
  const record = asRecord(value) ?? {};
  const totalWeeks = toInt(record.totalWeeks) ?? 16;
  return {
    id: record.id != null ? String(record.id) : 'imported',
    name: typeof record.name === 'string' ? record.name : '',
    year: toInt(record.year) ?? new Date().getFullYear(),
    termNo: toInt(record.termNo) ?? 1,
    ...(typeof record.startDate === 'string' && record.startDate ? { startDate: record.startDate } : {}),
    totalWeeks,
    slots: parseSlots(record.slots) ?? makeDefaultSlots(),
  };
}

function parseCourses(value: unknown): Course[] {
  const list = asArray(value);
  if (!list) return [];
  const courses: Course[] = [];

  list.forEach((entry, index) => {
    const record = asRecord(entry);
    if (!record) return;
    const name = typeof record.name === 'string' && record.name ? record.name : `课程 ${index + 1}`;
    const id = record.id != null ? String(record.id) : `${name}-${index}`;
    const sessions: Session[] = [];

    const sessionList = asArray(record.sessions) ?? asArray(record.periods) ?? [];
    for (const rawSession of sessionList) {
      const s = asRecord(rawSession);
      if (!s) continue;
      const day = toInt(s.day);
      const startSlot = toInt(s.startSlot) ?? toInt(s.start);
      const endSlot = toInt(s.endSlot) ?? toInt(s.end);
      const weeks = parseWeeks(s.weeks ?? s.weeksMask);
      if (day === null || startSlot === null || endSlot === null || weeks === 0) continue;
      if (day < 1 || day > 7) continue;
      const room = typeof s.room === 'string' && s.room ? s.room : typeof record.room === 'string' ? record.room : undefined;
      sessions.push({
        id: `${id}-${day}-${startSlot}-${endSlot}-${weeks}`,
        day: day as Weekday,
        startSlot,
        endSlot,
        weeks,
        ...(room ? { room } : {}),
      });
    }

    if (!sessions.length) return;
    courses.push({
      id,
      name,
      courseCode: record.courseCode != null ? String(record.courseCode) : undefined,
      teachingClassCode: record.teachingClassCode != null ? String(record.teachingClassCode) : undefined,
      teachers: toStringArray(record.teachers),
      faculty: typeof record.faculty === 'string' ? record.faculty : undefined,
      campus: typeof record.campus === 'string' ? record.campus : undefined,
      sessions,
    });
  });

  return courses;
}

function isTimetableShape(value: unknown): boolean {
  const record = asRecord(value);
  if (!record) return false;
  const courses = asArray(record.courses);
  if (!courses) return false;
  const first = asRecord(courses[0]);
  return first !== null && ('sessions' in first || 'periods' in first);
}

export const genericJsonAdapter: SchoolAdapter = {
  id: GENERIC_ADAPTER_ID,
  displayName: '通用 JSON（其他学校 / 手工整理）',
  version: GENERIC_ADAPTER_VERSION,
  description:
    '解析 `{term, courses:[{sessions}]}` 或 `{term, classes}` 两种通用 JSON，可直接作为新学校适配器的过渡方案。',
  canFetch: false,

  detect(input: ImportInput): number {
    for (const { text } of inputTexts(input)) {
      const parsed = tryParseJson(text);
      if (parsed === null) continue;
      const record = asRecord(parsed);
      if (!record) continue;
      if (toInt(record.schemaVersion) === TIMETABLE_SCHEMA_VERSION && isTimetableShape(parsed)) return 0.9;
      if (isTimetableShape(parsed)) return 0.6;
      if (isClassesShape(parsed)) return 0.4;
    }
    return 0;
  },

  parse(input: ImportInput): ImportResult {
    const diagnostics: ImportResult['diagnostics'] = [];
    let payload: unknown = null;
    for (const { text } of inputTexts(input)) {
      const parsed = tryParseJson(text);
      if (parsed !== null) {
        payload = parsed;
        break;
      }
    }

    const record = asRecord(payload);
    if (!record) {
      diagnostics.push(makeDiagnostic('error', 'generic.invalid', '不是合法的 JSON 对象。'));
      return emptyResult(diagnostics);
    }

    const term = parseTerm(record.term);

    if (isClassesShape(payload)) {
      const { term: parsedTerm, courses } = classesToCourses(payload);
      diagnostics.push(
        makeDiagnostic('info', 'generic.classes', `按 classes 结构导入 ${courses.length} 个教学班。`),
      );
      return {
        adapterId: GENERIC_ADAPTER_ID,
        adapterName: genericJsonAdapter.displayName,
        adapterVersion: GENERIC_ADAPTER_VERSION,
        term: parsedTerm,
        courses: [],
        candidates: courses,
        preselect: [],
        diagnostics,
      };
    }

    const courses = parseCourses(record.courses);
    if (!courses.length) {
      diagnostics.push(makeDiagnostic('error', 'generic.empty', '没有解析出任何含 `sessions` 或 `periods` 的课程。'));
    } else {
      diagnostics.push(makeDiagnostic('info', 'generic.summary', `导入 ${courses.length} 门课程。`));
    }

    return {
      adapterId: GENERIC_ADAPTER_ID,
      adapterName: genericJsonAdapter.displayName,
      adapterVersion: GENERIC_ADAPTER_VERSION,
      term,
      courses,
      preselect: [],
      diagnostics,
    };
  },
};

function emptyResult(diagnostics: ImportResult['diagnostics']): ImportResult {
  return {
    adapterId: GENERIC_ADAPTER_ID,
    adapterName: genericJsonAdapter.displayName,
    adapterVersion: GENERIC_ADAPTER_VERSION,
    term: { id: 'unknown', name: '', year: new Date().getFullYear(), termNo: 1, totalWeeks: 16, slots: makeDefaultSlots() },
    courses: [],
    preselect: [],
    diagnostics,
  };
}
