/* ==========================================================================
   pages/dll.js — DLL 扩展页（Phase 2 · C）
   对照旧 MainForm.DllExt.cs：
     · 受管插件 8 行（GameNative 锁定开启、无手柄、固定首位）+ 自定义扩展分组；
     · 行首手柄拖拽调整加载顺序（受管 / 自定义互不跨组，实时换位，松开不重建）；
     · 开关 / 顺序变化 = 未应用状态，点击「应用更改」前弹主题化确认再写 GameGaurd.ini；
     · 添加扩展 = 内置文件浏览器（filter=dll）；删除扩展 = 主题化列表选择（可同时删文件）；
     · 编辑配置 = 主题化 INI 分组编辑器（dll.readConfig / dll.saveConfig）。
   Phase 3 的「新DLL安装」不在此页，本页只保留「打开补丁包目录」。
   ========================================================================== */

import { h, icon, clear } from '../ui/dom.js';
import { createSwitch } from '../ui/switch.js';
import { createOverlay } from '../ui/overlay.js';
import { confirmModal } from '../ui/modal.js';
import { openFileBrowser } from '../ui/filebrowser.js';
import { openIniEditor } from '../ui/inieditor.js';
import { toast } from '../ui/toast.js';
import { call } from '../bridge.js';

const SUB = '客户端补丁挂载 · 勾选即写入 GameGaurd.ini 的 [Plugins] 列表（顺序生效）';
const LIST_HEAD = '已安装插件（勾选 = 挂载；行首手柄图标可拖拽调整加载顺序，底部为玩家自定义扩展）';
const CUSTOM_CAP = '—— 自定义扩展（开关 = 挂载，行尾可删除）——';
const CUSTOM_EMPTY = '—— 暂无自定义扩展（点击下方【添加扩展】挂载自己的插件）——';
const UNINSTALLED_CAP = '—— 未安装 DLL 扩展 ——';
const UNINSTALLED_TIP = '游戏根目录尚未安装客户端补丁（未发现挂载器 GameGaurd.dll / az.dll，且 GameGaurd.ini 无插件记录）。\n\n'
  + '请到【设置与关于】页点击【新DLL安装】，把 客户端补丁.zip 完整安装到游戏根目录；\n安装完成后本页会自动显示插件列表。';
const OP_NOTE = '添加扩展 = 选择自己的插件挂载（复制到游戏根目录 + 写入 GameGaurd.ini）；'
  + '删除扩展 = 移除已挂载的自定义插件；自定义扩展行有开关，取消勾选后应用 = 从列表移除（文件保留）；'
  + '拖动行首的手柄图标可调整加载顺序（必选项固定首位），调整后点击应用更改';
// 1280 宽下完整说明会超出操作行：行内只放短句，全文放 data-tip（自绘 tooltip）
const OP_NOTE_SHORT = '添加/删除扩展会写入 GameGaurd.ini（文件保留）；拖动手柄调整顺序后点【应用更改】';
const DIRTY_SWITCH = '已修改（未应用）：点击【应用更改】写入 GameGaurd.ini 生效';
const DIRTY_ORDER = '顺序已调整（未应用）：点击【应用更改】写入 GameGaurd.ini';

let mounted = null;
/** 窗口失焦兜底：只注册一次，避免每次挂载 DLL 页都叠加监听。 */
let endDragHook = null;
window.addEventListener('blur', () => { if (endDragHook) endDragHook(); });

export function renderDll(pageEl, ctx) {
  const log = ctx.log;
  const wrap = h('div.dll-page');
  const frag = document.createDocumentFragment();

  let state = null;
  let dirty = false;
  let busyFlag = false;
  let dragging = null;

  const say = (level, text) => { if (log && log.append) log.append(level, text); };
  const installed = () => !!(state && state.installed);

  // ---------------------------------------------------------------- 页头
  const btnOpenPatch = h('button.btn.sm', { type: 'button', id: 'btnDllOpenPatch' }, [icon('folder'), h('span', { text: '打开补丁包目录' })]);
  const btnRefresh = h('button.btn.sm', { type: 'button', id: 'btnDllRefresh' }, [icon('refresh'), h('span', { text: '刷新状态' })]);
  btnOpenPatch.addEventListener('click', () => {
    call('shell.open', { kind: 'patchDir', value: null }).catch(err => say('error', String(err.message || err)));
  });
  btnRefresh.addEventListener('click', () => { say('secondary', '>>> [DLL扩展] 刷新状态'); refresh(true); });

  frag.appendChild(h('div.page-head', [
    h('div.ph-text', [
      h('div.page-title', { text: 'DLL 扩展' }),
      h('div.page-sub', { text: SUB }),
    ]),
    btnOpenPatch,
    btnRefresh,
  ]));

  // ---------------------------------------------------------------- 列表卡片
  const managedList = h('div.dll-group', { dataset: { group: 'managed' } });
  const customList = h('div.dll-group', { dataset: { group: 'custom' } });
  const customCap = h('div.dll-cap');
  const emptyBox = h('div.dll-empty', { style: { display: 'none' } });
  const listHead = h('div.dll-listhead', { text: LIST_HEAD });
  const listScroll = h('div.dll-scroll', [managedList, customCap, customList]);

  frag.appendChild(h('div.card.dll-card', [
    listHead,
    listScroll,
    emptyBox,
  ]));

  // ---------------------------------------------------------------- 操作行 / 状态行
  const btnApply = h('button.btn.primary', { type: 'button', id: 'btnDllApply', text: '应用更改' });
  const btnAdd = h('button.btn', { type: 'button', id: 'btnDllAdd', text: '添加扩展' });
  const btnDel = h('button.btn.danger', { type: 'button', id: 'btnDllDel', text: '删除扩展' });
  btnApply.addEventListener('click', () => doApply());
  btnAdd.addEventListener('click', () => doAdd());
  btnDel.addEventListener('click', () => doDelete());

  // 旧 UI：三个按钮 + 同一行的说明小字
  frag.appendChild(h('div.dll-ops', [
    btnApply,
    btnAdd,
    btnDel,
    h('div.dll-opnote', { text: OP_NOTE_SHORT, 'data-tip': OP_NOTE }),
  ]));

  const statusEl = h('div.dll-status', { dataset: { bind: 'status' }, text: '正在读取插件状态…' });
  frag.appendChild(statusEl);

  wrap.appendChild(frag);
  pageEl.appendChild(wrap);

  // ---------------------------------------------------------------- 状态文案
  function setStatus(text, dirtyState) {
    statusEl.textContent = text || '';
    statusEl.classList.toggle('dirty', !!dirtyState);
  }

  function markDirty(text) {
    dirty = true;
    setStatus(text, true);
  }

  // ---------------------------------------------------------------- 拖拽（Pointer Events，只从手柄发起）
  // 与旧 MainForm.DllExt.cs 同构：手柄按下 → 移动超过 4px 才进入拖拽态 → 跨过中线实时换位 →
  // 松开提交一次 dll.setOrder。不走 HTML5 DnD（真实 WebView2 里 dragstart 不会由鼠标合成事件可靠触发，
  // 且旧版明确注释「不走 OLE DoDragDrop」）。
  const DRAG_THRESHOLD = 4;
  const isGameNativeFile = (f) => /^GameNative\.dll$/i.test(String(f || ''));

  function dragRows(container) {
    return Array.from(container.children).filter((n) => n.dataset && n.dataset.file);
  }

  function orderOf(container) {
    return dragRows(container).map((n) => n.dataset.file);
  }

  async function commitOrder() {
    if (!state) return;
    const managedOrder = orderOf(managedList);
    const customOrder = orderOf(customList);
    const sameManaged = JSON.stringify(managedOrder) === JSON.stringify(state.managedOrder || []);
    const sameCustom = JSON.stringify(customOrder) === JSON.stringify(state.customOrder || []);
    if (sameManaged && sameCustom) return;   // 拖了但没换位 → 不算修改
    try {
      const res = await call('dll.setOrder', { managedOrder, customOrder });
      if (res && res.ok !== false) { state = res; }
    } catch (err) {
      say('error', String(err.message || err));
    }
    markDirty(DIRTY_ORDER);
  }

  /** 按指针 Y 与各行中线比较：返回应该插入到哪一行之前（null = 末尾）。 */
  function rowBefore(container, y, row) {
    for (const r of dragRows(container)) {
      if (r === row) continue;
      const b = r.getBoundingClientRect();
      if (y < b.top + b.height / 2) return r;
    }
    return null;
  }

  /** 实时换位：跨过中线立即插入；受管组 GameNative 固定首位，两组互不接收。 */
  function moveRow(container, y) {
    const row = dragging.row;
    const others = dragRows(container).filter((r) => r !== row);
    if (!others.length) return;

    const native = others.find((r) => isGameNativeFile(r.dataset.file));
    let before = rowBefore(container, y, row);
    // 不允许插到 GameNative 之前：夹到它后面
    if (native && before === native) before = native.nextElementSibling;
    if (before === row) before = row.nextElementSibling;

    if (before) {
      if (row.nextElementSibling !== before) container.insertBefore(row, before);
    } else if (container.lastElementChild !== row) {
      container.appendChild(row);
    }
    updateIndicator(container, y);
  }

  /** 插入线画在被拖行上沿/下沿（实时换位后它就是目标位置）。 */
  function updateIndicator(container, y) {
    const row = dragging.row;
    const b = row.getBoundingClientRect();
    const above = y < b.top + b.height / 2;
    for (const r of dragRows(container)) {
      r.classList.toggle('drop-before', r === row && above);
      r.classList.toggle('drop-after', r === row && !above);
    }
  }

  function clearDragVisual(container) {
    for (const r of dragRows(container)) r.classList.remove('drop-before', 'drop-after', 'dragging');
    container.classList.remove('dragging');
    document.body.classList.remove('dll-dragging');
  }

  function endDrag(commit) {
    const d = dragging;
    if (!d) return;
    dragging = null;
    detachDoc();
    try {
      if (d.container.hasPointerCapture && d.container.hasPointerCapture(d.pointerId)) {
        d.container.releasePointerCapture(d.pointerId);
      }
    } catch { }
    clearDragVisual(d.container);
    if (!d.started) return;          // 只按下没越过阈值 → 不算拖拽
    if (commit !== false) commitOrder();
  }

  function onDocMove(ev) {
    const d = dragging;
    if (!d || ev.pointerId !== d.pointerId) return;
    if (!d.started) {
      if (Math.abs(ev.clientY - d.startY) < DRAG_THRESHOLD
        && Math.abs(ev.clientX - d.startX) < DRAG_THRESHOLD) return;
      d.started = true;
      d.row.classList.add('dragging');
      d.container.classList.add('dragging');
      document.body.classList.add('dll-dragging');
    }
    moveRow(d.container, ev.clientY);
  }

  function onDocUp(ev) {
    const d = dragging;
    if (!d || (ev && ev.pointerId !== d.pointerId)) return;
    endDrag(true);
  }

  function attachDoc() {
    document.addEventListener('pointermove', onDocMove, true);
    document.addEventListener('pointerup', onDocUp, true);
    document.addEventListener('pointercancel', onDocUp, true);
  }

  function detachDoc() {
    document.removeEventListener('pointermove', onDocMove, true);
    document.removeEventListener('pointerup', onDocUp, true);
    document.removeEventListener('pointercancel', onDocUp, true);
  }

  function wireHandle(handle, row, container) {
    handle.setAttribute('data-tip', '按住拖动可调整加载顺序');
    handle.addEventListener('pointerdown', (ev) => {
      if (!installed()) return;
      if (ev.button !== 0 || ev.isPrimary === false || dragging) return;
      ev.preventDefault();
      dragging = {
        pointerId: ev.pointerId,
        handle, row, container,
        startX: ev.clientX, startY: ev.clientY,
        started: false,
      };
      // 捕获挂在分组容器上（容器始终在文档里），真正的移动/抬起由 document 捕获监听驱动：
      // 实时换位会用 insertBefore 搬运被拖行，浏览器可能在搬运瞬间释放指针捕获，
      // 因此不依赖 lostpointercapture（否则真实 WebView2 里刚拖动就被中断）。
      try { container.setPointerCapture(ev.pointerId); } catch { }
      attachDoc();
    });
  }

  // 窗口失焦：指针抬起/取消事件可能收不到，兜底结束拖拽（全局只挂一次）
  endDragHook = () => endDrag(true);

  // ---------------------------------------------------------------- 行渲染
  /**
   * 一行的五列：手柄 28 / 开关 48 / 名称 200 / 说明 1fr / 操作 92。
   * 名称与说明必须同一行垂直居中（旧 UI 就是这样，不是上下堆叠）。
   */
  function managedRow(p, index) {
    const sw = createSwitch({
      checked: !!p.on,
      disabled: !installed() || p.locked,
      onChange: () => { markDirty(DIRTY_SWITCH); },
    });
    sw.el.dataset.file = p.file;
    sw.el.dataset.part = 'switch';

    const row = h('div.dll-row' + (p.locked ? '.locked' : ''), { dataset: { file: p.file, kind: 'managed' } });

    if (p.locked) {
      row.appendChild(h('span.dll-grip.grip-none', [icon('lock')]));
    } else {
      const grip = h('span.dll-grip', [icon('grip')]);
      wireHandle(grip, row, managedList);
      row.appendChild(grip);
    }

    row.appendChild(h('span.dll-ctl', [sw.el]));
    row.appendChild(h('div.dll-name' + (p.exists ? '' : '.missing'), {
      text: p.locked ? p.name + '（必选）' : p.name,
    }));
    row.appendChild(h('div.dll-desc' + (p.exists ? '' : '.missing'), {
      text: (p.desc || '') + '（' + p.file + '）' + (p.exists ? '' : ' · 文件未安装'),
    }));

    const actions = h('div.dll-actions');
    if (p.config) {
      actions.appendChild(h('button.btn.sm.dll-edit', {
        type: 'button', text: '编辑', 'data-tip': '编辑 ' + p.config,
        onclick: () => editConfig(p.config, p.name),
      }));
    }
    row.appendChild(actions);
    row.dataset.index = String(index);
    return row;
  }

  function customRow(c) {
    const sw = createSwitch({
      checked: !!c.on,
      disabled: !installed(),
      onChange: () => { markDirty(DIRTY_SWITCH); },
    });
    sw.el.dataset.file = c.file;
    sw.el.dataset.part = 'switch';

    const row = h('div.dll-row.custom', { dataset: { file: c.file, kind: 'custom' } });
    const grip = h('span.dll-grip', [icon('grip')]);
    wireHandle(grip, row, customList);
    row.appendChild(grip);
    row.appendChild(h('span.dll-ctl', [sw.el]));
    row.appendChild(h('div.dll-name' + (c.exists ? '' : '.missing'), { text: c.file }));
    row.appendChild(h('div.dll-desc' + (c.exists ? '' : '.missing'), {
      text: c.exists ? '已挂载（文件在游戏根目录）' : '文件缺失（仅剩 GameGaurd.ini 条目）',
    }));
    row.appendChild(h('div.dll-actions', [
      h('button.btn.sm.danger', {
        type: 'button', text: '删除',
        onclick: () => removeCustom(c.file),
      }),
    ]));
    return row;
  }

  function renderList() {
    clear(managedList);
    clear(customList);
    clear(customCap);
    emptyBox.style.display = 'none';
    listScroll.style.display = '';

    if (!state) return;

    if (!installed()) {
      // 未安装：连「已安装插件…」表头一起隐藏，只留空态（旧 UI 也是只显示空态）
      listHead.style.display = 'none';
      listScroll.style.display = 'none';
      emptyBox.style.display = '';
      clear(emptyBox);
      emptyBox.appendChild(h('div.dll-empty-cap', { text: UNINSTALLED_CAP }));
      emptyBox.appendChild(h('div.dll-empty-tip', { text: UNINSTALLED_TIP }));
      setStatus(state.status || '未安装 DLL 扩展', false);
      return;
    }

    listHead.style.display = '';
    listScroll.style.display = '';
    emptyBox.style.display = 'none';

    (state.managed || []).forEach((p, i) => managedList.appendChild(managedRow(p, i)));

    if ((state.custom || []).length) {
      customCap.appendChild(h('div.dll-cap-text', { text: CUSTOM_CAP }));
      for (const c of state.custom) customList.appendChild(customRow(c));
    } else {
      customCap.appendChild(h('div.dll-cap-text.muted', { text: CUSTOM_EMPTY }));
    }

    setStatus(state.status || '', dirty);
  }

  // ---------------------------------------------------------------- 读取 / 刷新
  async function refresh(fromUser) {
    let res = null;
    try { res = await call('dll.state', {}); }
    catch (err) {
      if (fromUser) toast('error', '读取 DLL 状态失败：' + String(err.message || err));
      setStatus('读取 DLL 状态失败：' + String(err.message || err), false);
      return;
    }
    if (!res || res.ok === false) {
      setStatus('读取 DLL 状态失败：' + ((res && res.error) || '未知错误'), false);
      return;
    }
    state = res;
    dirty = false;
    renderList();
  }

  // ---------------------------------------------------------------- 应用更改
  /**
   * 确认正文由宿主生成（dll.previewApply），与真正写入时的口径完全一致；
   * 宿主不可用或预览失败时才退回本地文案。
   */
  async function applyText(managedOn, customOnList) {
    try {
      const p = await call('dll.previewApply', { managedOn, customOn: customOnList });
      if (p && p.ok && p.confirmText) return p.confirmText;
    } catch { /* 退回本地文案 */ }
    return localApplyText(managedOn, customOnList);
  }

  /** 本地兜底：与 DllController.ConfirmText 保持同一措辞。 */
  function localApplyText(managedOn, customOnList) {
    const ini = state.iniPath || 'GameGaurd.ini';
    const loader = state.loaderInstalled !== false;
    return '将直写游戏根目录 GameGaurd.ini 的 [Plugins] 插件列表：\n'
      + '  启用受管插件 ' + managedOn.length + '（未勾选的受管条目会从列表移除，文件保留；\n'
      + '  自定义扩展 ' + customOnList.length + ' 个按开关勾选写入（取消勾选 = 从 [Plugins] 移除，文件保留）'
      + (loader ? '' : '\n\n提示：游戏根目录尚未发现挂载器 GameGaurd.dll / az.dll，插件 DLL 文件可能未安装——请先到【设置与关于】页点击【新DLL安装】安装补丁包')
      + '\n\n目标文件：' + ini + (state.iniExists ? '' : '（尚不存在，将创建）')
      + '\n\n继续？';
  }

  function collectOn(container) {
    const on = [];
    for (const el of Array.from(container.querySelectorAll('.switch[data-file]'))) {
      if (el.classList.contains('on')) on.push(el.dataset.file);
    }
    return on;
  }

  async function doApply() {
    if (!installed()) { notInstalled('应用更改'); return; }
    const managedOn = collectOn(managedList);
    const customOnList = collectOn(customList);

    // GameNative 必选：前端再兜一层
    if (!managedOn.some((f) => /^GameNative\.dll$/i.test(f))) {
      say('warning', '>>> [DLL扩展] 无法应用：原生整合补丁（GameNative）为必选插件，必须保持开启');
      toast('warning', '无法应用：原生整合补丁（GameNative）为必选插件，必须保持开启');
      return;
    }

    const r = await confirmModal('应用更改', await applyText(managedOn, customOnList), '写入 GameGaurd.ini');
    if (r.value !== 'yes') return;

    const managedOrder = orderOf(managedList);
    const customOrder = orderOf(customList);

    await withBusy(async () => {
      let res = null;
      try {
        res = await call('dll.apply', { managedOn, customOn: customOnList, managedOrder, customOrder });
      } catch (err) {
        res = { ok: false, error: String(err.message || err) };
      }
      if (res && res.ok) {
        toast('success', '已应用：受管插件 ' + (res.managed || 0) + ' · 自定义扩展 ' + (res.custom || 0) + '（直写 GameGaurd.ini）');
        say('secondary', '>>> [DLL扩展] 应用更改完成；应用更改不会覆盖已编辑配置');
        await refresh(false);
        return;
      }
      if (res && res.error) toast('error', res.error);
      else toast('error', '应用失败：宿主无响应');
    });
  }

  // ---------------------------------------------------------------- 添加扩展
  /** 未安装时的统一引导：日志 + toast（旧 UI 是 MessageBox，这里换成主题化提示）。 */
  function notInstalled(what) {
    say('warning', '>>> [DLL扩展] 未安装 DLL 扩展，已阻止：' + what
      + '（请先到【设置与关于】页点击【新DLL安装】安装 客户端补丁.zip）');
    toast('warning', '游戏根目录尚未安装 DLL 扩展，请先到【设置与关于】页点击【新DLL安装】');
  }

  async function doAdd() {
    if (!installed()) { notInstalled('添加自定义扩展'); return; }
    const pick = await openFileBrowser({
      mode: 'open',
      title: '添加扩展（选择要挂载的 .dll / .ini）',
      filter: 'dll',
      startPath: (ctx.paths && (ctx.paths.gr || ctx.paths.ad)) || '',
      okText: '挂载此文件',
    });
    if (pick.value !== 'ok') return;
    await withBusy(async () => {
      let res = null;
      try { res = await call('dll.add', { path: pick.path }); } catch (err) { res = { ok: false, error: String(err.message || err) }; }
      if (res && res.ok) {
        toast('success', '已挂载: ' + pick.fileName);
        if (res.state) { state = res.state; dirty = false; renderList(); } else await refresh(false);
        return;
      }
      if (res && res.error) {
        say('warning', '>>> [DLL扩展] ' + res.error);
        toast('warning', res.error);
      } else toast('error', '添加失败：宿主无响应');
    });
  }

  // ---------------------------------------------------------------- 删除扩展
  async function removeCustom(file) {
    const c = (state.custom || []).find((x) => x.file === file) || { file, exists: false };
    const pick = await pickCustomToDelete([c]);
    if (pick.value !== 'ok') return;
    await deleteCustom(pick.file, pick.deleteFile);
  }

  async function deleteCustom(file, deleteFile) {
    await withBusy(async () => {
      let res = null;
      try { res = await call('dll.delete', { file, deleteFile: !!deleteFile }); } catch (err) { res = { ok: false, error: String(err.message || err) }; }
      if (res && res.ok) {
        toast('success', '已移除: ' + file + (deleteFile ? '（同时删除游戏根目录文件）' : ''));
        if (res.state) { state = res.state; dirty = false; renderList(); } else await refresh(false);
        return;
      }
      if (res && res.error) { say('warning', '>>> [DLL扩展] ' + res.error); toast('warning', res.error); }
      else toast('error', '删除失败：宿主无响应');
    });
  }

  async function doDelete() {
    if (!installed()) { notInstalled('删除自定义扩展'); return; }
    const custom = state.custom || [];
    if (!custom.length) { toast('warning', '当前没有自定义扩展可删除'); return; }
    const pick = await pickCustomToDelete(custom);
    if (pick.value !== 'ok') return;
    await deleteCustom(pick.file, pick.deleteFile);
  }

  /**
   * 主题化列表选择：挑一个自定义扩展删除，可勾选「同时删除游戏根目录文件」。
   * @returns {Promise<{value:'ok'|'cancel', file?:string, deleteFile?:boolean}>}
   */
  function pickCustomToDelete(custom) {
    return new Promise((resolve) => {
      // 默认选中第一项：避免用户直接点「删除」时因为没选而静默无效。
      let chosen = custom.length ? custom[0].file : '';
      let settled = false;
      function done(value) {
        if (settled) return;
        settled = true;
        ov.close();
        resolve(value === 'ok' ? { value: 'ok', file: chosen, deleteFile: withFile.get() } : { value: 'cancel' });
      }

      const ov = createOverlay({
        title: '删除扩展',
        cls: 'ov-pick',
        onRequestClose: () => done('cancel'),
      });

      const list = h('div.pick-list');
      for (const c of custom) {
        const row = h('div.pick-row', { dataset: { file: c.file } }, [
          h('span.pick-radio'),
          h('div.pick-main', [
            h('div.pick-name' + (c.exists ? '' : '.missing'), { text: c.file }),
            h('div.pick-desc', { text: c.exists ? '文件在游戏根目录' : '文件缺失（仅剩 GameGaurd.ini 条目）' }),
          ]),
        ]);
        row.addEventListener('click', () => {
          chosen = c.file;
          for (const r2 of Array.from(list.children)) r2.classList.toggle('sel', r2.dataset.file === chosen);
        });
        if (c.file === chosen) row.classList.add('sel');
        list.appendChild(row);
      }

      const withFile = createSwitch({ label: '同时删除游戏根目录的插件文件', checked: false });

      ov.body.appendChild(h('div.pick-note', { text: '删除会从 GameGaurd.ini 的 [Plugins] 列表移除该条目；不勾选下面的开关时，插件文件会保留在游戏根目录。' }));
      ov.body.appendChild(list);
      ov.body.appendChild(h('div.pick-foot', [withFile.el]));
      ov.foot.appendChild(h('button.btn', { type: 'button', text: '取消', onclick: () => done('cancel') }));
      ov.foot.appendChild(h('button.btn.danger', { type: 'button', text: '删除', onclick: () => done('ok') }));
    });
  }

  // ---------------------------------------------------------------- 编辑配置
  async function editConfig(file, pluginName) {
    let res = null;
    try { res = await call('dll.readConfig', { file }); }
    catch (err) { toast('error', '读取配置失败：' + String(err.message || err)); return; }
    if (!res || res.ok === false) { toast('error', '读取配置失败：' + ((res && res.error) || '未知错误')); return; }

    const edit = await openIniEditor({
      // 旧 UI 标题：插件名 · 文件名
      title: (pluginName ? pluginName + ' · ' : '') + file,
      path: res.path,
      exists: !!res.exists,
      text: res.text || '',
      encoding: res.encoding,
    });
    if (edit.value !== 'ok') return;

    let save = null;
    try { save = await call('dll.saveConfig', { file, text: edit.text }); }
    catch (err) { save = { ok: false, error: String(err.message || err) }; }
    if (save && save.ok) { toast('success', '已保存配置: ' + file); await refresh(false); }
    else toast('error', '保存配置失败：' + ((save && save.error) || '宿主无响应'));
  }

  async function withBusy(fn) {
    if (busyFlag) return;
    busyFlag = true;
    wrap.classList.add('busy');
    try { await fn(); }
    finally { busyFlag = false; wrap.classList.remove('busy'); }
  }

  // ---------------------------------------------------------------- 初始化
  mounted = { alive: () => managedList.isConnected, refresh: () => refresh(false) };
  refresh(false);
  return null;
}

/** `dll.changed` 推送时由 app.js 调用：仅当 DLL 页仍挂载才刷新。 */
export function refreshDllIfActive() {
  if (!mounted || !mounted.alive()) { mounted = null; return false; }
  mounted.refresh();
  return true;
}
