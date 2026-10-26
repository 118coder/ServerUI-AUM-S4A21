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
    // 服务端已在运行时不再拦截：多开 DLL 的用法就是「服务端常驻，反复拉起客户端」。
    // 宿主收到 server.start 后会发现服务端存活，跳过启动步骤、只启动游戏客户端
    // （返回 reused=true）。这里只调整按钮文案，避免用户以为在重启服务端。
    const reuse = serverRunning(ctx);
    const done = busy(btn, reuse ? '正在启动客户端…' : '正在启动…');
    try {
      const res = await call('server.start', {}, { timeout: 180000 });
      if (res && res.reused) {
        say(ctx, 'secondary', '>>> 服务端已在运行，本次仅启动游戏客户端（多开）');
      }
      if (res && res.game === 'none') {
        warnInapplicable(ctx, '未找到 本地游戏S4.bat / 单机游戏启动.bat，无法启动客户端');
      }
    } catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
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
  const MODES = ['dx11', 'dx12', 'vulkan'];
  const MODE_LABEL = { dx11: 'DX11', dx12: 'DX12', vulkan: 'Vulkan' };
  const dxMode = MODES.indexOf(settings.dxMode) >= 0 ? settings.dxMode : null;

  function pushDx(mode, watermark, note) {
    call('dx.set', { mode: mode || 'off', watermark: !!watermark }).then((res) => {
      // 宿主没真正切换（补丁缺失 / 文件被游戏占用）时保持原状态，别让界面假装成功
      if (res && res.ok === false) return;
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

  /** Vulkan 走 DXVK，没有 dgVoodoo 水印：选中时强制关掉并禁用开关。 */
  function syncWatermark(mode) {
    const vk = mode === 'vulkan';
    if (vk && sw.get()) { sw.set(false, true); settings.watermark = false; }
    sw.disable(vk);
    if (vk) sw.el.setAttribute('data-tip', 'Vulkan 方式不使用 dgVoodoo，无需去水印');
    else sw.el.removeAttribute('data-tip');
  }

  const seg = createSegmented({
    items: [
      // 经典模式左栏窄：用短标签，避免分段控件把卡片撑出可视区
      { value: 'dx11', text: o.short ? 'DX11' : '以DX11方式运行游戏' },
      { value: 'dx12', text: o.short ? 'DX12' : '以DX12方式运行游戏' },
      { value: 'vulkan', text: o.short ? 'Vulkan' : '以Vulkan方式运行游戏' },
    ],
    value: dxMode,
    onChange: (val) => {
      syncWatermark(val);
      const label = MODE_LABEL[val];
      pushDx(val, sw.get(), val ? ('已启用 ' + label + ' 方式') : '已取消运行方式，恢复默认 DX9');
    },
  });

  syncWatermark(dxMode);

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

/**
 * 更新AUM：先取「计划」（会自动选发行包 / 源码编译），用主题化确认框把
 * 当前版本 → 目标版本 / 采用策略 / 策略原因 / 包大小 / 失败回滚 说明清楚，确认后执行。
 * forcePackage=true 时跳过源码编译，直接用发行包（设置页「强制使用发行包」入口）。
 */
export function wireAumUpdate(btn, ctx, opts) {
  const o = opts || {};
  btn.addEventListener('click', async () => {
    const forcePackage = !!o.forcePackage;
    const done = busy(btn, '检查中…');
    let plan = null;
    try {
      plan = await call('aum.plan', { forcePackage }, { timeout: 180000 });
    } catch (err) {
      say(ctx, 'error', String((err && err.message) || err));
      done();
      return;
    }
    done();

    if (!plan || plan.ok === false) {
      const msg = (plan && plan.error) || '检查更新失败';
      say(ctx, 'error', msg);
      toast('error', msg);
      return;
    }
    if (plan.upToDate) {
      say(ctx, 'secondary', '>>> [AUM更新] 已是最新版本 v' + (plan.current || ''));
      toast('info', '已是最新版本 v' + (plan.current || ''));
      return;
    }

    const strategyText = plan.strategyLabel || (plan.strategy === 'compile' ? '源码编译' : '发行包替换');
    const sizeText = plan.packageBytes > 0 ? formatBytes(plan.packageBytes) : '';
    const lines = [
      '当前版本：v' + (plan.current || '?') + '  →  目标版本：v' + (plan.target || '?'),
      '采用策略：' + strategyText,
      '判定原因：' + (plan.reason || '—'),
    ];
    if (sizeText) lines.push('发行包大小：' + sizeText);
    lines.push('', '更新前会校验包哈希与版本号，失败会自动回滚；完成后管理器将重启。');

    const res = await openModal({
      title: '更新 AUM 管理器',
      text: lines.join('\n'),
      buttons: [
        { text: '开始更新', value: 'yes', primary: true },
        { text: '取消', value: 'cancel' },
      ],
    });
    if (res.value !== 'yes') return;

    const done2 = busy(btn, '更新中…');
    try {
      const r = await call('aum.update', { forcePackage }, { timeout: 900000 });
      if (r && r.upToDate) toast('info', '已是最新版本');
      else if (r && r.ok === false) {
        say(ctx, 'error', '更新失败：' + (r.error || '未知错误'));
        toast('error', '更新失败，详情见日志');
      } else if (r && r.restarting) {
        toast('success', '已更新到 v' + r.target + '，正在重启');
      }
    } catch (err) {
      say(ctx, 'error', String((err && err.message) || err));
      toast('error', '更新失败，详情见日志');
    }
    done2();
  });
  return btn;
}

function formatBytes(n) {
  if (!n || n < 1) return '';
  var mb = n / (1024 * 1024);
  return mb >= 1 ? mb.toFixed(1) + ' MB' : (n / 1024).toFixed(0) + ' KB';
}

export function wireSdkInstall(btn, ctx) {
  btn.addEventListener('click', async () => {
    // 旧版 IS()：SDK 已就绪时仍会打开安装包，供修复/覆盖安装；这里不拦截。
    const sdkOk = !!(ctx && ctx.status && ctx.status.sdkOk);
    if (sdkOk) say(ctx, 'secondary', '>>> [SDK] .NET 10 SDK 已就绪，仍将打开安装包（可用于修复/覆盖安装）');
    const done = busy(btn, '安装中…');
    try { await call('sdk.install', {}, { timeout: 0 }); }
    catch (err) { say(ctx, 'error', String((err && err.message) || err)); }
    done();
    if (ctx && ctx.refreshStatus) ctx.refreshStatus();
  });
  return btn;
}
