<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { localTodayIso, termWeekAt, todaysSessions, type Timetable, type WeekFilter } from '@tjt/core';
import TimetableBoard from '../shared/TimetableBoard.vue';
import { createMockApi, getApi } from '../shared/api';
import { DEFAULT_SETTINGS, type AppState, type WidgetSettings } from '../../shared/ipc';

/** 桌面挂件窗口：一条可拖动标题栏 + 课表网格 + 右下角缩放柄。 */

const api = getApi() ?? createMockApi();

const state = ref<AppState>({ settings: { ...DEFAULT_SETTINGS }, timetable: null });
const toast = ref('');
let toastTimer: number | null = null;
let prefersDark = window.matchMedia('(prefers-color-scheme: dark)');
let offState: (() => void) | null = null;
let offFallback: (() => void) | null = null;

const settings = computed<WidgetSettings>(() => state.value.settings);
const timetable = computed<Timetable | null>(() => state.value.timetable);
const courses = computed(() => timetable.value?.courses ?? []);

const weekFilter = computed<WeekFilter>(() => settings.value.weekFilter);
const dark = computed(() => (settings.value.theme === 'auto' ? prefersDark.matches : settings.value.theme === 'dark'));

const today = localTodayIso();
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

function onBarPointerDown(event: MouseEvent): void {
  if (event.button !== 0) return;
  const target = event.target as HTMLElement;
  if (target.closest('button, select, input, a')) return;
  api.beginDrag();
}

function onResizePointerDown(event: MouseEvent): void {
  if (event.button !== 0) return;
  event.stopPropagation();
  api.beginResize();
}

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
  prefersDark.addEventListener('change', onThemeChange);
  window.addEventListener('mouseup', onPointerUp);
});

function onThemeChange(): void {
  // 触发 dark 计算属性重新求值
  state.value = { ...state.value };
}

onBeforeUnmount(() => {
  offState?.();
  offFallback?.();
  prefersDark.removeEventListener('change', onThemeChange);
  window.removeEventListener('mouseup', onPointerUp);
});
</script>

<template>
  <div
    class="widget-shell"
    :class="dark ? 'dark' : 'light'"
    :style="{ '--shell-alpha': String(settings.opacity) }"
  >
    <div class="widget-bar" @mousedown="onBarPointerDown">
      <span class="title">{{ termName }}</span>
      <span>{{ week ? `第 ${week} 周` : '假期' }}</span>
      <span v-if="todayCount">今日 {{ todayCount }} 节</span>
      <span class="spacer" />
      <button type="button" :title="`周次过滤：${filterLabel}`" @click="cycleWeekFilter">{{ filterLabel }}</button>
      <button type="button" title="显示/隐藏周末" @click="applySettings({ showWeekend: !settings.showWeekend })">
        {{ settings.showWeekend ? '含周末' : '仅工作日' }}
      </button>
      <button type="button" title="打开设置" @click="api.openManage()">设置</button>
      <button type="button" title="隐藏挂件" @click="api.toggleWidget(false)">隐藏</button>
    </div>

    <div class="widget-content">
      <div v-if="!timetable || !courses.length" class="widget-empty">
        <p>还没有课表数据</p>
        <button type="button" @click="api.openManage()">导入同济课表</button>
      </div>
      <TimetableBoard
        v-else
        :courses="courses"
        :term="timetable.term"
        :week-filter="weekFilter"
        :show-weekend="settings.showWeekend"
        :trim-empty-slots="settings.trimEmptySlots"
        :interactive="true"
        empty-hint="当前周次过滤下没有课"
        @pick="api.openManage()"
      />
    </div>

    <div class="resize-handle" @mousedown="onResizePointerDown" />
    <div class="toast" :class="{ show: !!toast }">{{ toast }}</div>
  </div>
</template>
