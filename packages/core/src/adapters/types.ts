import type { Course, Term } from '../model.js';

/**
 * 学校适配器契约 —— 这是"兼容其他学校"的扩展点。
 *
 * 新增一所学校：实现一个 `SchoolAdapter`，在 `registry.ts` 注册即可；
 * 核心算法（周次、冲突、布局、时间）与全部 UI 都不需要改动。
 */

export interface ImportFile {
  name: string;
  text: string;
}

export interface ImportInput {
  /** 直接粘贴的 JSON / HTML 文本。 */
  text?: string;
  /** 选择的文件（可多份：课表 + 校历 + 学生信息）。 */
  files?: ImportFile[];
  /** 用户显式指定的适配器 id，优先于自动探测。 */
  adapterId?: string;
  /** 校历里有多个学期时，指定要用的学期 id。 */
  termId?: string | number;
  /** 覆盖"现在"（测试用）。 */
  now?: Date;
  /** 写入 `Timetable.source.importedAt`（测试用）。 */
  importedAt?: string;
}

export type DiagnosticLevel = 'info' | 'warn' | 'error';

export interface Diagnostic {
  level: DiagnosticLevel;
  code: string;
  message: string;
  detail?: unknown;
}

export interface ImportResult {
  adapterId: string;
  adapterName: string;
  adapterVersion: string;
  term: Term;
  /**
   * 已经确定要显示的课程。
   *
   * 面向"个人已选课表"的适配器直接填充它；
   * 面向"培养计划 / 平行班清单"的适配器把它留空，改用 `candidates`。
   */
  courses: Course[];
  /**
   * 需要用户勾选的候选教学班池（含平行班）。
   *
   * 同济 `timetable/major` 返回的是专业培养计划里的全部平行班，必须走勾选流程。
   */
  candidates?: Course[];
  /** 默认勾选的教学班 id（例如"个人已选课表"接口给出的班级）。 */
  preselect: string[];
  diagnostics: Diagnostic[];
  /** 适配器附带的元信息（学生信息、学期来源等，UI 可展示）。 */
  meta?: Record<string, unknown>;
}

export interface AdapterContext {
  now: Date;
  importedAt: string;
}

export interface SchoolAdapter {
  /** 稳定 id，落盘与排查日志用。 */
  id: string;
  displayName: string;
  version: string;
  description: string;
  /** 是否支持程序内自动抓取（本阶段全部为 false，手动导入）。 */
  canFetch?: boolean;
  /** 匹配度：0 = 不匹配，1 = 确定。 */
  detect(input: ImportInput): number;
  parse(input: ImportInput, ctx: AdapterContext): ImportResult;
}

/** 收集输入里所有可解析的文本片段（粘贴的 + 各文件）。 */
export function inputTexts(input: ImportInput): { label: string; text: string }[] {
  const out: { label: string; text: string }[] = [];
  if (input.text && input.text.trim()) out.push({ label: '<粘贴内容>', text: input.text });
  for (const file of input.files ?? []) {
    if (file.text && file.text.trim()) out.push({ label: file.name, text: file.text });
  }
  return out;
}

/** 安全解析 JSON，失败返回 null。 */
export function tryParseJson(text: string): unknown | null {
  const trimmed = text.trim();
  if (!trimmed) return null;
  try {
    return JSON.parse(trimmed) as unknown;
  } catch {
    return null;
  }
}

export function asRecord(value: unknown): Record<string, unknown> | null {
  if (value && typeof value === 'object' && !Array.isArray(value)) return value as Record<string, unknown>;
  return null;
}

export function asArray(value: unknown): unknown[] | null {
  return Array.isArray(value) ? value : null;
}

/**
 * 解开同济教务常见的 `{code, msg, data}` 包装；也接受裸数组。
 */
export function unwrapData(value: unknown): unknown {
  const record = asRecord(value);
  if (record && 'data' in record) return record.data;
  return value;
}

export function makeDiagnostic(
  level: DiagnosticLevel,
  code: string,
  message: string,
  detail?: unknown,
): Diagnostic {
  return detail === undefined ? { level, code, message } : { level, code, message, detail };
}
