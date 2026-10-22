/* ==========================================================================
   ui/tooltip.js — 自绘提示（延迟 400ms，替代原生 title，§6.5）
   全局委托：任何带 data-tip 的元素都生效。
   ========================================================================== */

import { h } from './dom.js';

const DELAY = 400;
let node = null;
let timer = 0;
let current = null;

function ensure() {
  if (!node) {
    node = h('div.tooltip', { role: 'tooltip' });
    document.body.appendChild(node);
  }
  return node;
}

function place(anchor) {
  const tip = ensure();
  const a = anchor.getBoundingClientRect();
  const t = tip.getBoundingClientRect();
  let left = Math.round(a.left + a.width / 2 - t.width / 2);
  let top = Math.round(a.top - t.height - 8);
  if (top < 8) top = Math.round(a.bottom + 8);
  left = Math.max(8, Math.min(left, window.innerWidth - t.width - 8));
  tip.style.left = left + 'px';
  tip.style.top = top + 'px';
}

function show(anchor) {
  const text = anchor.getAttribute('data-tip');
  if (!text) return;
  const tip = ensure();
  tip.textContent = text;
  tip.classList.add('show');
  // 先显示再量尺寸，保证定位正确
  requestAnimationFrame(() => { if (current === anchor) place(anchor); });
  place(anchor);
}

function hide() {
  clearTimeout(timer);
  timer = 0;
  current = null;
  if (node) node.classList.remove('show');
}

export function initTooltip() {
  document.addEventListener('mouseover', (ev) => {
    const anchor = ev.target && ev.target.closest ? ev.target.closest('[data-tip]') : null;
    if (!anchor || anchor === current) return;
    hide();
    current = anchor;
    timer = setTimeout(() => { if (current === anchor) show(anchor); }, DELAY);
  });

  document.addEventListener('mouseout', (ev) => {
    const anchor = ev.target && ev.target.closest ? ev.target.closest('[data-tip]') : null;
    if (!anchor) return;
    hide();
  });

  document.addEventListener('mousedown', hide, true);
  document.addEventListener('wheel', hide, { passive: true });
  window.addEventListener('blur', hide);
  window.addEventListener('resize', hide);
}
