import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  defaultRegistry,
  formatWeeksLabel,
  importTimetable,
  ImportError,
  materializeTimetable,
  TONGJI_DEFAULT_SLOTS,
  type Course,
} from '../src/index.js';

const fixture = (name: string): string => readFileSync(new URL(`../fixtures/${name}`, import.meta.url), 'utf8');

interface ExpectedRow {
  courseCode: string;
  teachingClassCode: string;
  courseName: string;
  day: number;
  timeStart: number;
  timeEnd: number;
  weeksLabel: string;
  room: string;
  campus: string;
  faculty: string;
  teachers: string;
  teachingClassId: number;
}

interface Expected {
  term: { id: number; year: number; termNo: number };
  count: number;
  rows: ExpectedRow[];
}

function sessionKeys(courses: readonly Course[], term: { totalWeeks: number }): Map<string, Set<string>> {
  const map = new Map<string, Set<string>>();
  for (const course of courses) {
    const set = map.get(course.teachingClassCode ?? course.id) ?? new Set<string>();
    for (const session of course.sessions) {
      set.add(
        `${session.day}|${session.startSlot}|${session.endSlot}|${formatWeeksLabel(session.weeks, term.totalWeeks)}|${session.room ?? ''}`,
      );
    }
    map.set(course.teachingClassCode ?? course.id, set);
  }
  return map;
}

describe('tongji-major adapter（黄金测试：与既有 Python 解析结果逐条一致）', () => {
  const expected = JSON.parse(fixture('tongji-2026-1-major.expected.json')) as Expected;

  it('自动探测命中同济适配器', () => {
    const input = {
      files: [
        { name: 'timetable.json', text: fixture('tongji-2026-1-major.raw.json') },
        { name: 'calendar.json', text: fixture('tongji-school-calendar.json') },
      ],
    };
    const best = defaultRegistry.best(input);
    expect(best?.adapter.id).toBe('tongji-major');
    expect(best?.score).toBeGreaterThan(0.9);
  });

  it('解析出 147 条排课记录 / 128 个教学班，学期信息正确', () => {
    const result = importTimetable({
      files: [
        { name: 'timetable.json', text: fixture('tongji-2026-1-major.raw.json') },
        { name: 'calendar.json', text: fixture('tongji-school-calendar.json') },
      ],
      importedAt: '2026-09-14T00:00:00.000Z',
    });

    expect(result.adapterId).toBe('tongji-major');
    expect(result.term.id).toBe(String(expected.term.id));
    expect(result.term.year).toBe(expected.term.year);
    expect(result.term.termNo).toBe(expected.term.termNo);
    expect(result.term.totalWeeks).toBe(16);
    expect(result.term.startDate).toBe('2026-09-14');
    expect(result.term.name).toBe('2026-2027学年第1学期');
    expect(result.term.slots).toHaveLength(11);
    expect(result.term.slots[0]).toEqual({ index: 1, begin: '08:00', end: '08:45' });
    expect(result.term.slots[10]).toEqual({ index: 11, begin: '20:10', end: '20:55' });

    const candidates = result.candidates ?? [];
    expect(candidates).toHaveLength(128);
    expect(candidates.reduce((n, c) => n + c.sessions.length, 0)).toBe(expected.count);

    // 候选池 ≠ 已选课表：必须走勾选流程
    expect(result.courses).toHaveLength(0);
    expect(result.preselect).toHaveLength(0);
  });

  it('每个教学班的时段与 weeksLabel 与 expected.json 完全一致', () => {
    const result = importTimetable({
      files: [
        { name: 'timetable.json', text: fixture('tongji-2026-1-major.raw.json') },
        { name: 'calendar.json', text: fixture('tongji-school-calendar.json') },
      ],
    });
    const actual = sessionKeys(result.candidates ?? [], result.term);

    const want = new Map<string, Set<string>>();
    for (const row of expected.rows) {
      const set = want.get(row.teachingClassCode) ?? new Set<string>();
      set.add(`${row.day}|${row.timeStart}|${row.timeEnd}|${row.weeksLabel}|${row.room}`);
      want.set(row.teachingClassCode, set);
    }

    expect([...actual.keys()].sort()).toEqual([...want.keys()].sort());
    for (const [code, wantSet] of want) {
      expect([...(actual.get(code) ?? [])].sort()).toEqual([...wantSet].sort());
    }
  });

  it('课程名 / 教师 / 课程代码与 expected.json 一致', () => {
    const result = importTimetable({
      files: [
        { name: 'timetable.json', text: fixture('tongji-2026-1-major.raw.json') },
        { name: 'calendar.json', text: fixture('tongji-school-calendar.json') },
      ],
    });
    const byClass = new Map((result.candidates ?? []).map((c) => [c.teachingClassCode, c]));

    for (const row of expected.rows) {
      const course = byClass.get(row.teachingClassCode);
      expect(course, `缺少教学班 ${row.teachingClassCode}`).toBeDefined();
      expect(course!.name).toBe(row.courseName);
      expect(course!.courseCode).toBe(row.courseCode);
      expect(course!.faculty).toBe(row.faculty);
      expect(course!.teachers.join('、')).toBe(row.teachers);
      expect(course!.campus).toBe(row.campus);
      expect(course!.id).toBe(String(row.teachingClassId));
    }
  });

  it('没有校历时退化为 16 周 + 默认节次，并给出告警', () => {
    const result = importTimetable({ text: fixture('tongji-2026-1-major.raw.json') });
    expect(result.term.totalWeeks).toBe(16);
    expect(result.term.startDate).toBeUndefined();
    expect(result.term.slots).toEqual(TONGJI_DEFAULT_SLOTS);
    expect(result.diagnostics.some((d) => d.code === 'tongji.term.missing' && d.level === 'warn')).toBe(true);
  });

  it('materializeTimetable 只保留被勾选的教学班', () => {
    const result = importTimetable({
      files: [
        { name: 'timetable.json', text: fixture('tongji-2026-1-major.raw.json') },
        { name: 'calendar.json', text: fixture('tongji-school-calendar.json') },
      ],
    });
    const picks = (result.candidates ?? []).slice(0, 3).map((c) => c.id);
    const timetable = materializeTimetable(result, picks);
    expect(timetable.courses).toHaveLength(3);
    expect(timetable.courses.map((c) => c.id).sort()).toEqual([...picks].sort());
    expect(timetable.source.adapterId).toBe('tongji-major');
    expect(timetable.term.startDate).toBe('2026-09-14');
  });

  it('完全无法识别的输入抛 ImportError', () => {
    expect(() => importTimetable({ text: 'hello world' })).toThrow(ImportError);
    expect(() => importTimetable({ text: '{"foo":1}' })).toThrow(/无法识别/);
  });
});
