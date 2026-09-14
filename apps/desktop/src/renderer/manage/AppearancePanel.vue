<script setup lang="ts">
import type { WidgetSettings } from '../../shared/ipc';

/** 外观与行为设置（写回主进程 settings.json）。 */

const props = defineProps<{ settings: WidgetSettings; dirty: boolean; saving: boolean }>();

const emit = defineEmits<{
  (event: 'update', patch: Partial<WidgetSettings>): void;
  (event: 'save'): void;
  (event: 'reset'): void;
}>();
</script>

<template>
  <section class="card">
    <header class="card-head">
      <h2>3 · 显示与行为</h2>
      <button type="button" class="primary" :disabled="!dirty || saving" @click="emit('save')">
        {{ saving ? '保存中…' : '保存课表' }}
      </button>
    </header>

    <div class="grid">
      <label class="field">
        <span>层级模式</span>
        <select
          :value="settings.mode"
          @change="emit('update', { mode: ($event.target as HTMLSelectElement).value as WidgetSettings['mode'] })"
        >
          <option value="desktop">置底可交互（推荐）</option>
          <option value="wallpaper">壁纸层（贴桌面图标之下）</option>
        </select>
      </label>

      <label class="field">
        <span>周次显示</span>
        <select
          :value="settings.weekFilter"
          @change="emit('update', { weekFilter: ($event.target as HTMLSelectElement).value as WidgetSettings['weekFilter'] })"
        >
          <option value="all">全部周次</option>
          <option value="odd">仅单周</option>
          <option value="even">仅双周</option>
        </select>
      </label>

      <label class="field">
        <span>主题</span>
        <select
          :value="settings.theme"
          @change="emit('update', { theme: ($event.target as HTMLSelectElement).value as WidgetSettings['theme'] })"
        >
          <option value="auto">跟随系统</option>
          <option value="light">浅色</option>
          <option value="dark">深色</option>
        </select>
      </label>

      <label class="field">
        <span>不透明度 {{ Math.round(settings.opacity * 100) }}%</span>
        <input
          type="range"
          min="0.3"
          max="1"
          step="0.02"
          :value="settings.opacity"
          @input="emit('update', { opacity: Number(($event.target as HTMLInputElement).value) })"
        />
      </label>
    </div>

    <div class="toggles">
      <label><input type="checkbox" :checked="settings.showWeekend" @change="emit('update', { showWeekend: ($event.target as HTMLInputElement).checked })" /> 显示周末</label>
      <label><input type="checkbox" :checked="settings.trimEmptySlots" @change="emit('update', { trimEmptySlots: ($event.target as HTMLInputElement).checked })" /> 紧凑模式（隐藏空闲节次）</label>
      <label><input type="checkbox" :checked="settings.clickThrough" @change="emit('update', { clickThrough: ($event.target as HTMLInputElement).checked })" /> 点击穿透（锁定挂件）</label>
      <label><input type="checkbox" :checked="settings.showWidget" @change="emit('update', { showWidget: ($event.target as HTMLInputElement).checked })" /> 显示挂件</label>
      <label><input type="checkbox" :checked="settings.launchAtLogin" @change="emit('update', { launchAtLogin: ($event.target as HTMLInputElement).checked })" /> 开机自启</label>
    </div>

    <footer class="foot">
      <button type="button" @click="emit('reset')">清空课表</button>
      <span class="hint">挂件位置与大小可通过拖动 / 右下角手柄调整，自动保存</span>
    </footer>
  </section>
</template>

<style scoped>
.card {
  border: 1px solid #d5dde8;
  border-radius: 10px;
  background: #fff;
  padding: 12px 14px;
}
.card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.card-head h2 {
  font-size: 15px;
  margin: 0;
}
button {
  border: 1px solid #d5dde8;
  background: #fff;
  border-radius: 6px;
  padding: 5px 12px;
  font-size: 13px;
  cursor: pointer;
}
button:hover:not(:disabled) {
  border-color: #2563eb;
  color: #2563eb;
}
button.primary {
  background: #2563eb;
  border-color: #2563eb;
  color: #fff;
}
button.primary:hover:not(:disabled) {
  background: #1d4ed8;
  color: #fff;
}
button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 10px 14px;
  margin: 12px 0;
}
.field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 13px;
}
.field select {
  padding: 4px 8px;
  border: 1px solid #d5dde8;
  border-radius: 6px;
  font-size: 13px;
}
.toggles {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 16px;
  font-size: 13px;
  color: #374151;
  border-top: 1px solid #e8edf4;
  padding-top: 10px;
}
.toggles label {
  display: flex;
  align-items: center;
  gap: 6px;
}
.foot {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 12px;
  border-top: 1px solid #e8edf4;
  padding-top: 10px;
}
.hint {
  font-size: 12px;
  color: #6b7280;
}
</style>
