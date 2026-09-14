import { TONGJI_DEFAULT_SLOTS, type Term } from './model.js';

/**
 * 同济学期表（内置快照）。
 *
 * 个人课表响应里只带 `calendarId` / `calendarName`，没有开学日期 —— 而"当前第几周"必须要它。
 * 校历接口需要复杂参数（实测 POST 返回"系统繁忙"），因此把已知学期做成内置表：
 * 命中即用；未命中则退化为"16 周 + 内置节次、不显示周次"，用户可在设置里手填开学日期。
 *
 * 数据来源：1 系统校历接口响应快照（`packages/core/fixtures/tongji-school-calendar.json`）。
 */

export interface TermPreset {
  calendarId: string;
  name: string;
  year: number;
  termNo: number;
  /** 第 1 周周一。 */
  startDate: string;
  totalWeeks: number;
}

export const TONGJI_TERM_PRESETS: readonly TermPreset[] = [
  {
    calendarId: '122',
    name: '2026-2027学年第1学期',
    year: 2026,
    termNo: 1,
    startDate: '2026-09-14',
    totalWeeks: 16,
  },
  {
    calendarId: '124',
    name: '2027-2028学年第1学期',
    year: 2027,
    termNo: 1,
    startDate: '2027-09-06',
    totalWeeks: 16,
  },
];

export function findTermPreset(calendarId: string | number | undefined): TermPreset | undefined {
  if (calendarId === undefined || calendarId === null) return undefined;
  const wanted = String(calendarId).trim();
  return TONGJI_TERM_PRESETS.find((preset) => preset.calendarId === wanted);
}

/** 由内置学期表构造 Term（节次时间用同济默认表）。 */
export function termFromPreset(preset: TermPreset): Term {
  return {
    id: preset.calendarId,
    name: preset.name,
    year: preset.year,
    termNo: preset.termNo,
    startDate: preset.startDate,
    totalWeeks: preset.totalWeeks,
    slots: TONGJI_DEFAULT_SLOTS.map((slot) => ({ ...slot })),
  };
}
