import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { importTimetable, materializeTimetable, buildBoard, weeksToMask, fullWeekMask, evenWeekMask, oddWeekMask } from '../src/index.js';
import type { Course, ImportResult } from '../src/index.js';

/**
 * 同济**课表页报表接口**格式的验收（2026-09-16 补），与 C# 侧
 * `TongjiReportFormatTests.cs` 逐条对齐、共用同一份脱敏 fixture。
 *
 * 数据源是课表页真正调的那条接口：
 * `GET /api/electionservice/reportManagement/findStudentTimetab?calendarId=…&studentCode=…`
 * （研究生是 `findSchoolTimetab2`），响应是 `data: [课程…]`，每门课带 `timeTableList[]`。
 * 它与选课服务的 `data.selectedCourses[].course.times[]` 是**同一套语义、不同包法**。
 *
 * **学期从哪来**：报表响应体里没有 `calendarId`（只在请求 URL 上），所以抓取时要把 URL 里的
 * `calendarId` 通过 `termId` 传进来 —— 这正是下面两个用例的差别。
 */
const reportJson = readFileSync(new URL('../fixtures/tongji-2026-1-report.json', import.meta.url), 'utf8');

const importReport = (termId?: string): ImportResult =>
  importTimetable({ text: reportJson, ...(termId ? { termId } : {}) });

const find = (result: ImportResult, name: string): Course => {
  const course = result.courses.find((c) => c.name === name);
  if (!course) throw new Error(`missing course ${name}`);
  return course;
};

describe('同济报表接口格式（findStudentTimetab）', () => {
  it('被识别为同济课表，并跳过没有排课时段的课程', () => {
    const result = importReport('122');

    expect(result.adapterId).toBe('tongji-student');
    expect(result.diagnostics.some((d) => d.code === 'tongji.report' && d.level === 'info')).toBe(true);
    expect(result.diagnostics.some((d) => d.code === 'tongji.summary')).toBe(true);
    expect(result.diagnostics.some((d) => d.code === 'tongji.noSchedule' && d.message.includes('1 门'))).toBe(true);

    // fixture 里 15 门课、27 条 timeTableList；「军训」没有时段（-1 门），
    // 「专业导论」同一格 9 条按周次换老师被合并成 1 块（-8 条）→ 14 门 / 19 条
    expect(result.courses).toHaveLength(14);
    expect(result.courses.reduce((n, c) => n + c.sessions.length, 0)).toBe(19);
    expect(result.courses.some((c) => c.name === '军训')).toBe(false);
  });

  it('教室、教师与校区按实测规则映射', () => {
    const result = importReport('122');

    const linear = find(result, '线性代数B');
    expect(linear.id).toBe('9000000000000001');
    expect(linear.teachingClassCode).toBe('12201006');
    expect(linear.teachers).toEqual(['教师M(10008)']);
    expect(linear.campus).toBe('四平路校区');
    expect(linear.sessions.map((s) => s.room)).toEqual(['北301', '北301']);
    expect(find(result, '社会实践').courseCode).toBe('002137');

    // roomIdI18n 为空时退到 roomLable（线上课堂 / 操场这类没有教室编号的场地）
    expect(find(result, '社会实践').sessions[0]?.room).toBe('线上课堂');
    expect(find(result, '社会实践').teachers).toEqual(['教师W(10013)']);
    expect(find(result, '体育(1)').sessions[0]?.room).toBe('爱校路足球场（2号）');
  });

  it('周次数组落成掩码，同格多教师被合并', () => {
    const result = importReport('122');

    const physics = find(result, '大学物理B2(I)');
    expect(physics.sessions.find((s) => s.day === 4)?.weeks).toBe(oddWeekMask(16));
    expect(find(result, '形势与政策(1)').sessions[0]?.weeks).toBe(weeksToMask([11, 12, 13, 14]));
    expect(find(result, '运动营养与健康').sessions[0]?.weeks).toBe(
      weeksToMask(Array.from({ length: 12 }, (_, i) => i + 1)),
    );

    const intro = find(result, '专业导论（计算机与电子类）');
    expect(intro.sessions).toHaveLength(1);
    expect(intro.sessions[0]?.day).toBe(3);
    expect(intro.sessions[0]?.startSlot).toBe(9);
    expect(intro.sessions[0]?.weeks).toBe(fullWeekMask(16));
    expect(intro.teachers).toHaveLength(9);
    expect(intro.teachers[0]).toBe('教师G(10005)');
    expect(intro.teachers).toContain('教师Z(10025)');
  });

  it('带上请求 URL 里的 calendarId 就能命中内置学期表', () => {
    const term = importReport('122').term;

    expect(term.id).toBe('122');
    expect(term.name).toBe('2026-2027学年第1学期');
    expect(term.startDate).toBe('2026-09-14');
    expect(term.totalWeeks).toBe(16);
    expect(term.slots).toHaveLength(11);
    expect(term.slots[0]?.begin).toBe('08:00');
  });

  it('没带 calendarId 时退化成 16 周并给出可读诊断', () => {
    const result = importReport();

    expect(result.term.startDate).toBeUndefined();
    expect(result.term.totalWeeks).toBe(16);
    expect(result.diagnostics.some((d) => d.code === 'tongji.term.unknown' && d.level === 'warn')).toBe(true);
    expect(result.courses).toHaveLength(14); // 课表本身照常解析
  });

  it('一路走到布局', () => {
    const timetable = materializeTimetable(importReport('122'));
    const board = buildBoard(timetable.courses, timetable.term, { trimEmptySlots: true });

    expect(board.days).toHaveLength(7);
    expect(board.rows.length).toBeGreaterThan(0);
    expect(board.blocks).toHaveLength(19);
  });

  it('等价校验：evenWeekMask 与 weeksToMask 一致（防呆）', () => {
    expect(evenWeekMask(16)).toBe(weeksToMask([2, 4, 6, 8, 10, 12, 14, 16]));
  });
});
