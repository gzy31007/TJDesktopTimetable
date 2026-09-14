import { describe, expect, it } from 'vitest';
import {
  clampIntoWorkArea,
  defaultBounds,
  isFullyInside,
  resolveSize,
  type Rect,
} from '../src/main/windows/geometry.js';

/**
 * 窗口几何规则的契约测试。
 *
 * 这些用例锁的是**真机上踩出来的行为**（不是随手编的分支覆盖）：
 * 2026-09-14 实测把挂件用标题栏拖到屏幕右边缘外 104px 后，右下角的缩放手柄跟着跑到
 * 屏幕外，用户再也点不到 —— 旧实现只判"与工作区有交集"，于是这个位置会被一路恢复到下次
 * 启动。所以"完整可见"是这几条规则的硬约束。
 */

const area: Rect = { x: 0, y: 0, width: 1707, height: 1067 };

describe('clampIntoWorkArea', () => {
  it('完整在屏内时原样返回（同一个对象，不做无意义的拷贝）', () => {
    const bounds: Rect = { x: 100, y: 100, width: 800, height: 600 };
    expect(clampIntoWorkArea(bounds, area)).toBe(bounds);
  });

  it('挂在右边缘外时夹回来，保证右边缘可见', () => {
    // 复现真机现场：750 宽、x=1102 → 右边缘 1852 > 1707
    const clamped = clampIntoWorkArea({ x: 1102, y: 412, width: 750, height: 600 }, area);
    expect(clamped.x).toBe(1707 - 750);
    expect(clamped.x + clamped.width).toBe(1707);
    expect(clamped.y).toBe(412);
  });

  it('挂在下边缘外时夹回来', () => {
    const clamped = clampIntoWorkArea({ x: 10, y: 900, width: 800, height: 600 }, area);
    expect(clamped.y).toBe(1067 - 600);
  });

  it('左侧越界（例如显示器在左、坐标为负）时贴左边', () => {
    const leftArea: Rect = { x: -1920, y: 0, width: 1920, height: 1080 };
    const clamped = clampIntoWorkArea({ x: -2400, y: 40, width: 800, height: 600 }, leftArea);
    expect(clamped.x).toBe(-1920);
    expect(clamped.y).toBe(40);
  });

  it('窗口比工作区还大时贴左上角，不产生更外层的坐标', () => {
    const clamped = clampIntoWorkArea({ x: 500, y: 500, width: 2000, height: 1400 }, area);
    expect(clamped.x).toBe(0);
    expect(clamped.y).toBe(0);
  });

  it('只改位置、不改尺寸', () => {
    const clamped = clampIntoWorkArea({ x: 5000, y: 5000, width: 700, height: 500 }, area);
    expect(clamped.width).toBe(700);
    expect(clamped.height).toBe(500);
  });
});

describe('isFullyInside', () => {
  it('完整在屏内为真', () => {
    expect(isFullyInside({ x: 10, y: 10, width: 100, height: 100 }, area)).toBe(true);
  });

  it('"相交但没被完全包含"为假 —— 旧逻辑正是在这里放过了屏幕外的位置', () => {
    // 右侧越界 104px，但仍然与工作区相交
    expect(isFullyInside({ x: 1102, y: 412, width: 750, height: 600 }, area)).toBe(false);
  });

  it('完全在屏外为假', () => {
    expect(isFullyInside({ x: 3000, y: 3000, width: 100, height: 100 }, area)).toBe(false);
  });
});

describe('defaultBounds', () => {
  it('首次启动落在右下角并留边距', () => {
    const bounds = defaultBounds(area, { width: 800, height: 600 }, 24);
    expect(bounds.x).toBe(1707 - 800 - 24);
    expect(bounds.y).toBe(1067 - 600 - 24);
  });

  it('工作区比窗口还小时不会算出负坐标', () => {
    const small: Rect = { x: 0, y: 0, width: 400, height: 300 };
    const bounds = defaultBounds(small, { width: 800, height: 600 }, 24);
    expect(bounds.x).toBe(0);
    expect(bounds.y).toBe(0);
  });
});

describe('resolveSize', () => {
  const fallback = { width: 560, height: 440 };
  const min = { width: 320, height: 220 };

  it('没存过尺寸时用默认值', () => {
    expect(resolveSize(undefined, fallback, min)).toEqual(fallback);
  });

  it('存下来的尺寸小于下限时退回默认值（挂件小到看不见时间轴就等于坏了）', () => {
    expect(resolveSize({ width: 100, height: 100 }, fallback, min)).toEqual(fallback);
  });

  it('合法尺寸取整后使用', () => {
    expect(resolveSize({ width: 800.6, height: 600.4 }, fallback, min)).toEqual({ width: 801, height: 600 });
  });
});
