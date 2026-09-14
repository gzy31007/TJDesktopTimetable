import type { Timetable } from '@tjt/core';

/** 主进程 ↔ 渲染进程共享的类型（单一真源）。 */

export type WidgetMode = 'desktop' | 'wallpaper';
export type ThemeMode = 'light' | 'dark' | 'auto';
export type WeekFilterMode = 'all' | 'odd' | 'even';

export interface WidgetSettings {
  /** desktop = 置底可交互；wallpaper = WorkerW 壁纸层（贴桌面图标之下）。 */
  mode: WidgetMode;
  /** 窗口位置与大小（缺省时由主进程放到右下角）。 */
  bounds?: { x: number; y: number; width: number; height: number };
  /** 所在显示器 id，用于多屏恢复。 */
  displayId?: number;
  opacity: number;
  showWeekend: boolean;
  weekFilter: WeekFilterMode;
  trimEmptySlots: boolean;
  /** 锁定后挂件不接收鼠标事件（点击穿透）。 */
  clickThrough: boolean;
  showWidget: boolean;
  launchAtLogin: boolean;
  theme: ThemeMode;
  /** 置底保险定时器（毫秒）；0 表示不启用。 */
  keepAtBottomIntervalMs: number;
}

export const DEFAULT_SETTINGS: WidgetSettings = {
  mode: 'desktop',
  opacity: 0.92,
  showWeekend: true,
  weekFilter: 'all',
  trimEmptySlots: true,
  clickThrough: false,
  showWidget: true,
  launchAtLogin: false,
  theme: 'auto',
  keepAtBottomIntervalMs: 1000,
};

export interface AdapterInfo {
  id: string;
  displayName: string;
  description: string;
  canFetch: boolean;
}

export interface AppState {
  settings: WidgetSettings;
  timetable: Timetable | null;
}

export interface PickedFile {
  name: string;
  text: string;
}

/** preload 暴露到 `window.api` 的方法集合。 */
export interface DesktopApi {
  getState(): Promise<AppState>;
  updateSettings(patch: Partial<WidgetSettings>): Promise<AppState>;
  saveTimetable(timetable: Timetable): Promise<AppState>;
  clearTimetable(): Promise<AppState>;
  listAdapters(): Promise<AdapterInfo[]>;
  pickFiles(): Promise<PickedFile[]>;
  openManage(): Promise<void>;
  toggleWidget(visible?: boolean): Promise<AppState>;
  setClickThrough(enabled: boolean): Promise<AppState>;
  /** 挂件拖动 / 缩放的开始与结束（主进程跟随鼠标移动窗口）。 */
  beginDrag(): void;
  beginResize(): void;
  endPointer(): void;
  quit(): void;
}
