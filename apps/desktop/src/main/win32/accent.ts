import type { BrowserWindow } from 'electron';
import { log } from '../logger.js';
import { toHwnd, loadWin32 } from './api.js';

/**
 * 强调色毛玻璃（accent acrylic）——`SetWindowCompositionAttribute` 路径。
 *
 * ## 为什么需要它（2026-09-14 真机实测）
 *
 * Electron 能用的系统材质只有 `backgroundMaterial`（内部就是 DWM 的
 * `DWMWA_SYSTEMBACKDROP_TYPE`）。实测：挂件**从未被激活**（它贴在桌面层，焦点始终在别的
 * 程序上），而 DWM 对非激活窗口会把 mica / mica-alt / acrylic 一律降级成一块近黑的平色
 * —— 三种材质在同一位置测出的窗口底色完全一致（都是 RGB 20,21,22），换个位置也不变。
 * 用户看到的"纯黑背景、没有云母质感"就是这么来的。
 *
 * 参照实现 DeskBox 之所以有质感，是因为它走 WinUI 的 `MicaController` /
 * `DesktopAcrylicController` + `SystemBackdropConfiguration`：backdrop 由它自己驱动、
 * 可以强制 active 状态并指定 tint 色与亮度不透明度。Electron 没有这层控制器。
 *
 * 等价手段就是这里的 `ACCENT_ENABLE_ACRYLICBLURBEHIND`：模糊与色调由
 * `accent` 策略直接给，**与窗口激活态无关**，所以挂在桌面上的小组件也能拿到毛玻璃。
 *
 * ## 实测结论：在本项目上无效（2026-09-14）
 *
 * 调用成功（返回值非 0、无异常）但**画面无任何变化**：面板底色仍是 20,21,21，
 * 与 mica / mica-alt / acrylic 测得的数值完全一致，移动窗口也不变。
 * 原因是 Electron 的透明窗口带 `WS_EX_NOREDIRECTIONBITMAP`
 * （诊断日志里 `exStyle=0x200080`），而没有重定向表面的窗口会忽略 accent 策略。
 *
 * 因此这条路径**不对外暴露**（设置面板里没有这个选项），代码保留备查：
 * 若将来改用不带 NOREDIRECTIONBITMAP 的窗口（例如非透明 + 自绘圆角），它可能重新可用。
 */

/** `WINDOWCOMPOSITIONATTRIB`：强调色策略。 */
const WCA_ACCENT_POLICY = 19;
/** `ACCENT_STATE`：亚克力模糊（Win10 1803+）。 */
const ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

interface AccentApi {
  sizeof: (type: unknown) => number;
  policySize: number;
  call: (hwnd: number, data: unknown) => number;
}

let cachedAccent: AccentApi | null | undefined;

function loadAccentApi(): AccentApi | null {
  if (cachedAccent !== undefined) return cachedAccent;
  const api = loadWin32();
  if (!api) {
    cachedAccent = null;
    return null;
  }
  try {
    const policy = api.koffi.struct('ACCENTPOLICY', {
      AccentState: 'int',
      AccentFlags: 'int',
      GradientColor: 'uint32',
      AnimationId: 'int',
    });
    const data = api.koffi.struct('WINDOWCOMPOSITIONATTRIBDATA', {
      Attribute: 'int',
      Data: api.koffi.pointer(policy),
      SizeOfData: 'uint32',
    });
    /*
     * 必须在这里按**结构体指针**绑定：koffi 不接受把普通对象塞进 `void *` 形参
     * （实测报 `Unexpected Object value, expected void *`），声明成
     * `koffi.pointer(WINDOWCOMPOSITIONATTRIBDATA)` 后才会把 JS 对象编组成结构体再取地址。
     */
    const user32 = api.koffi.load('user32.dll');
    cachedAccent = {
      sizeof: api.koffi.sizeof,
      policySize: api.koffi.sizeof(policy),
      call: user32.func('__stdcall', 'SetWindowCompositionAttribute', 'int32', [
        'void *',
        api.koffi.pointer(data),
      ]) as (hwnd: number, value: unknown) => number,
    };
  } catch (error) {
    log('[accent] 初始化失败，accent 材质不可用', String(error));
    cachedAccent = null;
  }
  return cachedAccent;
}

/**
 * `GradientColor` 是 **ABGR** 打包的（`0xAABBGGRR`），不是 RGBA。
 */
function abgr(r: number, g: number, b: number, a: number): number {
  return (((a & 0xff) << 24) | ((b & 0xff) << 16) | ((g & 0xff) << 8) | (r & 0xff)) >>> 0;
}

export interface AccentOptions {
  /** 深色主题用深色调、浅色主题用浅色调（与 DWM 深色边框同步）。 */
  dark: boolean;
  /** 色调不透明度 0–1：越大越"实"、越小越透出桌面模糊。 */
  tintOpacity?: number;
}

/** 打开/更新强调色毛玻璃。返回 false 表示当前系统不支持。 */
export function applyAccentBlur(window: BrowserWindow, options: AccentOptions): boolean {
  const api = loadAccentApi();
  if (!api || window.isDestroyed()) return false;
  try {
    const alpha = Math.round(Math.min(1, Math.max(0, options.tintOpacity ?? 0.42)) * 255);
    const tint = options.dark ? abgr(28, 28, 30, alpha) : abgr(250, 250, 252, alpha);
    const result = api.call(toHwnd(window), {
      Attribute: WCA_ACCENT_POLICY,
      Data: {
        AccentState: ACCENT_ENABLE_ACRYLICBLURBEHIND,
        AccentFlags: 2,
        GradientColor: tint,
        AnimationId: 0,
      },
      SizeOfData: api.policySize,
    });
    if (!result) {
      log('[accent] SetWindowCompositionAttribute 返回 0（系统可能不支持）', { dark: options.dark });
      return false;
    }
    return true;
  } catch (error) {
    log('[accent] 设置强调色毛玻璃失败', String(error));
    return false;
  }
}

/** 关闭强调色毛玻璃（切回纯色/系统材质时调用）。 */
export function clearAccentBlur(window: BrowserWindow): void {
  const api = loadAccentApi();
  if (!api || window.isDestroyed()) return;
  try {
    api.call(toHwnd(window), {
      Attribute: WCA_ACCENT_POLICY,
      Data: { AccentState: 0, AccentFlags: 0, GradientColor: 0, AnimationId: 0 },
      SizeOfData: api.policySize,
    });
  } catch (error) {
    log('[accent] 关闭强调色毛玻璃失败', String(error));
  }
}
