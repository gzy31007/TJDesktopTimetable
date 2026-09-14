<script setup lang="ts">
import type { WidgetSettings } from '../../shared/ipc';
import { THEME_LABELS, THEME_MODES } from '../shared/theme';

/** 外观与行为设置（写回主进程 settings.json）。 */

const props = defineProps<{ settings: WidgetSettings; dirty?: boolean; saving?: boolean }>();

const emit = defineEmits<{
  (event: 'update', patch: Partial<WidgetSettings>): void;
  (event: 'save'): void;
  (event: 'reset'): void;
}>();

void props;
</script>

<template>
  <section class="f-card glass--edge">
    <header class="f-card-head">
      <h2>显示与行为</h2>
      <span class="f-hint">改完立即生效，无需保存</span>
    </header>

    <div class="grid">
      <label class="field">
        <span class="lbl">层级模式</span>
        <select
          class="f-select"
          :value="settings.mode"
          @change="emit('update', { mode: ($event.target as HTMLSelectElement).value as WidgetSettings['mode'] })"
        >
          <option value="desktop">贴桌面层（推荐）</option>
          <option value="wallpaper">壁纸层（桌面图标之下）</option>
        </select>
      </label>

      <label class="field">
        <span class="lbl">周次显示</span>
        <select
          class="f-select"
          :value="settings.weekFilter"
          @change="emit('update', { weekFilter: ($event.target as HTMLSelectElement).value as WidgetSettings['weekFilter'] })"
        >
          <option value="all">全部周次</option>
          <option value="odd">仅单周</option>
          <option value="even">仅双周</option>
        </select>
      </label>

      <label class="field">
        <span class="lbl">主题</span>
        <select
          class="f-select"
          :value="settings.theme"
          @change="emit('update', { theme: ($event.target as HTMLSelectElement).value as WidgetSettings['theme'] })"
        >
          <option v-for="mode in THEME_MODES" :key="mode" :value="mode">{{ THEME_LABELS[mode] }}</option>
        </select>
      </label>

      <label class="field">
        <span class="lbl">
          不透明度
          <b>{{ Math.round(settings.opacity * 100) }}%</b>
        </span>
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

    <hr class="f-divider" />

    <div class="switches">
      <label class="f-switch">
        <input
          type="checkbox"
          :checked="settings.desktopLayer"
          @change="emit('update', { desktopLayer: ($event.target as HTMLInputElement).checked })"
        />
        <span class="track" />
        <span>固定到桌面层（Win+D 后仍可见、不被图标遮挡）</span>
      </label>
      <label class="f-switch">
        <input
          type="checkbox"
          :checked="settings.showWeekend"
          @change="emit('update', { showWeekend: ($event.target as HTMLInputElement).checked })"
        />
        <span class="track" />
        <span>显示周末</span>
      </label>
      <label class="f-switch">
        <input
          type="checkbox"
          :checked="settings.trimEmptySlots"
          @change="emit('update', { trimEmptySlots: ($event.target as HTMLInputElement).checked })"
        />
        <span class="track" />
        <span>紧凑模式（隐藏空闲节次）</span>
      </label>
      <label class="f-switch">
        <input
          type="checkbox"
          :checked="settings.clickThrough"
          @change="emit('update', { clickThrough: ($event.target as HTMLInputElement).checked })"
        />
        <span class="track" />
        <span>点击穿透（锁定挂件）</span>
      </label>
      <label class="f-switch">
        <input
          type="checkbox"
          :checked="settings.showWidget"
          @change="emit('update', { showWidget: ($event.target as HTMLInputElement).checked })"
        />
        <span class="track" />
        <span>显示挂件</span>
      </label>
      <label class="f-switch">
        <input
          type="checkbox"
          :checked="settings.launchAtLogin"
          @change="emit('update', { launchAtLogin: ($event.target as HTMLInputElement).checked })"
        />
        <span class="track" />
        <span>开机自启</span>
      </label>
    </div>

    <hr class="f-divider" />

    <footer class="foot">
      <button type="button" @click="emit('reset')">清空课表</button>
      <span class="f-hint">挂件位置与大小可通过拖动 / 右下角手柄调整，自动保存</span>
    </footer>
  </section>
</template>

<style scoped>
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: 12px 16px;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.field .lbl {
  font-size: var(--fs-body);
  color: var(--text-2);
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
}

.field .lbl b {
  color: var(--accent);
  font-variant-numeric: tabular-nums;
}

.switches {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.switches .f-switch span:last-child {
  font-size: var(--fs-body-lg);
}

.foot {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}
</style>
