import { describe, expect, it } from 'vitest';
import {
  addDays,
  dayNumberToIso,
  dayNumberToWeekday,
  describeCountdown,
  isoToDayNumber,
  isoToWeekday,
  localTodayIso,
  mondayOf,
  msToIsoDate,
  nextSession,
  sessionsOnDate,
  summarizeNow,
  termWeekAt,
  weeksToMask,
  type Course,
  type Term,
} from '../src/index.js';
import { TONGJI_DEFAULT_SLOTS } from '../src/model.js';

const term: Term = {
  id: '122',
  name: '2026-2027学年第1学期',
  year: 2026,
  termNo: 1,
  startDate: '2026-09-14',
  totalWeeks: 16,
  slots: TONGJI_DEFAULT_SLOTS.map((s) => ({ ...s })),
};

const ALL = weeksToMask(Array.from({ length: 16 }, (_, i) => i + 1));
const ODD = weeksToMask([1, 3, 5, 7, 9, 11, 13, 15]);

const course: Course = {
  id: 'c1',
  name: '高等数学',
  courseCode: 'M1',
  teachers: ['张三(12345)'],
  sessions: [{ id: 's1', day: 1, startSlot: 1, endSlot: 2, weeks: ALL, room: '南101' }],
};

const oddCourse: Course = {
  id: 'c2',
  name: '大学物理',
  courseCode: 'P1',
  teachers: [],
  sessions: [{ id: 's2', day: 3, startSlot: 5, endSlot: 6, weeks: ODD }],
};

describe('time', () => {
  it('纯日期算术不受时区影响', () => {
    expect(dayNumberToIso(isoToDayNumber('2026-09-14'))).toBe('2026-09-14');
    expect(isoToDayNumber('2026-09-21') - isoToDayNumber('2026-09-14')).toBe(7);
    expect(Number.isNaN(isoToDayNumber('not-a-date'))).toBe(true);
    expect(addDays('2026-09-14', 7)).toBe('2026-09-21');
    expect(mondayOf('2026-09-16')).toBe('2026-09-14');
    expect(mondayOf('2026-09-20')).toBe('2026-09-14'); // 周日仍属本周
    expect(isoToWeekday('2026-09-14')).toBe(1);
    expect(isoToWeekday('2026-09-20')).toBe(7);
    expect(dayNumberToWeekday(isoToDayNumber('2026-09-19'))).toBe(6);
  });

  it('校历毫秒时间戳按北京时间换算（2026-09-14 00:00 CST）', () => {
    expect(msToIsoDate(1789315200000)).toBe('2026-09-14');
  });

  it('localTodayIso 使用东八区', () => {
    // 2026-09-13 16:30 UTC = 2026-09-14 00:30 CST
    expect(localTodayIso(new Date('2026-09-13T16:30:00Z'))).toBe('2026-09-14');
    expect(localTodayIso(new Date('2026-09-13T16:30:00Z'), 0)).toBe('2026-09-13');
  });

  it('教学周推算与边界', () => {
    expect(termWeekAt(term, '2026-09-07')).toBeNull(); // 开学前
    expect(termWeekAt(term, '2026-09-14')).toBe(1);
    expect(termWeekAt(term, '2026-09-20')).toBe(1);
    expect(termWeekAt(term, '2026-09-21')).toBe(2);
    expect(termWeekAt(term, '2027-01-03')).toBe(16);
    expect(termWeekAt(term, '2027-01-11')).toBeNull(); // 学期结束
    expect(termWeekAt({ ...term, startDate: undefined }, '2026-09-14')).toBeNull();
  });

  it('按日期取当天课程（含周次过滤）', () => {
    const monday = sessionsOnDate([course, oddCourse], term, '2026-09-14');
    expect(monday.map((o) => o.course.id)).toEqual(['c1']);

    // 第 3 周周三（单周）有课；第 4 周周三（双周）没有
    expect(sessionsOnDate([oddCourse], term, '2026-09-30').length).toBe(1);
    expect(sessionsOnDate([oddCourse], term, '2026-10-07').length).toBe(0);
  });

  it('下一节课：含"正在上课"与跨天', () => {
    const inClass = nextSession([course], term, new Date('2026-09-14T00:10:00Z')); // 08:10 CST
    expect(inClass?.inProgress).toBe(true);
    expect(describeCountdown(inClass!)).toBe('正在上课');

    const before = nextSession([course], term, new Date('2026-09-14T00:00:00Z')); // 08:00 CST 整
    expect(before?.inProgress).toBe(true);

    const earlyMorning = nextSession([course], term, new Date('2026-09-13T22:00:00Z')); // 06:00 CST
    expect(earlyMorning?.minutesUntil).toBe(120);
    expect(describeCountdown(earlyMorning!)).toBe('2 小时后');

    const nextWeek = nextSession([course], term, new Date('2026-09-14T04:00:00Z')); // 周一 12:00 CST
    expect(nextWeek?.date).toBe('2026-09-21');
    expect(nextWeek?.inProgress).toBe(false);
  });

  it('头部摘要', () => {
    const summary = summarizeNow(term, new Date('2026-09-30T02:00:00Z'));
    expect(summary.week).toBe(3);
    expect(summary.date).toBe('2026-09-30');
    expect(summary.label).toContain('第 3 周');
    expect(summary.label).toContain('周三');
  });
});
