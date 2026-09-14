import { describe, expect, it } from 'vitest';
import { decideRestingDisposition, type RestingInputs } from '../src/main/win32/resting-policy.js';

/**
 * 静息落点策略的契约测试。
 *
 * 这些用例锁的是**真机踩过的行为**，不是随手编的分支覆盖：
 * - 一律 `HWND_BOTTOM` 会破坏"页面之间的相对层级"（点过的窗口升上来之后挂件还在它下面）；
 * - 交到桌面壳手里时必须真的回桌面层，否则挂件会被桌面图标压在下面；
 * - 前台是自己时必须**什么都不做**，否则交互一松手挂件就跳位置。
 */

function inputs(partial: Partial<RestingInputs>): RestingInputs {
  return {
    hasForeground: true,
    foregroundIsDesktopShell: false,
    foregroundIsSelf: false,
    foregroundIsOwnApp: false,
    ...partial,
  };
}

describe('decideRestingDisposition', () => {
  it('没有前台窗口时回桌面层', () => {
    expect(decideRestingDisposition(inputs({ hasForeground: false }))).toBe('desktop-bottom');
  });

  it('前台是桌面壳（Progman / WorkerW / DefView）时回桌面层', () => {
    expect(decideRestingDisposition(inputs({ foregroundIsDesktopShell: true }))).toBe('desktop-bottom');
  });

  it('前台是自己时只维护内部顺序，不动全局层级', () => {
    expect(decideRestingDisposition(inputs({ foregroundIsSelf: true }))).toBe('preserve-peer-order');
  });

  it('前台是本应用其它窗口（设置窗口）时同样不动全局层级', () => {
    expect(decideRestingDisposition(inputs({ foregroundIsOwnApp: true }))).toBe('preserve-peer-order');
  });

  it('前台是第三方应用时插到它之后', () => {
    expect(decideRestingDisposition(inputs({}))).toBe('behind-foreground');
  });

  it('桌面壳优先于"自己"：Explorer 桌面拿到前台时不该走 preserve', () => {
    // 真机上点桌面 → 前台变成 Progman，此时期望"回桌面层"而不是"保持当前层级"，
    // 否则挂件会停在半空中（既不在桌面层也不在某个应用之后）。
    expect(
      decideRestingDisposition(inputs({ foregroundIsDesktopShell: true, foregroundIsSelf: true })),
    ).toBe('desktop-bottom');
  });

  it('没有前台时不区分其它标志位', () => {
    expect(
      decideRestingDisposition(
        inputs({ hasForeground: false, foregroundIsSelf: true, foregroundIsOwnApp: true }),
      ),
    ).toBe('desktop-bottom');
  });
});
