/* ==========================================================================
   ui/actions.js — 业务动作薄封装（开始页 / 极简模式 / 经典模式共用）
   原则：命令调用、运行前置检查、忙碌态、日志/提示文案只在这里写一次；
   各页面只负责摆按钮和布局，避免复制业务逻辑。
   ========================================================================== */

import { busy } from './dom.js';
import { call } from '../bridge.js';
import { toast } from './toast.js';
import { openModal } from './modal.js';
import { createSwitch } from './switch.js';
import { createSegmented } from './segmented.js';

// ---------------------------------------------------------------- 状态推导
export function serverRunning(ctx) {
  const s = (ctx && ctx.status) || {};
  return !!s.bat || !!s.dfo;
}

export function serverExternal(ctx) {
  const s = (ctx && ctx.status) || {};
  return !s.bat && !!s.dfo;
}

export function say(ctx, level, text) {
  if (ctx && ctx.log && typeof ctx.log.append === 'function') ctx.log.append(level, text);
}

/** 不适用操作：写日志 + toast，绝不静默禁用（P0-7）。 */
export function warnInapplicable(ctx, text, tag) {
  say(ctx, 'warning', '>>> [' + (tag || '服务端') + '] ' + text);
  toast('warning', text);
}

export function openShell(ctx, kind, value) {
  call('shell.open', { kind, value: value || null }).catch((err) => {
    say(ctx, 'error', String((err && err.message) || err));
  });
}

// ---------------------------------------------------------------- 服务端动作
export function wirePlay(btn, ctx) {
  btn.addEventListener('click', async () => {
    if (serverRunning(ctx)) {
      warnInapplicable(ctx, serverExternal(ctx) ? '服务端已在外部运行中，无需重复启动' : '服务端已在运行中，无需重复启动');
      return;
    }
    const done = busy(btn, '正在启动…');
    try { await call('server.start', {}, { timeout: 180000 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}

export function wireStop(btn, ctx) {
  btn.addEventListener('click', async () => {
    if (!serverRunning(ctx)) { warnInapplicable(ctx, '服务端未运行，无需停止'); return; }
    const done = busy(btn, '正在停止…');
    try { await call('server.stop', {}, { timeout: 120000 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}

export function wireRestart(btn, ctx) {
  btn.addEventListener('click', async () => {
    if (!serverRunning(ctx)) { warnInapplicable(ctx, '服务端未运行，无法重启'); return; }
    const done = busy(btn, '正在重启…');
    try { await call('server.restart', {}, { timeout: 180000 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}

export function wireGm(btn, ctx) {
  btn.addEventListener('click', async () => {
    const done = busy(btn);
    try { await call('gm.launch', {}, { timeout: 60000 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
  });
  return btn;
}

export function wireRestorePvf(btn, ctx) {
  btn.addEventListener('click', async () => {
    const done = busy(btn, '正在处理…');
    try { await call('pvf.restore', {}, { timeout: 300000 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}

// ---------------------------------------------------------------- DX / 水印
/**
 * 运行方式分段 + 去水印开关：任一变更即整组下发 dx.set。
 * @returns {{seg:object, sw:object}}
 */
export function createDxControls(ctx, opts) {
  const o = opts || {};
  const settings = (ctx && ctx.settings) || {};
  const dxMode = (settings.dxMode === 'dx11' || settings.dxMode === 'dx12') ? settings.dxMode : null;

  function pushDx(mode, watermark, note) {
    call('dx.set', { mode: mode || 'off', watermark: !!watermark }).then(() => {
      settings.dxMode = mode || 'off';
      settings.watermark = !!watermark;
      if (note) say(ctx, 'secondary', '>>> [运行方式] ' + note);
    }).catch((err) => {
      say(ctx, 'error', String((err && err.message) || err));
    });
  }

  const sw = createSwitch({
    label: '去除 dgVoodooCpl 水印',
    checked: !!settings.watermark,
    onChange: (on) => pushDx(seg.get(), on, on ? '已开启去除水印' : '已关闭去除水印'),
  });

  const seg = createSegmented({
    items: [
      // 经典模式左栏窄：用短标签，避免分段控件把卡片撑出可视区
      { value: 'dx11', text: o.short ? 'DX11' : '以DX11方式运行游戏' },
      { value: 'dx12', text: o.short ? 'DX12' : '以DX12方式运行游戏' },
    ],
    value: dxMode,
    onChange: (val) => pushDx(val, sw.get(), val ? ('已启用 ' + val.toUpperCase()) : '已取消运行方式，恢复默认 DX9'),
  });

  return { seg, sw };
}

// ---------------------------------------------------------------- 更新动作
export function updateParams(ctx) {
  const log = ctx && ctx.log;
  const settings = (ctx && ctx.settings) || {};
  return {
    mode: 'incremental',
    skipLog: log && log.skipLog ? log.skipLog() : !!settings.skipLog,
    mirror: log && log.mirror ? log.mirror() : !!settings.mirror,
  };
}

/** 开始更新（一键增量，不弹选择框）。 */
export function wireUpdate(btn, ctx) {
  btn.addEventListener('click', async () => {
    say(ctx, 'secondary', '>>> [更新] 开始增量更新（仅更新变化的文件）');
    const done = busy(btn, '更新中…');
    try { await call('update.start', updateParams(ctx), { timeout: 0 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}

/** 完整更新（先主题化确认，aria 与旧 UI 一致）。 */
export function wireFullUpdate(btn, ctx) {
  btn.addEventListener('click', async () => {
    const res = await openModal({
      title: '完整更新',
      text: '完整更新会重新校验并覆盖全部文件，耗时较长，建议在网络稳定时执行。\n确定要开始完整更新吗？',
      buttons: [
        { text: '开始完整更新', value: 'yes', primary: true },
        { text: '取消', value: 'cancel' },
      ],
    });
    if (res.value !== 'yes') return;
    say(ctx, 'secondary', '>>> [更新] 开始完整更新（覆盖全部文件）');
    const done = busy(btn, '更新中…');
    try {
      const params = updateParams(ctx);
      params.mode = 'full';
      await call('update.start', params, { timeout: 0 });
    } catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}

export function wireUpdateLog(btn, ctx) {
  btn.addEventListener('click', () => {
    call('update.openLog', {}).catch((err) => say(ctx, 'error', String((err && err.message) || err)));
  });
  return btn;
}

export function wireAumUpdate(btn, ctx) {
  btn.addEventListener('click', async () => {
    const done = busy(btn, '检查中…');
    try {
      const r = await call('aum.update', {}, { timeout: 600000 });
      if (r && r.blocked) toast('warning', r.error || 'AUM 自更新已阻止');
    } catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
  });
  return btn;
}

export function wireSdkInstall(btn, ctx) {
  btn.addEventListener('click', async () => {
    const sdkOk = !!(ctx && ctx.status && ctx.status.sdkOk);
    if (sdkOk) {
      say(ctx, 'warning', '>>> [SDK] .NET 10 SDK 已就绪，无需重复安装');
      toast('warning', '.NET 10 SDK 已就绪，无需重复安装');
      return;
    }
    const done = busy(btn, '安装中…');
    try { await call('sdk.install', {}, { timeout: 0 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}
