import { BrowserWindow } from 'electron';
import { join } from 'node:path';

/** 管理窗口：导入数据、勾选教学班、调外观。普通窗口（可缩放、有边框）。 */

let manageWindow: BrowserWindow | null = null;

export function getManageWindow(): BrowserWindow | null {
  return manageWindow;
}

export function createManageWindow(): BrowserWindow {
  if (manageWindow && !manageWindow.isDestroyed()) {
    manageWindow.show();
    manageWindow.focus();
    return manageWindow;
  }

  const win = new BrowserWindow({
    width: 1040,
    height: 760,
    minWidth: 860,
    minHeight: 600,
    title: '同济桌面课表 · 设置',
    backgroundColor: '#eef2f7',
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

  win.once('ready-to-show', () => win.show());
  win.on('closed', () => {
    manageWindow = null;
  });

  manageWindow = win;
  return win;
}
