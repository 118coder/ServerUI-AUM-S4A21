#!/usr/bin/env node
/*
 * 无头 UI 截图 + 布局审计（评审工具）
 * 不启动 WPF / WebView2，用本地静态服务 + 注入的 mock bridge + headless Edge。
 *
 *   node tools/ui-shots.js --out ../_ui-report --sizes=1280x840,1080x700
 *
 * 输出（Phase 1 + Phase 2）：
 *   dash-dark@{size}.png / dash-light@1280x840.png
 *   palette-dark / palette-light：控件色（老版 Ant Design）与日志行色的计算样式断言
 *   archive-dark@{size}.png / archive-light@1280x840.png
 *   dll-dark@{size}.png / dll-light@1280x840.png / dll-empty@1280x840.png
 *   archive-menu@1280x840.png / archive-filebrowser@1280x840.png / dll-inieditor@1280x840.png
 *   layout.txt（审计报告；出现 !! 即不通过）
 */
const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const net = require('net');
const { spawn } = require('child_process');

const ROOT = path.resolve(__dirname, '..');
const WEB = path.join(ROOT, 'web');
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
const OUT = path.resolve(flags.out || path.join(ROOT, '..', '_ui-report'));
const SIZES = String(flags.sizes || '1280x840,1080x700').split(',').map((s) => {
  const [w, h] = s.split(/[x×]/).map(Number);
  return { w, h, label: `${w}x${h}` };
});
fs.mkdirSync(OUT, { recursive: true });

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.json': 'application/json; charset=utf-8'
};

function startServer() {
  const server = http.createServer((req, res) => {
    let target = decodeURIComponent((req.url || '/').split('?')[0]);
    if (target === '/' || target === '') target = '/index.html';
    const file = path.join(WEB, target);
    if (path.relative(WEB, file).startsWith('..')) { res.writeHead(403).end('forbidden'); return; }
    fs.readFile(file, (err, data) => {
      if (err) { res.writeHead(404).end('not found'); return; }
      res.writeHead(200, {
        'Content-Type': MIME[path.extname(file).toLowerCase()] || 'application/octet-stream',
        'Cache-Control': 'no-store'
      });
      res.end(data);
    });
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => {
    resolve({ port: server.address().port, close: () => new Promise((d) => server.close(d)) });
  }));
}

function findBrowser() {
  const list = [
    process.env.AUM_BROWSER,
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'
  ].filter(Boolean);
  return list.find((p) => fs.existsSync(p)) || null;
}

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.unref();
    s.on('error', reject);
    s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => resolve(p)); });
  });
}
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function listTargets(port) {
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

async function launch(port) {
  const browser = findBrowser();
  if (!browser) throw new Error('找不到 Edge/Chrome');
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'serverui-ui-'));
  const proc = spawn(browser, [
    '--headless=new',
    `--remote-debugging-port=${port}`,
    '--remote-allow-origins=*',
    `--user-data-dir=${profile}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-gpu',
    '--force-device-scale-factor=1',
    '--window-size=1440,1000',
    'about:blank'
  ], { stdio: 'ignore', windowsHide: true });
  const started = Date.now();
  while (Date.now() - started < 15000) {
    try {
      const list = await listTargets(port);
      if (list.some((t) => t.type === 'page')) return { proc, profile };
    } catch { }
    await wait(160);
  }
  throw new Error('headless 浏览器未就绪');
}

async function connect(port) {
  const targets = await listTargets(port);
  const page = targets.find((t) => t.type === 'page');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
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
    if (msg.method === 'Runtime.consoleAPICalled') {
      const args = (msg.params.args || []).map((a) => a.value !== undefined ? a.value : (a.description || a.type));
      if (msg.params.type === 'error') consoleErrors.push(args.map(String).join(' '));
      console.log('[console.' + msg.params.type + ']', ...args);
    } else if (msg.method === 'Runtime.exceptionThrown') {
      const d = msg.params.exceptionDetails || {};
      const text = (d.exception && (d.exception.description || d.exception.value)) || d.text || '';
      consoleErrors.push(String(text));
      console.log('[exception]', text);
    }
  });
  const send = (method, params = {}) => new Promise((resolve) => {
    const mid = ++id;
    pending.set(mid, resolve);
    ws.send(JSON.stringify({ id: mid, method, params }));
  });
  const evaluate = async (expression) => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true });
    if (r.result && r.result.exceptionDetails) {
      const d = r.result.exceptionDetails;
      throw new Error((d.exception && (d.exception.description || d.exception.value)) || d.text || 'evaluate failed');
    }
    return r.result && r.result.result ? r.result.result.value : null;
  };
  return { send, evaluate, close: () => ws.close(), consoleErrors };
}

/* ==========================================================================
   Mock bridge：Phase 1 数据 + Phase 2（archive / dll / fs）数据
   - window.__mock.setInstalled(false) 切换 DLL 未安装空态
   - window.__mock.setServerRunning(true) 切换服务端运行中（存档操作拦截）
   ========================================================================== */
const MOCK = String.raw`
(() => {
  const listeners = [];
  const state = { theme: 'dark', installed: true, serverRunning: false };
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage(raw) {
      let msg; try { msg = JSON.parse(raw); } catch { return; }
      setTimeout(() => handle(msg), 12);
    },
    addEventListener(type, fn) { if (type === 'message') listeners.push(fn); }
  };
  const send = (data) => { for (const fn of listeners) { try { fn({ data: JSON.stringify(data) }); } catch (e) { } } };
  const reply = (id, result) => send({ id, ok: true, result });
  const fail = (id, error) => send({ id, ok: false, error });
  window.__mockErrors = [];
  const ce = console.error;
  console.error = function (...a) { window.__mockErrors.push(a.map(String).join(' ')); ce.apply(console, a); };
  window.addEventListener('error', (e) => window.__mockErrors.push(String(e.message)));

  const AD = 'E:\\Game\\S4A21_CN\\AUM管理组件';
  const GR = 'E:\\Game\\S4A21_CN';

  let winMode = 'full';
  const status = {
    bat: false, dfo: false, external: false, pvf: true,
    version: '2026-10-22',
    sdk: '.NET SDK: 已就绪', sdkOk: true, sdkLevel: 'success',
    serverState: '● 未运行', serverLevel: 'error',
    startedAt: null, unexpectedExit: null, lastExitCode: -1,
    lastUpdate: '上次更新: 2026-10-22', lastUpdateLevel: 'secondary', admin: false
  };
  const logs = [
    ['info', '>>> [网络] Gitee 正常（延迟 218 ms）'],
    ['info', '>>> [网络] GitHub 正常（延迟 205 ms）'],
    ['info', '>>> [网络] Codeberg 正常（延迟 604 ms）'],
    ['info', '>>> [AUM自检] 已是最新版本 v2.20'],
    ['secondary', '>>> [DLL扩展] 已刷新: 受管插件 8 个已启用, 自定义扩展 2 个已勾选'],
    ['secondary', '[检查] DNF.exe 已找到: E:\\Game\\S4A21_CN\\DNF.exe']
  ];
  const settings = { skipLog: false, mirror: false, dxMode: 'off', watermark: false, cleanRedundantDb: false };

  // ---------------- 存档 ----------------
  const archiveRows = [
    { name: '新生种子存档', size: 2359296, sizeDisplay: '2.3 MB', modified: '2026-09-21 05:24', current: true },
    { name: '全职业满级_20260918', size: 2621440, sizeDisplay: '2.5 MB', modified: '2026-09-18 22:10', current: false },
    { name: '主线通关备份', size: 2097152, sizeDisplay: '2.0 MB', modified: '2026-09-12 19:42', current: false },
    { name: '测试用_修改前', size: 1835008, sizeDisplay: '1.8 MB', modified: '2026-08-30 11:05', current: false },
    { name: '原始存档_勿动', size: 1572864, sizeDisplay: '1.5 MB', modified: '2026-08-02 03:18', current: false }
  ];
  let ascending = true;   // 与宿主 ArchiveController._ascending 默认一致

  function archiveList() {
    let rows = archiveRows.slice();
    rows.sort((a, b) => ascending
      ? String(a.modified).localeCompare(String(b.modified))
      : String(b.modified).localeCompare(String(a.modified)));
    return {
      ok: true,
      current: 'inventory.db (2.3 MB)',
      backupCount: 1,
      ascending,
      entries: rows.map((r, i) => ({
        index: i + 1,
        name: r.name,
        size: r.size,
        sizeDisplay: r.sizeDisplay,
        modified: r.modified,
        modifiedRaw: r.modified.replace(' ', 'T'),
        current: r.current
      }))
    };
  }

  // ---------------- DLL ----------------
  const managedDefs = [
    ['GameNative.dll', '游戏原生整合', '原生层游戏功能整合补丁（必选，不可关闭）', null, true],
    ['PreventMinimize.dll', '防止窗口最小化', '防止游戏窗口意外最小化', null, false],
    ['MultiInstance.dll', '游戏多开', '支持同时运行多个游戏客户端', null, false],
    ['AutoFire.dll', '自动连发', '按住攻击键自动连续攻击', 'AutoFire.ini', false],
    ['EquipmentSwap.dll', '一键换装', '快捷切换预设装备插件，在游戏中按下end呼出，(EquipmentSwap.dll)', null, false],
    ['DpsMeter.dll', 'DPS 统计', '战斗中实时统计输出', 'DpsMeter.ini', false],
    ['CombatPower.dll', '战斗力显示', '实时显示角色战斗力', 'CombatPower.ini', false],
    ['ImeFix.dll', '现代输入法兼容插件', '兼容现代输入法（拼音/五笔等）在游戏中正常输入文字', 'ImeFix.ini', false]
  ];
  const customDefs = [
    ['DfoVibrationS4A21.dll', true, true],
    ['S4A21MemOpt.dll', true, true]
  ];
  let managedOrder = managedDefs.map((d) => d[0]);
  let customOrder = customDefs.map((d) => d[0]);

  function dllState() {
    const installed = state.installed;
    const defs = managedOrder
      .map((f) => managedDefs.find((d) => d[0] === f))
      .filter(Boolean);
    const managed = defs.map((d) => ({
      file: d[0], name: d[1], desc: d[2], config: d[3], locked: d[4],
      on: installed, exists: installed
    }));
    const custom = installed
      ? customOrder.map((f) => {
          const c = customDefs.find((x) => x[0] === f) || [f, true, true];
          return { file: c[0], on: c[1], exists: c[2] };
        })
      : [];
    return {
      ok: true,
      installed,
      loaderInstalled: installed,
      iniPath: GR + '\\GameGaurd.ini',
      iniExists: installed,
      managed,
      custom,
      managedOrder: managed.map((m) => m.file),
      customOrder: custom.map((c) => c.file),
      status: installed
        ? '受管插件已启用 ' + managed.filter((m) => m.on).length + ' / ' + managed.length
          + ' · 自定义扩展 已勾选 ' + custom.filter((c) => c.on).length + ' / ' + custom.length + ' → GameGaurd.ini'
        : '未安装 DLL 扩展：游戏根目录未发现补丁文件，请先点击【新DLL安装】'
    };
  }

  const iniText = [
    '; GameGaurd 客户端补丁配置',
    '; 本文件由客户端补丁安装器生成，可安全编辑',
    'PatchVersion=2.20',
    '; 上次安装日期',
    'LastInstall=20260921',
    '',
    '[General]',
    'LogLevel=2',
    'EnableDebug=0',
    'Language=zh_CN',
    '',
    '; 自动连发',
    '[AutoFire]',
    'Enabled=1',
    'Interval=50',
    '',
    '[DpsMeter]',
    'Enabled=1',
    'RefreshRate=0.5',
    '; 统计面板位置',
    'PanelX=120',
    'PanelY=360',
    '',
    '[CombatPower]',
    'ShowPercent=false',
    'FontSize=13'
  ].join('\r\n') + '\r\n';

  // ---------------- 文件系统 ----------------
  const drives = [
    { name: 'Windows-SSD (C:)', path: 'C:\\', free: 1390034944, total: 236172865536 },
    { name: '新加卷 (D:)', path: 'D:\\', free: 44027183104, total: 274629742592 },
    { name: '游戏 (E:)', path: 'E:\\', free: 16015912960, total: 1101004795904 }
  ];
  const FS = {
    'C:\\': { dirs: ['Users', 'Windows', 'Program Files'], files: [] },
    'C:\\Users': { dirs: ['12290', 'Public'], files: [] },
    'E:\\': { dirs: ['Game', '网页小工具', 'SteamLibrary'], files: ['pagefile.sys'] },
    'E:\\Game': { dirs: ['S4A21_CN'], files: [] },
    'E:\\Game\\S4A21_CN': {
      dirs: ['ImagePacks2', 'SoundPacks', 'AUM管理组件'],
      files: ['DNF.exe', 'GameGaurd.ini', 'GameGaurd.dll', 'az.dll', 'Script.pvf',
        'DfoVibrationS4A21.dll', 'S4A21MemOpt.dll', 'dnf.tmp']
    },
    'E:\\Game\\S4A21_CN\\AUM管理组件': {
      dirs: ['实用工具包', '存档管理', 'ServerS4A21-AUM'],
      files: ['设置.json', '更新日志.txt', '运行日志.txt', '单机游戏启动.bat',
        '存档_0921_0524.zip', '存档_0918_2210.zip']
    },
    'E:\\Game\\S4A21_CN\\AUM管理组件\\实用工具包': {
      dirs: ['DLL覆盖', 'PVF原版'],
      files: []
    },
    'E:\\Game\\S4A21_CN\\AUM管理组件\\实用工具包\\DLL覆盖': {
      dirs: [],
      files: ['客户端补丁.zip', '说明.txt']
    },
    'E:\\Game\\S4A21_CN\\AUM管理组件\\存档管理': {
      dirs: ['切换库', '备份存档'],
      files: []
    },
    'E:\\Game\\S4A21_CN\\AUM管理组件\\存档管理\\切换库': {
      dirs: ['新生种子存档', '全职业满级_20260918', '主线通关备份'],
      files: []
    },
    'E:\\Game\\S4A21_CN\\AUM管理组件\\存档管理\\备份存档': {
      dirs: ['backup_20260918_221012'],
      files: []
    }
  };

  function parentOf(p) {
    if (!p) return null;
    const t = p.replace(/[\\/]+$/, '');
    const i = t.lastIndexOf('\\');
    if (i < 0) return null;
    const par = t.slice(0, i);
    return par.length === 2 && par[1] === ':' ? par + '\\' : par;
  }

  function browse(p, filter) {
    const target = p || 'E:\\Game\\S4A21_CN';
    const node = FS[target] || { dirs: [], files: [] };
    const dirs = node.dirs.map((d, i) => ({
      name: d, path: (target.endsWith('\\') ? target : target + '\\') + d, kind: 'dir', size: 0,
      modified: '2026-09-2' + (i % 8) + ' 10:2' + i
    }));
    const allow = filter === 'zip' ? /\.zip$/i
      : filter === 'db' ? /\.db$/i
      : filter === 'dll' ? /\.(dll|ini)$/i
      : filter === 'image' ? /\.(png|jpg|jpeg|webp|bmp)$/i
      : null;
    const files = node.files
      .filter((f) => !allow || allow.test(f))
      .map((f, i) => ({
        name: f, path: (target.endsWith('\\') ? target : target + '\\') + f, kind: 'file',
        size: 2359296 - i * 131072, modified: '2026-09-21 05:2' + (i % 9)
      }));
    return {
      ok: true, path: target, parent: parentOf(target), drives, dirs, files, error: null
    };
  }

  // ---------------- Phase 3a：时间 / 构建 / 安装 ----------------
  const timeNow = { value: '2026-10-22 11:00:00' };
  const calls = [];             // 交互断言用：记录前端实际调用的 bridge 方法
  function stamp() {
    // 宿主返回的时间文本（mock 固定值，保持可断言）
    return timeNow.value;
  }

  window.__mock = {
    calls,
    clearCalls() { calls.length = 0; },
    setNow(v) { timeNow.value = v; },
    setInstalled(v) { state.installed = !!v; },
    /** 推送一条日志（palette 断言要读真实日志行的计算色）。 */
    emitLog(level, text) { send({ type: 'log', payload: { level, text } }); },
    emitDrop(path) { send({ type: 'drop', payload: { paths: [path] } }); },
    /** 只改宿主侧「运行中」标志，不推 status —— 模拟前端快照过期（真实竞态）。 */
    setServerRunningSilent(v) { state.serverRunning = !!v; },
    setMode(m) { winMode = m; send({ type: 'window.mode', payload: { mode: m, maximized: false } }); },
    getMode() { return winMode; },
    setServerRunning(v) {
      state.serverRunning = !!v;
      status.bat = !!v;
      status.serverState = v ? '● 运行中' : '● 未运行';
      status.serverLevel = v ? 'success' : 'error';
      // 前端存档页读 app.js 的 status 快照，必须像宿主一样推送变更
      send({ type: 'status', payload: status });
    },
    archiveRows,
    iniText,
    /** 当前 DLL 状态的数据视图（审计用来核对开关数量） */
    dllView() {
      const st = dllState();
      return {
        managed: st.managed.length,
        custom: st.custom.length,
        onManaged: st.managed.filter((m) => m.on).length,
        onCustom: st.custom.filter((c) => c.on).length,
        onTotal: st.managed.filter((m) => m.on).length + st.custom.filter((c) => c.on).length
      };
    }
  };

  function handle(msg) {
    const { id, method, args } = msg;
    const a = args || {};
    switch (method) {
      case 'app.init':
        reply(id, {
          version: '2.20', theme: state.theme,
          paths: { ad: AD, gr: GR },
          isAdmin: false, page: 'dash', logHeight: 190, logCollapsed: false, settings,
          mode: winMode, modes: ['full', 'mini', 'classic']
        });
        setTimeout(() => { send({ type: 'status', payload: status }); }, 120);
        logs.forEach((it, i) => setTimeout(() => send({ type: 'log', payload: { level: it[0], text: it[1] } }), 200 + i * 60));
        break;
      case 'settings.get': reply(id, settings); break;
      case 'settings.set':
        if (a.theme) state.theme = a.theme;
        reply(id, {});
        break;
      case 'server.status': reply(id, status); break;
      case 'gm.launch': calls.push({ method: 'gm.launch' }); reply(id, { ok: true }); break;
      case 'server.start': calls.push({ method: 'server.start' }); reply(id, { ok: true }); break;
      case 'server.stop': calls.push({ method: 'server.stop' }); reply(id, { ok: true }); break;
      case 'server.restart': calls.push({ method: 'server.restart' }); reply(id, { ok: true }); break;
      case 'sdk.install': calls.push({ method: 'sdk.install' }); reply(id, { ok: true }); break;
      case 'aum.plan':
        calls.push({ method: 'aum.plan', forcePackage: !!a.forcePackage });
        reply(id, {
          ok: true, upToDate: false, current: '2.20', target: '2.20.1',
          strategy: a.forcePackage ? 'package' : 'compile',
          strategyLabel: a.forcePackage ? '发行包替换' : '源码编译',
          reason: a.forcePackage ? '已强制使用发行包' : '构建环境完整：.NET 10.0.303 + PowerShell 5.1',
          packageBytes: 1896885
        });
        break;
      case 'aum.update':
        calls.push({ method: 'aum.update', forcePackage: !!a.forcePackage });
        reply(id, { ok: true, strategy: a.forcePackage ? 'package' : 'compile', current: '2.20', target: '2.20.1', restarting: true });
        break;
      case 'window.mode': {
        // 与宿主语义一致：非法值回落 full 并回报 ok:false；切换后推送 window.mode
        const want = String(a.mode || '').trim().toLowerCase();
        const modes = ['full', 'mini', 'classic'];
        calls.push({ method: 'window.mode', mode: want, args: a });
        if (modes.indexOf(want) < 0) {
          winMode = 'full';
          reply(id, { ok: false, mode: winMode, maximized: false, supported: modes,
            error: '未知窗口模式：' + (want || '(空)') + '；支持 full / mini / classic，已回到完整模式' });
        } else {
          winMode = want;
          reply(id, { ok: true, mode: winMode, maximized: false, supported: modes });
        }
        setTimeout(() => send({ type: 'window.mode', payload: { mode: winMode, maximized: false } }), 16);
        break;
      }
      case 'window.drag': case 'window.minimize': case 'window.toggleMaximize':
      case 'window.close': case 'app.ready': case 'app.quit':
      case 'shell.open':
        calls.push({ method: 'shell.open', kind: a.kind, value: a.value });
        reply(id, { ok: true }); break;
      case 'clipboard.write': case 'update.openLog':
      case 'archive.cleanRedundant':
        reply(id, { ok: true }); break;
      case 'dll.setOrder':
        // 与宿主一致：保存顺序并返回完整状态（否则前端 state 会被覆盖成 {ok:true}）
        calls.push({ method: 'dll.setOrder', managedOrder: (a.managedOrder || []).slice(), customOrder: (a.customOrder || []).slice() });
        if (Array.isArray(a.managedOrder) && a.managedOrder.length) managedOrder = a.managedOrder.slice();
        if (Array.isArray(a.customOrder)) customOrder = a.customOrder.slice();
        reply(id, dllState());
        break;
      case 'update.start':
        calls.push({ method: 'update.start', mode: a.mode });
        reply(id, {});
        for (let i = 1; i <= 10; i++) {
          setTimeout(() => send({ type: 'progress', payload: { value: i * 10, label: '更新进度: ' + (i * 10) + '%', visible: true } }), i * 120);
        }
        setTimeout(() => send({ type: 'log', payload: { level: 'success', text: '>>> 更新完成（Mock）' } }), 1350);
        break;

      // ---------------- 存档 ----------------
      case 'archive.list': calls.push({ method: 'archive.list' }); reply(id, archiveList()); break;
      case 'archive.setSort':
        ascending = !!a.ascending;
        reply(id, archiveList()); break;
      case 'archive.store':
        calls.push({ method: 'archive.store', name: a.name });
        if (state.serverRunning) {
          reply(id, { ok: false, blocked: true, error: '目前服务端正在运行，请结束服务端后再使用存档管理相关功能。' });
          break;
        }
        reply(id, { ok: true, name: a.name });
        break;
      case 'archive.import': case 'archive.importZip': reply(id, { ok: true }); break;
      case 'archive.export': reply(id, { ok: true, path: a.path }); break;
      case 'archive.undo': calls.push({ method: 'archive.undo' }); reply(id, { ok: true, undone: true }); break;
      case 'archive.switch': calls.push({ method: 'archive.switch', name: a.name }); reply(id, { ok: true, name: a.name }); break;
      case 'archive.rename': reply(id, { ok: true, renamed: true }); break;
      case 'archive.dropFile': reply(id, { ok: true }); break;

      // ---------------- DLL ----------------
      case 'dll.state': case 'dll.refresh': reply(id, dllState()); break;
      case 'dll.previewApply': {
        const st = dllState();
        const managed = (a.managedOn || []).filter((f) => st.managedOrder.some((x) => x === f)).length;
        const custom = (a.customOn || []).filter((f) => st.customOrder.some((x) => x === f)).length;
        reply(id, {
          ok: true, managed, custom, loaderInstalled: st.loaderInstalled,
          confirmText: '将直写游戏根目录 GameGaurd.ini 的 [Plugins] 插件列表：\n'
            + '  启用受管插件 ' + managed + '（未勾选的受管条目会从列表移除，文件保留；\n'
            + '  自定义扩展 ' + custom + ' 个按开关勾选写入（取消勾选 = 从 [Plugins] 移除，文件保留）\n\n继续？'
        });
        break;
      }
      case 'dll.apply': reply(id, { ok: true, applied: true, managed: 8, custom: 2 }); break;
      case 'dll.add': reply(id, { ok: true, state: dllState() }); break;
      case 'dll.delete': reply(id, { ok: true, state: dllState() }); break;
      case 'dll.readConfig':
        reply(id, {
          ok: true, file: a.file, path: GR + '\\' + a.file,
          exists: true, text: iniText, encoding: 'utf-8'
        });
        break;
      case 'dll.saveConfig': reply(id, { ok: true, path: GR + '\\' + a.file }); break;

      // ---------------- 文件浏览器 ----------------
      case 'fs.drives': reply(id, { ok: true, drives }); break;
      case 'fs.browse': reply(id, browse(a.path, a.filter)); break;

      // ---------------- Phase 3a：时间 / 构建 / 安装 ----------------
      case 'time.get':
        calls.push({ method: 'time.get' });
        reply(id, { ok: true, now: stamp(), iso: stamp().replace(' ', 'T'), admin: false,
          readOnlyHint: '写入系统时间需要管理员权限' });
        break;
      case 'time.apply':
        calls.push({ method: 'time.apply', iso: a.iso });
        timeNow.value = a.iso;
        reply(id, { ok: true, now: a.iso });
        break;
      case 'time.quick':
        calls.push({ method: 'time.quick', year: a.year });
        timeNow.value = a.year + '-01-01 00:00:00';
        reply(id, { ok: true, now: timeNow.value });
        break;
      case 'time.sync':
        calls.push({ method: 'time.sync' });
        timeNow.value = '2026-10-22 11:11:11';
        reply(id, { ok: true, via: 'ntp', now: timeNow.value });
        break;
      case 'build.local':
        calls.push({ method: 'build.local' });
        reply(id, { ok: true, path: AD + '\进行本地编译.bat' });
        break;
      case 'dll.installPatch':
        calls.push({ method: 'dll.installPatch' });
        reply(id, { ok: true, copied: 12, enabled: 8, failed: [] });
        break;

      default: reply(id, {});
    }
  }
})();
`;

/* ==========================================================================
   审计脚本：每页跑一次
   ========================================================================== */
const AUDIT = String.raw`
(() => {
  const issues = [];
  const de = document.documentElement;
  if (de.scrollWidth > innerWidth + 1) issues.push('横向溢出: ' + de.scrollWidth + ' > ' + innerWidth);

  const vis = (el) => {
    const cs = getComputedStyle(el);
    const r = el.getBoundingClientRect();
    return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0;
  };

  const page = document.querySelector('#page');
  if (!page) { issues.push('缺少 #page'); }
  if (page) {
    if (page.scrollWidth > page.clientWidth + 1) issues.push('#page 横向溢出: ' + page.scrollWidth + ' > ' + page.clientWidth);
    if (!page.querySelector('.page-title') && !page.querySelector('.mini-page')) issues.push('缺少 #page .page-title');
    // h() 两参兼容回归：数字属性名说明 attrs/children 判错
    for (const el of page.querySelectorAll('*')) {
      let hit = null;
      for (const a of Array.from(el.attributes)) { if (/^\d+$/.test(a.name)) { hit = a.name; break; } }
      if (hit !== null) issues.push('h() 两参兼容回归: <' + el.tagName.toLowerCase() + '> 带有数字属性 "' + hit + '"');
    }
  }

  // 可见文字裁切（只在 nowrap 且没有 text-overflow:ellipsis 时算缺陷；
  // 省略号截断是设计取舍，单独统计，不计入 !!）
  let ellipsisCount = 0;
  for (const el of document.querySelectorAll('.page *')) {
    if (!vis(el)) continue;
    if (el.children.length) continue;
    const text = (el.textContent || '').trim();
    if (!text) continue;
    const cs = getComputedStyle(el);
    if (cs.whiteSpace !== 'nowrap') continue;
    if (el.scrollWidth <= el.clientWidth + 1) continue;
    if (cs.textOverflow === 'ellipsis') { ellipsisCount++; continue; }
    issues.push('文字裁切: <' + el.tagName.toLowerCase() + ' class="' + el.className + '"> ' + text.slice(0, 28));
  }

  for (const sel of ['.titlebar', '.sidebar', '.content', '.log-panel', '.log-resizer']) {
    if (!document.querySelector(sel)) issues.push('缺少 ' + sel);
  }
  // 标题栏/日志是所有页共用的；统计卡只在首页断言
  for (const sel of ['.status-pill', '#btnClassic', '#btnMini', '.log-toolbar']) {
    if (!document.querySelector(sel)) issues.push('缺少旧 UI 元素 ' + sel);
  }
  if (page && page.querySelector('.dash-cols') && !document.querySelector('.stat-row .stat-card')) {
    issues.push('缺少旧 UI 元素 .stat-row .stat-card');
  }

  /* ---------------- DLL 页：行几何 / 开关状态 / 空态 ---------------- */
  const dllCard = document.querySelector('.dll-card');
  const dllScroll = document.querySelector('.dll-scroll');
  if (dllCard) {
    const cardR = dllCard.getBoundingClientRect();
    const cardRight = cardR.right - parseFloat(getComputedStyle(dllCard).paddingRight || 0);
    const rows = Array.from(document.querySelectorAll('.dll-row'));
    const dllGeom = [];
    for (const r of rows) {
      const q = (sel) => r.querySelector(sel);
      const g = q('.dll-grip') || q('.grip-none');
      const sw = q('.switch');
      const nm = q('.dll-name');
      const ds = q('.dll-desc');
      const ac = q('.dll-actions');
      if (!sw || !nm || !ds || !ac) {
        issues.push('DLL 行缺少必需要素: ' + (r.dataset.file || '(no file)'));
        continue;
      }
      const B = (el) => el.getBoundingClientRect();
      const S = B(sw), N = B(nm), D = B(ds), A = B(ac), G = g ? B(g) : null;
      // 开关不得与名称水平重叠
      if (S.right > N.left + 0.5) {
        issues.push('DLL 开关压住名称: ' + r.dataset.file + ' sw.right=' + S.right.toFixed(1) + ' name.left=' + N.left.toFixed(1));
      }
      // 手柄不得压住开关
      if (G && G.right > S.left + 0.5) {
        issues.push('DLL 手柄压住开关: ' + r.dataset.file);
      }
      // 名称与说明必须同一行（垂直重叠 = 上下堆叠/错位）
      const vOverlap = Math.min(N.bottom, D.bottom) - Math.max(N.top, D.top);
      if (vOverlap <= 0) {
        issues.push('DLL 名称/说明不在同一行: ' + r.dataset.file + ' (' + N.top.toFixed(1) + '/' + D.top.toFixed(1) + ')');
      }
      // 名称与说明不得水平重叠
      if (N.right > D.left + 0.5) issues.push('DLL 名称压住说明: ' + r.dataset.file);
      // 操作按钮右边缘不得越过卡片内容右缘
      if (ac && ac.children.length && A.right > cardRight + 0.5) {
        issues.push('DLL 操作列超出卡片: ' + r.dataset.file + ' ' + A.right.toFixed(1) + ' > ' + cardRight.toFixed(1));
      }
      dllGeom.push({
        file: r.dataset.file, rowH: Math.round(B(r).height),
        swRight: Math.round(S.right), nameLeft: Math.round(N.left),
        nameRight: Math.round(N.right), descLeft: Math.round(D.left)
      });
    }

    if (rows.length) {
      const uniqH = Array.from(new Set(dllGeom.map((x) => x.rowH)));
      if (uniqH.length !== 1 || uniqH[0] < 36 || uniqH[0] > 40) {
        issues.push('DLL 行高应为 38–40 且一致，实际 ' + JSON.stringify(uniqH));
      }
      // 开关总数必须等于数据中 on 的数量（8 受管 + 2 自定义）；首行 GameNative 必须 on + locked
      const first = rows[0];
      if (!first || first.dataset.file !== 'GameNative.dll') issues.push('DLL 首行不是 GameNative');
      else {
        const fsw = first.querySelector('.switch');
        if (!fsw || !fsw.classList.contains('on')) issues.push('GameNative 开关视觉不是开启态');
        if (!first.classList.contains('locked')) issues.push('GameNative 行缺少 locked');
        const fk = fsw ? fsw.querySelector('.knob') : null;
        const track = fsw ? fsw.querySelector('.track') : null;
        if (fsw && getComputedStyle(fsw).opacity === '0.4') issues.push('GameNative 开关仍为 40% 透明');
        const primary = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim().toLowerCase();
        const trackBg = track ? getComputedStyle(track).backgroundColor : '';
        const hexToRgb = (h) => {
          const m = /^#([0-9a-f]{6})$/i.exec(h);
          if (!m) return null;
          const n = parseInt(m[1], 16);
          return 'rgb(' + ((n >> 16) & 255) + ', ' + ((n >> 8) & 255) + ', ' + (n & 255) + ')';
        };
        const want = hexToRgb(primary);
        if (want && trackBg !== want) {
          issues.push('GameNative track 不是主题主色: ' + trackBg + ' ≠ ' + want);
        }
      }
    }

    // 空态：不得同时出现表头/行
    const empty = document.querySelector('.dll-empty');
    const emptyVisible = empty && getComputedStyle(empty).display !== 'none';
    if (emptyVisible) {
      const head = document.querySelector('.dll-listhead');
      if (head && getComputedStyle(head).display !== 'none') {
        issues.push('DLL 空态时表头仍显示');
      }
      if (rows.length !== 0) issues.push('DLL 空态时仍渲染了 ' + rows.length + ' 行');
    }
  }

  // 开关 .on 数量必须与数据一致（8 受管 + 2 自定义）
  if (dllCard) {
    const rows = Array.from(document.querySelectorAll('.dll-row'));
    const emptyEl = document.querySelector('.dll-empty');
    const isEmpty = emptyEl && getComputedStyle(emptyEl).display !== 'none';
    const switches = Array.from(document.querySelectorAll('.dll-row .switch'));
    const onCount = switches.filter((x) => x.classList.contains('on')).length;
    const data = (!isEmpty && window.__mock && window.__mock.dllView) ? window.__mock.dllView() : null;
    if (data) {
      if (switches.length !== data.managed + data.custom) {
        issues.push('DLL 开关数 ' + switches.length + ' ≠ 数据 ' + (data.managed + data.custom));
      }
      if (onCount !== data.onTotal) {
        issues.push('DLL 开启态 ' + onCount + ' ≠ 数据 on ' + data.onTotal);
      }
    }
    // 自定义行必须存在且可达（DOM 存在；若不在视口则 scroll 必须可滚）
    const customRows = Array.from(document.querySelectorAll('.dll-row.custom'));
    if (customRows.length && data && customRows.length !== data.custom) {
      issues.push('DLL 自定义行数 ' + customRows.length + ' ≠ 数据 ' + data.custom);
    }
    if (customRows.length >= 2) {
      const sc = document.querySelector('.dll-scroll');
      const last = customRows[customRows.length - 1].getBoundingClientRect();
      const sr = sc.getBoundingClientRect();
      const inView = last.top >= sr.top - 1 && last.bottom <= sr.bottom + 1;
      const scrollable = sc.scrollHeight > sc.clientHeight + 1;
      if (!inView && !scrollable) {
        issues.push('DLL 第 2 个自定义扩展不可见且滚动容器不可滚');
      }
      // 需要滚动时必须真的出现滚动条（宽度 > 0）
      if (scrollable) {
        const sbWidth = sc.offsetWidth - sc.clientWidth;
        if (sbWidth < 6) issues.push('DLL 内容溢出但滚动条不可见（宽 ' + sbWidth + 'px）');
      }
    }
  }

  // INI 数字必须自绘：不得出现原生 number 输入
  const numInputs = document.querySelectorAll('input[type="number"]').length;
  if (numInputs) issues.push('出现原生 number 输入 × ' + numInputs);

  // 禁止的浏览器原生控件 / 原生提示
  for (const sel of ['select', 'input[type="date"]', 'input[type="time"]', 'input[type="color"]', '[title]']) {
    const n = document.querySelectorAll(sel).length;
    if (n) issues.push('出现原生控件/属性: ' + sel + ' × ' + n);
  }

  // 日志面板
  const lb = document.querySelector('.log-body');
  let logLines = 0;
  let topClip = 0;
  if (lb) {
    const lr = lb.getBoundingClientRect();
    for (const l of lb.querySelectorAll('.log-line')) {
      const r = l.getBoundingClientRect();
      const visible = r.bottom > lr.top && r.top < lr.bottom;
      if (!visible) continue;
      if (r.top >= lr.top - 1 && r.bottom <= lr.bottom + 1) logLines++;
      else if (r.top < lr.top - 1) topClip = Math.max(topClip, lr.top - r.top);
    }
    // 仅在「未滚动」（scrollTop≈0）时要求整行对齐；贴底显示时顶部半行是正常现象
    if (lb.scrollTop <= 1 && topClip > 4) {
      issues.push('日志面板顶部有 ' + topClip.toFixed(1) + 'px 行裁切（未对齐到整行）');
    }
    if (lb.querySelector('.log-line') && lb.querySelector('.log-empty')) issues.push('日志占位文本未在首条日志到达后移除');
    const lay = getComputedStyle(lb).overflowY;
    if (lay !== 'auto' && lay !== 'scroll') issues.push('.log-body 未启用独立滚动');
  }

  const grip = document.querySelector('.log-resizer .grip');
  const gripInfo = grip ? {
    w: Math.round(grip.getBoundingClientRect().width),
    h: Math.round(grip.getBoundingClientRect().height),
    bg: getComputedStyle(grip).backgroundColor
  } : null;
  const first = lb ? lb.querySelector('.log-line') : null;
  const logGeom = lb ? {
    scrollTop: Math.round(lb.scrollTop * 10) / 10,
    max: Math.round((lb.scrollHeight - lb.clientHeight) * 10) / 10,
    clientH: lb.clientHeight,
    scrollH: lb.scrollHeight,
    padTop: parseFloat(getComputedStyle(lb).paddingTop) || 0,
    lineH: first ? Math.round(first.getBoundingClientRect().height * 100) / 100 : 0,
    count: lb.querySelectorAll('.log-line').length
  } : null;

  return {
    issues,
    errors: (window.__mockErrors || []).slice(),
    title: document.title,
    pageText: (page ? page.innerText.slice(0, 160) : ''),
    theme: document.documentElement.getAttribute('data-theme'),
    logBg: lb ? getComputedStyle(lb).backgroundColor : null,
    logLines,
    topClip: Math.round(topClip * 10) / 10,
    ellipsisCount,
    grip: gripInfo,
    logGeom,
    pageScroll: page ? { sw: page.scrollWidth, cw: page.clientWidth, sh: page.scrollHeight, ch: page.clientHeight } : null
  };
})()
`;

/** 切到指定页并等待该页标题出现。 */
async function gotoPage(cdp, id, titleText) {
  await cdp.evaluate(`(() => {
    const n = document.querySelector('#sidebar [data-nav="${id}"]');
    if (n) n.click();
  })()`);
  const started = Date.now();
  while (Date.now() - started < 4000) {
    const t = await cdp.evaluate("(document.querySelector('#page .page-title') || {}).textContent || ''").catch(() => '');
    if (String(t).indexOf(titleText) >= 0) return true;
    await wait(80);
  }
  throw new Error('切页超时: ' + id + ' 期望标题 ' + titleText);
}

/** 轮询等待 mock 调用记录中出现某个方法（最多 timeoutMs），返回是否出现。 */
async function waitForCall(cdp, method, timeoutMs) {
  const started = Date.now();
  const limit = timeoutMs || 4000;
  while (Date.now() - started < limit) {
    const hit = await cdp.evaluate(`(window.__mock.calls || []).some((c) => c.method === ${JSON.stringify(method)})`).catch(() => false);
    if (hit) return true;
    await wait(60);
  }
  return false;
}

async function setTheme(cdp, theme) {
  for (let attempt = 0; attempt < 3; attempt++) {
    const cur = await cdp.evaluate("document.documentElement.getAttribute('data-theme')");
    if (cur === theme) {
      // 主题属性已正确；等一帧让令牌应用到卡片
      await wait(80);
      return;
    }
    await cdp.evaluate("document.getElementById('tbTheme').click()");
    await wait(180);
  }
  const now = await cdp.evaluate("document.documentElement.getAttribute('data-theme')");
  if (now !== theme) throw new Error('主题切换失败: 期望 ' + theme + ' 实际 ' + now);
}

async function shot(cdp, name) {
  // 截图前清掉上一用例遗留的 toast / tooltip，避免遮挡页面（不影响任何断言读值）
  await cdp.evaluate(`(() => {
    document.querySelectorAll('#toastHost .toast').forEach((t) => t.remove());
    document.querySelectorAll('.tooltip').forEach((t) => t.remove());
  })()`).catch(() => { });
  await wait(80);
  const s = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  fs.writeFileSync(path.join(OUT, name + '.png'), Buffer.from(s.result.data, 'base64'));
}

async function auditPage(cdp, report, label) {
  const audit = await cdp.evaluate(AUDIT);
  report.push(`=== ${label} ===`);
  report.push(`theme=${audit.theme} logLines=${audit.logLines} topClip=${audit.topClip}px ellipsis=${audit.ellipsisCount} pageScroll=${JSON.stringify(audit.pageScroll)}`);
  report.push(...audit.issues.map((x) => '!! ' + x));
  if (!audit.issues.length) report.push('ok: 无溢出/裁切/缺失');
  const errs = (audit.errors || []).filter(Boolean);
  if (errs.length) report.push(...errs.map((x) => '!! console.error: ' + x));
  return audit;
}

async function main() {
  const server = await startServer();
  const cdpPort = await freePort();
  const browser = await launch(cdpPort);
  const cdp = await connect(cdpPort);
  const report = [];
  try {
    await cdp.send('Page.enable');
    await cdp.send('Runtime.enable');
    await cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: MOCK });
    await cdp.send('Page.navigate', { url: `http://127.0.0.1:${server.port}/index.html` });

    const started = Date.now();
    let ready = false;
    while (Date.now() - started < 15000) {
      const ok = await cdp.evaluate("!!(document.querySelector('.page-title') && !document.getElementById('splash'))").catch(() => false);
      if (ok) { ready = true; break; }
      await wait(150);
    }
    if (!ready) {
      const dbg = await cdp.evaluate(`(() => ({
        splash: (document.getElementById('splashSub') || {}).textContent,
        pageHTML: (document.getElementById('page') || {}).innerHTML ? document.getElementById('page').innerHTML.slice(0, 800) : '',
        body: (document.body.innerText || '').slice(0, 500),
        errors: window.__mockErrors || [],
        hosted: !!(window.chrome && window.chrome.webview)
      }))()`).catch((e) => ({ evalError: String(e) }));
      throw new Error('页面未完成启动: ' + JSON.stringify(dbg));
    }
    await wait(700);

    /* ---------------------------------------------------------------- Phase 1 首页 */
    await setTheme(cdp, 'dark');
    for (const size of SIZES) {
      await cdp.send('Emulation.setDeviceMetricsOverride', { width: size.w, height: size.h, deviceScaleFactor: 1, mobile: false });
      await wait(220);
      await shot(cdp, `dash-dark@${size.label}`);
      const a = await auditPage(cdp, report, `${size.label} dark dash`);
      if (a.theme !== 'dark') report.push('!! 首页应为 dark');
    }

    // 长文案压测（P0-4：.NET SDK 卡在 1080 宽下不得裁切）
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1080, height: 700, deviceScaleFactor: 1, mobile: false });
    await wait(200);
    const longSdk = await cdp.evaluate(`(() => {
      const t = document.querySelector('.stat-card.compact .s-text');
      if (!t) return { found: false };
      const card = t.closest('.stat-card');
      const orig = t.textContent;
      t.textContent = '.NET SDK: 已就绪 v10.0.100';
      const cs = getComputedStyle(t);
      const r = t.getBoundingClientRect();
      const cr = card.getBoundingClientRect();
      const res = {
        found: true,
        clipped: cs.whiteSpace === 'nowrap' && t.scrollWidth > t.clientWidth + 1,
        overflowY: r.bottom > cr.bottom + 1 || r.top < cr.top - 1,
        overflowX: r.right > cr.right + 1,
        h: Math.round(r.height)
      };
      t.textContent = orig;
      return res;
    })()`);
    report.push('sdk-long(1080): ' + JSON.stringify(longSdk));
    if (longSdk.found && (longSdk.clipped || longSdk.overflowY || longSdk.overflowX)) {
      report.push('!! .NET SDK 长文案在 1080 宽下被裁切');
    }

    // 浅色首页
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await wait(200);
    await setTheme(cdp, 'light');
    await shot(cdp, 'dash-light@1280x840');
    const auditLight = await auditPage(cdp, report, '1280x840 light dash');
    if (auditLight.theme !== 'light') report.push('!! 点击 #tbTheme 后 data-theme 未切换为 light');
    if (auditLight.logBg !== 'rgb(245, 245, 247)') report.push('!! 浅色主题下日志底应跟随主题 rgb(245, 245, 247)，实际 ' + auditLight.logBg);

    /* ---------------------------------------------------------------- v2.20 配色回归（palette-dark / palette-light） */
    // 控件色 = 老版 Ant Design 深色；日志行色 = 旧 MainForm 原值，两套互不跟随。
    const PALETTE = {
      dark: {
        bg: 'rgb(20, 20, 20)', surface: 'rgb(28, 28, 28)', surface2: 'rgb(35, 35, 35)',
        border: 'rgb(43, 43, 43)', borderStrong: 'rgb(66, 66, 66)', text: 'rgb(220, 220, 220)',
        primary: 'rgb(22, 104, 220)', success: 'rgb(73, 170, 25)', danger: 'rgb(220, 68, 70)',
        warning: 'rgb(216, 150, 20)', info: 'rgb(19, 168, 168)',
        btnSuccess: 'rgb(73, 170, 25)', btnDanger: 'rgb(220, 68, 70)',
        btnWarning: 'rgb(216, 150, 20)', btnPrimary: 'rgb(22, 104, 220)'
      },
      light: {
        bg: 'rgb(255, 255, 255)', surface: 'rgb(255, 255, 255)', surface2: 'rgb(245, 245, 247)',
        border: 'rgb(227, 227, 232)', borderStrong: 'rgb(154, 154, 165)', text: 'rgb(31, 31, 35)',
        primary: 'rgb(33, 150, 243)', success: 'rgb(76, 175, 80)', danger: 'rgb(244, 67, 54)',
        warning: 'rgb(255, 152, 0)', info: 'rgb(0, 188, 212)',
        btnSuccess: 'rgb(76, 175, 80)', btnDanger: 'rgb(244, 67, 54)',
        btnWarning: 'rgb(255, 152, 0)', btnPrimary: 'rgb(33, 150, 243)'
      }
    };
    // 日志行色：深色保持旧 MainForm 原值；浅色用浅底可读的深色版本（日志区跟随主题）
    const LOG_COLORS = {
      dark: { success: 'rgb(40, 167, 69)', error: 'rgb(220, 53, 69)', warning: 'rgb(253, 126, 20)', bg: 'rgb(24, 24, 28)' },
      light: { success: 'rgb(26, 127, 55)', error: 'rgb(207, 19, 34)', warning: 'rgb(178, 94, 9)', bg: 'rgb(245, 245, 247)' }
    };

    async function paletteProbe() {
      // 造出三行真实日志，读它们的计算色（而不是只读 CSS 变量）
      await cdp.evaluate(`(() => {
        window.__mock.emitLog('success', 'palette probe success');
        window.__mock.emitLog('warning', 'palette probe warning');
        window.__mock.emitLog('error', 'palette probe error');
      })()`);
      await wait(200);
      return cdp.evaluate(`(() => {
        const de = document.documentElement;
        const cs = getComputedStyle(de);
        const tok = (n) => {
          const v = cs.getPropertyValue(n).trim().toLowerCase();
          const m = /^#([0-9a-f]{6})$/.exec(v);
          if (!m) return v;
          const n2 = parseInt(m[1], 16);
          return 'rgb(' + ((n2 >> 16) & 255) + ', ' + ((n2 >> 8) & 255) + ', ' + (n2 & 255) + ')';
        };
        const colorOf = (sel, prop) => {
          const el = document.querySelector(sel);
          return el ? getComputedStyle(el)[prop] : null;
        };
        const lastOf = (cls) => {
          const ls = Array.from(document.querySelectorAll('.log-line' + cls));
          return ls.length ? getComputedStyle(ls[ls.length - 1]).color : null;
        };
        const app = document.querySelector('.app');
        const card = document.querySelector('#page .card');
        const lb = document.querySelector('.log-body');
        const pureBlack = [];
        for (const el of document.querySelectorAll('#page *, .sidebar *, .titlebar *, .log-panel *')) {
          const st = getComputedStyle(el);
          if (st.display === 'none' || st.visibility === 'hidden') continue;
          if (st.backgroundColor === 'rgb(0, 0, 0)') {
            const r = el.getBoundingClientRect();
            if (r.width > 4 && r.height > 4) pureBlack.push((el.className || el.tagName) + ' ' + Math.round(r.width) + 'x' + Math.round(r.height));
          }
        }
        return {
          theme: de.getAttribute('data-theme'),
          tokens: {
            bg: tok('--bg'), surface: tok('--surface'), surface2: tok('--surface-2'),
            border: tok('--border'), borderStrong: tok('--border-strong'), text: tok('--text'),
            primary: tok('--primary'), success: tok('--success'), danger: tok('--danger'),
            warning: tok('--warning'), info: tok('--info'), logBg: tok('--log-bg')
          },
          els: {
            app: app ? getComputedStyle(app).backgroundColor : null,
            card: card ? getComputedStyle(card).backgroundColor : null,
            bodyText: getComputedStyle(document.body).color,
            logBg: lb ? getComputedStyle(lb).backgroundColor : null
          },
          btns: {
            success: colorOf('.btn.success', 'backgroundColor'),
            successText: colorOf('.btn.success', 'color'),
            danger: colorOf('.btn.danger', 'backgroundColor'),
            warning: colorOf('.btn.warning', 'backgroundColor'),
            primary: colorOf('.btn.primary', 'backgroundColor')
          },
          logs: {
            success: lastOf('.lv-success'),
            warning: lastOf('.lv-warning'),
            error: lastOf('.lv-error'),
            successBg: colorOf('.log-line.lv-success', 'backgroundColor')
          },
          pureBlack: pureBlack.slice(0, 6)
        };
      })()`);
    }

    function checkPalette(name, probe, want) {
      const bad = [];
      const eq = (label, got, exp) => { if (got !== exp) bad.push(label + '=' + got + '（期望 ' + exp + '）'); };
      eq('--bg', probe.tokens.bg, want.bg);
      eq('--surface', probe.tokens.surface, want.surface);
      eq('--surface-2', probe.tokens.surface2, want.surface2);
      eq('--border', probe.tokens.border, want.border);
      eq('--border-strong', probe.tokens.borderStrong, want.borderStrong);
      eq('--text', probe.tokens.text, want.text);
      eq('--primary', probe.tokens.primary, want.primary);
      eq('--success', probe.tokens.success, want.success);
      eq('--danger', probe.tokens.danger, want.danger);
      eq('--warning', probe.tokens.warning, want.warning);
      eq('--info', probe.tokens.info, want.info);
      eq('--log-bg', probe.tokens.logBg, (LOG_COLORS[probe.theme] || LOG_COLORS.dark).bg);
      eq('.app background', probe.els.app, want.bg);
      eq('#page .card background', probe.els.card, want.surface);
      eq('body color', probe.els.bodyText, want.text);
      eq('.btn.success bg', probe.btns.success, want.btnSuccess);
      eq('.btn.danger bg', probe.btns.danger, want.btnDanger);
      eq('.btn.warning bg', probe.btns.warning, want.btnWarning);
      eq('.btn.primary bg', probe.btns.primary, want.btnPrimary);
      eq('.btn.success color', probe.btns.successText, 'rgb(255, 255, 255)');
      const logWant = LOG_COLORS[probe.theme] || LOG_COLORS.dark;
      eq('.log-line success color', probe.logs.success, logWant.success);
      eq('.log-line warning color', probe.logs.warning, logWant.warning);
      eq('.log-line error color', probe.logs.error, logWant.error);
      // 日志行不允许自带色块（只上色文字），否则会盖住 --log-bg
      eq('.log-line.success background-color', probe.logs.successBg, 'rgba(0, 0, 0, 0)');
      eq('.log-body bg', probe.els.logBg, logWant.bg);
      report.push(name + ': ' + JSON.stringify(probe));
      if (bad.length) report.push('!! ' + name + ' 配色不符: ' + bad.join('；'));
      if (probe.pureBlack.length) report.push('!! ' + name + ' 出现纯黑块: ' + JSON.stringify(probe.pureBlack));
      return bad.length === 0;
    }

    /* ---------------------------------------------------------------- v2.20 多开：服务端运行中也能开始游戏 */
    // 多开 DLL 的用法是「服务端常驻 + 反复拉起客户端」，前端不得再拦截「开始游戏」。
    await cdp.evaluate("document.querySelector('[data-nav=\"dash\"]').click()");
    await wait(240);
    await cdp.evaluate("window.__mock.setServerRunning(true)");
    await wait(420);
    const multiBefore = await cdp.evaluate("window.__mock.calls.length");
    await cdp.evaluate(`(() => {
      const b = Array.from(document.querySelectorAll('#page button.btn.success')).find((x) => x.textContent.indexOf('开始游戏') >= 0);
      if (b) b.click();
    })()`);
    let multiOk = false;
    for (let i = 0; i < 40; i++) {
      const calls = await cdp.evaluate("(window.__mock.calls || []).slice()");
      if (calls.slice(multiBefore).some((c) => c.method === 'server.start')) { multiOk = true; break; }
      await wait(100);
    }
    const multiToast = await cdp.evaluate("Array.from(document.querySelectorAll('.toast')).map(t => t.textContent).join(' | ')");
    report.push('play-while-running: ' + JSON.stringify({ called: multiOk, toast: multiToast }));
    if (!multiOk) report.push('!! 服务端运行中点击「开始游戏」未调用 server.start（多开被前端拦截）');
    if (String(multiToast).indexOf('无需重复启动') >= 0) report.push('!! 仍在提示「无需重复启动」，多开被拦截');
    await cdp.evaluate("window.__mock.setServerRunning(false)");
    await wait(220);

    await setTheme(cdp, 'dark');
    await wait(280);
    checkPalette('palette-dark', await paletteProbe(), PALETTE.dark);

    await setTheme(cdp, 'light');
    await wait(280);
    checkPalette('palette-light', await paletteProbe(), PALETTE.light);
    // 清掉 palette 探针日志，避免污染浅色首页截图
    await cdp.evaluate("document.querySelectorAll('.log-body .log-line').forEach((el) => { if (el.textContent.indexOf('palette probe') >= 0) el.remove(); })");
    await wait(120);
    await shot(cdp, 'dash-light@1280x840');
    await auditPage(cdp, report, '1280x840 light dash (palette)');

    // 标题栏两个主题入口
    const verToggle = await cdp.evaluate(`(() => {
      const before = document.documentElement.getAttribute('data-theme');
      const v = document.getElementById('tbVer');
      if (!v) return { found: false, before };
      v.click();
      const after = document.documentElement.getAttribute('data-theme');
      v.click();
      return { found: true, before, after, restored: document.documentElement.getAttribute('data-theme') };
    })()`);
    report.push('tbVer-toggle: ' + JSON.stringify(verToggle));
    if (!verToggle.found) report.push('!! 未找到 #tbVer');
    else if (verToggle.after === verToggle.before) report.push('!! #tbVer 点击未切换主题（P0-2 要求两个入口都可用）');

    // 开始更新 = 一键增量（不得弹确认框）
    const linesBefore = await cdp.evaluate("document.querySelectorAll('.log-body .log-line').length");
    await cdp.evaluate("document.getElementById('btnUpdate').click()");
    await wait(450);
    const updMid = await cdp.evaluate(`(() => ({
      modal: !!document.querySelector('.modal-mask'),
      progressVisible: !document.getElementById('logProgress').classList.contains('hidden'),
      barWidth: document.getElementById('logProgressBar').style.width,
      label: document.getElementById('logProgressLabel').textContent,
      spinner: !!document.querySelector('#btnUpdate .spinner')
    }))()`);
    await wait(1500);
    const linesAfter = await cdp.evaluate("document.querySelectorAll('.log-body .log-line').length");
    report.push('update: ' + JSON.stringify(Object.assign({ linesBefore, linesAfter }, updMid)));
    if (updMid.modal) report.push('!! 开始更新弹出了确认框（P0-9 要求一键增量，无弹窗）');
    if (!updMid.progressVisible) report.push('!! 开始更新后未显示进度条');
    if (!(linesAfter > linesBefore)) report.push('!! 开始更新后日志行数未增加');

    // 不适用操作必须可点并提示
    const p7 = await cdp.evaluate(`(() => {
      const btn = Array.from(document.querySelectorAll('#page .btn')).find((b) => b.textContent.trim() === '停止服务端');
      const before = document.querySelectorAll('.log-body .log-line').length;
      const disabled = !!(btn && btn.disabled);
      if (btn) btn.click();
      return { found: !!btn, disabled, before };
    })()`);
    await wait(300);
    const p7after = await cdp.evaluate(`(() => {
      const ls = document.querySelectorAll('.log-body .log-line');
      return {
        after: ls.length,
        toast: !!document.querySelector('#toastHost .toast'),
        lastLog: ls.length ? ls[ls.length - 1].textContent : ''
      };
    })()`);
    report.push('p0-7: ' + JSON.stringify(Object.assign({}, p7, p7after)));
    if (!p7.found) report.push('!! 未找到「停止服务端」按钮');
    if (p7.disabled) report.push('!! 「停止服务端」在未运行时被禁用（P0-7 要求可点并提示）');
    if (!(p7after.after > p7.before)) report.push('!! 不适用操作未写入日志（P0-7）');
    if (!p7after.toast) report.push('!! 不适用操作未弹出提示（P0-7）');

    /* ---------------------------------------------------------------- Phase 2 存档页 */
    await setTheme(cdp, 'dark');
    await gotoPage(cdp, 'archive', '存档管理');
    await wait(420);

    for (const size of SIZES) {
      await cdp.send('Emulation.setDeviceMetricsOverride', { width: size.w, height: size.h, deviceScaleFactor: 1, mobile: false });
      await wait(220);
      await shot(cdp, `archive-dark@${size.label}`);
      await auditPage(cdp, report, `${size.label} dark archive`);
    }

    const arcFacts = await cdp.evaluate(`(() => {
      const rows = Array.from(document.querySelectorAll('.arc-tbl tbody tr'));
      const dataRows = rows.filter((r) => r.dataset && r.dataset.name);
      const ths = Array.from(document.querySelectorAll('.arc-tbl thead th'));
      return {
        rows: dataRows.length,
        names: dataRows.slice(0, 3).map((r) => r.dataset.name),
        currentRows: dataRows.filter((r) => r.classList.contains('current')).length,
        activeRows: dataRows.filter((r) => r.classList.contains('active')).length,
        rowH: dataRows[0] ? Math.round(dataRows[0].getBoundingClientRect().height) : 0,
        headers: ths.map((t) => t.textContent.trim()),
        sticky: ths[0] ? getComputedStyle(ths[0]).position : '',
        currentText: (document.querySelector('.arc-info [data-bind="current"]') || {}).textContent,
        backupText: (document.querySelector('.arc-info [data-bind="backupCount"]') || {}).textContent,
        sortArrow: (document.querySelector('.arc-tbl [data-bind="sortArrow"]') || {}).textContent,
        dropText: (document.querySelector('.arc-drop-text') || {}).textContent,
        hasImportZb: !!document.querySelector('#btnArcImport'),
        themeMenuCss: getComputedStyle(document.documentElement).getPropertyValue('--primary').trim()
      };
    })()`);
    report.push('archive-facts: ' + JSON.stringify(arcFacts));
    if (arcFacts.rows < 5) report.push('!! 存档表格行数不足 5（mock 数据未渲染完整）');
    if (arcFacts.currentRows !== 1) report.push('!! 存档「使用中」行应恰好 1 行，实际 ' + arcFacts.currentRows);
    if (arcFacts.activeRows !== 0) report.push('!! 未点击时不应有 .active 选中行（实际 ' + arcFacts.activeRows + '）');
    if (arcFacts.rowH !== 40) report.push('!! 存档行高应为 40px，实际 ' + arcFacts.rowH);
    if (arcFacts.sticky !== 'sticky') report.push('!! 存档表头未 sticky');
    if (arcFacts.currentText !== 'inventory.db (2.3 MB)') report.push('!! 信息栏「当前」不符: ' + arcFacts.currentText);
    if (arcFacts.backupText !== '1') report.push('!! 信息栏「备份数」不符: ' + arcFacts.backupText);
    if (arcFacts.sortArrow !== '↑') report.push('!! 默认排序箭头应 ↑（宿主 ascending:true），实际 ' + arcFacts.sortArrow);
    if (String(arcFacts.dropText || '').indexOf('拖拽 .db 文件到此处') < 0) report.push('!! 拖拽区文案缺失');

    // 排序：点「修改时间」表头 → 箭头翻转且首行变化
    const sortBefore = await cdp.evaluate("(document.querySelector('.arc-tbl tbody tr[data-name]') || {}).dataset.name");
    await cdp.evaluate("document.querySelector('.arc-tbl th.arc-thsort').click()");
    await wait(220);
    const sortAfter = await cdp.evaluate(`(() => ({
      first: (document.querySelector('.arc-tbl tbody tr[data-name]') || {}).dataset.name,
      arrow: (document.querySelector('.arc-tbl [data-bind="sortArrow"]') || {}).textContent
    }))()`);
    report.push('archive-sort: ' + JSON.stringify({ before: sortBefore, after: sortAfter }));
    if (sortAfter.arrow !== '↓') report.push('!! 排序后箭头未翻转');
    if (sortAfter.first === sortBefore) report.push('!! 排序后首行未变化（排序无效）');

    // 右键菜单（主题化）
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await wait(160);
    const menuInfo = await cdp.evaluate(`(() => {
      const row = document.querySelector('.arc-tbl tbody tr[data-name]');
      const r = row.getBoundingClientRect();
      row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(r.left + 240), clientY: Math.round(r.top + 16), button: 2 }));
      const m = document.querySelector('.ctx-menu');
      if (!m) return { open: false };
      const cs = getComputedStyle(m);
      return {
        open: true,
        items: Array.from(m.querySelectorAll('.menu-item')).map((x) => x.textContent.trim()),
        systemMenu: false,
        bg: cs.backgroundColor,
        z: cs.zIndex,
        inBody: m.parentElement === document.body,
        hot: !!m.querySelector('.menu-item.hot')
      };
    })()`);
    await wait(120);
    await shot(cdp, 'archive-menu@1280x840');
    report.push('archive-menu: ' + JSON.stringify(menuInfo));
    if (!menuInfo.open) report.push('!! 右键未弹出主题化菜单');
    else {
      if (JSON.stringify(menuInfo.items) !== JSON.stringify(['切换存档', '重命名存档'])) {
        report.push('!! 右键菜单项与旧 UI 不符: ' + JSON.stringify(menuInfo.items));
      }
      if (!menuInfo.inBody) report.push('!! 右键菜单未挂在 body 上');
      if (!menuInfo.hot) report.push('!! 右键菜单打开时未高亮第一个可用项（键盘可达性）');
      if (menuInfo.systemMenu) report.push('!! 出现了系统右键菜单');
    }
    // 键盘导航：ArrowDown 应移动高亮，Enter 应触发选中
    const menuKeys = await cdp.evaluate(`(() => {
      const m = document.querySelector('.ctx-menu');
      if (!m) return { open: false };
      const hot = () => { const h = m.querySelector('.menu-item.hot'); return h ? h.textContent.trim() : null; };
      const before = hot();
      const ev = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true });
      document.dispatchEvent(ev);
      const afterDown = hot();
      return { open: true, before, afterDown, moved: before !== afterDown };
    })()`);
    report.push('archive-menu-keys: ' + JSON.stringify(menuKeys));
    if (!menuKeys.open) report.push('!! 右键菜单未打开（键盘导航用例）');
    else if (!menuKeys.moved) report.push('!! 右键菜单 ArrowDown 未移动高亮');
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await wait(120);
    const menuClosed = await cdp.evaluate("!document.querySelector('.ctx-menu')");
    report.push('archive-menu-esc: ' + menuClosed);
    if (!menuClosed) report.push('!! Esc 未关闭右键菜单');

    // 自绘 tooltip：鼠标悬停 500ms 后出现，且不是原生 title
    const tipInfo = await cdp.evaluate(`(async () => {
      const el = document.getElementById('tbTheme');
      const r = el.getBoundingClientRect();
      el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, clientX: r.left + 5, clientY: r.top + 5 }));
      await new Promise((res) => setTimeout(res, 620));
      const tip = document.querySelector('.tooltip');
      const cs = tip ? getComputedStyle(tip) : null;
      return {
        exists: !!tip,
        visible: tip ? parseFloat(cs.opacity) > 0.5 : false,
        text: tip ? tip.textContent : null,
        nativeTitle: el.hasAttribute('title')
      };
    })()`);
    report.push('tooltip: ' + JSON.stringify(tipInfo));
    if (!tipInfo.exists || !tipInfo.visible) report.push('!! 自绘 tooltip 未显示');
    if (tipInfo.nativeTitle) report.push('!! 仍在使用原生 title 属性');
    await cdp.evaluate("document.querySelectorAll('.tooltip').forEach((t) => t.classList.remove('show'))");

    // 文件浏览器（导入存档）
    await cdp.evaluate("document.getElementById('btnArcImport').click()");
    await wait(400);
    const fbInfo = await cdp.evaluate(`(() => {
      const ov = document.querySelector('.ov-mask .ov-panel.ov-fb');
      if (!ov) return { open: false };
      const rows = ov.querySelectorAll('.fb-row').length;
      const dd = ov.querySelector('.dropdown .dd-label');
      return {
        open: true,
        rows,
        title: (ov.querySelector('.ov-title') || {}).textContent,
        driveLabel: dd ? dd.textContent : '',
        hasSelect: !!ov.querySelector('select'),
        listH: Math.round((ov.querySelector('.fb-list') || {}).clientHeight || 0),
        bodyH: Math.round((ov.querySelector('.ov-body') || {}).clientHeight || 0)
      };
    })()`);
    await wait(120);
    await shot(cdp, 'archive-filebrowser@1280x840');
    report.push('archive-filebrowser: ' + JSON.stringify(fbInfo));
    if (!fbInfo.open) report.push('!! 导入存档未打开内置文件浏览器');
    else {
      if (!(fbInfo.rows > 0)) report.push('!! 文件浏览器没有列出目录/文件');
      if (fbInfo.hasSelect) report.push('!! 文件浏览器出现原生 <select>');
      if (!fbInfo.driveLabel || fbInfo.driveLabel === '请选择') report.push('!! 文件浏览器盘符下拉未选中当前盘: ' + fbInfo.driveLabel);
      if (fbInfo.listH < 260) report.push('!! 文件浏览器列表高度塌陷: ' + fbInfo.listH + '/' + fbInfo.bodyH);
    }
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await wait(160);
    const fbClosed = await cdp.evaluate("!document.querySelector('.ov-fb')");
    report.push('archive-filebrowser-esc: ' + fbClosed);
    if (!fbClosed) report.push('!! Esc 未关闭文件浏览器');

    // 服务端运行中：存档操作必须被阻止（toast + 日志，不弹文件浏览器）
    await cdp.evaluate("window.__mock.setServerRunning(true)");
    const guardBefore = await cdp.evaluate("document.querySelectorAll('.log-body .log-line').length");
    await cdp.evaluate("document.getElementById('btnArcStore').click()");
    await wait(300);
    const guardRes = await cdp.evaluate(`(() => ({
      lines: document.querySelectorAll('.log-body .log-line').length,
      toasts: Array.from(document.querySelectorAll('#toastHost .toast')).map((t) => t.textContent),
      ov: !!document.querySelector('.ov-mask'),
      lastLog: (() => { const ls = document.querySelectorAll('.log-body .log-line'); return ls.length ? ls[ls.length-1].textContent : ''; })()
    }))()`);
    report.push('archive-server-guard: ' + JSON.stringify({ before: guardBefore, after: guardRes }));
    if (guardRes.lines <= guardBefore) report.push('!! 服务端运行中未写拦截日志');
    if (String(guardRes.lastLog).indexOf('服务端运行中，操作已阻止') < 0) report.push('!! 拦截日志文案不符旧 UI');
    if (!guardRes.toasts.some((t) => String(t).indexOf('服务端正在运行') >= 0)) {
      report.push('!! 服务端运行中未弹提示: ' + JSON.stringify(guardRes.toasts));
    }
    if (guardRes.ov) report.push('!! 服务端运行中仍弹出了输入框');
    await cdp.evaluate("window.__mock.setServerRunning(false)");

    // 竞态：前端状态快照仍「未运行」，但宿主判定运行中 → 必须收到 blocked 并弹主题化提示，且不卡死
    await cdp.evaluate("window.__mock.setServerRunningSilent(true)");
    const raceBefore = await cdp.evaluate("document.querySelectorAll('.log-body .log-line').length");
    await cdp.evaluate("document.getElementById('btnArcStore').click()");
    await wait(260);
    // 走完输入框（预检放行后才会弹）
    const racePrompt = await cdp.evaluate("!!document.querySelector('.ov-prompt')");
    if (racePrompt) {
      await cdp.evaluate(`(() => {
        const b = Array.from(document.querySelectorAll('.ov-prompt .ov-foot .btn')).find((x) => x.textContent.trim() === '储存');
        if (b) b.click();
      })()`);
      await wait(420);
    }
    const raceRes = await cdp.evaluate(`(() => {
      const masks = Array.from(document.querySelectorAll('.modal-mask .modal'));
      const m = masks.length ? masks[masks.length - 1] : null;
      return {
        promptOpened: ${racePrompt ? 'true' : 'false'},
        blockedModal: !!m,
        title: m ? (m.querySelector('.m-title') || {}).textContent : null,
        text: m ? (m.querySelector('.m-text') || {}).textContent : null,
        lines: document.querySelectorAll('.log-body .log-line').length,
        hung: false
      };
    })()`);
    report.push('archive-blocked-modal: ' + JSON.stringify(Object.assign({ before: raceBefore }, raceRes)));
    if (!raceRes.promptOpened) report.push('!! 竞态用例未走到输入框（前端预检误拦）');
    if (!raceRes.blockedModal) report.push('!! 宿主返回 blocked 时未弹主题化提示');
    else {
      if (raceRes.title !== '服务端运行中') report.push('!! blocked 弹窗标题应为「服务端运行中」，实际 ' + raceRes.title);
      if (String(raceRes.text).indexOf('目前服务端正在运行') < 0) report.push('!! blocked 弹窗正文未使用宿主 error: ' + raceRes.text);
    }
    if (raceRes.lines <= raceBefore) report.push('!! 竞态 blocked 未写日志');
    await cdp.evaluate(`(() => {
      const b = Array.from(document.querySelectorAll('.modal-mask .m-foot .btn')).find((x) => x.textContent.trim() === '知道了');
      if (b) b.click();
    })()`);
    await wait(160);
    const raceClosed = await cdp.evaluate("!document.querySelector('.modal-mask')");
    report.push('archive-blocked-modal-closed: ' + raceClosed);
    if (!raceClosed) report.push('!! blocked 弹窗无法关闭');
    await cdp.evaluate("window.__mock.setServerRunningSilent(false)");

    // 浅色存档页
    await setTheme(cdp, 'light');
    await wait(180);
    await shot(cdp, 'archive-light@1280x840');
    await auditPage(cdp, report, '1280x840 light archive');

    // 拖入 .db：在非存档页也应直接换挡（旧 UI 行为）
    await gotoPage(cdp, 'dash', '开始');
    await wait(160);
    const dropBefore = await cdp.evaluate("document.querySelectorAll('.log-body .log-line').length");
    const dropPathJson = JSON.stringify('E:/Game/S4A21_CN/AUM管理组件/存档管理/切换库/新生种子存档/inventory.db');
    await cdp.evaluate('window.__mock.emitDrop(' + dropPathJson + ')');
    await wait(320);
    const dropAfter = await cdp.evaluate(`(() => {
      const ls = document.querySelectorAll('.log-body .log-line');
      return {
        lines: ls.length,
        last: ls.length ? ls[ls.length - 1].textContent : '',
        toast: Array.from(document.querySelectorAll('#toastHost .toast')).map((t) => t.textContent)
      };
    })()`);
    report.push('drop-anywhere: ' + JSON.stringify({ before: dropBefore, after: dropAfter }));
    if (dropAfter.lines <= dropBefore) report.push('!! 非存档页拖入 .db 未写日志');
    if (!dropAfter.toast.some((t) => String(t).indexOf('拖拽换挡完成') >= 0)) report.push('!! 非存档页拖入 .db 未提示换挡完成');

    /* ---------------------------------------------------------------- Phase 2 DLL 页 */
    await setTheme(cdp, 'dark');
    await gotoPage(cdp, 'dll', 'DLL 扩展');
    await wait(420);

    for (const size of SIZES) {
      await cdp.send('Emulation.setDeviceMetricsOverride', { width: size.w, height: size.h, deviceScaleFactor: 1, mobile: false });
      await wait(220);
      await shot(cdp, `dll-dark@${size.label}`);
      await auditPage(cdp, report, `${size.label} dark dll`);
    }

    // 1080x700：允许滚动，但滚动条必须真的出现、所有行可达
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1080, height: 700, deviceScaleFactor: 1, mobile: false });
    await wait(220);
    const dllScroll1080 = await cdp.evaluate(`(() => {
      const sc = document.querySelector('.dll-scroll');
      const rows = Array.from(document.querySelectorAll('.dll-row'));
      const last = rows.length ? rows[rows.length - 1] : null;
      const sr = sc.getBoundingClientRect();
      const lr = last ? last.getBoundingClientRect() : null;
      return {
        ch: sc.clientHeight, sh: sc.scrollHeight,
        sbWidth: sc.offsetWidth - sc.clientWidth,
        overflowY: getComputedStyle(sc).overflowY,
        lastVisible: lr ? (lr.top >= sr.top - 1 && lr.bottom <= sr.bottom + 1) : null,
        rows: rows.length
      };
    })()`);
    report.push('dll-scroll-1080: ' + JSON.stringify(dllScroll1080));
    if (dllScroll1080.rows !== 10) report.push('!! 1080 下 DLL 行数应为 10，实际 ' + dllScroll1080.rows);
    if (dllScroll1080.sh <= dllScroll1080.ch) {
      report.push('!! 1080 下内容应超出容器以触发滚动（sh=' + dllScroll1080.sh + ' ch=' + dllScroll1080.ch + '）');
    } else if (dllScroll1080.sbWidth < 6) {
      report.push('!! 1080 下滚动条不可见（宽 ' + dllScroll1080.sbWidth + 'px）');
    }
    // 滚到底部，确认第 2 个自定义扩展可达
    const reach = await cdp.evaluate(`(() => {
      const sc = document.querySelector('.dll-scroll');
      sc.scrollTop = sc.scrollHeight;
      return new Promise((res) => requestAnimationFrame(() => {
        const rows = Array.from(document.querySelectorAll('.dll-row.custom'));
        const last = rows[rows.length - 1];
        const sr = sc.getBoundingClientRect();
        const lr = last ? last.getBoundingClientRect() : null;
        res({ customRows: rows.length, visible: lr ? (lr.top >= sr.top - 1 && lr.bottom <= sr.bottom + 1) : false, scrollTop: Math.round(sc.scrollTop) });
      }));
    })()`);
    report.push('dll-reach-bottom-1080: ' + JSON.stringify(reach));
    if (reach.customRows !== 2) report.push('!! 自定义扩展应 2 行，实际 ' + reach.customRows);
    if (!reach.visible) report.push('!! 滚到底后第 2 个自定义扩展仍不可见');

    // 交互用例统一在 1280x840 下跑，避免沿用上一个尺寸导致截图/坐标漂移
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await wait(200);

    const dllFacts = await cdp.evaluate(`(() => {
      const rows = Array.from(document.querySelectorAll('.dll-group[data-group="managed"] .dll-row'));
      const cust = Array.from(document.querySelectorAll('.dll-group[data-group="custom"] .dll-row'));
      const first = rows[0];
      return {
        managed: rows.length,
        custom: cust.length,
        firstFile: first ? first.dataset.file : '',
        firstHasGrip: first ? !!first.querySelector('.dll-grip') && !first.querySelector('.grip-none') : null,
        firstLocked: first ? first.classList.contains('locked') : null,
        grips: rows.filter((r) => r.querySelector('.dll-grip:not(.grip-none)')).length,
        gripsCust: cust.filter((r) => r.querySelector('.dll-grip')).length,
        switches: document.querySelectorAll('.dll-group .switch').length,
        editBtns: document.querySelectorAll('.dll-edit').length,
        status: (document.querySelector('[data-bind="status"]') || {}).textContent,
        head: (document.querySelector('.dll-listhead') || {}).textContent,
        ops: Array.from(document.querySelectorAll('.dll-ops .btn')).map((b) => b.textContent.trim()),
        opNote: (document.querySelector('.dll-opnote') || {}).textContent.slice(0, 60),
        opTip: (document.querySelector('.dll-opnote') || {}).getAttribute('data-tip') || '',
        missing: document.querySelectorAll('.dll-name.missing, .dll-desc.missing').length,
        rowGeom: (() => {
          const r = rows[1];
          if (!r) return null;
          const g = r.querySelector('.dll-grip, .grip-none');
          const c = r.querySelector('.dll-ctl');
          const sw = r.querySelector('.switch');
          const nm = r.querySelector('.dll-name');
          const box = (x) => x ? x.getBoundingClientRect() : null;
          const G = box(g), C = box(c), S = box(sw), N = box(nm), R = r.getBoundingClientRect();
          return {
            rowH: Math.round(R.height),
            gripRight: G ? Math.round(G.right) : null,
            ctlLeft: C ? Math.round(C.left) : null,
            ctlW: C ? Math.round(C.width) : null,
            swLeft: S ? Math.round(S.left) : null,
            swW: S ? Math.round(S.width) : null,
            nameLeft: N ? Math.round(N.left) : null,
            overlap: (S && N) ? Math.round(S.right - N.left) : null,
            on: sw ? sw.classList.contains('on') : null,
            knob: (() => {
              const k = r.querySelector('.switch .knob');
              const B = k ? k.getBoundingClientRect() : null;
              return B ? { left: Math.round(B.left), right: Math.round(B.right), top: Math.round(B.top), bg: getComputedStyle(k).backgroundColor } : null;
            })(),
            trackBg: sw ? getComputedStyle(sw.querySelector('.track')).backgroundColor : null,
            gripText: g ? g.textContent.trim() : null,
            gripSvg: g ? !!g.querySelector('svg') : null,
            box: (() => {
              const card = document.querySelector('.dll-card');
              const sc = document.querySelector('.dll-scroll');
              const cr = card ? card.getBoundingClientRect() : null;
              const sr = sc ? sc.getBoundingClientRect() : null;
              return {
                cardTop: cr ? Math.round(cr.top) : null, cardBottom: cr ? Math.round(cr.bottom) : null,
                scrollTop: sr ? Math.round(sr.top) : null, scrollBottom: sr ? Math.round(sr.bottom) : null,
                scrollCH: sc ? sc.clientHeight : null, scrollSH: sc ? sc.scrollHeight : null,
                scrollOverflow: sc ? getComputedStyle(sc).overflowY : null,
                headH: (() => { const e = document.querySelector('.dll-listhead'); return e ? Math.round(e.getBoundingClientRect().height) : null; })(),
                opsH: (() => { const e = document.querySelector('.dll-ops'); return e ? Math.round(e.getBoundingClientRect().height) : null; })(),
                statusH: (() => { const e = document.querySelector('.dll-status'); return e ? Math.round(e.getBoundingClientRect().height) : null; })(),
                capH: (() => { const e = document.querySelector('.dll-cap'); return e ? Math.round(e.getBoundingClientRect().height) : null; })(),
                pageH: (() => { const e = document.querySelector('.dll-page'); return e ? Math.round(e.getBoundingClientRect().height) : null; })()
              };
            })()
          };
        })()
      };
    })()`);
    report.push('dll-facts: ' + JSON.stringify(dllFacts));
    if (dllFacts.managed !== 8) report.push('!! 受管插件应为 8 行，实际 ' + dllFacts.managed);
    if (dllFacts.custom !== 2) report.push('!! 自定义扩展应为 2 行，实际 ' + dllFacts.custom);
    if (dllFacts.firstFile !== 'GameNative.dll') report.push('!! GameNative 未固定首位: ' + dllFacts.firstFile);
    if (dllFacts.firstHasGrip !== false || dllFacts.firstLocked !== true) report.push('!! GameNative 行不应有拖拽手柄且应锁定');
    if (dllFacts.grips !== 7) report.push('!! 受管组拖拽手柄应为 7 个，实际 ' + dllFacts.grips);
    if (dllFacts.gripsCust !== 2) report.push('!! 自定义组拖拽手柄应为 2 个，实际 ' + dllFacts.gripsCust);
    if (dllFacts.switches !== 10) report.push('!! DLL 页开关数应为 10（8 受管 + 2 自定义），实际 ' + dllFacts.switches);
    if (dllFacts.editBtns !== 4) report.push('!! 可编辑配置应为 4 个（AutoFire/DpsMeter/CombatPower/ImeFix），实际 ' + dllFacts.editBtns);
    if (String(dllFacts.status).indexOf('受管插件已启用 8 / 8') < 0) report.push('!! 状态行文案不符: ' + dllFacts.status);
    if (JSON.stringify(dllFacts.ops) !== JSON.stringify(['应用更改', '添加扩展', '删除扩展'])) {
      report.push('!! 操作行按钮与旧 UI 不符: ' + JSON.stringify(dllFacts.ops));
    }
    if (!dllFacts.opNote || dllFacts.opNote.indexOf('添加/删除扩展') < 0) report.push('!! 操作说明短句缺失: ' + dllFacts.opNote);
    if (!dllFacts.opTip || dllFacts.opTip.indexOf('添加扩展 = 选择自己的插件挂载') < 0) report.push('!! 操作说明完整 tooltip 缺失');
    if (dllFacts.missing !== 0) report.push('!! 已安装状态不应有「文件缺失」红字');

    // 应用更改确认框
    await cdp.evaluate("document.getElementById('btnDllApply').click()");
    await wait(350);
    const applyModal = await cdp.evaluate(`(() => {
      const m = document.querySelector('.modal-mask .modal');
      if (!m) return { open: false };
      return {
        open: true,
        title: (m.querySelector('.m-title') || {}).textContent,
        text: (m.querySelector('.m-text') || {}).textContent,
        buttons: Array.from(m.querySelectorAll('.m-foot .btn')).map((b) => b.textContent.trim())
      };
    })()`);
    report.push('dll-apply-confirm: ' + JSON.stringify(applyModal));
    if (!applyModal.open) report.push('!! 应用更改未弹主题化确认');
    else {
      if (applyModal.title !== '应用更改') report.push('!! 应用确认标题不符: ' + applyModal.title);
      if (String(applyModal.text || '').indexOf('将直写游戏根目录 GameGaurd.ini 的 [Plugins] 插件列表') < 0) {
        report.push('!! 应用确认正文未使用旧 UI 文案');
      }
      if (applyModal.buttons.indexOf('取消') < 0) report.push('!! 应用确认缺少取消按钮');
    }
    await cdp.evaluate(`(() => { const b = Array.from(document.querySelectorAll('.modal-mask .m-foot .btn')).find((x) => x.textContent.trim() === '取消'); if (b) b.click(); })()`);
    await wait(160);

    // INI 分组编辑器
    await cdp.evaluate("document.querySelector('.dll-edit').click()");
    await wait(450);
    const iniInfo = await cdp.evaluate(`(() => {
      const ov = document.querySelector('.ov-mask .ov-panel.ov-ini');
      if (!ov) return { open: false };
      const secs = Array.from(ov.querySelectorAll('.ini-sec-name')).map((x) => x.textContent.trim());
      return {
        open: true,
        title: (ov.querySelector('.ov-title') || {}).textContent,
        sections: secs,
        rows: ov.querySelectorAll('.ini-row').length,
        bools: ov.querySelectorAll('.ini-row .switch').length,
        nums: ov.querySelectorAll('.ini-numbox').length,
        texts: ov.querySelectorAll('.ini-text').length,
        notes: ov.querySelectorAll('.ini-note').length,
        addGroup: Array.from(ov.querySelectorAll('.ov-head-extra .btn')).map((b) => b.textContent.trim()),
        delHtml: (() => { const d = ov.querySelector('.ini-del'); return d ? d.outerHTML.slice(0, 200) : null; })(),
        delText: (() => { const d = ov.querySelector('.ini-del'); return d ? d.textContent : null; })(),
        delBox: (() => { const d = ov.querySelector('.ini-del'); return d ? Array.from(d.getClientRects()).length : null; })(),
        hasSelect: !!ov.querySelector('select'),
        geom: (() => {
          const rows = Array.from(ov.querySelectorAll('.ini-row'));
          const rights = rows.map((r) => {
            const d = r.querySelector('.ini-del');
            return d ? Math.round(d.getBoundingClientRect().right) : null;
          });
          const ctl = rows.map((r) => {
            const c = r.querySelector('.ini-ctl');
            return c ? Math.round(c.getBoundingClientRect().width) : null;
          });
          const wrap = ov.querySelector('.ini-wrap');
          const body = ov.querySelector('.ov-body');
          const heights = rows.map((r) => Math.round(r.getBoundingClientRect().height));
          const align = rows.map((r) => {
            const d = r.querySelector('.ini-del'); const c = r.querySelector('.ini-ctl');
            if (!d || !c) return null;
            return Math.round((d.getBoundingClientRect().top + d.getBoundingClientRect().height / 2)
              - (c.getBoundingClientRect().top + c.getBoundingClientRect().height / 2));
          });
          const delBoxes = rows.map((r) => {
            const d = r.querySelector('.ini-del');
            if (!d) return null;
            const b = d.getBoundingClientRect();
            const cs = getComputedStyle(d);
            return { x: Math.round(b.left), y: Math.round(b.top), w: Math.round(b.width), h: Math.round(b.height), sw: d.scrollWidth, cw: d.clientWidth,
              fs: cs.fontSize, pad: cs.padding, ls: cs.letterSpacing, ws: cs.whiteSpace, ff: cs.fontFamily };
          }).filter(Boolean);
          return {
            delBoxes: delBoxes.slice(0, 2),
            rowHeights: Array.from(new Set(heights)),
            centerDelta: Array.from(new Set(align)),
            delRights: Array.from(new Set(rights)),
            ctlWidths: Array.from(new Set(ctl)),
            wrapSW: wrap ? wrap.scrollWidth : 0,
            wrapCW: wrap ? wrap.clientWidth : 0,
            bodySW: body ? body.scrollWidth : 0,
            bodyCW: body ? body.clientWidth : 0
          };
        })()
      };
    })()`);
    await wait(160);
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 2, mobile: false });
    await wait(180);
    await shot(cdp, 'dll-inieditor@2x');
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await wait(180);
    await shot(cdp, 'dll-inieditor@1280x840');
    report.push('dll-inieditor: ' + JSON.stringify(iniInfo));
    if (!iniInfo.open) report.push('!! 编辑配置未打开 INI 分组编辑器');
    else {
      if (iniInfo.title !== '自动连发 · AutoFire.ini') {
        report.push('!! INI 编辑器标题应沿用旧 UI「插件名 · 文件名」，实际: ' + iniInfo.title);
      }
      // 分组标题沿用旧 UI：'▍ ' + 节名（不含方括号）；[节] 之前的键归入「(主配置)」
      if (JSON.stringify(iniInfo.sections) !== JSON.stringify(['▍ (主配置)', '▍ General', '▍ AutoFire', '▍ DpsMeter', '▍ CombatPower'])) {
        report.push('!! INI 分组不符: ' + JSON.stringify(iniInfo.sections));
      }
      if (iniInfo.rows < 13) report.push('!! INI 键行过少: ' + iniInfo.rows);
      if (iniInfo.bools < 4) report.push('!! INI 布尔开关过少: ' + iniInfo.bools);
      if (iniInfo.nums < 6) report.push('!! INI 数字框过少: ' + iniInfo.nums);
      if (iniInfo.hasSelect) report.push('!! INI 编辑器出现原生 <select>');
      if (iniInfo.addGroup.indexOf('添加分组') < 0) report.push('!! INI 编辑器缺少「添加分组」');
    }
    // 修改后标题必须出现「（已修改，保存后写入）」，关闭时必须二次确认
    const dirtyInfo = await cdp.evaluate(`(() => {
      const input = document.querySelector('.ov-ini .ini-text');
      if (!input) return { found: false };
      input.value = 'zh_TW';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      return { found: true, title: (document.querySelector('.ov-ini .ov-title') || {}).textContent };
    })()`);
    report.push('dll-inieditor-dirty: ' + JSON.stringify(dirtyInfo));
    if (!dirtyInfo.found) report.push('!! INI 编辑器缺少文本框');
    else if (String(dirtyInfo.title).indexOf('（已修改，保存后写入）') < 0) {
      report.push('!! 修改后标题未显示「（已修改，保存后写入）」: ' + dirtyInfo.title);
    }

    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await wait(260);
    const confirmInfo = await cdp.evaluate(`(() => {
      const m = document.querySelector('.modal-mask .modal');
      return {
        open: !!m,
        title: m ? (m.querySelector('.m-title') || {}).textContent : null,
        stillOpen: !!document.querySelector('.ov-ini')
      };
    })()`);
    report.push('dll-inieditor-discard-confirm: ' + JSON.stringify(confirmInfo));
    if (!confirmInfo.open) report.push('!! 未保存关闭时没有二次确认');
    if (!confirmInfo.stillOpen) report.push('!! 二次确认期间编辑器已被关闭');
    // 取消放弃 → 编辑器仍在
    await cdp.evaluate(`(() => { const b = Array.from(document.querySelectorAll('.modal-mask .m-foot .btn')).find((x) => x.textContent.trim() === '取消'); if (b) b.click(); })()`);
    await wait(200);
    const stillOpen = await cdp.evaluate("!!document.querySelector('.ov-ini')");
    report.push('dll-inieditor-cancel-discard: ' + stillOpen);
    if (!stillOpen) report.push('!! 取消放弃修改后编辑器不应关闭');
    // 再次 Esc → 放弃修改 → 关闭
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await wait(220);
    await cdp.evaluate(`(() => { const b = Array.from(document.querySelectorAll('.modal-mask .m-foot .btn')).find((x) => x.textContent.trim() === '放弃修改'); if (b) b.click(); })()`);
    await wait(260);
    const iniClosed = await cdp.evaluate("!document.querySelector('.ov-ini')");
    report.push('dll-inieditor-esc: ' + iniClosed);
    if (!iniClosed) report.push('!! 确认放弃后 INI 编辑器未关闭');

    // 浅色 DLL 页
    await setTheme(cdp, 'light');
    await wait(180);
    await shot(cdp, 'dll-light@1280x840');
    await auditPage(cdp, report, '1280x840 light dll');

    /* ---------------------------------------------------------------- Phase 2 交互回归 */
    await gotoPage(cdp, 'dash', '开始');
    await wait(120);
    await gotoPage(cdp, 'dll', 'DLL 扩展');
    await wait(360);

    // 拖拽排序：真实指针序列（CDP 鼠标 → Chromium 合成 pointerdown/pointermove/pointerup），
    // 与宿主 app-shots 的 dll-drag-native 同一条路径；不再使用 HTML5 drag 事件。
    const dragOrder = (group) => cdp.evaluate(
      `Array.from(document.querySelectorAll('.dll-group[data-group="${group}"] .dll-row')).map((r) => r.dataset.file)`);
    const gripPoint = (group, file) => cdp.evaluate(`(() => {
      const row = Array.from(document.querySelectorAll('.dll-group[data-group="${group}"] .dll-row'))
        .find((r) => r.dataset.file === ${JSON.stringify(file)});
      if (!row) return null;
      const g = row.querySelector('.dll-grip');
      if (!g) return null;
      const b = g.getBoundingClientRect();
      return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) };
    })()`);
    const rowMidY = (group, file) => cdp.evaluate(`(() => {
      const row = Array.from(document.querySelectorAll('.dll-group[data-group="${group}"] .dll-row'))
        .find((r) => r.dataset.file === ${JSON.stringify(file)});
      if (!row) return null;
      const b = row.getBoundingClientRect();
      return Math.round(b.top + b.height / 2);
    })()`);
    const rowTopY = (group, file) => cdp.evaluate(`(() => {
      const row = Array.from(document.querySelectorAll('.dll-group[data-group="${group}"] .dll-row'))
        .find((r) => r.dataset.file === ${JSON.stringify(file)});
      if (!row) return null;
      return Math.round(row.getBoundingClientRect().top + 3);
    })()`);

    async function pointerDrag(from, toY, onMid) {
      await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: from.x, y: from.y });
      await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: from.x, y: from.y, button: 'left', buttons: 1, clickCount: 1 });
      const steps = 8;
      for (let i = 1; i <= steps; i++) {
        const y = Math.round(from.y + (toY - from.y) * i / steps);
        await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: from.x, y, button: 'left', buttons: 1 });
        await wait(24);
        if (i === 3 && onMid) await onMid();
      }
      await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: from.x, y: toY, button: 'left', buttons: 0, clickCount: 1 });
    }

    // ---- 受管组：第 2 行向下拖过第 4 行（顺序必须改变）
    await cdp.evaluate("window.__mock.clearCalls()");
    const dragBefore = await dragOrder('managed');
    const fromGrip = await gripPoint('managed', dragBefore[1]);
    const toY = await rowMidY('managed', dragBefore[4]);
    if (!fromGrip) report.push('!! 受管第 2 行缺少拖拽手柄');
    else {
      let mid = null;
      await pointerDrag(fromGrip, toY, async () => {
        mid = await cdp.evaluate(`(() => ({
          dragging: document.querySelectorAll('.dll-row.dragging').length,
          indicator: document.querySelectorAll('.dll-row.drop-before, .dll-row.drop-after').length,
          cursor: getComputedStyle(document.body).cursor
        }))()`);
      });
      await wait(340);
      const dragAfter = await dragOrder('managed');
      const dragCalls = await cdp.evaluate("(window.__mock.calls || []).filter((c) => c.method === 'dll.setOrder')");
      const dragStatus = await cdp.evaluate("(document.querySelector('[data-bind=\"status\"]') || {}).textContent || ''");
      const dragEnd = await cdp.evaluate(`(() => {
        const first = document.querySelector('.dll-group[data-group="managed"] .dll-row');
        return {
          dragging: document.querySelectorAll('.dll-row.dragging').length,
          indicator: document.querySelectorAll('.dll-row.drop-before, .dll-row.drop-after').length,
          first: first ? first.dataset.file : null,
          firstHasGrip: first ? !!first.querySelector('.dll-grip:not(.grip-none)') : null,
          bodyDragging: document.body.classList.contains('dll-dragging')
        };
      })()`);
      report.push('dll-drag: ' + JSON.stringify({ before: dragBefore, mid, after: dragAfter, calls: dragCalls.length, status: dragStatus, end: dragEnd }));
      if (JSON.stringify(dragAfter) === JSON.stringify(dragBefore)) report.push('!! 指针拖拽未改变受管顺序');
      if (!mid || mid.dragging !== 1) report.push('!! 拖拽过程中未出现 .dragging');
      if (!mid || mid.indicator < 1) report.push('!! 拖拽过程中未显示 2px 插入线');
      if (dragEnd.dragging !== 0) report.push('!! 拖拽结束后 .dragging 未清除');
      if (dragEnd.indicator !== 0) report.push('!! 拖拽结束后插入线未清除');
      if (dragEnd.bodyDragging) report.push('!! 拖拽结束后 body.dll-dragging 未清除');
      if (dragCalls.length !== 1) report.push('!! dll.setOrder 应恰好调用 1 次，实际 ' + dragCalls.length);
      report.push('dll-drag-status: ' + dragStatus);
      if (dragStatus.indexOf('顺序已调整（未应用）') < 0) report.push('!! 拖拽排序后状态行未提示「顺序已调整（未应用）」: ' + dragStatus);
      if (dragEnd.first !== 'GameNative.dll') report.push('!! 换位后 GameNative 不在首位: ' + dragEnd.first);
      if (dragEnd.firstHasGrip !== false) report.push('!! GameNative 行不应有拖拽手柄');
    }

    // ---- 受管组：末行往上拖到 GameNative 之上 → 必须被夹回它后面
    await cdp.evaluate("window.__mock.clearCalls()");
    const clampBefore = await dragOrder('managed');
    const clampFrom = await gripPoint('managed', clampBefore[clampBefore.length - 1]);
    const topY = await rowTopY('managed', clampBefore[0]);
    if (!clampFrom) report.push('!! 受管末行缺少拖拽手柄');
    else {
      await pointerDrag(clampFrom, topY);
      await wait(320);
      const clampAfter = await dragOrder('managed');
      report.push('dll-drag-clamp: ' + JSON.stringify({ before: clampBefore, after: clampAfter }));
      if (clampAfter[0] !== 'GameNative.dll') report.push('!! 向上拖拽越过了固定首位的 GameNative');
      if (JSON.stringify(clampAfter) === JSON.stringify(clampBefore)) report.push('!! 向上拖拽未换位（应只被夹到 GameNative 之后）');
    }

    // ---- 自定义组：末行向上拖到首行
    await cdp.evaluate("window.__mock.clearCalls()");
    const custBefore = await dragOrder('custom');
    const custFrom = await gripPoint('custom', custBefore[custBefore.length - 1]);
    const custTo = await rowTopY('custom', custBefore[0]);
    if (!custFrom) report.push('!! 自定义扩展行缺少拖拽手柄');
    else {
      await pointerDrag(custFrom, custTo);
      await wait(320);
      const custAfter = await dragOrder('custom');
      const custCalls = await cdp.evaluate("(window.__mock.calls || []).filter((c) => c.method === 'dll.setOrder')");
      report.push('dll-drag-custom: ' + JSON.stringify({ before: custBefore, after: custAfter, calls: custCalls.length }));
      if (JSON.stringify(custAfter) === JSON.stringify(custBefore)) report.push('!! 自定义组指针拖拽未换位');
      if (custCalls.length !== 1) report.push('!! 自定义组 dll.setOrder 应恰好 1 次，实际 ' + custCalls.length);
      const managedNow = await dragOrder('managed');
      if (managedNow[0] !== 'GameNative.dll' || managedNow.length !== clampBefore.length) {
        report.push('!! 拖自定义组影响了受管组: ' + JSON.stringify(managedNow));
      }
    }

    // 添加扩展：内置文件浏览器 + filter=dll（只列 .dll/.ini）
    await cdp.evaluate("document.getElementById('btnDllAdd').click()");
    await wait(420);
    const addFb = await cdp.evaluate(`(() => {
      const ov = document.querySelector('.ov-fb');
      if (!ov) return { open: false };
      const names = Array.from(ov.querySelectorAll('.fb-row:not(.dir) .fb-name')).map((x) => x.textContent);
      const fsw = ov.querySelector('.fb-bar .switch');
      return {
        open: true, names,
        filterLabel: fsw ? (fsw.querySelector('.sw-label') || {}).textContent : null,
        filterOn: fsw ? fsw.classList.contains('on') : null,
        header: Array.from(ov.querySelectorAll('.fb-listhead span')).map((x) => x.textContent),
        okDisabled: (() => { const b = Array.from(ov.querySelectorAll('.ov-foot .btn')).find((x) => x.textContent.indexOf('挂载') >= 0); return b ? b.disabled : null; })(),
        hasSelect: !!ov.querySelector('select'),
        hasNumber: !!ov.querySelector('input[type="number"]'),
        listH: Math.round((ov.querySelector('.fb-list') || {}).clientHeight || 0)
      };
    })()`);
    report.push('dll-add-filebrowser: ' + JSON.stringify(addFb));
    if (!addFb.open) report.push('!! 添加扩展未打开内置文件浏览器');
    else {
      if (String(addFb.filterLabel).indexOf('仅显示 .dll / .ini') < 0) report.push('!! 添加扩展过滤器提示不符: ' + addFb.filterLabel);
      if (addFb.filterOn !== true) report.push('!! 添加扩展（filter=dll）时过滤开关应为开');
      if (JSON.stringify(addFb.header) !== JSON.stringify(['名称', '类型', '修改时间'])) {
        report.push('!! 文件浏览器表头不符: ' + JSON.stringify(addFb.header));
      }
      if (addFb.okDisabled !== true) report.push('!! 未选中文件时「挂载此文件」应禁用');
      if (!addFb.names.length) report.push('!! 添加扩展文件浏览器没有列出任何插件文件');
      if (addFb.names.some((n) => !/\.(dll|ini)$/i.test(n))) report.push('!! 添加扩展列出了非 dll/ini 文件: ' + addFb.names.join(','));
      if (addFb.hasSelect) report.push('!! 添加扩展出现原生 select');
    }
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await wait(200);

    // 删除扩展：主题化列表 + 同时删除文件开关
    await cdp.evaluate("document.getElementById('btnDllDel').click()");
    await wait(320);
    const pickInfo = await cdp.evaluate(`(() => {
      const ov = document.querySelector('.ov-pick');
      if (!ov) return { open: false };
      const rows = Array.from(ov.querySelectorAll('.pick-row'));
      return {
        open: true,
        title: (ov.querySelector('.ov-title') || {}).textContent,
        rows: rows.length,
        firstSel: rows.length ? rows[0].classList.contains('sel') : false,
        hasFileSwitch: !!ov.querySelector('.switch'),
        note: (ov.querySelector('.pick-note') || {}).textContent.slice(0, 40),
        buttons: Array.from(ov.querySelectorAll('.ov-foot .btn')).map((b) => b.textContent.trim())
      };
    })()`);
    report.push('dll-delete-picker: ' + JSON.stringify(pickInfo));
    if (!pickInfo.open) report.push('!! 删除扩展未打开主题化列表');
    else {
      if (pickInfo.rows !== 2) report.push('!! 删除列表行数应为 2，实际 ' + pickInfo.rows);
      if (!pickInfo.firstSel) report.push('!! 删除列表默认未选中首项');
      if (!pickInfo.hasFileSwitch) report.push('!! 删除列表缺少「同时删除游戏根目录文件」开关');
      if (pickInfo.buttons.indexOf('删除') < 0) report.push('!! 删除列表缺少删除按钮');
    }
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await wait(160);
    const pickClosed = await cdp.evaluate("!document.querySelector('.ov-pick')");
    report.push('dll-delete-picker-esc: ' + pickClosed);
    if (!pickClosed) report.push('!! Esc 未关闭删除列表');

    // 空态：切换 mock 为未安装（先回到深色，截图文件名 dll-empty 约定为深色）
    await setTheme(cdp, 'dark');
    await cdp.evaluate("window.__mock.setInstalled(false)");
    await gotoPage(cdp, 'dash', '开始');
    await wait(120);
    await gotoPage(cdp, 'dll', 'DLL 扩展');
    await wait(420);
    const emptyInfo = await cdp.evaluate(`(() => {
      const empty = document.querySelector('.dll-empty');
      const visible = empty && getComputedStyle(empty).display !== 'none';
      return {
        visible: !!visible,
        cap: (document.querySelector('.dll-empty-cap') || {}).textContent,
        tip: (document.querySelector('.dll-empty-tip') || {}).textContent,
        rows: document.querySelectorAll('.dll-group .dll-row').length,
        status: (document.querySelector('[data-bind="status"]') || {}).textContent,
        listHidden: getComputedStyle(document.querySelector('.dll-scroll')).display === 'none'
      };
    })()`);
    await shot(cdp, 'dll-empty@1280x840');
    report.push('dll-empty: ' + JSON.stringify(emptyInfo));
    if (!emptyInfo.visible) report.push('!! 未安装时未显示空态');
    if (emptyInfo.cap !== '—— 未安装 DLL 扩展 ——') report.push('!! 空态标题不符: ' + emptyInfo.cap);
    if (String(emptyInfo.tip || '').indexOf('请到【设置与关于】页点击【新DLL安装】') < 0) report.push('!! 空态引导文案不符');
    if (emptyInfo.rows !== 0) report.push('!! 未安装时不应渲染插件行');
    if (!emptyInfo.listHidden) report.push('!! 未安装时插件列表未隐藏');
    await auditPage(cdp, report, '1280x840 dark dll-empty');

    // 未安装时添加/删除/应用必须拦截并引导
    const guards = await cdp.evaluate(`(() => {
      const before = document.querySelectorAll('.log-body .log-line').length;
      document.getElementById('btnDllAdd').click();
      document.getElementById('btnDllDel').click();
      document.getElementById('btnDllApply').click();
      return { before };
    })()`);
    await wait(400);
    const guardAfter = await cdp.evaluate(`(() => ({
      after: document.querySelectorAll('.log-body .log-line').length,
      toasts: document.querySelectorAll('#toastHost .toast').length,
      ov: !!document.querySelector('.ov-mask')
    }))()`);
    report.push('dll-empty-guards: ' + JSON.stringify(Object.assign({}, guards, guardAfter)));
    if (!(guardAfter.after > guards.before)) report.push('!! 未安装时操作未写日志引导');
    if (!guardAfter.toasts) report.push('!! 未安装时操作未弹提示');
    if (guardAfter.ov) report.push('!! 未安装时仍打开了文件浏览器/选择框');

    // 恢复安装态，供后续人工观察
    await cdp.evaluate("window.__mock.setInstalled(true)");

    /* ---------------------------------------------------------------- Phase 3a：设置与关于 */
    await setTheme(cdp, 'dark');
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await gotoPage(cdp, 'settings', '设置与关于');
    await wait(360);
    await shot(cdp, 'settings-dark@1280x840');
    await auditPage(cdp, report, '1280x840 dark settings');

    const setFacts = await cdp.evaluate(`(() => {
      const links = Array.from(document.querySelectorAll('.set-link'));
      const tools = Array.from(document.querySelectorAll('.set-tool-btn'));
      const card = document.querySelector('.set-page');
      const lr = links.map((x) => { const r = x.getBoundingClientRect(); return { top: Math.round(r.top), bottom: Math.round(r.bottom), name: (x.querySelector('.set-link-name') || {}).textContent }; });
      const pr = card ? card.getBoundingClientRect() : null;
      const page = document.getElementById('page');
      return {
        linkCount: links.length,
        linkNames: links.map((x) => (x.querySelector('.set-link-name') || {}).textContent),
        linkUrls: links.map((x) => x.dataset.url),
        firstTop: lr.length ? lr[0].top : null,
        lastBottom: lr.length ? lr[lr.length - 1].bottom : null,
        pageBottom: pr ? Math.round(pr.bottom) : null,
        tools: tools.map((x) => x.textContent.trim()),
        appName: (document.querySelector('.set-appname') || {}).textContent,
        ver: (document.querySelector('.set-ver') || {}).textContent,
        desc: (document.querySelector('.set-desc') || {}).textContent,
        pageScroll: { sh: page.scrollHeight, ch: page.clientHeight }
      };
    })()`);
    report.push('settings-facts: ' + JSON.stringify(setFacts));
    if (setFacts.linkCount !== 4) report.push('!! 设置页应有 4 条链接，实际 ' + setFacts.linkCount);
    if (JSON.stringify(setFacts.linkNames) !== JSON.stringify(['ServerUI-仓库', 'ServerS4A21-主源仓库', 'GM工具-主源仓库', 'GM 工具'])) {
      report.push('!! 设置页链接名称不符: ' + JSON.stringify(setFacts.linkNames));
    }
    if (JSON.stringify(setFacts.tools) !== JSON.stringify(['进行本地编译', '待定', '新DLL安装', '更新AUM（强制发行包）'])) {
      report.push('!! 设置页实用工具按钮不符: ' + JSON.stringify(setFacts.tools));
    }
    if (setFacts.appName !== 'ServerS4A21 管理器') report.push('!! 关于卡名称不符');
    if (String(setFacts.ver).indexOf('v2.20 · ServerS4A21-AUM') < 0) report.push('!! 关于卡版本行不符: ' + setFacts.ver);

    // 点第 1 条链接 → shell.open {kind:'url'}
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.querySelector('.set-link').click()");
    await wait(260);
    const linkCall = await cdp.evaluate("window.__mock.calls.slice()");
    report.push('settings-link-call: ' + JSON.stringify(linkCall));
    const linkOk = linkCall.some((c) => c.method === 'shell.open' && c.kind === 'url'
      && String(c.value).indexOf('https://github.com/118coder/ServerUI-AUM-S4A21') === 0);
    if (!linkOk) report.push('!! 设置页点链接未调用 shell.open{kind:url}: ' + JSON.stringify(linkCall));

    // 待定 → 只写日志
    const pendingBefore = await cdp.evaluate("document.querySelectorAll('.log-body .log-line').length");
    await cdp.evaluate(`(() => {
      const b = Array.from(document.querySelectorAll('.set-tool-btn')).find((x) => x.textContent.trim() === '待定');
      if (b) b.click();
    })()`);
    await wait(240);
    const pendingAfter = await cdp.evaluate(`(() => {
      const ls = document.querySelectorAll('.log-body .log-line');
      return { lines: ls.length, last: ls.length ? ls[ls.length - 1].textContent : '', modal: !!document.querySelector('.modal-mask') };
    })()`);
    report.push('settings-pending: ' + JSON.stringify({ before: pendingBefore, after: pendingAfter }));
    if (pendingAfter.lines <= pendingBefore) report.push('!! 待定按钮未写日志');
    if (String(pendingAfter.last).indexOf('功能已暂时移除') < 0) report.push('!! 待定日志文案不符: ' + pendingAfter.last);
    if (pendingAfter.modal) report.push('!! 待定按钮不应弹窗');

    // 新DLL安装 → 确认框 → dll.installPatch
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate(`(() => {
      const b = Array.from(document.querySelectorAll('.set-tool-btn')).find((x) => x.textContent.trim() === '新DLL安装');
      if (b) b.click();
    })()`);
    await wait(320);
    const patchModal = await cdp.evaluate(`(() => {
      const m = document.querySelector('.modal-mask .modal');
      return {
        open: !!m,
        title: m ? (m.querySelector('.m-title') || {}).textContent : null,
        text: m ? (m.querySelector('.m-text') || {}).textContent : null,
        buttons: m ? Array.from(m.querySelectorAll('.m-foot .btn')).map((x) => x.textContent.trim()) : []
      };
    })()`);
    report.push('settings-install-confirm: ' + JSON.stringify({ open: patchModal.open, title: patchModal.title, buttons: patchModal.buttons }));
    if (!patchModal.open) report.push('!! 新DLL安装未弹确认框');
    else {
      if (String(patchModal.text || '').indexOf('把 实用工具包') < 0
        || String(patchModal.text || '').indexOf('客户端补丁.zip 完整安装到游戏根目录') < 0) {
        report.push('!! 新DLL安装确认正文不符');
      }
      if (patchModal.buttons.indexOf('开始安装') < 0) report.push('!! 新DLL安装确认缺少「开始安装」');
    }
    // 只点「当前可见」的那个模态里的按钮：历史上多个 modal-mask 叠加时，
    // 全局 querySelector 会命中旧层，导致"点了但没触发"。
    await cdp.evaluate(`(() => {
      const masks = Array.from(document.querySelectorAll('.modal-mask')).filter((m) => m.offsetParent !== null || getComputedStyle(m).display !== 'none');
      const mask = masks[masks.length - 1];
      if (!mask) return false;
      const b = Array.from(mask.querySelectorAll('.m-foot .btn')).find((x) => x.textContent.trim() === '开始安装');
      if (!b) return false;
      b.click();
      return true;
    })()`);
    await wait(460);
    const patchCalls = await cdp.evaluate("window.__mock.calls.slice()");
    const patchToasts = await cdp.evaluate("Array.from(document.querySelectorAll('#toastHost .toast')).map((t) => t.textContent)");
    report.push('settings-install-call: ' + JSON.stringify(patchCalls));
    report.push('settings-install-toast: ' + JSON.stringify(patchToasts));
    if (!patchCalls.some((c) => c.method === 'dll.installPatch')) report.push('!! 确认后未调用 dll.installPatch');
    if (!patchToasts.some((t) => String(t).indexOf('客户端补丁已安装') >= 0)) report.push('!! 安装成功后未 toast 结果');

    // 更新AUM：必须先弹「策略确认框」（当前→目标 / 策略 / 原因 / 包大小），确认后才执行
    await cdp.evaluate("document.querySelector('[data-nav=\"dash\"]').click()");
    await wait(220);
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate(`(() => {
      const b = Array.from(document.querySelectorAll('#page button.btn')).find((x) => x.textContent.indexOf('更新AUM') >= 0 && x.textContent.indexOf('强制') < 0);
      if (b) b.click();
    })()`);
    let aumModal = { open: false };
    for (let i = 0; i < 40; i++) {
      aumModal = await cdp.evaluate(`(() => {
        const masks = Array.from(document.querySelectorAll('.modal-mask'));
        const m = masks[masks.length - 1];
        if (!m) return { open: false };
        return {
          open: true,
          title: (m.querySelector('.m-title') || {}).textContent,
          text: (m.querySelector('.m-text') || {}).textContent,
          buttons: Array.from(m.querySelectorAll('.m-foot .btn')).map((x) => x.textContent.trim())
        };
      })()`);
      if (aumModal.open) break;
      await wait(100);
    }
    report.push('aum-plan-confirm: ' + JSON.stringify(aumModal));
    if (!aumModal.open) report.push('!! 更新AUM 未弹策略确认框');
    else {
      const t = String(aumModal.text || '');
      if (t.indexOf('当前版本：v2.20') < 0 || t.indexOf('目标版本：v2.20.1') < 0) {
        report.push('!! 更新AUM 确认框未写出 当前→目标 版本');
      }
      if (t.indexOf('采用策略：源码编译') < 0) report.push('!! 更新AUM 确认框未写出采用策略');
      if (t.indexOf('判定原因') < 0) report.push('!! 更新AUM 确认框未写出判定原因');
      if (t.indexOf('MB') < 0 && t.indexOf('KB') < 0) report.push('!! 更新AUM 确认框未写出包大小');
      if (t.indexOf('回滚') < 0) report.push('!! 更新AUM 确认框未说明失败会回滚');
    }
    // 确认前不得发起 aum.update
    let preCalls = await cdp.evaluate("(window.__mock.calls || []).slice()");
    if (preCalls.some((c) => c.method === 'aum.update')) report.push('!! 未确认就发起了 aum.update');
    // 取消 → 不更新
    await cdp.evaluate(`(() => {
      const masks = Array.from(document.querySelectorAll('.modal-mask'));
      const m = masks[masks.length - 1];
      if (!m) return;
      const b = Array.from(m.querySelectorAll('.m-foot .btn')).find((x) => x.textContent.trim() === '取消');
      if (b) b.click();
    })()`);
    await wait(320);
    const afterCancel = await cdp.evaluate("(window.__mock.calls || []).slice()");
    report.push('aum-cancel: ' + JSON.stringify({ updates: afterCancel.filter((c) => c.method === 'aum.update').length }));
    if (afterCancel.some((c) => c.method === 'aum.update')) report.push('!! 取消后仍然执行了 aum.update');

    // SDK 已就绪时仍应打开安装包（旧版 IS() 支持修复/覆盖安装，前端不得拦截）
    await cdp.evaluate("document.querySelector('[data-nav=\"dash\"]').click()");
    await wait(220);
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnSdkInstall').click()");
    let sdkCalled = false;
    for (let i = 0; i < 30; i++) {
      const calls = await cdp.evaluate("(window.__mock.calls || []).slice()");
      if (calls.some((c) => c.method === 'sdk.install')) { sdkCalled = true; break; }
      await wait(100);
    }
    report.push('sdk-install-while-ready: ' + JSON.stringify({ called: sdkCalled }));
    if (!sdkCalled) report.push('!! SDK 已就绪时【安装NET.10 SDK】被前端拦截（旧版允许覆盖安装）');

    // 1080 宽：4 条链接必须全部可见且在页面内
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1080, height: 700, deviceScaleFactor: 1, mobile: false });
    await wait(260);
    await shot(cdp, 'settings-dark@1080x700');
    const set1080 = await cdp.evaluate(`(() => {
      const page = document.getElementById('page');
      const pr = page.getBoundingClientRect();
      const links = Array.from(document.querySelectorAll('.set-link'));
      const rects = links.map((x) => x.getBoundingClientRect());
      return {
        pageTop: Math.round(pr.top), pageBottom: Math.round(pr.bottom),
        tops: rects.map((r) => Math.round(r.top)),
        bottoms: rects.map((r) => Math.round(r.bottom)),
        allInside: rects.every((r) => r.top >= pr.top - 1 && r.bottom <= pr.bottom + 1),
        pageScroll: { sh: page.scrollHeight, ch: page.clientHeight }
      };
    })()`);
    report.push('settings-1080: ' + JSON.stringify(set1080));
    if (!set1080.allInside) report.push('!! 设置页 1080 宽下 4 条链接未全部可见');
    await auditPage(cdp, report, '1080x700 dark settings');

    // 浅色
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await wait(180);
    await setTheme(cdp, 'light');
    await wait(200);
    await shot(cdp, 'settings-light@1280x840');
    await auditPage(cdp, report, '1280x840 light settings');

    /* ---------------------------------------------------------------- Phase 3a：调整系统时间 */
    await setTheme(cdp, 'dark');
    await gotoPage(cdp, 'time', '调整系统时间');
    await wait(420);
    await shot(cdp, 'time-dark@1280x840');
    await auditPage(cdp, report, '1280x840 dark time');

    const timeFacts = await cdp.evaluate(`(() => {
      const inp = document.querySelector('.dtp-input');
      return {
        hasNativeDate: document.querySelectorAll('input[type="date"], input[type="datetime-local"]').length,
        inputType: inp ? inp.getAttribute('type') : null,
        value: inp ? inp.value : null,
        readBtn: !!document.getElementById('btnTimeRead'),
        applyBtn: !!document.getElementById('btnTimeApply'),
        quick: Array.from(document.querySelectorAll('.time-ops .btn')).map((b) => b.textContent.trim()),
        syncBtn: (document.getElementById('btnTimeSync') || {}).textContent,
        note: (document.querySelector('.time-note') || {}).textContent,
        now: (document.querySelector('[data-bind="now"]') || {}).textContent,
        status: (document.querySelector('[data-bind="status"]') || {}).textContent
      };
    })()`);
    report.push('time-facts: ' + JSON.stringify(timeFacts));
    if (timeFacts.hasNativeDate !== 0) report.push('!! 时间页出现原生 date/datetime-local 输入');
    if (timeFacts.inputType !== 'text') report.push('!! 目标时间输入框应为 text（自绘）');
    if (!/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(String(timeFacts.value))) report.push('!! 目标时间格式不是 yyyy-MM-dd HH:mm:ss: ' + timeFacts.value);
    if (JSON.stringify(timeFacts.quick) !== JSON.stringify(['应用此时间', '快速调整 2006-01-01', '快速调整 2015-01-01', '快速调整 2017-01-01'])) {
      report.push('!! 时间操作行按钮不符: ' + JSON.stringify(timeFacts.quick));
    }
    if (String(timeFacts.syncBtn).indexOf('同步网络时间（校准为标准时间）') < 0) report.push('!! 网络同步按钮文案不符');
    if (String(timeFacts.note).indexOf('本工具可以调节系统时间') < 0) report.push('!! 网络同步说明文案不符');
    if (!/^当前系统时间：\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(String(timeFacts.now))) report.push('!! 当前系统时间格式不符: ' + timeFacts.now);

    // 同步卡必须撑满页面剩余高度：底边 = 页面内容底边（±2px）
    const timeFill = await cdp.evaluate(`(() => {
      const page = document.querySelector('.time-page');
      const card = document.querySelector('.time-card');
      const status = document.querySelector('.time-status');
      const pr = page.getBoundingClientRect();
      const cr = card.getBoundingClientRect();
      const sr = status ? status.getBoundingClientRect() : null;
      const cs = getComputedStyle(card);
      return {
        pageBottom: Math.round(pr.bottom), cardBottom: Math.round(cr.bottom),
        delta: Math.round(Math.abs(cr.bottom - pr.bottom)),
        statusTop: sr ? Math.round(sr.top) : null,
        statusBottom: sr ? Math.round(sr.bottom) : null,
        cardFlexGrow: cs.flexGrow,
        overflow: cs.overflow,
        cardScrollH: card.scrollHeight, cardClientH: card.clientHeight,
        statusInCard: status ? status.parentElement.classList.contains('time-card') : null
      };
    })()`);
    report.push('time-fill: ' + JSON.stringify(timeFill));
    if (timeFill.delta > 2) {
      report.push('!! 时间页同步卡未撑满（卡片底 ' + timeFill.cardBottom + ' vs 页面底 ' + timeFill.pageBottom + '）');
    }
    if (timeFill.cardScrollH > timeFill.cardClientH + 2) {
      report.push('!! 时间页同步卡内容被裁切（scrollH ' + timeFill.cardScrollH + ' > clientH ' + timeFill.cardClientH + '）');
    }
    if (timeFill.statusBottom !== null && timeFill.statusBottom > timeFill.pageBottom + 2) {
      report.push('!! 时间页状态行超出页面底部');
    }

    // 滚动条 thumb 默认色必须是 --border（不是 --border-strong）
    const sbRule = await cdp.evaluate(`(() => {
      const out = [];
      for (const sh of Array.from(document.styleSheets)) {
        let rules;
        try { rules = sh.cssRules; } catch { continue; }
        for (const r of Array.from(rules || [])) {
          if (!r.selectorText || r.selectorText.indexOf('-webkit-scrollbar-thumb') < 0) continue;
          if (!r.style || !r.style.background) continue;
          out.push({ sel: r.selectorText, bg: r.style.background });
        }
      }
      const cs = getComputedStyle(document.documentElement);
      return {
        theme: document.documentElement.getAttribute('data-theme'),
        border: cs.getPropertyValue('--border').trim(),
        borderStrong: cs.getPropertyValue('--border-strong').trim(),
        rules: out
      };
    })()`);
    report.push('scrollbar-rule: ' + JSON.stringify(sbRule));
    const thumb = (sbRule.rules || []).find((r) => r.sel === '::-webkit-scrollbar-thumb');
    if (!thumb) report.push('!! 未找到 ::-webkit-scrollbar-thumb 规则');
    else if (thumb.bg.indexOf('border-strong') >= 0) report.push('!! 滚动条 thumb 默认色仍为 --border-strong');
    else if (thumb.bg.indexOf('--border') < 0) report.push('!! 滚动条 thumb 默认色应为 --border，实际 ' + thumb.bg);
    else {
      const lum = (hex) => {
        const m = /^#([0-9a-f]{6})$/i.exec(hex || '');
        if (!m) return null;
        const n = parseInt(m[1], 16);
        return 0.299 * ((n >> 16) & 255) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255);
      };
      const lb = lum(sbRule.border), ls = lum(sbRule.borderStrong);
      report.push('scrollbar-lum: border=' + (lb === null ? null : lb.toFixed(1)) + ' borderStrong=' + (ls === null ? null : ls.toFixed(1)));
      if (lb !== null && ls !== null && !(lb < ls)) report.push('!! --border 应比 --border-strong 更暗');
    }

    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnTimeRead').click()");
    await waitForCall(cdp, 'time.get', 4000);
    await wait(120);
    const readCalls = await cdp.evaluate("window.__mock.calls.slice()");
    report.push('time-read-call: ' + JSON.stringify(readCalls));
    if (!readCalls.some((c) => c.method === 'time.get')) report.push('!! 读取系统时间未调用 time.get');

    // 打开日历（自绘）→ 截图
    await cdp.evaluate("document.querySelector('.dtp-btn').click()");
    await wait(280);
    const calInfo = await cdp.evaluate(`(() => {
      const p = document.querySelector('.dtp-panel');
      if (!p) return { open: false };
      return {
        open: true,
        month: (p.querySelector('.dtp-month') || {}).textContent,
        days: p.querySelectorAll('.dtp-day').length,
        weekdays: Array.from(p.querySelectorAll('.dtp-wd')).map((x) => x.textContent),
        hasTimeInputs: p.querySelectorAll('.dtp-num').length,
        today: !!p.querySelector('.dtp-day.today'),
        selected: p.querySelectorAll('.dtp-day.sel').length,
        foot: Array.from(p.querySelectorAll('.dtp-foot .btn')).map((x) => x.textContent.trim()),
        nativePopup: !!document.querySelector('input[type="date"], input[type="datetime-local"]')
      };
    })()`);
    await shot(cdp, 'time-calendar@1280x840');
    report.push('time-calendar: ' + JSON.stringify(calInfo));
    if (!calInfo.open) report.push('!! 日历浮层未打开');
    else {
      if (calInfo.days < 28) report.push('!! 日历天数不足: ' + calInfo.days);
      if (calInfo.hasTimeInputs !== 3) report.push('!! 日历浮层缺时分秒输入，实际 ' + calInfo.hasTimeInputs);
      if (!calInfo.today) report.push('!! 日历未标记今天');
      if (calInfo.selected !== 1) report.push('!! 日历选中日不唯一: ' + calInfo.selected);
      if (calInfo.nativePopup) report.push('!! 日历打开时出现原生日期控件');
    }

    // 在日历里选明确时间（10 日 08:09:10）→ 确定 → 应用 → time.apply
    await cdp.evaluate(`(() => {
      const p = document.querySelector('.dtp-panel');
      const cells = Array.from(p.querySelectorAll('.dtp-day')).filter((x) => !x.classList.contains('other'));
      const d10 = cells.find((x) => x.textContent.trim() === '10') || cells[0];
      d10.click();
      const nums = p.querySelectorAll('.dtp-num');
      nums[0].value = '08'; nums[1].value = '09'; nums[2].value = '10';
      const ok = Array.from(p.querySelectorAll('.dtp-foot .btn')).find((x) => x.textContent.trim() === '确定');
      ok.click();
    })()`);
    await wait(240);
    const afterPick = await cdp.evaluate("document.querySelector('.dtp-input').value");
    report.push('time-picked: ' + afterPick);
    if (!/ 08:09:10$/.test(String(afterPick))) report.push('!! 日历选时间后输入框未更新: ' + afterPick);

    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnTimeApply').click()");
    await waitForCall(cdp, 'time.apply', 4000);
    await wait(160);
    const applyCalls = await cdp.evaluate("window.__mock.calls.slice()");
    const applyStatus = await cdp.evaluate("(document.querySelector('[data-bind=\"status\"]') || {}).textContent");
    report.push('time-apply: ' + JSON.stringify({ calls: applyCalls, status: applyStatus }));
    if (!applyCalls.some((c) => c.method === 'time.apply')) report.push('!! 应用此时间未调用 time.apply');

    // 快速调整 2015 → time.quick
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate(`(() => {
      const b = Array.from(document.querySelectorAll('.time-ops .btn')).find((x) => x.textContent.indexOf('2015') >= 0);
      if (b) b.click();
    })()`);
    await waitForCall(cdp, 'time.quick', 4000);
    await wait(160);
    const quickCalls = await cdp.evaluate("window.__mock.calls.slice()");
    const quickValue = await cdp.evaluate("document.querySelector('.dtp-input').value");
    report.push('time-quick: ' + JSON.stringify({ calls: quickCalls, value: quickValue }));
    if (!quickCalls.some((c) => c.method === 'time.quick' && Number(c.year) === 2015)) report.push('!! 快速调整未调用 time.quick{year:2015}');
    if (String(quickValue).indexOf('2015-01-01') !== 0) report.push('!! 快速调整后输入框未变成 2015-01-01: ' + quickValue);

    // 网络同步 → time.sync
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnTimeSync').click()");
    await waitForCall(cdp, 'time.sync', 4000);
    await wait(200);
    const syncCalls = await cdp.evaluate("window.__mock.calls.slice()");
    report.push('time-sync: ' + JSON.stringify(syncCalls));
    if (!syncCalls.some((c) => c.method === 'time.sync')) report.push('!! 网络同步未调用 time.sync');

    // 1080
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1080, height: 700, deviceScaleFactor: 1, mobile: false });
    await wait(260);
    await shot(cdp, 'time-dark@1080x700');
    await auditPage(cdp, report, '1080x700 dark time');

    // 浅色
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await wait(180);
    await setTheme(cdp, 'light');
    await wait(200);
    await shot(cdp, 'time-light@1280x840');
    await auditPage(cdp, report, '1280x840 light time');

    /* ---------------------------------------------------------------- Phase 3a：使用说明 */
    await setTheme(cdp, 'dark');
    await gotoPage(cdp, 'help', '使用说明');
    await wait(380);
    await shot(cdp, 'help-dark@1280x840');
    await auditPage(cdp, report, '1280x840 dark help');

    const helpFacts = await cdp.evaluate(`(() => {
      const btns = Array.from(document.querySelectorAll('.doc-btn'));
      const left = document.querySelector('.doc-left');
      const scroll = document.querySelector('.doc-left-scroll');
      return {
        buttons: btns.length,
        firstLabel: btns[0] ? btns[0].textContent.trim() : null,
        title: (document.querySelector('.doc-title') || {}).textContent,
        bodyLen: ((document.querySelector('.doc-body') || {}).textContent || '').length,
        leftWidth: left ? Math.round(left.getBoundingClientRect().width) : null,
        scrollable: scroll ? scroll.scrollHeight > scroll.clientHeight : null,
        scrollTop: scroll ? scroll.scrollTop : null,
        firstActive: btns[0] ? btns[0].classList.contains('active') : null
      };
    })()`);
    report.push('help-facts: ' + JSON.stringify(helpFacts));
    if (helpFacts.buttons !== 19) report.push('!! 使用说明应有 19 条，实际 ' + helpFacts.buttons);
    if (helpFacts.firstLabel !== '快速开始') report.push('!! 使用说明首条应为「快速开始」，实际 ' + helpFacts.firstLabel);
    if (helpFacts.title !== '快速开始') report.push('!! 默认右侧标题应为「快速开始」，实际 ' + helpFacts.title);
    if (helpFacts.leftWidth !== 312) report.push('!! 左栏宽度应为 312，实际 ' + helpFacts.leftWidth);
    if (!helpFacts.firstActive) report.push('!! 首条按钮未标记 active');

    // 先把左栏滚到中间，再切条目：scrollTop 必须保持（证明没有重建整页）
    await cdp.evaluate(`(() => {
      const sc = document.querySelector('.doc-left-scroll');
      sc.scrollTop = Math.round((sc.scrollHeight - sc.clientHeight) / 2);
    })()`);
    await wait(140);
    const scrollBefore = await cdp.evaluate("document.querySelector('.doc-left-scroll').scrollTop");
    await cdp.evaluate("document.querySelectorAll('.doc-btn')[1].click()");
    await wait(240);
    const helpSecond = await cdp.evaluate(`(() => ({
      title: (document.querySelector('.doc-title') || {}).textContent,
      bodyLen: ((document.querySelector('.doc-body') || {}).textContent || '').length,
      scrollTop: document.querySelector('.doc-left-scroll').scrollTop,
      flowChildren: document.querySelector('.doc-left-flow').children.length
    }))()`);
    // 截图语义：切到第二条且左栏回到顶部（滚动断言已在上一行完成）
    await cdp.evaluate("document.querySelector('.doc-left-scroll').scrollTop = 0");
    await wait(120);
    await shot(cdp, 'help-second@1280x840');
    report.push('help-second: ' + JSON.stringify({ scrollBefore, scrollAfter: helpSecond.scrollTop, title: helpSecond.title }));
    if (helpSecond.title !== '开始游戏') report.push('!! 使用说明第二条标题应为「开始游戏」，实际 ' + helpSecond.title);
    if (helpSecond.scrollTop !== scrollBefore) report.push('!! 切换条目后左栏滚动位置跳变: ' + scrollBefore + ' -> ' + helpSecond.scrollTop);
    if (!(helpSecond.bodyLen > 50)) report.push('!! 第二条正文为空');

    // _gr 占位符必须替换成运行时游戏根目录（旧 UI 是编译期拼接）
    await cdp.evaluate(`(() => {
      const btns = Array.from(document.querySelectorAll('.doc-btn'));
      const b = btns.find((x) => x.textContent.indexOf('还原原版PVF') >= 0);
      if (b) b.click();
    })()`);
    await wait(220);
    const grBody = await cdp.evaluate("(document.querySelector('.doc-body') || {}).textContent || ''");
    const grLine = (grBody.split(String.fromCharCode(10)).find((l) => l.indexOf('确认后自动覆盖游戏根目录') >= 0) || '');
    report.push('help-gr: ' + JSON.stringify({ hasPlaceholder: grBody.indexOf('{_gr}') >= 0, line: grLine }));
    if (grBody.indexOf('{_gr}') >= 0) report.push('!! 使用说明正文仍残留 {_gr} 占位符');
    if (!grLine || grLine.indexOf('E:') < 0 || grLine.indexOf('S4A21_CN') < 0) {
      report.push('!! 使用说明正文未替换成 ctx.paths.gr: ' + grLine);
    }
    // 回到第二条，保持后续截图语义
    await cdp.evaluate("document.querySelectorAll('.doc-btn')[1].click()");
    await wait(160);

    await setTheme(cdp, 'light');
    // 主题切换有 120ms 颜色过渡；轮询到卡片底色真正落到白色再断言（避免读到过渡中间色）
    {
      const started = Date.now();
      while (Date.now() - started < 3000) {
        const bg = await cdp.evaluate("(document.querySelector('.doc-left') ? getComputedStyle(document.querySelector('.doc-left')).backgroundColor : '')").catch(() => '');
        if (bg === 'rgb(255, 255, 255)') break;
        await wait(80);
      }
    }
    const themeNow = await cdp.evaluate("document.documentElement.getAttribute('data-theme')");
    if (themeNow !== 'light') report.push('!! help 浅色截图时主题不是 light: ' + themeNow);
    const helpColors = await cdp.evaluate(`(() => {
      const l = document.querySelector('.doc-left');
      const r = document.querySelector('.doc-right');
      const cs = (x) => x ? getComputedStyle(x).backgroundColor : null;
      return {
        left: cs(l), right: cs(r),
        leftOpacity: l ? getComputedStyle(l).opacity : null,
        leftFilter: l ? getComputedStyle(l).filter : null,
        body: getComputedStyle(document.body).backgroundColor,
        page: getComputedStyle(document.getElementById('page')).backgroundColor
      };
    })()`);
    report.push('help-light-colors: ' + JSON.stringify(helpColors));
    // 浅色下滚动条同样必须是 --border（浅色 #E3E3E8），不能沿用深色令牌
    const sbLight = await cdp.evaluate(`(() => {
      const cs = getComputedStyle(document.documentElement);
      const out = [];
      for (const sh of Array.from(document.styleSheets)) {
        let rules;
        try { rules = sh.cssRules; } catch { continue; }
        for (const r of Array.from(rules || [])) {
          if (r.selectorText === '::-webkit-scrollbar-thumb' && r.style && r.style.background) out.push(r.style.background);
        }
      }
      return { theme: document.documentElement.getAttribute('data-theme'), border: cs.getPropertyValue('--border').trim(), rule: out[0] || null };
    })()`);
    report.push('scrollbar-light: ' + JSON.stringify(sbLight));
    if (sbLight.theme !== 'light') report.push('!! 浅色滚动条检查时主题不是 light');
    if (String(sbLight.rule).indexOf('--border') < 0) report.push('!! 浅色滚动条 thumb 未使用 --border: ' + sbLight.rule);
    // 浅色下卡片必须仍是 --surface（白），不能被遮罩/过渡污染
    if (helpColors.left !== 'rgb(255, 255, 255)' || helpColors.right !== 'rgb(255, 255, 255)') {
      report.push('!! 浅色使用说明卡片底色不是白色: ' + JSON.stringify(helpColors));
    }
    await shot(cdp, 'help-light@1280x840');
    await auditPage(cdp, report, '1280x840 light help');

    /* ---------------------------------------------------------------- Phase 3a：疑难杂症解惑 */
    await setTheme(cdp, 'dark');
    await gotoPage(cdp, 'tsh', '疑难杂症解惑');
    await wait(380);
    const tshTop = await cdp.evaluate(`(() => {
      const page = document.getElementById('page');
      const head = document.querySelector('.page-head');
      const pr = page.getBoundingClientRect();
      const hr = head ? head.getBoundingClientRect() : null;
      return { scrollTop: page.scrollTop, pageTop: Math.round(pr.top), headTop: hr ? Math.round(hr.top) : null,
        clipped: hr ? hr.top < pr.top - 1 : null };
    })()`);
    report.push('tsh-top: ' + JSON.stringify(tshTop));
    if (tshTop.scrollTop !== 0) report.push('!! 切到疑难页后 #page.scrollTop 应为 0，实际 ' + tshTop.scrollTop);
    if (tshTop.clipped) report.push('!! 疑难页页头被裁切');
    await shot(cdp, 'tsh-dark@1280x840');
    await auditPage(cdp, report, '1280x840 dark tsh');

    const tshFacts = await cdp.evaluate(`(() => {
      const btns = Array.from(document.querySelectorAll('.doc-btn'));
      return {
        buttons: btns.length,
        firstLabel: btns[0] ? btns[0].textContent.trim() : null,
        title: (document.querySelector('.doc-title') || {}).textContent,
        leftWidth: Math.round(document.querySelector('.doc-left').getBoundingClientRect().width)
      };
    })()`);
    report.push('tsh-facts: ' + JSON.stringify(tshFacts));
    if (tshFacts.buttons !== 11) report.push('!! 疑难页应有 11 条，实际 ' + tshFacts.buttons);
    if (tshFacts.firstLabel !== '关于"疑难杂症"') report.push('!! 疑难首条应为「关于"疑难杂症"」，实际 ' + tshFacts.firstLabel);
    if (tshFacts.leftWidth !== 312) report.push('!! 疑难页左栏宽度应为 312，实际 ' + tshFacts.leftWidth);

    await cdp.evaluate(`(() => {
      const sc = document.querySelector('.doc-left-scroll');
      sc.scrollTop = Math.round((sc.scrollHeight - sc.clientHeight) / 2);
    })()`);
    await wait(140);
    const tshScrollBefore = await cdp.evaluate("document.querySelector('.doc-left-scroll').scrollTop");
    await cdp.evaluate("document.querySelectorAll('.doc-btn')[1].click()");
    await wait(240);
    const tshSecond = await cdp.evaluate(`(() => ({
      title: (document.querySelector('.doc-title') || {}).textContent,
      scrollTop: document.querySelector('.doc-left-scroll').scrollTop
    }))()`);
    await cdp.evaluate("document.querySelector('.doc-left-scroll').scrollTop = 0");
    await wait(120);
    await shot(cdp, 'tsh-second@1280x840');
    report.push('tsh-second: ' + JSON.stringify({ scrollBefore: tshScrollBefore, scrollAfter: tshSecond.scrollTop, title: tshSecond.title }));
    if (tshSecond.title !== '回档或登录中断怎么办？') report.push('!! 疑难第二条标题不符: ' + tshSecond.title);
    if (tshSecond.scrollTop !== tshScrollBefore) report.push('!! 疑难切换后左栏滚动位置跳变');

    // 疑难页浅色（v2.20：深浅两套都要留档）。主题有 120ms 颜色过渡，
    // 必须等卡片真正落到白色再截，否则会拍到过渡中间色。
    await setTheme(cdp, 'light');
    {
      const started = Date.now();
      while (Date.now() - started < 3000) {
        const bg = await cdp.evaluate("(document.querySelector('.doc-left') ? getComputedStyle(document.querySelector('.doc-left')).backgroundColor : '')").catch(() => '');
        if (bg === 'rgb(255, 255, 255)') break;
        await wait(80);
      }
    }
    await wait(240);
    await shot(cdp, 'tsh-light@1280x840');
    await auditPage(cdp, report, '1280x840 light tsh');

    /* ---------------------------------------------------------------- Phase 3b：极简 / 经典模式 */
    await setTheme(cdp, 'dark');

    // 干净起点：1280×840 开始页 + full
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await gotoPage(cdp, 'dash', '开始');
    await wait(200);
    await cdp.evaluate("window.__mock.setMode('full')");
    await wait(160);

    // ---- 进入 mini（点标题栏 #btnMini）→ 620×470
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 620, height: 470, deviceScaleFactor: 1, mobile: false });
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnMini').click()");
    await waitForCall(cdp, 'window.mode', 4000);
    await wait(340);
    await shot(cdp, 'mini-dark@620x470');

    const miniFacts = await cdp.evaluate(`(() => {
      const app = document.querySelector('.app');
      const page = document.getElementById('page');
      const vis = (el) => { if (!el) return false; const c = getComputedStyle(el); const r = el.getBoundingClientRect(); return c.display !== 'none' && c.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
      const rows = Array.from(document.querySelectorAll('.mini-page tbody tr[data-name]'));
      const btnMini = document.getElementById('btnMini');
      const btnClassic = document.getElementById('btnClassic');
      const tbl = document.querySelector('.mini-page .arc-tbl-wrap');
      const pr = page.getBoundingClientRect();
      const tr = tbl ? tbl.getBoundingClientRect() : null;
      return {
        dataMode: app.dataset.mode,
        bodyMode: document.body.dataset.mode,
        pageTitle: (document.querySelector('#page .page-title') || {}).textContent || '',
        miniActive: btnMini.classList.contains('active'),
        classicActive: btnClassic.classList.contains('active'),
        ariaMini: btnMini.getAttribute('aria-pressed'),
        sidebarVisible: vis(document.getElementById('sidebar')),
        logVisible: vis(document.getElementById('logWrap')),
        resizerVisible: vis(document.getElementById('logResizer')),
        backBtn: !!document.getElementById('btnMiniBack'),
        playBtn: !!document.getElementById('btnMiniPlay'),
        stopBtn: !!document.getElementById('btnMiniStop'),
        gmBtn: !!document.getElementById('btnMiniGm'),
        storeBtn: !!document.getElementById('btnMiniStore'),
        undoBtn: !!document.getElementById('btnMiniUndo'),
        refreshBtn: !!document.getElementById('btnMiniRefresh'),
        rows: rows.length,
        currentRows: rows.filter((r) => r.classList.contains('current')).length,
        hint: (document.querySelector('.mi-hint') || {}).textContent || '',
        pageSW: page.scrollWidth, pageCW: page.clientWidth,
        docSW: document.documentElement.scrollWidth, docCW: document.documentElement.clientWidth,
        startH: (() => { const b = document.getElementById('btnMiniPlay'); return b ? Math.round(b.getBoundingClientRect().height) : null; })(),
        tableH: tr ? Math.round(tr.height) : 0,
        tableInside: tr ? (tr.top >= pr.top - 1 && tr.bottom <= pr.bottom + 1) : null,
        calls: (window.__mock.calls || []).filter((c) => c.method === 'window.mode').map((c) => c.args && c.args.mode)
      };
    })()`);
    report.push('mode-mini: ' + JSON.stringify(miniFacts));
    if (miniFacts.dataMode !== 'mini') report.push('!! .app[data-mode] 应为 mini，实际 ' + miniFacts.dataMode);
    if (miniFacts.bodyMode !== 'mini') report.push('!! body[data-mode] 应为 mini，实际 ' + miniFacts.bodyMode);
    if (!miniFacts.miniActive) report.push('!! 进入 mini 后 #btnMini 未加 active');
    if (miniFacts.classicActive) report.push('!! 进入 mini 后 #btnClassic 不应 active');
    if (miniFacts.ariaMini !== 'true') report.push('!! #btnMini aria-pressed 应为 true');
    if (miniFacts.sidebarVisible) report.push('!! mini 下侧栏仍可见');
    if (miniFacts.logVisible) report.push('!! mini 下日志面板仍可见');
    if (miniFacts.resizerVisible) report.push('!! mini 下日志 resizer 仍可见');
    if (!miniFacts.backBtn) report.push('!! mini 下缺少「返回完整模式」');
    if (!miniFacts.playBtn || !miniFacts.stopBtn || !miniFacts.gmBtn) report.push('!! mini 缺少 开始游戏/GM工具/停止服务端');
    if (!miniFacts.storeBtn || !miniFacts.undoBtn || !miniFacts.refreshBtn) report.push('!! mini 缺少 储存当前/撤销换挡/刷新');
    if (miniFacts.rows < 5) report.push('!! mini 存档表行数不足: ' + miniFacts.rows);
    if (miniFacts.currentRows !== 1) report.push('!! mini 存档表「使用中」行应恰好 1 行，实际 ' + miniFacts.currentRows);
    if (miniFacts.pageSW > miniFacts.pageCW + 1) report.push('!! mini 页面横向溢出 ' + miniFacts.pageSW + ' > ' + miniFacts.pageCW);
    if (miniFacts.docSW > miniFacts.docCW + 1) report.push('!! mini 文档横向溢出');
    if (miniFacts.startH !== 56) report.push('!! mini「开始游戏」应 56px，实际 ' + miniFacts.startH);
    if (!(miniFacts.tableH > 100)) report.push('!! mini 存档表未占剩余高度: ' + miniFacts.tableH);
    if (miniFacts.tableInside !== true) report.push('!! mini 存档表不在视口内');
    if (miniFacts.hint.indexOf('服务端运行中无法操作存档') < 0) report.push('!! mini 底部提示文案不符: ' + miniFacts.hint);
    if (miniFacts.calls.indexOf('mini') < 0) report.push('!! 点【简】未调用 window.mode{mini}: ' + JSON.stringify(miniFacts.calls));
    await auditPage(cdp, report, '620x470 dark mini');

    // mini 交互：双击存档行 → archive.switch，且选中行高亮唯一
    await cdp.evaluate("window.__mock.clearCalls()");
    const miniDbl = await cdp.evaluate(`(() => {
      const rows = Array.from(document.querySelectorAll('.mini-page tbody tr[data-name]'));
      const row = rows.find((r) => !r.classList.contains('current'));
      if (!row) return { found: false };
      row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      return { found: true, name: row.dataset.name };
    })()`);
    await waitForCall(cdp, 'archive.switch', 4000);
    await wait(280);
    const miniDblAfter = await cdp.evaluate(`(() => ({
      calls: (window.__mock.calls || []).slice(),
      activeRows: Array.from(document.querySelectorAll('.mini-page tbody tr[data-name]')).filter((r) => r.classList.contains('active')).length,
      toasts: Array.from(document.querySelectorAll('#toastHost .toast')).map((t) => t.textContent)
    }))()`);
    report.push('mode-mini-dblclick: ' + JSON.stringify({ found: miniDbl.found, name: miniDbl.name, activeRows: miniDblAfter.activeRows, toasts: miniDblAfter.toasts }));
    if (!miniDbl.found) report.push('!! mini 存档表没有可双击的数据行');
    if (!miniDblAfter.calls.some((c) => c.method === 'archive.switch')) report.push('!! mini 双击存档未调用 archive.switch');
    if (miniDblAfter.activeRows !== 1) report.push('!! mini 双击后选中行应恰好 1 行，实际 ' + miniDblAfter.activeRows);
    if (!miniDblAfter.toasts.some((t) => String(t).indexOf('已切换到') >= 0)) report.push('!! mini 双击切换后未 toast 结果');

    // mini 按钮 → 既有命令
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnMiniGm').click()");
    await cdp.evaluate("document.getElementById('btnMiniRefresh').click()");
    await waitForCall(cdp, 'gm.launch', 4000);
    await waitForCall(cdp, 'archive.list', 4000);
    await wait(160);
    const miniBtns = await cdp.evaluate("(window.__mock.calls || []).slice()");
    report.push('mode-mini-buttons: ' + JSON.stringify(miniBtns));
    if (!miniBtns.some((c) => c.method === 'gm.launch')) report.push('!! mini「GM工具」未调用 gm.launch');
    if (!miniBtns.some((c) => c.method === 'archive.list')) report.push('!! mini「刷新」未调用 archive.list');

    // mini 开始游戏 / 停止服务端 / 储存当前 / 撤销换挡 → 只调用既有命令
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnMiniPlay').click()");
    await waitForCall(cdp, 'server.start', 5000);
    await cdp.evaluate("window.__mock.setServerRunning(true)");
    await wait(260);
    await cdp.evaluate("document.getElementById('btnMiniStop').click()");
    await waitForCall(cdp, 'server.stop', 5000);
    await cdp.evaluate("window.__mock.setServerRunning(false)");
    await wait(300);

    await cdp.evaluate("document.getElementById('btnMiniUndo').click()");
    await waitForCall(cdp, 'archive.undo', 4000);
    await wait(240);

    // 「储存当前」前等上一步 busy 结束，避免随后的 withBusy 静默跳过
    {
      const t0 = Date.now();
      while (Date.now() - t0 < 3000) {
        const busy = await cdp.evaluate("!!document.querySelector('.mini-page.busy')").catch(() => true);
        if (!busy) break;
        await wait(80);
      }
    }
    await cdp.evaluate("document.getElementById('btnMiniStore').click()");
    let miniPrompt = false;
    {
      const t0 = Date.now();
      while (Date.now() - t0 < 3000) {
        miniPrompt = await cdp.evaluate("!!document.querySelector('.ov-mask .ov-prompt')").catch(() => false);
        if (miniPrompt) break;
        await wait(80);
      }
    }
    if (miniPrompt) {
      await cdp.evaluate(`(() => {
        const b = Array.from(document.querySelectorAll('.ov-prompt .ov-foot .btn')).find((x) => x.textContent.trim() === '储存');
        if (b) b.click();
      })()`);
      await waitForCall(cdp, 'archive.store', 4000);
      await wait(220);
    }
    const miniOps = await cdp.evaluate("(window.__mock.calls || []).map((c) => c.method)");
    report.push('mode-mini-ops: ' + JSON.stringify({ prompt: miniPrompt, calls: miniOps }));
    if (!miniOps.includes('server.start')) report.push('!! mini「开始游戏」未调用 server.start');
    if (!miniOps.includes('server.stop')) report.push('!! mini「停止服务端」未调用 server.stop');
    if (!miniOps.includes('archive.undo')) report.push('!! mini「撤销换挡」未调用 archive.undo');
    if (!miniPrompt) report.push('!! mini「储存当前」未弹主题化输入框');
    else if (!miniOps.includes('archive.store')) report.push('!! mini「储存当前」未调用 archive.store');
    await cdp.evaluate("window.__mock.setServerRunning(false)");
    await wait(160);

    // mini 文字裁切
    const miniClip = await cdp.evaluate(`(() => {
      const bad = [];
      for (const el of document.querySelectorAll('.app *')) {
        if (el.children.length) continue;
        const t = (el.textContent || '').trim();
        if (!t) continue;
        const cs = getComputedStyle(el);
        if (cs.display === 'none' || cs.visibility === 'hidden') continue;
        if (cs.whiteSpace !== 'nowrap') continue;
        if (cs.textOverflow === 'ellipsis') continue;
        if (el.scrollWidth > el.clientWidth + 1) bad.push((el.className || el.tagName) + ' :: ' + t.slice(0, 24));
      }
      return bad;
    })()`);
    report.push('mode-mini-clip: ' + JSON.stringify(miniClip));
    if (miniClip.length) report.push('!! mini 文字裁切: ' + JSON.stringify(miniClip));

    // ---- mini 浅色（清掉交互用例留下的 toast，避免遮挡截图）
    await cdp.evaluate("document.querySelectorAll('#toastHost .toast').forEach((t) => t.remove())");
    await setTheme(cdp, 'light');
    {
      const t0 = Date.now();
      while (Date.now() - t0 < 3000) {
        const bg = await cdp.evaluate("(document.querySelector('.mini-page .arc-tbl-wrap') ? getComputedStyle(document.querySelector('.mini-page .arc-tbl-wrap')).backgroundColor : '')").catch(() => '');
        if (bg === 'rgb(255, 255, 255)') break;
        await wait(80);
      }
    }
    await wait(160);
    await shot(cdp, 'mini-light@620x470');
    await auditPage(cdp, report, '620x470 light mini');

    // ---- 返回 full：侧栏 / 日志恢复、按钮取消 active、回到原页面
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await setTheme(cdp, 'dark');
    await wait(200);
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnMiniBack').click()");
    await waitForCall(cdp, 'window.mode', 4000);
    await wait(380);
    const backFacts = await cdp.evaluate(`(() => {
      const vis = (el) => { if (!el) return false; const c = getComputedStyle(el); const r = el.getBoundingClientRect(); return c.display !== 'none' && c.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
      return {
        dataMode: document.querySelector('.app').dataset.mode,
        sidebarVisible: vis(document.getElementById('sidebar')),
        logVisible: vis(document.getElementById('logWrap')),
        resizerVisible: vis(document.getElementById('logResizer')),
        activeModes: Array.from(document.querySelectorAll('.tb-mode.active')).map((b) => b.id),
        pageTitle: (document.querySelector('#page .page-title') || {}).textContent || '',
        calls: (window.__mock.calls || []).filter((c) => c.method === 'window.mode').map((c) => c.args && c.args.mode)
      };
    })()`);
    report.push('mode-back-full: ' + JSON.stringify(backFacts));
    if (backFacts.dataMode !== 'full') report.push('!! 返回后 data-mode 应为 full，实际 ' + backFacts.dataMode);
    if (!backFacts.sidebarVisible) report.push('!! 返回 full 后侧栏未恢复');
    if (!backFacts.logVisible) report.push('!! 返回 full 后日志面板未恢复');
    if (!backFacts.resizerVisible) report.push('!! 返回 full 后日志 resizer 未恢复');
    if (backFacts.activeModes.length) report.push('!! 返回 full 后模式按钮仍 active: ' + JSON.stringify(backFacts.activeModes));
    if (backFacts.calls.indexOf('full') < 0) report.push('!! 点「返回完整模式」未调用 window.mode{full}');
    if (String(backFacts.pageTitle).indexOf('开始') < 0) report.push('!! 返回 full 后未回到原页面: ' + backFacts.pageTitle);

    // ---- 进入 classic（点 #btnClassic）→ 1280×840
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnClassic').click()");
    await waitForCall(cdp, 'window.mode', 4000);
    await wait(460);
    await shot(cdp, 'classic-dark@1280x840');

    const classicFacts = await cdp.evaluate(`(() => {
      const vis = (el) => { if (!el) return false; const c = getComputedStyle(el); const r = el.getBoundingClientRect(); return c.display !== 'none' && c.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
      const page = document.getElementById('page');
      const pr = page.getBoundingClientRect();
      const cols = Array.from(document.querySelectorAll('.cl-cols > .cl-col'));
      const tbl = document.querySelector('.cl-archive .arc-tbl-wrap');
      const tr = tbl ? tbl.getBoundingClientRect() : null;
      const statRow = document.querySelector('.cl-stats');
      const sr = statRow ? statRow.getBoundingClientRect() : null;
      const lw = document.getElementById('logWrap');
      const lr = lw ? lw.getBoundingClientRect() : null;
      return {
        dataMode: document.querySelector('.app').dataset.mode,
        classicActive: document.getElementById('btnClassic').classList.contains('active'),
        miniActive: document.getElementById('btnMini').classList.contains('active'),
        sidebarVisible: vis(document.getElementById('sidebar')),
        logVisible: vis(lw),
        cols: cols.length,
        cards: document.querySelectorAll('.cl-col .card').length,
        statCards: document.querySelectorAll('.cl-stats .stat-card').length,
        statCaptions: Array.from(document.querySelectorAll('.cl-stats .s-caption')).map((x) => x.textContent),
        backBtn: !!document.getElementById('btnClassicBack'),
        pageTitle: (document.querySelector('#page .page-title') || {}).textContent,
        tableInside: tr ? (tr.top >= pr.top - 1 && tr.bottom <= pr.bottom + 1) : null,
        statInside: sr ? (sr.top >= pr.top - 1 && sr.bottom <= pr.bottom + 1) : null,
        logInside: lr ? (lr.top >= 0 && lr.bottom <= innerHeight + 1) : false,
        pageSW: page.scrollWidth, pageCW: page.clientWidth,
        docSW: document.documentElement.scrollWidth, docCW: document.documentElement.clientWidth,
        rows: document.querySelectorAll('.cl-archive tbody tr[data-name]').length,
        calls: (window.__mock.calls || []).filter((c) => c.method === 'window.mode').map((c) => c.args && c.args.mode)
      };
    })()`);
    report.push('mode-classic: ' + JSON.stringify(classicFacts));
    if (classicFacts.dataMode !== 'classic') report.push('!! .app[data-mode] 应为 classic，实际 ' + classicFacts.dataMode);
    if (!classicFacts.classicActive) report.push('!! 进入 classic 后 #btnClassic 未加 active');
    if (classicFacts.miniActive) report.push('!! 进入 classic 后 #btnMini 不应 active');
    if (classicFacts.sidebarVisible) report.push('!! classic 下侧栏仍可见');
    if (!classicFacts.logVisible) report.push('!! classic 下日志面板不可见');
    if (classicFacts.cols !== 2) report.push('!! classic 应为两栏，实际 ' + classicFacts.cols);
    if (classicFacts.statCards !== 4) report.push('!! classic 顶部状态卡应为 4 项，实际 ' + classicFacts.statCards);
    if (!classicFacts.backBtn) report.push('!! classic 缺少「返回完整模式」');
    if (classicFacts.tableInside !== true) report.push('!! classic 存档表不在视口内');
    if (classicFacts.statInside !== true) report.push('!! classic 状态卡不在视口内');
    if (!classicFacts.logInside) report.push('!! classic 日志面板超出视口');
    if (classicFacts.pageSW > classicFacts.pageCW + 1) report.push('!! classic 页面横向溢出 ' + classicFacts.pageSW + ' > ' + classicFacts.pageCW);
    if (classicFacts.docSW > classicFacts.docCW + 1) report.push('!! classic 文档横向溢出');
    if (classicFacts.rows < 5) report.push('!! classic 存档表行数不足: ' + classicFacts.rows);
    if (classicFacts.calls.indexOf('classic') < 0) report.push('!! 点【典】未调用 window.mode{classic}');
    await auditPage(cdp, report, '1280x840 dark classic');

    // classic-left-scroll：1280 下两栏都不得出现内部滚动（scrollHeight <= clientHeight + 2）
    const clLeftScroll = await cdp.evaluate(`(() => {
      const cols = Array.from(document.querySelectorAll('.cl-cols > .cl-col'));
      return cols.map((c) => ({
        cls: c.className,
        sh: c.scrollHeight, ch: c.clientHeight, sw: c.scrollWidth, cw: c.clientWidth,
        overflowY: getComputedStyle(c).overflowY,
        delta: c.scrollHeight - c.clientHeight
      }));
    })()`);
    report.push('classic-left-scroll: ' + JSON.stringify(clLeftScroll));
    clLeftScroll.forEach((c, i) => {
      if (c.delta > 2) report.push('!! 1280 下 .cl-col[' + i + '] 内部滚动 ' + c.sh + ' > ' + c.ch + '（' + c.cls + '）');
    });

    // classic-tools-visible：更新卡与 4 个按钮都在视口内，且 bottom 在日志面板上沿之上
    const clTools = await cdp.evaluate(`(() => {
      const logTop = document.querySelector('.log-panel').getBoundingClientRect().top;
      const card = document.querySelector('.cl-tools');
      const ids = ['btnClUpdate', 'btnClUpdateLog', 'btnClAum', 'btnClSdk'];
      const cr = card ? card.getBoundingClientRect() : null;
      const btns = ids.map((id) => {
        const b = document.getElementById(id);
        if (!b) return { id, found: false };
        const r = b.getBoundingClientRect();
        const cs = getComputedStyle(b);
        return {
          id, found: true,
          visible: cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0,
          top: Math.round(r.top), bottom: Math.round(r.bottom), h: Math.round(r.height)
        };
      });
      const left = Array.from(document.querySelectorAll('.cl-cols > .cl-col'))[0];
      const lr = left ? left.getBoundingClientRect() : null;
      return {
        logTop: Math.round(logTop),
        cardFound: !!card,
        cardTop: cr ? Math.round(cr.top) : null,
        cardBottom: cr ? Math.round(cr.bottom) : null,
        cardInViewport: cr ? (cr.top >= 0 && cr.bottom <= logTop + 1) : false,
        leftScrollTop: left ? left.scrollTop : null,
        leftBottom: lr ? Math.round(lr.bottom) : null,
        btns,
        allAboveLog: btns.every((b) => b.found && b.visible && b.bottom <= logTop + 1)
      };
    })()`);
    report.push('classic-tools-visible: ' + JSON.stringify(clTools));
    if (!clTools.cardFound) report.push('!! classic 缺少「更新与工具」卡 .cl-tools');
    else if (!clTools.cardInViewport) report.push('!! 1280 下「更新与工具」卡不在首屏（bottom ' + clTools.cardBottom + ' vs 日志上沿 ' + clTools.logTop + '）');
    for (const b of clTools.btns) {
      if (!b.found) report.push('!! 1280 下缺少按钮 ' + b.id);
      else if (!b.visible) report.push('!! 1280 下按钮不可见 ' + b.id);
      else if (b.bottom > clTools.logTop + 1) report.push('!! 1280 下按钮被日志遮挡 ' + b.id + ' bottom=' + b.bottom + ' logTop=' + clTools.logTop);
    }
    if (clTools.leftScrollTop !== null && clTools.leftScrollTop > 1) {
      report.push('!! 1280 下左栏初始 scrollTop 应为 0，实际 ' + clTools.leftScrollTop);
    }

    // classic-arcinfo-ellipsis：当前/备份数 不得出现省略号（scrollWidth <= clientWidth）
    const clArcinfo = await cdp.evaluate(`(() => {
      const vs = Array.from(document.querySelectorAll('.cl-archive .arc-info .v'));
      return vs.map((v) => ({
        bind: v.dataset.bind || '',
        text: v.textContent,
        sw: v.scrollWidth, cw: v.clientWidth,
        clipped: v.scrollWidth > v.clientWidth + 1,
        tip: v.getAttribute('data-tip') || null
      }));
    })()`);
    report.push('classic-arcinfo-ellipsis: ' + JSON.stringify({ count: clArcinfo.filter((x) => x.clipped).length, items: clArcinfo }));
    if (clArcinfo.length < 2) report.push('!! 存档信息行应有当前/备份数两项');
    for (const v of clArcinfo) {
      if (v.clipped) report.push('!! 存档信息行被截断 ' + v.bind + ': ' + v.text + ' (' + v.sw + '>' + v.cw + ')');
    }

    // classic 交互：存档双击 / 右键菜单 / 打开目录 / 开始更新
    await cdp.evaluate("window.__mock.clearCalls()");
    const clDbl = await cdp.evaluate(`(() => {
      const row = Array.from(document.querySelectorAll('.cl-archive tbody tr[data-name]')).find((r) => !r.classList.contains('current'));
      if (!row) return { found: false };
      row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      return { found: true, name: row.dataset.name };
    })()`);
    await waitForCall(cdp, 'archive.switch', 4000);
    await wait(260);
    const clDblCalls = await cdp.evaluate("(window.__mock.calls || []).slice()");
    report.push('mode-classic-dblclick: ' + JSON.stringify({ found: clDbl.found, name: clDbl.name, calls: clDblCalls.map((c) => c.method) }));
    if (!clDbl.found) report.push('!! classic 存档表没有可双击的数据行');
    if (!clDblCalls.some((c) => c.method === 'archive.switch')) report.push('!! classic 双击存档未调用 archive.switch');

    const clMenu = await cdp.evaluate(`(() => {
      const row = document.querySelector('.cl-archive tbody tr[data-name]');
      const r = row.getBoundingClientRect();
      row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: Math.round(r.left + 80), clientY: Math.round(r.top + 14), button: 2 }));
      const m = document.querySelector('.ctx-menu');
      return { open: !!m, items: m ? Array.from(m.querySelectorAll('.menu-item')).map((x) => x.textContent.trim()) : [] };
    })()`);
    report.push('mode-classic-menu: ' + JSON.stringify(clMenu));
    if (!clMenu.open) report.push('!! classic 右键未弹出主题化菜单');
    else if (JSON.stringify(clMenu.items) !== JSON.stringify(['切换存档', '重命名存档'])) report.push('!! classic 右键菜单项不符: ' + JSON.stringify(clMenu.items));
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await wait(180);

    // 等存档操作忙碌态结束（dblclick 触发的 switch 仍在收尾）
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnClPvfDir').click()");
    await waitForCall(cdp, 'shell.open', 4000);
    await cdp.evaluate("document.getElementById('btnClUpdate').click()");
    await waitForCall(cdp, 'update.start', 4000);
    await wait(220);
    const clCalls = await cdp.evaluate("(window.__mock.calls || []).slice()");
    report.push('mode-classic-calls: ' + JSON.stringify(clCalls.map((c) => c.method + (c.kind ? ':' + c.kind : ''))));
    if (!clCalls.some((c) => c.method === 'shell.open' && c.kind === 'pvfDir')) report.push('!! classic「打开PVF目录」未调用 shell.open{pvfDir}');
    if (!clCalls.some((c) => c.method === 'update.start')) report.push('!! classic「开始更新」未调用 update.start');
    await wait(1600);

    // classic 浅色（等主题过渡完全结束再截图）
    await cdp.evaluate("document.querySelectorAll('#toastHost .toast').forEach((t) => t.remove())");
    await setTheme(cdp, 'light');
    {
      const t0 = Date.now();
      while (Date.now() - t0 < 3000) {
        const bg = await cdp.evaluate("(document.querySelector('.cl-card') ? getComputedStyle(document.querySelector('.cl-card')).backgroundColor : '')").catch(() => '');
        if (bg === 'rgb(255, 255, 255)') break;
        await wait(80);
      }
    }
    await wait(160);
    await shot(cdp, 'classic-light@1280x840');
    await auditPage(cdp, report, '1280x840 light classic');

    // ---- classic 1080×700：页面纵向滚动，日志固定底部，不横向溢出（深色）
    await setTheme(cdp, 'dark');
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1080, height: 700, deviceScaleFactor: 1, mobile: false });
    await wait(360);
    await shot(cdp, 'classic-dark@1080x700');
    const cl1080 = await cdp.evaluate(`(() => {
      const page = document.querySelector('.classic-page');
      const doc = document.documentElement;
      const lw = document.getElementById('logWrap');
      const lp = document.querySelector('.log-panel');
      const lr = lp.getBoundingClientRect();
      const pr = page.getBoundingClientRect();
      const cols = Array.from(document.querySelectorAll('.cl-cols > .cl-col'));
      return {
        pageSW: page.scrollWidth, pageCW: page.clientWidth,
        docSW: doc.scrollWidth, docCW: doc.clientWidth,
        logBottomFixed: Math.abs(lr.bottom - (innerHeight - 12)) < 3,
        logVisible: lr.height > 30,
        pageScrollable: page.scrollHeight > page.clientHeight + 1,
        pageOverflowY: getComputedStyle(page).overflowY,
        pageInsideLog: pr.bottom <= lr.top + 1,
        colWidths: cols.map((c) => Math.round(c.getBoundingClientRect().width)),
        colOverflow: cols.map((c) => c.scrollWidth > c.clientWidth + 1)
      };
    })()`);
    const clArcinfo1080 = await cdp.evaluate(`(() => {
      const vs = Array.from(document.querySelectorAll('.cl-archive .arc-info .v'));
      return vs.map((v) => ({ bind: v.dataset.bind || '', text: v.textContent, sw: v.scrollWidth, cw: v.clientWidth, clipped: v.scrollWidth > v.clientWidth + 1 }));
    })()`);
    report.push('classic-arcinfo-1080: ' + JSON.stringify({ count: clArcinfo1080.filter((x) => x.clipped).length, items: clArcinfo1080 }));
    for (const v of clArcinfo1080) {
      if (v.clipped) report.push('!! 1080×700 存档信息行被截断 ' + v.bind + ': ' + v.text);
    }
    report.push('mode-classic-1080: ' + JSON.stringify(cl1080));
    if (cl1080.pageSW > cl1080.pageCW + 1) report.push('!! 1080×700 classic 页面横向溢出');
    if (cl1080.docSW > cl1080.docCW + 1) report.push('!! 1080×700 classic 文档横向溢出');
    if (!cl1080.logVisible) report.push('!! 1080×700 classic 日志不可见');
    if (!cl1080.logBottomFixed) report.push('!! 1080×700 classic 日志未固定底部');
    if (!cl1080.pageInsideLog) report.push('!! 1080×700 classic 页面侵入日志区');
    if (!cl1080.pageScrollable) report.push('!! 1080×700 classic 内容超出但页面不可滚（内容会被裁切）');
    await auditPage(cdp, report, '1080x700 dark classic');

    // 卡片不得横向撑破（只查容器，滚动容器本身允许 scrollWidth 大）
    const cl1080wide = await cdp.evaluate(`(() => {
      const bad = [];
      for (const el of document.querySelectorAll('.classic-page .card, .classic-page .cl-col, .classic-page .cl-archive')) {
        const cs = getComputedStyle(el);
        if (cs.overflowX === 'auto' || cs.overflowX === 'scroll') continue;
        if (el.scrollWidth > el.clientWidth + 1) bad.push((el.className || el.tagName) + ' ' + el.scrollWidth + '>' + el.clientWidth);
      }
      return bad;
    })()`);
    report.push('mode-classic-1080-clip: ' + JSON.stringify(cl1080wide));
    if (cl1080wide.length) report.push('!! 1080×700 classic 卡片横向溢出: ' + JSON.stringify(cl1080wide));

    // ---- 再次点当前模式按钮 = 返回 full
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 840, deviceScaleFactor: 1, mobile: false });
    await setTheme(cdp, 'dark');
    await wait(240);
    await cdp.evaluate("window.__mock.clearCalls()");
    await cdp.evaluate("document.getElementById('btnClassic').click()");
    await waitForCall(cdp, 'window.mode', 4000);
    await wait(360);
    const toggleBack = await cdp.evaluate(`(() => ({
      dataMode: document.querySelector('.app').dataset.mode,
      activeModes: Array.from(document.querySelectorAll('.tb-mode.active')).map((b) => b.id),
      sidebar: getComputedStyle(document.getElementById('sidebar')).display !== 'none',
      log: getComputedStyle(document.getElementById('logWrap')).display !== 'none',
      calls: (window.__mock.calls || []).filter((c) => c.method === 'window.mode').map((c) => c.args && c.args.mode)
    }))()`);
    report.push('mode-toggle-back: ' + JSON.stringify(toggleBack));
    if (toggleBack.dataMode !== 'full') report.push('!! 再点【典】未返回 full');
    if (toggleBack.calls.indexOf('full') < 0) report.push('!! 再点当前模式按钮未调用 window.mode{full}');
    if (toggleBack.activeModes.length) report.push('!! 再点后模式按钮仍 active');
    if (!toggleBack.sidebar || !toggleBack.log) report.push('!! 再点后侧栏/日志未恢复');

    // ---- 非法模式：宿主回落 full 并返回 ok:false（mock 与真实 Commands 语义一致）
    const badRes = await cdp.evaluate(`(() => new Promise((res) => {
      const id = 'bad-' + Date.now();
      const lis = (e) => {
        let d = e.data; if (typeof d === 'string') d = JSON.parse(d);
        if (d && d.id === id) res(d);
      };
      window.chrome.webview.addEventListener('message', lis);
      window.chrome.webview.postMessage(JSON.stringify({ id, method: 'window.mode', args: { mode: 'nope' } }));
      setTimeout(() => res({ timeout: true }), 3000);
    }))()`);
    report.push('mode-invalid: ' + JSON.stringify(badRes));
    if (!badRes || !badRes.result) report.push('!! 非法窗口模式宿主未返回结果');
    else if (badRes.result.ok !== false) report.push('!! 非法窗口模式宿主未返回 ok:false');
    else if (String(badRes.result.mode) !== 'full') report.push('!! 非法窗口模式未回落 full');
    await wait(200);
    await cdp.evaluate("window.__mock.setMode('full')");
    await wait(200);

    // 恢复默认视图
    await gotoPage(cdp, 'dash', '开始');
    await wait(160);

    /* ---------------------------------------------------------------- 汇总 */
    const allErrors = await cdp.evaluate("window.__mockErrors || []");
    if (allErrors.length) report.push(...allErrors.map((x) => '!! 累积 console.error: ' + x));
  } finally {
    try { cdp.close(); } catch { }
    try { browser.proc.kill(); } catch { }
    try { server.close(); } catch { }
    await wait(300);
    try { fs.rmSync(browser.profile, { recursive: true, force: true }); } catch { }
  }

  const text = report.join('\n') + '\n';
  fs.writeFileSync(path.join(OUT, 'layout.txt'), text, 'utf8');
  console.log(text);
}

main().catch((err) => { console.error(err); process.exit(1); });
