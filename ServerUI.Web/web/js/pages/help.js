/* ==========================================================================
   pages/help.js — 使用说明页（Phase 3a）
   文案来自 data/help.js（原样搬自旧 MainForm.Pages.cs BuildHelp 的 helps 数组）。
   ========================================================================== */

import { renderDocPage } from './docpage.js';
import { HELP_ITEMS } from '../data/help.js';

const SUB = '左侧选功能，右侧看详细说明 · 首次使用建议先看「快速开始」';

export function renderHelp(pageEl, ctx) {
  return renderDocPage(pageEl, ctx, {
    title: '使用说明',
    sub: SUB,
    items: HELP_ITEMS,
  });
}
