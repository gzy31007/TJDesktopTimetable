import { genericJsonAdapter } from './generic.js';
import { previewHtmlAdapter } from './preview-html.js';
import { tongjiMajorAdapter } from './tongji-major.js';
import { tongjiStudentAdapter } from './tongji-student.js';
import type { ImportInput, SchoolAdapter } from './types.js';

/** 适配器注册表：新增学校 = 实现 `SchoolAdapter` + 在这里登记。 */
export interface AdapterRegistry {
  list(): readonly SchoolAdapter[];
  get(id: string): SchoolAdapter | undefined;
  /** 返回匹配度最高的适配器（`score > 0` 才算命中）。 */
  best(input: ImportInput): { adapter: SchoolAdapter; score: number } | null;
}

export function createRegistry(adapters: readonly SchoolAdapter[]): AdapterRegistry {
  const items = [...adapters];
  return {
    list: () => items,
    get: (id) => items.find((a) => a.id === id),
    best(input) {
      let winner: { adapter: SchoolAdapter; score: number } | null = null;
      for (const adapter of items) {
        let score = 0;
        try {
          score = adapter.detect(input);
        } catch {
          score = 0;
        }
        if (score > 0 && (!winner || score > winner.score)) winner = { adapter, score };
      }
      return winner;
    },
  };
}

/** 内置适配器（按优先级：具体学校优先于通用格式）。 */
export const builtinAdapters: readonly SchoolAdapter[] = [
  tongjiMajorAdapter,
  tongjiStudentAdapter,
  previewHtmlAdapter,
  genericJsonAdapter,
];

export const defaultRegistry: AdapterRegistry = createRegistry(builtinAdapters);
