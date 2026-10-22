/* ==========================================================================
   ui/modal.js — 主题化模态（替代系统 MessageBox / 输入框，§6.5）
   Esc=取消，Enter=确认；焦点陷阱。
   ========================================================================== */

import { h } from './dom.js';

let active = null;   // { mask, buttons, resolve }
let prevFocus = null;
let finish = null;

function onKey(ev) {
  if (!active) return;
  const { mask, buttons, resolve } = active;
  if (ev.key === 'Escape') {
    ev.preventDefault();
    ev.stopPropagation();
    const cancelBtn = buttons.find(b => b.value === 'cancel' || b.value === 'no') || buttons[buttons.length - 1];
    finish(cancelBtn ? cancelBtn.value : null);
    return;
  }
  if (ev.key === 'Enter' && !ev.shiftKey && ev.target.tagName !== 'TEXTAREA') {
    ev.preventDefault();
    const okBtn = buttons.find(b => b.primary);
    if (okBtn) finish(okBtn.value);
    return;
  }
  // 简单焦点陷阱：Tab 循环
  if (ev.key === 'Tab') {
    const focusables = mask.querySelectorAll('button, input, textarea, [tabindex]');
    if (!focusables.length) return;
    const first = focusables[0], last = focusables[focusables.length - 1];
    if (ev.shiftKey && document.activeElement === first) { ev.preventDefault(); last.focus(); }
    else if (!ev.shiftKey && document.activeElement === last) { ev.preventDefault(); first.focus(); }
  }
}

/**
 * 打开模态。
 * @param {{title:string, text?:string, level?:'info'|'warning'|'bad',
 *          body?:Node, buttons?:[{text,value,primary,kind}], input?:{value,placeholder,multiline}}}
 * @returns {Promise<{value:string, input:string}>}
 */
export function openModal(opt) {
  return new Promise((resolve) => {
    const buttons = opt.buttons && opt.buttons.length
      ? opt.buttons
      : [{ text: '确定', value: 'ok', primary: true }];
    active = { mask: null, buttons, resolve: null };

    const foot = h('div.m-foot');
    for (const b of buttons) {
      foot.appendChild(h('button.btn' + (b.kind ? ' ' + b.kind : ''), {
        text: b.text,
        type: 'button',
        onclick: () => finish(b.value),
      }));
    }

    const panel = h('div.modal', [
      h('div.m-title ' + (opt.level === 'bad' ? 'bad' : opt.level === 'warning' ? 'warn' : ''), { text: opt.title || '' }),
      opt.text ? h('div.m-text', { text: opt.text }) : null,
      opt.body ? h('div.m-body', [opt.body]) : null,
      opt.input ? h('div.m-body', [
        opt.input.multiline
          ? h('textarea', { rows: 5, placeholder: opt.input.placeholder || '', text: opt.input.value || '' })
          : h('input', { type: 'text', placeholder: opt.input.placeholder || '', value: opt.input.value || '' }),
      ]) : null,
      foot,
    ]);

    const mask = h('div.modal-mask', [panel]);
    prevFocus = document.activeElement;
    active.mask = mask;
    active.resolve = resolve;

    finish = (value) => {
      let input = null;
      const ta = panel.querySelector('textarea, input');
      if (ta) input = ta.value;
      document.removeEventListener('keydown', onKey, true);
      if (mask.parentNode) mask.parentNode.removeChild(mask);
      if (prevFocus && prevFocus.focus) { try { prevFocus.focus(); } catch { } }
      prevFocus = null;
      active = null;
      resolve({ value, input });
    };

    document.addEventListener('keydown', onKey, true);
    document.body.appendChild(mask);

    const first = mask.querySelector('button.btn');
    if (first) first.focus();
  });
}

/** 信息提示框（单按钮）。 */
export function infoModal(title, text) {
  return openModal({ title, text, level: 'warning', buttons: [{ text: '确定', value: 'ok', primary: true }] });
}

/** 确认框（确定/取消）。 */
export function confirmModal(title, text, okText, okKind) {
  return openModal({
    title, text,
    buttons: [
      { text: okText || '确定', value: 'yes', primary: true, kind: okKind || '' },
      { text: '取消', value: 'cancel' },
    ],
  });
}

/** 三键确认（是/否/取消），对应旧 YesNoCancel。 */
export function yesNoCancelModal(title, text, yesText, noText) {
  return openModal({
    title, text,
    buttons: [
      { text: yesText || '是', value: 'yes', primary: true },
      { text: noText || '否', value: 'no' },
      { text: '取消', value: 'cancel' },
    ],
  });
}
