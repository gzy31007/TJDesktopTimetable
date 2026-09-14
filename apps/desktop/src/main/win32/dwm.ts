import type { BrowserWindow } from 'electron';
import { log } from '../logger.js';

/**
 * DWM 窗口外观（koffi 直调 dwmapi.dll）——Win11 的圆角与深色边框。
 *
 * 为什么必须走 DWM：
 * - 透明无边框窗口（`transparent: true`）不会得到 DWM 圆角，`SetWindowRgn` 又会在
 *   缩放时造成原生层崩溃（实测：拖动缩放 12 秒后进程无日志重启），所以不能自己裁形状；
 * - 桌面亚克力/云母这类系统材质由 DWM 绘制，圆角也由 DWM 统一裁，二者天然对齐。
 *
 * 对应 DeskBox 的做法（WinUI 3 用 `MicaController`/`DesktopAcrylicController` +
 * `SystemBackdropConfiguration`）：Electron 没有控制器层，等价手段就是
 * `backgroundMaterial`（材质）+ `DWMWA_WINDOW_CORNER_PREFERENCE`（圆角）。
 */

interface KoffiFunc {
  (...args: unknown[]): unknown;
}

interface KoffiLike {
  load(path: string): { func(convention: string, name: string, ret: string, args: string[]): unknown };
}

interface DwmApi {
  DwmSetWindowAttribute: KoffiFunc;
}

/** `DWMWINDOWATTRIBUTE`：圆角偏好。Win11 (build 22000+) 才识别。 */
const DWMWA_WINDOW_CORNER_PREFERENCE = 33;
/** 深色标题栏/边框（Win10 1809+ 是 19，Win11 20；两个都试）。 */
const DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
const DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

/** `DWM_WINDOW_CORNER_PREFERENCE` */
export type CornerPreference = 'default' | 'donotround' | 'round' | 'roundsmall';

const CORNER_VALUES: Record<CornerPreference, number> = {
  default: 0,
  donotround: 1,
  round: 2,
  roundsmall: 3,
};

let cached: DwmApi | null | undefined;
let loadFailed = false;

function loadDwm(): DwmApi | null {
  if (cached !== undefined) return cached;
  if (process.platform !== 'win32' || loadFailed) {
    cached = null;
    return null;
  }
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const koffi = require('koffi') as KoffiLike;
    const dwmapi = koffi.load('dwmapi.dll');
    cached = {
      // uxtheme/COM 之外的普通导出，调用约定是 stdcall
      DwmSetWindowAttribute: dwmapi.func('__stdcall', 'DwmSetWindowAttribute', 'int32', [
        'void *',
        'uint32',
        'void *',
        'uint32',
      ]) as KoffiFunc,
    };
  } catch (error) {
    loadFailed = true;
    cached = null;
    log('[dwm] 加载 dwmapi.dll 失败', String(error));
  }
  return cached;
}

function toHwnd(window: BrowserWindow): number {
  const buffer = window.getNativeWindowHandle();
  return buffer.length >= 8 ? Number(buffer.readBigUInt64LE(0)) : buffer.readUInt32LE(0);
}

/** 设置 Win11 圆角偏好。返回 false 表示当前系统/平台不支持。 */
export function applyRoundedCorners(window: BrowserWindow, preference: CornerPreference = 'round'): boolean {
  const api = loadDwm();
  if (!api || window.isDestroyed()) return false;
  try {
    const value = CORNER_VALUES[preference];
    const buffer = Buffer.alloc(4);
    buffer.writeInt32LE(value, 0);
    const hr = Number(api.DwmSetWindowAttribute(toHwnd(window), DWMWA_WINDOW_CORNER_PREFERENCE, buffer, 4));
    if (hr !== 0) {
      log('[dwm] 设置圆角偏好失败', { hr, preference });
      return false;
    }
    return true;
  } catch (error) {
    log('[dwm] 设置圆角偏好异常', String(error));
    return false;
  }
}

/** 让 DWM 知道窗口是深色（影响边框/系统绘制的材质取色）。 */
export function applyDarkFrame(window: BrowserWindow, dark: boolean): void {
  const api = loadDwm();
  if (!api || window.isDestroyed()) return;
  try {
    const buffer = Buffer.alloc(4);
    buffer.writeInt32LE(dark ? 1 : 0, 0);
    const hwnd = toHwnd(window);
    api.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, buffer, 4);
    api.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, buffer, 4);
  } catch (error) {
    log('[dwm] 设置深色边框异常', String(error));
  }
}
