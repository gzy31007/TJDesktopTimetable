import type { Timetable } from '@tjt/core';

/** 主进程 ↔ 渲染进程共享的类型（单一真源）。 */

export type WidgetMode = 'desktop' | 'wallpaper';
export type ThemeMode = 'light' | 'dark' | 'auto';
export type WeekFilterMode = 'all' | 'odd' | 'even';

export interface WidgetSettings {
  /** desktop = 置底可交互；wallpaper = WorkerW 壁纸层（贴桌面图标之下）。 */
  mode: WidgetMode;
  /**
   * 把窗口 Owner 设为桌面图标层（`SHELLDLL_DefView`）。
   *
   * 开启后：浮在桌面图标之上（不被图标遮挡），且 Win+D / "显示桌面" 不会把它隐藏。
   * 关掉则退化为普通顶层置底窗口（Win+D 会隐藏，由保险定时器恢复）。
   */
  desktopLayer: boolean;
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
  desktopLayer: true,
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

/** 网络抓取结果（同济 1 系统）。 */
export interface TongjiFetchResult {
  ok: boolean;
  /** 面向用户的结果说明。 */
  message: string;
  /** 课表接口响应原文（JSON 字符串），成功时提供。 */
  timetableText?: string;
  /** 校历接口响应原文（可选，用于节次时间与当前周次）。 */
  calendarText?: string;
  /** 实际命中的接口路径与探测过程（排查用）。 */
  probes?: { path: string; status: number; note?: string }[];
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
  /** 读取已保存的同济 1 系统 Cookie（为空表示未保存）。 */
  getTongjiCookie(): Promise<string>;
  /** 保存 Cookie（写入 userData/credentials.json，不入日志、不进版本库）。 */
  saveTongjiCookie(cookie: string): Promise<void>;
  /** 用 Cookie 从 1 系统抓取个人课表（+ 校历）。 */
  fetchTongji(cookie: string): Promise<TongjiFetchResult>;
  /** 挂件拖动 / 缩放的开始与结束（主进程跟随鼠标移动窗口）。 */
  beginDrag(): void;
  beginResize(): void;
  endPointer(): void;
  quit(): void;
}
