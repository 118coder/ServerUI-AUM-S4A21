/* ==========================================================================
   ui/inieditor.js — 主题化 INI 分组编辑器（替代旧 AntdUI IniEditorForm）
   规则与旧实现一致：
     · 解析后逐行保留：注释 / 空行 / 节头 / 键的原始顺序；
     · 值是布尔写法（1/0、true/false、on/off…）→ 开关，回写时保持原写法；
       值是数字 → 数字框 + 上下微调（保留小数位数）；其余 → 文本框；
     · 文件开头、任意分组之前的键归入「(主配置)」；
     · 分组标题行可「添加键 / 删除分组」，键行行尾可「删除」；
     · 标题显示「（已修改，保存后写入）」，Esc / 关闭时对未保存修改二次确认。
   本模块只负责产生新文本，写盘由调用方通过 dll.saveConfig 完成。
   ========================================================================== */

import { h } from './dom.js';
import { createOverlay } from './overlay.js';
import { createSwitch } from './switch.js';
import { openPrompt, promptText } from './prompt.js';
import { confirmModal } from './modal.js';

const BOOL_WORDS = ['1', '0', 'true', 'false', 'on', 'off', 'yes', 'no', 'enable', 'enabled', 'disable', 'disabled'];

function isBoolText(v) { return BOOL_WORDS.indexOf(String(v).toLowerCase()) >= 0; }

function isNumText(v) {
  const s = String(v);
  if (!/^[+-]?(\d+\.?\d*|\.\d+)$/.test(s)) return null;
  const dot = s.indexOf('.');
  return { digits: dot >= 0 ? s.length - dot - 1 : 0 };
}

function boolParse(v) {
  const s = String(v).toLowerCase();
  return s === '1' || s === 'true' || s === 'yes' || s === 'on' || s === 'enable' || s === 'enabled';
}

/** 布尔回写 —— 保持原配置文件的值写法。 */
function boolToOrig(b, orig) {
  const s = String(orig).toLowerCase();
  if (s === '1' || s === '0') return b ? '1' : '0';
  if (s === 'on' || s === 'off') return b ? 'on' : 'off';
  if (s === 'yes' || s === 'no') return b ? 'yes' : 'no';
  if (s === 'enable' || s === 'disable') return b ? 'enable' : 'disable';
  if (s === 'enabled' || s === 'disabled') return b ? 'enabled' : 'disabled';
  return b ? 'true' : 'false';
}

/** 解析 ini 文本 → 行模型（保留原始行）。 */
function parseIni(text) {
  const src = String(text == null ? '' : text).replace(/\r\n?/g, '\n').split('\n');
  if (src.length && src[src.length - 1] === '') src.pop();   // 结尾换行不算一个空行
  return src.map((raw) => {
    const t = raw.trim();
    if (!t) return { kind: 'blank', raw };
    if (t.startsWith(';') || t.startsWith('#')) return { kind: 'comment', raw };
    if (t.startsWith('[') && t.endsWith(']')) return { kind: 'section', raw, name: t.slice(1, -1).trim() };
    const eq = t.indexOf('=');
    if (eq > 0) {
      const value = t.slice(eq + 1).trim();
      const num = isNumText(value);
      return {
        kind: 'key', raw,
        key: t.slice(0, eq).trim(),
        value,
        style: isBoolText(value) ? 'bool' : (num ? 'num' : 'text'),
        digits: num ? num.digits : 0,
      };
    }
    return { kind: 'other', raw };
  });
}

/**
 * 打开 INI 分组编辑器。
 * @param {{title?:string, path?:string, exists?:boolean, text?:string, encoding?:string}} opt
 * @returns {Promise<{value:'ok'|'cancel', text?:string}>}
 */
export function openIniEditor(opt) {
  const o = opt || {};
  const title = o.title || '编辑配置';
  const lines = parseIni(o.text);

  return new Promise((resolve) => {
    let dirty = false;
    let settled = false;

    function done(value, text) {
      if (settled) return;
      settled = true;
      ov.close();
      resolve(value === 'ok' ? { value: 'ok', text } : { value: 'cancel' });
    }

    const ov = createOverlay({
      title,
      cls: 'ov-ini',
      onRequestClose: () => requestCancel(),
    });

    async function requestCancel() {
      if (!dirty) { done('cancel'); return; }
      const r = await confirmModal('放弃修改？',
        '当前配置有未保存的修改，关闭后这些修改会丢失。\n确定要放弃吗？',
        '放弃修改', 'danger');
      if (r.value === 'yes') done('cancel');
    }

    function markDirty() {
      dirty = true;
      ov.setTitle(title + '（已修改，保存后写入）');
    }

    // ---------------- 头部：说明 + 添加分组 ----------------
    ov.body.appendChild(h('div.ini-tip', {
      text: '直接调节：开关 = 启用/关闭 · 数字 = 上下箭头微调 · 文本框 = 填写内容；'
        + '灰色小字是原配置注释。分组标题行可「添加键 / 删除分组」，键行行尾可「删除」；保存后自动转回 ini 格式。',
    }));
    const bodyWrap = h('div.ini-wrap');
    ov.body.appendChild(bodyWrap);

    const btnAddGroup = h('button.btn.sm', {
      type: 'button', text: '添加分组',
      onclick: () => addGroup(),
    });
    ov.headExtra.appendChild(btnAddGroup);

    // ---------------- 行控件工厂 ----------------
    function makeControl(line) {
      if (line.style === 'bool') {
        const sw = createSwitch({
          checked: boolParse(line.value),
          onChange: () => markDirty(),
        });
        line.ctl = { kind: 'bool', get: () => boolToOrig(sw.get(), line.value) };
        return sw.el;
      }
      if (line.style === 'num') {
        const input = h('input.ini-num', {
          type: 'text', value: line.value, autocomplete: 'off', spellcheck: 'false',
          oninput: () => markDirty(),
        });
        const step = (dir) => {
          const d = Number(input.value);
          if (!isFinite(d)) return;
          const next = d + dir;
          input.value = line.digits > 0 ? next.toFixed(line.digits) : String(next);
          markDirty();
        };
        const up = h('button.ini-step', { type: 'button', text: '▲', 'aria-label': '增加', onclick: () => step(1) });
        const down = h('button.ini-step', { type: 'button', text: '▼', 'aria-label': '减少', onclick: () => step(-1) });
        const box = h('div.ini-numbox', [input, h('div.ini-steps', [up, down])]);
        line.ctl = {
          kind: 'num',
          get: () => {
            const v = (input.value || '').trim();
            if (line.digits > 0 && /^[+-]?(\d+\.?\d*|\.\d+)$/.test(v)) {
              return Number(v).toFixed(line.digits);
            }
            return v;
          },
        };
        return box;
      }
      const input = h('input.ini-text', {
        type: 'text', value: line.value, autocomplete: 'off', spellcheck: 'false',
        oninput: () => markDirty(),
      });
      line.ctl = { kind: 'text', get: () => (input.value || '').trim() };
      return input;
    }

    function valueOf(line) {
      if (line.ctl) return line.ctl.get();
      return line.value;
    }

    // ---------------- 结构操作 ----------------
    function sectionRange(sec) {
      let start = -1, end = lines.length;
      for (let i = 0; i < lines.length; i++) {
        const l = lines[i];
        if (l.kind !== 'section') continue;
        if (start >= 0) { end = i; break; }
        if (l.name.toLowerCase() === String(sec).toLowerCase()) start = i;
      }
      return { start, end };
    }

    async function addGroup() {
      const r = await promptText('添加分组', '分组名称（不能包含 [ ]，且不能与已有分组重名）', '', '添加');
      if (r.value !== 'ok') return;
      const name = (r.values[0] || '').trim();
      if (!name) return;
      if (name.indexOf('[') >= 0 || name.indexOf(']') >= 0) {
        await confirmModal('添加分组', '分组名称不能包含 [ 或 ] 字符。', '知道了');
        return;
      }
      if (lines.some((l) => l.kind === 'section' && l.name.toLowerCase() === name.toLowerCase())) {
        await confirmModal('添加分组', '分组 [' + name + '] 已存在。', '知道了');
        return;
      }
      lines.push({ kind: 'section', raw: '[' + name + ']', name });
      markDirty();
      render();
    }

    async function addKey(sec) {
      const isMain = sec === null;
      const r = await openPrompt({
        title: isMain ? '添加键（主配置）' : '添加键 — 分组 [' + sec + ']',
        text: '键名不能包含 =，且分组内不可重名。',
        okText: '添加',
        fields: [
          { label: '键名', value: '', placeholder: '例如 Enable' },
          { label: '值', value: '', placeholder: '可留空' },
        ],
      });
      if (r.value !== 'ok') return;
      const key = (r.values[0] || '').trim();
      const val = (r.values[1] || '').trim();
      if (!key || key.indexOf('=') >= 0) {
        await confirmModal('添加键', '键名不能为空，且不能包含 = 字符。', '知道了');
        return;
      }

      let start, end;
      if (isMain) {
        start = -1;
        end = lines.length;
        for (let i = 0; i < lines.length; i++) {
          if (lines[i].kind === 'section') { end = i; break; }
        }
      } else {
        const rr = sectionRange(sec);
        start = rr.start; end = rr.end;
        if (start < 0) { await confirmModal('添加键', '分组 [' + sec + '] 不存在。', '知道了'); return; }
      }

      for (let i = start + 1; i < end; i++) {
        if (lines[i].kind === 'key' && lines[i].key.toLowerCase() === key.toLowerCase()) {
          await confirmModal('添加键', '该分组下已存在同名键「' + key + '」。', '知道了');
          return;
        }
      }

      let insert = end;
      for (let i = start + 1; i < end; i++) if (lines[i].kind === 'key') insert = i + 1;
      const value = val;
      const num = isNumText(value);
      lines.splice(insert, 0, {
        kind: 'key', raw: key + '=' + value, key, value,
        style: isBoolText(value) ? 'bool' : (num ? 'num' : 'text'),
        digits: num ? num.digits : 0,
      });
      markDirty();
      render();
    }

    async function removeKey(line) {
      const r = await confirmModal('删除键', '确定删除键「' + line.key + '」？', '删除', 'danger');
      if (r.value !== 'yes') return;
      const i = lines.indexOf(line);
      if (i >= 0) lines.splice(i, 1);
      markDirty();
      render();
    }

    async function removeGroup(sec) {
      const { start, end } = sectionRange(sec);
      if (start < 0) return;
      const r = await confirmModal('删除分组',
        '确定删除分组 [' + sec + ']？\n\n该分组下的键与配置将一并移除。', '删除分组', 'danger');
      if (r.value !== 'yes') return;
      lines.splice(start, end - start);
      markDirty();
      render();
    }

    // ---------------- 渲染 ----------------
    /** 结构变化前先把控件里的现值写回行模型，避免重建时丢掉用户已做的修改。 */
    function syncValues() {
      for (const l of lines) {
        if (l.kind === 'key' && l.ctl) {
          l.value = l.ctl.get();
          l.ctl = null;
        }
      }
    }

    function sectionCaption(name, opts) {
      const o2 = opts || {};
      const box = h('div.ini-sec', [
        h('span.ini-sec-name', { text: '▍ ' + name }),
        h('span.ini-sec-spacer'),
        h('button.btn.sm', { type: 'button', text: '添加键', onclick: () => addKey(o2.sec === undefined ? name : o2.sec) }),
        o2.canRemove
          ? h('button.btn.sm.danger', { type: 'button', text: '删除分组', onclick: () => removeGroup(o2.sec === undefined ? name : o2.sec) })
          : h('span.ini-sec-pad'),
      ]);
      return box;
    }

    function render() {
      syncValues();
      bodyWrap.innerHTML = '';
      const frag = document.createDocumentFragment();

      let rows = 0;

      // 第一个 [节] 之前的所有键/注释统一归入「(主配置)」。
      // 旧实现只在「遇到 key」时才补标题，若主配置里只有注释或根本没有键就不会出现分组，
      // 这里按「第一个 section 之前」判断，保证空主配置也能加键。
      const firstSection = lines.findIndex((l) => l.kind === 'section');
      const mainEnd = firstSection < 0 ? lines.length : firstSection;
      let mainCaptionAdded = false;

      function ensureMainCaption() {
        if (mainCaptionAdded) return;
        mainCaptionAdded = true;
        frag.appendChild(sectionCaption('(主配置)', { sec: null, canRemove: false }));
      }

      for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        const inMain = firstSection < 0 || i < mainEnd;

        if (line.kind === 'key') {
          if (inMain) ensureMainCaption();
          const ctl = makeControl(line);
          frag.appendChild(h('div.ini-row', [
            h('span.ini-key', { text: line.key }),
            h('span.ini-ctl', [ctl]),
            h('button.ini-del', { type: 'button', text: '删除', 'aria-label': '删除键 ' + line.key, onclick: () => removeKey(line) }),
          ]));
          rows++;
          continue;
        }
        if (line.kind === 'section') {
          frag.appendChild(sectionCaption(line.name, { canRemove: true }));
          rows++;
          continue;
        }
        if (line.kind === 'comment') {
          frag.appendChild(h('div.ini-note', { text: line.raw.trim() }));
          rows++;
          continue;
        }
        if (line.kind === 'blank') {
          frag.appendChild(h('div.ini-blank'));
          continue;
        }
        frag.appendChild(h('div.ini-note', { text: line.raw }));
        rows++;
      }

      if (rows === 0) {
        frag.appendChild(h('div.ini-empty', { text: '该配置文件当前为空。可点击右上角「添加分组」，或在下方「(主配置)」里添加键。' }));
        frag.appendChild(sectionCaption('(主配置)', { sec: null, canRemove: false }));
      }

      bodyWrap.appendChild(frag);
      updateFoot();
    }

    function buildText() {
      const out = lines.map((l) => {
        if (l.kind === 'section') return '[' + l.name + ']';
        if (l.kind === 'key') return l.key + '=' + valueOf(l);
        return l.raw;
      });
      return out.join('\r\n').replace(/(\r\n)+$/, '') + '\r\n';
    }

    // ---------------- 底部 ----------------
    const footNote = h('span.ini-path');
    const btnSave = h('button.btn.primary', { type: 'button', text: '保存', onclick: () => done('ok', buildText()) });
    const btnCancel = h('button.btn', { type: 'button', text: '取消', onclick: () => requestCancel() });

    function updateFoot() {
      footNote.textContent = '保存位置：游戏根目录 → ' + (o.path || '(未知)')
        + (o.exists ? '' : '（新文件，不存在时将自动创建）');
    }

    ov.foot.appendChild(footNote);
    ov.foot.appendChild(h('span.ini-foot-spacer'));
    ov.foot.appendChild(btnCancel);
    ov.foot.appendChild(btnSave);

    render();
  });
}
