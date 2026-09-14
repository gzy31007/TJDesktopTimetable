import { BrowserWindow, screen } from 'electron';
import { join } from 'node:path';
import type { WidgetSettings } from '../../shared/ipc.js';
import { attachToDesktop, isWin32Available, type LayerHandle } from '../win32/layer.js';
import { loadSettings, saveSettings } from '../store.js';
import { log } from '../logger.js';

/**
 * 桌面挂件窗口。
 *
 * - 无边框 + 透明 + 不占任务栏；
 * - 默认置底（`HWND_BOTTOM`）且不抢焦点，可选 WorkerW 壁纸层；
 * - 拖动 / 缩放：渲染层按住后通过 IPC 让主进程跟随鼠标改 bounds（避免 `WS_EX_NOACTIVATE`
 *   与系统非客户区拖动冲突），结束后落盘位置。
 */

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
    onFallback: (reason) => {
      log('[widget] 层级回退：', reason);
      saveSettings({ mode: 'desktop' });
      broadcast('widget:fallback', reason);
    },
  });
  log('[widget] 层级模式', { requested: settings.mode, effective: layer.mode, win32: isWin32Available() });
  if (settings.clickThrough) win.setIgnoreMouseEvents(true, { forward: true });
  if (!settings.showWidget) layer.pause();
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
  const win = new BrowserWindow({
    ...bounds,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    resizable: false,
    movable: true,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    hasShadow: false,
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

  if (patch.mode !== undefined) {
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

function startPointerLoop(onTick: () => void): void {
  stopPointerLoop();
  pointerTimer = setInterval(onTick, 16);
}

function stopPointerLoop(): void {
  if (pointerTimer) {
    clearInterval(pointerTimer);
    pointerTimer = null;
  }
}

export function beginDrag(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed() || dragging) return;
  dragging = true;
  const startCursor = screen.getCursorScreenPoint();
  const [startX = 0, startY = 0] = win.getPosition();
  startPointerLoop(() => {
    if (!dragging || win.isDestroyed()) return;
    const cursor = screen.getCursorScreenPoint();
    win.setPosition(startX + (cursor.x - startCursor.x), startY + (cursor.y - startCursor.y));
  });
}

export function beginResize(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed() || resizing) return;
  resizing = true;
  const startCursor = screen.getCursorScreenPoint();
  const [startWidth = DEFAULT_WIDTH, startHeight = DEFAULT_HEIGHT] = win.getSize();
  startPointerLoop(() => {
    if (!resizing || win.isDestroyed()) return;
    const cursor = screen.getCursorScreenPoint();
    win.setSize(
      Math.max(MIN_WIDTH, startWidth + (cursor.x - startCursor.x)),
      Math.max(MIN_HEIGHT, startHeight + (cursor.y - startCursor.y)),
    );
  });
}

export function endPointer(): void {
  if (!dragging && !resizing) return;
  dragging = false;
  resizing = false;
  stopPointerLoop();
  persistBounds();
}

export function widgetFallbackMode(): void {
  const settings = loadSettings();
  saveSettings({ mode: 'desktop' });
  const win = widgetWindow;
  if (win && !win.isDestroyed()) attachLayer(win, { ...settings, mode: 'desktop' });
}

/** 重新应用当前的层级模式（托盘切换模式后调用）。 */
export function reapplyLayer(): void {
  const win = widgetWindow;
  if (!win || win.isDestroyed()) return;
  attachLayer(win, loadSettings());
}

export const widgetLimits = { MIN_WIDTH, MIN_HEIGHT };
