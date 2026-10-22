/* ==========================================================================
   ui/overlay.js — 大尺寸主题化覆盖层（文件浏览器 / INI 分组编辑器 / 多字段输入）
   与 ui/modal.js 一致：Esc = 请求关闭，点遮罩 = 请求关闭；不使用任何原生对话框。
   约定：z-index 980 —— 低于 .modal-mask(1000)，保证内嵌的确认/输入框永远压在它上面。
   ========================================================================== */

import { h } from './dom.js';

/** 打开中的覆盖层栈（后打开的在上面）。Esc 只关最上层，避免多层面板一起关掉。 */
const stack = [];

/** 当前是否有 modal.js 的模态（或右键菜单）压在覆盖层上面 —— 此时覆盖层不抢键盘。 */
function overlayBelow() {
  return !!document.querySelector('.modal-mask') || !!document.querySelector('.ctx-menu');
}

/**
 * @param {{title?:string, cls?:string, onRequestClose?:Function, headExtra?:Node}} opt
 * @returns {{mask, panel, head, body, foot, headExtra, setTitle, close, closed}}
 */
export function createOverlay(opt) {
  const o = opt || {};
  const titleEl = h('div.ov-title', { text: o.title || '' });
  const headExtra = h('div.ov-head-extra');
  const head = h('div.ov-head', [titleEl, headExtra, h('button.icon-btn.ov-close', {
    type: 'button', 'data-tip': '关闭', 'aria-label': '关闭',
    onclick: () => requestClose(),
  }, [h('span', { text: '×' })])]);
  const body = h('div.ov-body');
  const foot = h('div.ov-foot');
  const panel = h('div.ov-panel' + (o.cls ? ' ' + o.cls : ''), [head, body, foot]);
  const mask = h('div.ov-mask', { onmousedown: (ev) => { if (ev.target === mask) requestClose(); } }, [panel]);

  let closed = false;
  let prevFocus = null;

  function requestClose() {
    if (closed) return;
    if (typeof o.onRequestClose === 'function') o.onRequestClose();
    else close();
  }

  function close() {
    if (closed) return;
    closed = true;
    const i = stack.indexOf(entry);
    if (i >= 0) stack.splice(i, 1);
    document.removeEventListener('keydown', onKey, true);
    window.removeEventListener('resize', onResize);
    if (mask.parentNode) mask.parentNode.removeChild(mask);
    if (prevFocus && prevFocus.focus) { try { prevFocus.focus(); } catch { } }
    prevFocus = null;
  }

  function onKey(ev) {
    if (ev.key !== 'Escape') return;
    if (overlayBelow()) return;              // 上面还有模态/菜单：交给它处理
    if (stack[stack.length - 1] !== entry) return;   // 不是最上层：让它先关
    ev.preventDefault();
    ev.stopPropagation();
    requestClose();
  }

  function onResize() { /* 尺寸变化由 CSS 自适应，这里只保留钩子 */ }

  const entry = { mask, requestClose, close };
  stack.push(entry);

  prevFocus = document.activeElement;
  document.addEventListener('keydown', onKey, true);
  window.addEventListener('resize', onResize);
  document.body.appendChild(mask);

  return {
    mask, panel, head, body, foot, headExtra,
    setTitle: (t) => { titleEl.textContent = t || ''; },
    requestClose,
    close,
    get closed() { return closed; },
  };
}
