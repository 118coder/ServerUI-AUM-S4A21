/* ==========================================================================
   ui/dom.js — 极简 DOM 构建工具（无框架）
   ========================================================================== */

/**
 * 创建元素。
 * @param {string} tag 标签名，可带 class：'div.card.flat'
 * @param {object|null} attrs 属性 / 事件（on* 为事件监听，style 支持对象）
 * @param {Array|Node|string} children 子节点（数组、节点或文本）
 */
export function h(tag, attrs, children) {
  // 兼容两种调用：h('div.card', {..}, [..]) 与 h('div.card', [..])
  if (Array.isArray(attrs) || attrs instanceof Node || typeof attrs === 'string' || typeof attrs === 'number') {
    children = attrs;
    attrs = null;
  }
  const parts = String(tag).split('.');
  const el = document.createElement(parts.shift());
  if (parts.length) el.className = parts.join(' ');

  if (attrs) {
    for (const key of Object.keys(attrs)) {
      const val = attrs[key];
      if (val === null || val === undefined || val === false) continue;
      if (key === 'class' || key === 'className') { el.className = (el.className ? el.className + ' ' : '') + val; continue; }
      if (key === 'text') { el.textContent = String(val); continue; }
      if (key === 'html') { el.innerHTML = val; continue; }
      if (key === 'style' && typeof val === 'object') { Object.assign(el.style, val); continue; }
      if (key === 'dataset' && typeof val === 'object') { Object.assign(el.dataset, val); continue; }
      if (key.slice(0, 2) === 'on' && typeof val === 'function') { el.addEventListener(key.slice(2), val); continue; }
      if (val === true) { el.setAttribute(key, ''); continue; }
      el.setAttribute(key, String(val));
    }
  }

  append(el, children);
  return el;
}

export function append(parent, children) {
  if (children === null || children === undefined || children === false) return parent;
  if (Array.isArray(children)) {
    for (const c of children) append(parent, c);
    return parent;
  }
  parent.appendChild(children instanceof Node ? children : document.createTextNode(String(children)));
  return parent;
}

/** 内联 SVG 图标（外部 sprite；同源，无网络请求）。 */
export function icon(name, cls) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  if (cls) svg.setAttribute('class', cls);
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(ns, 'use');
  use.setAttribute('href', 'assets/icons.svg#i-' + name);
  svg.appendChild(use);
  return svg;
}

export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

export function qs(sel, root) { return (root || document).querySelector(sel); }

/** 让元素获得“忙碌”外观：禁用 + 内联菊环。返回恢复函数。 */
export function busy(el, label) {
  const prevHtml = el.innerHTML;
  const prevDisabled = el.disabled;
  el.disabled = true;
  el.innerHTML = '';
  el.appendChild(h('span.spinner'));
  if (label) el.appendChild(h('span', { text: label }));
  return () => {
    el.disabled = prevDisabled;
    el.innerHTML = prevHtml;
  };
}
