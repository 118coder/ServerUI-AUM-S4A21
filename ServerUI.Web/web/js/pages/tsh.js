/* ==========================================================================
   pages/tsh.js — 疑难杂症解惑页（Phase 3a）
   文案来自 data/tsh.js（原样搬自旧 MainForm.Troubleshoot.cs 的 items 数组）。
   ========================================================================== */

import { renderDocPage } from './docpage.js';
import { TSH_ITEMS } from '../data/tsh.js';

const SUB = '按分类查找常见问题与解决办法 · 回档 / 闪退 / 断联 / 环境兼容';

export function renderTsh(pageEl, ctx) {
  return renderDocPage(pageEl, ctx, {
    title: '疑难杂症解惑',
    sub: SUB,
    items: TSH_ITEMS,
  });
}
