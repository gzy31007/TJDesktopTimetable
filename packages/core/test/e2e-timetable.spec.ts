import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  blockRect,
  buildBoard,
  fitGeometry,
  importTimetable,
  materializeTimetable,
  sessionsOnDate,
  termWeekAt,
  weeksToMask,
  type BoardBlock,
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

/**
 * 端到端：同格撞车（构造 fixture，非真实抓包）。
 *
 * 覆盖布局里唯一无法从真实个人课表稳定复现的路径——**同一格真的挤了多门不同的课**：
 * - 周一 1-2 节三课并排（1-16 全周 / 单周 / 双周）→ `colCount = 3`、`col = 0..2`；
 * - 周三 5-6 节两课**周次完全不相交**（1-8 / 9-16）→ 视觉上照样并排（`colCount = 2`），
 *   虽然 `conflict.ts` 判定它们不冲突：**并排 ≠ 冲突**，两者是独立语义；
 * - 周一 1-3 节与 1-2 仅**部分重叠** → 分组键是"同天 + 同起止节次"，所以各自 `colCount = 1`；
 * - 同课程多条 times 同格同教室 → 合并成一块、周次取并集（`1-16`）；
 *   同格**不同教室** → 不合并，成为两块。
 *
 * 两侧（TS `packages/core` 与 C# `dotnet/TjtCore`）共用这一份 fixture，任何并排语义漂移都会在两端同时暴露。
 */

const collision = readFileSync(new URL('../fixtures/tongji-2026-1-collision.json', import.meta.url), 'utf8');

describe('端到端：同格撞车（构造 fixture）', () => {
  const result = importTimetable({ text: collision, importedAt: '2026-09-01T00:00:00.000Z' });
  const timetable = materializeTimetable(result);
  const board = buildBoard(timetable.courses, timetable.term, { today: '2026-09-14', trimEmptySlots: true });

  const cell = (day: Course['sessions'][number]['day'], startSlot: number, endSlot: number): BoardBlock[] =>
    board.blocks.filter((b) => b.day === day && b.startSlot === startSlot && b.endSlot === endSlot);

  it('整体形状：8 门课 / 9 条上课安排 / 10 行节次', () => {
    expect(result.adapterId).toBe('tongji-student');
    expect(result.term.startDate).toBe('2026-09-14');
    expect(result.term.totalWeeks).toBe(16);
    expect(result.courses).toHaveLength(8);
    expect(timetable.courses.reduce((n, c) => n + c.sessions.length, 0)).toBe(9);
    expect(board.blocks).toHaveLength(9);
    expect(board.rows.map((r) => r.index)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    // 同格撞车不是诊断项：导入侧只报告门数/时段数，不报冲突
    expect(result.diagnostics.map((d) => d.code)).toEqual(['tongji.personal', 'tongji.summary']);
  });

  it('周一 1-2 节：三门不同的课三列并排', () => {
    const three = cell(1, 1, 2).sort((a, b) => a.col - b.col);
    expect(three.map((b) => b.name)).toEqual(['测试大学物理', '测试大学英语', '测试高等数学（工科类）']);
    expect(three.map((b) => b.col)).toEqual([0, 1, 2]);
    expect(three.every((b) => b.colCount === 3 && b.stacked)).toBe(true);
    // 周次互不相同 → 三块里只有全周那块不是 special
    expect(three.find((b) => b.name === '测试高等数学（工科类）')!.special).toBe(false);
    expect(three.find((b) => b.name === '测试大学物理')!.weeksLabel).toBe('1, 3, 5, 7, 9, 11, 13, 15');
    expect(three.find((b) => b.name === '测试大学英语')!.weeksLabel).toBe('2, 4, 6, 8, 10, 12, 14, 16');
  });

  it('周一 1-3 节与 1-2 节只是部分重叠 → 不算同格，跨节那块独占整列', () => {
    const spanning = cell(1, 1, 3);
    expect(spanning).toHaveLength(1);
    expect(spanning[0]!.name).toBe('测试跨节实践（部分重叠）');
    expect(spanning[0]!.colCount).toBe(1);
    expect(spanning[0]!.stacked).toBe(false);
    // 它确实和上面三块在同一时间段里（1-3 覆盖 1-2），说明"视觉重叠"不是分组条件
    expect(cell(1, 1, 2)).toHaveLength(3);
  });

  it('周三 5-6 节：两课周次完全不相交，但视觉上照样并排（并排 ≠ 冲突）', () => {
    const pair = cell(3, 5, 6).sort((a, b) => a.col - b.col);
    expect(pair.map((b) => b.name)).toEqual(['测试并行交替甲', '测试并行交替乙']);
    expect(pair.map((b) => b.colCount)).toEqual([2, 2]);
    expect(pair.map((b) => b.weeksLabel)).toEqual(['1-8', '9-16']);
    expect(pair.every((b) => b.stacked && b.special)).toBe(true);
  });

  it('同课程同格同教室的多条 times 合并成一块（周次取并集）', () => {
    const merged = cell(5, 9, 10).find((b) => b.name === '测试同格多时段合并')!;
    expect(merged).toBeDefined();
    expect(merged.weeksLabel).toBe('1-16');
    expect(merged.special).toBe(false);
    expect(merged.teachers).toEqual(['甲(10007)', '乙(10008)', '丙(10009)']);
    // 三条 times 合并成一块：该课程只有 1 条 session
    expect(timetable.courses.find((c) => c.name === '测试同格多时段合并')!.sessions).toHaveLength(1);
  });

  it('同课程同格但教室不同 → 不合并，两块的周次各自按教室独立', () => {
    const room = cell(5, 9, 10).filter((b) => b.name === '测试同格异教室不合并');
    expect(room.map((b) => b.room)).toEqual(['北402', '北403']);
    expect(room.find((b) => b.room === '北402')!.weeksLabel).toBe('1-16');
    expect(room.find((b) => b.room === '北403')!.weeksLabel).toBe('1, 3, 5, 7, 9, 11, 13, 15');
  });

  it('周五 9-10 节共三列并排（合并后的课程 + 异教室的两块）', () => {
    const five = cell(5, 9, 10);
    expect(five.map((b) => b.colCount)).toEqual([3, 3, 3]);
    expect(five.map((b) => b.col).sort()).toEqual([0, 1, 2]);
  });

  it('三列并排的像素矩形：等宽、不重叠、宽度仍为正', () => {
    const geometry = fitGeometry(1000, board.rows.length, board.days.length);
    const three = cell(1, 1, 2).sort((a, b) => a.col - b.col);
    const rects = three.map((b) => blockRect(board, b, geometry));

    expect(rects.every((r) => r.width > 0 && r.height > 0)).toBe(true);
    // 等宽
    expect(new Set(rects.map((r) => r.width)).size).toBe(1);
    // 严格递增、且后一块的左边不小于前一块的右边 → 不重叠
    expect(rects[1]!.left).toBeGreaterThan(rects[0]!.left);
    expect(rects[2]!.left).toBeGreaterThan(rects[1]!.left);
    expect(rects[1]!.left).toBeGreaterThanOrEqual(rects[0]!.left + rects[0]!.width);
    expect(rects[2]!.left).toBeGreaterThanOrEqual(rects[1]!.left + rects[1]!.width);
    // 参考数值：cellWidth = floor((1000-74)/7) = 132 → 列宽 (132-6)/3 = 42，色块再收 2px = 40
    expect(rects[0]!.width).toBe(40);
    expect(rects.map((r) => r.left)).toEqual([77, 119, 161]);
    // 高度只由节次跨度决定（1-2 → 2 行）
    expect(rects.every((r) => r.height === 102)).toBe(true);
  });

  it('单双周过滤下三列并排各自只剩命中周次的块', () => {
    const odd = buildBoard(timetable.courses, timetable.term, { today: '2026-09-14', weekFilter: 'odd' });
    const oddCell = odd.blocks.filter((b) => b.day === 1 && b.startSlot === 1 && b.endSlot === 2);
    expect(oddCell.map((b) => b.name).sort()).toEqual(['测试大学物理', '测试高等数学（工科类）']);
    expect(oddCell.every((b) => b.colCount === 2)).toBe(true);
    expect(odd.hiddenSessions).toBeGreaterThan(0);
  });
});
