import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  buildBoard,
  coursesConflict,
  importTimetable,
  materializeTimetable,
  sessionsOnDate,
  toggleCourse,
  type Course,
} from '../src/index.js';

/**
 * 端到端：真实抓包数据 → 导入 → 按课程挑不冲突的教学班 → 落成课表 → 渲染布局 → 时间推算。
 *
 * 覆盖"导入管线 + 冲突选择 + 存储模型 + 网格布局 + 教学周"整条链路，
 * 是单测里最接近用户实际操作路径的一条。
 */

const fixture = (name: string): string => readFileSync(new URL(`../fixtures/${name}`, import.meta.url), 'utf8');

const files = [
  { name: 'timetable.json', text: fixture('tongji-2026-1-major.raw.json') },
  { name: 'calendar.json', text: fixture('tongji-school-calendar.json') },
];

/** 模拟用户操作：每门课尽量挑一个与已选不冲突的教学班。 */
function pickNonConflicting(pool: Course[]): string[] {
  const byName = new Map<string, Course[]>();
  for (const course of pool) {
    const list = byName.get(course.name);
    if (list) list.push(course);
    else byName.set(course.name, [course]);
  }

  let selected: string[] = [];
  for (const list of byName.values()) {
    for (const course of list) {
      const result = toggleCourse(course, selected, pool);
      if (result.action === 'added') {
        selected = result.selected;
        break;
      }
    }
  }
  return selected;
}

describe('端到端：导入 → 勾选 → 课表', () => {
  const result = importTimetable({ files, importedAt: '2026-09-01T00:00:00.000Z' });
  const pool = result.candidates ?? [];

  it('导入阶段给出候选池与诊断', () => {
    expect(pool).toHaveLength(128);
    expect(result.diagnostics.some((d) => d.code === 'tongji.summary' && d.level === 'info')).toBe(true);
    expect(result.meta?.sources).toEqual(['timetable.json:课表 147 条', 'calendar.json:校历 2 个学期']);
  });

  it('勾选出的教学班两两不冲突', () => {
    const selected = pickNonConflicting(pool);
    expect(selected.length).toBeGreaterThanOrEqual(5);

    const chosen = pool.filter((course) => selected.includes(course.id));
    for (let i = 0; i < chosen.length; i += 1) {
      for (let j = i + 1; j < chosen.length; j += 1) {
        expect(coursesConflict(chosen[i]!, chosen[j]!), `${chosen[i]!.name} vs ${chosen[j]!.name}`).toBe(false);
      }
    }
  });

  it('落盘模型 → 网格布局 → 第一天有课', () => {
    const selected = pickNonConflicting(pool);
    const timetable = materializeTimetable(result, selected);
    expect(timetable.courses).toHaveLength(selected.length);
    expect(timetable.term.startDate).toBe('2026-09-14');
    expect(timetable.source.adapterId).toBe('tongji-major');

    const sessionTotal = timetable.courses.reduce((n, course) => n + course.sessions.length, 0);
    const board = buildBoard(timetable.courses, timetable.term, {
      today: '2026-09-16',
      trimEmptySlots: true,
      now: new Date('2026-09-16T02:00:00Z'), // 北京时间的周三 10:00
    });

    expect(board.blocks).toHaveLength(sessionTotal);
    expect(board.currentWeek).toBe(1);
    expect(board.days).toHaveLength(7);
    // 每个色块都能算出绘制矩形
    expect(board.blocks.every((block) => block.colCount >= 1 && block.weeksLabel.length > 0)).toBe(true);

    // 第 1 周周一确实有课（真实数据里周一 1-3 节有思政课）
    const monday = sessionsOnDate(timetable.courses, timetable.term, '2026-09-14');
    expect(monday.length).toBeGreaterThan(0);

    // 假期内没有任何课
    expect(sessionsOnDate(timetable.courses, timetable.term, '2026-08-31')).toHaveLength(0);
  });

  it('周次过滤后的块数变少（单双周课被滤掉）', () => {
    const selected = pickNonConflicting(pool);
    const timetable = materializeTimetable(result, selected);
    const all = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16' });
    const odd = buildBoard(timetable.courses, timetable.term, { today: '2026-09-16', weekFilter: 'odd' });
    expect(odd.blocks.length).toBeLessThanOrEqual(all.blocks.length);
    expect(odd.hiddenSessions).toBeGreaterThan(0);
  });
});
