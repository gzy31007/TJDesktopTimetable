<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { localTodayIso, termWeekAt, todaysSessions, type Timetable, type WeekFilter } from '@tjt/core';
import TimetableBoard from '../shared/TimetableBoard.vue';
import { createMockApi, getApi, previewOverrides } from '../shared/api';
import { isDarkTheme, themeOf } from '../shared/theme';
import { DEFAULT_SETTINGS, type AppState, type ThemeMode, type WidgetSettings } from '../../shared/ipc';
import '../shared/fluent.css';
import './widget.css';

/**
 * 桌面挂件窗口：亚克力玻璃底板 + 可拖动命令条 + 课表网格 + 右下角缩放柄。
 *
 * 视觉分层：壳（玻璃底/圆角/阴影）→ 命令条（命令与摘要）→ 内容（网格）→ 状态栏（网格组件内）。
 */

const preview = previewOverrides();
const api = getApi() ?? createMockApi();

const state = ref<AppState>({ settings: { ...DEFAULT_SETTINGS }, timetable: null });
const toast = ref('');
let toastTimer: number | null = null;
let offState: (() => void) | null = null;
let offFallback: (() => void) | null = null;

const settings = computed<WidgetSettings>(() => state.value.settings);
const timetable = computed<Timetable | null>(() => state.value.timetable);
const courses = computed(() => timetable.value?.courses ?? []);

const weekFilter = computed<WeekFilter>(() => settings.value.weekFilter);
/** 网格组件暴露的 board：用来在工具条提示里说明"有多少时段被周次过滤"。 */
const boardRef = ref<{ board: { hiddenSessions: number } } | null>(null);
const hiddenSessions = computed(() => boardRef.value?.board.hiddenSessions ?? 0);
/**
 * 三种外观主题（移植自 WitchDrawer 的 AppTheme）：moe / glass / crystal。
 * 预览参数 `?theme=` 可覆盖，便于在浏览器里逐个比对。
 */
const theme = computed<ThemeMode>(() => preview.theme ?? themeOf(settings.value));
const dark = computed(() => isDarkTheme(theme.value));

/** 预览参数只在 mock 模式生效（见 `shared/api.ts`）。 */
const today = preview.today ?? localTodayIso();
const week = computed(() => (timetable.value ? termWeekAt(timetable.value.term, today) : null));
const todayCount = computed(() =>
  timetable.value ? todaysSessions(timetable.value.courses, timetable.value.term).length : 0,
);

const termName = computed(() => timetable.value?.term.name || (timetable.value ? `学期 ${timetable.value.term.id}` : ''));

function showToast(message: string): void {
  toast.value = message;
  if (toastTimer !== null) window.clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => {
    toast.value = '';
    toastTimer = null;
  }, 2200);
}

async function applySettings(patch: Partial<WidgetSettings>): Promise<void> {
  state.value = await api.updateSettings(patch);
}

function onBarPointerDown(event: PointerEvent): void {
  if (event.button !== 0) return;
  const target = event.target as HTMLElement;
  if (target.closest('button, select, input, a')) return;
  event.preventDefault();
  api.beginDrag();
}

function onResizePointerDown(event: PointerEvent): void {
  if (event.button !== 0) return;
  event.preventDefault();
  event.stopPropagation();
  // 关键：捕获指针后，即使指针移出挂件（甚至移出屏幕边缘），pointerup 仍会送到这里
  const handle = event.currentTarget as HTMLElement;
  try {
    handle.setPointerCapture(event.pointerId);
  } catch {
    /* 某些环境不支持捕获，主进程侧还有左键状态兜底 */
  }
  api.beginResize();
}

function releaseCapture(event: PointerEvent): void {
  const handle = event.currentTarget as HTMLElement | null;
  try {
    handle?.releasePointerCapture(event.pointerId);
  } catch {
    /* ignore */
  }
}

/** 幂等：主进程侧对重复调用是安全的，多路兜底避免"拖动停不下来"。 */
function onPointerUp(): void {
  api.endPointer();
}

function cycleWeekFilter(): void {
  const order: WeekFilter[] = ['all', 'odd', 'even'];
  const next = order[(order.indexOf(weekFilter.value) + 1) % order.length] ?? 'all';
  void applySettings({ weekFilter: next });
}

const filterLabel = computed(() => ({ all: '全部周', odd: '单周', even: '双周' })[weekFilter.value]);

onMounted(async () => {
  state.value = await api.getState();
  offState = api.onStateChanged?.((next) => (state.value = next)) ?? null;
  offFallback = api.onFallback?.((reason) => showToast(reason)) ?? null;
  // 兜底：即使指针事件丢失（原生拖动会吞掉 pointerup），也能收尾
  window.addEventListener('pointerup', onPointerUp);
  window.addEventListener('blur', onPointerUp);

});

// 主题（深浅）变化时同步窗口底色与 DWM 深色边框：mica 的取色跟窗口深浅走
watch(dark, (value) => void api.setTitleBarTheme?.(value), { immediate: true });

onBeforeUnmount(() => {
  offState?.();
  offFallback?.();
  window.removeEventListener('pointerup', onPointerUp);
  window.removeEventListener('blur', onPointerUp);
});
</script>

<template>
  <div
    class="widget-shell fluent-root"
    :data-theme="theme"
    :style="{ '--shell-alpha': String(settings.opacity) }"
  >
    <div class="widget-bar" title="按住此处可拖动挂件" @pointerdown="onBarPointerDown" @pointerup="onPointerUp">
      <span class="title">{{ termName }}</span>
      <span class="meta">
        <span>{{ week ? `第 ${week} 周` : '假期' }}</span>
        <template v-if="todayCount"><span class="sep">·</span><span>今日 {{ todayCount }} 节</span></template>
      </span>
      <span class="spacer" />
      <span class="actions">
        <button
          class="f-pill"
          type="button"
          :title="`周次过滤：${filterLabel}${hiddenSessions ? `（${hiddenSessions} 个时段被过滤）` : ''}，点击切换`"
          @click="cycleWeekFilter"
        >
          {{ filterLabel }}<span v-if="hiddenSessions" class="count">{{ hiddenSessions }}</span>
        </button>
        <button
          class="f-pill"
          :class="{ 'is-on': settings.showWeekend }"
          type="button"
          :title="settings.showWeekend ? '当前显示周末，点击仅显示工作日' : '当前仅工作日，点击显示周末'"
          @click="applySettings({ showWeekend: !settings.showWeekend })"
        >
          {{ settings.showWeekend ? '含周末' : '仅工作日' }}
        </button>
        <span class="divider" />
        <button class="f-pill" type="button" title="打开设置" @click="api.openManage()">设置</button>
        <button class="f-pill" type="button" title="隐藏挂件（可从托盘恢复）" @click="api.toggleWidget(false)">
          隐藏
        </button>
      </span>
    </div>

    <div class="widget-content">
      <div v-if="!timetable || !courses.length" class="widget-empty">
        <span class="icon">▤</span>
        <p>还没有课表数据</p>
        <button class="f-btn f-btn--accent" type="button" @click="api.openManage()">导入同济课表</button>
      </div>
      <TimetableBoard
        v-else
        ref="boardRef"
        :courses="courses"
        :term="timetable.term"
        :week-filter="weekFilter"
        :show-weekend="settings.showWeekend"
        :trim-empty-slots="settings.trimEmptySlots"
        :interactive="true"
        :dark="dark"
        :today="preview.today"
        :now-minutes="preview.nowMinutes"
        empty-hint="当前周次过滤下没有课"
        @pick="api.openManage()"
      />
    </div>

    <div
      class="resize-handle"
      title="拖动可缩放挂件"
      @pointerdown="onResizePointerDown"
      @pointerup="releaseCapture($event); onPointerUp()"
      @pointercancel="releaseCapture($event); onPointerUp()"
      @lostpointercapture="onPointerUp"
    />
    <div class="toast" :class="{ show: !!toast }">{{ toast }}</div>
  </div>
</template>

<style scoped>
.widget-bar .sep {
  color: var(--text-3);
}

/* 被周次过滤掉的时段数：贴在小角标里，省掉一整条状态栏 */
.widget-bar .count {
  margin-left: 4px;
  padding: 0 5px;
  border-radius: 999px;
  background-color: var(--layer-strong);
  color: var(--text-3);
  font-size: 10px;
  font-variant-numeric: tabular-nums;
}
</style>
