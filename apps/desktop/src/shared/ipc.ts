import type { Timetable } from '@tjt/core';

/** 主进程 ↔ 渲染进程共享的类型（单一真源）。 */

export type WidgetMode = 'desktop' | 'wallpaper';
/**
 * 外观主题（三选一，移植自 WitchDrawer 的 AppTheme）：
 * `moe` 浅色 / `glass` 深色玻璃 / `crystal` 透白水晶。
 * 旧版本的 `light`/`dark`/`auto` 由渲染层 `normalizeTheme()` 迁移。
 */
export type ThemeMode = 'moe' | 'glass' | 'crystal';
export type WeekFilterMode = 'all' | 'odd' | 'even';
/**
 * 挂件窗口的系统材质（Win11 原生合成）。
 *
 * - `solid`：不透明实色底（**默认**，最稳；材质出问题时可随时退回这里）；
 * - `mica` / `mica-alt`：云母 / 云母 Alt（取壁纸色调，克制）；
 * - `acrylic`：亚克力（模糊最强，代价是必须透明窗口 → 拿不到 DWM 圆角）。
 *
 * 注意：`backgroundMaterial` **只在 `new BrowserWindow()` 时声明有效**，
 * 运行时改不了 —— 所以切换材质要重建挂件窗口（见 widget.ts 的 recreateWidgetWindow）。
 */
export type WindowMaterial = 'solid' | 'mica' | 'mica-alt' | 'acrylic';

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
  /** 窗口系统材质（Win11 原生合成）；默认纯色。 */
  material: WindowMaterial;
  /** owner 巡检间隔（毫秒，0 = 关闭）。语义已变：只查 owner 是否丢失，不再重压 z-order。 */
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
  theme: 'glass',
  material: 'solid',
  keepAtBottomIntervalMs: 5000,
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
  /** 诊断信息（请求、HTTP 状态、数据识别结论）。 */
  probes?: { label: string; value: string }[];
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
  /** 读取上次粘贴的抓取请求（便于复用；含 Cookie，仅存本机）。 */
  getTongjiRequest(): Promise<string>;
  /** 保存粘贴的抓取请求。 */
  saveTongjiRequest(requestText: string): Promise<void>;
  /** 用粘贴的浏览器请求（Copy as cURL / PowerShell）抓取个人课表。 */
  fetchTongjiRequest(requestText: string): Promise<TongjiFetchResult>;
  /** 挂件拖动 / 缩放的开始与结束（主进程跟随鼠标移动窗口）。 */
  beginDrag(): void;
  beginResize(): void;
  endPointer(): void;
  /** 同步系统窗口按钮配色（管理窗口自绘标题栏用）。 */
  setTitleBarTheme(dark: boolean): Promise<void>;
  quit(): void;
}
