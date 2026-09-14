<script setup lang="ts">
import { computed, ref } from 'vue';
import type { Diagnostic } from '@tjt/core';
import type { AdapterInfo, PickedFile } from '../../shared/ipc';

/** 导入面板：① 抓取个人课表（粘贴浏览器请求）② 本地 JSON 导入。 */

const props = defineProps<{
  adapters: AdapterInfo[];
  adapterId: string;
  pastedText: string;
  requestText: string;
  files: PickedFile[];
  diagnostics: Diagnostic[];
  probes: { label: string; value: string }[];
  error: string;
  busy: boolean;
  fetchBusy: boolean;
}>();

const emit = defineEmits<{
  (event: 'update:adapterId', value: string): void;
  (event: 'update:pastedText', value: string): void;
  (event: 'update:requestText', value: string): void;
  (event: 'pick'): void;
  (event: 'run'): void;
  (event: 'fetch', requestText: string): void;
  (event: 'remove-file', index: number): void;
  (event: 'clear'): void;
}>();

const hasInput = computed(() => props.pastedText.trim().length > 0 || props.files.length > 0);
const requestReady = computed(() => props.requestText.trim().length > 0);

const requestPlaceholder = `在这里粘贴从浏览器复制的请求（F12 → Network → 右键该请求 → Copy → Copy as cURL），例如：
curl 'https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk' -X POST -H 'cookie: ...' -H 'x-token: ...'

也支持 PowerShell 的 Invoke-WebRequest 片段。整条请求自带登录态，程序只做这一次请求。`;

const jsonPlaceholder = `或者把课表接口的响应 JSON 直接粘贴到这里：
{"code":200,"msg":"","data":{"calendarId":122,"selectedCourses":[{"course":{"courseName":"大学物理B2(I)","times":[...]}}]}}`;
</script>

<template>
  <section class="card">
    <header class="card-head">
      <h2>1 · 从 1 系统获取我的课表</h2>
      <button type="button" class="primary" :disabled="!requestReady || fetchBusy" @click="emit('fetch', requestText)">
        {{ fetchBusy ? '获取中…' : '获取我的课表' }}
      </button>
    </header>

    <textarea
      class="paste"
      :value="requestText"
      :placeholder="requestPlaceholder"
      spellcheck="false"
      @input="emit('update:requestText', ($event.target as HTMLTextAreaElement).value)"
    />

    <details class="help">
      <summary>怎么复制这条请求？（一次即可，课表变了再重来一次）</summary>
      <ol>
        <li>浏览器登录 <code>1.tongji.edu.cn</code>，打开<b>选课 / 我的课表</b>页面。</li>
        <li>按 F12 → <b>Network</b> → 刷新页面。</li>
        <li>
          找到返回 200、体积较大的那条（一般是
          <code>/api/electionservice/student/xxxx/getDataBk</code>），<b>右键 → Copy → Copy as cURL</b>。
        </li>
        <li>粘贴到上面的框里 → 点"获取我的课表"。请求里已经包含 Cookie 与 x-token，程序照原样请求一次。</li>
      </ol>
      <p class="note">
        粘贴内容只保存在本机 <code>%APPDATA%\TJDesktopTimetable\credentials.json</code>，不上传、不进日志；用完可在浏览器退出登录使其失效。
      </p>
    </details>

    <p v-if="error" class="error">{{ error }}</p>

    <ul v-if="probes.length" class="probes">
      <li v-for="(probe, index) in probes" :key="index">
        <span class="label">{{ probe.label }}</span>
        <span class="value">{{ probe.value }}</span>
      </li>
    </ul>

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
      class="paste short"
      :value="pastedText"
      :placeholder="jsonPlaceholder"
      spellcheck="false"
      @input="emit('update:pastedText', ($event.target as HTMLTextAreaElement).value)"
    />

    <div class="row">
      <button type="button" @click="emit('pick')">选择 JSON 文件…</button>
      <button type="button" @click="emit('clear')">清空</button>
      <span class="hint">粘贴请求里的响应也可以直接存成 JSON 再导入</span>
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
  margin-bottom: 8px;
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
  margin-bottom: 6px;
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
.paste.short {
  min-height: 90px;
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
  margin-top: 8px;
}
.help summary {
  cursor: pointer;
}
.help ol {
  margin: 6px 0 0;
  padding-left: 18px;
  line-height: 1.7;
}
.help .note {
  margin: 6px 0 0;
  color: #6b7280;
  line-height: 1.6;
}
.help code {
  background: #f1f5f9;
  padding: 0 3px;
  border-radius: 3px;
}
.probes {
  list-style: none;
  margin: 8px 0 0;
  padding: 0;
  font-size: 12px;
}
.probes li {
  display: flex;
  gap: 8px;
  padding: 2px 0;
}
.probes .label {
  color: #6b7280;
  flex: 0 0 72px;
}
.probes .value {
  font-family: Consolas, monospace;
  word-break: break-all;
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
