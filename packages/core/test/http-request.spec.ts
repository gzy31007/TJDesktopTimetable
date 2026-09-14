import { describe, expect, it } from 'vitest';
import { cookieOf, parseHttpRequest, tokenize } from '../src/index.js';

/** 用户实际提供的 PowerShell 片段（Cookie 已替换为占位值）。 */
const powershell = `$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$session.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/152.0.0.0 Safari/537.36 Edg/152.0.0.0"
$session.Cookies.Add((New-Object System.Net.Cookie("language", "cn", "/", "1.tongji.edu.cn")))
$session.Cookies.Add((New-Object System.Net.Cookie("JSESSIONID", "AAA111", "/", "1.tongji.edu.cn")))
$session.Cookies.Add((New-Object System.Net.Cookie("sessionid", "BBB222", "/", "1.tongji.edu.cn")))
Invoke-WebRequest -UseBasicParsing -Uri "https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk" \`
-Method "POST" \`
-WebSession $session \`
-Headers @{
"authority"="1.tongji.edu.cn"
  "method"="POST"
  "accept"="application/json, text/plain, */*"
  "origin"="https://1.tongji.edu.cn"
  "referer"="https://1.tongji.edu.cn/studentElect?t=1789359218896"
  "x-token"="BBB222"
}`;

/** F12 → Copy as cURL (bash) 的典型形态。 */
const curl = `curl 'https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk' \\
  -X POST \\
  -H 'accept: application/json, text/plain, */*' \\
  -H 'cookie: language=cn; JSESSIONID=AAA111; sessionid=BBB222' \\
  -H 'referer: https://1.tongji.edu.cn/studentElect' \\
  -H 'x-token: BBB222' \\
  --data-raw '{"a":1}' \\
  --compressed`;

describe('http-request 解析', () => {
  it('tokenize 尊重引号与转义', () => {
    expect(tokenize(`a 'b c' "d e"`)).toEqual(['a', 'b c', 'd e']);
    expect(tokenize(`-H 'k: v' -H "x: y"`)).toEqual(['-H', 'k: v', '-H', 'x: y']);
  });

  it('解析 PowerShell（Invoke-WebRequest + Cookie 容器）', () => {
    const spec = parseHttpRequest(powershell);
    expect(spec).not.toBeNull();
    expect(spec!.source).toBe('powershell');
    expect(spec!.url).toBe('https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk');
    expect(spec!.method).toBe('POST');
    expect(spec!.headers['x-token']).toBe('BBB222');
    expect(spec!.headers['referer']).toContain('studentElect');
    expect(cookieOf(spec!)).toBe('language=cn; JSESSIONID=AAA111; sessionid=BBB222');
  });

  it('解析 curl（-H/-X/--data-raw，URL 不误取 header 里的 referer）', () => {
    const spec = parseHttpRequest(curl);
    expect(spec).not.toBeNull();
    expect(spec!.source).toBe('curl');
    expect(spec!.url).toBe('https://1.tongji.edu.cn/api/electionservice/student/5582/getDataBk');
    expect(spec!.method).toBe('POST');
    expect(spec!.body).toBe('{"a":1}');
    expect(spec!.headers['cookie']).toContain('sessionid=BBB222');
    expect(spec!.headers['x-token']).toBe('BBB222');
  });

  it('只给 URL 时退化为 GET', () => {
    const spec = parseHttpRequest('https://1.tongji.edu.cn/api/x');
    expect(spec).toEqual({ url: 'https://1.tongji.edu.cn/api/x', method: 'GET', headers: {}, source: 'url' });
  });

  it('无法识别的内容返回 null', () => {
    expect(parseHttpRequest('   ')).toBeNull();
  });
});
