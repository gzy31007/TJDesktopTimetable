import type { BrowserWindow } from 'electron';
import type { WidgetMode } from '../../shared/ipc.js';

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
}

const GWL_EXSTYLE = -20;
const WS_EX_TOOLWINDOW = 0x00000080;
const WS_EX_NOACTIVATE = 0x08000000;
const WS_EX_APPWINDOW = 0x00040000;

const SW_SHOWNOACTIVATE = 4;
const SW_HIDE = 0;

/** `SetWindowPos` 的 hWndInsertAfter 常量。 */
const HWND_BOTTOM = 1;

const SWP_NOSIZE = 0x0001;
const SWP_NOMOVE = 0x0002;
const SWP_NOACTIVATE = 0x0010;
const SWP_NOOWNERZORDER = 0x0200;
const SWP_NOSENDCHANGING = 0x0400;

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
    };
  } catch (error) {
    loadFailed = true;
    cached = null;
    console.error('[win32] koffi/user32 加载失败，退化为普通置底窗口：', error);
  }
  return cached;
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

function applyExStyle(api: Win32, hwnd: number): void {
  const current = Number(api.GetWindowLongPtrW(hwnd, GWL_EXSTYLE));
  const next = (current | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
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

export interface LayerOptions {
  mode: WidgetMode;
  /** 置底保险定时器间隔（毫秒），0 表示关闭。 */
  keepAtBottomIntervalMs?: number;
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
  applyExStyle(api, hwnd);

  let mode: WidgetMode = options.mode;
  if (mode === 'wallpaper') {
    const workerW = findWorkerW(api);
    if (workerW === null) {
      mode = 'desktop';
      options.onFallback?.('未找到 WorkerW（可能被其它壁纸软件占用），已回退为置底模式');
    } else {
      api.SetParent(hwnd, workerW);
      api.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }
  }

  const interval = options.keepAtBottomIntervalMs ?? 1000;
  let timer: NodeJS.Timeout | null = null;

  const keepAlive = (): void => {
    if (!api.IsWindowVisible(hwnd) || api.IsIconic(hwnd)) {
      // "显示桌面" / 最小化后重新露面，但不抢焦点
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
      api.ShowWindow(hwnd, SW_HIDE);
    },
  };
}
