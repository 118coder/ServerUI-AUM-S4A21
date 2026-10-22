/* ==========================================================================
   ui/filebrowser.js — 内置文件浏览器（替代 OpenFileDialog / SaveFileDialog，§1.7）
   数据来自宿主只读命令 fs.drives / fs.browse；本文件不发起任何网络请求，
   也不调用任何系统文件对话框。
   ========================================================================== */

import { h, icon, clear } from './dom.js';
import { call } from '../bridge.js';
import { createOverlay } from './overlay.js';
import { createDropdown } from './dropdown.js';
import { createSwitch } from './switch.js';

const FILTER_NOTE = {
  dll: '仅显示 .dll / .ini',
  zip: '仅显示 .zip',
  db: '仅显示 .db',
  image: '仅显示图片',
  all: '显示全部文件',
};

function joinPath(dir, name) {
  if (!dir) return name;
  const sep = dir.indexOf('\\') >= 0 ? '\\' : '/';
  return dir.endsWith('\\') || dir.endsWith('/') ? dir + name : dir + sep + name;
}

/** 当前路径属于哪个盘符（用于下拉的真实选中项）。 */
function driveOf(dir, drives) {
  const d = String(dir || '').toLowerCase();
  const hit = (drives || []).find((x) => x && x.path && d.startsWith(String(x.path).toLowerCase()));
  return hit ? hit.path : '';
}

/** 过滤器对应的默认扩展名（保存模式自动补全）。 */
const FILTER_EXT = { zip: '.zip', db: '.db', dll: '.dll', image: '.png' };

function fmtSize(n) {
  const v = Number(n) || 0;
  if (v >= 1048576) return (v / 1048576).toFixed(1) + ' MB';
  if (v >= 1024) return (v / 1024).toFixed(1) + ' KB';
  return v + ' B';
}

/**
 * 打开内置文件浏览器。
 * @param {{mode?:'open'|'save', title?:string, filter?:'dll'|'zip'|'db'|'image'|'all',
 *          startPath?:string, fileName?:string, okText?:string}} opt
 * @returns {Promise<{value:'ok'|'cancel', path?:string, fileName?:string}>}
 */
export function openFileBrowser(opt) {
  const o = opt || {};
  const mode = o.mode === 'save' ? 'save' : 'open';
  const filter = o.filter || 'all';

  return new Promise((resolve) => {
    let curDir = o.startPath || '';
    let showHidden = false;
    // 过滤开关：与传入的 filter 一致（导入 .zip 时默认开）。关掉 = 显示全部文件。
    let activeFilter = filter;
    let selected = null;          // { name, path } 选中的文件
    let lastError = '';
    let btnOk = null;             // 底部主按钮（未选中时禁用）

    let settled = false;
    function done(value, path, fileName) {
      if (settled) return;
      settled = true;
      try { driveDd.destroy(); } catch { }
      ov.close();
      resolve(value === 'ok' ? { value: 'ok', path, fileName } : { value: 'cancel' });
    }

    const ov = createOverlay({
      title: o.title || (mode === 'save' ? '选择保存位置' : '选择文件'),
      cls: 'ov-fb',
      onRequestClose: () => done('cancel'),
    });

    // ---------------- 工具条 ----------------
    const driveDd = createDropdown({ items: [], onChange: (v) => { if (v) load(v); } });
    const pathInput = h('input.fb-path', {
      type: 'text', value: curDir, autocomplete: 'off', spellcheck: 'false',
      'aria-label': '当前路径',
      onkeydown: (ev) => { if (ev.key === 'Enter') { ev.preventDefault(); load(pathInput.value.trim()); } },
    });
    const btnUp = h('button.btn.sm', {
      type: 'button', text: '上级', 'data-tip': '回到上级目录',
      onclick: () => { if (parentDir) load(parentDir); },
    });
    const btnHome = h('button.btn.sm', {
      type: 'button', text: '回到起始目录', 'data-tip': '回到打开时的目录',
      onclick: () => load(o.startPath || ''),
    });
    const hiddenSw = createSwitch({
      label: '显示隐藏项', checked: false,
      onChange: (on) => { showHidden = on; load(curDir); },
    });
    const filterSw = createSwitch({
      label: FILTER_NOTE[filter] || FILTER_NOTE.all,
      checked: filter !== 'all',
      disabled: filter === 'all',
      onChange: (on) => {
        activeFilter = on ? filter : 'all';
        load(curDir);
      },
    });

    let parentDir = null;

    ov.body.appendChild(h('div.fb-bar', [
      h('span.fb-label', { text: '盘符' }),
      driveDd.el,
      btnUp,
      pathInput,
      h('span.fb-spacer'),
      filterSw.el,
      hiddenSw.el,
    ]));

    const listHead = h('div.fb-listhead', [
      h('span.fb-h-name', { text: '名称' }),
      h('span.fb-h-type', { text: '类型' }),
      h('span.fb-h-mod', { text: '修改时间' }),
    ]);
    const listEl = h('div.fb-list', { role: 'listbox', 'aria-label': '目录与文件列表' });
    const errEl = h('div.fb-err', { style: { display: 'none' } });
    ov.body.appendChild(listHead);
    ov.body.appendChild(errEl);
    ov.body.appendChild(listEl);

    // ---------------- 保存模式的文件名 ----------------
    let nameInput = null;
    if (mode === 'save') {
      nameInput = h('input', {
        type: 'text', value: o.fileName || '', autocomplete: 'off', spellcheck: 'false',
        'aria-label': '文件名',
      });
      ov.body.appendChild(h('div.fb-save', [
        h('span.fb-label', { text: '文件名' }),
        nameInput,
      ]));
    }

    // ---------------- 底部按钮 ----------------
    const btnCancel = h('button.btn', { type: 'button', text: '取消', onclick: () => done('cancel') });
    btnOk = h('button.btn.primary', { type: 'button', text: o.okText || (mode === 'save' ? '保存' : '确定'), onclick: () => confirmPick() });
    ov.foot.appendChild(btnCancel);
    ov.foot.appendChild(btnOk);

    // ---------------- 交互 ----------------
    function selectFile(file) {
      selected = file;
      for (const row of Array.from(listEl.querySelectorAll('.fb-row'))) {
        row.classList.toggle('sel', row.dataset.path === (file && file.path));
      }
      if (mode === 'open' && file && nameInput) nameInput.value = file.name;
      syncOkState();
    }

    /** 选择模式：没选文件就禁用主按钮（旧实现的「点了才提示」体验差）。 */
    function syncOkState() {
      if (!btnOk) return;
      if (mode === 'save') { btnOk.disabled = false; return; }
      btnOk.disabled = !selected;
    }

    function confirmPick() {
      if (mode === 'save') {
        let name = (nameInput.value || '').trim();
        if (!name) { flash('请先填写文件名'); return; }
        const ext = FILTER_EXT[filter];
        if (ext && name.toLowerCase().indexOf('.') < 0) {
          name += ext;
          nameInput.value = name;
        }
        if (lastError) flash('');
        done('ok', joinPath(curDir, name), name);
        return;
      }
      if (!selected) { flash('请先选择一个文件'); return; }
      done('ok', selected.path, selected.name);
    }

    function flash(text) {
      lastError = text || '';
      errEl.textContent = text || '';
      errEl.style.display = text ? '' : 'none';
    }

    function rowNode(entry, isDir) {
      // 列与表头对齐：名称 /（大小作为类型列副信息）/ 修改时间
      const typeText = isDir ? '文件夹' : fmtSize(entry.size);
      const row = h('div.fb-row' + (isDir ? '.dir' : ''), {
        role: 'option',
        dataset: { path: entry.path },
        title: null,
        onclick: () => { if (!isDir) selectFile(entry); },
        ondblclick: () => { if (isDir) load(entry.path); else { selectFile(entry); confirmPick(); } },
      }, [
        icon(isDir ? 'folder' : 'file-text'),
        h('span.fb-name', { text: entry.name }),
        h('span.fb-meta.fb-type', { text: typeText }),
        h('span.fb-meta.fb-date', { text: entry.modified || '' }),
      ]);
      return row;
    }

    async function load(target) {
      clear(listEl);
      listEl.appendChild(h('div.fb-loading', { text: '正在读取目录…' }));
      errEl.style.display = 'none';

      let res;
      try {
        res = await call('fs.browse', { path: target || '', filter: activeFilter, showHidden });
      } catch (err) {
        res = { ok: false, error: String(err.message || err) };
      }
      if (settled) return;

      clear(listEl);

      // 盘符列表（成功/失败都可能带回来；宿主返回的是数组）
      const drives = res && Array.isArray(res.drives) ? res.drives : [];
      if (drives.length) {
        driveDd.setItems(drives.map((d) => ({ value: d.path, text: d.name })), driveOf(curDir, drives));
      }

      if (!res || res.ok === false) {
        pathInput.value = (res && res.path) || target || '';
        flash((res && res.error) || '无法读取该目录');
        listEl.appendChild(h('div.fb-empty', { text: '该目录无法访问（权限不足或不存在），可点击「上级」或选择盘符。' }));
        parentDir = null;
        btnUp.disabled = true;
        return;
      }

      curDir = res.path;
      parentDir = res.parent || null;
      pathInput.value = curDir;
      if (drives.length) driveDd.setItems(drives.map((d) => ({ value: d.path, text: d.name })), driveOf(curDir, drives));
      btnUp.disabled = !parentDir;

      const dirs = res.dirs || [];
      const files = res.files || [];
      if (!dirs.length && !files.length) {
        listEl.appendChild(h('div.fb-empty', { text: '此目录下没有任何匹配的项目。' }));
      } else {
        for (const d of dirs) listEl.appendChild(rowNode(d, true));
        for (const f of files) listEl.appendChild(rowNode(f, false));
      }

      // 保存模式：文件名框保持用户输入
      if (mode === 'save' && nameInput && !nameInput.value) nameInput.value = o.fileName || '';
      if (mode === 'open') selectFile(null);
    }

    syncOkState();
    load(curDir).then(() => {
      syncOkState();
      if (nameInput) { nameInput.focus(); try { nameInput.setSelectionRange(0, nameInput.value.length); } catch { } }
      else pathInput.focus();
    });
  });
}
