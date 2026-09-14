import { BrowserWindow, screen } from 'electron';
import { join } from 'node:path';
import type { WidgetSettings } from '../../shared/ipc.js';
import {
  attachToDesktop,
  ensureWidgetVisible,
  logLayerDiagnostics,
  beginNativeMove,
  isLeftButtonDown,
  isWin32Available,
  refreshDesktopLayer,
  resumeRestingStyle,
  suspendRestingStyle,
  watchDesktopLayerMessages,
  type LayerHandle,
} from '../win32/layer.js';
import { loadSettings, saveSettings } from '../store.js';
import { clampIntoWorkArea, defaultBounds, resolveSize, type Rect } from './geometry.js';
import { applyDarkFrame, applyRoundedCorners } from '../win32/dwm.js';
import { log } from '../logger.js';

/**
 * 桌面挂件窗口。
 *
 * - 无边框 + 透明 + 不占任务栏；
 * - 默认置底（`HWND_BOTTOM`）且不抢焦点，可选 WorkerW 壁纸层；
 * - 拖动 / 缩放：渲染层按住后通过 IPC 让主进程跟随鼠标改 bounds（避免 `WS_EX_NOACTIVATE`
 *   与系统非客户区拖动冲突），结束后落盘位置。
 */

/** 玻璃卡片的圆角半径（与 widget.css 的 .widget-shell 保持一致）。 */
const CORNER_RADIUS = 14;
/**
 * 窗口底色：非透明窗口忽略 alpha，必须是实色（给透明色会得到黑底）。
 * 跟随主题在深浅之间切换，浅色底特意取亮白——底色偏灰会让叠加的白色玻璃看起来一片灰。
 */
const WIDGET_BASE_COLOR = { light: '#fafafa', dark: '#202020' } as const;

/** 三种外观里只有 `glass` 是深色底（与 renderer/shared/theme.ts 的 isDarkTheme 一致）。 */
function isDarkTheme(theme: string | undefined): boolean {
  return theme === 'glass';
}

/**
 * 系统材质 → BrowserWindow 参数的映射。
 *
 * 真机结论（2026-09-14）：云母不需要透明窗口；亚克力必须透明窗口才糊得出来，
 * 代价是 `transparent: true` 之后 DWM 不再给圆角（要不要 CSS 兜圆角另说）。
 * `backgroundMaterial` 只在创建时有效，所以切换材质必须重建窗口。
 */
function resolveMaterial(material: WidgetSettings['material']): {
  backgroundMaterial: string | null;
  transparent: boolean;
} {
  switch (material) {
    case 'mica':
      return { backgroundMaterial: 'mica', transparent: false };
    case 'mica-alt':
      return { backgroundMaterial: 'tabbed', transparent: false };
    case 'acrylic':
      return { backgroundMaterial: 'acrylic', transparent: true };
    default:
      return { backgroundMaterial: null, transparent: false };
  }
}

const DEFAULT_WIDTH = 560;
const DEFAULT_HEIGHT = 440;
const MIN_WIDTH = 320;
const MIN_HEIGHT = 220;
const MARGIN = 24;

let widgetWindow: BrowserWindow | null = null;
let layer: LayerHandle | null = null;
/** 桌面层级消息订阅（窗口销毁时取消）。 */
let offMessages: (() => void) | null = null;
/** 越界夹回的去抖句柄（见 scheduleClampIntoWorkArea）。 */
let clampTimer: NodeJS.Timeout | null = null;
/** WM_SETTINGCHANGE 去抖句柄：这条消息在系统里很吵，必须合并。 */
let layerMessageTimer: NodeJS.Timeout | null = null;
let dragging = false;
let resizing = false;
let pointerTimer: NodeJS.Timeout | null = null;
export function getWidgetWindow(): BrowserWindow | null {
  return widgetWindow;
}

function resolveBounds(settings: WidgetSettings): Rect {
  const size = resolveSize(settings.bounds, { width: DEFAULT_WIDTH, height: DEFAULT_HEIGHT }, {
    width: MIN_WIDTH,
    height: MIN_HEIGHT,
  });
  const stored = settings.bounds;

  if (stored) {
    const area = screen.getDisplayNearestPoint({ x: stored.x, y: stored.y }).workArea;
    /*
     * 不管存下来的位置是否完整在屏内，都过一遍夹取（纯函数，有单测）：
     * 旧实现只判"与工作区有交集"，于是被拖到屏幕外的位置会被原样恢复 ——
     * 右下角的缩放手柄也跟着在屏幕外，用户点不到。
     */
    const clamped = clampIntoWorkArea(
      { x: Math.round(stored.x), y: Math.round(stored.y), width: size.width, height: size.height },
      area,
    );
    return clamped;
  }

  return defaultBounds(screen.getPrimaryDisplay().workArea, size, MARGIN);
}

function loadRenderer(win: BrowserWindow, page: 'widget' | 'manage'): void {
  const devUrl = process.env['ELECTRON_RENDERER_URL'];
  if (devUrl) {
    void win.loadURL(`${devUrl}/${page}/index.html`);
  } else {
    void win.loadFile(join(__dirname, `../renderer/${page}/index.html`));
  }
}

function attachLayer(win: BrowserWindow, settings: WidgetSettings): void {
  layer?.detach();
  layer = attachToDesktop(win, {
    mode: settings.mode,
    keepAtBottomIntervalMs: settings.keepAtBottomIntervalMs,
    desktopLayer: settings.desktopLayer,
    onFallback: (reason) => {
      log('[widget] 层级回退：', reason);
      broadcast('widget:fallback', reason);
    },
  });
  log('[widget] 层级模式', {
    requested: settings.mode,
    effective: layer.mode,
    desktopLayer: settings.desktopLayer,
    win32: isWin32Available(),
  });
  if (settings.clickThrough) win.setIgnoreMouseEvents(true, { forward: true });
  if (!settings.showWidget) layer.pause();
  // 重新 attach（切换层级模式 / 重新挂载）之后窗口可能还处于隐藏态：
  // 只要设置里要求显示，就把它带回屏幕上。
  else if (!win.isVisible()) win.showInactive();

}

function broadcast(channel: string, payload?: unknown): void {
  for (const win of BrowserWindow.getAllWindows()) {
    if (!win.isDestroyed()) win.webContents.send(channel, payload);
  }
}

/**
 * 桌面层级消息的去抖分发。
 *
 * `WM_SETTINGCHANGE` 在系统里非常吵（任务栏、主题、工作区都会发），
 * 而重建层级要作废宿主缓存并动 z-order，必须合并成一次。
 */
function onDesktopLayerMessage(reason: string): void {
  if (layerMessageTimer) clearTimeout(layerMessageTimer);
  layerMessageTimer = setTimeout(() => {
    layerMessageTimer = null;
    const win = widgetWindow;
    if (!win || win.isDestroyed()) return;
    log('[widget] 收到桌面层级变化消息', { reason });
    refreshDesktopLayer(win, reason);
    /*
     * 显示拓扑变化之后**位置也要重新校验**：最典型的是拔掉外接显示器 ——
     * 原来那台屏上的坐标在新拓扑里可能整块落在工作区之外，只重修层级的话挂件会停在
     * 看不见的地方（用户只能改配置文件救）。
     * 复用越界夹回（同样的纯函数规则，见 geometry.ts）。
     */
    scheduleClampIntoWorkArea();
    layer?.resume();
  }, 300);
}

export function createWidgetWindow(): BrowserWindow {
  if (widgetWindow && !widgetWindow.isDestroyed()) return widgetWindow;

  const settings = loadSettings();
  const bounds = resolveBounds(settings);
  // 初始底色必须按当前主题决定：写死深色会让浅色主题顶着深底，叠上白色玻璃就是一片灰
  const startDark = isDarkTheme(settings.theme);
  const material = resolveMaterial(settings.material);
  const win = new BrowserWindow({
    ...bounds,
    frame: false,
    /*
     * 材质与透明度的搭配：
     * - 纯色 / 云母 / 云母 Alt：非透明窗口 + `backgroundMaterial`，圆角交给 DWM；
     * - 亚克力：必须透明窗口才糊得出来，代价是拿不到 DWM 圆角。
     * 非透明窗口会忽略 `backgroundColor` 的 alpha，给透明色只会得到黑底。
     */
    ...(material.transparent
      ? { transparent: true, backgroundColor: '#00000000' }
      : {
          transparent: false,
          // 非透明窗口忽略 alpha：必须给不透明实色，否则是黑底
          backgroundColor: startDark ? WIDGET_BASE_COLOR.dark : WIDGET_BASE_COLOR.light,
        }),
    ...(material.backgroundMaterial && process.platform === 'win32'
      ? { backgroundMaterial: material.backgroundMaterial as 'mica' | 'acrylic' | 'tabbed' }
      : {}),
    /*
     * hasShadow: false 去掉窗口投影（那层"外部立体感"）。
     *
     * ── 材质的历史结论与现状 ──
     * 旧结论是"挂件一律不开 `backgroundMaterial`"：当时开启材质后，Win+D（隐藏 →
     * `ShowWindow` 恢复）会让 DWM 合成失效 —— 窗口 `IsWindowVisible` 为真、owner 与
     * z-order 全对，但屏幕上就是不出现。
     *
     * 2026-09-14 层级层重写后，挂件**不再被 Win+D 隐藏**（owner 挂在 `SHELLDLL_DefView`
     * 上，3 次 Win+D 实测 0 条 hide/minimize），那条结论的触发前提已经消失。
     * 因此材质重新做成可选设置，默认仍是 `solid`（纯色）——材质是否稳定**待真机回归**，
     * 不稳就退回默认，不影响用户。
     */
    ...(process.platform === 'win32' ? { roundedCorners: !material.transparent, hasShadow: false } : {}),
    resizable: false,
    movable: true,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    show: false,
    title: '同济桌面课表',
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  win.setMenuBarVisibility(false);

  // 圆角交给 DWM；同时按初始主题同步深色边框
  applyRoundedCorners(win, 'round');
  applyDarkFrame(win, startDark);


  let revealed = false;
  const reveal = (reason: string): void => {
    if (revealed || win.isDestroyed()) return;
    revealed = true;
    log('[widget] 显示窗口', reason);
    if (loadSettings().showWidget) {
      win.showInactive();
      layer?.resume();
    }
  };

  loadRenderer(win, 'widget');
  attachLayer(win, settings);
  offMessages = watchDesktopLayerMessages(win, onDesktopLayerMessage);

  /*
   * 启动自检：1s / 3s / 6s 各打一行"Electron 视角 vs Win32 视角"。
   *
   * 排查"Electron 说 visible=true、屏幕上看不见"这类偏差时，必须同时看两边的说法
   * —— 之前只信 Electron 的 `isVisible()`，结果漏掉了"窗口其实已经被改成了子窗口/
   * owner 没写进去"这种 Win32 侧的事实。
   */
  for (const delay of [1000, 3000, 6000]) {
    setTimeout(() => {
      const target = widgetWindow;
      if (target && !target.isDestroyed()) logLayerDiagnostics(target, `startup+${delay}ms`);
    }, delay);
  }

  // 三重保险：透明窗口在部分 Windows 配置下不会触发 ready-to-show，
  // 只靠它会导致"进程在跑但界面永不出现"。
  win.once('ready-to-show', () => reveal('ready-to-show'));
  win.webContents.on('did-finish-load', () => reveal('did-finish-load'));
  setTimeout(() => reveal('timeout-fallback'), 3000);
  win.webContents.on('did-fail-load', (_event, code, description, url) => {
    log('[widget] 页面加载失败', { code, description, url });
  });
  win.webContents.on('render-process-gone', (_event, details) => {
    log('[widget] 渲染进程退出', details);
  });

  // Win+D 相关的窗口状态事件：即时恢复（不依赖渲染层 pointerup —— 原生拖动会吞事件）
  win.on('minimize', () => {
    log('[widget] 事件 minimize → 立即恢复');
    if (!win.isDestroyed()) ensureWidgetVisible(win);
  });
  win.on('restore', () => log('[widget] 事件 restore'));
  win.on('show', () => log('[widget] 事件 show'));
  win.on('hide', () => {
    log('[widget] 事件 hide → 立即恢复');
    if (win.isDestroyed()) return;
    ensureWidgetVisible(win);
  });

  win.on('moved', persistBounds);
  win.on('resized', persistBounds);
  win.on('closed', () => {
    if (layerMessageTimer) {
      clearTimeout(layerMessageTimer);
      layerMessageTimer = null;
    }
    if (clampTimer) {
      clearTimeout(clampTimer);
      clampTimer = null;
    }
    offMessages?.();
    offMessages = null;
    layer?.detach();
    layer = null;
    widgetWindow = null;
  });

  widgetWindow = win;
  return win;
}

/**
 * 重建挂件窗口。
 *
 * `backgroundMaterial` 只在 `new BrowserWindow()` 时生效（运行时用
 * `setBackgroundMaterial()` 即使 `DwmGetWindowAttribute` 读回 accepted 也不出模糊），
 * 所以切换材质只能重建窗口：销毁旧的 → 按新设置创建 → 需要可见就显示出来。
 */
export function recreateWidgetWindow(): BrowserWindow {
  const old = widgetWindow;
  if (old && !old.isDestroyed()) {
    offMessages?.();
    offMessages = null;
    layer?.detach();
    layer = null;
    widgetWindow = null;
    old.destroy();
  }
  const win = createWidgetWindow();
  if (loadSettings().showWidget) setWidgetVisible(true);
  return win;
}

export function persistBounds(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  const bounds = win.getBounds();
  saveSettings({ bounds, displayId: screen.getDisplayMatching(bounds).id });
  scheduleClampIntoWorkArea();
}

/**
 * 把窗口夹回所在显示器的工作区。
 *
 * 为什么需要：`-webkit-app-region: drag` 走的是系统原生 move loop，**不限制越界** ——
 * 挂件可以被拖到屏幕外（实测右边缘越界 104px），而右下角的缩放手柄一旦跑到屏幕外就
 * 再也点不到，用户只能靠拖回来救。
 *
 * 为什么去抖而不是每次 `moved` 都夹：原生拖动期间 `moved` 连续触发，当场 `setBounds`
 * 会和系统 move loop 抢位置（手感变"粘"）。停手 220ms 后再夹一次，既不影响拖动，
 * 又保证松手后挂件是完整可见的。
 */
function scheduleClampIntoWorkArea(): void {
  if (clampTimer) clearTimeout(clampTimer);
  clampTimer = setTimeout(() => {
    clampTimer = null;
    clampWidgetIntoWorkArea();
  }, 220);
}

function clampWidgetIntoWorkArea(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  const bounds = win.getBounds();
  const area = screen.getDisplayMatching(bounds).workArea;
  // 规则本身在 `geometry.ts`（纯函数 + 单测）：位置只信一处实现
  const next = clampIntoWorkArea(bounds, area);
  if (next.x === bounds.x && next.y === bounds.y) return;
  log('[widget] 挂件越出工作区，已夹回', { from: `${bounds.x},${bounds.y}`, to: `${next.x},${next.y}` });
  win.setBounds(next);
  saveSettings({ bounds: next, displayId: screen.getDisplayMatching(next).id });
}

/** 应用外观 / 层级相关设置（周次过滤、透明度这类纯渲染项由渲染层处理）。 */
export function applyWidgetSettings(patch: Partial<WidgetSettings>, next: WidgetSettings): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;

  if (patch.mode !== undefined || patch.desktopLayer !== undefined) {
    attachLayer(win, next);
  }
  if (patch.clickThrough !== undefined) {
    win.setIgnoreMouseEvents(next.clickThrough, { forward: true });
  }
  if (patch.showWidget !== undefined) {
    if (next.showWidget) {
      win.showInactive();
      layer?.resume();
    } else {
      layer?.pause();
      win.hide();
    }
  }
}

export function setWidgetVisible(visible: boolean): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  if (visible) {
    win.showInactive();
    layer?.resume();
  } else {
    layer?.pause();
    win.hide();
  }
}

/**
 * 拖动 / 缩放。
 *
 * 拖动：置底模式交给 Windows 原生 move loop（跟手性 = 系统窗口拖动）；壁纸层模式是子窗口，
 * 原生拖动坐标会错乱，退回自实现。
 * 缩放：窗口是 `resizable: false` 的透明无边框窗口，系统缩放不可用，自实现 + 三重保险：
 *   渲染层 `setPointerCapture`（指针移出窗口也不丢 pointerup）、主进程每 8ms 检查
 *   `GetAsyncKeyState(VK_LBUTTON)`（左键松开即收尾）、起止时暂停/恢复置底定时器。
 */

function startPointerLoop(onTick: () => void, intervalMs = 8): void {
  stopPointerLoop();
  pointerTimer = setInterval(onTick, intervalMs);
}

function stopPointerLoop(): void {
  if (pointerTimer) {
    clearInterval(pointerTimer);
    pointerTimer = null;
  }
}

export function beginDrag(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed() || !win.isVisible() || dragging || resizing) return;
  dragging = true;
  // 关键一：拖动期间停掉 owner 巡检，否则巡检里的重挂会和拖动抢 z-order
  layer?.pause();
  // 关键二：摘掉静息样式（WS_EX_NOACTIVATE 会让系统跳过原生 move loop，拖不动）并临时浮起
  suspendRestingStyle(win);
  log('[widget] 开始拖动', { mode: layer?.mode });

  const native = layer?.mode === 'desktop' ? beginNativeMove(win) : false;
  if (!native) {
    const startCursor = screen.getCursorScreenPoint();
    const [startX = 0, startY = 0] = win.getPosition();
    startPointerLoop(() => {
      if (!dragging || win.isDestroyed() || !isLeftButtonDown()) {
        endPointer();
        return;
      }
      const cursor = screen.getCursorScreenPoint();
      win.setPosition(startX + (cursor.x - startCursor.x), startY + (cursor.y - startCursor.y));
    });
    return;
  }

  // 原生拖动：SendMessage 返回时用户已松手
  endPointer();
}

export function beginResize(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed() || !win.isVisible() || resizing || dragging) return;
  resizing = true;
  layer?.pause();
  suspendRestingStyle(win);
  log('[widget] 开始缩放');

  const startCursor = screen.getCursorScreenPoint();
  const bounds = win.getBounds();
  const [startWidth = DEFAULT_WIDTH, startHeight = DEFAULT_HEIGHT] = win.getSize();
  const area = screen.getDisplayMatching(bounds).workArea;
  /*
   * 上限 = 工作区右/下边界到窗口左/上边界的距离，但**不得小于当前尺寸**、也不超过工作区
   * 本身。加了这一层是因为：窗口若已（部分）越出右边界，`room` 会小于当前宽度，用户一按
   * 手柄窗口就先"缩一下"——越界应该由 `clampWidgetIntoWorkArea()` 夹回来解决，
   * 而不是把用户的窗口缩小。
   */
  const roomWidth = area.x + area.width - bounds.x;
  const roomHeight = area.y + area.height - bounds.y;
  const maxWidth = Math.max(MIN_WIDTH, Math.min(area.width, Math.max(startWidth, roomWidth)));
  const maxHeight = Math.max(MIN_HEIGHT, Math.min(area.height, Math.max(startHeight, roomHeight)));

  startPointerLoop(() => {
    if (!resizing || win.isDestroyed() || !isLeftButtonDown()) {
      endPointer();
      return;
    }
    const cursor = screen.getCursorScreenPoint();
    const width = Math.min(maxWidth, Math.max(MIN_WIDTH, startWidth + (cursor.x - startCursor.x)));
    const height = Math.min(maxHeight, Math.max(MIN_HEIGHT, startHeight + (cursor.y - startCursor.y)));
    win.setBounds({ width, height });
  });
}

export function endPointer(): void {
  const wasDragging = dragging || resizing;

  if (wasDragging) {
    dragging = false;
    resizing = false;
    stopPointerLoop();
    // 先按前台决定静息落点，再恢复 owner 巡检（顺序反了会被巡检插一脚）
    const win = widgetWindow;
    if (win && !win.isDestroyed()) resumeRestingStyle(win, 'drag-end');
    layer?.resume();
    persistBounds();
  }

  /*
   * 任何指针交互结束（包括"只是点了挂件一下"）都要确认窗口仍然贴在桌面层上方。
   *
   * 原因：Win32 侧在鼠标按下期间会临时摘掉 Shell owner（避免 Explorer 把挂件
   * 记成 Progman 的 last active popup），松开后恢复；这一刻是修正 z-order 最
   * 可靠的时机，否则要等 keepAlive 下一拍，期间可能出现"挂件看不见"。
   */
  const win = widgetWindow;
  if (win && !win.isDestroyed() && !wasDragging) {
    // 只点了一下（没有拖动/缩放）：同样要确认它回到正确层级。
    // 拖动/缩放的情况上面已经做过静息，这里不再重复。
    ensureWidgetVisible(win);
  }

  if (wasDragging) log('[widget] 拖动/缩放结束', widgetWindow?.getBounds());
}

export function widgetFallbackMode(): void {
  const settings = loadSettings();
  saveSettings({ mode: 'desktop' });
  const win = widgetWindow;
  if (win && !win.isDestroyed()) attachLayer(win, { ...settings, mode: 'desktop' });
}

/** 重新应用当前的层级模式（托盘切换模式后调用）。 */
/** 跟随主题切换窗口底色与 DWM 深色边框（mica 的取色也跟窗口深浅走）。 */
export function applyWidgetTheme(dark: boolean): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  win.setBackgroundColor(dark ? WIDGET_BASE_COLOR.dark : WIDGET_BASE_COLOR.light);
  applyDarkFrame(win, dark);
}

export function reapplyLayer(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  attachLayer(win, loadSettings());
}

export const widgetLimits = { MIN_WIDTH, MIN_HEIGHT };
