import { parseHttpRequest } from '@tjt/core';
import type { TongjiFetchResult } from '../shared/ipc.js';
import { log } from './logger.js';

/**
 * 用「用户从浏览器复制出来的请求」抓取个人课表。
 *
 * 为什么不猜接口路径：1 系统的个人课表来自选课服务
 * `POST /api/electionservice/student/{id}/getDataBk`，其中 `{id}` 是选课批次相关的内部 id，
 * 无法稳定构造。让用户 F12 → Copy as cURL 粘一次最可靠，也天然带上了 Cookie 与 `x-token`。
 *
 * 安全：Cookie 只在内存与本机 `credentials.json` 里，**任何日志都不打印其内容**。
 */

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';

/** 响应是否像同济课表数据。 */
function looksLikeTimetable(payload: unknown): { ok: boolean; note: string } {
  if (!payload || typeof payload !== 'object') return { ok: false, note: '响应不是 JSON 对象' };
  const record = payload as Record<string, unknown>;
  if ('message' in record && !('data' in record)) {
    return { ok: false, note: `服务端返回：${String(record.message).slice(0, 80)}` };
  }
  const data = record.data;
  if (data && typeof data === 'object' && !Array.isArray(data)) {
    const selected = (data as { selectedCourses?: unknown }).selectedCourses;
    if (Array.isArray(selected)) return { ok: true, note: `selectedCourses ${selected.length} 门` };
    return { ok: false, note: `data 字段：${Object.keys(data as object).slice(0, 8).join(', ')}` };
  }
  if (Array.isArray(data)) return { ok: true, note: `data 数组 ${data.length} 条` };
  return { ok: false, note: `顶层字段：${Object.keys(record).slice(0, 8).join(', ')}` };
}

export async function fetchViaPastedRequest(requestText: string): Promise<TongjiFetchResult> {
  const spec = parseHttpRequest(requestText);
  if (!spec) {
    return {
      ok: false,
      message:
        '没能从粘贴的内容里解析出请求。请在 F12 → Network 里右键该请求 → Copy → Copy as cURL，然后原样粘贴到上面。',
    };
  }

  const cookie = spec.headers['cookie'] ?? '';
  if (!cookie) {
    return {
      ok: false,
      message: '粘贴的请求里没有 Cookie：请用 "Copy as cURL"（会带上全部请求头），而不是只复制 URL。',
    };
  }

  const headers: Record<string, string> = { 'user-agent': UA, accept: 'application/json, text/plain, */*' };
  for (const [key, value] of Object.entries(spec.headers)) {
    if (key === 'host' || key === 'content-length' || key === 'accept-encoding') continue;
    headers[key] = value;
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 30_000);
  let status = 0;
  let text = '';
  try {
    const res = await fetch(spec.url, {
      method: spec.method,
      headers,
      ...(spec.body !== undefined ? { body: spec.body } : {}),
      signal: controller.signal,
    });
    status = res.status;
    text = await res.text();
  } catch (error) {
    log('[tongji] 请求异常', error instanceof Error ? error.message : String(error));
    return { ok: false, message: `请求失败：${error instanceof Error ? error.message : String(error)}` };
  } finally {
    clearTimeout(timer);
  }

  const probes = [
    { label: '请求', value: `${spec.method} ${spec.url}` },
    { label: 'HTTP', value: String(status) },
    { label: '来源', value: spec.source },
    { label: '响应大小', value: `${text.length} 字节` },
  ];

  if (status !== 200) {
    return {
      ok: false,
      message:
        status === 401 || status === 403
          ? '登录态已失效（HTTP 401/403）：请重新登录 1 系统，再复制一次请求。'
          : `服务端返回 HTTP ${status}${text ? `：${text.slice(0, 120)}` : ''}`,
      probes,
    };
  }

  let payload: unknown = null;
  try {
    payload = JSON.parse(text) as unknown;
  } catch {
    return { ok: false, message: '响应不是合法 JSON（可能复制到了 HTML 页面请求，请换成 getDataBk 那条）。', probes };
  }

  const verdict = looksLikeTimetable(payload);
  probes.push({ label: '数据识别', value: verdict.note });
  if (!verdict.ok) {
    return {
      ok: false,
      message: `这份响应里没有个人课表数据（${verdict.note}）。请在课表/选课页面刷新后，复制其中那条返回 200 且体积较大的请求。`,
      probes,
    };
  }

  log('[tongji] 抓取成功', { note: verdict.note, bytes: text.length });
  return {
    ok: true,
    message: `获取成功：${verdict.note}，已交给解析器。`,
    timetableText: text,
    probes,
  };
}
