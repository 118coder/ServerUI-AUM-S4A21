/* ==========================================================================
   router.js — 页面切换（无任何动画，§1.6）
   页面挂载到 #page，绝对定位铺满内容卡片；切换 = 同步替换 DOM。
   ========================================================================== */

let pageEl = null;
const pages = new Map();
let current = null;
let listeners = [];

export function initRouter(el) {
  pageEl = el;
}

/** 注册页面：{ id, render(container, ctx) }。 */
export function register(id, def) {
  pages.set(id, def);
}

export function currentPage() { return current; }

export function onNavigate(fn) { listeners.push(fn); }

/**
 * 切页。
 * @param {string} id
 * @param {object} ctx 渲染上下文（app 提供）
 * @param {boolean} [force] 同页强制重绘
 */
export function go(id, ctx, force) {
  if (!pageEl) return false;
  if (!pages.has(id)) return false;
  if (current === id && !force) return true;

  const def = pages.get(id);
  // 同步替换：先清空再挂载，无过渡、无渐变
  while (pageEl.firstChild) pageEl.removeChild(pageEl.firstChild);
  pageEl.scrollTop = 0;
  current = id;

  const node = def.render(pageEl, ctx);
  if (node && node.parentNode !== pageEl) pageEl.appendChild(node);

  for (const fn of listeners) { try { fn(id); } catch { } }
  return true;
}
