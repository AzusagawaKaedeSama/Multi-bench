// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 远程访问放行策略（单一真源）。
//
// 背景：工作台此前只接受 127.0.0.1 / localhost 的 Host，手机经 Tailscale 访问一律 403。
// 现在 Tailscale Serve 已把 https://kaede.tail34f007.ts.net/ 代理到本机 3900，
// 需要区分「可远程读取/标注」与「仅限本机」两类接口。
//
// 规则：
//   1. 127.0.0.1 / localhost 永远放行（桌面壳与本机浏览器）。
//   2. Tailscale 网段（100.64.0.0/10）与本机 MagicDNS 名放行「远程读取」类接口。
//   3. 能触碰本机文件系统、导出快照、调用 explorer.exe 的接口，永远仅限本机。
//
// 设计要点：放行清单由本文件集中定义，避免在 hub.cjs / workbench.cjs / result-handoff.cjs
// / project-api.cjs 里各写一份字符串判断，改一处漏一处就会出现「手机能看不能改」或反之的错位。

const fs = require('node:fs');
const path = require('node:path');

const HERE = __dirname;

// Tailscale 官方 CGNAT 网段：100.64.0.0/10，覆盖绝大多数家庭/自建 tailnet。
const TAILSCALE_CGNAT = /^100\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;
// Tailscale IPv6 ULA 网段：fd7a:115c:a1e0::/48。
const TAILSCALE_IPV6 = /^fd7a:115c:a1e0:/i;

function readTailscaleNames() {
  // 从 tailscale status --json 读取本机 MagicDNS 名与 IP；不可用时静默降级。
  const names = new Set();
  try {
    const exe = 'C:\\Program Files\\Tailscale\\tailscale.exe';
    if (!fs.existsSync(exe)) return names;
    const { execFileSync } = require('node:child_process');
    const raw = execFileSync(exe, ['status', '--json'], {
      encoding: 'utf8', timeout: 4000, windowsHide: true,
    });
    const data = JSON.parse(raw);
    const self = data.Self || {};
    if (self.DNSName) names.add(String(self.DNSName).replace(/\.$/, '').toLowerCase());
    if (self.HostName) names.add(String(self.HostName).toLowerCase());
    for (const ip of data.TailscaleIPs || []) names.add(String(ip).toLowerCase());
  } catch { /* 读不到就只用静态规则 */ }
  return names;
}

let cachedNames = null;
function tailscaleNames() {
  if (!cachedNames) cachedNames = readTailscaleNames();
  return cachedNames;
}

function isTailscaleHost(hostname) {
  const h = String(hostname || '').toLowerCase();
  if (!h) return false;
  if (TAILSCALE_IPV6.test(h)) return true;
  const m = TAILSCALE_CGNAT.exec(h);
  if (m) {
    const a = Number(m[1]);
    // 100.64.0.0/10 即第二段 64~127。
    if (a >= 64 && a <= 127) return true;
    return false;
  }
  return tailscaleNames().has(h);
}

// 主机名是否属于本机（localhost / 127.0.0.1 / 本机名）。
function isLocalHost(hostname) {
  const h = String(hostname || '').toLowerCase();
  if (h === 'localhost' || h === '127.0.0.1' || h === '::1' || h === '[::1]') return true;
  if (h.endsWith('.localhost')) return true;
  return false;
}

/**
 * 判断请求来源是否属于「可信本机」。
 * 用于：能打开资源管理器、导出快照、写本机文件等仅限本机的接口。
 */
function isTrustedLocal(req) {
  const host = String(req.headers.host || '').toLowerCase();
  // Host 形如 127.0.0.1:3900 或 kaede.tail34f007.ts.net
  const withoutPort = host.startsWith('[')
    ? host.slice(0, host.indexOf(']') + 1)
    : host.split(':')[0];
  return isLocalHost(withoutPort);
}

/**
 * 判断请求是否来自允许远程访问的来源（手机经 Tailscale）。
 * 用于：列表读取、图片预览、审阅标注保存等无副作用接口。
 */
function isRemoteAllowed(req) {
  const host = String(req.headers.host || '').toLowerCase();
  const withoutPort = host.startsWith('[')
    ? host.slice(0, host.indexOf(']') + 1)
    : host.split(':')[0];
  if (isLocalHost(withoutPort)) return true;
  return isTailscaleHost(withoutPort);
}

/**
 * Origin 校验：远程来源允许 http/https 自家源，本机仍要求严格同源。
 * 远程时 Origin 可能带 https（经 Tailscale Serve），本机时是 http。
 */
function originMatches(req) {
  const origin = req.headers.origin;
  if (!origin) return false;
  const host = String(req.headers.host || '').toLowerCase();
  if (origin === `http://${host}` || origin === `https://${host}`) return true;
  // 经 Tailscale Serve 时浏览器 Origin 为 https://kaede.tail34f007.ts.net，
  // 而 Host 头可能带端口或不带，两边都试一次。
  const bare = host.replace(/:\d+$/, '');
  return origin === `https://${bare}` || origin === `http://${bare}`;
}

module.exports = {
  isLocalHost,
  isTailscaleHost,
  isTrustedLocal,
  isRemoteAllowed,
  originMatches,
  tailscaleNames,
};
