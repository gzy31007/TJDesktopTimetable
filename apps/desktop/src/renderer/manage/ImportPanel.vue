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
curl 'https://1.tongji.edu.cn/api/electionservice/reportManagement/findStudentTimetab?calendarId=122&studentCode=...' -H 'cookie: ...' -H 'x-token: ...'

也支持旧接口 /api/electionservice/student/xxxx/getDataBk 与 PowerShell 的 Invoke-WebRequest 片段。整条请求自带登录态，程序只做这一次请求。`;

const jsonPlaceholder = `或者把课表接口的响应 JSON 直接粘贴到这里：
{"code":200,"msg":"","data":{"calendarId":122,"selectedCourses":[{"course":{"courseName":"大学物理B2(I)","times":[...]}}]}}`;
</script>

<template>
  <section class="f-card glass--edge">
    <header class="f-card-head">
      <h2>从 1 系统获取我的课表</h2>
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
        <li>浏览器登录 <code>1.tongji.edu.cn</code>，打开<b>我的课表</b>页面。</li>
        <li>按 F12 → <b>Network</b> → 刷新页面。</li>
        <li>
          找到返回 200、<b>内容是课程列表</b>的那条 —— 课表页现在调的是
          <code>/api/electionservice/reportManagement/findStudentTimetab?calendarId=…&amp;studentCode=…</code>
          （旧接口 <code>/api/electionservice/student/xxxx/getDataBk</code> 同样支持），
          <b>右键 → Copy → Copy as cURL</b>。
          别复制成校历那条 <code>/api/baseresservice/schoolCalendar/detail</code> —— 那只是学期起止，里面没有课程。
        </li>
        <li>
          粘贴到上面的框里 → 点"获取我的课表"。请求里已经包含 Cookie 与 x-token，程序照原样请求一次；
          学期 id 会从请求里的 <code>calendarId</code> 自动取，所以"现在第几周"也是准的。
        </li>
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

  <section class="f-card glass--edge">
    <header class="f-card-head">
      <h2>本地 JSON 导入</h2>
      <button type="button" :disabled="!hasInput || busy" @click="emit('run')">
        {{ busy ? '解析中…' : '导入并应用' }}
      </button>
    </header>

    <label class="field">
      <span class="lbl">适配器</span>
      <select class="f-select" :value="adapterId" @change="emit('update:adapterId', ($event.target as HTMLSelectElement).value)">
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
      <span class="f-hint">粘贴请求里的响应也可以直接存成 JSON 再导入</span>
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
.field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  margin-bottom: 10px;
}

.field .lbl {
  font-size: var(--fs-body);
  color: var(--text-2);
}

.paste {
  width: 100%;
  min-height: 116px;
  padding: 8px 10px;
  font-family: var(--font-num);
  font-size: var(--fs-body);
  line-height: 1.55;
  resize: vertical;
}

.paste.short {
  min-height: 84px;
}

.row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 10px;
  flex-wrap: wrap;
}

.help {
  margin-top: 10px;
  font-size: var(--fs-body);
  line-height: 1.7;
}

.help summary {
  cursor: pointer;
  color: var(--text-2);
  font-size: var(--fs-body);
}

.help ol {
  margin: 8px 0 0;
  padding-left: 18px;
}

.help .note {
  margin: 8px 0 0;
  color: var(--text-3);
  line-height: 1.6;
}

.error {
  color: #c42b1c;
  font-size: var(--fs-body);
  margin: 10px 0 0;
  line-height: 1.6;
  padding: 7px 10px;
  border-radius: var(--r-md);
  background-color: rgba(196, 43, 28, 0.09);
  border: 1px solid rgba(196, 43, 28, 0.2);
}

.file-list {
  list-style: none;
  margin: 10px 0 0;
  padding: 0;
  font-size: var(--fs-body);
}

.file-list li {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
}

.file-list .name {
  flex: 1 1 auto;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-list .size {
  color: var(--text-3);
  font-variant-numeric: tabular-nums;
}

.probes {
  list-style: none;
  margin: 10px 0 0;
  padding: 0;
  font-size: var(--fs-body);
}

.probes li {
  display: flex;
  gap: 8px;
  padding: 4px 8px;
  border-radius: var(--r-sm);
  margin-bottom: 4px;
}

.probes .label {
  color: var(--text-3);
  flex: 0 0 76px;
}

.probes .value {
  font-family: var(--font-num);
  word-break: break-all;
}

.diagnostics {
  list-style: none;
  margin: 10px 0 0;
  padding: 0;
  font-size: var(--fs-body);
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.diagnostics li {
  display: flex;
  gap: 8px;
  padding: 6px 9px;
  border-radius: var(--r-sm);
  line-height: 1.5;
}

.diagnostics b {
  font-weight: 600;
  text-transform: uppercase;
  font-size: 10px;
  line-height: 16px;
  flex: 0 0 auto;
}
</style>
