import { describe, expect, it } from 'vitest';
import {
  blockRect,
  boardSize,
  buildBoard,
  DEFAULT_GEOMETRY,
  defaultShortName,
  fitGeometry,
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
const EVEN = weeksToMask([2, 4, 6, 8, 10, 12, 14, 16]);

const math: Course = {
  id: 'm1',
  name: '高等数学（工科类）',
  courseCode: 'M1',
  teachers: ['张三(12345)'],
  sessions: [{ id: 's1', day: 1, startSlot: 1, endSlot: 2, weeks: ALL, room: '南101' }],
};

const physics: Course = {
  id: 'p1',
  name: '大学物理',
  courseCode: 'P1',
  teachers: [],
  sessions: [
    { id: 's2', day: 1, startSlot: 1, endSlot: 2, weeks: ODD, room: '北201' },
    { id: 's3', day: 3, startSlot: 7, endSlot: 8, weeks: ALL, room: '北202' },
  ],
};

const english: Course = {
  id: 'e1',
  name: '大学英语',
  courseCode: 'E1',
  teachers: [],
  sessions: [{ id: 's4', day: 1, startSlot: 1, endSlot: 2, weeks: EVEN, room: '一教101' }],
};

describe('layout / buildBoard', () => {
  it('默认参数：7 列、节次范围覆盖到 11 节', () => {
    const board = buildBoard([math], term, { today: '2026-09-14' });
    expect(board.days.map((d) => d.day)).toEqual([1, 2, 3, 4, 5, 6, 7]);
    expect(board.rows.map((r) => r.index)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
    expect(board.blocks).toHaveLength(1);
    expect(board.currentWeek).toBe(1);
    expect(board.days[0]!.isToday).toBe(true);
  });

  it('隐藏周末', () => {
    const board = buildBoard([math], term, { showWeekend: false, today: '2026-09-14' });
    expect(board.days.map((d) => d.day)).toEqual([1, 2, 3, 4, 5]);
  });

  it('trimEmptySlots 收窄到有课的节次', () => {
    const board = buildBoard([physics], term, { trimEmptySlots: true, today: '2026-09-14' });
    expect(board.rows.map((r) => r.index)).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
  });

  it('同格多课并排（并标记 special）', () => {
    const board = buildBoard([math, physics, english], term, { today: '2026-09-14' });
    const cell = board.blocks.filter((b) => b.day === 1 && b.startSlot === 1);
    expect(cell).toHaveLength(3);
    expect(cell.map((b) => b.col).sort()).toEqual([0, 1, 2]);
    expect(cell.every((b) => b.colCount === 3 && b.stacked)).toBe(true);

    const mathBlock = cell.find((b) => b.courseId === 'm1')!;
    const physicsBlock = cell.find((b) => b.courseId === 'p1')!;
    expect(mathBlock.special).toBe(false); // 1-16 周 → 全周
    expect(mathBlock.weeksLabel).toBe('1-16');
    expect(physicsBlock.special).toBe(true); // 单周 → 条纹
    expect(physicsBlock.weeksLabel).toBe('1, 3, 5, 7, 9, 11, 13, 15');
  });

  it('周次过滤只保留命中周次的块，并统计隐藏数', () => {
    const odd = buildBoard([math, physics, english], term, { weekFilter: 'odd', today: '2026-09-14' });
    const oddIds = new Set(odd.blocks.map((b) => b.courseId));
    expect(oddIds.has('e1')).toBe(false); // 双周课被过滤
    expect(odd.hiddenSessions).toBe(1); // 仅 e1 的那一块被过滤
    expect(odd.blocks.some((b) => b.courseId === 'p1' && b.day === 3)).toBe(true);
  });

  it('课程名压缩去掉括号后缀', () => {
    expect(defaultShortName('高等数学（工科类）')).toBe('高等数学');
    expect(defaultShortName('体育(1)')).toBe('体育');
    expect(defaultShortName('大学物理')).toBe('大学物理');
  });

  it('几何计算：自适应列宽与色块矩形', () => {
    const board = buildBoard([math, physics, english], term, { today: '2026-09-14' });
    const geo = fitGeometry(1000, board.rows.length, board.days.length);
    expect(geo.cellWidth).toBe(Math.floor((1000 - DEFAULT_GEOMETRY.gutterWidth) / 7));
    const size = boardSize(board, geo);
    expect(size.width).toBe(geo.gutterWidth + geo.cellWidth * 7);

    const block = board.blocks.find((b) => b.courseId === 'm1')!;
    const rect = blockRect(board, block, geo);
    expect(rect.left).toBeGreaterThanOrEqual(geo.gutterWidth);
    expect(rect.width).toBeGreaterThan(0);
    expect(rect.height).toBe(2 * geo.rowHeight - 2);
  });
});
