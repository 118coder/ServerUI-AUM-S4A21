/* ==========================================================================
   ui/prompt.js — 主题化输入框（替代 Interaction.InputBox / 多字段输入）
   与 ui/modal.js 同层级：Esc 取消、Enter 确定、点遮罩取消。
   ========================================================================== */

import { h } from './dom.js';
import { createOverlay } from './overlay.js';

/**
 * @param {{title:string, text?:string, fields:Array<{label?:string, value?:string,
 *          placeholder?:string, mono?:boolean, width?:number}>,
 *          okText?:string, danger?:boolean}} opt
 * @returns {Promise<{value:'ok'|'cancel', values:string[]}>}
 */
export function openPrompt(opt) {
  const o = opt || {};
  const fields = (o.fields && o.fields.length) ? o.fields : [{ label: '值', value: '' }];

  return new Promise((resolve) => {
    const inputs = [];
    const rows = fields.map((f) => {
      const input = h('input', {
        type: 'text',
        value: f.value || '',
        placeholder: f.placeholder || '',
        autocomplete: 'off',
        spellcheck: 'false',
      });
      if (f.mono) input.style.fontFamily = 'var(--font-mono)';
      if (f.width) input.style.width = f.width + 'px';
      inputs.push(input);
      return h('div.field.pf-field', [
        f.label ? h('label', { text: f.label }) : null,
        input,
      ]);
    });

    let settled = false;
    function done(value) {
      if (settled) return;
      settled = true;
      const values = inputs.map((i) => i.value);
      ov.close();
      resolve({ value, values });
    }

    const ov = createOverlay({
      title: o.title || '输入',
      cls: 'ov-prompt',
      onRequestClose: () => done('cancel'),
    });

    ov.body.appendChild(h('div.prompt-body', [
      o.text ? h('div.p-note', { text: o.text }) : null,
      ...rows,
    ]));

    ov.foot.appendChild(h('button.btn', { type: 'button', text: '取消', onclick: () => done('cancel') }));
    ov.foot.appendChild(h('button.btn' + (o.danger ? '.danger' : '.primary'), {
      type: 'button', text: o.okText || '确定', onclick: () => done('ok'),
    }));

    ov.panel.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter') {
        const t = ev.target;
        if (t && t.tagName === 'TEXTAREA') return;
        ev.preventDefault();
        done('ok');
      }
    });

    const first = inputs[0];
    if (first) {
      first.focus();
      try { first.setSelectionRange(0, first.value.length); } catch { }
    }
  });
}

/** 单字段文本输入（对应旧 Interaction.InputBox）。 */
export function promptText(title, text, value, okText) {
  return openPrompt({ title, text, okText, fields: [{ label: '名称', value: value || '' }] });
}
