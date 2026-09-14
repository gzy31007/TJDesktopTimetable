import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  buildBoard,
  importTimetable,
  materializeTimetable,
  sessionsOnDate,
  weeksToMask,
  type Course,
} from '../src/index.js';

/**
 * 端到端：同济个人课表 JSON（模拟 1 系统响应）→ 导入 → 直接成课表 → 渲染布局 → 时间推算。
 *
 * 覆盖"导入即用"主链路（不再有教学班勾选环节）。
 */

const calendar = readFileSync(new URL('../fixtures/tongji-school-calendar.json', import.meta.url), 'utf8');

const ALL = 65535;
const ODD = weeksToMask([1, 3, 5, 7, 9, 11, 13, 15]);

/** 模拟 1 系统「我的课表」响应：3 门课，其中一门单周上课。 */
const personalTimetable = JSON.stringify({
  code: 200,
  msg: '',
  data: [
    {
      teachingClassId: 1111111124960511,
      code: '5000295005512',
      courseCode: '50002950055',
      courseName: '习近平新时代中国特色社会主义思想概论',
      dayOfWeek: 4,
      timeStart: 5,
      timeEnd: 7,
      weekState: ALL,
      roomName: '一教126',
      facultyI18n: '马克思主义学院',
      campusI18n: '四平路校区',
      value: '习近平新时代中国特色社会主义思想概论 张文彬(10083) 星期四5-7节 [1-16] 一教126',
    },
    {
      teachingClassId: 1111111124959692,
      code: '32000105',
      courseCode: '320001',
      courseName: '体育(1)',
      dayOfWeek: 1,
      timeStart: 1,
      timeEnd: 2,
      weekState: ALL,
      roomName: '游泳馆',
      facultyI18n: '体育部',
      campusI18n: '四平路校区',
      value: '体育(1) 秦海权(09102) 星期一1-2节 [1-16] 游泳馆',
    },
    {
      teachingClassId: 1111111124900001,
      code: '5000244018901',
      courseCode: '50002440189',
      courseName: '专业导论（计算机与电子类）',
      dayOfWeek: 3,
      timeStart: 9,
      timeEnd: 10,
      weekState: ODD,
      roomName: '北201',
      facultyI18n: '电子与信息工程学院',
      campusI18n: '四平路校区',
      value: '专业导论（计算机与电子类） 赵君峤(13182) 星期三9-10节 [1, 3, 5, 7, 9, 11, 13, 15] 北201',
    },
  ],
});

const files = [
  { name: 'my-timetable.json', text: personalTimetable },
  { name: 'calendar.json', text: calendar },
];

describe('端到端：个人课表导入即用', () => {
  const result = importTimetable({ files, importedAt: '2026-09-01T00:00:00.000Z' });

  it('自动探测到同济个人课表适配器', () => {
    expect(result.adapterId).toBe('tongji-student');
  });

  it('直接产出课程，不产生候选池（无需勾选）', () => {
    expect(result.candidates).toBeUndefined();
    expect(result.courses).toHaveLength(3);
    expect(result.courses.every((course) => course.sessions.length > 0)).toBe(true);
    expect(result.diagnostics.some((d) => d.code === 'tongji.summary' && d.level === 'info')).toBe(true);
    expect(result.diagnostics.some((d) => d.code === 'tongji.looksLikePlan')).toBe(false);
  });

  it('学期信息取自校历', () => {
    expect(result.term.id).toBe('122');
    expect(result.term.startDate).toBe('2026-09-14');
    expect(result.term.totalWeeks).toBe(16);
    expect(result.term.slots).toHaveLength(11);
    expect(result.term.slots[0]).toEqual({ index: 1, begin: '08:00', end: '08:45' });
  });

  it('落成课表 → 网格布局 → 当日课程', () => {
    const timetable = materializeTimetable(result);
    expect(timetable.courses).toHaveLength(3);
    expect(timetable.source.adapterId).toBe('tongji-student');

    const sessionTotal = timetable.courses.reduce((n, course) => n + course.sessions.length, 0);
    const board = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16', trimEmptySlots: true });
    expect(board.blocks).toHaveLength(sessionTotal);
    expect(board.currentWeek).toBe(1);
    expect(board.days).toHaveLength(7);

    const monday = sessionsOnDate(timetable.courses, timetable.term, '2026-09-14');
    expect(monday.map((o) => o.course.name)).toEqual(['体育(1)']);

    // 周三 9-10 节是单周课：第 1 周有、第 2 周没有
    expect(sessionsOnDate(timetable.courses, timetable.term, '2026-09-16')).toHaveLength(1);
    expect(sessionsOnDate(timetable.courses, timetable.term, '2026-09-23')).toHaveLength(0);

    // 假期
    expect(sessionsOnDate(timetable.courses, timetable.term, '2026-08-31')).toHaveLength(0);
  });

  it('周次过滤：单双周会筛掉对应课程', () => {
    const timetable = materializeTimetable(result);
    const all = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16' });
    const even = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16', weekFilter: 'even' });
    expect(even.blocks.length).toBeLessThan(all.blocks.length);
    expect(even.hiddenSessions).toBeGreaterThan(0);
  });

  it('没有校历时退化为内置节次 + 16 周并给出提示', () => {
    const degraded = importTimetable({ text: personalTimetable });
    expect(degraded.adapterId).toBe('tongji-student');
    expect(degraded.term.totalWeeks).toBe(16);
    expect(degraded.term.startDate).toBeUndefined();
    expect(degraded.courses).toHaveLength(3);
    expect(degraded.diagnostics.some((d) => d.code === 'tongji.term.missing' && d.level === 'warn')).toBe(true);
  });

  it('看起来像培养计划的数据会给出提示', () => {
    const planLike = JSON.stringify({
      code: 200,
      data: [
        { teachingClassId: 1, courseCode: 'A', courseName: '课程A', dayOfWeek: 1, timeStart: 1, timeEnd: 2, weekState: ALL },
        { teachingClassId: 2, courseCode: 'A', courseName: '课程A', dayOfWeek: 1, timeStart: 3, timeEnd: 4, weekState: ALL },
        { teachingClassId: 3, courseCode: 'B', courseName: '课程B', dayOfWeek: 2, timeStart: 1, timeEnd: 2, weekState: ALL },
        { teachingClassId: 4, courseCode: 'B', courseName: '课程B', dayOfWeek: 2, timeStart: 3, timeEnd: 4, weekState: ALL },
        { teachingClassId: 5, courseCode: 'C', courseName: '课程C', dayOfWeek: 3, timeStart: 1, timeEnd: 2, weekState: ALL },
        { teachingClassId: 6, courseCode: 'C', courseName: '课程C', dayOfWeek: 3, timeStart: 3, timeEnd: 4, weekState: ALL },
      ],
    });
    const imported = importTimetable({ text: planLike });
    expect(imported.diagnostics.some((d) => d.code === 'tongji.looksLikePlan' && d.level === 'warn')).toBe(true);
    expect(imported.courses).toHaveLength(6);
  });

  it('同一教学班的多个时段会合并成一门课', () => {
    const multi = JSON.stringify({
      code: 200,
      data: [
        { teachingClassId: 9, code: 'X01', courseCode: 'X', courseName: '高等数学', dayOfWeek: 1, timeStart: 1, timeEnd: 2, weekState: ALL, roomName: '南101' },
        { teachingClassId: 9, code: 'X01', courseCode: 'X', courseName: '高等数学', dayOfWeek: 3, timeStart: 3, timeEnd: 4, weekState: ALL, roomName: '南101' },
      ],
    });
    const imported = importTimetable({ text: multi });
    expect(imported.courses).toHaveLength(1);
    const course = imported.courses[0] as Course;
    expect(course.sessions).toHaveLength(2);
    expect(course.sessions.map((s) => s.day)).toEqual([1, 3]);
  });
});
