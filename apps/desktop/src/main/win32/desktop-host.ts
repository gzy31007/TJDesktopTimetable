import { log } from '../logger.js';
import {
  GA_ROOT,
  GWLP_HWNDPARENT,
  HWND_NOTOPMOST,
  SWP_NOACTIVATE,
  SWP_NOMOVE,
  SWP_NOSIZE,
  hwndOrNull,
  isWindow,
  loadWin32,
  windowClassName,
  type Win32,
} from './api.js';

/**
 * 桌面宿主（Explorer 的桌面图标视图）与 owner 关系管理。
 *
 * ## 为什么宿主用 `SHELLDLL_DefView` 而不是 Progman
 *
 * 挂件要在"显示桌面（Win+D）"之后仍然可见，唯一可靠的做法是让它成为桌面宿主窗口的
 * **owned window**：owned 窗口恒在 owner 之上，且不属于"显示桌面"要最小化的那批
 * 独立顶层窗口。宿主必须是 Explorer 已经创建好的桌面图标视图 `SHELLDLL_DefView`
 * —— 它是 Progman 或 WorkerW 的子窗口，真正承载桌面图标与桌面的层级语义。
 *
 * 三条硬约束（都是真机踩出来的）：
 *
 * 1. **不使用 `SetParent`**：把挂件变成 WorkerW 的子窗口会让它被桌面图标压在下面，
 *    而且拖动坐标会错乱（子窗口坐标相对父窗口）。owner 关系（`GWLP_HWNDPARENT`）
 *    让窗口保持顶层窗口身份，拖动/坐标/DPI 全部正常。
 * 2. **绝不主动催生 WorkerW**（不给 Progman 发 `0x052C`）：登录阶段这么做会和 Explorer
 *    恢复图标布局的流程抢时序，把用户的桌面图标顺序搞乱。
 * 3. **写 owner 之后必须读回校验**：`SetWindowLongPtrW` 在某些时序下会静默失败
 *    （Explorer 刚重启、句柄已失效），校验失败必须还原原 owner 并走回退路径。
 *
 * 参照的是 DeskBox（GPL-3.0-only）**已验证的机制**，代码为独立实现，不含其源码。
 */

export interface DesktopHost {
  hwnd: number;
  /** 宿主来源，仅用于日志：`DefView` = 桌面图标视图（正常）。 */
  source: 'DefView';
}

let cachedHost: number | null = null;

/** owner 存档：写入前记下原值，回退时还原（不清空别人的 owner 关系）。 */
const originalOwners = new Map<number, number>();

/** 宿主句柄缓存作废。Explorer 重启 / 显示拓扑变化后必须调用。 */
export function invalidateDesktopHostCache(): void {
  cachedHost = null;
}

/**
 * 找桌面图标视图：遍历顶层窗口，找第一个含 `SHELLDLL_DefView` 子窗口的那个。
 *
 * 为什么是 `EnumWindows` 而不是 `FindWindowW('Progman')`：桌面图标视图可能挂在
 * Progman 下，也可能挂在某个 WorkerW 下（其它壁纸软件/活动桌面）。枚举 + 找子窗口
 * 两者都能覆盖；只认 Progman 会在部分机器上找不到。
 */
export function resolveDesktopHost(): DesktopHost | null {
  const api = loadWin32();
  if (!api) return null;

  if (cachedHost !== null && isWindow(api, cachedHost)) {
    return { hwnd: cachedHost, source: 'DefView' };
  }
  cachedHost = null;

  const found = findDesktopIconView(api);
  if (found === null) {
    // 不催生 WorkerW，交给调用方走回退；下一次层级操作会再试一遍。
    log('[win32] 未找到已创建的桌面图标视图（SHELLDLL_DefView），使用回退层级');
    return null;
  }
  cachedHost = found;
  return { hwnd: found, source: 'DefView' };
}

function findDesktopIconView(api: Win32): number | null {
  let result: number | null = null;
  const cb = api.koffi.register((hwnd: unknown) => {
    const value = hwndOrNull(hwnd);
    if (value === null) return 1;
    const defView = hwndOrNull(api.FindWindowExW(value, null, 'SHELLDLL_DefView', null));
    if (defView !== null) {
      result = defView;
      return 0; // 找到就停
    }
    return 1;
  }, api.koffi.pointer(api.enumCbProto));
  try {
    api.EnumWindows(cb, 0);
  } catch (error) {
    log('[win32] 枚举桌面宿主失败', String(error));
  } finally {
    api.koffi.unregister(cb);
  }
  return result;
}

export function getCurrentOwner(api: Win32, hwnd: number): number | null {
  return hwndOrNull(api.GetWindowLongPtrW(hwnd, GWLP_HWNDPARENT));
}

/**
 * 把窗口挂成桌面宿主的 owned window。写入前存档原 owner，写入后读回校验。
 *
 * @returns 成功与否；失败时已还原原 owner，调用方应走"无 owner + 置底"回退。
 */
export function attachToDesktopHost(api: Win32, hwnd: number, host: number): boolean {
  if (!isWindow(api, hwnd) || !isWindow(api, host)) return false;

  if (!originalOwners.has(hwnd)) {
    originalOwners.set(hwnd, getCurrentOwner(api, hwnd) ?? 0);
  }

  if (getCurrentOwner(api, hwnd) !== host) {
    api.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, host);
  }

  if (getCurrentOwner(api, hwnd) !== host) {
    log('[win32] 桌面宿主 owner 写入失败，还原原 owner', { hwnd, host });
    restoreOriginalOwner(api, hwnd);
    invalidateDesktopHostCache();
    return false;
  }
  return true;
}

/** 还原到挂载前的 owner（没有存档就置 0）。 */
export function restoreOriginalOwner(api: Win32, hwnd: number): void {
  const original = originalOwners.get(hwnd);
  originalOwners.delete(hwnd);
  if (original === undefined) return;
  try {
    api.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, original);
    api.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
  } catch (error) {
    log('[win32] 还原 owner 失败', String(error));
  }
}

/** 释放记录（窗口关闭时调用，避免 Map 泄漏）。 */
export function forgetOwnerRecord(hwnd: number): void {
  originalOwners.delete(hwnd);
}

export function hasOwnerRecord(hwnd: number): boolean {
  return originalOwners.has(hwnd);
}

/** 当前前台窗口的根窗口（顶层窗口）。 */
export function foregroundRoot(api: Win32): number | null {
  const foreground = hwndOrNull(api.GetForegroundWindow());
  if (foreground === null) return null;
  const root = hwndOrNull(api.GetAncestor(foreground, GA_ROOT));
  return root ?? foreground;
}

/** 窗口自身或其祖先是不是桌面壳窗口（Progman / WorkerW / SHELLDLL_DefView）。 */
export function isDesktopShellWindow(api: Win32, hwnd: number | null): boolean {
  let current = hwnd;
  let guard = 0;
  while (current !== null && guard < 16) {
    const className = windowClassName(api, current);
    if (className === 'Progman' || className === 'WorkerW' || className === 'SHELLDLL_DefView') {
      return true;
    }
    current = hwndOrNull(api.GetParent(current));
    guard += 1;
  }
  return false;
}

/** 诊断用：宿主句柄与当前 owner。 */
export function describeHost(api: Win32, hwnd: number): { host: number | null; owner: number | null } {
  return { host: cachedHost, owner: getCurrentOwner(api, hwnd) };
}
