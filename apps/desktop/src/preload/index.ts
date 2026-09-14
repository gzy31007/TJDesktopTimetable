import { contextBridge, ipcRenderer } from 'electron';
import type { Timetable } from '@tjt/core';
import type {
  AdapterInfo,
  AppState,
  DesktopApi,
  PickedFile,
  TongjiFetchResult,
  WidgetSettings,
} from '../shared/ipc.js';

/**
 * 渲染层唯一的对外通道（contextIsolation 开启，渲染层拿不到 Node）。
 */

const api: DesktopApi & {
  onStateChanged(listener: (state: AppState) => void): () => void;
  onFallback(listener: (reason: string) => void): () => void;
} = {
  getState: () => ipcRenderer.invoke('state:get') as Promise<AppState>,
  updateSettings: (patch: Partial<WidgetSettings>) =>
    ipcRenderer.invoke('settings:update', patch) as Promise<AppState>,
  saveTimetable: (timetable: Timetable) => ipcRenderer.invoke('timetable:save', timetable) as Promise<AppState>,
  clearTimetable: () => ipcRenderer.invoke('timetable:clear') as Promise<AppState>,
  listAdapters: () => ipcRenderer.invoke('adapters:list') as Promise<AdapterInfo[]>,
  pickFiles: () => ipcRenderer.invoke('files:pick') as Promise<PickedFile[]>,
  openManage: () => ipcRenderer.invoke('window:manage') as Promise<void>,
  toggleWidget: (visible?: boolean) => ipcRenderer.invoke('widget:toggle', visible) as Promise<AppState>,
  setClickThrough: (enabled: boolean) => ipcRenderer.invoke('widget:click-through', enabled) as Promise<AppState>,
  getTongjiRequest: () => ipcRenderer.invoke('tongji:request:get') as Promise<string>,
  saveTongjiRequest: (requestText: string) => ipcRenderer.invoke('tongji:request:save', requestText) as Promise<void>,
  fetchTongjiRequest: (requestText: string) => ipcRenderer.invoke('tongji:fetch', requestText) as Promise<TongjiFetchResult>,
  setTitleBarTheme: (dark: boolean) => ipcRenderer.invoke('window:titlebar-theme', dark) as Promise<void>,
  beginDrag: () => ipcRenderer.send('widget:drag-start'),
  beginResize: () => ipcRenderer.send('widget:resize-start'),
  endPointer: () => ipcRenderer.send('widget:pointer-end'),
  quit: () => ipcRenderer.send('app:quit'),

  onStateChanged: (listener) => {
    const handler = (_event: unknown, state: AppState): void => listener(state);
    ipcRenderer.on('state:changed', handler);
    return () => ipcRenderer.removeListener('state:changed', handler);
  },
  onFallback: (listener) => {
    const handler = (_event: unknown, reason: string): void => listener(reason);
    ipcRenderer.on('widget:fallback', handler);
    return () => ipcRenderer.removeListener('widget:fallback', handler);
  },
};

contextBridge.exposeInMainWorld('api', api);
