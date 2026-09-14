<script setup lang="ts">
import { computed } from 'vue';
import type { Diagnostic } from '@tjt/core';
import type { AdapterInfo, PickedFile } from '../../shared/ipc';

/** 导入面板：适配器选择 + 粘贴 / 选文件 + 诊断信息。 */

const props = defineProps<{
  adapters: AdapterInfo[];
  adapterId: string;
  pastedText: string;
  files: PickedFile[];
  diagnostics: Diagnostic[];
  error: string;
  busy: boolean;
}>();

const emit = defineEmits<{
  (event: 'update:adapterId', value: string): void;
  (event: 'update:pastedText', value: string): void;
  (event: 'pick'): void;
  (event: 'run'): void;
  (event: 'remove-file', index: number): void;
  (event: 'clear'): void;
}>();

const placeholder = `把同济 1 系统的接口响应 JSON 粘贴到这里，例如：
{"code":200,"msg":"","data":[{"teachingClassId":1111111124960444,"dayOfWeek":7,"timeStart":9,"timeEnd":10,"weekState":65535,"courseName":"社会实践",...}]}

也可以同时选择"课表 + 校历"两个 JSON 文件。`;

const hasInput = computed(() => props.pastedText.trim().length > 0 || props.files.length > 0);
</script>

<template>
  <section class="card">
    <header class="card-head">
      <h2>1 · 导入课表</h2>
      <button type="button" class="primary" :disabled="!hasInput || busy" @click="emit('run')">
        {{ busy ? '解析中…' : '解析' }}
      </button>
    </header>

    <label class="field">
      <span>适配器</span>
      <select :value="adapterId" @change="emit('update:adapterId', ($event.target as HTMLSelectElement).value)">
        <option value="">自动探测（推荐）</option>
        <option v-for="adapter in adapters" :key="adapter.id" :value="adapter.id">
          {{ adapter.displayName }}{{ adapter.canFetch ? '' : '' }}
        </option>
      </select>
    </label>

    <textarea
      class="paste"
      :value="pastedText"
      :placeholder="placeholder"
      spellcheck="false"
      @input="emit('update:pastedText', ($event.target as HTMLTextAreaElement).value)"
    />

    <div class="row">
      <button type="button" @click="emit('pick')">选择 JSON 文件…</button>
      <button type="button" @click="emit('clear')">清空</button>
      <span class="hint">支持一次选多个文件（课表 + 校历 + 学生信息）</span>
    </div>

    <ul v-if="files.length" class="file-list">
      <li v-for="(file, index) in files" :key="file.name + index">
        <span class="name">{{ file.name }}</span>
        <span class="size">{{ (file.text.length / 1024).toFixed(1) }} KB</span>
        <button type="button" class="link" @click="emit('remove-file', index)">移除</button>
      </li>
    </ul>

    <p v-if="error" class="error">{{ error }}</p>

    <ul v-if="diagnostics.length" class="diagnostics">
      <li v-for="(item, index) in diagnostics" :key="index" :class="item.level">
        <b>{{ item.level }}</b>
        <span>{{ item.message }}</span>
      </li>
    </ul>

    <details class="help">
      <summary>数据怎么获取？</summary>
      <ol>
        <li>浏览器登录 1 系统（<code>1.tongji.edu.cn</code>），打开个人专业课表页面。</li>
        <li>F12 → Network，找到课表接口响应（含 <code>weekState</code> / <code>dayOfWeek</code> 字段），复制为 JSON。</li>
        <li>再复制一份校历接口响应（含 <code>noWeekendWorkTimes</code>），用于计算节次时间与"当前第几周"。</li>
        <li>把两份 JSON 粘贴或保存成文件后导入，然后勾选自己实际要上的教学班。</li>
      </ol>
    </details>
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
  gap: 8px;
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
button.link {
  border: none;
  color: #2563eb;
  padding: 0 4px;
}
.field {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 10px 0 6px;
  font-size: 13px;
}
.field select {
  flex: 1 1 auto;
  padding: 4px 8px;
  border: 1px solid #d5dde8;
  border-radius: 6px;
  font-size: 13px;
}
.paste {
  width: 100%;
  min-height: 130px;
  box-sizing: border-box;
  border: 1px solid #d5dde8;
  border-radius: 8px;
  padding: 8px 10px;
  font-family: Consolas, 'Courier New', monospace;
  font-size: 12px;
  line-height: 1.5;
  resize: vertical;
}
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
}
.hint {
  font-size: 12px;
  color: #6b7280;
}
.file-list {
  list-style: none;
  margin: 8px 0 0;
  padding: 0;
  font-size: 12px;
}
.file-list li {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 3px 0;
}
.file-list .name {
  flex: 1 1 auto;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.file-list .size {
  color: #6b7280;
}
.error {
  color: #dc2626;
  font-size: 12px;
  margin: 8px 0 0;
}
.diagnostics {
  list-style: none;
  margin: 8px 0 0;
  padding: 0;
  font-size: 12px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.diagnostics li {
  display: flex;
  gap: 6px;
  padding: 4px 8px;
  border-radius: 6px;
  background: #f8fafc;
}
.diagnostics li.warn {
  background: #fffbeb;
}
.diagnostics li.error {
  background: #fef2f2;
  color: #b91c1c;
}
.diagnostics b {
  font-weight: 600;
  text-transform: uppercase;
  font-size: 10px;
  line-height: 16px;
}
.help {
  margin-top: 10px;
  font-size: 12px;
  color: #4b5563;
}
.help summary {
  cursor: pointer;
}
.help code {
  background: #f1f5f9;
  padding: 0 3px;
  border-radius: 3px;
}
</style>
