import type { AppState, DesktopApi, PickedFile, WidgetSettings } from '../../shared/ipc.js';
import type { Timetable } from '@tjt/core';
import { DEFAULT_SETTINGS } from '../../shared/ipc.js';
import { mockTimetable } from './mockData.js';

/**
 * 访问 preload 暴露的 `window.api`；在纯浏览器（`pnpm dev:web`）下退化为内存 mock，
 * 这样渲染层可以在 WSL 里直接调视觉，不依赖 Electron 与 Windows。
 */

export interface RendererApi extends DesktopApi {
  onStateChanged?(listener: (state: AppState) => void): () => void;
  onFallback?(listener: (reason: string) => void): () => void;
}

declare global {
  interface Window {
    api?: RendererApi;
  }
}

export function getApi(): RendererApi | null {
  if (typeof window === 'undefined') return null;
  return window.api ?? null;
}

export const isMock = (): boolean => getApi() === null;

/**
 * `pnpm dev:web` 视觉调试用：允许用 URL 查询参数覆盖"今天/当前时间/主题"，
 * 例如 `?today=2026-09-16&now=600&theme=dark`。桌面端（有 `window.api`）下不生效，
 * 避免误改真实课表的当前周判断。
 */
export interface PreviewOverrides {
  today?: string;
  nowMinutes?: number;
  theme?: 'light' | 'dark';
}

export function previewOverrides(): PreviewOverrides {
  if (!isMock() || typeof window === 'undefined') return {};
  const params = new URLSearchParams(window.location.search);
  const overrides: PreviewOverrides = {};
  const today = params.get('today');
  if (today && /^\d{4}-\d{2}-\d{2}$/.test(today)) overrides.today = today;
  const now = Number(params.get('now'));
  if (Number.isFinite(now) && now >= 0 && now <= 24 * 60) overrides.nowMinutes = now;
  const theme = params.get('theme');
  if (theme === 'light' || theme === 'dark') overrides.theme = theme;
  return overrides;
}

/**
 * `?wall=dark|color` —— 仅浏览器预览：把 <html> 涂成壁纸色，用来模拟
 * Electron 透明窗口底下真实的桌面背景（否则预览里窗后永远是白的，看不出玻璃效果）。
 */
export function applyPreviewWallpaper(): void {
  if (!isMock() || typeof document === 'undefined') return;
  const wall = new URLSearchParams(window.location.search).get('wall');
  if (!wall) return;
  const html = document.documentElement;
  if (wall === 'dark') {
    html.style.background = '#1b2430';
    html.style.backgroundImage =
      'radial-gradient(1000px 600px at 20% 15%, #35506b 0%, transparent 60%), radial-gradient(800px 500px at 85% 80%, #4a3b57 0%, transparent 62%)';
  } else if (wall === 'color') {
    html.style.background = '#2b3a55';
    html.style.backgroundImage = 'linear-gradient(135deg, #2b3a55, #6d4b6b)';
  }
}

const STORAGE_KEY = 'tjt.mock.state';

function readMockState(): AppState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) return JSON.parse(raw) as AppState;
  } catch {
    /* ignore */
  }
  return { settings: { ...DEFAULT_SETTINGS }, timetable: null };
}

function writeMockState(state: AppState): AppState {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    /* ignore */
  }
  return state;
}

/** 浏览器 mock 实现（含一份示例课表，便于调视觉）。 */
export function createMockApi(): RendererApi {
  const listeners = new Set<(state: AppState) => void>();
  let state = readMockState();
  if (!state.timetable) state = writeMockState({ ...state, timetable: mockTimetable() });

  const update = (next: AppState): AppState => {
    state = writeMockState(next);
    for (const listener of listeners) listener(state);
    return state;
  };

  return {
    getState: async () => state,
    updateSettings: async (patch: Partial<WidgetSettings>) =>
      update({ ...state, settings: { ...state.settings, ...patch } }),
    saveTimetable: async (timetable: Timetable) => update({ ...state, timetable }),
    clearTimetable: async () => update({ ...state, timetable: null }),
    listAdapters: async () => [],
    pickFiles: async (): Promise<PickedFile[]> => [],
    openManage: async () => {
      window.location.href = '/manage/index.html';
    },
    setTitleBarTheme: async () => {},
    toggleWidget: async (visible?: boolean) =>
      update({ ...state, settings: { ...state.settings, showWidget: visible ?? !state.settings.showWidget } }),
    setClickThrough: async (enabled: boolean) =>
      update({ ...state, settings: { ...state.settings, clickThrough: enabled } }),
    getTongjiRequest: async () => '',
    saveTongjiRequest: async () => {},
    fetchTongjiRequest: async () => ({
      ok: false,
      message: '浏览器预览模式不支持网络抓取（请在桌面应用里使用）。',
    }),
    beginDrag: () => {},
    beginResize: () => {},
    endPointer: () => {},
    quit: () => {},
    onStateChanged: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    onFallback: () => () => {},
  };
}
