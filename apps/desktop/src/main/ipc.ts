import { app, BrowserWindow, dialog, ipcMain } from 'electron';
import { readFileSync } from 'node:fs';
import { defaultRegistry } from '@tjt/core';
import type { Timetable } from '@tjt/core';
import type { AdapterInfo, AppState, PickedFile, TongjiFetchResult, WidgetSettings } from '../shared/ipc.js';
import * as store from './store.js';
import { log } from './logger.js';
import { fetchViaPastedRequest } from './tongji.js';
import { refreshTrayMenu } from './tray.js';
import { applyTitleBarTheme, createManageWindow } from './windows/manage.js';
import {
  applyWidgetSettings,
  applyWidgetTheme,
  beginDrag,
  beginResize,
  createWidgetWindow,
  recreateWidgetWindow,
  endPointer,
  setWidgetVisible,
} from './windows/widget.js';

/** 主进程仅做窗口、文件与持久化；课表业务逻辑全部在渲染层用 `@tjt/core` 完成。 */

function broadcastState(): void {
  const state = store.getState();
  for (const win of BrowserWindow.getAllWindows()) {
    if (!win.isDestroyed()) win.webContents.send('state:changed', state);
  }
}

export function registerIpc(): void {
  ipcMain.handle('state:get', (): AppState => store.getState());

  ipcMain.handle('settings:update', (_event, patch: Partial<WidgetSettings>): AppState => {
    const next = store.saveSettings(patch ?? {});
    /*
     * 材质必须在**窗口创建时**声明（运行时 `setBackgroundMaterial()` 不出效果），
     * 所以改材质要走"重建挂件窗口"这条路，不能只 applyWidgetSettings。
     */
    if (patch?.material !== undefined) {
      recreateWidgetWindow();
    } else {
      applyWidgetSettings(patch ?? {}, next);
    }
    if (patch?.launchAtLogin !== undefined) {
      app.setLoginItemSettings({ openAtLogin: next.launchAtLogin });
    }
    refreshTrayMenu();
    broadcastState();
    return store.getState();
  });

  ipcMain.handle('timetable:save', (_event, timetable: Timetable): AppState => {
    store.saveTimetable(timetable);
    broadcastState();
    return store.getState();
  });

  ipcMain.handle('timetable:clear', (): AppState => {
    store.clearTimetable();
    broadcastState();
    return store.getState();
  });

  ipcMain.handle('adapters:list', (): AdapterInfo[] =>
    defaultRegistry.list().map((adapter) => ({
      id: adapter.id,
      displayName: adapter.displayName,
      description: adapter.description,
      canFetch: Boolean(adapter.canFetch),
    })),
  );

  ipcMain.handle('tongji:request:get', (): string => store.loadTongjiRequest());

  ipcMain.handle('tongji:request:save', (_event, requestText: string): void => {
    store.saveTongjiRequest(String(requestText ?? ''));
    // 注意：绝不把请求内容（含 Cookie）写进日志，只记长度
    log('[tongji] 抓取请求已保存', { length: String(requestText ?? '').trim().length });
  });

  ipcMain.handle('tongji:fetch', async (_event, requestText: string): Promise<TongjiFetchResult> => {
    const effective = String(requestText ?? '').trim() || store.loadTongjiRequest();
    log('[tongji] 开始抓取个人课表', { requestLength: effective.length });
    const result = await fetchViaPastedRequest(effective);
    log('[tongji] 抓取结果', { ok: result.ok, message: result.message });
    return result;
  });

  ipcMain.handle('files:pick', async (): Promise<PickedFile[]> => {
    const result = await dialog.showOpenDialog({
      title: '选择课表 / 校历 JSON',
      properties: ['openFile', 'multiSelections'],
      filters: [
        { name: 'JSON', extensions: ['json'] },
        { name: '网页', extensions: ['html', 'htm'] },
        { name: '全部文件', extensions: ['*'] },
      ],
    });
    if (result.canceled) return [];
    const files: PickedFile[] = [];
    for (const path of result.filePaths) {
      try {
        files.push({ name: path.split(/[\\/]/).pop() ?? path, text: readFileSync(path, 'utf8') });
      } catch (error) {
        console.error('[ipc] 读取文件失败：', path, error);
      }
    }
    return files;
  });

  ipcMain.handle('window:manage', (): void => {
    createManageWindow();
  });

  // 渲染层切深浅主题时同步系统窗口按钮配色（自绘标题栏必须与按钮一致）
  ipcMain.handle('window:titlebar-theme', (_event, dark: boolean): void => {
    const isDark = Boolean(dark);
    applyTitleBarTheme(isDark);
    applyWidgetTheme(isDark);
  });

  ipcMain.handle('widget:toggle', (_event, visible?: boolean): AppState => {
    const current = store.loadSettings().showWidget;
    const next = store.saveSettings({ showWidget: visible ?? !current });
    if (next.showWidget) {
      createWidgetWindow();
      setWidgetVisible(true);
    } else {
      setWidgetVisible(false);
    }
    refreshTrayMenu();
    broadcastState();
    return store.getState();
  });

  ipcMain.handle('widget:click-through', (_event, enabled: boolean): AppState => {
    const next = store.saveSettings({ clickThrough: Boolean(enabled) });
    const win = createWidgetWindow();
    win.setIgnoreMouseEvents(next.clickThrough, { forward: true });
    broadcastState();
    return store.getState();
  });

  ipcMain.on('widget:drag-start', () => beginDrag());
  ipcMain.on('widget:resize-start', () => beginResize());
  ipcMain.on('widget:pointer-end', () => endPointer());

  ipcMain.on('app:quit', () => app.quit());
}
