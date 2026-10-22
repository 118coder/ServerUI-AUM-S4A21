/* ==========================================================================
   pages/placeholder.js — Phase 1 未实现页面的主题化占位（不留空白，§6.5 空态）
   这些页面属于 Phase 2/3 范围，先给出标题 + 说明 + 空态。
   ========================================================================== */

import { h, icon } from '../ui/dom.js';
import { emptyState } from '../ui/field.js';

/**
 * @param {{title:string, sub:string, note:string, phase:string}} def
 */
export function renderPlaceholder(def) {
  return function (pageEl) {
    const frag = document.createDocumentFragment();
    frag.appendChild(h('div.page-head', [
      h('div.ph-text', [
        h('div.page-title', { text: def.title }),
        h('div.page-sub', { text: def.sub }),
      ]),
      h('span.pill', [icon('clock'), h('span', { text: def.phase })]),
    ]));
    frag.appendChild(h('div.card', [
      emptyState({
        icon: def.icon || 'info',
        title: def.note,
        sub: '该页面属于 ' + def.phase + ' 范围，本轮（Phase 1）只交付外壳与「开始」页；'
          + '当前导航项可正常切换、无动画，页面内不会有任何原生控件或联网行为。',
      }),
    ]));
    pageEl.appendChild(frag);
    return null;
  };
}
