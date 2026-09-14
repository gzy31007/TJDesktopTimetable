# 适配器指南：接入一所新学校

目标：**新增一所学校 = 1 个适配器文件 + 1 行注册 + 1 份 fixture + 1 个 spec**，核心算法与全部 UI 零改动。

## 契约

```ts
// packages/core/src/adapters/types.ts
interface SchoolAdapter {
  id: string;              // 稳定 id，例如 'tongji-student'
  displayName: string;     // 导入面板展示名
  version: string;         // 语义化版本
  description: string;     // 一句话说明
  canFetch?: boolean;      // 是否支持程序内自动抓取（目前全为 false）
  detect(input: ImportInput): number;                 // 0 = 不匹配，1 = 确定
  parse(input: ImportInput, ctx: AdapterContext): ImportResult;
}
```

`ImportResult` 的两种填法：

| 数据来源 | 填法 | UI 行为 |
|---|---|---|
| 学生**已选**课表 | `courses: Course[]`（`candidates` 留空） | 直接显示课表 |
| 培养计划 / 平行班清单 | `candidates: Course[]`（`courses` 留空）+ `preselect` | 进入勾选面板，自动冲突拦截（**当前 UI 未启用**，同济已改用个人课表） |

## 步骤

### 1. 新建文件 `packages/core/src/adapters/<school>.ts`

```ts
import { makeDefaultSlots, type Course, type Term, type Weekday } from '../model.js';
import { weeksToMask } from '../weeks.js';
import {
  asArray, asRecord, inputTexts, makeDiagnostic, tryParseJson, unwrapData,
  type ImportInput, type ImportResult, type SchoolAdapter,
} from './types.js';

export const MY_SCHOOL_ID = 'myschool';
export const MY_SCHOOL_VERSION = '1.0.0';

export const mySchoolAdapter: SchoolAdapter = {
  id: MY_SCHOOL_ID,
  displayName: '某某大学 · 教务课表',
  version: MY_SCHOOL_VERSION,
  description: '解析某某大学教务导出的课程 JSON。',
  canFetch: false,

  detect(input: ImportInput): number {
    for (const { text } of inputTexts(input)) {
      const parsed = tryParseJson(text);
      const list = asArray(unwrapData(parsed));
      if (list?.length && asRecord(list[0]) && '课程特征字段' in asRecord(list[0])!) return 0.9;
    }
    return 0;
  },

  parse(input: ImportInput): ImportResult {
    const diagnostics: ImportResult['diagnostics'] = [];
    const items = inputTexts(input)
      .map(({ text }) => asArray(unwrapData(tryParseJson(text))))
      .find((list) => list && list.length) ?? [];

    const courses: Course[] = [];
    for (const raw of items) {
      const record = asRecord(raw);
      if (!record) continue;
      // TODO: 映射字段 → Course / Session
      // 周次如果是数组：weeksToMask([1, 3, 5])；如果是掩码：weeksToMask([mask])
    }

    const term: Term = {
      id: '2026-1', name: '2026-2027学年第1学期', year: 2026, termNo: 1,
      startDate: '2026-09-14', totalWeeks: 16, slots: makeDefaultSlots(),
    };

    diagnostics.push(makeDiagnostic('info', 'myschool.summary', `导入 ${courses.length} 门课程。`));

    return {
      adapterId: MY_SCHOOL_ID,
      adapterName: mySchoolAdapter.displayName,
      adapterVersion: MY_SCHOOL_VERSION,
      term,
      courses,          // 已选课表 → 直接给 courses；平行班清单 → 改成 candidates 并留空 courses
      preselect: [],
      diagnostics,
    };
  },
};
```

### 2. 注册

```ts
// packages/core/src/adapters/registry.ts
export const builtinAdapters: readonly SchoolAdapter[] = [
  tongjiMajorAdapter,
  tongjiStudentAdapter,
  mySchoolAdapter,      // ← 加在这里，注意顺序：具体学校优先于通用格式
  previewHtmlAdapter,
  genericJsonAdapter,
];
```

并在 `packages/core/src/index.ts` 里 export（可选，供 UI 单独引用）。

### 3. 放 fixture

```
packages/core/fixtures/<school>-<term>.raw.json     # 脱敏后的真实响应
packages/core/fixtures/<school>-<term>.expected.json # 期望解析结果（可选，但强烈建议）
```

**必须脱敏**：删除学号、姓名、cookie、token、`Authorization` 头。仓库是公开的。

### 4. 写 spec

`packages/core/test/<school>-adapter.spec.ts`，至少覆盖：

1. `detect()` 对真实样本返回高分，对无关 JSON 返回 0；
2. 解析条目数 / 课程数正确；
3. 关键字段逐条与 `expected.json` 一致（黄金测试）；
4. 缺少辅助文件（校历等）时的降级行为与诊断信息；
5. `materializeTimetable(result, 勾选)` 的结果符合预期。

参考 `test/tongji-adapter.spec.ts`。

### 5. 跑测试

```bash
pnpm test && pnpm typecheck
```

## 常见坑

- **`detect()` 别写太宽**：`0.4` 以上的通用匹配会和 `generic` / `preview` 抢输入。宁可窄一点，让用户在导入面板手动选。
- **周次**：先确认抓包数据是"数组"还是"位掩码"。掩码直接用 `weeksToMask([mask])`；数组用 `weeksToMask([1,3,5])`。
- **星期**：国内教务多数是 1–7（7 = 周日），但也有 0–6（0 = 周日）；转换时别搞混。
- **节次**：确认节次是"第几节"而不是"第几大节"；如果学校用 `1-2 节 = 第一节大节`，需要换算成连续节次。
- **时区**：时间戳如果是"当地午夜"，用 `msToIsoDate(ms, 480)`（东八区）换算，否则会差一天。
- **平行班**：如果接口返回的是"可选班级清单"而不是"我已选的课"，务必走 `candidates` 路径，否则用户会看到一堆不是自己的课。
- **适配器版本**：字段映射变化时递增 `version`，便于排查用户导入的历史数据。
