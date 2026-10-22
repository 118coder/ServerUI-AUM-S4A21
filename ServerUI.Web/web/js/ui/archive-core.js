/* ==========================================================================
   ui/archive-core.js — 存档面板共享内核（完整页 / 经典模式 / 极简模式共用）
   - 数据与动作只在这里实现一次：列表、排序、储存/导入/导出/撤销/切换/重命名/拖拽换挡
   - 视图层只负责摆位置：createArchiveTable / createArchiveDrop / createArchiveInfo
   - 服务端运行中拦截、blocked 弹窗、toast、日志文案与 Phase 2 完整页保持一致
   ========================================================================== */

import { h, icon } from './dom.js';
import { createSwitch } from './switch.js';
import { promptText } from './prompt.js';
import { openModal } from './modal.js';
import { openFileBrowser } from './filebrowser.js';
import { openContextMenu } from './contextmenu.js';
import { toast } from './toast.js';
import { call } from '../bridge.js';

const BAD_NAME = /[\\/:*?"<>|]/;

export function pad2(n) { return n < 10 ? '0' + n : String(n); }

/** 旧 UI 的默认存档名：MMdd_HHmm。 */
export function stamp() {
  const d = new Date();
  return pad2(d.getMonth() + 1) + pad2(d.getDate()) + '_' + pad2(d.getHours()) + pad2(d.getMinutes());
}

export function baseName(p) { return String(p || '').split(/[\\/]/).pop(); }

// ---------------------------------------------------------------- 活动视图登记
// 三个存档视图同一时间只会挂载一个；拖入推送据此路由（app.js 调用 archiveDrop）。
let activeSurface = null;

export function setActiveArchiveSurface(surface) { activeSurface = surface; }

/** 宿主 drop 推送：由当前存档视图处理，没有活动视图时返回 false。 */
export function archiveDrop(path) {
  if (!activeSurface || !activeSurface.alive()) { activeSurface = null; return false; }
  activeSurface.drop(path);
  return true;
}

/** archive.changed 推送：仅当某个存档视图仍挂载才刷新。 */
export function refreshArchiveIfActive() {
  if (!activeSurface || !activeSurface.alive()) { activeSurface = null; return false; }
  activeSurface.refresh();
  return true;
}

// ---------------------------------------------------------------- 数据与动作
/**
 * @param {object} ctx app.js 提供的渲染上下文
 * @returns 存档数据/动作内核（视图通过 subscribe 跟随）
 */
export function createArchiveStore(ctx) {
  const log = ctx.log;
  const say = (level, text) => { if (log && log.append) log.append(level, text); };

  const listeners = new Set();
  const s = {
    entries: [],
    ascending: true,
    current: '',
    backupCount: 0,
    currentName: '',
    selected: '',
    busy: false,
  };

  function emit() {
    for (const fn of Array.from(listeners)) { try { fn(s); } catch { } }
  }

  function subscribe(fn) {
    listeners.add(fn);
    try { fn(s); } catch { }
    return () => listeners.delete(fn);
  }

  function serverRunning() {
    const st = ctx.status || {};
    return !!st.bat || !!st.dfo;
  }

  /** 旧 DoArchiveOp 的前端前置检查（宿主侧还有第二道拦截）。 */
  function blockedByServer() {
    if (!serverRunning()) return false;
    say('warning', '>>> [存档管理] 服务端运行中，操作已阻止');
    toast('warning', '服务端正在运行，请结束服务端后再使用存档管理功能');
    return true;
  }

  function applyList(res) {
    s.entries = (res && res.entries) || [];
    s.ascending = !!(res && res.ascending);
    s.current = (res && res.current) || '';
    s.backupCount = (res && typeof res.backupCount === 'number') ? res.backupCount : 0;
    s.currentName = (s.entries.find((e) => e.current) || {}).name || '';
    if (s.selected && !s.entries.some((e) => e.name === s.selected)) s.selected = '';
    emit();
  }

  async function refresh() {
    let res = null;
    try { res = await call('archive.list', {}); }
    catch (err) { toast('error', '读取存档列表失败：' + String((err && err.message) || err)); return null; }
    if (!res || res.ok === false) {
      toast('error', '读取存档列表失败：' + ((res && res.error) || '未知错误'));
      return null;
    }
    applyList(res);
    return res;
  }

  let busyFlag = false;
  async function withBusy(fn) {
    if (busyFlag) return;
    busyFlag = true;
    s.busy = true;
    emit();
    try { await fn(); }
    finally {
      busyFlag = false;
      s.busy = false;
      emit();
    }
  }

  /**
   * 统一处理宿主回包：ok / blocked / cancelled / error。
   * blocked（服务端运行中）由前端弹主题化提示（宿主侧非阻塞）。
   */
  async function settle(res, okText) {
    if (!res) { toast('error', '操作失败：宿主无响应'); return false; }
    if (res.ok) {
      if (okText) toast('success', okText);
      await refresh();
      return true;
    }
    if (res.blocked) {
      say('warning', '>>> [存档管理] 服务端运行中，操作已阻止');
      await openModal({
        title: '服务端运行中',
        text: res.error || '目前服务端正在运行，请结束服务端后再使用存档管理相关功能。',
        level: 'warning',
        buttons: [{ text: '知道了', value: 'ok', primary: true }],
      });
      return false;
    }
    if (res.cancelled) { say('secondary', '>>> [存档] 已取消（用户选择“取消”）'); return false; }
    toast('error', res.error || '操作失败');
    return false;
  }

  function select(name) {
    s.selected = name || '';
    emit();
  }

  function showRowMenu(name, x, y) {
    openContextMenu({
      x, y,
      items: [
        { text: '切换存档', icon: 'refresh', value: 'switch' },
        { text: '重命名存档', icon: 'edit', value: 'rename' },
      ],
      onSelect: (value) => {
        if (value === 'switch') doSwitch(name);
        else if (value === 'rename') doRename(name);
      },
    });
  }

  async function doStore() {
    if (blockedByServer()) return;
    const r = await promptText('储存当前存档', '存档名称（保存到「切换库」）：', stamp(), '储存');
    if (r.value !== 'ok') return;
    const name = (r.values[0] || '').trim();
    if (!name) return;
    if (BAD_NAME.test(name)) {
      toast('warning', '存档名称包含不允许的字符（\\ / : * ? " < > | 等），请换个名称');
      return;
    }
    await withBusy(async () => {
      let res = null;
      try { res = await call('archive.store', { name }); }
      catch (err) { res = { ok: false, error: String((err && err.message) || err) }; }
      await settle(res, '已储存到切换库: ' + name);
    });
  }

  async function doSwitch(name) {
    if (!name) return;
    if (blockedByServer()) return;
    await withBusy(async () => {
      let res = null;
      try { res = await call('archive.switch', { name }); }
      catch (err) { res = { ok: false, error: String((err && err.message) || err) }; }
      await settle(res, '已切换到: ' + name);
    });
  }

  async function doRename(oldName) {
    const r = await promptText('重命名存档', '新的存档名称：', oldName, '重命名');
    if (r.value !== 'ok') return;
    const newName = (r.values[0] || '').trim();
    if (!newName || newName === oldName) return;
    if (BAD_NAME.test(newName)) { toast('warning', '名称包含不允许的字符（\\ / : * ? " < > | 等）'); return; }
    await withBusy(async () => {
      let res = null;
      try { res = await call('archive.rename', { oldName, newName }); }
      catch (err) { res = { ok: false, error: String((err && err.message) || err) }; }
      await settle(res, '已重命名: ' + oldName + ' → ' + newName);
    });
  }

  async function doImport() {
    if (blockedByServer()) return;
    const pick = await openFileBrowser({
      mode: 'open',
      title: '导入存档（选择 .zip 压缩包）',
      filter: 'zip',
      startPath: (ctx.paths && ctx.paths.ad) || '',
      okText: '导入此文件',
    });
    if (pick.value !== 'ok') return;
    await withBusy(async () => {
      let res = null;
      try { res = await call('archive.importZip', { path: pick.path }); }
      catch (err) { res = { ok: false, error: String((err && err.message) || err) }; }
      await settle(res, '已导入: ' + baseName(pick.path));
    });
  }

  async function doExport() {
    if (blockedByServer()) return;
    const pick = await openFileBrowser({
      mode: 'save',
      title: '导出当前存档（保存为 .zip）',
      filter: 'zip',
      startPath: (ctx.paths && ctx.paths.ad) || '',
      fileName: '存档_' + stamp() + '.zip',
      okText: '导出到此',
    });
    if (pick.value !== 'ok') return;
    await withBusy(async () => {
      let res = null;
      try { res = await call('archive.export', { path: pick.path }); }
      catch (err) { res = { ok: false, error: String((err && err.message) || err) }; }
      await settle(res, '已导出: ' + baseName(pick.path));
    });
  }

  async function doUndo() {
    if (blockedByServer()) return;
    await withBusy(async () => {
      let res = null;
      try { res = await call('archive.undo', {}); }
      catch (err) { res = { ok: false, error: String((err && err.message) || err) }; }
      if (res && res.ok && res.undone) { await settle(res, '已撤销'); return; }
      if (res && res.blocked) { await settle(res); return; }
      say('warning', '>>> 无备份');
      if (res && res.error) toast('warning', res.error);
      await refresh();
    });
  }

  async function doSort() {
    if (busyFlag) return;
    const next = !s.ascending;
    let res = null;
    try { res = await call('archive.setSort', { ascending: next }); }
    catch (err) { toast('error', String((err && err.message) || err)); return; }
    if (res && res.ok !== false) applyList(res);
  }

  async function doDrop(path) {
    if (blockedByServer()) return;
    if (!/\.db$/i.test(path)) { toast('warning', '只接受 .db 文件'); return; }
    say('secondary', '>>> 拖拽换挡: ' + baseName(path));
    await withBusy(async () => {
      let res = null;
      try { res = await call('archive.dropFile', { path }); }
      catch (err) { res = { ok: false, error: String((err && err.message) || err) }; }
      await settle(res, '拖拽换挡完成: ' + baseName(path));
    });
  }

  return {
    state: s,
    subscribe,
    refresh,
    select,
    showRowMenu,
    store: doStore,
    switchTo: doSwitch,
    rename: doRename,
    importZip: doImport,
    exportZip: doExport,
    undo: doUndo,
    sort: doSort,
    drop: doDrop,
    serverRunning,
    say,
    isBusy: () => busyFlag,
    alive: () => true,
  };
}

// ---------------------------------------------------------------- 表格
/**
 * 存档表格（variant: full | classic | mini）。
 * @returns {{el:HTMLElement, table:HTMLElement, tbody:HTMLElement}}
 */
export function createArchiveTable(store, opts) {
  const o = opts || {};
  const variant = o.variant || 'full';
  const compact = variant === 'mini';

  const thSort = compact
    ? h('th', { style: { width: '132px' }, text: '修改时间' })
    : h('th.sortable.arc-thsort', { style: { width: variant === 'full' ? '150px' : '136px' } }, [
      h('span', { text: variant === 'full' ? '修改时间 (点此排序)' : '修改时间 (点此排序)' }),
      h('span.arrow', { dataset: { bind: 'sortArrow' }, text: '↑' }),
    ]);
  if (!compact) thSort.addEventListener('click', () => store.sort());

  const tbody = h('tbody');
  const head = [];
  if (variant === 'full') head.push(h('th.mid', { style: { width: '56px' }, text: '#' }));
  head.push(h('th', {
    text: compact ? '存档名称' : (variant === 'full' ? '存档名称 (双击切换 · 右键更多)' : '存档名称 (双击切换)'),
  }));
  if (variant !== 'mini') head.push(h('th.num', { style: { width: '90px' }, text: '大小' }));
  head.push(thSort);

  const table = h('table.tbl.arc-tbl' + (compact ? '.compact' : '.v-' + variant), [
    h('thead', [h('tr', head)]),
    tbody,
  ]);
  const wrap = h('div.arc-tbl-wrap' + (compact ? '.compact' : ''), [table]);

  function renderRows(state) {
    if (!tbody.isConnected && !wrap.isConnected) return;
    while (tbody.firstChild) tbody.removeChild(tbody.firstChild);

    const entries = state.entries || [];
    if (!entries.length) {
      const colspan = variant === 'full' ? 4 : (variant === 'mini' ? 2 : 3);
      tbody.appendChild(h('tr', [h('td', { colspan: String(colspan) }, [
        h('div.arc-empty', { text: '切换库中还没有存档。点击「储存当前」把当前存档存入切换库。' }),
      ])]));
      return;
    }

    for (const e of entries) {
      const isCurrent = !!e.current || (!!state.currentName && e.name === state.currentName);
      const nameCell = h('td.arc-name', [
        h('div.arc-name-in', [
          h('span.arc-nm', { text: e.name }),
          isCurrent ? h('span.pill.ok', [h('span.dot'), h('span', { text: '使用中' })]) : null,
        ]),
      ]);
      const cells = [];
      if (variant === 'full') cells.push(h('td.mid', { text: String(e.index) }));
      cells.push(nameCell);
      if (variant !== 'mini') cells.push(h('td.num', { text: e.sizeDisplay || '' }));
      cells.push(h('td.mid', { text: e.modified || '' }));

      const tr = h('tr' + (isCurrent ? '.current' : ''), { dataset: { name: e.name } }, cells);
      tr.classList.toggle('active', !!state.selected && state.selected === e.name);

      tr.addEventListener('click', () => store.select(e.name));
      tr.addEventListener('dblclick', () => store.switchTo(e.name));
      tr.addEventListener('contextmenu', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        store.select(e.name);
        store.showRowMenu(e.name, ev.clientX, ev.clientY);
      });
      tbody.appendChild(tr);
    }

    const arrow = table.querySelector('[data-bind="sortArrow"]');
    if (arrow) arrow.textContent = state.ascending ? '↑' : '↓';
  }

  store.subscribe(renderRows);
  return { el: wrap, table, tbody };
}

// ---------------------------------------------------------------- 信息栏
/**
 * 当前 / 备份数 / 清理冗余DB / 刷新（完整页与经典模式共用）。
 * @param {object} opts
 * @param {boolean} [opts.shortCurrent] 当前存档只显示文件名（表格已有大小列）；
 *        完整「文件名 (大小)」放进 data-tip，避免窄栏被省略号截断
 */
export function createArchiveInfo(store, ctx, opts) {
  const o = opts || {};
  const elCurrent = h('span.v', { dataset: { bind: 'current' }, text: '—' });
  const elBackup = h('span.v', { dataset: { bind: 'backupCount' }, text: '0' });

  const cleanSw = createSwitch({
    label: '清理冗余DB',
    checked: false,
    onChange: (on) => {
      store.say('secondary', '>>> [清理冗余DB] ' + (on ? '已启用' : '已关闭'));
      call('archive.cleanRedundant', { on }).catch((err) => store.say('error', String((err && err.message) || err)));
    },
  });

  const btnRefresh = h('button.btn.sm', {
    type: 'button', id: o.refreshId || 'btnArcRefresh',
    onclick: () => { store.say('secondary', '>>> 刷新存档列表'); store.refresh(); },
  }, [icon('refresh'), h('span', { text: '刷新存档' })]);

  store.subscribe((s) => {
    const full = s.current || '—';
    // 宿主返回「文件名 (大小)」；经典模式只取文件名（大小在表格里），完整文本放 data-tip
    const short = full.replace(/\s*\([^()]*\)\s*$/, '').trim() || full;
    elCurrent.textContent = o.shortCurrent ? short : full;
    elCurrent.dataset.tip = full;
    elBackup.textContent = String(s.backupCount || 0);
  });

  const el = h('div.card.arc-info', [
    h('span.arc-kv', [h('span.k', { text: '当前: ' }), elCurrent]),
    h('span.arc-kv', [h('span.k', { text: '备份数: ' }), elBackup]),
    h('span.arc-spacer'),
    cleanSw.el,
    btnRefresh,
  ]);

  call('settings.get', {}).then((res) => {
    if (res && typeof res.cleanRedundantDb === 'boolean') cleanSw.set(res.cleanRedundantDb, true);
  }).catch(() => { });

  return { el, btnRefresh, cleanSw };
}

// ---------------------------------------------------------------- 拖拽区
export function createArchiveDrop(store, ctx, opts) {
  const o = opts || {};
  const zone = h('div.arc-drop', [
    h('div.arc-drop-row', [icon('database'), h('span.arc-drop-text', { text: '📁 拖拽 .db 文件到此处 = 快速替换存档 (双击 = 储存当前)' })]),
    h('div.arc-drop-note', { text: '拖拽或双击都会先检查服务端是否运行；运行中会被阻止并提示。' }),
  ]);
  if (o.dblclick !== false) zone.addEventListener('dblclick', () => store.store());
  zone.addEventListener('dragover', (ev) => { ev.preventDefault(); zone.classList.add('over'); });
  zone.addEventListener('dragenter', (ev) => { ev.preventDefault(); zone.classList.add('over'); });
  zone.addEventListener('dragleave', () => zone.classList.remove('over'));
  zone.addEventListener('drop', (ev) => {
    ev.preventDefault();
    zone.classList.remove('over');
    const f = ev.dataTransfer && ev.dataTransfer.files && ev.dataTransfer.files[0];
    if (!f) { store.say('warning', '>>> [换挡] 未取到文件路径，请直接把 .db 拖到管理器窗口'); return; }
    const p = f.path || '';
    if (p) store.drop(p);
    else store.say('warning', '>>> [换挡] 当前环境不提供拖入文件的完整路径（以宿主 drop 推送为准）');
  });
  return zone;
}
