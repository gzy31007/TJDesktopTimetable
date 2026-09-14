import { app, BrowserWindow, dialog, ipcMain } from 'electron';
import { readFileSync } from 'node:fs';
import { defaultRegistry } from '@tjt/core';
import type { Timetable } from '@tjt/core';
import type { AdapterInfo, AppState, PickedFile, WidgetSettings } from '../shared/ipc.js';
import * as store from './store.js';
import { refreshTrayMenu } from './tray.js';
import { createManageWindow } from './windows/manage.js';
import {
  applyWidgetSettings,
  beginDrag,
  beginResize,
  createWidgetWindow,
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
    applyWidgetSettings(patch ?? {}, next);
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
