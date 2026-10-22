/* ==========================================================================
   pages/dash.js — 「开始」页（§6.3，对齐 dash.png）
   - 所有按钮接真实 bridge 命令；按钮常态可点（仅执行中临时禁用），
     不适用的操作写入日志并提示，不做静默禁用。
   - 按钮行为集中在 ui/actions.js（极简/经典模式共用同一实现）。
   - 状态变化走 applyStatus() 原地更新（不重建 DOM）。
   ========================================================================== */

import { h, icon } from '../ui/dom.js';
import { statusView } from '../ui/status.js';
import {
  wirePlay, wireStop, wireRestart, wireGm, wireRestorePvf, createDxControls,
  wireUpdate, wireFullUpdate, wireUpdateLog, wireAumUpdate, wireSdkInstall, openShell,
} from '../ui/actions.js';

function statCard(caption, bind, value, tone, cls) {
  return h('div.stat-card' + (cls ? '.' + cls : ''), { dataset: { bind } }, [
    h('div.s-caption', { text: caption }),
    h('div.s-value' + (tone ? ' ' + tone : ''), [
      tone ? h('span.dot') : null,
      h('span.s-text', { text: value || '—' }),
    ]),
  ]);
}

/**
 * 原地更新页面上的状态绑定元素（不重建 DOM）。
 * @param {HTMLElement} root 页面根（#page）
 * @param {object} st 宿主状态快照
 * @param {object} [ctx] 渲染上下文（取版本兜底）
 */
export function applyStatus(root, st, ctx) {
  if (!root || !st) return;
  const view = statusView(st, ctx);
  for (const key of Object.keys(view)) {
    const host = root.querySelector('[data-bind="' + key + '"]');
    if (!host) continue;
    const item = view[key];
    if (host.classList.contains('stat-card')) {
      const box = host.querySelector('.s-value');
      const text = host.querySelector('.s-text');
      if (box) box.className = 's-value' + (item.tone ? ' ' + item.tone : '');
      if (text) text.textContent = item.text;
    } else {
      host.textContent = item.text;
    }
  }
}

export function renderDash(pageEl, ctx) {
  const view = statusView(ctx.status, ctx);
  const frag = document.createDocumentFragment();

  // ---------------- 页头 ----------------
  frag.appendChild(h('div.page-head', [
    h('div.ph-text', [
      h('div.page-title', { text: '开始' }),
      h('div.page-sub', { text: '服务端与游戏的一键控制台 · 常用操作都集中在这里' }),
    ]),
  ]));

  // ---------------- 统计卡 4 张 ----------------
  frag.appendChild(h('div.stat-row', [
    statCard('服务端状态', 'server', view.server.text, view.server.tone),
    statCard('PVF 状态', 'pvf', view.pvf.text, view.pvf.tone),
    statCard('服务端版本', 'version', view.version.text, null),
    statCard('.NET SDK', 'sdk', view.sdk.text, view.sdk.tone, 'compact'),
  ]));

  // ---------------- 左：服务端控制 ----------------
  const btnPlay = wirePlay(h('button.btn.tall.block.success', { type: 'button' }, [icon('play'), h('span', { text: '开始游戏' })]), ctx);
  const btnStop = wireStop(h('button.btn.danger', { type: 'button' }, [icon('stop'), h('span', { text: '停止服务端' })]), ctx);
  const btnRestart = wireRestart(h('button.btn.warning', { type: 'button' }, [icon('reload'), h('span', { text: '重启服务端' })]), ctx);

  const dx = createDxControls(ctx);

  const btnPvfDir = h('button.btn', { type: 'button' }, [icon('folder'), h('span', { text: '打开PVF目录' })]);
  btnPvfDir.addEventListener('click', () => openShell(ctx, 'pvfDir'));
  const btnGm = wireGm(h('button.btn', { type: 'button' }, [icon('tool'), h('span', { text: 'GM工具' })]), ctx);
  const btnSave = h('button.btn', { type: 'button' }, [icon('database'), h('span', { text: '打开主存档' })]);
  btnSave.addEventListener('click', () => openShell(ctx, 'saveDir'));
  const btnRestorePvf = wireRestorePvf(h('button.btn.block', { type: 'button' }, [icon('rollback'), h('span', { text: '还原-原版PVF' })]), ctx);

  const leftCard = h('div.card', [
    h('div.sec-title', [h('span', { text: '服务端控制' })]),
    h('div.ctrl-stack', [
      btnPlay,
      h('div.btn-row3', [
        btnStop,
        h('div', { style: { gridColumn: '2 / 3' } }),
        btnRestart,
      ]),
      h('div.opt-row', [
        h('span.o-label', { text: '可选运行方式' }),
        h('span.o-spacer'),
        dx.seg.el,
      ]),
      h('div.opt-row', [
        dx.sw.el,
      ]),
      h('div.btn-row3', [btnPvfDir, btnGm, btnSave]),
      btnRestorePvf,
    ]),
    h('div.hint', { text: '提示：首次使用请先完成一次更新；服务端运行中会阻止存档切换等操作。' }),
  ]);

  // ---------------- 右：更新与工具 ----------------
  const btnUpdate = wireUpdate(h('button.btn.tall.block.primary', { type: 'button', id: 'btnUpdate' },
    [icon('thunderbolt'), h('span', { text: '开始更新' })]), ctx);
  const btnFull = wireFullUpdate(h('button.btn-text', { type: 'button', id: 'btnFullUpdate', 'data-tip': '重新校验并覆盖全部文件，耗时较长' },
    [icon('sync'), h('span', { text: '完整更新' })]), ctx);
  const btnUpdateLog = wireUpdateLog(h('button.btn.block', { type: 'button' }, [icon('file-text'), h('span', { text: '查看更新日志' })]), ctx);
  const btnAum = wireAumUpdate(h('button.btn', {
    type: 'button',
    'data-tip': 'WebView2 重构版：AUM 自更新暂未接入新版源码，点击只提示、不会回退界面',
  }, [icon('download'), h('span', { text: '更新AUM' })]), ctx);
  const btnSdk = wireSdkInstall(h('button.btn', { type: 'button' }, [icon('cpu'), h('span', { text: '安装NET.10 SDK' })]), ctx);

  const rightCard = h('div.card', [
    h('div.sec-title', [h('span', { text: '更新与工具' })]),
    h('div.ctrl-stack', [
      btnUpdate,
      h('div.opt-row.upd-sub', [
        h('span.o-label', { text: '增量更新：只更新变化的文件（推荐）' }),
        h('span.o-spacer'),
        btnFull,
      ]),
      btnUpdateLog,
      h('div.info-line', [
        icon('clock'),
        h('span.mono', { dataset: { bind: 'lastUpdate' }, text: view.lastUpdate.text }),
      ]),
      h('div.btn-row2', [btnAum, btnSdk]),
    ]),
    h('div.hint', { text: '提示：更新耗时较长，期间请勿重复点击；更新失败可勾选下方「镜像下载」后重试。' }),
  ]);

  // ---------------- 两栏 ----------------
  frag.appendChild(h('div.dash-cols', [leftCard, rightCard]));
  pageEl.appendChild(frag);
  return null;
}
