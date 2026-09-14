import { app, BrowserWindow, dialog } from 'electron';
import { join } from 'node:path';
import { registerIpc } from './ipc.js';
import { describeEnvironment, installCrashHandlers, log } from './logger.js';
import { loadSettings, loadTimetable } from './store.js';
import { createTray } from './tray.js';
import { createManageWindow } from './windows/manage.js';
import { createWidgetWindow } from './windows/widget.js';

/**
 * 应用入口。
 *
 * 单实例锁：第二次启动只把已有实例的管理窗口带到前台。
 * 托盘常驻：关闭所有窗口不退出（Windows 上由托盘"退出"结束进程）。
 * 启动过程写 `%APPDATA%/TJDesktopTimetable/startup.log`，打包后无控制台也能排查。
 */

// app name 默认取 package.json 的 name（`@tjt/desktop`），会把数据目录写成
// `%APPDATA%\@tjt\desktop`。这里显式固定，保证日志/设置位置可预期。
app.setName('TJDesktopTimetable');
app.setPath('userData', join(app.getPath('appData'), 'TJDesktopTimetable'));

installCrashHandlers();
log('=== startup ===', describeEnvironment());

// Windows 上让任务栏与通知归属正确；必须在窗口创建前设置
app.setAppUserModelId('com.gzy31007.tjdesktoptimetable');

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  log('[app] 已有实例在运行，退出本次启动');
  app.quit();
} else {
  app.on('second-instance', () => {
    log('[app] 第二次启动：聚焦管理窗口');
    createManageWindow();
  });

  app
    .whenReady()
    .then(() => {
      log('[app] whenReady');
      registerIpc();
      createWidgetWindow();
      createTray();
      log('[app] 窗口与托盘就绪');

      const settings = loadSettings();
      app.setLoginItemSettings({ openAtLogin: settings.launchAtLogin });

      // 首次启动（还没有课表）直接打开设置窗口：否则用户只会看到一个贴在桌面底层的
      // 空挂件（或被最大化窗口完全盖住），很容易误判为"没打开"。
      const timetable = loadTimetable();
      const firstRun = !timetable || timetable.courses.length === 0;
      if (firstRun || process.argv.includes('--manage')) {
        log('[app] 打开管理窗口', { firstRun });
        createManageWindow();
      }

      app.on('activate', () => {
        if (BrowserWindow.getAllWindows().length === 0) createWidgetWindow();
      });
    })
    .catch((error) => {
      log('[fatal] whenReady 失败', error);
      dialog.showErrorBox('同济桌面课表启动失败', error instanceof Error ? error.message : String(error));
      app.quit();
    });

  app.on('window-all-closed', () => {
    // 托盘常驻：不退出
  });

  app.on('before-quit', () => {
    log('[app] before-quit');
  });
}
