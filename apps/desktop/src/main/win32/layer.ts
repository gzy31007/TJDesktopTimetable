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
  GetShellWindow: AnyFn;
  GetForegroundWindow: AnyFn;
  GetClassNameW: AnyFn;
  GetWindowTextW: AnyFn;
  WindowFromPoint: AnyFn;
  GetAncestor: AnyFn;
  GetWindow: AnyFn;
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
const SWP_SHOWWINDOW = 0x0040;

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
      GetShellWindow: user32.func('__stdcall', 'GetShellWindow', 'void *', []) as AnyFn,
      GetForegroundWindow: user32.func('__stdcall', 'GetForegroundWindow', 'void *', []) as AnyFn,
      GetClassNameW: user32.func('__stdcall', 'GetClassNameW', 'int32', ['void *', 'void *', 'int32']) as AnyFn,
      GetWindowTextW: user32.func('__stdcall', 'GetWindowTextW', 'int32', ['void *', 'void *', 'int32']) as AnyFn,
      WindowFromPoint: user32.func('__stdcall', 'WindowFromPoint', 'void *', ['int64']) as AnyFn,
      GetAncestor: user32.func('__stdcall', 'GetAncestor', 'void *', ['void *', 'uint32']) as AnyFn,
      GetWindow: user32.func('__stdcall', 'GetWindow', 'void *', ['void *', 'uint32']) as AnyFn,
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

/**
 * 立即把挂件恢复到"桌面层上方 + 可见"。
 *
 * 渲染层在拖动/点击结束后调用 —— 这是最可靠的时机：Win32 侧此刻刚结束鼠标
 * 交互、owner 恢复，而 `keepAlive` 的下一拍可能还在摘除/恢复的中间态。
 */
/**
 * 诊断：打印挂件在顶层 z-order 里的站位，以及前两个压着它的窗口。
 *
 * `恢复后看不见` 说明窗口可见、owner 正常，但被别的窗口盖住了 —— 这个方法
 * 直接把"谁盖着它"打出来，避免继续盲猜。
 */
export function logZOrder(window: BrowserWindow): void {
  try {
    const api = loadWin32();
    if (!api || window.isDestroyed()) return;
    const hwnd = toHwnd(window);
    const top = topLevelWindows(api, 512);
    const index = top.findIndex((item) => item.hwnd === hwnd);
    const above = index > 0 ? top.slice(0, index).slice(-2) : [];
    const [x = 0, y = 0] = window.getPosition();
    const progman = hwndOrNull(api.GetShellWindow());
    const progmanIndex = progman === null ? -1 : top.findIndex((item) => item.hwnd === progman);
    log('[win32] z-order', {
      position: index < 0 ? '未在顶层窗口中找到' : `第 ${index + 1}/${top.length}`,
      xy: `${x},${y}`,
      // 挂件必须在 Progman 之后（z-order 更低）：progmanIndex < index 表示被桌面盖住 = 异常
      progmanIndex: progmanIndex < 0 ? '未找到' : progmanIndex + 1,
      coveredByShell: progmanIndex >= 0 && index >= 0 && progmanIndex < index,
      above: above.map((item) => `${item.cls}${item.title ? `(${item.title})` : ''}`),
      below: index >= 0 ? top.slice(index + 1, index + 3).map((item) => item.cls) : [],
    });
  } catch (error) {
    log('[win32] z-order 诊断异常', String(error));
  }
}

export function ensureWidgetVisible(window: BrowserWindow): void {
  const api = loadWin32();
  if (!api || window.isDestroyed()) return;
  const hwnd = toHwnd(window);
  if (!api.IsWindowVisible(hwnd)) api.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
  if (api.IsIconic(hwnd)) api.ShowWindow(hwnd, SW_RESTORE);
  // 注意：不要拿 owner 当 insertAfter（那会把窗口插到 owner 下面、被桌面盖住）
  api.SetWindowPos(
    hwnd,
    HWND_BOTTOM,
    0,
    0,
    0,
    0,
    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW,
  );
  const progman = hwndOrNull(api.GetShellWindow());
  log('[win32] ensureWidgetVisible', { owner: hwndOrNull(api.GetWindowLongPtrW(hwnd, GWLP_HWNDPARENT)), progman });
}

/** 遍历顶层窗口，返回按 z-order 从上到下的列表（前 N 个）。 */
function topLevelWindows(api: Win32, limit = 20): { hwnd: number; cls: string; title: string }[] {
  const ranked: { hwnd: number; cls: string; title: string }[] = [];
  const cb = api.koffi.register((hwnd: unknown) => {
    const value = hwndOrNull(hwnd);
    if (value !== null) {
      const clsBuf = Buffer.alloc(128);
      api.GetClassNameW(value, clsBuf, 64);
      const titleBuf = Buffer.alloc(256);
      api.GetWindowTextW(value, titleBuf, 128);
      ranked.push({
        hwnd: value,
        cls: clsBuf.toString('utf16le').split('\0')[0] ?? '',
        title: titleBuf.toString('utf16le').split('\0')[0] ?? '',
      });
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

/**
 * 把窗口压到 z-order 底部。
 *
 * ⚠️ `SetWindowPos` 的 `hWndInsertAfter` 语义是"插到该窗口**之后**（z-order 更低）"，
 * 不是"上方"。把 owner 传进来会让挂件跑到 owner **下面**，反而被桌面盖住（实测）。
 *
 * owned 窗口本来就有"恒在 owner 之上"的保证，所以这里就该用 `HWND_BOTTOM`：
 * 压到所有普通窗口之下，同时仍在 owner（Progman）之上。
 */
export function pushToBottom(api: Win32, hwnd: number, insertAfter: number = HWND_BOTTOM): void {
  api.SetWindowPos(
    hwnd,
    insertAfter,
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
/**
 * 解析桌面 owner。
 *
 * 优先 **Progman（GetShellWindow）** —— 与 WitchDrawer 的
 * `DesktopShellHost.ResolveOwner` 一致：Win11 上 Progman 本身就是桌面宿主，
 * 顶层窗口做 owner 时 owned 窗口的 z-order 语义稳定（Win+D 不会把它带走）。
 *
 * 之前我们用的是 `SHELLDLL_DefView`（Progman 的**子窗口**）当 owner，
 * 在 Win+D 路径下 z-order 行为不同，实测会"恢复了却看不见"。
 */
function resolveDesktopOwner(api: Win32): { owner: number | null; source: string } {
  const shell = hwndOrNull(api.GetShellWindow());
  if (shell !== null) {
    const defView = hwndOrNull(api.FindWindowExW(shell, null, 'SHELLDLL_DefView', null));
    return { owner: shell, source: defView !== null ? 'Progman(含 DefView)' : 'Progman' };
  }
  const defView = findDesktopIconView(api);
  return { owner: defView, source: defView !== null ? 'DefView(回退)' : '无' };
}

export function attachToDesktopIconLayer(window: BrowserWindow): boolean {
  const api = loadWin32();
  if (!api) return false;
  const hwnd = toHwnd(window);
  const { owner, source } = resolveDesktopOwner(api);
  if (owner === null) {
    log('[win32] 未找到可用的桌面宿主（Progman / DefView）');
    return false;
  }
  api.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, owner);
  const actual = hwndOrNull(api.GetWindowLongPtrW(hwnd, GWLP_HWNDPARENT));
  if (actual !== owner) {
    log('[win32] 桌面层 Owner 设置失败', { expected: owner, actual, source });
    return false;
  }
  pushToBottom(api, hwnd, owner);
  log('[win32] 已挂到桌面宿主', { owner, source });
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

  /**
   * owner 关系是否还在期望的桌面宿主上（Win+D 之后 Explorer/DWM 会清掉它）。
   *
   * 必须用 `resolveDesktopOwner` 的期望值比较 —— 早期版本硬比较 DefView，
   * 而 owner 已改为 Progman，导致两者永不相等、每秒误判"丢失"并重挂。
   */
  const ownerLost = (): boolean => {
    if (!desktopOwned) return false;
    const { owner: expected } = resolveDesktopOwner(api);
    if (expected === null) return false;
    const actual = hwndOrNull(api.GetWindowLongPtrW(hwnd, GWLP_HWNDPARENT));
    return actual !== expected;
  };

  let lastState = '';
  /** 当前 owner（桌面图标层）句柄；没有则返回 null。 */
  const currentOwner = (): number | null => hwndOrNull(api.GetWindowLongPtrW(hwnd, GWLP_HWNDPARENT));

  /**
   * 桌面（Progman/WorkerW）是否是前台窗口。
   *
   * 对应 WitchDrawer 的 `ShouldSendToBottom(isDesktopForeground)`：只有桌面在前台时
   * 才把挂件压到底。否则"显示桌面"之后的无条件置底会把挂件压到桌面层之下。
   */
  const isDesktopForeground = (): boolean => {
    const foreground = hwndOrNull(api.GetForegroundWindow());
    if (foreground === null) return true; // 没有前台窗口 = 桌面在前
    const shell = hwndOrNull(api.GetShellWindow());
    if (shell !== null && foreground === shell) return true;
    const buffer = Buffer.alloc(64);
    api.GetClassNameW(foreground, buffer, 32);
    const name = buffer.toString('utf16le').replace(/\0.*$/, '');
    return name === 'Progman' || name === 'WorkerW';
  };

  /**
   * 鼠标按下期间临时摘掉 Shell owner（WitchDrawer 的
   * `SuspendDesktopOwnershipForMouseInput`）。
   *
   * 不摘的话，Explorer 会把被点到的挂件记成 Progman 的 "last active popup"，
   * 之后 Win+D 就会变成"激活挂件"而不是显示桌面 → 表现成挂件消失/异常。
   */
  let ownershipSuspended = false;
  let lastButtonDown = false;
  let ownerRelogged = false;

  let zorderTicks = 0;
  const keepAlive = (): void => {
    // 每 3 拍打一次 z-order 诊断（主进程侧，不依赖渲染层事件）
    zorderTicks += 1;
    if (zorderTicks % 3 === 0) logZOrder(window);

    // 埋点：只有状态变化时才写日志，用于定位 Win+D 后"有时恢复有时不恢复"
    const snapshot = `${api.IsWindowVisible(hwnd) ? 'vis' : 'hid'}/${api.IsIconic(hwnd) ? 'iconic' : 'normal'}/${ownerLost() ? 'no-owner' : 'owner'}`;
    if (snapshot !== lastState) {
      lastState = snapshot;
      log('[win32] 窗口状态变化', { state: snapshot });
    }

    // 鼠标交互：按下时摘 owner，松开后挂回（owner 由 keepAlive 轮询检测，不依赖渲染层事件）
    const buttonDown = isLeftButtonDown();
    if (buttonDown && !lastButtonDown && desktopOwned && !ownershipSuspended) {
      if (api.SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, 0) !== 0) {
        ownershipSuspended = true;
        log('[win32] 鼠标交互：已临时摘除桌面层 owner');
      }
    } else if (!buttonDown && lastButtonDown && ownershipSuspended) {
      ownershipSuspended = false;
      attachToDesktopIconLayer(window);
      log('[win32] 鼠标交互结束：已恢复桌面层 owner');
    }
    lastButtonDown = buttonDown;

    let recovered = false;
    if (api.IsIconic(hwnd)) {
      // 被"显示桌面"最小化：必须用 SW_RESTORE，SW_SHOWNOACTIVATE 不会恢复最小化窗口
      api.ShowWindow(hwnd, SW_RESTORE);
      recovered = true;
      log('[win32] 显示桌面后恢复：SW_RESTORE');
    } else if (!api.IsWindowVisible(hwnd)) {
      api.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
      recovered = true;
      log('[win32] 窗口被隐藏，已重新显示');
    }

    // owner 丢了 → 重新挂回桌面层。注意：鼠标交互期间是我们主动摘除的，
    // 那种情况不要在这里抢挂回去（否则和"鼠标交互结束恢复"互相打架，
    // 实测会出现 owner 反复丢失/重挂、恢复时读到 null 的问题）。
    if (ownerLost() && !ownershipSuspended) {
      if (!ownerRelogged) {
        ownerRelogged = true;
        log('[win32] 桌面层 owner 丢失，重新挂载（后续同类不再重复记录）');
      }
      attachToDesktopIconLayer(window);
      return;
    }
    ownerRelogged = false;

    if (recovered) {
      // 先确保 owner 关系还在（owner 保证"在桌面之上"），再压到 HWND_BOTTOM
      if (currentOwner() === null) {
        log('[win32] 恢复时 owner 为空，先重新挂载');
        attachToDesktopIconLayer(window);
      }
      pushToBottom(api, hwnd);
      // 再来一次：ShowWindow 之后紧接着的 SetWindowPos 偶发被系统丢弃
      api.SetWindowPos(
        hwnd,
        HWND_BOTTOM,
        0,
        0,
        0,
        0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW,
      );
      log('[win32] 恢复完成（owner 保持 + 压到底部）', { owner: currentOwner() });
      logZOrder(window);
      return;
    }

    // 仅在桌面是前台时置底：否则（例如"显示桌面"刚结束）会把挂件压到桌面层之下
    if (isDesktopForeground()) {
      pushToBottom(api, hwnd);
    }
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
