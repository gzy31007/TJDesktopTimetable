/**
 * 静息落点策略（纯函数，零依赖，可单测）。
 *
 * 单独成文件的原因：`resting.ts` 里其它函数都要 koffi/user32，在 Node/WSL 下跑不起来；
 * 而"该落到哪一层"这个判断恰恰是最需要被测试锁定的部分（它决定挂件是否会被压到
 * 桌面之下、以及交互结束后会不会从"紧贴当前前台窗口"掉到所有窗口之后）。
 */

/** 静息落点。 */
export type RestingDisposition = 'desktop-bottom' | 'preserve-peer-order' | 'behind-foreground';

export interface RestingInputs {
  /** 存在有效的前台窗口。 */
  hasForeground: boolean;
  /** 前台窗口属于桌面壳（Progman / WorkerW / SHELLDLL_DefView）。 */
  foregroundIsDesktopShell: boolean;
  /** 前台窗口就是本挂件自己。 */
  foregroundIsSelf: boolean;
  /** 前台窗口是本应用的其它窗口（例如设置窗口）。 */
  foregroundIsOwnApp: boolean;
}

/**
 * 给定前台情况，决定静息落点。
 *
 * - 没有前台、或前台就是桌面 → 回桌面层（挂 owner + 置底）；
 * - 前台是自己或本应用其它窗口 → 只维护挂件内部顺序，**不动全局层级**
 *   （否则用户刚把挂件拖到某个位置，交互一结束就被砸到底部）；
 * - 前台是第三方应用 → 插到该应用之后，保持"紧贴当前页面的下沿"。
 */
export function decideRestingDisposition(input: RestingInputs): RestingDisposition {
  if (!input.hasForeground || input.foregroundIsDesktopShell) return 'desktop-bottom';
  if (input.foregroundIsSelf || input.foregroundIsOwnApp) return 'preserve-peer-order';
  return 'behind-foreground';
}
