/**
 * 挂件窗口的几何规则（纯函数，零依赖，可单测）。
 *
 * 单独成文件的原因：这几条规则都是**真机上踩出来的**，却不是显而易见的那种代码 ——
 * 一旦混在 `widget.ts` 里（要 electron 的 `screen` 才能调用），单测就写不了，下次重构
 * 极易被悄悄改回去。抽出来之后，`widget.ts` 只负责把 electron 的 `workArea` 喂进来。
 */

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * 把窗口夹进工作区，保证**完整可见**。
 *
 * 为什么必须做：窗口拖动走的是 `-webkit-app-region: drag` → 系统原生 move loop，
 * **不限制越界**。实测把挂件拖到屏幕右边缘外 104px 后，右下角的缩放手柄也跟着跑到
 * 屏幕外，用户再也点不到（只能靠拖回来救）。
 *
 * 边界处理：
 * - 窗口比工作区还大时，贴工作区的左上角（而不是把 x 夹成负数，那会让标题栏跑到屏幕外）；
 * - 只调位置，不改尺寸（尺寸由 `resolveSize`/缩放上限负责）。
 */
export function clampIntoWorkArea(bounds: Rect, area: Rect): Rect {
  const maxX = area.x + Math.max(0, area.width - bounds.width);
  const maxY = area.y + Math.max(0, area.height - bounds.height);
  const x = Math.min(Math.max(bounds.x, area.x), maxX);
  const y = Math.min(Math.max(bounds.y, area.y), maxY);
  if (x === bounds.x && y === bounds.y) return bounds;
  return { ...bounds, x, y };
}

/**
 * 窗口是否完整落在工作区内。
 *
 * 注意与"是否与工作区相交"的区别：旧实现用的是相交判断，于是"手柄在屏幕外"的位置会被
 * 一路恢复到下次启动；对桌面挂件来说，"完整可见"才是可用的前提。
 */
export function isFullyInside(bounds: Rect, area: Rect): boolean {
  return (
    bounds.x >= area.x &&
    bounds.y >= area.y &&
    bounds.x + bounds.width <= area.x + area.width &&
    bounds.y + bounds.height <= area.y + area.height
  );
}

/**
 * 首次启动（没有存过位置）时的落点：主显示器工作区的右下角，留出边距。
 *
 * 工作区比窗口还小时贴左上角，避免算出负坐标。
 */
export function defaultBounds(area: Rect, size: { width: number; height: number }, margin: number): Rect {
  const x = area.x + Math.max(0, area.width - size.width - margin);
  const y = area.y + Math.max(0, area.height - size.height - margin);
  return { x, y, width: size.width, height: size.height };
}

/**
 * 复位存下来的尺寸：小于下限就退回默认尺寸。
 *
 * 为什么不信任存值：窗口可以被缩到很小，也可能因为显示器变化导致旧尺寸不合理；
 * 下限保证挂件不会小到连一节时间轴都显示不出来。
 */
export function resolveSize(
  stored: { width?: number; height?: number } | undefined,
  fallback: { width: number; height: number },
  min: { width: number; height: number },
): { width: number; height: number } {
  const width = stored?.width && stored.width >= min.width ? Math.round(stored.width) : fallback.width;
  const height = stored?.height && stored.height >= min.height ? Math.round(stored.height) : fallback.height;
  return { width, height };
}
