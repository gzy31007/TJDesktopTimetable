import { TIMETABLE_SCHEMA_VERSION, type Course, type Timetable } from './model.js';
import { defaultRegistry, type AdapterRegistry } from './adapters/registry.js';
import type { AdapterContext, ImportInput, ImportResult } from './adapters/types.js';

/**
 * 导入管线：把任意来源的数据交给适配器，产出统一模型。
 */

export class ImportError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = 'ImportError';
    this.code = code;
  }
}

/**
 * 导入课表。
 *
 * - 传 `adapterId` 时使用指定适配器；
 * - 否则自动探测（`detect()` 打分最高者），全部不匹配时抛 `ImportError('adapter.nomatch')`。
 */
export function importTimetable(input: ImportInput = {}, registry: AdapterRegistry = defaultRegistry): ImportResult {
  const ctx: AdapterContext = {
    now: input.now ?? new Date(),
    importedAt: input.importedAt ?? new Date().toISOString(),
  };

  if (input.adapterId) {
    const adapter = registry.get(input.adapterId);
    if (!adapter) throw new ImportError('adapter.unknown', `未知适配器：${input.adapterId}`);
    return adapter.parse(input, ctx);
  }

  const best = registry.best(input);
  if (!best) {
    throw new ImportError(
      'adapter.nomatch',
      '无法识别导入的数据格式：请在导入面板手动选择适配器，或确认 JSON 完整（同济课表需要 weekState/dayOfWeek 字段）。',
    );
  }
  return best.adapter.parse(input, ctx);
}

/**
 * 把导入结果 + 用户勾选的教学班落成最终课表（窗口层与存储层只消费这个结果）。
 *
 * - 有 `candidates`（平行班池）时，只保留被勾选的；
 * - 没有 `candidates`（个人课表型适配器）时，若未指定勾选则原样全用。
 */
export function materializeTimetable(result: ImportResult, selectedIds: Iterable<string> = []): Timetable {
  const selected = new Set(selectedIds);
  let courses: Course[];

  if (result.candidates) {
    courses = result.candidates.filter((c) => selected.has(c.id));
  } else if (selected.size > 0) {
    courses = result.courses.filter((c) => selected.has(c.id));
  } else {
    courses = [...result.courses];
  }

  return {
    schemaVersion: TIMETABLE_SCHEMA_VERSION,
    term: result.term,
    courses,
    source: {
      adapterId: result.adapterId,
      adapterVersion: result.adapterVersion,
      importedAt: new Date().toISOString(),
    },
  };
}

/** 勾选结果为空时的提示（UI 用）。 */
export function describeSelection(result: ImportResult, selectedIds: Iterable<string>): string {
  const count = [...selectedIds].length;
  const pool = result.candidates?.length ?? result.courses.length;
  return `已选 ${count} / ${pool} 个教学班`;
}
