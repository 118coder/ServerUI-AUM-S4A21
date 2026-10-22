/* ==========================================================================
   ui/datetimepicker.js — 自绘日期时间选择器（替代 <input type="date"> 原生弹层）
   结构：只读输入框（显示 yyyy-MM-dd HH:mm:ss）+ 日历图标按钮；
         点击任一处打开主题化浮层：月视图日历 + 时/分/秒 三段输入 + 今天/确定/取消。
   禁止：input[type=date] / input[type=datetime-local] / 任何原生弹层。
   ========================================================================== */

import { h, icon } from './dom.js';

const WD = ['一', '二', '三', '四', '五', '六', '日'];

function pad2(n) { return n < 10 ? '0' + n : String(n); }
function fmt(d) {
  return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate())
    + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
}

/** 解析 yyyy-MM-dd HH:mm:ss（也接受 yyyy-MM-dd）；非法返回 null。 */
export function parseDateTime(text) {
  const m = /^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?$/.exec(String(text || '').trim());
  if (!m) return null;
  const [_, y, mo, d, hh, mi, ss] = m;
  const dt = new Date(Number(y), Number(mo) - 1, Number(d),
    Number(hh || 0), Number(mi || 0), Number(ss || 0));
  // 反查校验（防止 2026-02-31 这类溢出）
  if (dt.getFullYear() !== Number(y) || dt.getMonth() !== Number(mo) - 1 || dt.getDate() !== Number(d)) return null;
  if (dt.getHours() !== Number(hh || 0) || dt.getMinutes() !== Number(mi || 0) || dt.getSeconds() !== Number(ss || 0)) return null;
  return dt;
}

/**
 * @param {{value?:Date, onChange?:(d:Date)=>void}} opt
 * @returns {{el:HTMLElement, get:()=>Date, set:(d:Date)=>void, text:()=>string, close:()=>void}}
 */
export function createDateTimePicker(opt) {
  const o = opt || {};
  let value = o.value instanceof Date ? new Date(o.value.getTime()) : new Date();

  const input = h('input.dtp-input', {
    type: 'text',
    value: fmt(value),
    autocomplete: 'off',
    spellcheck: 'false',
    'aria-label': '目标时间',
    onkeydown: (ev) => {
      if (ev.key === 'Enter') {
        ev.preventDefault();
        commitText();
      } else if (ev.key === 'Escape') {
        close();
      }
    },
    onblur: () => { /* 失焦不强制提交，避免打断输入 */ },
  });

  const btn = h('button.dtp-btn', {
    type: 'button', 'data-tip': '打开日历选择时间', 'aria-label': '打开日历',
    onclick: () => toggle(),
  }, [icon('calendar')]);

  const el = h('div.dtp', [input, btn]);
  let panel = null;
  let viewY = value.getFullYear();
  let viewM = value.getMonth();
  let picked = new Date(value.getTime());

  function paintInput() {
    input.value = fmt(value);
    input.classList.remove('invalid');
  }

  function set(d, silent) {
    value = new Date(d.getTime());
    paintInput();
    if (!silent && o.onChange) o.onChange(value);
  }

  function text() { return fmt(value); }

  /** 手输提交：非法则标红并 toast 由调用方处理（这里只返回 false）。 */
  function commitText() {
    const d = parseDateTime(input.value);
    if (!d) { input.classList.add('invalid'); return false; }
    set(d);
    return true;
  }

  // ---------------------------------------------------------------- 浮层
  function buildPanel() {
    const monthLabel = h('div.dtp-month');
    const grid = h('div.dtp-grid');
    const hourIn = h('input.dtp-num', { type: 'text', inputmode: 'numeric', value: pad2(picked.getHours()), 'aria-label': '时' });
    const minIn = h('input.dtp-num', { type: 'text', inputmode: 'numeric', value: pad2(picked.getMinutes()), 'aria-label': '分' });
    const secIn = h('input.dtp-num', { type: 'text', inputmode: 'numeric', value: pad2(picked.getSeconds()), 'aria-label': '秒' });

    function clampNum(inp, max) {
      const v = String(inp.value || '').replace(/[^\d]/g, '');
      let n = v === '' ? 0 : Number(v);
      if (!isFinite(n)) n = 0;
      if (n > max) n = max;
      inp.value = pad2(n);
      return n;
    }

    const body = h('div.dtp-body');
    const head = h('div.dtp-head', [
      h('button.icon-btn.dtp-nav', {
        type: 'button', 'aria-label': '上个月', 'data-tip': '上个月',
        onclick: () => { viewM--; if (viewM < 0) { viewM = 11; viewY--; } renderGrid(); },
      }, [h('span', { text: '‹' })]),
      monthLabel,
      h('button.icon-btn.dtp-nav', {
        type: 'button', 'aria-label': '下个月', 'data-tip': '下个月',
        onclick: () => { viewM++; if (viewM > 11) { viewM = 0; viewY++; } renderGrid(); },
      }, [h('span', { text: '›' })]),
    ]);

    const timeRow = h('div.dtp-time', [
      h('span.dtp-time-label', { text: '时间' }),
      hourIn, h('span.dtp-colon', { text: ':' }),
      minIn, h('span.dtp-colon', { text: ':' }),
      secIn,
      h('span.dtp-spacer'),
      h('button.btn.sm.dtp-today', {
        type: 'button', text: '今天',
        onclick: () => {
          const n = new Date();
          picked = n;
          viewY = n.getFullYear(); viewM = n.getMonth();
          hourIn.value = pad2(n.getHours()); minIn.value = pad2(n.getMinutes()); secIn.value = pad2(n.getSeconds());
          renderGrid();
        },
      }),
    ]);

    const foot = h('div.dtp-foot', [
      h('button.btn.sm', { type: 'button', text: '取消', onclick: () => close() }),
      h('button.btn.sm.primary', {
        type: 'button', text: '确定',
        onclick: () => {
          const d = new Date(picked.getFullYear(), picked.getMonth(), picked.getDate(),
            clampNum(hourIn, 23), clampNum(minIn, 59), clampNum(secIn, 59));
          set(d);
          close();
        },
      }),
    ]);

    body.appendChild(head);
    body.appendChild(grid);
    body.appendChild(timeRow);
    body.appendChild(foot);
    const p = h('div.dtp-panel', [body]);

    // 点到浮层外关闭（capture，避免被其他组件吞掉）
    const onDown = (ev) => {
      if (p.contains(ev.target) || el.contains(ev.target)) return;
      close();
    };
    const onKey = (ev) => {
      if (ev.key === 'Escape') { ev.preventDefault(); ev.stopPropagation(); close(); }
    };
    setTimeout(() => {
      document.addEventListener('mousedown', onDown, true);
      document.addEventListener('keydown', onKey, true);
    }, 0);

    p.__cleanup = () => {
      document.removeEventListener('mousedown', onDown, true);
      document.removeEventListener('keydown', onKey, true);
    };

    return { panel: p, monthLabel, grid, hourIn, minIn, secIn, renderGrid: null };
  }

  let refs = null;

  function renderGrid() {
    if (!refs) return;
    const { monthLabel, grid } = refs;
    monthLabel.textContent = viewY + ' 年 ' + (viewM + 1) + ' 月';

    while (grid.firstChild) grid.removeChild(grid.firstChild);
    for (const w of WD) grid.appendChild(h('span.dtp-wd', { text: w }));

    const first = new Date(viewY, viewM, 1);
    // 周一为一周起点
    const lead = (first.getDay() + 6) % 7;
    const daysInMonth = new Date(viewY, viewM + 1, 0).getDate();
    const prevDays = new Date(viewY, viewM, 0).getDate();

    const cells = [];
    for (let i = lead - 1; i >= 0; i--) cells.push({ d: prevDays - i, other: true, date: new Date(viewY, viewM - 1, prevDays - i) });
    for (let d = 1; d <= daysInMonth; d++) cells.push({ d, other: false, date: new Date(viewY, viewM, d) });
    while (cells.length % 7 !== 0) {
      const d = cells.length - (lead + daysInMonth) + 1;
      cells.push({ d, other: true, date: new Date(viewY, viewM + 1, d) });
    }

    const today = new Date();
    for (const c of cells) {
      const isSel = c.date.getFullYear() === picked.getFullYear()
        && c.date.getMonth() === picked.getMonth()
        && c.date.getDate() === picked.getDate();
      const isToday = c.date.getFullYear() === today.getFullYear()
        && c.date.getMonth() === today.getMonth()
        && c.date.getDate() === today.getDate();
      const cell = h('button.dtp-day' + (c.other ? '.other' : '') + (isSel ? '.sel' : '') + (isToday ? '.today' : ''), {
        type: 'button', text: String(c.d),
        onclick: () => { picked = c.date; renderGrid(); },
      });
      grid.appendChild(cell);
    }
  }

  function open() {
    if (panel) return;
    refs = buildPanel();
    panel = refs.panel;
    refs.renderGrid = renderGrid;
    // 打开时以当前 value 初始化
    picked = new Date(value.getTime());
    viewY = picked.getFullYear();
    viewM = picked.getMonth();
    refs.hourIn.value = pad2(picked.getHours());
    refs.minIn.value = pad2(picked.getMinutes());
    refs.secIn.value = pad2(picked.getSeconds());
    renderGrid();
    el.appendChild(panel);
    el.classList.add('open');
  }

  function close() {
    if (!panel) return;
    try { panel.__cleanup(); } catch { }
    if (panel.parentNode) panel.parentNode.removeChild(panel);
    panel = null;
    refs = null;
    el.classList.remove('open');
  }

  function toggle() { panel ? close() : open(); }

  el.addEventListener('click', (ev) => {
    // 点输入框也打开日历（只读展示，避免键盘输入和浮层抢焦点）
    if (ev.target === input) { ev.preventDefault(); open(); }
  });

  paintInput();
  return { el, get: () => new Date(value.getTime()), set, text, close, input, commitText };
}
