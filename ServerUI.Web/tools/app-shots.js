#!/usr/bin/env node
/*
 * 真实宿主离屏验收（WPF + WebView2 + 真实 IPC）
 *
 *   node tools/app-shots.js --exe=bin/Release/net10.0-windows/win-x64/ServerUI.exe --out=../_ui-report
 *
 * 启动参数（全部只影响本次进程）：
 *   AUM_OFFSCREEN=1     窗口放到屏幕外、1% 不透明、不抢焦点（MainWindow 内实现）
 *   AUM_CDP_PORT        开启 WebView2 CDP
 *   AUM_DATA_ROOT       设置写到临时目录，不污染 AUM管理组件\设置.json
 *   AUM_WEBVIEW_DATA    WebView2 profile 写到临时目录，避免与真实实例抢锁
 *
 * 只做只读 IPC（status/archive.list/dll.state/fs.drives/time.get）+ 视图/主题/模式切换；
 * 绝不启动/停止服务端、不更新、不写系统时间、不安装补丁。服务端若正在运行，
 * 退出时直接结束测试进程树，不调用 app.quit（避免清理逻辑动到真实服务端）。
 * 输出 host-report.txt；出现 !! 即不通过。
 */
const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const net = require('net');
const { spawn } = require('child_process');

const ROOT = path.resolve(__dirname, '..');
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
const EXE = path.resolve(flags.exe || path.join(ROOT, 'bin', 'Release', 'net10.0-windows', 'win-x64', 'ServerUI.exe'));
const OUT = path.resolve(flags.out || path.join(ROOT, '..', '_ui-report'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
fs.mkdirSync(OUT, { recursive: true });

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
      let body = '';
      res.on('data', (c) => (body += c));
      res.on('end', () => { try { resolve(JSON.parse(body)); } catch (e) { reject(e); } });
    });
    req.on('error', reject);
    req.setTimeout(1500, () => req.destroy(new Error('timeout')));
  });
}

async function waitTarget(port, appUrl, timeoutMs) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    try {
      const list = await listTargets(port);
      const page = list.find((t) => t.type === 'page' && String(t.url || '').indexOf(appUrl) === 0);
      if (page) return page;
    } catch { }
    await wait(200);
  }
  throw new Error('等待 WebView2 CDP 目标超时（' + appUrl + '）');
}

async function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  await new Promise((res, rej) => {
    ws.addEventListener('open', res, { once: true });
    ws.addEventListener('error', rej, { once: true });
  });
  let id = 0;
  const pending = new Map();
  const consoleErrors = [];
  ws.addEventListener('message', (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); return; }
    if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') {
      const args = (msg.params.args || []).map((a) => a.value !== undefined ? a.value : (a.description || a.type));
      consoleErrors.push(args.map(String).join(' '));
    } else if (msg.method === 'Runtime.exceptionThrown') {
      const d = msg.params.exceptionDetails || {};
      const text = (d.exception && (d.exception.description || d.exception.value)) || d.text || '';
      consoleErrors.push(String(text));
    } else if (msg.method === 'Log.entryAdded') {
      const e = msg.params.entry || {};
      if (e.level === 'error') consoleErrors.push(String(e.text || ''));
    }
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const mid = ++id;
    pending.set(mid, (m) => {
      if (m.error) reject(new Error(method + ': ' + JSON.stringify(m.error)));
      else resolve(m.result);
    });
    ws.send(JSON.stringify({ id: mid, method, params }));
  });
  const evaluate = async (expression) => {
    const r = await send('Runtime.evaluate', {
      expression, returnByValue: true, awaitPromise: true, userGesture: true
    });
    if (r.exceptionDetails) {
      const d = r.exceptionDetails;
      throw new Error((d.exception && (d.exception.description || d.exception.value)) || d.text || 'evaluate failed');
    }
    return r.result ? r.result.value : null;
  };
  return { send, evaluate, close: () => ws.close(), consoleErrors };
}

async function main() {
  if (!fs.existsSync(EXE)) throw new Error('找不到 exe：' + EXE);
  const port = await freePort();
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'serverui-host-'));
  const dataRoot = path.join(tmp, 'data');
  const wvData = path.join(tmp, 'webview2');
  fs.mkdirSync(dataRoot, { recursive: true });
  fs.mkdirSync(wvData, { recursive: true });

  // ---- 沙箱游戏根：把 exe 复制到 <tmp>/AUM管理组件/ServerUI.Web/bin，令 Paths.Ad/Gr 指向临时目录，
  // 再铺一个最小 GameGaurd.ini（8 受管 + 2 自定义），让 DLL 页在真实宿主里也有可拖拽的行。
  // 全程只写临时目录，绝不碰真实游戏根。
  const sandbox = path.join(tmp, 'game');
  const sandboxAd = path.join(sandbox, 'AUM管理组件');
  const sandboxBin = path.join(sandboxAd, 'ServerUI.Web', 'bin');
  fs.mkdirSync(sandboxBin, { recursive: true });
  // 宿主推导：_ad = exe 目录（其下无 "AUM管理组件" 子目录），_gr = _ad 的父目录 → ini/插件放这里
  const sandboxGr = path.dirname(sandboxBin);
  const exeDir = path.dirname(EXE);
  // 只复制 exe 本身（发布物是单文件）。不能递归复制 exe 目录：
  // 部署版 exe 位于游戏根，递归会把整个游戏（含 AUM管理组件）复制进沙箱，
  // 令 Paths.Ad 多穿一层 → GameGaurd.ini 写到错误位置、DLL 页空态。
  const shallowCopy = (src, dst) => {
    fs.mkdirSync(dst, { recursive: true });
    for (const ent of fs.readdirSync(src, { withFileTypes: true })) {
      if (!ent.isFile()) continue;
      if (!/\.(exe|dll|json)$/i.test(ent.name)) continue;
      fs.copyFileSync(path.join(src, ent.name), path.join(dst, ent.name));
    }
  };
  shallowCopy(exeDir, sandboxBin);
  const SANDBOX_EXE = path.join(sandboxBin, path.basename(EXE));
  const managedPlugins = ['GameNative.dll', 'PreventMinimize.dll', 'MultiInstance.dll', 'AutoFire.dll',
    'EquipmentSwap.dll', 'DpsMeter.dll', 'CombatPower.dll', 'ImeFix.dll'];
  const iniLines = ['[Plugins]'];
  managedPlugins.forEach((f, i) => iniLines.push('Plugin' + (i + 1) + '=' + f));
  iniLines.push('Plugin9=DfoVibrationS4A21.dll');
  iniLines.push('Plugin10=S4A21MemOpt.dll');
  fs.writeFileSync(path.join(sandboxGr, 'GameGaurd.ini'), iniLines.join('\r\n') + '\r\n', 'utf8');
  // 挂载器 + 插件 DLL 都放成空文件：installed=true，exists=true
  for (const f of ['GameGaurd.dll', ...managedPlugins, 'DfoVibrationS4A21.dll', 'S4A21MemOpt.dll']) {
    fs.writeFileSync(path.join(sandboxGr, f), '');
  }

  const env = { ...process.env };
  delete env.AUM_WEB_ROOT;
  delete env.AUM_WEBVIEW_ARGS;
  delete env.AUM_DEVTOOLS;
  env.AUM_OFFSCREEN = '1';
  env.AUM_CDP_PORT = String(port);
  env.AUM_DATA_ROOT = dataRoot;
  env.AUM_WEBVIEW_DATA = wvData;

  say('exe: ' + EXE);
  say('sandbox: ' + sandbox);
  const proc = spawn(SANDBOX_EXE, [], { env, cwd: path.dirname(SANDBOX_EXE), stdio: 'ignore', windowsHide: true });
  let exited = false;
  proc.on('exit', () => { exited = true; });

  let cdp = null;
  try {
    const page = await waitTarget(port, 'https://app.serverui/', 25000);
    say('target: ' + page.url);
    cdp = await connect(page.webSocketDebuggerUrl);
    await cdp.send('Page.enable');
    await cdp.send('Runtime.enable');
    await cdp.send('Log.enable');

    const shot = async (name) => {
      // 截图前清掉残留 tooltip（鼠标停在手柄上会留下自绘提示，纯属截图噪声）
      try {
        await cdp.evaluate("document.querySelectorAll('.tooltip').forEach((t) => t.remove())");
        await wait(80);
      } catch { }
      const r = await cdp.send('Page.captureScreenshot', { format: 'png', fromSurface: true });
      fs.writeFileSync(path.join(OUT, name + '.png'), Buffer.from(r.data, 'base64'));
    };

    // 等 splash 移除（app.init 完成）
    let ready = false;
    for (let i = 0; i < 100; i++) {
      try {
        ready = await cdp.evaluate("!document.getElementById('splash') && !!document.querySelector('.nav-item')");
        if (ready) break;
      } catch { }
      await wait(200);
    }
    if (!ready) {
      try {
        say('splashSub: ' + await cdp.evaluate("(document.getElementById('splashSub') || {}).textContent || ''"));
        say('bodyText: ' + String(await cdp.evaluate("document.body.innerText")).slice(0, 800).split(String.fromCharCode(10)).join(' | '));
        say('hasBridge: ' + await cdp.evaluate("!!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage)"));
        await shot('host-fail');
      } catch (e) { say('diag failed: ' + e.message); }
      say('consoleErrors: ' + JSON.stringify(cdp.consoleErrors));
      throw new Error('前端未完成初始化（splash 未移除）');
    }
    say('ready: true');

    // 等启动期状态轮询/首帧布局稳定，再量真实切页耗时
    await wait(1200);
    await cdp.evaluate("new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))");

    // 单实例：同 profile 再启动一次，第二个应立即退出，第一个不受影响
    const second = spawn(SANDBOX_EXE, [], { env, cwd: path.dirname(SANDBOX_EXE), stdio: 'ignore', windowsHide: true });
    let secondExited = false;
    second.on('exit', () => { secondExited = true; });
    for (let i = 0; i < 30 && !secondExited; i++) await wait(200);
    say('single-instance: secondExited=' + secondExited + ' firstAlive=' + !exited);
    if (!secondExited) {
      fail('第二实例未退出（单实例锁失效）');
      try { spawn('taskkill', ['/PID', String(second.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true }); } catch { }
    }
    if (exited) fail('第二实例导致第一个实例退出');

    const viewport = () => cdp.evaluate("({w: innerWidth, h: innerHeight, dpr: devicePixelRatio})");

    // ---------------------------------------------------------------- 真实 IPC（只读）
    async function probe(method, args) {
      return cdp.evaluate(`new Promise((resolve) => {
        const id = 'probe_' + Math.random().toString(36).slice(2);
        const timer = setTimeout(() => { cleanup(); resolve({ timeout: true, method: ${JSON.stringify(method)} }); }, 6000);
        function on(ev) {
          let m = ev.data;
          if (typeof m === 'string') { try { m = JSON.parse(m); } catch { return; } }
          if (m && m.id === id) { cleanup(); resolve(m); }
        }
        function cleanup() {
          clearTimeout(timer);
          try { chrome.webview.removeEventListener('message', on); } catch (e) { }
        }
        chrome.webview.addEventListener('message', on);
        chrome.webview.postMessage(JSON.stringify({ id: id, method: ${JSON.stringify(method)}, args: ${JSON.stringify(args || {})} }));
      })`);
    }

    const probes = {};
    for (const [name, method] of [
      ['status', 'server.status'],
      ['archive', 'archive.list'],
      ['dll', 'dll.state'],
      ['drives', 'fs.drives'],
      ['time', 'time.get'],
      ['mode', 'window.mode'],
      ['settings', 'settings.get']
    ]) {
      const r = await probe(method);
      probes[name] = r;
      say('probe ' + method + ': ' + JSON.stringify(r).slice(0, 500));
      if (!r || r.timeout || !r.ok) fail('IPC ' + method + ' 未成功');
    }
    // AUM 双通道：只验证「离线时的行为正确」，绝不在这里真的触发更新/替换。
    //   预期：要么 upToDate，要么 ok=false 且错误信息里能看出网络/无 Release 的原因；
    //   绝不允许在无 Release 时假装成功（silent success 会误导用户以为已更新）。
    const aumProbe = await probe('aum.update');
    say('probe aum.update: ' + JSON.stringify(aumProbe).slice(0, 400));
    if (!aumProbe || aumProbe.timeout || !aumProbe.result) {
      fail('aum.update 无响应');
    } else {
      const r = aumProbe.result;
      const legit = r.ok === true || r.ok === false;
      if (!legit) fail('aum.update 返回结构异常: ' + JSON.stringify(r).slice(0, 200));
      if (r.ok === false && !r.error) fail('aum.update 失败但未给出可读原因');
    }

    const serverRunning = !!(probes.status && probes.status.result && (probes.status.result.bat || probes.status.result.dfo));
    say('serverRunning: ' + serverRunning);
    if (probes.archive && probes.archive.result && Array.isArray(probes.archive.result.entries)) {
      say('archive.entries: ' + probes.archive.result.entries.length + ' current=' + probes.archive.result.current);
    }

    // ---------------------------------------------------------------- 页面截图 + 输入护栏
    const navs = ['dash', 'archive', 'dll', 'time', 'settings', 'help', 'tsh'];
    const navCount = await cdp.evaluate("document.querySelectorAll('.nav-item').length");
    say('navCount: ' + navCount);
    if (navCount !== 7) fail('导航项应为 7，实际 ' + navCount);

    for (const id of navs) {
      // 真实切页耗时：click 后两个 rAF（不含测试等待/往返）
      const switchMs = await cdp.evaluate(`new Promise((resolve) => {
        const t0 = performance.now();
        document.querySelector('[data-nav="${id}"]').click();
        requestAnimationFrame(() => requestAnimationFrame(() => resolve(Math.round(performance.now() - t0))));
      })`);
      await wait(120);
      const info = await cdp.evaluate(`(() => ({
        nav: '${id}',
        title: (document.querySelector('.page-title') || {}).textContent || '',
        nativeControls: document.querySelectorAll('input[type="date"], input[type="datetime-local"], select').length,
        ctxPrevented: (() => { const ev = new MouseEvent('contextmenu', { bubbles: true, cancelable: true }); document.body.dispatchEvent(ev); return ev.defaultPrevented; })(),
        keyPrevented: (() => { const ev = new KeyboardEvent('keydown', { key: 'F12', bubbles: true, cancelable: true }); document.body.dispatchEvent(ev); return ev.defaultPrevented; })()
      }))()`);
      say('page ' + id + ': ' + JSON.stringify(info) + ' switch=' + switchMs + 'ms');
      if (!info.title) fail(id + ' 页标题为空');
      if (!(switchMs >= 0 && switchMs < 100)) fail(id + ' 切页耗时异常：' + switchMs + 'ms');
      if (info.nativeControls !== 0) fail(id + ' 页出现原生日期/下拉控件：' + info.nativeControls);
      if (!info.ctxPrevented) fail(id + ' 页 contextmenu 未被拦截');
      await shot('host-' + id + '-dark@1280x840');
    }

    // 真实鼠标拖拽 DLL 行（Pointer Events 路径；绝不用 Input.dispatchDragEvent）
    // 手柄按下 → 多次 mouseMoved（每次间隔 30ms）→ mouseReleased。
    await cdp.evaluate("document.querySelector('[data-nav=\"dll\"]').click()");
    await wait(600);
    const dllBefore = await cdp.evaluate("Array.from(document.querySelectorAll('.dll-group[data-group=\"managed\"] .dll-row')).map(r => r.dataset.file)");
    const dllGrips = await cdp.evaluate(`(() => {
      const rows = Array.from(document.querySelectorAll('.dll-group[data-group="managed"] .dll-row'));
      const first = rows[0];
      const g = rows[1] ? rows[1].querySelector('.dll-grip') : null;
      return {
        rows: rows.length,
        first: first ? first.dataset.file : null,
        firstHasGrip: first ? !!first.querySelector('.dll-grip:not(.grip-none)') : null,
        grip: g ? (() => { const b = g.getBoundingClientRect(); return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) }; })() : null
      };
    })()`);
    say('dll-grips: ' + JSON.stringify({ rows: dllGrips.rows, first: dllGrips.first, firstHasGrip: dllGrips.firstHasGrip }));
    if (dllGrips.first !== 'GameNative.dll') fail('DLL 首行不是 GameNative: ' + dllGrips.first);
    if (dllGrips.firstHasGrip !== false) fail('GameNative 行不应有拖拽手柄');
    if (!dllGrips.grip) fail('DLL 拖拽手柄未找到（受管组第 2 行）');
    else {
      // 统计 dll.setOrder 实际调用次数（包一层 postMessage，不改 bridge 行为）
      await cdp.evaluate(`(() => {
        if (window.__dllOrderCalls !== undefined) { window.__dllOrderCalls = 0; return; }
        window.__dllOrderCalls = 0;
        const orig = chrome.webview.postMessage.bind(chrome.webview);
        chrome.webview.postMessage = (raw) => {
          try { const m = JSON.parse(raw); if (m && m.method === 'dll.setOrder') window.__dllOrderCalls++; } catch (e) { }
          return orig(raw);
        };
      })()`);
      const g = dllGrips.grip;
      const toY = g.y + 150;
      await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: g.x, y: g.y });
      await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: g.x, y: g.y, button: 'left', buttons: 1, clickCount: 1 });
      let midDrag = null;
      const steps = 12;
      for (let i = 1; i <= steps; i++) {
        await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: g.x, y: Math.round(g.y + (toY - g.y) * i / steps), button: 'left', buttons: 1 });
        await wait(30);
        if (i === 5) {
          midDrag = await cdp.evaluate(`(() => ({
            dragging: document.querySelectorAll('.dll-row.dragging').length,
            indicator: document.querySelectorAll('.dll-row.drop-before, .dll-row.drop-after').length,
            order: Array.from(document.querySelectorAll('.dll-group[data-group="managed"] .dll-row')).map((r) => r.dataset.file)
          }))()`);
        }
      }
      await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: g.x, y: toY, button: 'left', buttons: 0, clickCount: 1 });
      await wait(600);
      const dllAfter = await cdp.evaluate("Array.from(document.querySelectorAll('.dll-group[data-group=\"managed\"] .dll-row')).map(r => r.dataset.file)");
      const dllEnd = await cdp.evaluate(`(() => ({
        dragging: document.querySelectorAll('.dll-row.dragging').length,
        indicator: document.querySelectorAll('.dll-row.drop-before, .dll-row.drop-after').length,
        calls: window.__dllOrderCalls,
        status: (document.querySelector('[data-bind="status"]') || {}).textContent || '',
        first: (document.querySelector('.dll-group[data-group="managed"] .dll-row') || {}).dataset.file || null
      }))()`);
      say('dll-drag-native: ' + JSON.stringify({ before: dllBefore, midDrag, after: dllAfter, end: dllEnd, moved: dllBefore.join('|') !== dllAfter.join('|') }));
      if (dllBefore.join('|') === dllAfter.join('|')) fail('DLL 行拖拽未生效（真实鼠标拖拽后顺序不变）');
      if (!midDrag || midDrag.dragging !== 1) fail('真实拖拽过程中未出现 .dragging（real=' + JSON.stringify(midDrag) + '）');
      if (!midDrag || midDrag.indicator < 1) fail('真实拖拽过程中未显示插入线');
      if (dllEnd.dragging !== 0) fail('真实拖拽结束后 .dragging 未清除');
      if (dllEnd.indicator !== 0) fail('真实拖拽结束后插入线未清除');
      if (dllEnd.calls !== 1) fail('dll.setOrder 应恰好调用 1 次，实际 ' + dllEnd.calls);
      if (String(dllEnd.status).indexOf('顺序已调整（未应用）') < 0) fail('真实拖拽后状态行未提示「顺序已调整（未应用）」: ' + dllEnd.status);
      if (dllEnd.first !== 'GameNative.dll') fail('真实拖拽后 GameNative 不在首位: ' + dllEnd.first);
      await shot('host-dll-drag');
    }

    // 版本号必须与主程序版本一致（从 app.init 取，避免每次升版改测试）
    // app.init 里的 version 是程序版本（status.version 是服务端版本，不是一回事）
    const initProbe = await probe('app.init');
    const expectVer = (initProbe && initProbe.result && initProbe.result.version) || '';
    say('expectVersion: ' + expectVer);
    const verInfo = await cdp.evaluate(`(() => ({
      title: document.title,
      tbVer: (document.getElementById('tbVer') || {}).textContent || '',
      settingsVer: (() => {
        document.querySelector('[data-nav="settings"]').click();
        return null;
      })()
    }))()`);
    await wait(260);
    verInfo.settingsVer = await cdp.evaluate("(document.querySelector('.set-ver') || {}).textContent || ''");
    say('version: ' + JSON.stringify(verInfo));
    if (!expectVer) fail('无法取得主程序版本，版本一致性未验证');
    else {
      if (String(verInfo.tbVer).indexOf('v' + expectVer) < 0) fail('标题栏版本不是 v' + expectVer + ': ' + verInfo.tbVer);
      if (String(verInfo.title).indexOf('v' + expectVer) < 0) fail('document.title 版本不是 v' + expectVer + ': ' + verInfo.title);
      if (String(verInfo.settingsVer).indexOf('v' + expectVer) < 0) fail('设置页版本不是 v' + expectVer + ': ' + verInfo.settingsVer);
    }
    const verLog = await cdp.evaluate(`(() => {
      const ls = Array.from(document.querySelectorAll('.log-body .log-line')).map((e) => e.textContent);
      return ls.find((t) => t.indexOf('ServerUI 版本') >= 0) || null;
    })()`);
    say('version-log: ' + JSON.stringify(verLog));
    if (!verLog || verLog.indexOf('ServerUI 版本: ' + expectVer) < 0) fail('日志缺少「ServerUI 版本: ' + expectVer + '」: ' + verLog);

    // 主题切换（真实 settings.set 写临时 profile）
    await cdp.evaluate("document.querySelector('[data-nav=\"dash\"]').click()");
    await wait(150);
    await cdp.evaluate("document.getElementById('tbTheme').click()");
    await wait(450);
    let theme = await cdp.evaluate("document.documentElement.getAttribute('data-theme')");
    say('theme after toggle: ' + theme);
    if (theme !== 'light') fail('主题切换未生效（应为 light）');
    await shot('host-dash-light@1280x840');
    await cdp.evaluate("document.getElementById('tbTheme').click()");
    await wait(400);
    theme = await cdp.evaluate("document.documentElement.getAttribute('data-theme')");
    if (theme !== 'dark') fail('主题切回 dark 失败，实际 ' + theme);

    // ---------------------------------------------------------------- 窗口模式（真实宿主几何）
    const modeState = () => cdp.evaluate(`(() => ({
      mode: document.querySelector('.app').getAttribute('data-mode'),
      w: innerWidth, h: innerHeight,
      mini: document.getElementById('btnMini').classList.contains('active'),
      classic: document.getElementById('btnClassic').classList.contains('active'),
      sidebar: getComputedStyle(document.getElementById('sidebar')).display !== 'none',
      log: getComputedStyle(document.getElementById('logWrap')).display !== 'none'
    }))()`);

    let v = await viewport();
    say('full viewport: ' + JSON.stringify(v));
    if (v.w < 1000 || v.h < 700) fail('完整模式视口异常：' + JSON.stringify(v));

    await cdp.evaluate("document.getElementById('btnMini').click()");
    for (let i = 0; i < 50; i++) {
      const s = await modeState();
      if (s.mode === 'mini' && s.w <= 640 && s.h <= 500) break;
      await wait(100);
    }
    await wait(250);
    let mini = await modeState();
    say('mini: ' + JSON.stringify(mini));
    if (mini.mode !== 'mini') fail('未进入 mini 模式');
    if (mini.w > 640 || mini.h > 500) fail('mini 窗口尺寸异常：' + mini.w + 'x' + mini.h);
    if (mini.sidebar || mini.log) fail('mini 下侧栏/日志仍可见');
    if (!mini.mini) fail('mini 按钮未标记 active');
    await shot('host-mini-dark@' + mini.w + 'x' + mini.h);

    await cdp.evaluate("document.getElementById('btnClassic').click()");
    for (let i = 0; i < 60; i++) {
      const s = await modeState();
      if (s.mode === 'classic' && s.w >= 1270 && s.h >= 830) break;
      await wait(100);
    }
    await wait(600);
    let classic = await modeState();
    say('classic: ' + JSON.stringify(classic));
    if (classic.mode !== 'classic') fail('未进入 classic 模式');
    if (classic.w < 1270 || classic.h < 830) fail('classic 窗口尺寸异常：' + classic.w + 'x' + classic.h);
    if (classic.sidebar) fail('classic 下侧栏仍可见');
    if (!classic.log) fail('classic 下日志不可见');
    const classicInfo = await cdp.evaluate(`(() => ({
      cols: document.querySelectorAll('.cl-col').length,
      tools: !!document.getElementById('btnClUpdate'),
      rows: document.querySelectorAll('.cl-archive tbody tr').length,
      empty: !!document.querySelector('.cl-archive .arc-empty'),
      backBtn: !!document.querySelector('.cl-back'),
      overflowX: document.documentElement.scrollWidth - document.documentElement.clientWidth
    }))()`);
    say('classicInfo: ' + JSON.stringify(classicInfo));
    if (classicInfo.cols !== 2) fail('classic 两栏缺失');
    if (!classicInfo.tools) fail('classic 更新卡缺失');
    if (!classicInfo.backBtn) fail('classic 返回按钮缺失');
    if (classicInfo.overflowX > 1) fail('classic 出现横向溢出：' + classicInfo.overflowX);
    await shot('host-classic-dark@' + classic.w + 'x' + classic.h);

    await cdp.evaluate("document.getElementById('tbTheme').click()");
    await wait(450);
    await shot('host-classic-light@' + classic.w + 'x' + classic.h);
    await cdp.evaluate("document.getElementById('tbTheme').click()");
    await wait(400);

    await cdp.evaluate("document.querySelector('.cl-back').click()");
    for (let i = 0; i < 40; i++) {
      const s = await modeState();
      if (s.mode === 'full' && s.w >= 1000) break;
      await wait(100);
    }
    const full = await modeState();
    say('back full: ' + JSON.stringify(full));
    if (full.mode !== 'full') fail('未返回 full 模式');
    if (!full.sidebar || !full.log) fail('返回 full 后侧栏/日志未恢复');
    if (full.mini || full.classic) fail('返回 full 后模式按钮仍 active');
    await shot('host-full-back@' + full.w + 'x' + full.h);

    // ---------------------------------------------------------------- 关闭确认（仅在服务端未运行时真正退出）
    if (flags['no-close']) {
      say('close-test: skipped（--no-close，保留进程供外部检查）');
    } else if (!serverRunning) {
      await cdp.evaluate("document.getElementById('tbClose').click()");
      await wait(450);
      const closeModal = await cdp.evaluate(`(() => {
        const m = document.querySelector('.modal');
        return {
          open: !!m,
          title: m ? (m.querySelector('.m-title') || {}).textContent : null,
          buttons: m ? Array.from(m.querySelectorAll('.m-foot .btn')).map(b => b.textContent.trim()) : []
        };
      })()`);
      say('close-modal: ' + JSON.stringify(closeModal));
      if (!closeModal.open) fail('关闭确认模态未出现');
      await shot('host-close-confirm');
      // 先取消，确认应用仍可用
      await cdp.evaluate(`(() => {
        const b = Array.from(document.querySelectorAll('.modal .m-foot .btn')).find(x => x.textContent.trim() === '取消');
        if (b) b.click();
      })()`);
      await wait(300);
      const stillAlive = await cdp.evaluate("!!document.querySelector('.app') && !document.querySelector('.modal')");
      say('close-cancel: ' + stillAlive);
      if (!stillAlive) fail('取消关闭后应用状态异常');
      // 再关闭并确认退出
      await cdp.evaluate("document.getElementById('tbClose').click()");
      await wait(400);
      await cdp.evaluate(`(() => {
        const b = Array.from(document.querySelectorAll('.modal .m-foot .btn')).find(x => x.textContent.trim() === '退出');
        if (b) b.click();
      })()`);
      const quitEnd = Date.now() + 12000;
      while (Date.now() < quitEnd && !exited) await wait(200);
      say('close-exit: ' + exited);
      if (!exited) fail('确认退出后进程未在 12s 内结束');
      else await wait(600);

      // 设置持久化：主题恢复 dark、日志高度 190、窗口几何写回 full
      try {
        const sf = path.join(dataRoot, '设置.json');
        const saved = JSON.parse(fs.readFileSync(sf, 'utf8'));
        say('settings-saved: ' + JSON.stringify(saved));
        if (saved.theme !== 'dark') fail('退出后主题未持久化为 dark：' + saved.theme);
        if (saved.logHeight !== 190) fail('退出后日志高度异常：' + saved.logHeight);
        if (!(saved.winW >= 1270 && saved.winW <= 1300 && saved.winH >= 830 && saved.winH <= 860)) {
          fail('退出后窗口几何异常：' + saved.winW + 'x' + saved.winH);
        }
      } catch (e) { fail('读取退出后设置失败：' + e.message); }
    } else {
      say('close-test: skipped（服务端运行中，禁止走退出清理）');
    }

    say('consoleErrors: ' + JSON.stringify(cdp.consoleErrors));
    if (cdp.consoleErrors.length) fail('控制台错误 ' + cdp.consoleErrors.length + ' 条');
  } finally {
    // 退出：直接结束本次测试进程树，不调用 app.quit（服务端可能正在运行，
    // 清理逻辑会停服/杀 GM 工具；真实关闭路径另行用未运行状态单独验收）。
    try { if (cdp) cdp.close(); } catch { }
    await wait(200);
    if (!exited) {
      try { spawn('taskkill', ['/PID', String(proc.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true }); } catch { }
      await wait(800);
    }
    try { fs.rmSync(tmp, { recursive: true, force: true }); } catch { }
  }

  fs.writeFileSync(path.join(OUT, 'host-report.txt'), report.join('\n') + '\n', 'utf8');
  const fails = report.filter((l) => l.startsWith('!!'));
  console.log('\n完成：' + (report.length) + ' 行，失败 ' + fails.length + ' 项');
  if (fails.length) process.exitCode = 1;
}

main().catch((err) => {
  console.error(err && err.stack || err);
  fs.writeFileSync(path.join(OUT, 'host-report.txt'), report.join('\n') + '\n!! ' + (err && err.message || err) + '\n', 'utf8');
  process.exitCode = 1;
});
