#!/usr/bin/env node
/*
 * AUM 真实升级端到端验证（不联网 mock，走真实 GitHub Release）
 *
 *   node tools/e2e-aum-update.js --exe="C:\...\AUM管理组件\ServerUI-有依赖版.exe" --from=2.20
 *
 * 流程：
 *   1. 离屏启动旧版（AUM_OFFSCREEN=1 + CDP）
 *   2. aum.plan（只读规划）→ 断言目标版本 > 当前版本、策略与资产合法
 *   3. aum.update → 等待进程退出/替换
 *   4. 校验 exe 文件版本已变为目标版本、备份 .bak 存在
 *
 * 注意：这会真实下载发行包并替换 --exe 指向的文件。只对沙箱副本使用。
 */
const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const net = require('net');
const { spawn } = require('child_process');

const flags = {};
{
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    const m = argv[i].match(/^--([^=]+)(?:=(.*))?$/);
    if (!m) continue;
    if (m[2] !== undefined) flags[m[1]] = m[2];
    else if (argv[i + 1] && !argv[i + 1].startsWith('--')) flags[m[1]] = argv[++i];
    else flags[m[1]] = true;
  }
}
const EXE = flags.exe;
const FROM = flags.from || '';
const FORCE_PKG = flags['force-package'] === true;
if (!EXE || !fs.existsSync(EXE)) {
  console.error('用法: node tools/e2e-aum-update.js --exe="<沙箱里的旧版 exe>" [--from=2.20]');
  process.exit(2);
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const report = [];
const say = (s) => { report.push(s); console.log(s); };
const fail = (s) => { report.push('!! ' + s); console.log('!! ' + s); };

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.unref();
    s.on('error', reject);
    s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => resolve(p)); });
  });
}
function listTargets(port) {
  return new Promise((resolve, reject) => {
    const req = http.get({ host: '127.0.0.1', port, path: '/json/list' }, (res) => {
      let b = ''; res.on('data', (c) => (b += c));
      res.on('end', () => { try { resolve(JSON.parse(b)); } catch (e) { reject(e); } });
    });
    req.on('error', reject);
    req.setTimeout(1500, () => req.destroy(new Error('timeout')));
  });
}
async function waitTarget(port, timeoutMs) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    try {
      const list = await listTargets(port);
      const p = list.find((t) => t.type === 'page' && String(t.url || '').indexOf('app.serverui') >= 0);
      if (p) return p;
    } catch { }
    await wait(200);
  }
  throw new Error('等待 WebView2 CDP 目标超时');
}
async function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  await new Promise((res, rej) => {
    ws.addEventListener('open', res, { once: true });
    ws.addEventListener('error', rej, { once: true });
  });
  let id = 0; const pending = new Map();
  ws.addEventListener('message', (ev) => {
    let m; try { m = JSON.parse(ev.data); } catch { return; }
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  });
  const send = (method, params = {}) => new Promise((resolve) => {
    const mid = ++id; pending.set(mid, resolve);
    ws.send(JSON.stringify({ id: mid, method, params }));
  });
  const evaluate = async (expression) => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true });
    if (r.result && r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails).slice(0, 300));
    return r.result && r.result.result ? r.result.result.value : null;
  };
  return { send, evaluate, close: () => ws.close() };
}

(async () => {
  const port = await freePort();
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'aum-e2e-'));
  const dataRoot = path.join(tmp, 'data');
  const wv = path.join(tmp, 'wv');
  fs.mkdirSync(dataRoot, { recursive: true });
  fs.mkdirSync(wv, { recursive: true });

  const exeDir = path.dirname(EXE);
  const bakBefore = fs.readdirSync(exeDir).filter((f) => f.indexOf('.bak-') >= 0);

  const env = { ...process.env };
  env.AUM_OFFSCREEN = '1';
  env.AUM_CDP_PORT = String(port);
  env.AUM_DATA_ROOT = dataRoot;
  env.AUM_WEBVIEW_DATA = wv;
  delete env.AUM_WEB_ROOT;
  delete env.AUM_ALLOW_AUM_DOWNGRADE;

  say('exe: ' + EXE);
  say('sandbox dir: ' + exeDir);
  say('from: ' + (FROM || '(unknown)'));

  const proc = spawn(EXE, [], { env, cwd: exeDir, stdio: 'ignore', windowsHide: true });
  let exited = false;
  proc.on('exit', () => { exited = true; });

  let cdp = null;
  try {
    const page = await waitTarget(port, 30000);
    cdp = await connect(page.webSocketDebuggerUrl);
    await cdp.send('Runtime.enable');

    let ready = false;
    for (let i = 0; i < 120; i++) {
      try {
        ready = await cdp.evaluate("!document.getElementById('splash') && !!document.querySelector('.nav-item')");
        if (ready) break;
      } catch { }
      await wait(200);
    }
    if (!ready) throw new Error('界面未初始化');
    say('ui ready');

    const probe = (method, args) => cdp.evaluate(`new Promise((resolve) => {
      const id = 'e2e_' + Math.random().toString(36).slice(2);
      const timer = setTimeout(() => { cleanup(); resolve({ timeout: true }); }, 120000);
      function on(ev) {
        let m = ev.data;
        if (typeof m === 'string') { try { m = JSON.parse(m); } catch { return; } }
        if (m && m.id === id) { cleanup(); resolve(m); }
      }
      function cleanup() { clearTimeout(timer); try { chrome.webview.removeEventListener('message', on); } catch (e) {} }
      chrome.webview.addEventListener('message', on);
      chrome.webview.postMessage(JSON.stringify({ id, method: ${JSON.stringify(method)}, args: ${JSON.stringify(args || {})} }));
    })`);

    // ---------------- 规划
    say('calling aum.plan (forcePackage=' + FORCE_PKG + ')…');
    const planResp = await probe('aum.plan', { forcePackage: FORCE_PKG });
    const plan = planResp && planResp.result;
    say('plan: ' + JSON.stringify(plan).slice(0, 600));
    if (!plan) fail('aum.plan 无返回');
    else {
      if (plan.ok === false) fail('aum.plan 失败: ' + plan.error);
      else {
        if (plan.upToDate) fail('已经是最新版本，无法验证升级');
        const cmp = (a, b) => {
          const pa = String(a).split('.').map(Number), pb = String(b).split('.').map(Number);
          for (let i = 0; i < 3; i++) { const x = pa[i] || 0, y = pb[i] || 0; if (x !== y) return x < y ? -1 : 1; }
          return 0;
        };
        if (cmp(plan.target, plan.current) <= 0) fail('目标版本未高于当前版本: ' + plan.current + ' -> ' + plan.target);
        if (['compile', 'package'].indexOf(plan.strategy) < 0) fail('策略非法: ' + plan.strategy);
        if (!plan.strategyLabel || !plan.reason) fail('缺少策略说明');
        if (plan.strategy === 'package' && !(plan.packageBytes > 0)) fail('发行包策略但未给出包大小');
        say('plan OK: ' + plan.current + ' -> ' + plan.target + ' via ' + plan.strategy);
      }
    }

    // ---------------- 执行
    say('calling aum.update…（真实下载 + 替换，可能需要数分钟）');
    const updResp = await probe('aum.update', { forcePackage: FORCE_PKG });
    const upd = updResp && updResp.result;
    say('update: ' + JSON.stringify(upd).slice(0, 600));
    if (!upd) fail('aum.update 无返回');
    else if (upd.ok === false && !upd.cancelled) fail('aum.update 失败: ' + upd.error);

    // ---------------- 等替换/重启
    say('waiting for process to exit / restart…');
    const deadline = Date.now() + 120000;
    while (Date.now() < deadline && !exited) await wait(500);
    say('original process exited: ' + exited);
    await wait(4000);
  } finally {
    try { if (cdp) cdp.close(); } catch { }
    await wait(300);
    if (!exited) {
      try { spawn('taskkill', ['/PID', String(proc.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true }); } catch { }
      await wait(800);
    }
  }

  // ---------------- 校验结果（用系统工具读版本，不依赖被测进程）
  const exeVer = require('child_process')
    .execSync(`powershell -NoProfile -Command "(Get-Item '${EXE.replace(/'/g, "''")}').VersionInfo.FileVersion"`,
      { encoding: 'utf8' }).trim();
  say('final exe FileVersion: ' + exeVer);

  const baks = fs.readdirSync(exeDir).filter((f) => f.indexOf('.bak-') >= 0 && bakBefore.indexOf(f) < 0);
  say('new backups: ' + JSON.stringify(baks));

  if (FROM && exeVer.indexOf(FROM) === 0) fail('exe 版本仍是 ' + exeVer + '，替换未发生');
  if (!baks.length) fail('未生成 .bak 备份');
  if (/^0\.0|^\s*$/.test(exeVer)) fail('读不到 exe 版本');

  fs.writeFileSync(path.join(os.tmpdir(), 'aum-e2e-report.txt'), report.join('\n') + '\n', 'utf8');
  const fails = report.filter((l) => l.startsWith('!!'));
  console.log('\n结果：' + fails.length + ' 项失败');
  process.exitCode = fails.length ? 1 : 0;
})().catch((err) => {
  console.error(err && err.stack || err);
  process.exitCode = 1;
});
