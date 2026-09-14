/**
 * 解析"用户从浏览器复制出来的请求"，用于替代猜接口路径。
 *
 * 支持三种输入：
 * 1. F12 → Copy as cURL（bash）粘贴的命令行；
 * 2. 你在 PowerShell 里用的 `Invoke-WebRequest ... -WebSession $session` 片段
 *    （含 `$session.Cookies.Add(...)` 与 `-Headers @{...}`）；
 * 3. 只贴一个 `https://...` 地址（无 Cookie，实际会 401，仅作兜底）。
 *
 * 这是纯函数，方便单测；真正的网络请求由窗口层负责。
 */

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE' | 'PATCH';

export interface HttpRequestSpec {
  url: string;
  method: HttpMethod;
  headers: Record<string, string>;
  body?: string;
  source: 'curl' | 'powershell' | 'url';
}

/** 续行符（bash `\`、PowerShell 反引号、cmd `^`）统一成空格。 */
function normalize(text: string): string {
  return text.replace(/\\\r?\n/g, ' ').replace(/`\r?\n/g, ' ').replace(/\^\r?\n/g, ' ').trim();
}

/** 按 shell 规则切分 token（尊重单/双引号与反斜杠转义）。 */
export function tokenize(input: string): string[] {
  const tokens: string[] = [];
  let current = '';
  let quote: string | null = null;
  for (let i = 0; i < input.length; i += 1) {
    const ch = input[i]!;
    if (quote) {
      if (ch === '\\' && quote === '"') {
        current += input[i + 1] ?? '';
        i += 1;
        continue;
      }
      if (ch === quote) {
        quote = null;
        continue;
      }
      current += ch;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (/\s/.test(ch)) {
      if (current) {
        tokens.push(current);
        current = '';
      }
      continue;
    }
    current += ch;
  }
  if (current) tokens.push(current);
  return tokens;
}

function isUrl(value: string): boolean {
  return /^https?:\/\//i.test(value);
}

function parseCurl(text: string): HttpRequestSpec | null {
  const tokens = tokenize(normalize(text));
  const headers: Record<string, string> = {};
  let url: string | undefined;
  let method: HttpMethod = 'GET';
  let body: string | undefined;

  for (let i = 0; i < tokens.length; i += 1) {
    const token = tokens[i]!;
    const next = tokens[i + 1];

    if (token === '-H' || token === '--header') {
      if (next) {
        const idx = next.indexOf(':');
        if (idx > 0) headers[next.slice(0, idx).trim().toLowerCase()] = next.slice(idx + 1).trim();
        i += 1;
      }
      continue;
    }
    if (token === '-X' || token === '--request') {
      if (next) {
        method = next.toUpperCase() as HttpMethod;
        i += 1;
      }
      continue;
    }
    if (['-d', '--data', '--data-raw', '--data-binary', '--data-urlencode'].includes(token)) {
      if (next !== undefined) {
        body = body === undefined ? next : `${body}&${next}`;
        method = method === 'GET' ? 'POST' : method;
        i += 1;
      }
      continue;
    }
    if (token === '-b' || token === '--cookie') {
      if (next !== undefined) {
        headers['cookie'] = next;
        i += 1;
      }
      continue;
    }
    if (token.startsWith('http') && !url) {
      url = token;
    }
  }

  if (!url) return null;
  return { url, method, headers, ...(body !== undefined ? { body } : {}), source: 'curl' };
}

function parsePowerShell(text: string): HttpRequestSpec | null {
  const flat = normalize(text);
  const uriMatch = flat.match(/-Uri\s+["']([^"']+)["']/i);
  const bareUrl = flat.match(/https?:\/\/[^\s"')]+/);
  const url = uriMatch?.[1] ?? bareUrl?.[0];
  if (!url) return null;

  const method = (flat.match(/-Method\s+["']?([A-Za-z]+)["']?/i)?.[1]?.toUpperCase() ?? 'GET') as HttpMethod;

  const headers: Record<string, string> = {};
  const headerBlock = flat.match(/-Headers\s+@\{([\s\S]*?)\}\s*(?=-|$)/i)?.[1];
  if (headerBlock) {
    for (const match of headerBlock.matchAll(/["']([^"']+)["']\s*=\s*["']([^"']*)["']/g)) {
      const key = match[1];
      const value = match[2];
      if (key && value !== undefined) headers[key.toLowerCase()] = value;
    }
  }

  // $session.Cookies.Add((New-Object System.Net.Cookie("name", "value", "/", "domain")))
  const cookies: string[] = [];
  for (const match of flat.matchAll(/New-Object\s+System\.Net\.Cookie\(\s*["']([^"']+)["']\s*,\s*["']([^"']*)["']/gi)) {
    const name = match[1];
    const value = match[2];
    if (name) cookies.push(`${name}=${value ?? ''}`);
  }
  if (cookies.length) headers['cookie'] = cookies.join('; ');

  const bodyMatch = flat.match(/-Body\s+["']([\s\S]*?)["']\s*(?=-|$)/i);

  return {
    url,
    method,
    headers,
    ...(bodyMatch?.[1] ? { body: bodyMatch[1] } : {}),
    source: 'powershell',
  };
}

/** 解析请求文本；无法识别时返回 `null`。 */
export function parseHttpRequest(text: string): HttpRequestSpec | null {
  const trimmed = text.trim();
  if (!trimmed) return null;
  if (isUrl(trimmed)) return { url: trimmed, method: 'GET', headers: {}, source: 'url' };

  const lower = trimmed.toLowerCase();
  if (lower.includes('invoke-webrequest') || lower.includes('invoke-restmethod') || lower.includes('$session')) {
    return parsePowerShell(trimmed);
  }
  if (lower.includes('curl')) return parseCurl(trimmed);
  return parsePowerShell(trimmed) ?? parseCurl(trimmed);
}

/** 从请求里取 Cookie（用于判断是否携带登录态）。 */
export function cookieOf(spec: HttpRequestSpec): string {
  return spec.headers['cookie'] ?? '';
}
