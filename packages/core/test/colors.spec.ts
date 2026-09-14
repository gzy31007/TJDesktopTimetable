import { describe, expect, it } from 'vitest';
import { colorForCourse, COURSE_PALETTE, hashString, readableTextColor, withAlpha } from '../src/index.js';

describe('colors', () => {
  it('同名课程颜色稳定，跨调用不变', () => {
    const a = colorForCourse('高等数学');
    const b = colorForCourse('高等数学');
    expect(a).toBe(b);
    expect(COURSE_PALETTE).toContain(a);
  });

  it('不同课程名分布到不同颜色（抽样 20 门课）', () => {
    const names = Array.from({ length: 20 }, (_, i) => `课程-${i}`);
    const used = new Set(names.map((n) => colorForCourse(n)));
    expect(used.size).toBeGreaterThan(5);
  });

  it('哈希稳定且为无符号 32 位', () => {
    expect(hashString('abc')).toBe(hashString('abc'));
    expect(hashString('abc')).not.toBe(hashString('abd'));
    expect(hashString('高等数学')).toBeGreaterThanOrEqual(0);
  });

  it('withAlpha 生成 8 位十六进制', () => {
    expect(withAlpha('#2563eb', 0.8)).toBe('#2563ebcc');
    expect(withAlpha('#2563eb', 0)).toBe('#2563eb00');
    expect(withAlpha('#2563eb', 1)).toBe('#2563ebff');
  });

  it('文字颜色对比度', () => {
    expect(readableTextColor('#ffffff')).toBe('#111827');
    expect(readableTextColor('#000000')).toBe('#ffffff');
    expect(readableTextColor('#15803d')).toBe('#ffffff');
  });
});
