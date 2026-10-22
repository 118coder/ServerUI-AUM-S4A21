/* ==========================================================================
   ui/contextmenu.js — 主题化右键菜单（替代系统菜单，§6.5）
   打开 / 键盘 ↑↓ + Enter / Esc 关闭 / 点击外部关闭；不出现任何系统菜单。
   ========================================================================== */

import { h, icon } from './dom.js';

let current = null;

/** 关闭当前菜单（没有则忽略）。 */
export function closeContextMenu() {
  if (current) current.close();
}

/**
 * @param {{x:number, y:number,
 *          items:Array<{text?:string, icon?:string, value?:string, danger?:boolean,
 *                       disabled?:boolean, separator?:boolean, note?:string}>,
 *          onSelect?:(value:string)=>void}} opt
 * @returns {{el:HTMLElement, close:Function}}
 */
export function openContextMenu(opt) {
  closeContextMenu();
  const o = opt || {};
  const items = (o.items || []).filter(Boolean);

  const menu = h('div.menu.ctx-menu', { role: 'menu', tabindex: '-1', 'aria-label': '行操作菜单' });
  const rows = [];       // [{ row, item }]
  let index = -1;

  function setActive(i) {
    index = i;
    rows.forEach((r, k) => r.row.classList.toggle('hot', k === i));
  }

  function moveActive(delta) {
    if (rows.length === 0) return;
    let i = index;
    for (let step = 0; step < rows.length; step++) {
      i = (i + delta + rows.length) % rows.length;
      if (!rows[i].item.disabled) { setActive(i); return; }
    }
  }

  function activate(value) {
    close();
    if (typeof o.onSelect === 'function') o.onSelect(value);
  }

  let built = 0;
  for (const it of items) {
    if (it.separator) { menu.appendChild(h('div.menu-sep')); continue; }
    const row = h('div.menu-item' + (it.danger ? '.danger' : '') + (it.disabled ? '.disabled' : ''), {
      role: 'menuitem',
      tabindex: '-1',
      'aria-disabled': it.disabled ? 'true' : 'false',
      onclick: (ev) => { ev.stopPropagation(); if (!it.disabled) activate(it.value); },
      onmouseenter: () => setActive(built),
    }, [it.icon ? icon(it.icon) : null, h('span.mi-text', { text: it.text || '' })]);
    rows.push({ row, item: it });
    menu.appendChild(row);
    built++;
  }

  document.body.appendChild(menu);

  // 定位（越界时向内收，保证整块可见）
  const rect = menu.getBoundingClientRect();
  const x = Math.max(4, Math.min(o.x || 0, window.innerWidth - rect.width - 6));
  const y = Math.max(4, Math.min(o.y || 0, window.innerHeight - rect.height - 6));
  menu.style.left = Math.round(x) + 'px';
  menu.style.top = Math.round(y) + 'px';

  function onKey(ev) {
    if (ev.key === 'Escape') { ev.preventDefault(); ev.stopPropagation(); close(); return; }
    if (ev.key === 'ArrowDown') { ev.preventDefault(); ev.stopPropagation(); moveActive(1); return; }
    if (ev.key === 'ArrowUp') { ev.preventDefault(); ev.stopPropagation(); moveActive(-1); return; }
    if (ev.key === 'Home') { ev.preventDefault(); setActive(0); return; }
    if (ev.key === 'End') { ev.preventDefault(); setActive(rows.length - 1); return; }
    if (ev.key === 'Enter' || ev.key === ' ') {
      ev.preventDefault(); ev.stopPropagation();
      const r = rows[index];
      if (r && !r.item.disabled) activate(r.item.value);
      return;
    }
    if (ev.key === 'Tab') { ev.preventDefault(); close(); }
  }

  function onDown(ev) {
    if (!menu.contains(ev.target)) close();
  }
  function onScroll() { close(); }

  document.addEventListener('keydown', onKey, true);
  document.addEventListener('mousedown', onDown, true);
  window.addEventListener('blur', close);
  window.addEventListener('resize', close);
  window.addEventListener('wheel', onScroll, { passive: true });

  let closed = false;
  function close() {
    if (closed) return;
    closed = true;
    document.removeEventListener('keydown', onKey, true);
    document.removeEventListener('mousedown', onDown, true);
    window.removeEventListener('blur', close);
    window.removeEventListener('resize', close);
    window.removeEventListener('wheel', onScroll);
    if (menu.parentNode) menu.parentNode.removeChild(menu);
    if (current && current.el === menu) current = null;
  }

  current = { el: menu, close };
  try { menu.focus(); } catch { }
  // 默认高亮第一个可用项，键盘一进来就能用
  const first = rows.findIndex((r) => !r.item.disabled);
  if (first >= 0) setActive(first);

  return { el: menu, close };
}
