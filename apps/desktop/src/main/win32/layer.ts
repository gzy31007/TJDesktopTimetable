import type { BrowserWindow } from 'electron';
import type { WidgetMode } from '../../shared/ipc.js';
import { log } from '../logger.js';

/**
 * Win32 窗口层级控制（koffi 纯 FFI 调用 user32.dll）。
 *
 * 两种模式：
 * - `desktop`（默认）：普通顶层窗口 + `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，
 *   用 `SetWindowPos(HWND_BOTTOM)` 压到 z-order 最底；保险定时器每秒重压一次，
 *   并处理"显示桌面"导致窗口被隐藏的情况。
 * - `wallpaper`：WorkerW 注入（Progman → 0x052C → 找 SHELLDLL_DefView → 取其后空 WorkerW
 *   → `SetParent`），即贴在桌面图标之下的真壁纸层。失败自动回退 `desktop`。
 *
 * 说明：非 Windows 平台（开发期的 Linux/WSL）直接退化为 Electron 自身的窗口属性，
 * 保证 `pnpm build` / 单测不会被原生模块拖垮。
 */

type AnyFn = (...args: unknown[]) => unknown;

interface KoffiLib {
  func(convention: string, name: string, ret: string, args: unknown[]): AnyFn;
}

interface KoffiLike {
  load(path: string): KoffiLib;
  proto(convention: string, name: string, ret: string, args: unknown[]): unknown;
  pointer(type: unknown): unknown;
  register(fn: (...args: unknown[]) => unknown, type: unknown): unknown;
  unregister(handle: unknown): void;
}

interface Win32 {
  /** 原始 koffi 模块，回调注册要用。 */
  koffi: KoffiLike;
  /** EnumWindows 回调原型。 */
  enumCbProto: unknown;
  FindWindowW: AnyFn;
  FindWindowExW: AnyFn;
  SendMessageTimeoutW: AnyFn;
  EnumWindows: AnyFn;
  SetParent: AnyFn;
  ShowWindow: AnyFn;
  SetWindowLongPtrW: AnyFn;
  GetWindowLongPtrW: AnyFn;
  SetWindowPos: AnyFn;
  IsIconic: AnyFn;
  IsWindowVisible: AnyFn;
  ReleaseCapture: AnyFn;
  SendMessageW: AnyFn;
  GetAsyncKeyState: AnyFn;
}

const GWL_EXSTYLE = -20;
const WS_EX_TOOLWINDOW = 0x00000080;
const WS_EX_NOACTIVATE = 0x08000000;
const WS_EX_APPWINDOW = 0x00040000;

const SW_SHOWNOACTIVATE = 4;
const SW_RESTORE = 9;
const SW_HIDE = 0;

/** `SetWindowLongPtrW` 的索引：子窗口=父窗口；顶层窗口=Owner。 */
const GWLP_HWNDPARENT = -8;

/** `SetWindowPos` 的 hWndInsertAfter 常量。 */
const HWND_BOTTOM = 1;

const SWP_NOSIZE = 0x0001;
const SWP_NOMOVE = 0x0002;
const SWP_NOACTIVATE = 0x0010;
const SWP_NOOWNERZORDER = 0x0200;
const SWP_NOSENDCHANGING = 0x0400;

const WM_NCLBUTTONDOWN = 0x00a1;
/** 命中测试码：标题栏（用于原生拖动）。 */
const HTCAPTION = 2;
const VK_LBUTTON = 0x01;

let cached: Win32 | null | undefined;
let loadFailed = false;

function loadWin32(): Win32 | null {
  if (cached !== undefined) return cached;
  if (process.platform !== 'win32' || loadFailed) {
    cached = null;
    return null;
  }
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const koffi = require('koffi') as KoffiLike;
    const user32 = koffi.load('user32.dll');
    const EnumWindowsCb = koffi.proto('__stdcall', 'EnumWindowsCb', 'int32', ['void *', 'intptr_t']);
    cached = {
      koffi,
      enumCbProto: EnumWindowsCb,
      FindWindowW: user32.func('__stdcall', 'FindWindowW', 'void *', ['str16', 'str16']) as AnyFn,
      FindWindowExW: user32.func('__stdcall', 'FindWindowExW', 'void *', ['void *', 'void *', 'str16', 'str16']) as AnyFn,
      SendMessageTimeoutW: user32.func('__stdcall', 'SendMessageTimeoutW', 'intptr_t', [
        'void *',
        'uint32',
        'uintptr_t',
        'intptr_t',
        'uint32',
        'uint32',
        'void *',
      ]) as AnyFn,
      EnumWindows: user32.func('__stdcall', 'EnumWindows', 'int32', [koffi.pointer(EnumWindowsCb), 'intptr_t']) as AnyFn,
      SetParent: user32.func('__stdcall', 'SetParent', 'void *', ['void *', 'void *']) as AnyFn,
      ShowWindow: user32.func('__stdcall', 'ShowWindow', 'int32', ['void *', 'int32']) as AnyFn,
      SetWindowLongPtrW: user32.func('__stdcall', 'SetWindowLongPtrW', 'intptr_t', ['void *', 'int32', 'intptr_t']) as AnyFn,
      GetWindowLongPtrW: user32.func('__stdcall', 'GetWindowLongPtrW', 'intptr_t', ['void *', 'int32']) as AnyFn,
      SetWindowPos: user32.func('__stdcall', 'SetWindowPos', 'int32', [
        'void *',
        'void *',
        'int32',
        'int32',
        'int32',
        'int32',
        'uint32',
      ]) as AnyFn,
      IsIconic: user32.func('__stdcall', 'IsIconic', 'int32', ['void *']) as AnyFn,
      IsWindowVisible: user32.func('__stdcall', 'IsWindowVisible', 'int32', ['void *']) as AnyFn,
      ReleaseCapture: user32.func('__stdcall', 'ReleaseCapture', 'int32', []) as AnyFn,
      SendMessageW: user32.func('__stdcall', 'SendMessageW', 'intptr_t', [
        'void *',
        'uint32',
        'uintptr_t',
        'intptr_t',
      ]) as AnyFn,
      GetAsyncKeyState: user32.func('__stdcall', 'GetAsyncKeyState', 'int16', ['int32']) as AnyFn,
    };
  } catch (error) {
    loadFailed = true;
    cached = null;
    log('[win32] koffi/user32 加载失败，退化为普通置底窗口：', error);
  }
  return cached;
}

/**
 * 交给 Windows 自己拖动窗口（原生 move loop）。
 *
 * 为什么不用"轮询光标 + setPosition"：那种做法有三个硬伤——
 * 1. 松开鼠标必须靠渲染层收到 `mouseup`，指针一移出窗口就丢事件，拖动会"粘住"；
 * 2. 每 16ms 一次 `setPosition` 要等 DWM 合成，肉眼可见的滞后、橡皮筋感；
 * 3. 与置底定时器抢 z-order，拖动中会跳。
 *
 * `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)` 让系统进入自己的模态拖动循环：
 * 跟手性 = 系统窗口拖动，且自动处理鼠标捕获、多屏、DPI 与松手结束。
 * 代价是该调用会阻塞到用户松手（主进程在拖动期间不处理其它消息，可接受）；
 * WorkerW 壁纸层模式下窗口是子窗口，原生拖动坐标会错乱，因此只在置底模式使用。
 */
export function beginNativeMove(window: BrowserWindow): boolean {
  const api = loadWin32();
  if (!api) return false;
  const hwnd = toHwnd(window);
  try {
    api.ReleaseCapture();
    api.SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    return true;
  } catch (error) {
    log('[win32] 原生拖动失败，回退自实现：', error);
    return false;
  }
}

/** 左键当前是否按下（自实现拖动/缩放的兜底：即使丢了 pointerup 也能收尾）。 */
export function isLeftButtonDown(): boolean {
  const api = loadWin32();
  if (!api) return false;
  try {
    return (Number(api.GetAsyncKeyState(VK_LBUTTON)) & 0x8000) !== 0;
  } catch {
    return false;
  }
}

export function isWin32Available(): boolean {
  return loadWin32() !== null;
}

/** Electron 的原生窗口句柄 → number（x64 上取低 8 字节；HWND 实际值远小于 2^53）。 */
function toHwnd(window: BrowserWindow): number {
  const buffer = window.getNativeWindowHandle();
  return buffer.length >= 8 ? Number(buffer.readBigUInt64LE(0)) : buffer.readUInt32LE(0);
}

function hwndOrNull(value: unknown): number | null {
  if (typeof value === 'bigint') return value === 0n ? null : Number(value);
  if (typeof value === 'number') return value === 0 ? null : value;
  return null;
}

/**
 * 扩展样式。
 *
 * 注意**不加** `WS_EX_NOACTIVATE`：不可激活的窗口会被系统跳过原生 move loop，
 * 表现为"完全拖不动"。窗口"不打扰"的职责改由"贴桌面层 + 置底"承担
 * （参考 DeskBox：只在 DesktopPinned 模式才加 NOACTIVATE）。
 */
function applyExStyle(api: Win32, hwnd: number, noActivate = false): void {
  const current = Number(api.GetWindowLongPtrW(hwnd, GWL_EXSTYLE));
  let next = (current | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
  next = noActivate ? next | WS_EX_NOACTIVATE : next & ~WS_EX_NOACTIVATE;
  api.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, next);
}

export function pushToBottom(api: Win32, hwnd: number): void {
  api.SetWindowPos(
    hwnd,
    HWND_BOTTOM,
    0,
    0,
    0,
    0,
    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING,
  );
}

/** 找到位于桌面图标之下的空 WorkerW 窗口。 */
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

/** 桌面图标视图（`SHELLDLL_DefView`）本身 —— 它是桌面图标的容器窗口。 */
function findDesktopIconView(api: Win32): number | null {
  const { koffi, enumCbProto } = api;
  let found: number | null = null;
  const callback = koffi.register((hwnd: unknown) => {
    const value = hwndOrNull(hwnd);
    if (value === null) return 1;
    const defView = hwndOrNull(api.FindWindowExW(value, null, 'SHELLDLL_DefView', null));
    if (defView !== null) {
      found = defView;
      return 0;
    }
    return 1;
  }, koffi.pointer(enumCbProto));

  api.EnumWindows(callback, 0);
  koffi.unregister(callback);
  return found;
}

/**
 * 把顶层窗口的 **Owner** 设为桌面图标层（不是 `SetParent` 成子窗口）。
 *
 * 这是"贴桌面"最省事也最稳的一条路（参考 DeskBox 的 DesktopPinned）：
 * - owned 窗口永远显示在 owner 之上 → 浮在桌面图标之上，不被图标遮挡；
 * - owner 是桌面壳，Win+D / "显示桌面" 不会把它最小化 → 桌面常驻；
 * - 窗口仍是顶层窗口，拖动、鼠标交互、坐标都正常（子窗口方案做不到）。
 */
export function attachToDesktopIconLayer(window: BrowserWindow): boolean {
  const api = loadWin32();
  if (!api) return false;
  const hwnd = toHwnd(window);
  const defView = findDesktopIconView(api);
  if (defView === null) {
    log('[win32] 未找到桌面图标层 SHELLDLL_DefView');
    return false;
  }
  api.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, defView);
  const actual = hwndOrNull(api.GetWindowLongPtrW(hwnd, GWLP_HWNDPARENT));
  if (actual !== defView) {
    log('[win32] 桌面层 Owner 设置失败', { expected: defView, actual });
    return false;
  }
  pushToBottom(api, hwnd);
  log('[win32] 已挂到桌面图标层', { defView });
  return true;
}

export function detachFromDesktopIconLayer(window: BrowserWindow): void {
  const api = loadWin32();
  if (!api) return;
  api.SetWindowLongPtrW(toHwnd(window), GWLP_HWNDPARENT, 0);
}

export interface LayerOptions {
  mode: WidgetMode;
  /** 置底保险定时器间隔（毫秒），0 表示关闭。 */
  keepAtBottomIntervalMs?: number;
  /** 是否把窗口挂到桌面图标层（Win+D 后仍可见）；默认 true。 */
  desktopLayer?: boolean;
  /** 模式降级回调（例如 wallpaper 不可用时）。 */
  onFallback?: (reason: string) => void;
}

export interface LayerHandle {
  /** 实际生效的模式（可能与请求的不同）。 */
  readonly mode: WidgetMode;
  /** 暂停保险定时器（窗口被主动隐藏时调用）。 */
  pause(): void;
  /** 恢复保险定时器并立即重压到底部。 */
  resume(): void;
  /** 立即重压到底部。 */
  refresh(): void;
  /** 解除控制（退出前调用）。 */
  detach(): void;
}

/**
 * 把窗口挂到桌面层级。
 *
 * 非 Windows 平台或 koffi 不可用时返回空实现（仅设置 Electron 自身属性）。
 */
export function attachToDesktop(window: BrowserWindow, options: LayerOptions): LayerHandle {
  window.setSkipTaskbar(true);
  const api = loadWin32();
  if (!api) {
    return { mode: options.mode, pause() {}, resume() {}, refresh() {}, detach() {} };
  }

  const hwnd = toHwnd(window);
  applyExStyle(api, hwnd, false);

  let mode: WidgetMode = options.mode;
  let desktopOwned = false;

  if (mode === 'wallpaper') {
    const workerW = findWorkerW(api);
    if (workerW === null) {
      mode = 'desktop';
      options.onFallback?.('未找到 WorkerW（可能被其它壁纸软件占用），已回退为普通置底模式');
    } else {
      api.SetParent(hwnd, workerW);
      api.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }
  } else if (options.desktopLayer !== false) {
    desktopOwned = attachToDesktopIconLayer(window);
    if (!desktopOwned) {
      options.onFallback?.('未找到桌面图标层，已回退为普通置底模式（Win+D 后会被隐藏）');
    }
  }

  const interval = options.keepAtBottomIntervalMs ?? 1000;
  let timer: NodeJS.Timeout | null = null;

  const keepAlive = (): void => {
    if (api.IsIconic(hwnd)) {
      // 被"显示桌面"最小化：必须用 SW_RESTORE，SW_SHOWNOACTIVATE 不会恢复最小化窗口
      api.ShowWindow(hwnd, SW_RESTORE);
    } else if (!api.IsWindowVisible(hwnd)) {
      api.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }
    pushToBottom(api, hwnd);
  };

  const start = (): void => {
    if (mode !== 'desktop' || interval <= 0 || timer) return;
    timer = setInterval(keepAlive, interval);
  };
  const stop = (): void => {
    if (timer) {
      clearInterval(timer);
      timer = null;
    }
  };

  if (mode === 'desktop') {
    pushToBottom(api, hwnd);
    start();
  }

  return {
    get mode() {
      return mode;
    },
    pause: stop,
    resume() {
      pushToBottom(api, hwnd);
      start();
    },
    refresh() {
      pushToBottom(api, hwnd);
    },
    detach() {
      stop();
      if (desktopOwned) detachFromDesktopIconLayer(window);
      api.ShowWindow(hwnd, SW_HIDE);
    },
  };
}
