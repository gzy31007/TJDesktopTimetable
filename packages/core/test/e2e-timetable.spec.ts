import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  buildBoard,
  importTimetable,
  materializeTimetable,
  sessionsOnDate,
  termWeekAt,
  weeksToMask,
  type Course,
} from '../src/index.js';

/**
 * 端到端：真实的个人课表响应（选课服务 `getDataBk`）→ 导入 → 直接成课表 → 布局 → 时间推算。
 *
 * fixture 来自一次真实抓包，已脱敏（剔除学生姓名/学号与无关字段）。
 */

const personal = readFileSync(new URL('../fixtures/tongji-2026-1-personal.json', import.meta.url), 'utf8');

describe('端到端：个人课表（selectedCourses 格式）', () => {
  const result = importTimetable({ text: personal, importedAt: '2026-09-01T00:00:00.000Z' });

  it('自动探测到同济个人课表适配器', () => {
    expect(result.adapterId).toBe('tongji-student');
    expect(result.diagnostics.some((d) => d.code === 'tongji.personal' && d.level === 'info')).toBe(true);
  });

  it('学期来自 calendarId + 内置学期表（无需校历接口）', () => {
    expect(result.term.id).toBe('122');
    expect(result.term.name).toBe('2026-2027学年第1学期');
    expect(result.term.startDate).toBe('2026-09-14');
    expect(result.term.totalWeeks).toBe(16);
    expect(result.term.slots).toHaveLength(11);
    expect(result.term.slots[0]).toEqual({ index: 1, begin: '08:00', end: '08:45' });
    expect(result.meta?.calendarId).toBe('122');
  });

  it('没有排课时段的课程（军训）被跳过并给出提示', () => {
    const noSchedule = result.diagnostics.find((d) => d.code === 'tongji.noSchedule');
    expect(noSchedule?.message).toContain('1 门课');
    expect(result.courses).toHaveLength(14);
    expect(result.courses.some((c) => c.name === '军训')).toBe(false);
  });

  it('课程字段映射正确（名称/代码/教学班/教师/教室/周次数组）', () => {
    const physics = result.courses.find((c) => c.name === '大学物理B2(I)') as Course;
    expect(physics).toBeDefined();
    expect(physics.courseCode).toBe('50002810095');
    expect(physics.teachingClassCode).toBe('5000281009505');
    expect(physics.teachers).toEqual(['欧凯(21158)']);

    const session = physics.sessions.find((s) => s.day === 4 && s.startSlot === 5);
    expect(session).toBeDefined();
    expect(session!.endSlot).toBe(6);
    expect(session!.room).toBe('南201');
    // 响应里 weeks 是数组 [1,3,5,...]，适配器转成掩码
    expect(session!.weeks).toBe(weeksToMask([1, 3, 5, 7, 9, 11, 13, 15]));
  });

  it('多时段课程合并成一门课', () => {
    const math = result.courses.find((c) => c.name.includes('高等数学')) as Course;
    expect(math.sessions.length).toBeGreaterThan(1);
  });

  it('同一格不同周次/不同老师的多条 times 合并为一块（专业导论专题授课）', () => {
    const intro = result.courses.find((c) => c.name.includes('专业导论')) as Course;
    expect(intro).toBeDefined();
    // 原始响应里是 9 条（weeks 分别为 [10]/[11]/[9]/... 与一条 1-4+13-16），合并后应为 1 块
    expect(intro.sessions).toHaveLength(1);
    const session = intro.sessions[0]!;
    expect(session.day).toBe(3);
    expect(session.startSlot).toBe(9);
    expect(session.room).toBe('北201');
    // 周次取并集 → 1-16 周全周
    expect(session.weeks).toBe(weeksToMask(Array.from({ length: 16 }, (_, i) => i + 1)));
    // 九位授课老师都要保留
    expect(intro.teachers.length).toBe(9);
  });

  it('没有教室的课（体育/美育/实验）room 为空而不是 0', () => {
    const pe = result.courses.find((c) => c.name === '体育(1)') as Course;
    expect(pe).toBeDefined();
    expect(pe.sessions[0]!.room).toBeUndefined();
  });

  it('落成课表 → 网格布局 → 当日课程与教学周', () => {
    const timetable = materializeTimetable(result);
    expect(timetable.courses).toHaveLength(14);
    expect(timetable.source.adapterId).toBe('tongji-student');

    const sessionTotal = timetable.courses.reduce((n, c) => n + c.sessions.length, 0);
    const board = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16', trimEmptySlots: true });
    expect(board.blocks).toHaveLength(sessionTotal);
    expect(board.currentWeek).toBe(1);
    expect(board.days).toHaveLength(7);

    expect(termWeekAt(timetable.term, '2026-09-14')).toBe(1);
    expect(termWeekAt(timetable.term, '2026-08-31')).toBeNull();

    const monday = sessionsOnDate(timetable.courses, timetable.term, '2026-09-14');
    expect(monday.length).toBeGreaterThan(0);
  });

  it('单双周过滤生效', () => {
    const timetable = materializeTimetable(result);
    const all = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16' });
    const even = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16', weekFilter: 'even' });
    expect(even.blocks.length).toBeLessThan(all.blocks.length);
  });

  it('同一格多门课并排（专业导论有 9 个时段，容易出现同格）', () => {
    const timetable = materializeTimetable(result);
    const board = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16' });
    expect(board.blocks.every((b) => b.colCount >= 1)).toBe(true);
  });
});

describe('兼容与降级', () => {
  it('排课服务扁平格式（weekState 掩码）仍可解析', () => {
    const flat = JSON.stringify({
      code: 200,
      data: [
        {
          teachingClassId: 111,
          code: 'X01',
          courseCode: 'X',
          courseName: '大学化学',
          dayOfWeek: 2,
          timeStart: 3,
          timeEnd: 4,
          weekState: 65535,
          roomName: '南202',
          value: '大学化学 李四(22334) 星期二3-4节 [1-16] 南202',
        },
      ],
    });
    const imported = importTimetable({ text: flat });
    expect(imported.adapterId).toBe('tongji-student');
    expect(imported.courses).toHaveLength(1);
    expect(imported.courses[0]!.sessions[0]!.room).toBe('南202');
    expect(imported.courses[0]!.teachers).toEqual(['李四(22334)']);
  });

  it('未知 calendarId 时退化为默认 16 周并提示', () => {
    const unknown = JSON.stringify({
      code: 200,
      data: {
        calendarId: 999999,
        selectedCourses: [
          {
            course: {
              courseName: '某课程',
              courseCode: 'Z',
              teachClassId: 1,
              teachClassCode: 'Z01',
              times: [{ dayOfWeek: 1, timeStart: 1, timeEnd: 2, weeks: [1, 2, 3], roomIdI18n: 'A101' }],
            },
          },
        ],
      },
    });
    const imported = importTimetable({ text: unknown });
    expect(imported.courses).toHaveLength(1);
    expect(imported.term.startDate).toBeUndefined();
    expect(imported.diagnostics.some((d) => d.code === 'tongji.term.unknown' && d.level === 'warn')).toBe(true);
    expect(imported.courses[0]!.sessions[0]!.weeks).toBe(weeksToMask([1, 2, 3]));
  });

  it('完全不含课表数据的输入报错', () => {
    const empty = JSON.stringify({ code: 200, data: { selectedCourses: [] } });
    const imported = importTimetable({ text: empty });
    expect(imported.diagnostics.some((d) => d.code === 'tongji.schedule.missing' && d.level === 'error')).toBe(true);
    expect(imported.courses).toHaveLength(0);
  });
});
