/* ==========================================================================
   pages/archive.js — 存档管理页（Phase 2 · B）
   对照旧 MainForm.Pages.cs BuildArchive + MainForm.Archive.cs：
     信息栏（当前 / 备份数 / 清理冗余DB / 刷新存档）
     常用操作（储存当前 / 导入存档 / 导出当前 / 撤销换挡）
     目录访问（打开切换库 / 打开备份库 / 打开主存档）
     列表（# / 存档名称 / 大小 / 修改时间，双击切换、右键主题化菜单、点表头排序）
     拖拽区（拖入 .db 快速换挡；双击 = 储存当前）

   数据与动作在 ui/archive-core.js（极简/经典模式共用同一内核），
   本页只负责完整模式的排版。
   ========================================================================== */

import { h, icon } from '../ui/dom.js';
import {
  createArchiveStore, createArchiveTable, createArchiveInfo, createArchiveDrop,
  setActiveArchiveSurface, archiveDrop as coreDrop, refreshArchiveIfActive as coreRefresh,
} from '../ui/archive-core.js';
import { openShell } from '../ui/actions.js';

const SUB = '切换、备份与导入导出存档 · 双击列表换挡，拖动 .db 到窗口也能换';

export function renderArchive(pageEl, ctx) {
  const wrap = h('div.arc-page');
  const frag = document.createDocumentFragment();

  const store = createArchiveStore(ctx);
  const table = createArchiveTable(store, { variant: 'full' });
  const info = createArchiveInfo(store, ctx, { refreshId: 'btnArcRefresh' });
  const drop = createArchiveDrop(store, ctx);

  // ---------------------------------------------------------------- 页头
  frag.appendChild(h('div.page-head', [
    h('div.ph-text', [
      h('div.page-title', { text: '存档管理' }),
      h('div.page-sub', { text: SUB }),
    ]),
  ]));

  // ---------------------------------------------------------------- 信息栏
  frag.appendChild(info.el);

  // ---------------------------------------------------------------- 常用操作
  const btnStore = h('button.btn.primary', { type: 'button', id: 'btnArcStore' }, [icon('save'), h('span', { text: '储存当前' })]);
  const btnImport = h('button.btn', { type: 'button', id: 'btnArcImport' }, [icon('folder'), h('span', { text: '导入存档' })]);
  const btnExport = h('button.btn', { type: 'button', id: 'btnArcExport' }, [icon('download'), h('span', { text: '导出当前' })]);
  const btnUndo = h('button.btn', { type: 'button', id: 'btnArcUndo' }, [icon('rollback'), h('span', { text: '撤销换挡' })]);

  btnStore.addEventListener('click', () => store.store());
  btnImport.addEventListener('click', () => store.importZip());
  btnExport.addEventListener('click', () => store.exportZip());
  btnUndo.addEventListener('click', () => store.undo());

  frag.appendChild(h('div.arc-ops', [btnStore, btnImport, btnExport, btnUndo]));

  // ---------------------------------------------------------------- 目录访问
  frag.appendChild(h('div.arc-dirs', [
    h('button.btn', { type: 'button', onclick: () => openShell(ctx, 'switchDir') }, [icon('folder'), h('span', { text: '打开切换库' })]),
    h('button.btn', { type: 'button', onclick: () => openShell(ctx, 'backupDir') }, [icon('folder'), h('span', { text: '打开备份库' })]),
    h('button.btn', { type: 'button', onclick: () => openShell(ctx, 'saveDir') }, [icon('folder'), h('span', { text: '打开主存档' })]),
  ]));

  // ---------------------------------------------------------------- 列表 + 拖拽区
  frag.appendChild(table.el);
  frag.appendChild(drop);

  wrap.appendChild(frag);
  pageEl.appendChild(wrap);

  // 忙碌态淡出操作区（与旧 CSS 行为一致）
  let wasBusy = false;
  store.subscribe((s) => {
    if (s.busy === wasBusy) return;
    wasBusy = s.busy;
    wrap.classList.toggle('busy', !!s.busy);
  });

  // ---------------------------------------------------------------- 初始化
  setActiveArchiveSurface({
    drop: (path) => { store.drop(path); },
    alive: () => table.tbody.isConnected,
    refresh: () => store.refresh(),
  });

  store.refresh();
  return null;
}

/** 宿主 drop 推送：仅当存档页仍挂载时处理。 */
export function archiveDrop(path) { return coreDrop(path); }

/** `archive.changed` 推送时由 app.js 调用：仅当存档视图仍挂载才刷新。 */
export function refreshArchiveIfActive() { return coreRefresh(); }
