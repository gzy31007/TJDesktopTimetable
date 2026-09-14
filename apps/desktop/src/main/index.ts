import { app, BrowserWindow } from 'electron';
import { registerIpc } from './ipc.js';
import { loadSettings } from './store.js';
import { createTray } from './tray.js';
import { createManageWindow } from './windows/manage.js';
import { createWidgetWindow } from './windows/widget.js';

/**
 * 应用入口。
 *
 * 单实例锁：第二次启动只把已有实例的管理窗口带到前台。
 * 托盘常驻：关闭所有窗口不退出（Windows 上由托盘"退出"结束进程）。
 */

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    createManageWindow();
  });

  app.whenReady().then(() => {
    app.setAppUserModelId('com.gzy31007.tjdesktoptimetable');
    registerIpc();
    createWidgetWindow();
    createTray();

    const settings = loadSettings();
    app.setLoginItemSettings({ openAtLogin: settings.launchAtLogin });

    // 带 --manage 启动时直接打开设置窗口（快捷方式可用）
    if (process.argv.includes('--manage')) createManageWindow();

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWidgetWindow();
    });
  });

  app.on('window-all-closed', () => {
    // 托盘常驻：不退出
  });
}
