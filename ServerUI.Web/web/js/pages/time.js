/* ==========================================================================
   pages/time.js — 调整系统时间页（Phase 3a）
   对照旧 MainForm.Time.cs：
     信息栏（目标时间 + 自绘日期时间选择器 + 读取系统时间）
     操作行（应用此时间 + 快速调整 2006 / 2015 / 2017）
     网络同步卡（同步按钮 + 说明 + 每秒刷新的当前系统时间）
     状态行（就绪 / 成功 / 失败）
   日期时间选择器为自绘浮层（ui/datetimepicker.js），不使用任何原生日期控件。
   ========================================================================== */

import { h, icon } from '../ui/dom.js';
import { createDateTimePicker, parseDateTime } from '../ui/datetimepicker.js';
import { toast } from '../ui/toast.js';
import { call } from '../bridge.js';

const SUB = '手动设置 / 快速换算 / 网络校准 · 写入系统时间需要管理员权限';
const SYNC_NOTE = '本工具可以调节系统时间，用于无损修改相关道具物品的使用时间，避免魔改PVF导致错误而造成开发人员的困扰。';

function pad2(n) { return n < 10 ? '0' + n : String(n); }
function fmt(d) {
  return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate())
    + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
}

export function renderTime(pageEl, ctx) {
  const log = ctx.log;
  const say = (level, text) => { if (log && log.append) log.append(level, text); };
  const wrap = h('div.time-page');
  let busy = false;
  let ticker = 0;

  // ---------------------------------------------------------------- 页头
  wrap.appendChild(h('div.page-head', [
    h('div.ph-text', [
      h('div.page-title', { text: '调整系统时间' }),
      h('div.page-sub', { text: SUB }),
    ]),
  ]));

  // ---------------------------------------------------------------- 信息栏
  const picker = createDateTimePicker({ value: new Date() });
  const btnRead = h('button.btn', { type: 'button', id: 'btnTimeRead' }, [icon('clock'), h('span', { text: '读取系统时间' })]);
  wrap.appendChild(h('div.card.time-info', [
    h('span.time-label', { text: '目标时间：' }),
    picker.el,
    btnRead,
  ]));

  // ---------------------------------------------------------------- 操作行
  const btnApply = h('button.btn.primary', { type: 'button', id: 'btnTimeApply' }, [icon('check'), h('span', { text: '应用此时间' })]);
  const btn2006 = h('button.btn', { type: 'button', 'data-year': '2006' }, [icon('undo'), h('span', { text: '快速调整 2006-01-01' })]);
  const btn2015 = h('button.btn', { type: 'button', 'data-year': '2015' }, [icon('undo'), h('span', { text: '快速调整 2015-01-01' })]);
  const btn2017 = h('button.btn', { type: 'button', 'data-year': '2017' }, [icon('undo'), h('span', { text: '快速调整 2017-01-01' })]);
  wrap.appendChild(h('div.time-ops', [btnApply, btn2006, btn2015, btn2017]));

  // ---------------------------------------------------------------- 网络同步卡
  const btnSync = h('button.btn.success.time-sync', { type: 'button', id: 'btnTimeSync' }, [
    icon('sync'), h('span', { text: '同步网络时间（校准为标准时间）' }),
  ]);
  const nowEl = h('div.time-now', { dataset: { bind: 'now' }, text: '当前系统时间：--' });

  // 状态行放在同步卡内部最底部：卡片撑满页面剩余高度，状态行贴卡片下沿
  // （旧 UI 是独立一行，视觉位置一致；这样卡片底边 = 页面内容底边）
  const statusEl = h('div.time-status', { dataset: { bind: 'status' }, text: '就绪' });
  wrap.appendChild(h('div.card.time-card', [
    btnSync,
    h('div.time-note', { text: SYNC_NOTE }),
    nowEl,
    statusEl,
  ]));

  pageEl.appendChild(wrap);

  function setStatus(text, tone) {
    statusEl.textContent = text || '就绪';
    statusEl.classList.toggle('ok', tone === 'ok');
    statusEl.classList.toggle('bad', tone === 'bad');
  }

  function paintNow() {
    nowEl.textContent = '当前系统时间：' + fmt(new Date());
  }

  let pending = null;

  /**
   * 串行执行：忙碌时把这次操作排队（而不是静默丢弃）。
   * 旧 UI 是同步阻塞、连点无效；新 UI 若直接 return，用户点了快速调整会毫无反应。
   */
  async function withBusy(fn) {
    if (busy) { pending = pending ? pending.then(fn, fn) : Promise.resolve().then(fn); return pending; }
    busy = true;
    wrap.classList.add('busy');
    try {
      await fn();
      while (pending) {
        const next = pending;
        pending = null;
        await next;
      }
    } finally { busy = false; wrap.classList.remove('busy'); }
  }

  async function refreshFromHost() {
    let res = null;
    try { res = await call('time.get', {}); }
    catch (err) { res = null; }
    if (res && res.ok) {
      const d = parseDateTime(res.now);
      if (d) picker.set(d, true);
      paintNow();
    }
    return res;
  }

  // ---------------------------------------------------------------- 读取
  btnRead.addEventListener('click', () => {
    say('secondary', '>>> 已读取当前系统时间');
    refreshFromHost().then((res) => {
      if (!res || res.ok === false) {
        toast('error', '读取系统时间失败');
        setStatus('读取失败', 'bad');
        return;
      }
      setStatus('已读取：' + picker.text(), 'ok');
    });
  });

  // ---------------------------------------------------------------- 应用
  async function applyTime(date, fromQuick) {
    if (!(date instanceof Date) || isNaN(date.getTime())) {
      toast('warning', '目标时间不合法，请重新选择');
      setStatus('目标时间不合法', 'bad');
      return;
    }
    const ts = fmt(date);
    await withBusy(async () => {
      let res = null;
      try { res = await call('time.apply', { iso: ts }); }
      catch (err) { res = { ok: false, error: String(err.message || err) }; }
      if (res && res.ok) {
        const msg = (fromQuick ? '已快速调整：' : '已设置：') + ts;
        setStatus(msg, 'ok');
        toast('success', msg);
        await refreshFromHost();
        return;
      }
      const err = (res && res.error) || '宿主无响应';
      setStatus('设置失败：' + err, 'bad');
      toast('error', '设置失败：' + err);
    });
  }

  btnApply.addEventListener('click', () => {
    if (picker.input.classList.contains('invalid')) {
      toast('warning', '时间格式不正确，请使用 yyyy-MM-dd HH:mm:ss');
      setStatus('时间格式不正确', 'bad');
      return;
    }
    const typed = parseDateTime(picker.input.value);
    if (!typed) {
      picker.input.classList.add('invalid');
      toast('warning', '时间格式不正确，请使用 yyyy-MM-dd HH:mm:ss');
      setStatus('时间格式不正确', 'bad');
      return;
    }
    applyTime(typed, false);
  });

  for (const btn of [btn2006, btn2015, btn2017]) {
    btn.addEventListener('click', () => {
      const year = Number(btn.dataset.year);
      say('secondary', '>>> 快速调整: ' + year + '-01-01 00:00:00');
      withBusy(async () => {
        let res = null;
        try { res = await call('time.quick', { year }); }
        catch (err) { res = { ok: false, error: String(err.message || err) }; }
        if (res && res.ok) {
          const d = new Date(year, 0, 1, 0, 0, 0);
          picker.set(d, true);
          setStatus('已快速调整：' + fmt(d), 'ok');
          toast('success', '已快速调整到 ' + year + '-01-01');
          await refreshFromHost();
          return;
        }
        const err = (res && res.error) || '宿主无响应';
        setStatus('快速调整失败：' + err, 'bad');
        toast('error', '快速调整失败：' + err);
      });
    });
  }

  // ---------------------------------------------------------------- 网络同步
  btnSync.addEventListener('click', () => {
    withBusy(async () => {
      setStatus('正在同步网络时间…');
      let res = null;
      try { res = await call('time.sync', {}); }
      catch (err) { res = { ok: false, error: String(err.message || err) }; }
      if (res && res.ok) {
        const suffix = res.via === 'w32tm' ? '（w32tm）' : '';
        const msg = '已同步' + suffix + (res.now ? '：' + res.now : '');
        setStatus(msg, 'ok');
        toast('success', msg);
        await refreshFromHost();
        return;
      }
      const err = (res && res.error) || '无法连接时间服务器';
      setStatus('同步失败：' + err, 'bad');
      toast('error', '同步失败：' + err);
    });
  });

  // ---------------------------------------------------------------- 每秒刷新
  paintNow();
  refreshFromHost();
  ticker = setInterval(() => {
    if (!nowEl.isConnected) { clearInterval(ticker); return; }
    paintNow();
  }, 1000);

  return null;
}
