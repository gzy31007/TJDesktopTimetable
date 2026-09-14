import { describe, expect, it } from 'vitest';
import {
  countWeeks,
  evenWeekMask,
  formatWeeksLabel,
  fullWeekMask,
  highestWeek,
  isAllWeeks,
  maskToWeeks,
  oddWeekMask,
  resolveWeekFilter,
  weeksOverlap,
  weeksToMask,
} from '../src/index.js';

describe('weeks', () => {
  it('周次列表 ↔ 掩码往返', () => {
    expect(weeksToMask([1, 2, 3])).toBe(0b111);
    expect(weeksToMask([16])).toBe(1 << 15);
    expect(maskToWeeks(0b111, 16)).toEqual([1, 2, 3]);
    expect(maskToWeeks(1 << 15, 16)).toEqual([16]);
  });

  it('忽略非法周次', () => {
    expect(weeksToMask([0, -1, 1.5, 33, 3])).toBe(weeksToMask([3]));
    expect(weeksToMask([])).toBe(0);
  });

  it('第 32 周（掩码最高位）仍可表达', () => {
    expect(weeksToMask([32])).toBe(-2147483648 >>> 0);
    expect(maskToWeeks(weeksToMask([32]), 32)).toEqual([32]);
  });

  it('全周掩码 = 0xFFFF（与同济 weekState 一致）', () => {
    expect(fullWeekMask(16)).toBe(65535);
    expect(countWeeks(fullWeekMask(16))).toBe(16);
    expect(isAllWeeks(65535, 16)).toBe(true);
    expect(isAllWeeks(65535, 17)).toBe(false);
  });

  it('weeksLabel 与既有 Python 实现一致（黄金格式）', () => {
    expect(formatWeeksLabel(65535)).toBe('1-16');
    expect(formatWeeksLabel(weeksToMask([1, 3, 5, 7, 9, 11, 13, 15]))).toBe('1, 3, 5, 7, 9, 11, 13, 15');
    expect(formatWeeksLabel(weeksToMask([2, 4, 6, 8, 10, 12, 14, 16]))).toBe('2, 4, 6, 8, 10, 12, 14, 16');
    expect(formatWeeksLabel(weeksToMask([11, 12, 13, 14]))).toBe('11-14');
    expect(formatWeeksLabel(weeksToMask([9, 16]))).toBe('9, 16');
    expect(formatWeeksLabel(0)).toBe('-');
  });

  it('掩码最高位超出校历周数时仍完整展开', () => {
    const mask = weeksToMask([17, 18, 19]);
    expect(highestWeek(mask)).toBe(19);
    expect(maskToWeeks(mask, 16)).toEqual([17, 18, 19]);
    expect(formatWeeksLabel(mask, 16)).toBe('17-19');
  });

  it('单双周掩码按总周数生成，不硬编码 0x5555', () => {
    expect(oddWeekMask(16)).toBe(0x5555);
    expect(evenWeekMask(16)).toBe(0xaaaa);
    expect(maskToWeeks(oddWeekMask(15), 15)).toEqual([1, 3, 5, 7, 9, 11, 13, 15]);
    expect(maskToWeeks(evenWeekMask(15), 15)).toEqual([2, 4, 6, 8, 10, 12, 14]);
    // 20 周学期：0x5555 会截断，泛化实现不会
    expect(maskToWeeks(oddWeekMask(20), 20)).toEqual([1, 3, 5, 7, 9, 11, 13, 15, 17, 19]);
  });

  it('过滤器解析与交集判断', () => {
    expect(resolveWeekFilter('all', 16)).toBeNull();
    expect(resolveWeekFilter('odd', 16)).toBe(0x5555);
    expect(resolveWeekFilter('even', 16)).toBe(0xaaaa);
    expect(weeksOverlap(weeksToMask([1, 3]), weeksToMask([2, 3]))).toBe(true);
    expect(weeksOverlap(weeksToMask([1, 3]), weeksToMask([2, 4]))).toBe(false);
  });
});
