/* ==========================================================================
   ui/field.js — 表单行 / 空态（§6.5）
   ========================================================================== */

import { h, icon } from './dom.js';

/** 输入行：左标签 + 右输入框。 */
export function fieldRow(label, inputEl, hint) {
  return h('div.field', [
    h('label', { text: label }),
    inputEl,
    hint ? h('span.hint', { text: hint }) : null,
  ]);
}

/** 普通文本输入（禁用浏览器 autofill 外观）。 */
export function textInput(opt) {
  const o = opt || {};
  return h('input', {
    type: 'text',
    value: o.value || '',
    placeholder: o.placeholder || '',
    autocomplete: 'off',
    spellcheck: 'false',
    oninput: o.onInput,
  });
}

/** 空态：图标 + 标题 + 说明 +（可选）主按钮。 */
export function emptyState(opt) {
  const o = opt || {};
  return h('div.empty', [
    icon(o.icon || 'info'),
    h('div.e-title', { text: o.title || '暂无内容' }),
    o.sub ? h('div.e-sub', { text: o.sub }) : null,
    o.action ? h('div', { style: { marginTop: '4px' } }, [o.action]) : null,
  ]);
}
