import type { BrowserWindow } from 'electron';
import { log } from '../logger.js';

/**
 * Win32 绑定的唯一入口（koffi 纯 FFI，无原生 npm 模块）。
 *
 * 拆出来的原因：桌面层级相关代码有四个关注点（宿主解析、z-order 原语、静息策略、
 * 窗口编排），都要用同一套 user32 句柄与常量。集中在此处避免每个模块各写一份
 * `load`/`func` 声明（早期单文件的写法导致 Win32 接口和业务逻辑缠在一起，
 * 排查 z-order 时要在一千行里跳）。
 *
 * 说明：非 Windows 平台（开发期的 Linux/WSL）`loadWin32()` 返回 `null`，
 * 所有调用方都必须按"退化实现"处理，保证 `pnpm build` / 单测不被原生调用拖垮。
 */

export type AnyFn = (...args: unknown[]) => unknown;

interface KoffiLib {
  func(convention: string, name: string, ret: string, args: unknown[]): AnyFn;
}

export interface KoffiLike {
  load(path: string): KoffiLib;
  proto(convention: string, name: string, ret: string, args: unknown[]): unknown;
  pointer(type: unknown): unknown;
  register(fn: (...args: unknown[]) => unknown, type: unknown): unknown;
  unregister(handle: unknown): void;
  struct(name: string, definition: Record<string, unknown>): unknown;
  sizeof(type: unknown): number;
}

export interface Win32 {
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
  IsWindow: AnyFn;
  ReleaseCapture: AnyFn;
  SendMessageW: AnyFn;
  GetAsyncKeyState: AnyFn;
  GetShellWindow: AnyFn;
  GetForegroundWindow: AnyFn;
  SetForegroundWindow: AnyFn;
  GetLastActivePopup: AnyFn;
  GetWindowThreadProcessId: AnyFn;
  GetClassNameW: AnyFn;
  GetWindowTextW: AnyFn;
  WindowFromPoint: AnyFn;
  GetAncestor: AnyFn;
  GetParent: AnyFn;
  GetWindow: AnyFn;
  GetWindowRect: AnyFn;
  RegisterWindowMessageW: AnyFn;
}

/** `GetWindowLongPtrW` 的索引：基本样式 / 扩展样式。 */
export const GWL_STYLE = -16;
export const GWL_EXSTYLE = -20;
export const WS_EX_TOOLWINDOW = 0x00000080;
export const WS_EX_NOACTIVATE = 0x08000000;
export const WS_EX_APPWINDOW = 0x00040000;

export const SW_HIDE = 0;
export const SW_SHOWNOACTIVATE = 4;
export const SW_RESTORE = 9;

/** `SetWindowLongPtrW` 的索引：子窗口=父窗口；顶层窗口=Owner。 */
export const GWLP_HWNDPARENT = -8;

/** `SetWindowPos` 的 hWndInsertAfter 常量。 */
export const HWND_BOTTOM = 1;
export const HWND_TOP = 0;
export const HWND_TOPMOST = -1;
export const HWND_NOTOPMOST = -2;

export const SWP_NOSIZE = 0x0001;
export const SWP_NOMOVE = 0x0002;
export const SWP_NOZORDER = 0x0004;
export const SWP_NOACTIVATE = 0x0010;
export const SWP_SHOWWINDOW = 0x0040;
export const SWP_NOOWNERZORDER = 0x0200;
export const SWP_NOSENDCHANGING = 0x0400;
export const SWP_FRAMECHANGED = 0x0020;

/** `GetAncestor` 的 flag：取根窗口（顶层窗口，含 owner 链不作为祖先）。 */
export const GA_ROOT = 2;

export const WM_NCLBUTTONDOWN = 0x00a1;
/** 命中测试码：标题栏（用于原生拖动）。 */
export const HTCAPTION = 2;
const VK_LBUTTON = 0x01;

/** 窗口消息 / 注册消息：层级自愈的触发源。 */
export const WM_SETTINGCHANGE = 0x001a;
export const WM_DISPLAYCHANGE = 0x007e;

let cached: Win32 | null | undefined;
let loadFailed = false;

export function loadWin32(): Win32 | null {
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
      FindWindowExW: user32.func('__stdcall', 'FindWindowExW', 'void *', [
        'void *',
        'void *',
        'str16',
        'str16',
      ]) as AnyFn,
      SendMessageTimeoutW: user32.func('__stdcall', 'SendMessageTimeoutW', 'intptr_t', [
        'void *',
        'uint32',
        'uintptr_t',
        'intptr_t',
        'uint32',
        'uint32',
        'void *',
      ]) as AnyFn,
      EnumWindows: user32.func('__stdcall', 'EnumWindows', 'int32', [
        koffi.pointer(EnumWindowsCb),
        'intptr_t',
      ]) as AnyFn,
      SetParent: user32.func('__stdcall', 'SetParent', 'void *', ['void *', 'void *']) as AnyFn,
      ShowWindow: user32.func('__stdcall', 'ShowWindow', 'int32', ['void *', 'int32']) as AnyFn,
      SetWindowLongPtrW: user32.func('__stdcall', 'SetWindowLongPtrW', 'intptr_t', [
        'void *',
        'int32',
        'intptr_t',
      ]) as AnyFn,
      GetWindowLongPtrW: user32.func('__stdcall', 'GetWindowLongPtrW', 'intptr_t', [
        'void *',
        'int32',
      ]) as AnyFn,
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
      IsWindow: user32.func('__stdcall', 'IsWindow', 'int32', ['void *']) as AnyFn,
      ReleaseCapture: user32.func('__stdcall', 'ReleaseCapture', 'int32', []) as AnyFn,
      SendMessageW: user32.func('__stdcall', 'SendMessageW', 'intptr_t', [
        'void *',
        'uint32',
        'uintptr_t',
        'intptr_t',
      ]) as AnyFn,
      GetAsyncKeyState: user32.func('__stdcall', 'GetAsyncKeyState', 'int16', ['int32']) as AnyFn,
      GetShellWindow: user32.func('__stdcall', 'GetShellWindow', 'void *', []) as AnyFn,
      SetForegroundWindow: user32.func('__stdcall', 'SetForegroundWindow', 'int32', ['void *']) as AnyFn,
      GetLastActivePopup: user32.func('__stdcall', 'GetLastActivePopup', 'void *', ['void *']) as AnyFn,
      GetWindowThreadProcessId: user32.func('__stdcall', 'GetWindowThreadProcessId', 'uint32', [
        'void *',
        'void *',
      ]) as AnyFn,
      GetForegroundWindow: user32.func('__stdcall', 'GetForegroundWindow', 'void *', []) as AnyFn,
      GetClassNameW: user32.func('__stdcall', 'GetClassNameW', 'int32', ['void *', 'void *', 'int32']) as AnyFn,
      GetWindowTextW: user32.func('__stdcall', 'GetWindowTextW', 'int32', ['void *', 'void *', 'int32']) as AnyFn,
      WindowFromPoint: user32.func('__stdcall', 'WindowFromPoint', 'void *', ['int64']) as AnyFn,
      GetAncestor: user32.func('__stdcall', 'GetAncestor', 'void *', ['void *', 'uint32']) as AnyFn,
      GetParent: user32.func('__stdcall', 'GetParent', 'void *', ['void *']) as AnyFn,
      GetWindow: user32.func('__stdcall', 'GetWindow', 'void *', ['void *', 'uint32']) as AnyFn,
      GetWindowRect: user32.func('__stdcall', 'GetWindowRect', 'int32', ['void *', 'void *']) as AnyFn,
      RegisterWindowMessageW: user32.func('__stdcall', 'RegisterWindowMessageW', 'uint32', ['str16']) as AnyFn,
    };
  } catch (error) {
    loadFailed = true;
    cached = null;
    log('[win32] koffi/user32 加载失败，退化为普通窗口：', error);
  }
  return cached;
}

export function isWin32Available(): boolean {
  return loadWin32() !== null;
}

/** Electron 的原生窗口句柄 → number（x64 上取低 8 字节；HWND 实际值远小于 2^53）。 */
export function toHwnd(window: BrowserWindow): number {
  const buffer = window.getNativeWindowHandle();
  return buffer.length >= 8 ? Number(buffer.readBigUInt64LE(0)) : buffer.readUInt32LE(0);
}

export function hwndOrNull(value: unknown): number | null {
  if (typeof value === 'bigint') return value === 0n ? null : Number(value);
  if (typeof value === 'number') return value === 0 ? null : value;
  return null;
}

export function isWindow(api: Win32, hwnd: number | null): boolean {
  if (hwnd === null) return false;
  try {
    return Number(api.IsWindow(hwnd)) !== 0;
  } catch {
    return false;
  }
}

export function windowClassName(api: Win32, hwnd: number): string {
  try {
    const buffer = Buffer.alloc(128);
    api.GetClassNameW(hwnd, buffer, 64);
    return buffer.toString('utf16le').replace(/\0.*$/, '');
  } catch {
    return '';
  }
}

export function windowTitle(api: Win32, hwnd: number): string {
  try {
    const buffer = Buffer.alloc(512);
    api.GetWindowTextW(hwnd, buffer, 256);
    return buffer.toString('utf16le').replace(/\0.*$/, '');
  } catch {
    return '';
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
