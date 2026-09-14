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
    toggleWidget: async (visible?: boolean) =>
      update({ ...state, settings: { ...state.settings, showWidget: visible ?? !state.settings.showWidget } }),
    setClickThrough: async (enabled: boolean) =>
      update({ ...state, settings: { ...state.settings, clickThrough: enabled } }),
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
