import { BrowserWindow, screen } from 'electron';
import { join } from 'node:path';
import type { WidgetSettings } from '../../shared/ipc.js';
import {
  attachToDesktop,
  ensureWidgetVisible,
  logZOrder,
  beginNativeMove,
  isLeftButtonDown,
  isWin32Available,
  type LayerHandle,
} from '../win32/layer.js';
import { loadSettings, saveSettings } from '../store.js';
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

const DEFAULT_WIDTH = 560;
const DEFAULT_HEIGHT = 440;
const MIN_WIDTH = 320;
const MIN_HEIGHT = 220;
const MARGIN = 24;

let widgetWindow: BrowserWindow | null = null;
let layer: LayerHandle | null = null;
let dragging = false;
let resizing = false;
let pointerTimer: NodeJS.Timeout | null = null;
/** 桌面层挂载失败（Explorer 未就绪）时只重试一次，避免无限循环。 */
let layerRetried = false;

export function getWidgetWindow(): BrowserWindow | null {
  return widgetWindow;
}

function resolveBounds(settings: WidgetSettings): { x: number; y: number; width: number; height: number } {
  const stored = settings.bounds;
  const width = stored?.width && stored.width >= MIN_WIDTH ? Math.round(stored.width) : DEFAULT_WIDTH;
  const height = stored?.height && stored.height >= MIN_HEIGHT ? Math.round(stored.height) : DEFAULT_HEIGHT;

  if (stored) {
    const area = screen.getDisplayNearestPoint({ x: stored.x, y: stored.y }).workArea;
    const onScreen =
      stored.x + width > area.x &&
      stored.x < area.x + area.width &&
      stored.y + height > area.y &&
      stored.y < area.y + area.height;
    if (onScreen) return { x: Math.round(stored.x), y: Math.round(stored.y), width, height };
  }

  const area = screen.getPrimaryDisplay().workArea;
  return {
    x: area.x + area.width - width - MARGIN,
    y: area.y + area.height - height - MARGIN,
    width,
    height,
  };
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

  // Explorer 可能比我们启动得晚（DefView 还不存在）→ 稍后重试一次
  if (settings.mode === 'desktop' && settings.desktopLayer && !layerRetried) {
    layerRetried = true;
    setTimeout(() => {
      const target = widgetWindow;
      if (target && !target.isDestroyed()) reapplyLayer();
    }, 2000);
  }
}

function broadcast(channel: string, payload?: unknown): void {
  for (const win of BrowserWindow.getAllWindows()) {
    if (!win.isDestroyed()) win.webContents.send(channel, payload);
  }
}

export function createWidgetWindow(): BrowserWindow {
  if (widgetWindow && !widgetWindow.isDestroyed()) return widgetWindow;

  const settings = loadSettings();
  const bounds = resolveBounds(settings);
  // 初始底色必须按当前主题决定：写死深色会让浅色主题顶着深底，叠上白色玻璃就是一片灰
  const startDark = isDarkTheme(settings.theme);
  const win = new BrowserWindow({
    ...bounds,
    frame: false,
    /*
     * 不透明窗口：Win11 的 Acrylic 与圆角都由 DWM 绘制，透明窗口拿不到圆角
     * （`transparent: true` 时 DWM 会跳过圆角与阴影），所以这里交给 DWM 全权处理；
     * 渲染层背景保持透明，材质与圆角自然对齐。
     */
    transparent: false,
    // 非透明窗口忽略 alpha：必须给不透明实色，否则是黑底
    backgroundColor: startDark ? WIDGET_BASE_COLOR.dark : WIDGET_BASE_COLOR.light,
    /*
     * 材质与底色都对齐设置窗口：mica + 近不透明的渲染层底色（见 shared/fluent.css 的 --glass-shell）。
     * acrylic 曾因"Win+D 隐藏→恢复"后 DWM 合成失效（窗口可见但不显示）而弃用。
     * hasShadow: false 去掉窗口投影（那层"外部立体感"）。
     */
    /*
     * 对齐设置窗口的组合：material + 近不透明渲染层底。
     * 之前 mica 会让"选中变灰"，根因是渲染层底只有 72~78%、材质透出来；
     * 设置窗口的 `.manage` 用 ~90% 底，所以看不出焦点跳变。这里照抄。
     */
    ...(process.platform === 'win32'
      ? { backgroundMaterial: 'mica' as const, roundedCorners: true, hasShadow: false }
      : {}),
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
    if (!win.isDestroyed()) {
      ensureWidgetVisible(win);
      nudgeRepaint();
    }
  });
  win.on('restore', () => log('[widget] 事件 restore'));
  win.on('show', () => log('[widget] 事件 show'));
  win.on('hide', () => {
    log('[widget] 事件 hide → 立即恢复');
    if (win.isDestroyed()) return;
    layer?.refresh();
    ensureWidgetVisible(win);
    nudgeRepaint();
    // Win+D 之后 DWM 合成可能滞后，补两拍抖动
    setTimeout(nudgeRepaint, 250);
    setTimeout(nudgeRepaint, 800);
  });

  win.on('moved', persistBounds);
  win.on('resized', persistBounds);
  win.on('closed', () => {
    layer?.detach();
    layer = null;
    widgetWindow = null;
  });

  widgetWindow = win;
  return win;
}

export function persistBounds(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  const bounds = win.getBounds();
  saveSettings({ bounds, displayId: screen.getDisplayMatching(bounds).id });
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
  // 关键：拖动期间不要让置底定时器反复 SetWindowPos，否则拖到一半会被压回底部
  layer?.pause();
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
  log('[widget] 开始缩放');

  const startCursor = screen.getCursorScreenPoint();
  const bounds = win.getBounds();
  const [startWidth = DEFAULT_WIDTH, startHeight = DEFAULT_HEIGHT] = win.getSize();
  const area = screen.getDisplayMatching(bounds).workArea;
  // 不允许拖出所在显示器的工作区
  const maxWidth = Math.max(MIN_WIDTH, area.x + area.width - bounds.x);
  const maxHeight = Math.max(MIN_HEIGHT, area.y + area.height - bounds.y);

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

/**
 * 强制 DWM 重新合成窗口。
 *
 * 背景：Win+D 会隐藏挂件，恢复后 `IsWindowVisible` 为真、z-order 也正确，
 * 但窗口在屏幕上不出现 —— 说明 DWM 的合成（Acrylic 材质那一层）失效了。
 * 这里用一次 1px 的 bounds 抖动 + 重绘请求把它逼回来。
 */
function nudgeRepaint(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  try {
    win.webContents.invalidate();
    const bounds = win.getBounds();
    win.setBounds({ ...bounds, width: bounds.width + 1 });
    setTimeout(() => {
      if (!win.isDestroyed()) win.setBounds(bounds);
    }, 40);
    log('[widget] 已请求强制重绘');
  } catch (error) {
    log('[widget] 强制重绘失败', String(error));
  }
}

export function endPointer(): void {
  const wasDragging = dragging || resizing;

  if (wasDragging) {
    dragging = false;
    resizing = false;
    stopPointerLoop();
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
  if (win && !win.isDestroyed()) {
    ensureWidgetVisible(win);
    nudgeRepaint();
    // 诊断：如果仍然看不见，这一行会打出"谁压在挂件上面"
    logZOrder(win);
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
