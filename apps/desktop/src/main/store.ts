import { app } from 'electron';
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Timetable } from '@tjt/core';
import { DEFAULT_SETTINGS, type AppState, type WidgetSettings } from '../shared/ipc.js';

/**
 * 极简 JSON 持久化（不引入 electron-store，避免 ESM/CJS 与打包链路的额外变量）。
 *
 * - `settings.json`：外观与窗口状态
 * - `timetable.json`：最终课表，可直接手工替换 / 备份
 *
 * 写入采用"临时文件 + rename"原子替换，避免断电/崩溃留下半个文件。
 */

const FILE_SETTINGS = 'settings.json';
const FILE_TIMETABLE = 'timetable.json';

function dataDir(): string {
  const dir = app.getPath('userData');
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  return dir;
}

function readJson<T>(file: string): T | null {
  const path = join(dataDir(), file);
  if (!existsSync(path)) return null;
  try {
    return JSON.parse(readFileSync(path, 'utf8')) as T;
  } catch (error) {
    console.error(`[store] 读取 ${file} 失败：`, error);
    return null;
  }
}

function writeJson(file: string, value: unknown): void {
  const path = join(dataDir(), file);
  const tmp = `${path}.tmp`;
  writeFileSync(tmp, JSON.stringify(value, null, 2), 'utf8');
  renameSync(tmp, path);
}

let settingsCache: WidgetSettings | null = null;

export function loadSettings(): WidgetSettings {
  if (settingsCache) return settingsCache;
  const stored = readJson<Partial<WidgetSettings>>(FILE_SETTINGS) ?? {};
  settingsCache = { ...DEFAULT_SETTINGS, ...stored, bounds: stored.bounds ?? undefined };
  return settingsCache;
}

export function saveSettings(patch: Partial<WidgetSettings>): WidgetSettings {
  const next: WidgetSettings = { ...loadSettings(), ...patch };
  settingsCache = next;
  writeJson(FILE_SETTINGS, next);
  return next;
}

export function loadTimetable(): Timetable | null {
  return readJson<Timetable>(FILE_TIMETABLE);
}

export function saveTimetable(timetable: Timetable): void {
  writeJson(FILE_TIMETABLE, timetable);
}

export function clearTimetable(): void {
  writeJson(FILE_TIMETABLE, null);
}

export function getState(): AppState {
  return { settings: loadSettings(), timetable: loadTimetable() };
}

/** 数据目录路径（设置页里展示，便于用户备份 / 排查）。 */
export function userDataDir(): string {
  return dataDir();
}
