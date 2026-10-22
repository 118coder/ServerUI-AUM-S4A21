/* ==========================================================================
   ui/segmented.js — 分段控件（选中 --surface-3，再点已选项取消，§6.5/§6.3）
   ========================================================================== */

import { h, icon } from './dom.js';

/**
 * @param {{items:[{value,text,icon?}], value?:string, allowEmpty?:boolean,
 *          onChange?:(value|null)=>void}} opt
 * @returns {{el:HTMLElement, get:()=>string|null, set:(v:string|null, silent?:boolean)=>void}}
 */
export function createSegmented(opt) {
  const o = opt || {};
  let value = o.value || null;

  const el = h('div.segmented', { role: 'radiogroup' });
  const items = [];
  for (const it of o.items || []) {
    const btn = h('button.seg', {
      type: 'button',
      role: 'radio',
      onclick: () => {
        // 与旧逻辑一致：再点已选项 = 取消
        const next = (value === it.value && o.allowEmpty !== false) ? null : it.value;
        set(next);
      },
    }, [
      it.icon ? icon(it.icon) : null,
      h('span', { text: it.text }),
    ]);
    btn.dataset.value = it.value;
    items.push(btn);
    el.appendChild(btn);
  }

  function set(v, silent) {
    value = v;
    for (const btn of items) btn.classList.toggle('active', btn.dataset.value === value);
    if (!silent && o.onChange) o.onChange(value);
  }

  set(value, true);
  return { el, set, get: () => value };
}
