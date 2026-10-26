/* ==========================================================================
   pages/classic.js — 经典模式视图（1280×840，最小 1080×700）
   对照旧 ClassicForm.cs 的一站式布局：顶部 4 项状态卡 → 左栏（开始游戏 /
   服务端控制 / 快速操作 / 更新与工具）+ 右栏（存档管理）→ 底部共享日志
   （全局 #logWrap，不在此页内）。

   高度预算（1280×840，日志 190px）：左栏三张卡 + 独立开始游戏行全部放进
   首屏，左栏不出现内部滚动；1080×700 才由 .classic-page 整页滚动。

   - 侧栏由 .app[data-mode="classic"] 隐藏；日志面板沿用完整模式的同一实例
   - 所有动作走 ui/actions.js 与 ui/archive-core.js（与完整模式共用，无复制逻辑）
   ========================================================================== */

import { h, icon } from '../ui/dom.js';
import { requestMode } from '../ui/mode.js';
import { statusView } from '../ui/status.js';
import { applyStatus as applyStatCards } from './dash.js';
import {
  wirePlay, wireStop, wireRestart, wireGm, wireRestorePvf, openShell,
  createDxControls, wireUpdate, wireFullUpdate, wireUpdateLog, wireAumUpdate, wireSdkInstall,
} from '../ui/actions.js';
import {
  createArchiveStore, createArchiveTable, createArchiveInfo, createArchiveDrop,
  setActiveArchiveSurface,
} from '../ui/archive-core.js';

function statCard(caption, bind, value, tone, cls, tip) {
  return h('div.stat-card' + (cls ? '.' + cls : ''), { dataset: { bind }, 'data-tip': tip || null }, [
    h('div.s-caption', { text: caption }),
    h('div.s-value' + (tone ? ' ' + tone : ''), [
      tone ? h('span.dot') : null,
      h('span.s-text', { text: value || '—' }),
    ]),
  ]);
}

export function renderClassic(pageEl, ctx) {
  const view = statusView(ctx.status, ctx);
  const store = createArchiveStore(ctx);
  const table = createArchiveTable(store, { variant: 'classic' });
  const info = createArchiveInfo(store, ctx, { refreshId: 'btnClArcRefresh', shortCurrent: true });
  const drop = createArchiveDrop(store, ctx);

  // ---------------------------------------------------------------- 页头（左上：返回完整模式）
  const btnBack = h('button.btn.sm.cl-back', {
    type: 'button', id: 'btnClassicBack', 'data-tip': '返回完整模式',
    onclick: () => requestMode('full'),
  }, [icon('restore'), h('span', { text: '返回完整模式' })]);

  const head = h('div.cl-head', [
    h('div.ph-text', [
      h('div.page-title.cl-title', { text: '经典模式' }),
      h('div.page-sub', { text: '一站式布局 · 服务端、更新与存档管理同屏' }),
    ]),
    btnBack,
  ]);

  // ---------------------------------------------------------------- 顶部 4 项状态卡
  const stats = h('div.stat-row.cl-stats', [
    statCard('服务端', 'server', view.server.text, view.server.tone),
    statCard('PVF', 'pvf', view.pvf.text, view.pvf.tone),
    statCard('版本', 'version', view.version.text, null),
    statCard('上次更新', 'lastUpdateShort', view.lastUpdateShort.text, null, 'wide', view.lastUpdateShort.tip),
  ]);

  // ---------------------------------------------------------------- 开始游戏（独立 56px 行）
  const btnPlay = h('button.btn.tall.block.success.cl-play', { type: 'button', id: 'btnClPlay' },
    [icon('play'), h('span', { text: '开始游戏' })]);
  wirePlay(btnPlay, ctx);
  const playRow = h('div.cl-play-row', [btnPlay]);

  // ---------------------------------------------------------------- 服务端控制（停止 / 重启 + 运行方式 + 水印）
  const btnStop = h('button.btn.danger', { type: 'button', id: 'btnClStop' }, [icon('stop'), h('span', { text: '停止服务端' })]);
  wireStop(btnStop, ctx);
  const btnRestart = h('button.btn.warning', { type: 'button', id: 'btnClRestart' }, [icon('reload'), h('span', { text: '重启服务端' })]);
  wireRestart(btnRestart, ctx);
  const dx = createDxControls(ctx, { short: true });

  const cardServer = h('div.card.cl-card.cl-server', [
    h('div.sec-title', [h('span', { text: '服务端控制' })]),
    h('div.ctrl-stack', [
      h('div.btn-row2', [btnStop, btnRestart]),
      h('div.opt-row.opt-wrap', [
        h('span.o-label', { text: '可选运行方式' }),
        h('span.o-spacer'),
        dx.seg.el,
      ]),
      h('div.opt-row', [dx.sw.el]),
    ]),
  ]);

  // ---------------------------------------------------------------- 快速操作（目录 + 还原PVF）
  const btnPvfDir = h('button.btn', { type: 'button', id: 'btnClPvfDir' }, [icon('folder'), h('span', { text: '打开PVF目录' })]);
  btnPvfDir.addEventListener('click', () => openShell(ctx, 'pvfDir'));
  const btnGm = h('button.btn', { type: 'button', id: 'btnClGm' }, [icon('tool'), h('span', { text: 'GM工具' })]);
  wireGm(btnGm, ctx);
  const btnSave = h('button.btn', { type: 'button', id: 'btnClSaveDir' }, [icon('database'), h('span', { text: '打开主存档' })]);
  btnSave.addEventListener('click', () => openShell(ctx, 'saveDir'));
  const btnRestorePvf = h('button.btn.block', { type: 'button', id: 'btnClRestorePvf' },
    [icon('rollback'), h('span', { text: '还原-原版PVF' })]);
  wireRestorePvf(btnRestorePvf, ctx);

  const cardQuick = h('div.card.cl-card.cl-quick', [
    h('div.sec-title', [h('span', { text: '快速操作' })]),
    h('div.ctrl-stack', [
      h('div.btn-row2', [btnPvfDir, btnGm]),
      h('div.btn-row2', [btnSave, btnRestorePvf]),
    ]),
  ]);

  // ---------------------------------------------------------------- 更新与工具（2 列紧凑排布）
  const btnUpdate = h('button.btn.block.primary.cl-update', { type: 'button', id: 'btnClUpdate' },
    [icon('thunderbolt'), h('span', { text: '开始更新' })]);
  wireUpdate(btnUpdate, ctx);
  const btnFull = h('button.btn.block.sm', { type: 'button', id: 'btnClFullUpdate', 'data-tip': '重新校验并覆盖全部文件，耗时较长' },
    [icon('sync'), h('span', { text: '完整更新' })]);
  wireFullUpdate(btnFull, ctx);
  const btnUpdateLog = h('button.btn.block', { type: 'button', id: 'btnClUpdateLog' },
    [icon('file-text'), h('span', { text: '查看更新日志' })]);
  wireUpdateLog(btnUpdateLog, ctx);
  const btnAum = h('button.btn', {
    type: 'button', id: 'btnClAum',
    'data-tip': 'WebView2 重构版：AUM 自更新暂未接入新版源码，点击只提示、不会回退界面',
  }, [icon('download'), h('span', { text: '更新AUM' })]);
  wireAumUpdate(btnAum, ctx);
  const btnSdk = h('button.btn', { type: 'button', id: 'btnClSdk' }, [icon('cpu'), h('span', { text: '安装NET.10 SDK' })]);
  wireSdkInstall(btnSdk, ctx);

  const cardTools = h('div.card.cl-card.cl-tools', [
    h('div.sec-title', [h('span', { text: '更新与工具' })]),
    h('div.cl-tools-grid', [
      h('div.cl-tools-col', [
        btnUpdate,
        h('div.info-line', [
          icon('sync'),
          h('span', { text: '增量更新：只更新变化的文件（推荐）' }),
        ]),
        btnFull,
      ]),
      h('div.cl-tools-col', [
        btnUpdateLog,
        h('div.info-line', [
          icon('clock'),
          h('span.mono', { dataset: { bind: 'lastUpdate' }, text: view.lastUpdate.text }),
        ]),
        h('div.btn-row2', [btnAum, btnSdk]),
      ]),
    ]),
  ]);

  // ---------------------------------------------------------------- 右栏：存档管理
  const btnStore = h('button.btn.primary', { type: 'button', id: 'btnClArcStore' }, [icon('save'), h('span', { text: '储存当前' })]);
  const btnImport = h('button.btn', { type: 'button', id: 'btnClArcImport' }, [icon('upload'), h('span', { text: '导入存档' })]);
  const btnExport = h('button.btn', { type: 'button', id: 'btnClArcExport' }, [icon('download'), h('span', { text: '导出当前' })]);
  const btnUndo = h('button.btn', { type: 'button', id: 'btnClArcUndo' }, [icon('rollback'), h('span', { text: '撤销换挡' })]);
  btnStore.addEventListener('click', () => store.store());
  btnImport.addEventListener('click', () => store.importZip());
  btnExport.addEventListener('click', () => store.exportZip());
  btnUndo.addEventListener('click', () => store.undo());

  const cardArchive = h('div.card.cl-card.cl-archive', [
    h('div.sec-title', [h('span', { text: '存档管理' })]),
    info.el,
    h('div.arc-ops.cl-ops', [btnStore, btnImport, btnExport, btnUndo]),
    table.el,
    drop,
  ]);

  const leftCol = h('div.cl-col.cl-left', [
    playRow,
    h('div.cl-split', [cardServer, cardQuick]),
    cardTools,
  ]);
  const rightCol = h('div.cl-col.cl-right', [cardArchive]);
  const cols = h('div.cl-cols', [leftCol, rightCol]);

  const page = h('div.classic-page', [head, stats, cols]);
  pageEl.appendChild(page);

  // 忙碌态（存档操作中淡出按钮，与完整页一致）
  let wasBusy = false;
  store.subscribe((s) => {
    if (s.busy === wasBusy) return;
    wasBusy = s.busy;
    page.classList.toggle('busy', !!s.busy);
  });

  // ---------------------------------------------------------------- 初始数据
  setActiveArchiveSurface({
    drop: (path) => { store.drop(path); },
    alive: () => table.tbody.isConnected,
    refresh: () => store.refresh(),
  });
  store.refresh();
  return null;
}

/** 状态推送落地：顶部 4 张状态卡（与开始页同一套 stat-card 结构）+ 更新卡上次更新行。 */
export function applyStatus(root, st, ctx) {
  if (!root || !st) return;
  applyStatCards(root, st, ctx);
  const info = root.querySelector('.info-line [data-bind="lastUpdate"]');
  if (info) {
    const view = statusView(st, ctx);
    info.textContent = view.lastUpdate.text;
  }
}
