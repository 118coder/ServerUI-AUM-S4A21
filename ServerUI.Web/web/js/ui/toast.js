/* ==========================================================================
   ui/toast.js — 右下角堆叠通知（3s 自动消失，进出 120ms）
   ========================================================================== */

import { h } from './dom.js';

let host = null;

function ensureHost() {
  if (!host) {
    host = document.getElementById('toastHost');
    if (!host) { host = h('div.toast-host', { id: 'toastHost' }); document.body.appendChild(host); }
  }
  return host;
}

/**
 * @param {'success'|'warning'|'error'|'info'} level
 * @param {string} text
 */
export function toast(level, text) {
  if (!text) return;
  const box = h('div.toast ' + (level || 'info'), { text: String(text) });
  box.style.opacity = '0';
  box.style.transform = 'translateY(6px)';
  ensureHost().appendChild(box);
  requestAnimationFrame(() => {
    box.style.transition = 'opacity 120ms linear, transform 120ms linear';
    box.style.opacity = '1';
    box.style.transform = 'translateY(0)';
  });
  setTimeout(() => {
    box.style.opacity = '0';
    box.style.transform = 'translateY(6px)';
    setTimeout(() => { if (box.parentNode) box.parentNode.removeChild(box); }, 140);
  }, 3000);
}
