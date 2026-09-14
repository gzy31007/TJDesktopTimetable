import {
  GWL_EXSTYLE,
  HWND_BOTTOM,
  HWND_NOTOPMOST,
  HWND_TOPMOST,
  SWP_FRAMECHANGED,
  SWP_NOACTIVATE,
  SWP_NOMOVE,
  SWP_NOOWNERZORDER,
  SWP_NOSIZE,
  SWP_NOZORDER,
  SWP_SHOWWINDOW,
  WS_EX_APPWINDOW,
  WS_EX_NOACTIVATE,
  WS_EX_TOOLWINDOW,
  type Win32,
} from './api.js';

/**
 * 静息（resting）状态：挂件常态下的物理落点与窗口样式。
 *
 * ## 为什么落点是"三选一"而不是一律 `HWND_BOTTOM`
 *
 * 一律压到底会破坏"页面之间的相对层级"：用户点开浏览器 A 之后，A 会升到普通层级带
 * 顶部；此时挂件应该在 **A 的下方紧邻位置**，而不是掉到所有窗口之后。把挂件砸到
 * `HWND_BOTTOM` 的结果是：用户切回任何窗口都得重新越过大片窗口才能看到挂件，
 * 且每次交互结束都被"砸下去"一次（表现为规律性闪烁/回落异常）。
 *
 * 所以静息落点按**当前前台窗口**决定：
 * - 前台是我们自己或本应用其它窗口 → 只整理挂件之间的顺序，不动全局层级；
 * - 前台是第三方应用 → 插到该应用之后（紧邻其下方）；
 * - 没有前台窗口、或前台就是桌面壳 → 回桌面层（重新挂 owner + 置底）。
 *
 * ## 为什么静息样式要戴 `WS_EX_NOACTIVATE`
 *
 * 桌面层的挂件在语义上"不是可交互应用窗口"：戴 `WS_EX_NOACTIVATE` 后，
 * 点击它不会把整个窗口提到第三方应用之上，也不会抢前台（这正是"贴桌面"该有的手感）。
 *
 * 代价：系统会跳过不可激活窗口的原生 move loop，**戴着它拖不动**。所以交互开始前必须
 * 临时摘掉、交互结束后再戴回（见 `layer.ts` 的 `beginPointerInteraction` /
 * `endPointerInteraction`）。这是一对必须成对出现的操作。
 */

export type { RestingDisposition, RestingInputs } from './resting-policy.js';
export { decideRestingDisposition } from './resting-policy.js';

/**
 * 瞬时浮起到"普通层级带顶部"。
 *
 * 技巧：先设 `HWND_TOPMOST` 再立刻 `HWND_NOTOPMOST`。窗口会停在**非 topmost 层级带的最上方**
 * —— 视觉上浮在所有普通窗口之上，但不留持久 TopMost 属性，别的窗口被激活时能正常盖过它。
 * 直接 `HWND_TOP` 不行：那会在同层级带里置顶，但仍低于所有 topmost 窗口，
 * 且会被后续的激活操作打乱。
 */
export function holdTemporaryTopMost(api: Win32, hwnd: number, showWindow = false): void {
  const flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | (showWindow ? SWP_SHOWWINDOW : 0);
  api.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
  api.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
}

/** 清除 TopMost 属性（不改变尺寸/位置/激活状态）。 */
export function clearTopMost(api: Win32, hwnd: number): void {
  api.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}

/**
 * 压到 z-order 最底。
 *
 * ⚠️ `SetWindowPos` 的 `hWndInsertAfter` 语义是"插到该窗口**之后**（z-order 更低）"，
 * 不是"上方"。曾经误把 owner 传进来当锚点，结果窗口被插到 owner 下面、被桌面盖住。
 *
 * 只在"明确回到桌面层"时使用（见 `RestingDisposition`）；第三方前台场景必须用
 * `placeBehindWindow`。owned 窗口有"恒在 owner 之上"的保证，所以置底后仍会停在桌面之上。
 */
export function pushToBottom(api: Win32, hwnd: number): void {
  clearTopMost(api, hwnd);
  api.SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}

/** 插到 `anchor` 之后（紧邻其下方），保留挂件自身的相对位置。 */
export function placeBehindWindow(api: Win32, hwnd: number, anchor: number): void {
  api.SetWindowPos(
    hwnd,
    anchor,
    0,
    0,
    0,
    0,
    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER,
  );
}

export function isNoActivate(api: Win32, hwnd: number): boolean {
  const style = Number(api.GetWindowLongPtrW(hwnd, GWL_EXSTYLE));
  return (style & WS_EX_NOACTIVATE) !== 0;
}

/**
 * 设置/清除 `WS_EX_NOACTIVATE`。
 *
 * 改完必须补一次 `SWP_FRAMECHANGED`，否则系统不会重新计算窗口的非客户区行为，
 * 样式"看起来写进去了但不生效"。
 */
export function setNoActivate(api: Win32, hwnd: number, enabled: boolean): boolean {
  const current = Number(api.GetWindowLongPtrW(hwnd, GWL_EXSTYLE));
  const next = enabled ? current | WS_EX_NOACTIVATE : current & ~WS_EX_NOACTIVATE;
  if (next === current) return isNoActivate(api, hwnd);
  api.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, next);
  api.SetWindowPos(
    hwnd,
    null,
    0,
    0,
    0,
    0,
    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
  );
  return isNoActivate(api, hwnd);
}

/**
 * 工具箱样式：`WS_EX_TOOLWINDOW` 让窗口不出现在任务栏与 Alt+Tab；
 * 顺手清掉 `WS_EX_APPWINDOW`（Electron 在某些时序下会把它加上，导致挂件跑到任务栏里）。
 */
export function applyToolWindowStyle(api: Win32, hwnd: number): void {
  const current = Number(api.GetWindowLongPtrW(hwnd, GWL_EXSTYLE));
  const next = (current | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
  if (next !== current) api.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, next);
}
