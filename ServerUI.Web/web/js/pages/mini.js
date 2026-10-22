/* ==========================================================================
   pages/mini.js — 极简模式视图（620×470，对照旧 MiniForm.cs）
   一个紧凑单列页：状态行 / 开始游戏 / GM+停止 / 存档操作 / 紧凑存档表 / 提示
   - 侧栏与日志面板由 .app[data-mode="mini"] 隐藏（CSS），本页不重复实现
   - 业务动作全部走 ui/actions.js 与 ui/archive-core.js（与完整模式共用）
   ========================================================================== */

import { h, icon } from '../ui/dom.js';
import { requestMode } from '../ui/mode.js';
import { statusView } from '../ui/status.js';
import { wirePlay, wireStop, wireGm } from '../ui/actions.js';
import {
  createArchiveStore, createArchiveTable, setActiveArchiveSurface,
} from '../ui/archive-core.js';

const HINT = '提示: 服务端运行中无法操作存档; 停服请点【停止服务端】(优雅停服防止回档)';

export function renderMini(pageEl, ctx) {
  const store = createArchiveStore(ctx);
  const table = createArchiveTable(store, { variant: 'mini' });
  const view = statusView(ctx.status, ctx);

  // ---------------------------------------------------------------- 状态行 + 返回
  const elServer = h('span.mi-state', { dataset: { bind: 'server' } }, [
    h('span.dot'), h('span', { text: view.server.text }),
  ]);
  elServer.classList.add(view.server.tone || 'off');

  const elPvf = h('span.mi-kv', { dataset: { bind: 'pvf' }, text: 'PVF: ' + view.pvf.text });
  elPvf.classList.add(view.pvf.tone || 'off');
  const elVer = h('span.mi-kv', { dataset: { bind: 'version' }, text: '版本: ' + view.version.text });

  const btnBack = h('button.btn.sm.mi-back', {
    type: 'button', id: 'btnMiniBack', 'data-tip': '返回完整模式',
    onclick: () => requestMode('full'),
  }, [icon('restore'), h('span', { text: '返回完整模式' })]);

  const head = h('div.mi-head', [
    h('div.mi-status', [elServer, h('span.mi-sep'), elPvf, h('span.mi-sep'), elVer]),
    btnBack,
  ]);

  // ---------------------------------------------------------------- 主按钮
  const btnPlay = h('button.btn.tall.block.success', { type: 'button', id: 'btnMiniPlay' },
    [icon('play'), h('span', { text: '开始游戏' })]);
  wirePlay(btnPlay, ctx);

  // ---------------------------------------------------------------- GM / 停止
  const btnGm = h('button.btn', { type: 'button', id: 'btnMiniGm' }, [icon('tool'), h('span', { text: 'GM工具' })]);
  wireGm(btnGm, ctx);

  const btnStop = h('button.btn.danger', { type: 'button', id: 'btnMiniStop' }, [icon('stop'), h('span', { text: '停止服务端' })]);
  wireStop(btnStop, ctx);

  // ---------------------------------------------------------------- 存档操作
  const btnStore = h('button.btn.primary', { type: 'button', id: 'btnMiniStore' }, [icon('save'), h('span', { text: '储存当前' })]);
  const btnUndo = h('button.btn', { type: 'button', id: 'btnMiniUndo' }, [icon('rollback'), h('span', { text: '撤销换挡' })]);
  const btnRefresh = h('button.btn', { type: 'button', id: 'btnMiniRefresh' }, [icon('refresh'), h('span', { text: '刷新' })]);

  btnStore.addEventListener('click', () => store.store());
  btnUndo.addEventListener('click', () => store.undo());
  btnRefresh.addEventListener('click', () => {
    store.say('secondary', '>>> 刷新存档列表');
    store.refresh();
  });

  // ---------------------------------------------------------------- 表格（占剩余高度）
  const page = h('div.mini-page', [
    head,
    btnPlay,
    h('div.mi-row2', [btnGm, btnStop]),
    h('div.mi-row3', [btnStore, btnUndo, btnRefresh]),
    table.el,
    h('div.mi-hint', { text: HINT }),
  ]);

  store.subscribe((s) => {
    page.classList.toggle('busy', !!s.busy);
  });

  pageEl.appendChild(page);

  // ---------------------------------------------------------------- 初始数据
  setActiveArchiveSurface({
    drop: (path) => { store.drop(path); },
    alive: () => table.tbody.isConnected,
    refresh: () => store.refresh(),
  });
  store.refresh();
  return null;
}

/** 状态推送落地（不重建 DOM）。 */
export function applyStatus(root, st, ctx) {
  if (!root || !st) return;
  const view = statusView(st, ctx);
  const server = root.querySelector('[data-bind="server"]');
  if (server) {
    const txt = server.querySelector('span:last-child');
    if (txt) txt.textContent = view.server.text;
    server.className = 'mi-state ' + (view.server.tone || 'off');
  }
  const pvf = root.querySelector('[data-bind="pvf"]');
  if (pvf) {
    pvf.textContent = 'PVF: ' + view.pvf.text;
    pvf.className = 'mi-kv ' + (view.pvf.tone || 'off');
  }
  const ver = root.querySelector('[data-bind="version"]');
  if (ver) ver.textContent = '版本: ' + view.version.text;
}
