/* ==========================================================================
   ui/logpanel.js — 运行日志面板（§6.4）
   - 行格式 [HH:mm:ss] 文本，最多 2000 行，只在上滚到底部时自动跟随
   - 追加支持批量：一批日志只做一次 DOM 插入 + 一次 scrollHeight 读取 + rAF 滚动
   - toolbar：跳过更新日志 / 镜像下载 两个开关 + 进度文字 + 复制 / 清空 / 折叠
   - 分隔抓手：拖动改高度（min 80，max 窗口 42%），双击复位 190px
   ========================================================================== */

import { h, icon } from './dom.js';
import { createSwitch } from './switch.js';
import { call } from '../bridge.js';

const MAX_LINES = 2000;
const DEFAULT_H = 190;
const MIN_H = 80;
const MAX_RATIO = 0.42;

function stamp() {
  const d = new Date();
  const p = (n) => (n < 10 ? '0' + n : '' + n);
  return '[' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds()) + '] ';
}

/** 文本包含的显示行数（换行符 + 1）。 */
function countLines(text) {
  const s = String(text || '');
  let n = 1;
  for (let i = 0; i < s.length; i++) if (s.charCodeAt(i) === 10) n++;
  return n;
}

export function createLogPanel(refs, settings) {
  const { wrap, body, empty, resizer, progress, progressBar, progressLabel, switches, copyBtn, clearBtn, collapseBtn } = refs;

  let lineCount = 0;
  let follow = true;
  let collapsed = !!(settings && settings.logCollapsed);
  let lastProgressText = '';
  let scrollQueued = false;

  // ---------- 追加日志 ----------
  function makeLine(level, text) {
    const el = h('div.log-line', { text: stamp() + String(text) });
    if (level && level !== 'default') el.classList.add('lv-' + level);
    return el;
  }

  function atBottom() {
    return body.scrollHeight - body.scrollTop - body.clientHeight < 24;
  }

  function removePlaceholder() {
    const ph = body.querySelector('.log-empty');
    if (ph && ph.parentNode) ph.parentNode.removeChild(ph);
  }

  /** 滚到底部：整段日志能装下时只隐藏顶部内边距（任何一行都不被裁）；
      装不下时贴底显示最新行（旧 UI / 终端行为，顶端行允许被裁）。 */
  function scrollToBottom() {
    const max = body.scrollHeight - body.clientHeight;
    if (max <= 0) { body.scrollTop = 0; return; }
    const cs = getComputedStyle(body);
    const padTop = parseFloat(cs.paddingTop) || 0;
    const padBottom = parseFloat(cs.paddingBottom) || 0;
    const linesH = Math.max(0, body.scrollHeight - padTop - padBottom);
    let top = max;
    if (linesH > 0 && linesH <= body.clientHeight) top = Math.min(max, padTop);
    body.scrollTop = top;
  }

  /** 合并同一帧内的多次滚动请求（批量插入期间不读 scrollHeight）。 */
  function scheduleScroll() {
    if (scrollQueued) return;
    scrollQueued = true;
    requestAnimationFrame(() => {
      scrollQueued = false;
      if (follow || atBottom()) scrollToBottom();
    });
  }

  function trim() {
    while (body.childElementCount > MAX_LINES && body.firstChild) {
      lineCount -= countLines(body.firstChild.textContent);
      body.removeChild(body.firstChild);
    }
    trimByCount();
  }

  /**
   * 批量追加：一次 DocumentFragment 插入、一次裁剪、一次滚动调度。
   * @param {Array<{level?:string,text:string}>} items
   */
  function appendMany(items) {
    if (!items || !items.length) return;
    const keepBottom = follow || atBottom();
    const frag = document.createDocumentFragment();
    for (const it of items) {
      if (!it || it.text === null || it.text === undefined) continue;
      const s = String(it.text);
      if (!s) continue;
      frag.appendChild(makeLine(it.level, s));
      lineCount += countLines(s);
    }
    if (!frag.childNodes.length) return;

    removePlaceholder();
    body.appendChild(frag);
    trim();
    if (keepBottom) scheduleScroll();
  }

  function append(level, text) {
    if (text === null || text === undefined) return;
    const s = String(text);
    if (!s) return;
    appendMany([{ level, text: s }]);
  }

  function trimByCount() {
    while (lineCount > MAX_LINES && body.firstChild) {
      const first = body.firstChild;
      lineCount -= countLines(first.textContent);
      body.removeChild(first);
    }
  }

  function clear() {
    while (body.firstChild) body.removeChild(body.firstChild);
    lineCount = 0;
    body.scrollTop = 0;
    body.appendChild(h('div.log-empty', { id: 'logEmpty', text: '暂无日志。' }));
  }

  function text() {
    const out = [];
    for (const node of Array.from(body.children)) {
      if (node.classList && node.classList.contains('log-empty')) continue;
      out.push(node.textContent || '');
    }
    return out.join('\r\n');
  }

  // ---------- 进度（toolbar 3px 条 + 文字） ----------
  function setProgress(value, label, visible, keepLabel) {
    if (visible === undefined && label === undefined) return;
    const show = visible === true;
    if (progress) progress.classList.toggle('hidden', !show);
    if (show) {
      const v = Math.max(0, Math.min(100, value || 0));
      if (progressBar) progressBar.style.width = v + '%';
      if (progressLabel) progressLabel.textContent = label || ('更新进度: ' + Math.round(v) + '%');
      lastProgressText = progressLabel ? progressLabel.textContent : '';
    } else {
      if (progressBar) progressBar.style.width = '0%';
      if (progressLabel && !keepLabel) progressLabel.textContent = '';
      if (!keepLabel) lastProgressText = '';
    }
  }

  // ---------- 折叠 ----------
  function setCollapsed(v, silent) {
    collapsed = !!v;
    if (wrap) wrap.classList.toggle('collapsed', collapsed);
    if (collapseBtn) {
      const svg = collapseBtn.querySelector('svg');
      if (svg) svg.style.transform = collapsed ? 'rotate(180deg)' : 'rotate(0deg)';
    }
    if (!silent) persist({ logCollapsed: collapsed });
  }

  // ---------- 高度 ----------
  function applyHeight(px, silent) {
    const h2 = Math.round(px);
    wrap.style.flex = '0 0 ' + h2 + 'px';
    wrap.style.height = h2 + 'px';
    document.documentElement.style.setProperty('--h-log', h2 + 'px');
    if (!silent) persist({ logHeight: h2 });
    return h2;
  }

  function currentHeight() {
    return wrap.getBoundingClientRect().height || DEFAULT_H;
  }

  function maxHeight() {
    return Math.max(MIN_H, Math.round(window.innerHeight * MAX_RATIO));
  }

  // ---------- 持久化（合并 250ms） ----------
  let persistTimer = 0;
  const pending = {};
  function persist(patch) {
    Object.assign(pending, patch);
    if (settings) Object.assign(settings, patch);
    clearTimeout(persistTimer);
    persistTimer = setTimeout(() => {
      call('settings.set', Object.assign({}, pending)).catch(() => { });
      for (const k of Object.keys(pending)) delete pending[k];
    }, 250);
  }

  // ---------- 交互绑定 ----------
  if (copyBtn) {
    copyBtn.addEventListener('click', async () => {
      const t = text();
      if (!t) return;
      try { await call('clipboard.write', { text: t }); } catch { }
    });
  }
  if (clearBtn) clearBtn.addEventListener('click', () => clear());
  if (collapseBtn) collapseBtn.addEventListener('click', () => setCollapsed(!collapsed));

  if (body) {
    body.addEventListener('scroll', () => {
      follow = body.scrollHeight - body.scrollTop - body.clientHeight < 24;
    }, { passive: true });
  }

  if (resizer) {
    let dragging = false;
    let startY = 0;
    let startH = 0;

    resizer.addEventListener('mousedown', (ev) => {
      if (ev.button !== 0) return;
      dragging = true;
      startY = ev.clientY;
      startH = currentHeight();
      resizer.classList.add('dragging');
      document.body.style.cursor = 'ns-resize';
      ev.preventDefault();
    });

    window.addEventListener('mousemove', (ev) => {
      if (!dragging) return;
      const dy = startY - ev.clientY;
      const next = Math.max(MIN_H, Math.min(maxHeight(), startH + dy));
      applyHeight(next, true);
    });

    window.addEventListener('mouseup', () => {
      if (!dragging) return;
      dragging = false;
      resizer.classList.remove('dragging');
      document.body.style.cursor = '';
      persist({ logHeight: currentHeight() });
    });

    resizer.addEventListener('dblclick', () => applyHeight(DEFAULT_H));
  }

  // ---------- toolbar 开关 ----------
  const skipSwitch = createSwitch({
    label: '跳过更新日志',
    checked: !!(settings && settings.skipLog),
    onChange: (on) => persist({ skipLog: on }),
  });
  const mirrorSwitch = createSwitch({
    label: '镜像下载',
    checked: !!(settings && settings.mirror),
    onChange: (on) => persist({ mirror: on }),
  });
  if (switches) {
    switches.appendChild(skipSwitch.el);
    switches.appendChild(mirrorSwitch.el);
  }

  // 初始状态
  applyHeight((settings && settings.logHeight) || DEFAULT_H, true);
  setCollapsed(collapsed, true);
  setProgress(0, '', false);

  return {
    append,
    appendMany,
    clear,
    text,
    setProgress,
    setCollapsed,
    applyHeight,
    isCollapsed: () => collapsed,
    skipLog: () => skipSwitch.get(),
    mirror: () => mirrorSwitch.get(),
    setSkipLog: (v) => skipSwitch.set(!!v, true),
    setMirror: (v) => mirrorSwitch.set(!!v, true),
  };
}
