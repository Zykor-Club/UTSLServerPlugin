#!/usr/bin/env node
/**
 * RuYouMusic 取链网关（零依赖）
 *
 *   对外：GET /api.php?types=search|url|lyric|pic&...   ← 和公共接口同形，服务端插件直接 apiBase 指过来即可
 *   后端：
 *     mode=public  —— 直连公共 API（music-api.gdstudio.xyz）
 *     mode=browser —— 用本机 Edge 无头模式打开 https://music.gdstudio.org/，
 *                     在页面里用它自己的 crc32 + 自己的会话来调它的同源 api.php
 *                     （这样能过 Cloudflare，也能拿到它后端才有能力解析的直链）
 *
 *   用法：node gateway.js [--port 7880] [--mode browser|public|auto] [--edge <path>]
 *   说明：auto = 先试 browser，失败回退 public。
 */
const http = require("http");
const { spawn } = require("child_process");
const os = require("os");
const path = require("path");
const fs = require("fs");

const args = process.argv.slice(2);
const argOf = (k, d) => { const i = args.indexOf("--" + k); return i >= 0 && args[i + 1] ? args[i + 1] : d; };
const PORT = parseInt(argOf("port", "7880"), 10);
const MODE = argOf("mode", "auto");
const PUBLIC_API = argOf("publicApi", "https://music-api.gdstudio.xyz/api.php");
const SITE = argOf("site", "https://music.gdstudio.org/");
const EDGE = argOf("edge", "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe");
const CDP_PORT = PORT + 1;
const cache = new Map();          // key -> { at, ttl, body }
const CACHE_TTL = { search: 5 * 60e3, url: 12 * 60e3, lyric: 60 * 60e3, pic: 60 * 60e3, autosource: 5 * 60e3 };
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

// ---------------- CDP 瘦客户端 ----------------
class Cdp {
  constructor(wsUrl) { this.wsUrl = wsUrl; this.id = 0; this.pending = new Map(); this.ws = null; }
  connect() {
    return new Promise((res, rej) => {
      const ws = new WebSocket(this.wsUrl);
      this.ws = ws;
      ws.onopen = () => res();
      ws.onerror = (e) => rej(new Error("WebSocket 错误"));
      ws.onmessage = (ev) => {
        let m; try { m = JSON.parse(ev.data); } catch { return; }
        if (m.id && this.pending.has(m.id)) {
          const { res, rej } = this.pending.get(m.id); this.pending.delete(m.id);
          m.error ? rej(new Error(m.error.message)) : res(m.result);
        }
      };
      ws.onclose = () => { for (const { rej } of this.pending.values()) rej(new Error("CDP 断开")); this.pending.clear(); };
    });
  }
  send(method, params = {}, sessionId) {
    const id = ++this.id;
    const msg = { id, method, params };
    if (sessionId) msg.sessionId = sessionId;
    return new Promise((res, rej) => {
      this.pending.set(id, { res, rej });
      this.ws.send(JSON.stringify(msg));
      setTimeout(() => { if (this.pending.has(id)) { this.pending.delete(id); rej(new Error(method + " 超时")); } }, 30000);
    });
  }
  close() { try { this.ws && this.ws.close(); } catch { } }
}

// ---------------- 浏览器会话 ----------------
let browser = null;      // { proc, cdp, sessionId, ready }
let browserBusy = Promise.resolve();

async function httpGetJson(url) {
  const r = await fetch(url);
  return await r.json();
}

async function launchBrowser() {
  if (browser && browser.ready) return browser;
  if (browser && browser.proc) { try { browser.proc.kill(); } catch { } }
  const userDataDir = path.join(os.tmpdir(), "ruyoumusic-gateway-" + process.pid);
  log("启动无头 Edge…");
  const proc = spawn(EDGE, [
    "--headless=new",
    "--remote-debugging-port=" + CDP_PORT,
    "--user-data-dir=" + userDataDir,
    "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--mute-audio",
    "--window-size=900,600",
    "about:blank"
  ], { stdio: "ignore", detached: false });
  proc.on("exit", (c) => { log("Edge 退出", c); if (browser) browser.ready = false; });

  let ver = null;
  for (let i = 0; i < 60; i++) {
    await new Promise(r => setTimeout(r, 500));
    try { ver = await httpGetJson("http://127.0.0.1:" + CDP_PORT + "/json/version"); break; } catch { }
  }
  if (!ver) throw new Error("无法连接无头 Edge（调试端口 " + CDP_PORT + "）");
  const cdp = new Cdp(ver.webSocketDebuggerUrl);
  await cdp.connect();
  const t = await cdp.send("Target.createTarget", { url: SITE });
  const at = await cdp.send("Target.attachToTarget", { targetId: t.targetId, flatten: true });
  const sessionId = at.sessionId;
  await cdp.send("Runtime.enable", {}, sessionId);
  // 等页面的 crc32 就绪（站点自己的 JS）
  let ready = false;
  for (let i = 0; i < 60; i++) {
    await new Promise(r => setTimeout(r, 500));
    try {
      const r = await cdp.send("Runtime.evaluate", { expression: "typeof crc32", returnByValue: true }, sessionId);
      if (r && r.result && r.result.value === "function") { ready = true; break; }
    } catch { }
  }
  if (!ready) throw new Error("页面里的 crc32 未就绪（站点是否可访问？）");
  log("浏览器会话就绪（页面 crc32 可用）");
  browser = { proc, cdp, sessionId, ready: true };
  return browser;
}

/** 在页面里调它的同源 api.php（签名用页面自己的 crc32） */
async function browserApi(params) {
  const b = await launchBrowser();
  const q = Object.fromEntries(params.entries());
  q.__sign = params.get("__sign") || "raw";
  params = new URLSearchParams(params); params.delete("__sign");
  const expr = "(async()=>{try{" +
    "const enc=(typeof urlEncode==='function')?urlEncode:encodeURIComponent;" +
    "const q=" + JSON.stringify(q) + ";" +
    "const subject=(q.types==='url'||q.types==='lyric'||q.types==='pic')?enc(String(q.id||'')):((q.types==='search')?String(q.name||''):String(q.id||q.name||''));" +
    "const s=crc32(String(q.__sign==='enc'?enc(String(subject)):String(subject)));" +
    "const us=new URLSearchParams(q); us.set('s',s);" +
    "const r=await fetch('/api.php?'+us.toString(),{credentials:'include'});" +
    "return JSON.stringify({status:r.status,body:await r.text()});" +
    "}catch(e){return JSON.stringify({status:-1,body:String(e)});}})()";
  const r = await b.cdp.send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }, b.sessionId);
  if (r.exceptionDetails) throw new Error("页面执行异常: " + (r.exceptionDetails.text || ""));
  const out = JSON.parse(r.result.value);
  if (out.status !== 200) throw new Error("站点 API HTTP " + out.status + " " + String(out.body).slice(0, 80));
  return out.body;
}

async function publicApi(params) {
  const url = PUBLIC_API + "?" + params.toString();
  const r = await fetch(url, { headers: { "User-Agent": "Mozilla/5.0" } });
  const t = await r.text();
  if (!r.ok) throw new Error("公共 API HTTP " + r.status);
  return t;
}

// ---------------- HTTP 服务 ----------------
const server = http.createServer(async (req, res) => {
  const u = new URL(req.url, "http://127.0.0.1:" + PORT);
  const send = (code, body, type = "application/json; charset=utf-8") => { res.writeHead(code, { "Content-Type": type }); res.end(body); };
  if (u.pathname === "/health") return send(200, JSON.stringify({ ok: true, mode: MODE, browserReady: !!(browser && browser.ready), cache: cache.size }));
  if (u.pathname !== "/api.php") return send(404, JSON.stringify({ detail: "not found" }));

  const params = u.searchParams;
  params.delete("mode");
  const types = params.get("types") || "url";
  const mode = u.searchParams.get("mode") || MODE;
  const key = types + "?" + params.toString();
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < hit.ttl) { res.setHeader("X-Gateway-Cache", "HIT"); return send(200, hit.body); }

  // 浏览器模式是串行的（一个页面），排队执行
  const run = async () => {
    let body = null, err1 = null;
    if (mode === "browser" || mode === "auto") {
      try { body = await browserApi(params); } catch (e) { err1 = e; if (mode === "browser") throw e; log("browser 模式失败，回退 public:", e.message); }
    }
    if (body === null) { body = await publicApi(params); }
    return body;
  };
  try {
    const body = await (browserBusy = browserBusy.then(run, run));
    cache.set(key, { at: Date.now(), ttl: CACHE_TTL[types] ?? 5 * 60e3, body });
    res.setHeader("X-Gateway-Cache", "MISS");
    send(200, body);
  } catch (e) {
    log("请求失败", types, e.message);
    send(502, JSON.stringify({ detail: "gateway error: " + e.message }));
  }
});
server.listen(PORT, "127.0.0.1", () => log("取链网关已启动 http://127.0.0.1:" + PORT + "/api.php  模式=" + MODE));
process.on("SIGINT", () => { try { browser && browser.proc && browser.proc.kill(); } catch { } process.exit(0); });
