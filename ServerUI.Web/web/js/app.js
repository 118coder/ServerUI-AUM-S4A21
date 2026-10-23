/* ==========================================================================
   app.js — 壳逻辑装配（导航 / 主题 / 标题栏 / 日志 / 推送 / 启动流程）
   Phase 1：只有「开始」页是完整实现，其余导航项为主题化占位。
   ========================================================================== */

import { call, on, isHosted } from './bridge.js';
import { h, icon, clear } from './ui/dom.js';
import { initTooltip } from './ui/tooltip.js';
import { toast } from './ui/toast.js';
import { createLogPanel } from './ui/logpanel.js';
import { handleAsk } from './ui/dialog.js';
import { openModal } from './ui/modal.js';
import { initRouter, register, go, currentPage } from './router.js';
import { renderDash, applyStatus } from './pages/dash.js';
import { renderArchive, archiveDrop, refreshArchiveIfActive } from './pages/archive.js';
import { renderDll, refreshDllIfActive } from './pages/dll.js';
import { renderSettings } from './pages/settings.js';
import { renderTime } from './pages/time.js';
import { renderHelp } from './pages/help.js';
import { renderTsh } from './pages/tsh.js';
import { renderPlaceholder } from './pages/placeholder.js';
import { renderMini, applyStatus as applyMiniStatus } from './pages/mini.js';
import { renderClassic, applyStatus as applyClassicStatus } from './pages/classic.js';
import { initMode, initModeGetter } from './ui/mode.js';

// ------------------------------------------------------------------ 导航定义
const NAV = [
  { id: 'dash', text: '开始', icon: 'gauge' },
  { id: 'archive', text: '存档管理', icon: 'archive' },
  { id: 'dll', text: 'DLL扩展', icon: 'dll' },
  { id: 'time', text: '调整系统时间', icon: 'clock', phase: 'Phase 3', note: '时间页（自绘日期时间选择器）尚未实现' },
  { id: 'settings', text: '设置与关于', icon: 'setting', phase: 'Phase 3', note: '设置与关于页尚未实现' },
  { id: 'help', text: '使用说明', icon: 'help', phase: 'Phase 3', note: '使用说明页尚未实现' },
  { id: 'tsh', text: '疑难杂症解惑', icon: 'bulb', phase: 'Phase 3', note: '疑难杂症解惑页尚未实现' },
];

const SUB = {
  dash: '服务端与游戏的一键控制台 · 常用操作都集中在这里',
  archive: '切换、备份与导入导出存档 · 双击列表换挡，拖动 .db 到窗口也能换',
  dll: '客户端补丁挂载 · 勾选即写入 GameGaurd.ini 的 [Plugins] 列表（顺序生效）',
  time: '本机时间校准与 NTP 同步',
  settings: '外观、路径与版本信息',
  help: '使用流程与常见问题',
  tsh: '启动失败等疑难问题的排查路径',
};

// ------------------------------------------------------------------ 全局状态
const state = {
  version: '2.21',
  theme: 'dark',
  page: 'dash',
  mode: 'full',
  settings: {},
  status: {},
  statusJson: '',
  isAdmin: false,
  paths: {},
  navigated: false,
  fullPage: 'dash',
};

/** 模式名规范化：只接受 full / mini / classic，其余一律回落 full。 */
function normalizeMode(m) {
  const v = String(m || '').trim().toLowerCase();
  return (v === 'mini' || v === 'classic') ? v : 'full';
}

// ------------------------------------------------------------------ 主题
function applyTheme(theme) {
  const t = (theme === 'light') ? 'light' : 'dark';
  state.theme = t;
  document.documentElement.dataset.theme = t;
  const btn = document.getElementById('tbTheme');
  if (btn) {
    const use = btn.querySelector('use');
    if (use) use.setAttribute('href', 'assets/icons.svg#i-' + (t === 'dark' ? 'moon' : 'sun'));
    btn.dataset.tip = t === 'dark' ? '切换到浅色主题' : '切换到深色主题';
  }
}

function toggleTheme() {
  applyTheme(state.theme === 'dark' ? 'light' : 'dark');
  call('settings.set', { theme: state.theme }).catch(() => { });
}

// ------------------------------------------------------------------ 状态药丸（标题栏）
function applyPill(st) {
  const pill = document.getElementById('statusPill');
  const text = document.getElementById('statusPillText');
  if (!pill || !text) return;

  const s = st || {};
  const bat = !!s.bat;
  const dfo = !!s.dfo;
  const external = !bat && dfo;

  let label = '未运行';
  let tone = 'bad';
  if (bat && dfo) { label = '运行中'; tone = 'ok'; }
  else if (external) { label = '运行中(外部)'; tone = 'warn'; }

  // 宿主已给出 serverState / serverLevel 时以宿主为准（同一套 Rs() 逻辑）
  if (typeof s.serverState === 'string' && s.serverState) {
    label = s.serverState.replace(/^[●\s]+/, '').replace(/\s*\(\s*外部\s*\)\s*/, '(外部)').trim();
    tone = ({ success: 'ok', warning: 'warn', error: 'bad', secondary: 'off' })[s.serverLevel] || tone;
  }

  text.textContent = label;
  pill.className = 'status-pill ' + tone;
  pill.dataset.tip = '服务端状态：' + label;
}

/** 状态快照落地：标题栏药丸 + 当前页原地更新（内容未变则跳过 DOM 写入）。 */
function applyStatusSnapshot(st) {
  const snapshot = st || {};
  const json = JSON.stringify(snapshot);
  const changed = json !== state.statusJson;
  state.status = snapshot;
  state.statusJson = json;
  if (typeof snapshot.admin === 'boolean') state.isAdmin = snapshot.admin;

  applyPill(snapshot);
  if (changed) applyModeStatus(snapshot);
}

/** 当前视图的状态行/状态卡原地更新（不重建 DOM）。 */
function applyModeStatus(snapshot) {
  const root = document.getElementById('page');
  if (!root) return;
  if (state.page === 'dash') {
    applyStatus(root, snapshot, ctx);
  } else if (state.page === 'mini') {
    applyMiniStatus(root, snapshot, ctx);
  } else if (state.page === 'classic') {
    applyClassicStatus(root, snapshot, ctx);
  }
}

// ------------------------------------------------------------------ 窗口模式
/**
 * 应用模式：写 .app/body 的 data-mode、同步 典/简 active、切到对应视图。
 * 宿主 push（window.mode）与命令返回都走这里（幂等）。
 */
function applyMode(mode, maximized) {
  const next = normalizeMode(mode);
  const prev = state.mode;
  state.mode = next;

  const app = document.querySelector('.app');
  if (app) app.dataset.mode = next;
  document.body.dataset.mode = next;

  const bClassic = document.getElementById('btnClassic');
  const bMini = document.getElementById('btnMini');
  if (bClassic) {
    bClassic.classList.toggle('active', next === 'classic');
    bClassic.setAttribute('aria-pressed', next === 'classic' ? 'true' : 'false');
    bClassic.dataset.tip = next === 'classic' ? '经典界面（当前）· 再点返回完整模式' : '经典界面';
  }
  if (bMini) {
    bMini.classList.toggle('active', next === 'mini');
    bMini.setAttribute('aria-pressed', next === 'mini' ? 'true' : 'false');
    bMini.dataset.tip = next === 'mini' ? '简洁界面（当前）· 再点返回完整模式' : '简洁界面';
  }
  if (typeof maximized === 'boolean') setMaxIcon(maximized);

  // 视图：mini/classic 是独立页；回 full 时恢复进入前的页面（默认开始页）
  if (next === 'mini' || next === 'classic') {
    if (prev === 'full') state.fullPage = state.page;
    if (state.page !== next) navigate(next);
  } else if (prev !== 'full') {
    const back = (state.fullPage && state.fullPage !== 'mini' && state.fullPage !== 'classic') ? state.fullPage : 'dash';
    if (state.page === 'mini' || state.page === 'classic') navigate(back);
  }
}

/** 请求切换模式：命令返回与 push 双通道，两边都应用（幂等）。 */
function requestMode(mode) {
  const target = normalizeMode(mode);
  // 再点当前模式按钮 = 返回完整模式
  const next = (target === state.mode && target !== 'full') ? 'full' : target;

  // 立即应用，避免等待宿主往返造成视觉延迟（宿主随后会 push 权威值）
  applyMode(next);

  call('window.mode', { mode: next }).then((res) => {
    if (res && res.mode) applyMode(res.mode, res.maximized);
    if (res && res.ok === false) {
      // 宿主拒绝（非法值/未就绪）：回落到宿主回报的模式，并给出可读提示
      applyMode(res.mode || 'full', res.maximized);
      const msg = res.error || '窗口模式切换失败';
      if (logPanel) logPanel.append('warning', '>>> [界面] ' + msg);
      toast('warning', msg);
    }
  }).catch((err) => {
    const msg = String((err && err.message) || err);
    if (logPanel) logPanel.append('error', '>>> [界面] 切换窗口模式失败: ' + msg);
    toast('error', '切换窗口模式失败：' + msg);
  });
}

// ------------------------------------------------------------------ 导航
function buildNav(navEl) {
  clear(navEl);
  for (const item of NAV) {
    const el = h('div.nav-item', {
      role: 'button',
      tabindex: '0',
      dataset: { nav: item.id },
      onclick: () => navigate(item.id),
      onkeydown: (ev) => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); navigate(item.id); } },
    }, [icon(item.icon), h('span', { text: item.text })]);
    navEl.appendChild(el);
  }
}

function markNav(id) {
  const navEl = document.getElementById('sidebar');
  for (const el of Array.from(navEl.children)) {
    el.classList.toggle('active', el.dataset.nav === id);
  }
}

// ------------------------------------------------------------------ 渲染上下文
let logPanel = null;

const ctx = {
  get status() { return state.status; },
  get settings() { return state.settings; },
  get version() { return state.version; },
  get paths() { return state.paths; },
  get isAdmin() { return state.isAdmin; },
  get log() { return logPanel; },
  refreshStatus: () => { call('server.status', {}).then(s => { applyStatusSnapshot(s); }).catch(() => { }); },
};

function navigate(id) {
  if (!go(id, ctx)) return;
  state.page = id;
  state.navigated = true;
  markNav(id);
}

// ------------------------------------------------------------------ 标题栏
function wireTitlebar() {
  const drag = document.getElementById('tbDrag');
  drag.addEventListener('mousedown', (ev) => {
    if (ev.button !== 0) return;
    call('window.drag', {}).catch(() => { });
  });
  drag.addEventListener('dblclick', () => { call('window.toggleMaximize', {}).catch(() => { }); });

  document.getElementById('tbMin').addEventListener('click', () => { call('window.minimize', {}).catch(() => { }); });
  document.getElementById('tbMax').addEventListener('click', () => { call('window.toggleMaximize', {}).catch(() => { }); });
  document.getElementById('tbClose').addEventListener('click', () => { requestClose(); });

  // 主题切换：标题栏月亮/太阳按钮 + 版本号文字（两个入口都要可用）
  const tbTheme = document.getElementById('tbTheme');
  if (tbTheme) tbTheme.addEventListener('click', toggleTheme);
  const tbVer = document.getElementById('tbVer');
  if (tbVer) tbVer.addEventListener('click', toggleTheme);

  wireModeButtons();
}

/** 典 / 简：经典 / 极简界面入口（Phase 3b：真实切换窗口模式）。 */
function wireModeButtons() {
  const bClassic = document.getElementById('btnClassic');
  const bMini = document.getElementById('btnMini');
  if (bClassic) bClassic.addEventListener('click', () => requestMode('classic'));
  if (bMini) bMini.addEventListener('click', () => requestMode('mini'));
}

function setMaxIcon(maximized) {
  const btn = document.getElementById('tbMax');
  if (!btn) return;
  const use = btn.querySelector('use');
  if (!use) return;
  use.setAttribute('href', 'assets/icons.svg#i-' + (maximized ? 'restore' : 'max'));
  btn.dataset.tip = maximized ? '还原' : '最大化';
}

// ------------------------------------------------------------------ 关闭拦截
async function requestClose() {
  // 宿主会推送 ask-close；这里直接调用一次，保证无推送丢失时也能关闭
  call('window.close', {}).catch(() => { });
}

async function confirmQuit() {
  const res = await openModal({
    title: '退出 ServerS4A21 管理器',
    text: '确定要退出管理器吗？\n退出时会自动执行清理（结束更新、停止服务端、清理缓存）。',
    buttons: [
      { text: '退出', value: 'yes', primary: true, kind: 'danger' },
      { text: '取消', value: 'cancel' },
    ],
  });
  if (res.value === 'yes') {
    call('app.quit', {}).catch(() => { });
  }
}

// ------------------------------------------------------------------ 输入守卫
// 宿主已设置 AreBrowserAcceleratorKeysEnabled=false / AreDevToolsEnabled=false /
// AreDefaultContextMenusEnabled=false；此处为前端兜底（见 §10 偏差说明）。
const BLOCKED_KEYS = new Set(['F12', 'F5', 'F7']);

function wireInputGuards() {
  document.addEventListener('contextmenu', (ev) => { ev.preventDefault(); });
  document.addEventListener('dragstart', (ev) => {
    const t = ev.target;
    if (t && (t.tagName === 'IMG' || t.tagName === 'A' || t.tagName === 'SVG')) ev.preventDefault();
  });

  document.addEventListener('keydown', (ev) => {
    const k = ev.key;
    if (BLOCKED_KEYS.has(k)) { ev.preventDefault(); return; }
    if (ev.ctrlKey && !ev.altKey) {
      const lower = (k || '').toLowerCase();
      if (ev.shiftKey && (lower === 'i' || lower === 'j' || lower === 'c')) { ev.preventDefault(); return; }
      if (['r', 'n', 't', 'w', 'u', 'p', 's', 'o'].indexOf(lower) >= 0) { ev.preventDefault(); return; }
      if (k === '+' || k === '-' || k === '=' || k === '0') { ev.preventDefault(); return; }
    }
    if (ev.altKey && (k === 'ArrowLeft' || k === 'ArrowRight')) { ev.preventDefault(); return; }
    if (k === 'Backspace') {
      const t = ev.target;
      const editable = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable);
      if (!editable) ev.preventDefault();
    }
  });

  // Ctrl+滚轮缩放：前端再拦一层
  window.addEventListener('wheel', (ev) => {
    if (ev.ctrlKey) ev.preventDefault();
  }, { passive: false });
}

// ------------------------------------------------------------------ 推送处理
function wirePushes() {
  on('log', (p) => {
    if (!logPanel || !p) return;
    if (p.items && p.items.length) {
      // 宿主已合并的批量日志：一次性插入（DocumentFragment + 单次滚动）
      if (logPanel.appendMany) logPanel.appendMany(p.items);
      else for (const it of p.items) logPanel.append(it.level, it.text);
    } else {
      logPanel.append(p.level, p.text);
    }
  });

  on('progress', (p) => {
    if (!logPanel || !p) return;
    logPanel.setProgress(p.value, p.label, p.visible);
  });

  on('status', (p) => { applyStatusSnapshot(p); });

  on('toast', (p) => {
    if (!p) return;
    toast(p.level || 'info', p.text || '');
  });

  on('theme.changed', (p) => {
    if (p && p.theme) applyTheme(p.theme);
  });

  on('window.state', (p) => {
    if (p && typeof p.maximized === 'boolean') setMaxIcon(p.maximized);
  });

  // 窗口模式推送 { mode, maximized }：与命令返回同一条落地路径
  on('window.mode', (p) => {
    if (!p || !p.mode) return;
    applyMode(p.mode, p.maximized);
  });

  on('ask-close', () => { confirmQuit(); });
  on('ask', (p) => { handleAsk(p); });
  on('drop', (p) => {
    if (!p || !p.paths || !p.paths.length) return;
    handleDrop(p.paths);
  });

  // 数据变化推送：当前页就是该页时原地刷新（不再只是写日志）
  on('archive.changed', () => {
    const p = currentPage();
    if (p === 'archive' || p === 'mini' || p === 'classic') { refreshArchiveIfActive(); return; }
    logPanel && logPanel.append('secondary', '>>> [存档] 数据已变化');
  });
  on('dll.changed', () => {
    if (currentPage() === 'dll') { refreshDllIfActive(); return; }
    logPanel && logPanel.append('secondary', '>>> [DLL扩展] 配置已变化');
  });
}

/**
 * 文件拖入：与旧 UI 一致 —— 拖 .db 到窗口任意位置都换挡（不要求先切到存档页）。
 * 存档页挂载时交给它的 doDrop（带服务端拦截 + 刷新 + toast）；否则直接调宿主并写日志。
 */
function handleDrop(paths) {
  const list = (paths || []).filter(Boolean);
  if (!list.length) return;
  const first = list[0];
  if (list.length > 1) {
    logPanel && logPanel.append('secondary', '>>> 一次拖入多个文件，仅处理第一个：' + first);
  }

  if (currentPage() === 'archive' && archiveDrop(first)) return;
  if ((currentPage() === 'mini' || currentPage() === 'classic') && archiveDrop(first)) return;

  if (!/\.db$/i.test(first)) {
    logPanel && logPanel.append('warning', '>>> 只接受 .db 文件：' + first);
    toast('warning', '只接受 .db 文件');
    return;
  }

  logPanel && logPanel.append('secondary', '>>> 拖入换挡: ' + first);
  call('archive.dropFile', { path: first })
    .then((res) => {
      if (res && res.ok) {
        toast('success', '拖拽换挡完成: ' + first.split(/[\\/]/).pop());
        logPanel && logPanel.append('success', '>>> 拖拽换挡完成');
      } else if (res && res.blocked) {
        // 宿主已弹提示 + 写日志
      } else {
        const msg = (res && res.error) || '换挡失败';
        toast('error', msg);
        logPanel && logPanel.append('error', '>>> [换挡] ' + msg);
      }
    })
    .catch((err) => {
      const msg = String((err && err.message) || err);
      toast('error', msg);
      logPanel && logPanel.append('error', '>>> [换挡] ' + msg);
    });
}

// ------------------------------------------------------------------ 启动
async function boot() {
  initTooltip();
  wireInputGuards();
  wireTitlebar();
  buildNav(document.getElementById('sidebar'));
  initRouter(document.getElementById('page'));

  register('dash', { render: renderDash });
  register('archive', { render: renderArchive });
  register('dll', { render: renderDll });
  register('settings', { render: renderSettings });
  register('time', { render: renderTime });
  register('help', { render: renderHelp });
  register('tsh', { render: renderTsh });
  register('mini', { render: renderMini });
  register('classic', { render: renderClassic });
  for (const item of NAV) {
    if (item.id === 'dash' || item.id === 'archive' || item.id === 'dll') continue;
    if (item.id === 'settings' || item.id === 'time' || item.id === 'help' || item.id === 'tsh') continue;
    register(item.id, {
      render: renderPlaceholder({
        title: item.text,
        sub: SUB[item.id] || '',
        phase: item.phase || 'Phase 3',
        note: item.note || (item.text + '尚未实现'),
        icon: item.icon,
      }),
    });
  }

  // 日志面板
  logPanel = createLogPanel({
    wrap: document.getElementById('logWrap'),
    body: document.getElementById('logBody'),
    empty: document.getElementById('logEmpty'),
    resizer: document.getElementById('logResizer'),
    progress: document.getElementById('logProgress'),
    progressBar: document.getElementById('logProgressBar'),
    progressLabel: document.getElementById('logProgressLabel'),
    switches: document.getElementById('logSwitches'),
    copyBtn: document.getElementById('logCopy'),
    clearBtn: document.getElementById('logClear'),
    collapseBtn: document.getElementById('logCollapse'),
  }, state.settings);

  // 主题切换入口已在 wireTitlebar() 内绑定（#tbTheme 与 #tbVer）
  wirePushes();

  if (!isHosted()) {
    // 无宿主（例如直接双击 index.html）：给出明确提示，不静默失败
    document.getElementById('splashSub').textContent = '未检测到宿主（bridge 不可用）';
    logPanel.append('error', '未检测到宿主通道：请通过 ServerUI.exe 启动本界面。');
    applyTheme('dark');
    return;
  }

  try {
    const setSub = (t) => { const el = document.getElementById('splashSub'); if (el) el.textContent = t; };
    const bar = document.getElementById('splashBar');
    if (bar) requestAnimationFrame(() => { bar.style.transform = 'scaleX(.12)'; });
    setSub('正在连接宿主…');
    const init = await call('app.init', {});
    state.version = init.version || state.version;
    state.page = init.page || 'dash';
    state.isAdmin = !!init.isAdmin;
    state.paths = init.paths || {};
    Object.assign(state.settings, init.settings || {});
    if (typeof init.logHeight === 'number') state.settings.logHeight = init.logHeight;
    if (typeof init.logCollapsed === 'boolean') state.settings.logCollapsed = init.logCollapsed;

    applyTheme(init.theme || 'dark');
    logPanel.applyHeight(state.settings.logHeight || 190, true);
    logPanel.setCollapsed(!!state.settings.logCollapsed, true);

    // 补齐 logHeight/logCollapsed 之外的用户设置（开关状态）
    call('settings.get', {}).then(s => {
      if (s) {
        Object.assign(state.settings, s);
        if (logPanel) {
          if (typeof s.skipLog === 'boolean') logPanel.setSkipLog(s.skipLog);
          if (typeof s.mirror === 'boolean') logPanel.setMirror(s.mirror);
        }
      }
    }).catch(() => { });

    setSub('正在读取服务端状态…');
    const st = await call('server.status', {});
    applyStatusSnapshot(st);

    // 模式：init.mode（宿主当前模式）+ 客户端兜底 full；随后宿主还会 push 权威值
    initMode((mode) => requestMode(mode));
    initModeGetter(() => state.mode);
    applyMode(init.mode || 'full');
    logPanel && logPanel.append('secondary', '>>> [界面] 当前窗口模式: ' + state.mode);

    // 宿主以 mini/classic 启动时直接落到对应视图，不被 app.init.page 覆盖
    const startPage = (state.mode === 'mini' || state.mode === 'classic') ? state.mode : (state.page || 'dash');
    state.page = startPage;
    setSub('正在准备界面…');
    navigate(startPage);
    applyPill(state.status);
    await hideSplash();
  } catch (err) {
    document.getElementById('splashSub').textContent = '初始化失败：' + (err.message || err);
    return;
  }

  // 通知宿主：界面就绪（宿主据此刷新日志/状态）
  call('app.ready', {}).catch(() => { });
}

function hideSplash() {
  const splash = document.getElementById('splash');
  return new Promise((resolve) => {
    if (!splash) { resolve(); return; }
    // 进度条补满再淡出，与宿主启动页同一收尾节奏
    const bar = document.getElementById('splashBar');
    if (bar) bar.style.transform = 'scaleX(1)';
    setTimeout(() => {
      splash.classList.add('out');
      setTimeout(() => { if (splash.parentNode) splash.parentNode.removeChild(splash); resolve(); }, 190);
    }, 140);
  });
}

window.addEventListener('error', (ev) => {
  // 无头工具的布局审计会检查 console.error 数量（§8.3）
  console.error('[ui] 未捕获异常', ev.message);
});

boot();
