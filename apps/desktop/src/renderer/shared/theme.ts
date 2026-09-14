import type { ThemeMode, WidgetSettings } from '../../shared/ipc';

/**
 * 三种外观主题（移植自 WitchDrawer 的 AppTheme）。
 *
 * - `moe`：浅色，苹果风 #0071E3（对应上游 AppTheme.Moe）
 * - `glass`：深色玻璃（对应 AppTheme.Glass）
 * - `crystal`：透白水晶（对应 AppTheme.Crystal）
 */

export const THEME_MODES: readonly ThemeMode[] = ['moe', 'glass', 'crystal'];

export const THEME_LABELS: Record<ThemeMode, string> = {
  moe: '现代（浅色）',
  glass: '玻璃（深色）',
  crystal: '水晶（透白）',
};

/** 主题是否为深色底（决定 `-dark` 类文本色与 DWM 深色边框）。 */
export function isDarkTheme(theme: ThemeMode): boolean {
  return theme === 'glass';
}

/** 旧设置（auto / light / dark）迁移到三选一：auto→moe、light→moe、dark→glass。 */
export function normalizeTheme(value: unknown): ThemeMode {
  if (value === 'glass' || value === 'crystal' || value === 'moe') return value;
  if (value === 'dark') return 'glass';
  return 'moe';
}

/** 取设置里的主题（已归一化）。 */
export function themeOf(settings: WidgetSettings): ThemeMode {
  return normalizeTheme(settings.theme);
}
