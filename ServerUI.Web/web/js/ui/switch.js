/* ==========================================================================
   ui/switch.js — 自绘开关（40×22，§6.5）
   ========================================================================== */

import { h } from './dom.js';

/**
 * @param {{label?:string, checked?:boolean, disabled?:boolean, onChange?:(on:boolean)=>void}} opt
 * @returns {{el:HTMLElement, set:(on:boolean, silent?:boolean)=>void, get:()=>boolean}}
 */
export function createSwitch(opt) {
  const o = opt || {};
  let on = !!o.checked;
  let disabled = !!o.disabled;

  const el = h('button.switch', { type: 'button', role: 'switch' }, [
    h('span.track', [h('span.knob')]),
    o.label ? h('span.sw-label', { text: o.label }) : null,
  ]);

  function paint() {
    el.classList.toggle('on', on);
    el.setAttribute('aria-checked', on ? 'true' : 'false');
    if (disabled) el.setAttribute('disabled', ''); else el.removeAttribute('disabled');
  }

  function set(value, silent) {
    on = !!value;
    paint();
    if (!silent && o.onChange) o.onChange(on);
  }

  el.addEventListener('click', (ev) => {
    ev.stopPropagation();
    if (disabled) return;
    set(!on);
  });

  paint();
  return {
    el,
    set,
    get: () => on,
    disable: (v) => { disabled = !!v; paint(); },
  };
}
