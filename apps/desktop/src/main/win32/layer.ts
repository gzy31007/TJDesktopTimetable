import type { BrowserWindow } from 'electron';
import type { WidgetMode } from '../../shared/ipc.js';
import { log } from '../logger.js';
import {
  HTCAPTION,
  SW_HIDE,
  WM_DISPLAYCHANGE,
  WM_SETTINGCHANGE,
  SW_RESTORE,
  SW_SHOWNOACTIVATE,
  WM_NCLBUTTONDOWN,
  hwndOrNull,
  isWin32Available,
  isLeftButtonDown,
  isWindow,
  loadWin32,
  toHwnd,
  windowClassName,
  windowTitle,
  type Win32,
} from './api.js';
import {
  attachToDesktopHost,
  foregroundRoot,
  forgetOwnerRecord,
  getCurrentOwner,
  invalidateDesktopHostCache,
  isDesktopShellWindow,
  resolveDesktopHost,
  restoreOriginalOwner,
} from './desktop-host.js';
import { decideRestingDisposition } from './resting-policy.js';
import {
  applyToolWindowStyle,
  clearTopMost,
  holdTemporaryTopMost,
  isNoActivate,
  placeBehindWindow,
  pushToBottom,
  setNoActivate,
} from './resting.js';

/**
 * 挂件窗口的层级编排（Win32，koffi 纯 FFI）。
 *
 * ## 设计要点（2026-09-14 重写）
 *
 * 旧实现有五个互相干扰的机制同时在动 z-order：每秒无条件 `SetWindowPos(HWND_BOTTOM)`、
 * `hide`/`minimize` 事件里的 `SW_RESTORE` + owner 重挂、`nudgeRepaint` 的 1px resize、
 * 定期"修 Shell last active popup"（内部会抢一次前台）、以及"owner 丢了就重挂"。
 * 结果是窗口永远在动，"Win+D 后看不见"这类问题无法收敛。
 *
 * 新实现的职责划分：
 * - **静息态由 `resting.ts` 的落点策略决定**（三选一），不再一律置底；
 * - **不再有每秒重压**。只保留一个 5 秒的 **owner 巡检**：owner 关系没丢就完全不动窗口，
 *   丢了才重挂 —— 代价是一次 `GetWindowLongPtrW` 读，不产生任何 z-order 变化；
 * - **可靠性来自事件而不是轮询**：Explorer 重启（`TaskbarCreated`）、
 *   显示器/工作区变化（`WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE`）由 `widget.ts` 订阅后
 *   调用 `refreshDesktopLayer()`，作废宿主缓存并重新静息；
 * - **交互期临时摘掉 `WS_EX_NOACTIVATE`**（否则系统跳过原生 move loop，窗口拖不动），
 *   交互结束后戴回并重新静息。这一对必须成对出现，见 `suspendRestingStyle` /
 *   `resumeRestingStyle`。
 *
 * 非 Windows 平台或 koffi 不可用时全部退化为空实现（`pnpm build` / 单测不受影响）。
 */

export interface LayerOptions {
  mode: WidgetMode;
  /**
   * 静息态 owner 巡检间隔（毫秒，0 = 关闭）。
   *
   * 注意语义已经变了：这是"owner 关系是否还在"的巡检，**不是** z-order 重压。
   * owner 正常时不做任何窗口操作。
   */
  keepAtBottomIntervalMs?: number;
  /** 是否把窗口挂到桌面图标层（Win+D 后仍可见）；默认 true。 */
  desktopLayer?: boolean;
  /** 模式降级回调（例如 wallpaper 不可用时）。 */
  onFallback?: (reason: string) => void;
}

export interface LayerHandle {
  /** 实际生效的模式（可能与请求的不同）。 */
  readonly mode: WidgetMode;
  /** 暂停 owner 巡检（窗口被主动隐藏时调用）。 */
  pause(): void;
  /** 恢复巡检并立即重新静息一次。 */
  resume(): void;
  /** 立即重新静息一次（按当前前台窗口决定落点）。 */
  refresh(): void;
  /** 解除控制（退出前调用）。 */
  detach(): void;
}

/** 当前是否有挂件在桌面层静息（决定 `WS_EX_NOACTIVATE` 的归属）。 */
let restingDesktopLayer = false;
let currentHwnd: number | null = null;

/**
 * 把窗口挂到桌面层级。
 */
export function attachToDesktop(window: BrowserWindow, options: LayerOptions): LayerHandle {
  window.setSkipTaskbar(true);
  const api = loadWin32();
  if (!api) {
    return { mode: options.mode, pause() {}, resume() {}, refresh() {}, detach() {} };
  }

  const hwnd = toHwnd(window);
  currentHwnd = hwnd;
  applyToolWindowStyle(api, hwnd);

  let mode: WidgetMode = options.mode;
  restingDesktopLayer = false;

  if (mode === 'wallpaper') {
    const workerW = findWorkerW(api);
    if (workerW === null) {
      mode = 'desktop';
      options.onFallback?.('未找到 WorkerW（可能被其它壁纸软件占用），已回退为普通桌面层模式');
    } else {
      api.SetParent(hwnd, workerW);
      api.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
      restoreOriginalOwner(api, hwnd);
      setNoActivate(api, hwnd, false);
    }
  }

  if (mode === 'desktop') {
    restingDesktopLayer = options.desktopLayer !== false;
    if (!restingDesktopLayer) {
      // 用户显式关闭"固定到桌面层"：不挂 owner，纯置底窗口。
      // 代价是 Win+D 会把它最小化，靠 hide/minimize 事件恢复（见 `ensureWidgetVisible`）。
      restoreOriginalOwner(api, hwnd);
      setNoActivate(api, hwnd, false);
      applyToolWindowStyle(api, hwnd);
      pushToBottom(api, hwnd);
    } else {
      applyRestingLayer(api, hwnd, 'initial');
    }
  }

  const interval = options.keepAtBottomIntervalMs ?? 5000;
  let timer: NodeJS.Timeout | null = null;
  let ownerRelogged = false;

  /**
   * owner 巡检：**只在 owner 关系丢了的时候动窗口**。
   *
   * 之所以需要它：Explorer 重启、显示拓扑变化、以及其它桌面软件抢宿主之后，
   * owner 可能被系统清掉，此时窗口就失去了"Win+D 后仍可见"的保护。
   * 事件通道（TaskbarCreated / WM_DISPLAYCHANGE）负责主要修复，这里是低频兜底。
   */
  const patrol = (): void => {
    if (!restingDesktopLayer || !isWindow(api, hwnd)) return;
    const host = resolveDesktopHost();
    if (host === null) return;
    if (getCurrentOwner(api, hwnd) === host.hwnd) {
      ownerRelogged = false;
      return;
    }
    if (!ownerRelogged) {
      ownerRelogged = true;
      log('[win32] 桌面层 owner 丢失，重新挂载（同类不再重复记录）');
    }
    applyRestingLayer(api, hwnd, 'owner-patrol');
  };

  const start = (): void => {
    if (mode !== 'desktop' || interval <= 0 || timer) return;
    timer = setInterval(patrol, interval);
  };
  const stop = (): void => {
    if (timer) {
      clearInterval(timer);
      timer = null;
    }
  };

  start();

  return {
    get mode() {
      return mode;
    },
    pause: stop,
    resume() {
      restoreRestingLayer(api, hwnd, 'resume');
      start();
    },
    refresh() {
      restoreRestingLayer(api, hwnd, 'refresh');
    },
    detach() {
      stop();
      restoreOriginalOwner(api, hwnd);
      forgetOwnerRecord(hwnd);
      currentHwnd = null;
      // 注意：这里**不隐藏窗口**。detach 在"切换层级模式"时也会被调用
      // （托盘 / 设置面板改了 mode 就重新 attach），旧实现在这里 `SW_HIDE`，
      // 结果是切一次模式挂件就消失且没人再把它显示回来。
      // 真正需要隐藏时走 `setWidgetVisible(false)`。
    },
  };
}

/**
 * 重新静息：按当前前台窗口决定落点。
 *
 * 三种落点见 `decideRestingDisposition`。这是**唯一**会改变挂件全局 z-order 的入口
 * （除了交互期临时浮起）。
 */
export function restoreRestingLayer(
  api: Win32,
  hwnd: number,
  reason: string,
): void {
  if (!isWindow(api, hwnd)) return;

  const foreground = foregroundRoot(api);
  const ownAppPid = currentProcessId(api, hwnd);
  const disposition = decideRestingDisposition({
    hasForeground: foreground !== null,
    foregroundIsDesktopShell: isDesktopShellWindow(api, foreground),
    foregroundIsSelf: foreground === hwnd,
    foregroundIsOwnApp: foreground !== null && currentProcessId(api, foreground) === ownAppPid,
  });

  switch (disposition) {
    case 'desktop-bottom':
      // 注意：只有用户开着"固定到桌面层"时才挂 owner。关掉时必须保持无 owner，
      // 否则会在这里把它偷偷变回桌面层窗口（历史 bug，A/B 对照实验因此作废过一次）。
      if (restingDesktopLayer) applyRestingLayer(api, hwnd, reason);
      else {
        clearTopMost(api, hwnd);
        pushToBottom(api, hwnd);
      }
      break;
    case 'behind-foreground': {
      // 保住 owner（Win+D 保护），但不要压到底：插到当前前台之后。
      if (restingDesktopLayer) attachOwnerOnly(api, hwnd);
      else clearTopMost(api, hwnd);
      if (foreground !== null) placeBehindWindow(api, hwnd, foreground);
      break;
    }
    case 'preserve-peer-order':
      // 前台是我们自己：只确保 owner 与样式，不动全局层级。
      if (restingDesktopLayer) attachOwnerOnly(api, hwnd);
      else clearTopMost(api, hwnd);
      break;
  }

  if (restingDesktopLayer) setNoActivate(api, hwnd, true);
  log('[win32] 静息落点', {
    reason,
    disposition,
    owner: getCurrentOwner(api, hwnd),
  });
}

/**
 * 把窗口放回桌面层：挂 owner + 置底 + 戴静息样式。
 *
 * 没有可用宿主时（Explorer 还没起来）**不挂 owner**，退化为纯置底并摘掉静息样式
 * （保住可拖动性）；下一次层级操作（owner 巡检 / 事件通道）会再试挂载。
 */
function applyRestingLayer(api: Win32, hwnd: number, reason: string): void {
  if (!isWindow(api, hwnd)) return;

  const host = resolveDesktopHost();
  if (host === null || !attachToDesktopHost(api, hwnd, host.hwnd)) {
    log('[win32] 桌面宿主不可用，回退为无 owner 置底', { reason });
    clearTopMost(api, hwnd);
    pushToBottom(api, hwnd);
    setNoActivate(api, hwnd, false);
    return;
  }

  pushToBottom(api, hwnd);
  setNoActivate(api, hwnd, true);
}

/** 只确保 owner 关系（不改变全局 z-order）。 */
function attachOwnerOnly(api: Win32, hwnd: number): void {
  const host = resolveDesktopHost();
  if (host === null) return;
  if (getCurrentOwner(api, hwnd) === host.hwnd) return;
  attachToDesktopHost(api, hwnd, host.hwnd);
}

function currentProcessId(api: Win32, hwnd: number | null): number {
  if (hwnd === null) return -1;
  try {
    const buffer = Buffer.alloc(4);
    api.GetWindowThreadProcessId(hwnd, buffer);
    return buffer.readUInt32LE(0);
  } catch {
    return -1;
  }
}

/**
 * 交互期：摘掉静息样式并临时浮起。
 *
 * 两件事必须一起做：`WS_EX_NOACTIVATE` 会让系统跳过原生 move loop（拖不动），
 * 而贴桌面层的窗口本来就在所有普通窗口之下，用户拖它时看不到自己在拖什么。
 */
export function suspendRestingStyle(window: BrowserWindow): void {
  const api = loadWin32();
  if (!api || window.isDestroyed()) return;
  const hwnd = toHwnd(window);
  if (restingDesktopLayer && isNoActivate(api, hwnd)) {
    setNoActivate(api, hwnd, false);
    log('[win32] 交互开始：已摘除静息样式');
  }
  holdTemporaryTopMost(api, hwnd);
}

/** 交互期结束：重新静息（戴回样式 + 按前台决定落点）。 */
export function resumeRestingStyle(window: BrowserWindow, reason = 'pointer-end'): void {
  const api = loadWin32();
  if (!api || window.isDestroyed()) return;
  const hwnd = toHwnd(window);
  if (!restingDesktopLayer) {
    // 用户关掉了桌面层：不挂 owner，只把临时浮起收回去（置底）。
    clearTopMost(api, hwnd);
    pushToBottom(api, hwnd);
    return;
  }
  restoreRestingLayer(api, hwnd, reason);
}

/**
 * 立即把挂件恢复到"可见 + 落在正确层级"。
 *
 * 调用时机：窗口被系统隐藏/最小化之后（Win+D、Explorer 重启），以及交互结束。
 * 与旧实现的区别：这里不再无条件 `SW_RESTORE` + owner 重挂 + 压底，
 * 而是先修可见性，再交给落点策略决定位置。
 */
export function ensureWidgetVisible(window: BrowserWindow): void {
  const api = loadWin32();
  if (!api || window.isDestroyed()) return;
  const hwnd = toHwnd(window);
  if (!api.IsWindowVisible(hwnd)) api.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
  if (api.IsIconic(hwnd)) api.ShowWindow(hwnd, SW_RESTORE);
  restoreRestingLayer(api, hwnd, 'ensure-visible');
}

/**
 * 显示器 / 工作区 / Explorer 变化之后重建桌面层级。
 *
 * 宿主句柄（`SHELLDLL_DefView`）在这些场景下可能已经被 Explorer 换掉，
 * 所以必须先作废缓存再重新静息。
 */
export function refreshDesktopLayer(window: BrowserWindow, reason: string): void {
  const api = loadWin32();
  if (!api || window.isDestroyed()) return;
  invalidateDesktopHostCache();
  log('[win32] 重建桌面层级', { reason });
  restoreRestingLayer(api, toHwnd(window), reason);
}

/** 诊断：打印挂件在顶层 z-order 里的站位、owner 与压着它的窗口。 */
export function logZOrder(window: BrowserWindow): void {
  try {
    const api = loadWin32();
    if (!api || window.isDestroyed()) return;
    const hwnd = toHwnd(window);
    const top = topLevelWindows(api, 512);
    const index = top.findIndex((item) => item.hwnd === hwnd);
    const above = index > 0 ? top.slice(0, index).slice(-2) : [];
    const [x = 0, y = 0] = window.getPosition();
    const host = top.findIndex((item) => item.title === '' && item.cls === 'Progman');
    const owner = getCurrentOwner(api, hwnd);
    const ownerIndex = owner === null ? -1 : top.findIndex((item) => item.hwnd === owner);
    log('[win32] z-order', {
      position: index < 0 ? '未在顶层窗口中找到' : `第 ${index + 1}/${top.length}`,
      xy: `${x},${y}`,
      owner,
      ownerIndex: ownerIndex < 0 ? '未找到' : ownerIndex + 1,
      // 挂件必须排在 owner 之前（z-order 更高）：ownerIndex < index 表示被宿主/桌面盖住 = 异常
      coveredByOwner: ownerIndex >= 0 && index >= 0 && ownerIndex < index,
      progmanIndex: host < 0 ? '未找到' : host + 1,
      above: above.map((item) => `${item.cls}${item.title ? `(${item.title})` : ''}`),
      below: index >= 0 ? top.slice(index + 1, index + 3).map((item) => item.cls) : [],
    });
  } catch (error) {
    log('[win32] z-order 诊断异常', String(error));
  }
}

/** 遍历顶层窗口，返回按 z-order 从上到下的列表（前 N 个）。 */
function topLevelWindows(api: Win32, limit = 20): { hwnd: number; cls: string; title: string }[] {
  const ranked: { hwnd: number; cls: string; title: string }[] = [];
  const cb = api.koffi.register((hwnd: unknown) => {
    const value = hwndOrNull(hwnd);
    if (value !== null) {
      ranked.push({ hwnd: value, cls: windowClassName(api, value), title: windowTitle(api, value) });
    }
    return ranked.length < limit ? 1 : 0;
  }, api.koffi.pointer(api.enumCbProto));
  try {
    api.EnumWindows(cb, 0);
  } catch (error) {
    log('[win32] z-order 扫描失败', String(error));
  } finally {
    api.koffi.unregister(cb);
  }
  return ranked;
}

/**
 * 交给 Windows 自己拖动窗口（原生 move loop）。
 *
 * 为什么不用"轮询光标 + setPosition"：松开鼠标要靠渲染层收到 `mouseup`，
 * 指针一移出窗口就丢事件，拖动会"粘住"；而且每 16ms 一次 `setPosition`
 * 肉眼可见滞后、与层级定时器抢 z-order。
 *
 * 注意：调用前必须先 `suspendRestingStyle()` 摘掉 `WS_EX_NOACTIVATE`，
 * 否则系统会跳过原生 move loop（窗口拖不动）。
 */
export function beginNativeMove(window: BrowserWindow): boolean {
  const api = loadWin32();
  if (!api) return false;
  const hwnd = toHwnd(window);
  try {
    api.ReleaseCapture();
    api.SendMessageW(hwnd, 0x00a1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
    return true;
  } catch (error) {
    log('[win32] 原生拖动失败，回退自实现：', error);
    return false;
  }
}

/**
 * 找到位于桌面图标之下的空 WorkerW 窗口（"壁纸层"模式）。
 *
 * ⚠️ 本函数会给 Progman 发 `0x052C` **催生** WorkerW。这条消息只应在本模式使用：
 * 登录阶段催生 WorkerW 会和 Explorer 恢复桌面图标布局抢时序，把用户图标顺序搞乱。
 * 默认的 `desktop` 模式**绝不**走这条路。
 */
function findWorkerW(api: Win32): number | null {
  const { koffi, enumCbProto } = api;
  const progman = hwndOrNull(api.FindWindowW('Progman', null));
  if (progman === null) return null;

  const result = Buffer.alloc(8);
  api.SendMessageTimeoutW(progman, 0x052c, 0xd, 0x1, 0, 1000, result);

  let shellViewParent: number | null = null;
  const callback = koffi.register((hwnd: unknown) => {
    const value = hwndOrNull(hwnd);
    if (value === null) return 1;
    const defView = hwndOrNull(api.FindWindowExW(value, null, 'SHELLDLL_DefView', null));
    if (defView !== null) {
      shellViewParent = value;
      return 0;
    }
    return 1;
  }, koffi.pointer(enumCbProto));

  api.EnumWindows(callback, 0);
  koffi.unregister(callback);

  if (shellViewParent === null) return null;
  return hwndOrNull(api.FindWindowExW(null, shellViewParent, 'WorkerW', null));
}

/** 当前是否处于桌面层静息（渲染层/设置面板可用来解释行为）。 */
export function isRestingOnDesktopLayer(): boolean {
  return restingDesktopLayer;
}

export { isLeftButtonDown, isWin32Available };

/**
 * 订阅桌面层级相关的窗口消息（事件驱动，替代轮询兜底）。
 *
 * 三条通道对应三类会把宿主关系打坏的系统事件：
 * - `WM_DISPLAYCHANGE`：分辨率/显示器拓扑变化，Explorer 可能重建桌面窗口；
 * - `WM_SETTINGCHANGE`：工作区（任务栏）变化等，触发频率高，调用方需自行去抖；
 * - `TaskbarCreated`（注册消息）：**Explorer 重启**的信号。此时旧的
 *   `SHELLDLL_DefView` 句柄已失效，必须作废宿主缓存后重新静息。
 *
 * @returns 取消订阅函数。
 */
export function watchDesktopLayerMessages(
  window: BrowserWindow,
  onChange: (reason: string) => void,
): () => void {
  if (process.platform !== 'win32' || window.isDestroyed()) return () => {};
  const api = loadWin32();
  if (!api) return () => {};

  const hooked: number[] = [];
  const hook = (message: number, reason: string): void => {
    if (message <= 0 || window.isDestroyed()) return;
    try {
      if (window.isWindowMessageHooked(message)) return;
      window.hookWindowMessage(message, () => onChange(reason));
      hooked.push(message);
    } catch (error) {
      log('[win32] 订阅窗口消息失败', { message, reason, error: String(error) });
    }
  };

  hook(WM_DISPLAYCHANGE, 'display-change');
  hook(WM_SETTINGCHANGE, 'setting-change');
  try {
    hook(Number(api.RegisterWindowMessageW('TaskbarCreated')), 'explorer-restart');
  } catch (error) {
    log('[win32] 注册 TaskbarCreated 消息失败', String(error));
  }

  return () => {
    for (const message of hooked) {
      try {
        if (!window.isDestroyed()) window.unhookWindowMessage(message);
      } catch {
        /* 窗口已销毁，忽略 */
      }
    }
  };
}
