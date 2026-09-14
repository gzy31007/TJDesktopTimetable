<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import {
  blockRect,
  buildBoard,
  fitGeometry,
  DEFAULT_GEOMETRY,
  localMinutesOfDay,
  toMinutes,
  type BoardBlock,
  type Course,
  type Term,
  type WeekFilter,
} from '@tjt/core';
import './fluent.css';
import './board.css';

/**
 * 课表网格（桌面挂件与管理窗口共用）。
 *
 * 视觉：Win11 亚克力玻璃 —— 半透明网格、圆角卡片式色块、今日列淡染、当前节次与
 * 当前时间线高亮；同格多课横向并排（留缝隙）、非全周课细条纹虚线。
 *
 * 颜色策略：色板色相不变（`packages/core/colors.ts`），但不再整块铺满高饱和色，
 * 改为"浅染底 + 同色细描边 + 深墨字"，深浅主题各有一套 tint/ink 参数。
 */

const props = withDefaults(
  defineProps<{
    courses: Course[];
    term: Term;
    weekFilter?: WeekFilter;
    showWeekend?: boolean;
    trimEmptySlots?: boolean;
    interactive?: boolean;
    /** 覆盖"今天"（测试 / 预览用）。 */
    today?: string;
    /** 覆盖当前时间（分钟数，测试 / 预览用）。 */
    nowMinutes?: number;
    /** 深色主题下由样式表接管文字色，逐课程只算色相。 */
    dark?: boolean;
    /**
     * 列宽下限：管理窗口的预览面板可用宽度小，格子太窄时课程名只剩两个字，
     * 放宽下限让它横向滚动，可读性优先。
     */
    minCellWidth?: number;
    /**
     * 占满父容器高度（挂件用）：网格区自己滚动，状态栏恒贴底。
     * 置为 false 时按内容高度排布，由外层滚动容器负责滚动（管理窗口预览用）。
     */
    fill?: boolean;
    /** 空数据时的提示文案。 */
    emptyHint?: string;
  }>(),
  {
    weekFilter: 'all',
    showWeekend: true,
    trimEmptySlots: true,
    interactive: false,
    dark: false,
    minCellWidth: 72,
    fill: true,
    emptyHint: '暂无课表数据',
  },
);

const emit = defineEmits<{ (event: 'pick', courseId: string): void }>();

const host = ref<HTMLElement | null>(null);
const availableWidth = ref(720);
const overflow = ref(false);
let observer: ResizeObserver | null = null;
let pendingWidth = 0;
let rafId: number | null = null;

const board = computed(() =>
  buildBoard(props.courses, props.term, {
    weekFilter: props.weekFilter,
    showWeekend: props.showWeekend,
    trimEmptySlots: props.trimEmptySlots,
    ...(props.today ? { today: props.today } : {}),
  }),
);

const geometry = computed(() =>
  fitGeometry(availableWidth.value, board.value.rows.length, board.value.days.length, DEFAULT_GEOMETRY, props.minCellWidth),
);

const rects = computed(() =>
  board.value.blocks.map((block) => {
    const rect = blockRect(board.value, block, geometry.value);
    // 同格并排时再压出 4px 缝隙：core 只留了 2px，挤在一起分不清是两个块
    const gap = block.stacked ? 4 : 0;
    return {
      block,
      rect: { ...rect, left: rect.left + (block.col > 0 ? gap : 0), width: Math.max(0, rect.width - gap) },
      key: `${block.courseId}|${block.day}|${block.startSlot}|${block.col}`,
    };
  }),
);

const styleVars = computed(() => ({
  '--off': `${geometry.value.gutterWidth}px`,
  '--colw': `${geometry.value.cellWidth}px`,
  '--rowh': `${geometry.value.rowHeight}px`,
  '--header-h': `${geometry.value.headerHeight}px`,
  '--cols': String(board.value.days.length),
  '--rows': String(board.value.rows.length),
}));

/** 画布总宽：网格占满可用宽度时与容器等宽，否则按格宽撑开（触发横向滚动）。 */
const canvasWidth = computed(() => geometry.value.gutterWidth + geometry.value.cellWidth * board.value.days.length);
const canvasHeight = computed(() => geometry.value.headerHeight + geometry.value.rowHeight * board.value.rows.length + 2);

const gridHeight = computed(() => geometry.value.rowHeight * board.value.rows.length + 2);

/** 今日列高亮层位置。 */
const todayTint = computed(() => {
  const index = board.value.days.findIndex((day) => day.isToday);
  if (index < 0) return null;
  return { left: geometry.value.gutterWidth + index * geometry.value.cellWidth };
});

function slotTop(index: number): number {
  const position = board.value.rows.findIndex((row) => row.index === index);
  return (position < 0 ? 0 : position) * geometry.value.rowHeight + geometry.value.rowHeight * 0.32;
}

/** 当前时刻在网格里的纵向位置（不在任何节次内 / 不在学期内则为 null）。 */
const nowFraction = computed<number | null>(() => {
  if (!board.value.currentWeek) return null;
  const minutes = props.nowMinutes ?? localMinutesOfDay(new Date());
  const rows = board.value.rows;
  const first = rows[0];
  const last = rows[rows.length - 1];
  if (!first || !last) return null;
  const begin = toMinutes(String(first.begin ?? ''));
  const end = toMinutes(String(last.end ?? ''));
  if (begin === null || end === null || end <= begin) return null;
  if (minutes < begin || minutes > end) return null;
  return (minutes - begin) / (end - begin);
});

const nowLeft = computed(() => `${geometry.value.gutterWidth - 4}px`);
const nowTop = computed(() =>
  nowFraction.value === null ? 0 : geometry.value.headerHeight + nowFraction.value * gridHeight.value,
);

/* --------------------------------------------------------------- 色块染色 */

/** 同时认 `#rrggbb` 与 `rgb(r, g, b)`（`lift()` 会产出后者）。 */
function channels(color: string): [number, number, number] {
  const hex = color.trim();
  if (hex.startsWith('#')) {
    return [
      Number.parseInt(hex.slice(1, 3), 16),
      Number.parseInt(hex.slice(3, 5), 16),
      Number.parseInt(hex.slice(5, 7), 16),
    ];
  }
  const match = hex.match(/(\d+)\D+(\d+)\D+(\d+)/);
  if (!match) return [128, 128, 128];
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

function rgba(color: string, alpha: number): string {
  const [r, g, b] = channels(color);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

/** 深色主题下色板本身偏暗，需要提亮一档才看得清（保持 #rrggbb 以便继续混色）。 */
function lift(color: string, amount: number): string {
  const mix = (value: number) => Math.round(value + (255 - value) * amount);
  const [r, g, b] = channels(color);
  return `#${[mix(r), mix(g), mix(b)].map((v) => v.toString(16).padStart(2, '0')).join('')}`;
}

/**
 * 逐课程染色：只设 CSS 变量，具体分层的透明度交给样式表按主题决定，
 * 这样深浅主题共用同一个内联样式，也避免 `!important` 打架。
 */
function tintStyle(block: BoardBlock): Record<string, string> {
  const dark = props.dark;
  const base = dark ? lift(block.color, 0.45) : block.color;
  const style: Record<string, string> = {
    '--tint': rgba(base, dark ? 0.3 : 0.13),
    '--tint-hover': rgba(base, dark ? 0.4 : 0.2),
    '--edge': rgba(base, dark ? 0.45 : 0.26),
    '--edge-strong': rgba(base, dark ? 0.7 : 0.5),
    // 深浅主题的文字色不同：浅色下用深墨字，深色下必须用白字
    '--ink': dark ? '#ffffff' : '#17223a',
    '--ink-2': dark ? 'rgba(255, 255, 255, 0.76)' : 'rgba(15, 23, 42, 0.62)',
  };
  return style;
}

function onBlockClick(courseId: string): void {
  if (!props.interactive) return;
  emit('pick', courseId);
}

// 父层（挂件工具条 / 管理窗口卡片头）要用的派生信息，避免在网格里再画一条重复状态栏
defineExpose({ board });

/** 窄列（预览面板、小挂件）里按块宽收一档字号，避免课程名只剩一个字。 */
function blockFontSize(width: number): number {
  return Math.max(9.5, Math.min(12, width / 7));
}

/** 给定时块宽度，决定课程名能显示几个字（单行省略号 + tooltip 兜底完整信息）。 */
function blockName(name: string, width: number): string {
  const max = width < 62 ? 2 : width < 80 ? 4 : width < 100 ? 6 : 8;
  return name.length > max ? `${name.slice(0, max)}…` : name;
}

/** 网格放不下时补一层背景，让横向滚动时列头仍有衬底。 */
function refreshOverflow(): void {
  const element = host.value;
  if (!element) return;
  overflow.value = element.clientWidth + 1 < canvasWidth.value;
}

onMounted(() => {
  const element = host.value;
  if (!element) return;
  availableWidth.value = element.clientWidth;
  refreshOverflow();
  // 缩放窗口时 ResizeObserver 会连续触发；用 rAF 合并到每帧一次，避免反复重算网格
  observer = new ResizeObserver((entries) => {
    const entry = entries[0];
    if (!entry) return;
    pendingWidth = entry.contentRect.width;
    if (rafId !== null) return;
    rafId = window.requestAnimationFrame(() => {
      rafId = null;
      availableWidth.value = pendingWidth;
      refreshOverflow();
    });
  });
  observer.observe(element);
});

onBeforeUnmount(() => {
  observer?.disconnect();
  observer = null;
  if (rafId !== null) {
    window.cancelAnimationFrame(rafId);
    rafId = null;
  }
});
</script>

<template>
  <div ref="host" class="tt-root f-scroll" :class="{ 'is-overflow': overflow, 'is-fill': fill }">
    <div class="tt-body f-scroll">
      <div class="tt" :style="{ ...styleVars, width: `${canvasWidth}px`, height: `${canvasHeight}px` }">
        <!-- 星期列头 -->
        <div
          v-for="(day, index) in board.days"
          :key="`head-${day.day}`"
          class="col-head"
          :class="{ today: day.isToday, weekend: day.weekend }"
          :style="{ left: `${geometry.gutterWidth + index * geometry.cellWidth}px` }"
        >
          {{ day.label }}
        </div>

        <!-- 节次标签 -->
        <div class="slot-labels" :style="{ top: `${geometry.headerHeight}px`, height: `${gridHeight}px` }">
          <div
            v-for="row in board.rows"
            :key="`slot-${row.index}`"
            :class="{ current: row.isCurrent }"
            :style="{ top: `${slotTop(row.index)}px` }"
          >
            <b>{{ row.index }}</b>
            <span v-if="row.begin"> · {{ row.begin }}</span>
          </div>
        </div>

        <!-- 网格背景 -->
        <div class="grid-bg" :style="{ top: `${geometry.headerHeight}px`, width: `${canvasWidth - geometry.gutterWidth}px`, height: `${gridHeight}px` }">
          <template v-for="(row, rowIndex) in board.rows" :key="`row-${row.index}`">
            <div
              v-for="(day, colIndex) in board.days"
              :key="`cell-${row.index}-${day.day}`"
              class="cell"
              :class="{
                weekend: day.weekend,
                'last-col': colIndex === board.days.length - 1,
                'last-row': rowIndex === board.rows.length - 1,
              }"
            />
          </template>
        </div>

        <!-- 今日列淡染 -->
        <div
          v-if="todayTint"
          class="col-tint"
          :style="{ left: `${todayTint.left}px`, width: `${geometry.cellWidth}px`, top: `${geometry.headerHeight}px`, height: `${gridHeight}px` }"
        />

        <!-- 当前时间指示线 -->
        <div v-if="nowFraction !== null" class="time-line" :style="{ left: nowLeft, top: `${nowTop}px` }" />

        <!-- 课程色块 -->
        <div
          v-for="item in rects"
          :key="item.key"
          class="block"
          :class="{ special: item.block.special }"
          :style="{
            left: `${item.rect.left}px`,
            top: `${item.rect.top}px`,
            width: `${item.rect.width}px`,
            height: `${item.rect.height}px`,
            fontSize: `${blockFontSize(item.rect.width)}px`,
            ...tintStyle(item.block),
          }"
          :title="`${item.block.name}\n教师：${item.block.teachers.join('、') || '—'}\n教室：${item.block.room || '—'}\n周次：${item.block.weeksLabel}`"
          @click="onBlockClick(item.block.courseId)"
        >
          <div class="nm">{{ blockName(item.block.shortName, item.rect.width) }}</div>
          <div v-if="item.block.room" class="rm">{{ item.block.room }}</div>
          <div class="wk">{{ item.block.weeksLabel }}</div>
        </div>

        <div v-if="!board.blocks.length" class="empty-hint">
          <span>{{ emptyHint }}</span>
        </div>
      </div>
    </div>

  </div>
</template>

<style scoped>
.tt-root {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

/* fill 模式（挂件）：占满容器高度，网格区自己纵向滚动，状态栏恒在底部 */
.tt-root.is-fill {
  height: 100%;
  min-height: 0;
}

.tt-root.is-fill .tt-body {
  flex: 1 1 auto;
  min-height: 0;
}

.tt-body {
  position: relative;
  min-height: 0;
  overflow: auto;
}

/* 横向放不下时给列头补衬底，滚动时压住下面的色块 */
.tt-root.is-overflow .col-head {
  background-color: var(--layer-2);
  backdrop-filter: blur(8px);
}
</style>
