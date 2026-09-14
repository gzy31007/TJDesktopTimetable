import { BrowserWindow, nativeTheme } from 'electron';
import { join } from 'node:path';
import { log } from '../logger.js';

/** 管理窗口：导入数据、调外观。Win11 上使用系统 Mica 材质 + 自绘标题栏。 */

let manageWindow: BrowserWindow | null = null;

/** 标题栏配色（自绘标题栏 + 系统窗口按钮必须一致，否则按钮会糊在深/浅底上）。 */
const TITLEBAR_COLORS = {
  light: { color: '#00000000', symbolColor: '#1a1a1a' },
  dark: { color: '#00000000', symbolColor: '#ffffff' },
} as const;

/** 标题栏高度需与渲染层 CSS 的 `.titlebar` 保持一致。 */
const TITLEBAR_HEIGHT = 48;

export function getManageWindow(): BrowserWindow | null {
  return manageWindow;
}

/** 随主题切换标题栏按钮颜色（渲染层切换深浅色时调用）。 */
export function applyTitleBarTheme(dark: boolean): void {
  const win = manageWindow;
  if (!win || win.isDestroyed()) return;
  try {
    win.setTitleBarOverlay({ ...TITLEBAR_COLORS[dark ? 'dark' : 'light'], height: TITLEBAR_HEIGHT });
  } catch (error) {
    log('[manage] 设置标题栏配色失败', String(error));
  }
}

export function createManageWindow(): BrowserWindow {
  if (manageWindow && !manageWindow.isDestroyed()) {
    log('[manage] 复用已有窗口');
    manageWindow.show();
    manageWindow.focus();
    return manageWindow;
  }
  log('[manage] 创建窗口');

  const dark = nativeTheme.shouldUseDarkColors;
  const win = new BrowserWindow({
    width: 1080,
    height: 780,
    minWidth: 880,
    minHeight: 620,
    title: '同济桌面课表 · 设置',
    // Mica 材质需要透明底色，否则会盖住系统绘制的那一层
    backgroundColor: '#00000000',
    backgroundMaterial: 'mica',
    // 自绘标题栏：保留系统窗口按钮，其余交给渲染层（拖动区由 CSS 声明）
    titleBarStyle: 'hidden',
    titleBarOverlay: { ...TITLEBAR_COLORS[dark ? 'dark' : 'light'], height: TITLEBAR_HEIGHT },
    show: false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  const devUrl = process.env['ELECTRON_RENDERER_URL'];
  if (devUrl) void win.loadURL(`${devUrl}/manage/index.html`);
  else void win.loadFile(join(__dirname, '../renderer/manage/index.html'));

  let revealed = false;
  const reveal = (reason: string): void => {
    if (revealed || win.isDestroyed()) return;
    revealed = true;
    log('[manage] 显示窗口', reason);
    win.show();
    win.focus();
  };
  win.once('ready-to-show', () => reveal('ready-to-show'));
  win.webContents.on('did-finish-load', () => reveal('did-finish-load'));
  setTimeout(() => reveal('timeout-fallback'), 3000);
  win.webContents.on('did-fail-load', (_event, code, description, url) => {
    log('[manage] 页面加载失败', { code, description, url });
  });

  win.on('closed', () => {
    manageWindow = null;
  });

  manageWindow = win;
  return win;
}
