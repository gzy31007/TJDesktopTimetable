<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import {
  blockRect,
  buildBoard,
  fitGeometry,
  DEFAULT_GEOMETRY,
  termLabel,
  type Course,
  type Term,
  type WeekFilter,
} from '@tjt/core';
import './board.css';

/**
 * 课表网格（桌面挂件与管理窗口共用）。
 *
 * 渲染规则与 `select_preview.html` 一致：7 列 × 节次行、色块显示课程名/教室/周次、
 * 非全周课条纹虚线、同格多课横向并排、今日列与当前节次高亮。
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
    /** 空数据时的提示文案。 */
    emptyHint?: string;
  }>(),
  {
    weekFilter: 'all',
    showWeekend: true,
    trimEmptySlots: true,
    interactive: false,
    emptyHint: '暂无课表数据',
  },
);

const emit = defineEmits<{ (event: 'pick', courseId: string): void }>();

const host = ref<HTMLElement | null>(null);
const availableWidth = ref(720);
let observer: ResizeObserver | null = null;

const board = computed(() =>
  buildBoard(props.courses, props.term, {
    weekFilter: props.weekFilter,
    showWeekend: props.showWeekend,
    trimEmptySlots: props.trimEmptySlots,
    ...(props.today ? { today: props.today } : {}),
  }),
);

const geometry = computed(() =>
  fitGeometry(availableWidth.value, board.value.rows.length, board.value.days.length, DEFAULT_GEOMETRY, 72),
);

const rects = computed(() =>
  board.value.blocks.map((block) => ({
    block,
    rect: blockRect(board.value, block, geometry.value),
    key: `${block.courseId}|${block.day}|${block.startSlot}|${block.col}`,
  })),
);

const styleVars = computed(() => ({
  '--off': `${geometry.value.gutterWidth}px`,
  '--colw': `${geometry.value.cellWidth}px`,
  '--rowh': `${geometry.value.rowHeight}px`,
  '--header-h': `${geometry.value.headerHeight}px`,
  '--cols': String(board.value.days.length),
  '--rows': String(board.value.rows.length),
}));

const canvasHeight = computed(() => geometry.value.headerHeight + geometry.value.rowHeight * board.value.rows.length + 2);

function slotTop(index: number): number {
  const position = board.value.rows.findIndex((row) => row.index === index);
  return (position < 0 ? 0 : position) * geometry.value.rowHeight + geometry.value.rowHeight * 0.32;
}

function onBlockClick(courseId: string): void {
  if (!props.interactive) return;
  emit('pick', courseId);
}

onMounted(() => {
  const element = host.value;
  if (!element) return;
  availableWidth.value = element.clientWidth;
  observer = new ResizeObserver((entries) => {
    const entry = entries[0];
    if (entry) availableWidth.value = entry.contentRect.width;
  });
  observer.observe(element);
});

onBeforeUnmount(() => {
  observer?.disconnect();
  observer = null;
});
</script>

<template>
  <div ref="host" class="tt-root">
    <div class="tt" :style="{ ...styleVars, height: `${canvasHeight}px` }">
      <!-- 星期列头 -->
      <div
        v-for="(day, index) in board.days"
        :key="`head-${day.day}`"
        class="col-head"
        :class="{ today: day.isToday }"
        :style="{ left: `${geometry.gutterWidth + index * geometry.cellWidth}px` }"
      >
        {{ day.label }}
      </div>

      <!-- 节次标签 -->
      <div class="slot-labels" :style="{ top: `${geometry.headerHeight}px` }">
        <div
          v-for="row in board.rows"
          :key="`slot-${row.index}`"
          :class="{ current: row.isCurrent }"
          :style="{ top: `${slotTop(row.index)}px` }"
        >
          {{ row.label }}<template v-if="row.begin"><br />{{ row.begin }}</template>
        </div>
      </div>

      <!-- 网格背景 -->
      <div class="grid-bg" :style="{ top: `${geometry.headerHeight}px` }">
        <template v-for="row in board.rows" :key="`row-${row.index}`">
          <div
            v-for="day in board.days"
            :key="`cell-${row.index}-${day.day}`"
            class="cell"
            :class="{ weekend: day.weekend, today: day.isToday }"
          />
        </template>
      </div>

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
          background: item.block.special ? item.block.color : item.block.fill,
          color: item.block.textColor,
        }"
        :title="`${item.block.name}\n教师：${item.block.teachers.join('、') || '—'}\n教室：${item.block.room || '—'}\n周次：${item.block.weeksLabel}`"
        @click="onBlockClick(item.block.courseId)"
      >
        <div class="nm">{{ item.block.shortName }}</div>
        <div v-if="item.block.room" class="rm">{{ item.block.room }}</div>
        <div class="wk">{{ item.block.weeksLabel }}</div>
      </div>

      <div v-if="!board.blocks.length" class="empty-hint">{{ emptyHint }}</div>
    </div>

    <div class="tt-meta">
      <span>{{ termLabel(board.term) }}</span>
      <span v-if="board.currentWeek">第 {{ board.currentWeek }} 周</span>
      <span v-else class="muted">假期 / 未在学期内</span>
      <span v-if="board.hiddenSessions" class="muted">（{{ board.hiddenSessions }} 个时段被周次过滤）</span>
    </div>
  </div>
</template>

<style scoped>
.tt-meta {
  display: flex;
  gap: 10px;
  align-items: baseline;
  font-size: 12px;
  color: var(--muted, #6b7280);
  padding: 6px 2px 2px;
}
.tt-meta .muted {
  opacity: 0.75;
}
</style>
