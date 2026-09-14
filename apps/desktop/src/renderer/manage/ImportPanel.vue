<script setup lang="ts">
import { computed, ref } from 'vue';
import type { Diagnostic } from '@tjt/core';
import type { AdapterInfo, PickedFile } from '../../shared/ipc';

/** 导入面板：① 从 1 系统抓取个人课表 ② 本地 JSON 导入。 */

const props = defineProps<{
  adapters: AdapterInfo[];
  adapterId: string;
  pastedText: string;
  cookie: string;
  files: PickedFile[];
  diagnostics: Diagnostic[];
  probes: { path: string; status: number; note?: string }[];
  error: string;
  busy: boolean;
  fetchBusy: boolean;
}>();

const emit = defineEmits<{
  (event: 'update:adapterId', value: string): void;
  (event: 'update:pastedText', value: string): void;
  (event: 'update:cookie', value: string): void;
  (event: 'pick'): void;
  (event: 'run'): void;
  (event: 'fetch', cookie: string): void;
  (event: 'remove-file', index: number): void;
  (event: 'clear'): void;
}>();

const showCookie = ref(false);
const showProbes = ref(false);

const hasInput = computed(() => props.pastedText.trim().length > 0 || props.files.length > 0);

const placeholder = `把个人课表接口的响应 JSON 粘贴到这里，例如：
{"code":200,"msg":"","data":[{"teachingClassId":1111111124960444,"dayOfWeek":7,"timeStart":9,"timeEnd":10,"weekState":65535,"courseName":"社会实践",...}]}

可选：再粘一份校历响应（含 noWeekendWorkTimes），用于节次时间与"当前第几周"。`;
</script>

<template>
  <section class="card">
    <header class="card-head">
      <h2>1 · 从同济 1 系统获取</h2>
      <button type="button" class="primary" :disabled="!cookie.trim() || fetchBusy" @click="emit('fetch', cookie)">
        {{ fetchBusy ? '获取中…' : '获取我的课表' }}
      </button>
    </header>

    <label class="field">
      <span>1 系统 Cookie</span>
      <span class="row-inline">
        <input
          :type="showCookie ? 'text' : 'password'"
          class="cookie"
          :value="cookie"
          placeholder="粘贴请求头里的 Cookie 值（很长，正常）"
          spellcheck="false"
          @input="emit('update:cookie', ($event.target as HTMLInputElement).value)"
        />
        <button type="button" class="link" @click="showCookie = !showCookie">{{ showCookie ? '隐藏' : '显示' }}</button>
      </span>
    </label>

    <details class="help">
      <summary>怎么拿到 Cookie？</summary>
      <ol>
        <li>Chrome/Edge 登录 <code>1.tongji.edu.cn</code>，打开"我的课表"页面。</li>
        <li>按 F12 → <b>Network</b> → 刷新页面 → 随便点一条发往 1.tongji.edu.cn 的请求。</li>
        <li>右侧 <b>Headers → Request Headers</b> 里找到 <code>Cookie:</code>，把它<b>后面那一整串</b>复制过来。</li>
        <li>粘贴到上面 → 点"获取我的课表"。Cookie 只保存在本机 <code>%APPDATA%\TJDesktopTimetable</code>，不上传、不进日志；用完可在浏览器退出登录使其失效。</li>
      </ol>
    </details>

    <p v-if="error" class="error">{{ error }}</p>

    <details v-if="probes.length" class="probes" :open="showProbes" @toggle="showProbes = ($event.target as HTMLDetailsElement).open">
      <summary>接口探测详情（{{ probes.length }} 条）——抓取失败时请把这里发给我</summary>
      <ul>
        <li v-for="(probe, index) in probes" :key="index">
          <code>{{ probe.path }}</code>
          <b :class="{ bad: probe.status === 0 || probe.status >= 400 }">HTTP {{ probe.status || '失败' }}</b>
          <span v-if="probe.note">· {{ probe.note }}</span>
        </li>
      </ul>
    </details>

    <ul v-if="diagnostics.length" class="diagnostics">
      <li v-for="(item, index) in diagnostics" :key="index" :class="item.level">
        <b>{{ item.level }}</b>
        <span>{{ item.message }}</span>
      </li>
    </ul>
  </section>

  <section class="card">
    <header class="card-head">
      <h2>2 · 本地 JSON 导入</h2>
      <button type="button" :disabled="!hasInput || busy" @click="emit('run')">
        {{ busy ? '解析中…' : '导入并应用' }}
      </button>
    </header>

    <label class="field">
      <span>适配器</span>
      <select :value="adapterId" @change="emit('update:adapterId', ($event.target as HTMLSelectElement).value)">
        <option value="">自动探测（推荐）</option>
        <option v-for="adapter in adapters" :key="adapter.id" :value="adapter.id">{{ adapter.displayName }}</option>
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
      <span class="hint">可一次选多份（课表 + 校历）</span>
    </div>

    <ul v-if="files.length" class="file-list">
      <li v-for="(file, index) in files" :key="file.name + index">
        <span class="name">{{ file.name }}</span>
        <span class="size">{{ (file.text.length / 1024).toFixed(1) }} KB</span>
        <button type="button" class="link" @click="emit('remove-file', index)">移除</button>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.card {
  border: 1px solid #d5dde8;
  border-radius: 10px;
  background: #fff;
  padding: 12px 14px;
}
.card + .card {
  margin-top: 12px;
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
.field > span:first-child {
  flex: 0 0 auto;
}
.row-inline {
  display: flex;
  align-items: center;
  gap: 4px;
  flex: 1 1 auto;
  min-width: 0;
}
.cookie {
  flex: 1 1 auto;
  min-width: 0;
  padding: 5px 8px;
  border: 1px solid #d5dde8;
  border-radius: 6px;
  font-family: Consolas, monospace;
  font-size: 12px;
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
  min-height: 120px;
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
  line-height: 1.6;
}
.help {
  font-size: 12px;
  color: #4b5563;
  margin-top: 6px;
}
.help summary {
  cursor: pointer;
}
.help ol {
  margin: 6px 0 0;
  padding-left: 18px;
  line-height: 1.7;
}
.help code {
  background: #f1f5f9;
  padding: 0 3px;
  border-radius: 3px;
}
.probes {
  margin-top: 8px;
  font-size: 12px;
  color: #4b5563;
}
.probes summary {
  cursor: pointer;
}
.probes ul {
  list-style: none;
  margin: 6px 0 0;
  padding: 0;
  max-height: 160px;
  overflow: auto;
}
.probes li {
  display: flex;
  gap: 6px;
  align-items: baseline;
  padding: 2px 0;
}
.probes code {
  font-family: Consolas, monospace;
}
.probes .bad {
  color: #dc2626;
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
</style>
