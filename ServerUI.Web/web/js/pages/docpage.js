/* ==========================================================================
   pages/docpage.js — 使用说明 / 疑难杂症解惑 的共用布局（Phase 3a）
   旧 MainForm.Pages.cs BuildHelp / MainForm.Troubleshoot.cs 的同一套结构：
     左 312px 卡片：分组标题 + 单列按钮列表（44 高、252 宽），可滚动
     右卡片：标题（16pt 粗体）+ 分隔线 + 正文（10pt 灰、pre-wrap、可滚动）
   切换条目只更新右侧标题/正文，不重建整页（左栏滚动位置不跳）。
   ========================================================================== */

import { h, icon } from '../ui/dom.js';

// Ant Design 线性图标名 → 本项目 sprite 的近似映射
const ICON_MAP = {
  RocketOutlined: 'thunderbolt',
  PlayCircleOutlined: 'play',
  StopOutlined: 'stop',
  ReloadOutlined: 'reload',
  ThunderboltOutlined: 'thunderbolt',
  WatermarkOutlined: 'image',
  FolderOpenOutlined: 'folder',
  DatabaseOutlined: 'database',
  ToolOutlined: 'tool',
  FileTextOutlined: 'file-text',
  SyncOutlined: 'sync',
  DownloadOutlined: 'download',
  SafetyOutlined: 'shield',
  ApiOutlined: 'box',
  ClockCircleOutlined: 'clock',
  RollbackOutlined: 'rollback',
  ConsoleSqlOutlined: 'list',
  UndoOutlined: 'undo',
  LaptopOutlined: 'box',
  FullscreenOutlined: 'max',
  HomeOutlined: 'gauge',
  CloudDownloadOutlined: 'download',
  BugOutlined: 'alert',
  HourglassOutlined: 'clock',
  SafetyCertificateOutlined: 'shield',
  DisconnectOutlined: 'alert',
  InfoCircleOutlined: 'info',
};

/**
 * @param {HTMLElement} pageEl
 * @param {object} ctx
 * @param {{title:string, sub:string, items:Array<{group,name,icon,title,body}>,
 *          kicker:string}} def
 */
export function renderDocPage(pageEl, ctx, def) {
  const wrap = h('div.doc-page');

  wrap.appendChild(h('div.page-head', [
    h('div.ph-text', [
      h('div.page-title', { text: def.title }),
      h('div.page-sub', { text: def.sub }),
    ]),
  ]));

  const grid = h('div.doc-grid');
  const leftCard = h('div.card.doc-left');
  const leftScroll = h('div.doc-left-scroll');
  const leftFlow = h('div.doc-left-flow');

  // ---------------------------------------------------------------- 左栏按钮
  const buttons = [];
  let lastGroup = null;
  def.items.forEach((it, idx) => {
    if (it.group !== lastGroup) {
      lastGroup = it.group;
      leftFlow.appendChild(h('div.doc-group', { text: '—— ' + it.group + ' ——' }));
    }
    const btn = h('button.btn.doc-btn', {
      type: 'button',
      dataset: { idx: String(idx) },
      onclick: () => select(idx),
    }, [icon(ICON_MAP[it.icon] || 'info'), h('span', { text: it.name })]);
    buttons.push(btn);
    leftFlow.appendChild(btn);
  });

  leftScroll.appendChild(leftFlow);
  leftCard.appendChild(leftScroll);

  // ---------------------------------------------------------------- 右侧说明
  const docTitle = h('div.doc-title');
  const docDivider = h('div.doc-divider');
  const docBody = h('div.doc-body');
  const docInner = h('div.doc-right-inner', [docTitle, docDivider, docBody]);
  const rightCard = h('div.card.doc-right', [docInner]);

  grid.appendChild(leftCard);
  grid.appendChild(rightCard);
  wrap.appendChild(grid);
  pageEl.appendChild(wrap);

  const gr = (ctx && ctx.paths && ctx.paths.gr) || '游戏根目录';

  /** 正文里的 {_gr} 占位符换成运行时的游戏根目录（旧实现是编译期拼接）。 */
  function bodyOf(text) {
    return String(text || '').replace(/\{_gr\}/g, gr);
  }

  function select(i) {
    const it = def.items[i];
    if (!it) return;
    for (let k = 0; k < buttons.length; k++) buttons[k].classList.toggle('active', k === i);
    // 只改右侧文字，不重建整页 → 左栏 scrollTop 保持
    docTitle.textContent = it.title;
    docBody.textContent = bodyOf(it.body);
    docBody.scrollTop = 0;   // 正文换条目时回到顶部（左栏滚动位置不受影响）
  }

  // 默认第一条
  select(0);

  return null;
}
