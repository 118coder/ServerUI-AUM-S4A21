/* ==========================================================================
   ui/status.js — 状态快照 → 视图文案（开始页 / 极简模式 / 经典模式共用）
   宿主状态快照字段见 Bridge/AppController.BuildSnapshot；这里只做纯映射。
   ========================================================================== */

/** 去掉宿主文案里的装饰圆点；统一「(外部)」为无空格写法。 */
export function cleanState(text) {
  return String(text || '')
    .replace(/^[●\s]+/, '')
    .replace(/\s*\(\s*外部\s*\)\s*/, '(外部)')
    .trim();
}

export function toneOf(level) {
  switch (level) {
    case 'success': return 'ok';
    case 'warning': return 'warn';
    case 'error': return 'bad';
    case 'secondary': return 'off';
    default: return 'off';
  }
}

/** 由状态快照派生视图文案（宿主文案优先，其次本地兜底）。 */
export function statusView(st, ctx) {
  const s = st || {};
  const running = !!s.bat || !!s.dfo;
  const version = s.version || (ctx && ctx.version) || '2.20';

  // 经典模式状态卡标题已经是「上次更新」，值里不要再重复前缀；
  // 长占位文案压成「尚未更新」，完整文案放 data-tip。
  const luFull = String(s.lastUpdate || '');
  const luBody = luFull.replace(/^上次更新[:：]\s*/, '').trim();
  const luShort = !luBody ? '未知'
    : (luBody.indexOf('尚未有log') >= 0 || luBody.indexOf('无法识别版本') >= 0 ? '尚未更新' : luBody);

  return {
    server: {
      text: cleanState(s.serverState) || (running ? '运行中' : '未运行'),
      tone: toneOf(s.serverLevel),
    },
    pvf: {
      text: s.pvf ? '已加载' : '未找到',
      tone: s.pvf ? 'ok' : 'bad',
    },
    version: {
      text: 'v' + String(version).replace(/^v/i, ''),
      tone: null,
    },
    sdk: {
      text: s.sdk || '.NET SDK: 检测中…',
      tone: s.sdkOk ? 'ok' : 'off',
    },
    lastUpdate: {
      text: s.lastUpdate || '上次更新: 未知',
      tone: null,
    },
    lastUpdateShort: {
      text: luShort,
      tone: null,
      tip: luFull || '上次更新: 未知',
    },
  };
}
