import { app } from 'electron';
import { appendFileSync, existsSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * 文件日志 —— 打包后的 Windows 应用没有控制台，出问题时只能靠它诊断。
 *
 * 位置：`%APPDATA%/TJDesktopTimetable/startup.log`（设置页/托盘"打开数据目录"可见）。
 * 只做同步追加，单文件不轮转（一次启动几十行，无需复杂策略）。
 */

let logFile: string | null = null;

function resolveLogFile(): string {
  if (logFile) return logFile;
  const dir = app.getPath('userData');
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  logFile = join(dir, 'startup.log');
  return logFile;
}

function stringify(value: unknown): string {
  if (typeof value === 'string') return value;
  if (value instanceof Error) return `${value.name}: ${value.message}\n${value.stack ?? ''}`;
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

export function log(...parts: unknown[]): void {
  const line = `[${new Date().toISOString()}] ${parts.map(stringify).join(' ')}`;
  // 开发态同时打到 stdout，方便 electron-vite 终端查看
  console.log(line);
  try {
    appendFileSync(resolveLogFile(), `${line}\n`, 'utf8');
  } catch {
    /* 日志失败不能影响主流程 */
  }
}

export function describeEnvironment(): Record<string, unknown> {
  return {
    appVersion: app.getVersion(),
    electron: process.versions.electron,
    chrome: process.versions.chrome,
    node: process.versions.node,
    platform: `${process.platform}-${process.arch}`,
    packaged: app.isPackaged,
    resourcesPath: process.resourcesPath,
    userData: app.getPath('userData'),
    argv: process.argv.slice(1),
  };
}

/** 捕获未处理异常，避免应用静默退出（Windows 上用户看不到任何提示）。 */
export function installCrashHandlers(): void {
  process.on('uncaughtException', (error) => {
    log('[fatal] uncaughtException', error);
  });
  process.on('unhandledRejection', (reason) => {
    log('[fatal] unhandledRejection', reason);
  });
}

export function revealLogFile(): string {
  return resolveLogFile();
}
