/**
 * 课程配色 —— 按课程名稳定取色。
 *
 * 与 `select_preview.html` 的差异（有意改进）：基准版按出现顺序分配颜色，
 * 同一门课在不同导入顺序 / 不同会话下会换色；这里改为对课程名做稳定哈希，
 * 只要课程名不变，颜色永远一致。色板沿用基准版的 12 色。
 */

export const COURSE_PALETTE: readonly string[] = [
  '#2563eb',
  '#7c3aed',
  '#be185d',
  '#b45309',
  '#15803d',
  '#0e7490',
  '#a16207',
  '#c2410c',
  '#4338ca',
  '#1d4ed8',
  '#9d174d',
  '#166534',
];

/** FNV-1a 32 位哈希。 */
export function hashString(input: string): number {
  let hash = 0x811c9dc5;
  for (let i = 0; i < input.length; i += 1) {
    hash ^= input.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return hash >>> 0;
}

export function colorForCourse(name: string, palette: readonly string[] = COURSE_PALETTE): string {
  const fallback = palette[0] ?? '#2563eb';
  if (palette.length === 0) return fallback;
  return palette[hashString(name) % palette.length] ?? fallback;
}

/** `#rrggbb` + 透明度 → `#rrggbbaa`（基准版用 `color + 'cc'` 的半透明色块）。 */
export function withAlpha(hex: string, alpha: number): string {
  const clamped = Math.max(0, Math.min(1, alpha));
  const byte = Math.round(clamped * 255)
    .toString(16)
    .padStart(2, '0');
  return `${hex.slice(0, 7)}${byte}`;
}

/** 返回黑或白，保证在给定背景色上可读。 */
export function readableTextColor(hex: string): '#ffffff' | '#111827' {
  const r = Number.parseInt(hex.slice(1, 3), 16) / 255;
  const g = Number.parseInt(hex.slice(3, 5), 16) / 255;
  const b = Number.parseInt(hex.slice(5, 7), 16) / 255;
  const lin = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  const luminance = 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
  return luminance > 0.45 ? '#111827' : '#ffffff';
}
