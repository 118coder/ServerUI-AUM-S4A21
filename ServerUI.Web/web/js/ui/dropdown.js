/* ==========================================================================
   ui/dropdown.js — 自绘下拉选择（禁止 <select>，§6.5）
   ========================================================================== */

import { h, icon, clear } from './dom.js';

/**
 * @param {{items:[{value,text}], value?:string, placeholder?:string,
 *          onChange?:(value:string)=>void}} opt
 * @returns {{el:HTMLElement, get:()=>string, set:(v:string,silent?:boolean)=>void,
 *            setItems:(items:Array<{value,text}>, value?:string)=>void}}
 */
export function createDropdown(opt) {
  const o = opt || {};
  let items = (o.items || []).slice();
  let value = o.value || (items[0] ? items[0].value : '');

  const label = h('span.dd-label');
  const list = h('div.dd-list', { role: 'listbox' });
  const el = h('div.dropdown', { tabindex: '0' }, [
    h('div.dd-face', { onclick: () => toggle() }, [label, icon('chevron-down', 'dd-arrow')]),
    list,
  ]);

  function textOf(v) {
    const it = items.find(x => x.value === v);
    return it ? it.text : (o.placeholder || '请选择');
  }

  function paint() {
    label.textContent = textOf(value);
    for (const node of Array.from(list.children)) {
      node.classList.toggle('active', node.dataset.value === value);
    }
  }

  function buildList() {
    clear(list);
    for (const it of items) {
      const node = h('div.dd-opt', {
        role: 'option',
        text: it.text,
        onclick: (ev) => {
          ev.stopPropagation();
          set(it.value);
          close();
        },
      });
      node.dataset.value = it.value;
      list.appendChild(node);
    }
    paint();
  }

  function toggle() { el.classList.contains('open') ? close() : open(); }
  function open() { el.classList.add('open'); }
  function close() { el.classList.remove('open'); }

  function set(v, silent) {
    value = v;
    paint();
    if (!silent && o.onChange) o.onChange(value);
  }

  function setItems(next, keep) {
    items = (next || []).slice();
    if (keep !== undefined) value = keep;
    else if (!items.some(x => x.value === value)) value = items[0] ? items[0].value : '';
    buildList();
  }

  el.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') { close(); return; }
    if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); toggle(); return; }
    if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
      ev.preventDefault();
      const idx = items.findIndex(x => x.value === value);
      const next = ev.key === 'ArrowDown'
        ? Math.min(items.length - 1, idx + 1)
        : Math.max(0, idx - 1);
      if (items[next]) set(items[next].value);
    }
  });

  document.addEventListener('mousedown', onDocDown);

  function onDocDown(ev) {
    if (!el.contains(ev.target)) close();
  }

  /** 覆层关闭时必须调用，否则 document 上的监听器会随每次打开累积。 */
  function destroy() {
    document.removeEventListener('mousedown', onDocDown);
  }

  buildList();
  return { el, get: () => value, set, setItems, destroy };
}
