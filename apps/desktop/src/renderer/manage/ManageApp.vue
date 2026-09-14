<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import {
  importTimetable,
  materializeTimetable,
  toggleCourse as toggleCourseInPool,
  type Course,
  type Diagnostic,
  type ImportResult,
} from '@tjt/core';
import AppearancePanel from './AppearancePanel.vue';
import CoursePicker from './CoursePicker.vue';
import ImportPanel from './ImportPanel.vue';
import TimetableBoard from '../shared/TimetableBoard.vue';
import { createMockApi, getApi, isMock } from '../shared/api';
import { DEFAULT_SETTINGS, type AdapterInfo, type AppState, type PickedFile, type WidgetSettings } from '../../shared/ipc';

/** 管理窗口：导入 → 勾选（冲突拦截）→ 保存，另含外观设置与实时预览。 */
const api = getApi() ?? createMockApi();

const state = ref<AppState>({ settings: { ...DEFAULT_SETTINGS }, timetable: null });
const adapters = ref<AdapterInfo[]>([]);
const adapterId = ref('');
const pastedText = ref('');
const files = ref<PickedFile[]>([]);
const importResult = ref<ImportResult | null>(null);
const selectedIds = ref<string[]>([]);
const diagnostics = ref<Diagnostic[]>([]);
const importError = ref('');
const busy = ref(false);
const saving = ref(false);
const toastMessage = ref('');
const dirty = ref(false);
const pickerRef = ref<InstanceType<typeof CoursePicker> | null>(null);

let toastTimer: number | null = null;
let offState: (() => void) | null = null;

const settings = computed<WidgetSettings>(() => state.value.settings);
const candidates = computed<Course[]>(() => importResult.value?.candidates ?? []);
const savedCourses = computed<Course[]>(() => state.value.timetable?.courses ?? []);

/** 预览用：优先显示"正在编辑的勾选结果"，否则显示已保存课表。 */
const previewCourses = computed<Course[]>(() => {
  if (importResult.value) {
    if (importResult.value.candidates) {
      const selected = new Set(selectedIds.value);
      return importResult.value.candidates.filter((course) => selected.has(course.id));
    }
    return importResult.value.courses;
  }
  return savedCourses.value;
});

const previewTerm = computed(() => importResult.value?.term ?? state.value.timetable?.term ?? null);
const totalWeeks = computed(() => previewTerm.value?.totalWeeks ?? 16);

function showToast(message: string): void {
  toastMessage.value = message;
  if (toastTimer !== null) window.clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => {
    toastMessage.value = '';
    toastTimer = null;
  }, 2600);
}

async function runImport(): Promise<void> {
  busy.value = true;
  importError.value = '';
  try {
    const result = importTimetable({
      ...(pastedText.value.trim() ? { text: pastedText.value } : {}),
      ...(files.value.length ? { files: files.value } : {}),
      ...(adapterId.value ? { adapterId: adapterId.value } : {}),
    });
    importResult.value = result;
    diagnostics.value = result.diagnostics;
    if (result.candidates) {
      selectedIds.value = [...result.preselect];
    } else {
      selectedIds.value = result.courses.map((course) => course.id);
    }
    dirty.value = true;
    showToast(`解析完成：${result.candidates?.length ?? result.courses.length} 个候选教学班`);
  } catch (error) {
    importError.value = error instanceof Error ? error.message : String(error);
    importResult.value = null;
    selectedIds.value = [];
    diagnostics.value = [];
  } finally {
    busy.value = false;
  }
}

function onToggle(course: Course): void {
  const result = toggleCourseInPool(course, selectedIds.value, candidates.value);
  if (result.action === 'blocked') {
    pickerRef.value?.flash(course.id);
    showToast(`「${course.name}」与已选「${result.conflict?.name ?? ''}」时间冲突，未选中`);
    return;
  }
  selectedIds.value = result.selected;
  dirty.value = true;
  if (result.action === 'switched' && result.replaced) {
    showToast(`已切换「${course.name}」的教学班`);
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
  selectedIds.value = [];
  diagnostics.value = [];
  importError.value = '';
  dirty.value = false;
}

async function save(): Promise<void> {
  const result = importResult.value;
  if (!result) {
    showToast('先导入课表再保存');
    return;
  }
  saving.value = true;
  try {
    const timetable = materializeTimetable(result, selectedIds.value);
    state.value = await api.saveTimetable(timetable);
    dirty.value = false;
    showToast(`已保存 ${timetable.courses.length} 个教学班到桌面挂件`);
  } finally {
    saving.value = false;
  }
}

async function clearTimetable(): Promise<void> {
  if (!window.confirm('确定清空已保存的课表？此操作不可撤销。')) return;
  state.value = await api.clearTimetable();
  importResult.value = null;
  selectedIds.value = [];
  dirty.value = false;
  showToast('已清空课表');
}

async function updateSettings(patch: Partial<WidgetSettings>): Promise<void> {
  state.value = await api.updateSettings(patch);
}

/** 供"空数据"预览用：无。 */

onMounted(async () => {
  state.value = await api.getState();
  adapters.value = await api.listAdapters();
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
        导入 1 系统课表 → 勾选自己的教学班（自动冲突拦截）→ 保存后固定到桌面
        <template v-if="isMock()">（浏览器预览模式，数据保存在 localStorage）</template>
      </span>
      <span class="spacer" />
      <span class="saved">已保存 {{ savedCourses.length }} 个教学班</span>
    </header>

    <main class="layout">
      <section class="column left">
        <ImportPanel
          v-model:adapter-id="adapterId"
          v-model:pasted-text="pastedText"
          :adapters="adapters"
          :files="files"
          :diagnostics="diagnostics"
          :error="importError"
          :busy="busy"
          @pick="pickFiles"
          @run="runImport"
          @remove-file="removeFile"
          @clear="clearInput"
        />
        <CoursePicker
          v-if="candidates.length"
          ref="pickerRef"
          :candidates="candidates"
          :selected-ids="selectedIds"
          :total-weeks="totalWeeks"
        />
        <section v-else class="card placeholder">
          <h2>2 · 勾选我的教学班</h2>
          <p>
            导入后会在这里列出专业培养计划里的全部平行教学班，勾选你实际要上的课即可；时间冲突会被自动拦截。
          </p>
          <p v-if="savedCourses.length" class="ok">当前桌面挂件已保存 {{ savedCourses.length }} 个教学班。</p>
        </section>
      </section>

      <section class="column right">
        <section class="card preview">
          <header class="preview-head">
            <h2>课表预览</h2>
            <span class="hint">{{ importResult ? '未保存的编辑结果' : '已保存的课表' }}</span>
          </header>
          <div class="preview-body">
            <TimetableBoard
              v-if="previewTerm"
              :courses="previewCourses"
              :term="previewTerm"
              :week-filter="settings.weekFilter"
              :show-weekend="settings.showWeekend"
              :trim-empty-slots="settings.trimEmptySlots"
              empty-hint="还没有勾选任何教学班"
            />
            <p v-else class="hint pad">导入数据后可在此预览课表。</p>
          </div>
        </section>

        <AppearancePanel
          :settings="settings"
          :dirty="dirty"
          :saving="saving"
          @update="updateSettings"
          @save="save"
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
  flex: 0 1 520px;
  max-height: calc(100vh - 92px);
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
.placeholder h2 {
  font-size: 15px;
  margin: 0 0 6px;
}
.placeholder p {
  font-size: 13px;
  color: #6b7280;
  margin: 4px 0;
  line-height: 1.6;
}
.placeholder .ok {
  color: #059669;
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
