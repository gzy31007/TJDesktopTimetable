import { makeDefaultSlots, type Course, type Term, type Weekday } from '../model.js';
import { weeksToMask } from '../weeks.js';
import { asArray, asRecord, inputTexts, makeDiagnostic, tryParseJson, unwrapData, type ImportInput, type ImportResult, type SchoolAdapter } from './types.js';

/**
 * 同济 1 系统「个人课表」适配器 —— **占位实现，等待抓包结果**。
 *
 * 现状：`timetable/major` 只给出专业培养计划的全部平行教学班（见 `tongji-major.ts`），
 * 学生"实际选了哪个教学班"需要另一个接口（形如 `timetable/student` / 已选课表）。
 *
 * 接入步骤（拿到抓包结果后）：
 * 1. 在 `detect()` 里识别该接口的响应特征（例如 data 数组元素带 `studentCode` + `weekState`）；
 * 2. 在 `parse()` 里把每条记录映射成 `Course`（`courses` 直接填充，不需要用户勾选）；
 * 3. 把 `preselect` 填成这些教学班的 id；
 * 4. 在 `test/` 下加一份脱敏后的 fixture 与 spec。
 */

export const TONGJI_STUDENT_ADAPTER_ID = 'tongji-student';
export const TONGJI_STUDENT_ADAPTER_VERSION = '0.0.1';

function toInt(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return Math.trunc(value);
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) return Number.parseInt(value, 10);
  return null;
}

function emptyTerm(): Term {
  return { id: 'unknown', name: '', year: new Date().getFullYear(), termNo: 1, totalWeeks: 16, slots: makeDefaultSlots() };
}

export const tongjiStudentAdapter: SchoolAdapter = {
  id: TONGJI_STUDENT_ADAPTER_ID,
  displayName: '同济大学 · 个人课表（待接入）',
  version: TONGJI_STUDENT_ADAPTER_VERSION,
  description:
    '个人已选课表接口的占位适配器：拿到抓包结果后补上字段映射，即可跳过"勾选教学班"这一步。当前不会匹配任何输入。',
  canFetch: false,

  // 有意返回 0：没有真实抓包样本前不猜格式，避免误吞其他适配器的输入。
  detect(_input: ImportInput): number {
    return 0;
  },

  parse(input: ImportInput): ImportResult {
    const diagnostics: ImportResult['diagnostics'] = [
      makeDiagnostic(
        'error',
        'tongji.student.todo',
        '个人课表适配器尚未实现：请提供抓包得到的接口响应样本（脱敏），或改用「专业课表」适配器 + 勾选教学班。',
      ),
    ];

    for (const { text } of inputTexts(input)) {
      const parsed = tryParseJson(text);
      if (parsed === null) continue;
      const list = asArray(unwrapData(parsed));
      if (!list?.length) continue;
      const first = asRecord(list[0]);
      if (first) {
        diagnostics.push(
          makeDiagnostic('info', 'tongji.student.fields', `样本字段：${Object.keys(first).slice(0, 40).join(', ')}`),
        );
      }
      break;
    }

    return {
      adapterId: TONGJI_STUDENT_ADAPTER_ID,
      adapterName: tongjiStudentAdapter.displayName,
      adapterVersion: TONGJI_STUDENT_ADAPTER_VERSION,
      term: emptyTerm(),
      courses: [],
      preselect: [],
      diagnostics,
    };
  },
};

/** 预留：把个人课表记录转成课程（接入时使用）。 */
export function toStudentCourse(input: {
  id: string;
  name: string;
  day: number;
  startSlot: number;
  endSlot: number;
  weeks: number;
  room?: string;
  teachers?: string[];
}): Course {
  return {
    id: input.id,
    name: input.name,
    teachers: input.teachers ?? [],
    sessions: [
      {
        id: `${input.id}-${input.day}-${input.startSlot}-${input.endSlot}`,
        day: input.day as Weekday,
        startSlot: input.startSlot,
        endSlot: input.endSlot,
        weeks: weeksToMask([input.weeks]),
        ...(input.room ? { room: input.room } : {}),
      },
    ],
  };
}

/** 预留：判断是否疑似个人课表响应。 */
export function looksLikeStudentTimetable(value: unknown): boolean {
  const list = asArray(unwrapData(value));
  if (!list?.length) return false;
  return list.some((item) => {
    const record = asRecord(item);
    return record !== null && 'weekState' in record && 'dayOfWeek' in record && 'studentCode' in record;
  });
}

/** 预留：节次编号工具，接入时复用。 */
export function slotRangeOf(start: unknown, end: unknown): { start: number; end: number } | null {
  const s = toInt(start);
  const e = toInt(end);
  if (s === null || e === null) return null;
  return { start: s, end: e };
}
