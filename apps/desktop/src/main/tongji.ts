import { TONGJI_ORIGIN } from '@tjt/core';
import type { TongjiFetchResult } from '../shared/ipc.js';
import { log } from './logger.js';

/**
 * 同济 1 系统个人课表抓取。
 *
 * 思路：Cookie 由用户手动粘贴（不做自动登录 —— 1 系统 SSO 带短信增强，塞进桌面客户端不划算），
 * 拿到 Cookie 后由主进程发起请求（渲染层会被 CORS 拦）。
 *
 * 接口路径不写死：先试一批候选路径，再从 1 系统前端 bundle 里正则捞 `timetable` 相关路径，
 * 命中"含 weekState/dayOfWeek 的 JSON 数组"就算成功。这样接口改名时也能自愈，
 * 抓不到时把每个候选的状态码回传，便于定位。
 */

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';

const TIMETABLE_CANDIDATES = [
  '/api/arrangementservice/timetable/student',
  '/api/arrangementservice/timetable/my',
  '/api/arrangementservice/timetable/personal',
  '/api/arrangementservice/timetable/studentTimeTable',
  '/api/arrangementservice/timetable/current',
  '/api/arrangementservice/student/timetable',
  '/api/arrangementservice/timetable/major',
];

const CALENDAR_CANDIDATES = [
  '/api/baseresservice/schoolCalendar/queryAll',
  '/api/baseresservice/schoolCalendar/all',
  '/api/baseresservice/schoolCalendar/list',
  '/api/arrangementservice/schoolCalendar/queryAll',
];

interface ProbeRecord {
  path: string;
  status: number;
  note?: string;
}

async function httpGet(
  path: string,
  cookie: string,
  timeoutMs = 20_000,
): Promise<{ status: number; text: string; contentType: string }> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(`${TONGJI_ORIGIN}${path}`, {
      headers: {
        cookie,
        'user-agent': UA,
        accept: 'application/json, text/plain, */*',
        'x-requested-with': 'XMLHttpRequest',
      },
      signal: controller.signal,
    });
    return { status: res.status, text: await res.text(), contentType: res.headers.get('content-type') ?? '' };
  } catch (error) {
    log('[tongji] 请求失败', path, error instanceof Error ? error.message : String(error));
    return { status: 0, text: '', contentType: '' };
  } finally {
    clearTimeout(timer);
  }
}

function parseJson(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return null;
  }
}

function dataArrayOf(payload: unknown): unknown[] | null {
  if (Array.isArray(payload)) return payload;
  if (payload && typeof payload === 'object' && 'data' in payload) {
    const data = (payload as { data: unknown }).data;
    if (Array.isArray(data)) return data;
    if (Array.isArray((data as { records?: unknown })?.records)) {
      return (data as { records: unknown[] }).records;
    }
  }
  return null;
}

function isTimetablePayload(payload: unknown): boolean {
  const list = dataArrayOf(payload);
  if (!list?.length) return false;
  return list.some((item) => {
    if (!item || typeof item !== 'object') return false;
    const record = item as Record<string, unknown>;
    return 'weekState' in record && 'dayOfWeek' in record;
  });
}

function isCalendarPayload(payload: unknown): boolean {
  const list = dataArrayOf(payload);
  if (!list?.length) return false;
  return list.some((item) => {
    if (!item || typeof item !== 'object') return false;
    const record = item as Record<string, unknown>;
    return 'noWeekendWorkTimes' in record || ('beginDay' in record && 'weekNum' in record);
  });
}

/** 从前端 bundle 里捞 `timetable` / `schoolCalendar` 相关 API 路径，补充候选。 */
async function discoverPathsFromBundle(cookie: string, probes: ProbeRecord[]): Promise<string[]> {
  const discovered = new Set<string>();
  const home = await httpGet('/', cookie, 20_000);
  probes.push({ path: '/', status: home.status, note: `首页 ${home.contentType}` });
  if (home.status !== 200) return [];

  const scripts = [...home.text.matchAll(/<script[^>]+src="([^"]+)"/g)]
    .map((m) => m[1])
    .filter((value): value is string => Boolean(value));
  for (const src of scripts.slice(0, 12)) {
    const url = src.startsWith('http') ? src : `${TONGJI_ORIGIN}${src.startsWith('/') ? '' : '/'}${src}`;
    let text = '';
    try {
      const res = await fetch(url, { headers: { cookie, 'user-agent': UA } });
      text = await res.text();
    } catch {
      continue;
    }
    for (const match of text.matchAll(/["'`](\/api\/[a-zA-Z0-9/_-]*(?:timetable|schedule|arrange)[a-zA-Z0-9/_-]*)["'`]/g)) {
      const path = match[1];
      if (path && !discovered.has(path)) discovered.add(path);
    }
  }
  if (discovered.size) {
    probes.push({ path: '<bundle>', status: 200, note: `从 JS 发现 ${discovered.size} 个候选路径` });
  }
  return [...discovered];
}

/** 用 Cookie 抓取个人课表（并尝试顺带抓校历）。 */
export async function fetchTongjiTimetable(cookie: string): Promise<TongjiFetchResult> {
  const trimmed = cookie.trim();
  if (!trimmed) return { ok: false, message: '请先填入 1 系统 Cookie。' };

  const probes: ProbeRecord[] = [];
  const discovered = await discoverPathsFromBundle(trimmed, probes);
  const candidates = [...new Set([...TIMETABLE_CANDIDATES, ...discovered])];

  let timetableText: string | undefined;
  let timetablePath = '';
  for (const path of candidates) {
    const res = await httpGet(path, trimmed);
    const payload = res.status === 200 ? parseJson(res.text) : null;
    const hit = payload !== null && isTimetablePayload(payload);
    probes.push({ path, status: res.status, note: hit ? '命中课表数据' : undefined });
    if (hit) {
      timetableText = res.text;
      timetablePath = path;
      break;
    }
  }

  if (!timetableText) {
    const unauthorized = probes.some((p) => p.status === 401 || p.status === 403);
    log('[tongji] 抓取失败', { probes });
    return {
      ok: false,
      message: unauthorized
        ? 'Cookie 可能已失效（接口返回 401/403）：请在浏览器重新登录 1 系统后复制新的 Cookie。'
        : '没有找到个人课表接口。请把下面的探测结果发给我，我按真实路径调整；也可以先用「本地 JSON 导入」。',
      probes,
    };
  }

  let calendarText: string | undefined;
  for (const path of CALENDAR_CANDIDATES) {
    const res = await httpGet(path, trimmed);
    const payload = res.status === 200 ? parseJson(res.text) : null;
    const hit = payload !== null && isCalendarPayload(payload);
    probes.push({ path, status: res.status, note: hit ? '命中校历' : undefined });
    if (hit) {
      calendarText = res.text;
      break;
    }
  }

  return {
    ok: true,
    message: `已从 ${timetablePath} 获取课表${calendarText ? '，并取得校历' : '（未取到校历，将使用内置节次时间）'}。`,
    timetableText,
    ...(calendarText ? { calendarText } : {}),
    probes,
  };
}
