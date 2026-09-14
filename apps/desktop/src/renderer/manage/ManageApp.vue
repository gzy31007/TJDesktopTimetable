<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import {
  importTimetable,
  materializeTimetable,
  termLabel,
  type Course,
  type Diagnostic,
  type ImportResult,
  type Term,
} from '@tjt/core';
import AppearancePanel from './AppearancePanel.vue';
import ImportPanel from './ImportPanel.vue';
import TimetableBoard from '../shared/TimetableBoard.vue';
import { createMockApi, getApi, isMock, previewOverrides } from '../shared/api';
import {
  DEFAULT_SETTINGS,
  type AdapterInfo,
  type AppState,
  type PickedFile,
  type TongjiFetchResult,
  type WidgetSettings,
} from '../../shared/ipc';
import '../shared/fluent.css';
import './manage.css';
import './manage-controls.css';

/**
 * 管理窗口：导入个人课表（本地 JSON / 从 1 系统抓取）→ 立即生效，另含外观设置与实时预览。
 *
 * 外观：Windows 11 Fluent —— 窗口用系统 Mica 材质（主进程设置），标题栏改为自绘，
 * 右侧留出系统窗口按钮区域（`titleBarOverlay`）。
 *
 * 个人课表就是"我已选的课"，所以没有教学班勾选环节：解析成功即写入并刷新挂件。
 */

const preview = previewOverrides();
const api = getApi() ?? createMockApi();

const state = ref<AppState>({ settings: { ...DEFAULT_SETTINGS }, timetable: null });
const adapters = ref<AdapterInfo[]>([]);
const adapterId = ref('');
const pastedText = ref('');
const files = ref<PickedFile[]>([]);
const importResult = ref<ImportResult | null>(null);
const diagnostics = ref<Diagnostic[]>([]);
const importError = ref('');
const busy = ref(false);
const toastMessage = ref('');
const requestText = ref('');
const fetchBusy = ref(false);
const probes = ref<{ label: string; value: string }[]>([]);
const prefersDark = window.matchMedia('(prefers-color-scheme: dark)');

let toastTimer: number | null = null;
let offState: (() => void) | null = null;

const settings = computed<WidgetSettings>(() => state.value.settings);
const savedCourses = computed<Course[]>(() => state.value.timetable?.courses ?? []);

const previewTerm = computed(() => importResult.value?.term ?? state.value.timetable?.term ?? null);

/** 预览：优先显示刚导入的结果，否则显示已保存的课表。 */
const previewCourses = computed<Course[]>(() => {
  const result = importResult.value;
  if (!result) return savedCourses.value;
  return result.candidates ?? result.courses;
});

/** 主题：挂件设置优先（auto → 跟随系统）；浏览器预览下允许 ?theme= 覆盖。 */
const dark = computed(() => {
  if (preview.theme) return preview.theme === 'dark';
  const mode = settings.value.theme;
  if (mode === 'auto') return prefersDark.matches;
  return mode === 'dark';
});

const previewHint = computed(() => (importResult.value ? '最近一次导入的结果' : '已应用的课表'));

/** 预览网格暴露的 board：用于卡片头展示学期/周次/过滤数量（网格内不再画状态栏）。 */
const boardRef = ref<{ board: { term: Term; currentWeek: number | null; hiddenSessions: number } } | null>(null);
const boardInfo = computed(() => boardRef.value?.board ?? null);

function showToast(message: string): void {
  toastMessage.value = message;
  if (toastTimer !== null) window.clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => {
    toastMessage.value = '';
    toastTimer = null;
  }, 3000);
}

/** 解析结果直接落盘（个人课表无需挑选教学班）。 */
async function applyResult(result: ImportResult): Promise<void> {
  const timetable = materializeTimetable(result);
  if (!timetable.courses.length) {
    importError.value = '没有解析出任何课程，请检查数据是否完整。';
    return;
  }
  state.value = await api.saveTimetable(timetable);
  const sessions = timetable.courses.reduce((n, course) => n + course.sessions.length, 0);
  showToast(`已应用 ${timetable.courses.length} 门课程（${sessions} 条上课安排）到桌面挂件`);
}

async function runImport(): Promise<void> {
  busy.value = true;
  importError.value = '';
  probes.value = [];
  try {
    const result = importTimetable({
      ...(pastedText.value.trim() ? { text: pastedText.value } : {}),
      ...(files.value.length ? { files: files.value } : {}),
      ...(adapterId.value ? { adapterId: adapterId.value } : {}),
    });
    importResult.value = result;
    diagnostics.value = result.diagnostics;
    await applyResult(result);
  } catch (error) {
    importError.value = error instanceof Error ? error.message : String(error);
    importResult.value = null;
    diagnostics.value = [];
  } finally {
    busy.value = false;
  }
}

/** 从 1 系统抓取：把用户粘贴的浏览器请求原样发一次（主进程执行）。 */
async function fetchFromTongji(request: string): Promise<void> {
  fetchBusy.value = true;
  importError.value = '';
  probes.value = [];
  try {
    requestText.value = request;
    await api.saveTongjiRequest(request);
    const fetched: TongjiFetchResult = await api.fetchTongjiRequest(request);
    probes.value = fetched.probes ?? [];
    if (!fetched.ok || !fetched.timetableText) {
      importError.value = fetched.message;
      return;
    }
    const imported = importTimetable({ text: fetched.timetableText, adapterId: 'tongji-student' });
    importResult.value = imported;
    diagnostics.value = imported.diagnostics;
    await applyResult(imported);
  } catch (error) {
    importError.value = error instanceof Error ? error.message : String(error);
  } finally {
    fetchBusy.value = false;
  }
}

async function pickFiles(): Promise<void> {
  const picked = await api.pickFiles();
  if (!picked.length) return;
  files.value = [...files.value, ...picked];
}

function removeFile(index: number): void {
  files.value = files.value.filter((_, i) => i !== index);
}

function clearInput(): void {
  pastedText.value = '';
  files.value = [];
  importResult.value = null;
  diagnostics.value = [];
  importError.value = '';
  probes.value = [];
}

async function clearTimetable(): Promise<void> {
  if (!window.confirm('确定清空已保存的课表？此操作不可撤销。')) return;
  state.value = await api.clearTimetable();
  importResult.value = null;
  showToast('已清空课表');
}

async function updateSettings(patch: Partial<WidgetSettings>): Promise<void> {
  state.value = await api.updateSettings(patch);
}

function onThemeChange(): void {
  // 触发 dark 计算属性重新求值
  state.value = { ...state.value };
}

// 自绘标题栏必须与系统窗口按钮同色：深浅主题一变就同步过去（旧版 preload 无此方法则跳过）
watch(dark, (value) => void api.setTitleBarTheme?.(value), { immediate: true });

onMounted(async () => {
  state.value = await api.getState();
  adapters.value = await api.listAdapters();
  requestText.value = await api.getTongjiRequest();
  offState = api.onStateChanged?.((next) => (state.value = next)) ?? null;
  prefersDark.addEventListener('change', onThemeChange);
});

onBeforeUnmount(() => {
  offState?.();
  prefersDark.removeEventListener('change', onThemeChange);
  if (toastTimer !== null) window.clearTimeout(toastTimer);
});
</script>

<template>
  <div class="manage fluent-root" :data-theme="dark ? 'dark' : 'light'">
    <header class="titlebar">
      <span class="mark" aria-hidden="true">课</span>
      <h1>同济桌面课表</h1>
      <span class="sub">
        导入个人课表 → 立即固定到桌面
        <template v-if="isMock()">（浏览器预览，数据存 localStorage）</template>
      </span>
      <span class="spacer" />
      <span class="f-badge f-badge--ok">已应用 {{ savedCourses.length }} 门课程</span>    </header>

    <main class="layout">
      <section class="column left f-scroll">
        <ImportPanel
          v-model:adapter-id="adapterId"
          v-model:pasted-text="pastedText"
          v-model:request-text="requestText"
          :adapters="adapters"
          :files="files"
          :diagnostics="diagnostics"
          :probes="probes"
          :error="importError"
          :busy="busy"
          :fetch-busy="fetchBusy"
          @pick="pickFiles"
          @run="runImport"
          @fetch="fetchFromTongji"
          @remove-file="removeFile"
          @clear="clearInput"
        />
      </section>

      <section class="column right">
        <section class="f-card preview">
          <header class="f-card-head">
            <h2>课表预览</h2>
            <span class="f-hint">
              <template v-if="boardInfo">
                {{ termLabel(boardInfo.term) }}
                <template v-if="boardInfo.currentWeek"> · 第 {{ boardInfo.currentWeek }} 周</template>
                <template v-if="boardInfo.hiddenSessions"> · {{ boardInfo.hiddenSessions }} 个时段被过滤</template>
              </template>
              <template v-else>{{ previewHint }}</template>
            </span>
          </header>
          <div class="preview-body f-scroll">
            <TimetableBoard
              v-if="previewTerm"
              ref="boardRef"
              :courses="previewCourses"
              :term="previewTerm"
              :week-filter="settings.weekFilter"
              :show-weekend="settings.showWeekend"
              :trim-empty-slots="settings.trimEmptySlots"
              :dark="dark"
              :today="preview.today"
              :now-minutes="preview.nowMinutes"
              :min-cell-width="96"
              :fill="false"
              empty-hint="还没有导入课表"
            />
            <p v-else class="f-hint pad">导入后可在此预览课表。</p>
          </div>
        </section>

        <AppearancePanel
          :settings="settings"
          :dirty="false"
          :saving="false"
          @update="updateSettings"
          @save="runImport"
          @reset="clearTimetable"
        />
      </section>
    </main>

    <div class="toast" :class="{ show: !!toastMessage }">{{ toastMessage }}</div>
  </div>
</template>

<style scoped>
.manage {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  /*
   * 自己画玻璃基底：浅色 = 白色微透明，深色 = 近黑微透明。
   * 不能留 transparent —— 那样背景由系统 Mica 按壁纸采样决定，浅色主题下会偏灰/偏暗，
   * 看起来像"还在用深色背景"。
   */
  background-color: var(--win-tint);
  background-image: linear-gradient(180deg, rgba(255, 255, 255, 0.35), rgba(255, 255, 255, 0) 34%);
  color: var(--text);
}

[data-theme='dark'].manage {
  background-image: linear-gradient(180deg, rgba(255, 255, 255, 0.05), rgba(255, 255, 255, 0) 30%);
}

/* ------------------------------------------------------------ 自绘标题栏 */
/* 右侧预留系统窗口按钮的宽度：Windows 上按钮区约 138 CSS px（不随 DPI 缩放），
   窄窗口用 11vw 收缩一点，宽窗口留足余量 */
.titlebar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 0 clamp(138px, 11vw, 190px) 0 14px;
  height: 48px;
  flex: 0 0 auto;
  border-bottom: 1px solid var(--divider);
  /* Mica 是半透明的：标题栏区再压一层淡底，保证文字在浅/深底色上都清晰 */
  background-color: var(--win-tint-2);
  /* 拖动窗口：交给 Chromium 原生拖动 */
  -webkit-app-region: drag;
  user-select: none;
}

.titlebar .mark {
  width: 22px;
  height: 22px;
  border-radius: var(--r-sm);
  display: grid;
  place-items: center;
  font-size: 12px;
  font-weight: 700;
  color: var(--text-on-accent);
  background: linear-gradient(145deg, var(--accent), var(--accent-strong));
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.18);
}

.titlebar h1 {
  margin: 0;
  font-size: var(--fs-body-lg);
  font-weight: 600;
  letter-spacing: 0.01em;
  color: var(--text);
  white-space: nowrap;
}

.titlebar .sub {
  font-size: var(--fs-caption);
  color: var(--text-2);
  opacity: 0.95;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.titlebar .spacer {
  flex: 1 1 auto;
}

/* ------------------------------------------------------------------ 布局 */
.layout {
  flex: 1 1 auto;
  display: flex;
  gap: 14px;
  padding: 14px 16px 18px;
  align-items: flex-start;
  min-height: 0;
  overflow: hidden;
}

.column {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  min-height: 0;
}

.column.left {
  flex: 0 1 520px;
  max-height: calc(100vh - 66px);
  overflow: auto;
  padding-right: 4px;
}

.column.right {
  flex: 1 1 520px;
  max-height: calc(100vh - 66px);
  overflow: auto;
  padding-right: 4px;
}

.preview-body {
  margin-top: 6px;
  /* 预览面板按内容滚：网格自然排布，避免内层网格被容器高度切成两半 */
  max-height: min(60vh, 620px);
  overflow: auto;
  border-radius: var(--r-md);
  /* 预览画布比卡片再实一点，色块不至于糊在卡片底上 */
  background-color: var(--layer-strong);
  border: 1px solid var(--stroke);
  box-shadow: inset 0 1px 0 0 var(--stroke-top);
  padding: 6px 8px;
}

.pad {
  padding: 18px 0;
}
</style>
