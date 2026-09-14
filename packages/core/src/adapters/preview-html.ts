import { makeDefaultSlots, TIMETABLE_SCHEMA_VERSION, type Course, type Session, type Term, type Weekday } from '../model.js';
import { highestWeek, normalizeMask } from '../weeks.js';
import { asArray, asRecord, inputTexts, makeDiagnostic, tryParseJson, type ImportInput, type ImportResult, type SchoolAdapter } from './types.js';

/**
 * 兼容器：解析本项目雏形阶段的 `select_preview.html` / `build_select_html.py` 产物。
 *
 * 数据形状：
 * ```js
 * const DATA = {
 *   term: { id: 122, year: 2026, termNo: 1 },
 *   classes: [{
 *     code, courseCode, name, teachers, room, faculty,
 *     periods: [{ day, start, end, weeksMask, weeksLabel }]
 *   }]
 * };
 * ```
 * 该结构同样用于"从网页版排课工具导出后导入桌面小组件"。
 */

export const PREVIEW_HTML_ADAPTER_ID = 'preview-html';
export const PREVIEW_HTML_ADAPTER_VERSION = '1.0.0';

/** 从 HTML/JS 文本里提取 `const DATA = {...}`（做括号配对，容忍字符串内的花括号）。 */
export function extractDataObject(text: string): unknown | null {
  const direct = tryParseJson(text);
  if (direct !== null) return direct;

  const anchor = text.search(/(?:const|var|let)\s+DATA\s*=/);
  const searchFrom = anchor >= 0 ? text.indexOf('=', anchor) : text.indexOf('{');
  if (searchFrom < 0) return null;

  const start = text.indexOf('{', searchFrom);
  if (start < 0) return null;

  let depth = 0;
  let inString: string | null = null;
  let escaped = false;
  for (let i = start; i < text.length; i += 1) {
    const ch = text[i]!;
    if (inString) {
      if (escaped) escaped = false;
      else if (ch === '\\') escaped = true;
      else if (ch === inString) inString = null;
      continue;
    }
    if (ch === '"' || ch === "'") {
      inString = ch;
      continue;
    }
    if (ch === '{') depth += 1;
    else if (ch === '}') {
      depth -= 1;
      if (depth === 0) {
        const raw = text.slice(start, i + 1);
        try {
          return JSON.parse(raw) as unknown;
        } catch {
          return null;
        }
      }
    }
  }
  return null;
}

export interface ClassesData {
  term: { id?: unknown; year?: unknown; termNo?: unknown; name?: unknown; totalWeeks?: unknown };
  classes: {
    code?: unknown;
    courseCode?: unknown;
    name?: unknown;
    teachers?: unknown;
    room?: unknown;
    faculty?: unknown;
    periods?: { day?: unknown; start?: unknown; end?: unknown; weeksMask?: unknown }[];
  }[];
}

export function isClassesShape(value: unknown): value is ClassesData {
  const record = asRecord(value);
  if (!record) return false;
  const classes = asArray(record.classes);
  if (!classes || classes.length === 0) return false;
  const first = asRecord(classes[0]);
  return first !== null && ('periods' in first || 'courseCode' in first);
}

function toInt(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return Math.trunc(value);
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) return Number.parseInt(value, 10);
  return null;
}

function splitTeachers(value: unknown): string[] {
  if (Array.isArray(value)) return value.map((v) => String(v));
  if (typeof value !== 'string') return [];
  return value
    .split(/[、,，;；]/)
    .map((s) => s.trim())
    .filter(Boolean);
}

/** `{term, classes}` → 统一模型（被 preview 与 generic 两个适配器共用）。 */
export function classesToCourses(data: ClassesData): { term: Term; courses: Course[] } {
  const termRecord = asRecord(data.term) ?? {};
  const periodMasks: number[] = [];

  const courses: Course[] = [];
  for (const cls of data.classes) {
    const id =
      (typeof cls.code === 'string' && cls.code) ||
      (cls.code != null ? String(cls.code) : '') ||
      `${cls.courseCode ?? 'unknown'}-${cls.name ?? ''}`;
    const sessions: Session[] = [];
    for (const period of cls.periods ?? []) {
      const day = toInt(period.day);
      const startSlot = toInt(period.start);
      const endSlot = toInt(period.end);
      const mask = toInt(period.weeksMask);
      if (day === null || startSlot === null || endSlot === null || mask === null) continue;
      if (day < 1 || day > 7) continue;
      const weeks = normalizeMask(mask);
      periodMasks.push(weeks);
      sessions.push({
        id: `${id}-${day}-${startSlot}-${endSlot}-${weeks}`,
        day: day as Weekday,
        startSlot,
        endSlot,
        weeks,
        ...(typeof cls.room === 'string' && cls.room ? { room: cls.room } : {}),
      });
    }
    if (!sessions.length) continue;
    courses.push({
      id,
      name: typeof cls.name === 'string' && cls.name ? cls.name : '(未知课程)',
      courseCode: cls.courseCode != null ? String(cls.courseCode) : undefined,
      teachingClassCode: typeof cls.code === 'string' ? cls.code : undefined,
      teachers: splitTeachers(cls.teachers),
      faculty: typeof cls.faculty === 'string' ? cls.faculty : undefined,
      sessions,
    });
  }

  const inferredWeeks = periodMasks.reduce((max, mask) => Math.max(max, highestWeek(mask)), 0);
  const declaredWeeks = toInt(termRecord.totalWeeks) ?? 0;
  const totalWeeks = Math.max(16, declaredWeeks, inferredWeeks);

  const term: Term = {
    id: termRecord.id != null ? String(termRecord.id) : 'unknown',
    name: typeof termRecord.name === 'string' ? termRecord.name : '',
    year: toInt(termRecord.year) ?? new Date().getFullYear(),
    termNo: toInt(termRecord.termNo) ?? 1,
    totalWeeks,
    slots: makeDefaultSlots(),
  };

  return { term, courses };
}

export const previewHtmlAdapter: SchoolAdapter = {
  id: PREVIEW_HTML_ADAPTER_ID,
  displayName: '排课工具导出（select_preview / classes JSON）',
  version: PREVIEW_HTML_ADAPTER_VERSION,
  description:
    '解析 `select_preview.html`（或 `{term, classes}` 结构的 JSON），即网页版排课工具的导出结果。所有教学班都会进入候选池。',
  canFetch: false,

  detect(input: ImportInput): number {
    for (const { text } of inputTexts(input)) {
      const trimmed = text.trim();
      if (trimmed.startsWith('{') && isClassesShape(tryParseJson(trimmed))) return 0.75;
      if (trimmed.includes('<!DOCTYPE') || trimmed.includes('const DATA')) {
        if (isClassesShape(extractDataObject(trimmed))) return 0.85;
      }
    }
    return 0;
  },

  parse(input: ImportInput): ImportResult {
    const diagnostics: ImportResult['diagnostics'] = [];
    let found: ClassesData | null = null;
    let from = '';

    for (const { label, text } of inputTexts(input)) {
      const candidate = extractDataObject(text);
      if (isClassesShape(candidate)) {
        found = candidate;
        from = label;
        break;
      }
    }

    if (!found) {
      diagnostics.push(makeDiagnostic('error', 'preview.notfound', '没有找到 `{term, classes}` 结构的数据。'));
      return {
        adapterId: PREVIEW_HTML_ADAPTER_ID,
        adapterName: previewHtmlAdapter.displayName,
        adapterVersion: PREVIEW_HTML_ADAPTER_VERSION,
        term: { id: 'unknown', name: '', year: new Date().getFullYear(), termNo: 1, totalWeeks: 16, slots: makeDefaultSlots() },
        courses: [],
        candidates: [],
        preselect: [],
        diagnostics,
      };
    }

    const { term, courses } = classesToCourses(found);
    diagnostics.push(
      makeDiagnostic('info', 'preview.summary', `从 ${from} 导入 ${courses.length} 个教学班（学期 ${term.id}）。`),
    );

    return {
      adapterId: PREVIEW_HTML_ADAPTER_ID,
      adapterName: previewHtmlAdapter.displayName,
      adapterVersion: PREVIEW_HTML_ADAPTER_VERSION,
      term,
      courses: [],
      candidates: courses,
      preselect: [],
      diagnostics,
      meta: { source: from, schemaVersion: TIMETABLE_SCHEMA_VERSION },
    };
  },
};
