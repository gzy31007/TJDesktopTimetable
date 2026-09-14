<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { importTimetable, materializeTimetable, type Course, type Diagnostic, type ImportResult } from '@tjt/core';
import AppearancePanel from './AppearancePanel.vue';
import ImportPanel from './ImportPanel.vue';
import TimetableBoard from '../shared/TimetableBoard.vue';
import { createMockApi, getApi, isMock } from '../shared/api';
import {
  DEFAULT_SETTINGS,
  type AdapterInfo,
  type AppState,
  type PickedFile,
  type TongjiFetchResult,
  type WidgetSettings,
} from '../../shared/ipc';

/**
 * 管理窗口：导入个人课表（本地 JSON / 从 1 系统抓取）→ 立即生效，另含外观设置与实时预览。
 *
 * 个人课表就是"我已选的课"，所以没有教学班勾选环节：解析成功即写入并刷新挂件。
 */

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

let toastTimer: number | null = null;
let offState: (() => void) | null = null;

const settings = computed<WidgetSettings>(() => state.value.settings);
const savedCourses = computed<Course[]>(() => state.value.timetable?.courses ?? []);

/** 预览：优先显示刚导入的结果，否则显示已保存的课表。 */
const previewCourses = computed<Course[]>(() => {
  const result = importResult.value;
  if (!result) return savedCourses.value;
  return result.candidates ?? result.courses;
});
const previewTerm = computed(() => importResult.value?.term ?? state.value.timetable?.term ?? null);

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

onMounted(async () => {
  state.value = await api.getState();
  adapters.value = await api.listAdapters();
  requestText.value = await api.getTongjiRequest();
  offState = api.onStateChanged?.((next) => (state.value = next)) ?? null;
});

onBeforeUnmount(() => {
  offState?.();
  if (toastTimer !== null) window.clearTimeout(toastTimer);
});
</script>

<template>
  <div class="manage">
    <header class="top">
      <h1>同济桌面课表 · 设置</h1>
      <span class="sub">
        导入你的个人课表（本地 JSON 或从 1 系统抓取）→ 立即固定到桌面
        <template v-if="isMock()">（浏览器预览模式，数据保存在 localStorage）</template>
      </span>
      <span class="spacer" />
      <span class="saved">已应用 {{ savedCourses.length }} 门课程</span>
    </header>

    <main class="layout">
      <section class="column left">
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
        <section class="card preview">
          <header class="preview-head">
            <h2>课表预览</h2>
            <span class="hint">{{ importResult ? '最近一次导入的结果' : '已应用的课表' }}</span>
          </header>
          <div class="preview-body">
            <TimetableBoard
              v-if="previewTerm"
              :courses="previewCourses"
              :term="previewTerm"
              :week-filter="settings.weekFilter"
              :show-weekend="settings.showWeekend"
              :trim-empty-slots="settings.trimEmptySlots"
              empty-hint="还没有导入课表"
            />
            <p v-else class="hint pad">导入后可在此预览课表。</p>
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
  background: #eef2f7;
  color: #1f2937;
  font-family: 'PingFang SC', 'Microsoft YaHei', 'Segoe UI', system-ui, sans-serif;
}
.top {
  display: flex;
  align-items: baseline;
  gap: 12px;
  padding: 12px 18px;
  background: #fff;
  border-bottom: 1px solid #d5dde8;
  flex-wrap: wrap;
}
.top h1 {
  font-size: 17px;
  margin: 0;
}
.top .sub {
  font-size: 12px;
  color: #6b7280;
}
.top .spacer {
  flex: 1 1 auto;
}
.top .saved {
  font-size: 12px;
  color: #059669;
}
.layout {
  flex: 1 1 auto;
  display: flex;
  gap: 14px;
  padding: 14px 18px 20px;
  align-items: flex-start;
  min-height: 0;
}
.column {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
}
.column.left {
  flex: 0 1 540px;
  max-height: calc(100vh - 92px);
  overflow: auto;
}
.column.right {
  flex: 1 1 520px;
}
.card {
  border: 1px solid #d5dde8;
  border-radius: 10px;
  background: #fff;
  padding: 12px 14px;
}
.preview-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
}
.preview-head h2 {
  font-size: 15px;
  margin: 0;
}
.preview-body {
  margin-top: 8px;
  overflow: auto;
  max-height: 52vh;
}
.hint {
  font-size: 12px;
  color: #6b7280;
}
.pad {
  padding: 16px 0;
}
.toast {
  position: fixed;
  left: 50%;
  bottom: 26px;
  transform: translateX(-50%);
  background: #1f2937;
  color: #fff;
  padding: 8px 16px;
  border-radius: 8px;
  font-size: 13px;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.2s;
  max-width: 80vw;
}
.toast.show {
  opacity: 1;
}
</style>
