/* ==========================================================================
   pages/settings.js — 设置与关于页（Phase 3a）
   对照旧 MainForm.Pages.cs BuildAbout：
     关于卡（名称 / 版本 / 简介）
     仓库与链接卡（4 行，整行可点 → shell.open{kind:"url"}）
     实用工具卡（进行本地编译 / 待定 / 新DLL安装）
   ========================================================================== */

import { h, icon } from '../ui/dom.js';
import { confirmModal, openModal } from '../ui/modal.js';
import { toast } from '../ui/toast.js';
import { call } from '../bridge.js';

const LINKS = [
  { name: 'ServerUI-仓库', url: 'https://github.com/118coder/ServerUI-AUM-S4A21', text: 'github.com/118coder/ServerUI-AUM-S4A21' },
  { name: 'ServerS4A21-主源仓库', url: 'https://gitgud.io/rewio/ServerS4A21', text: 'gitgud.io/rewio/ServerS4A21' },
  { name: 'GM工具-主源仓库', url: 'https://gitgud.io/rewio/S4A21GmTool', text: 'gitgud.io/rewio/S4A21GmTool' },
  { name: 'GM 工具', url: 'http://localhost:5051', text: 'http://localhost:5051' },
];

const BUILD_TIP = '调用【AUM管理组件\\进行本地编译.bat】，用【AUM管理组件\\latest\\】的源码包重新编译【服务端】与【GM工具】';
const PENDING_TIP = '功能已暂时移除（S4A21 版本已无此问题），按钮保留待定';
const PATCH_TIP = '把 实用工具包\\DLL覆盖\\客户端补丁.zip 完整安装到游戏根目录：复制补丁文件（GameGaurd.dll / az.dll / 插件 DLL / 配置 ini 等）并合并 GameGaurd.ini 的 [Plugins] 列表（受管插件默认全启用，已编辑过的配置 ini 保留不覆盖）';

/** 「更新AUM（强制发行包）」：以 forcePackage=true 走与首页同一个更新流程。 */
async function doForcePackageUpdate(say, ctx) {
  const res = await call('aum.plan', { forcePackage: true }, { timeout: 180000 }).catch((err) => ({ ok: false, error: String(err.message || err) }));
  if (!res || res.ok === false) {
    const msg = (res && res.error) || '检查更新失败';
    say('error', msg);
    toast('error', msg);
    return;
  }
  if (res.upToDate) { toast('info', '已是最新版本 v' + (res.current || '')); return; }
  const res2 = await confirmModal('更新 AUM（强制发行包）',
    '当前版本：v' + (res.current || '?') + '  →  目标版本：v' + (res.target || '?') + '\n'
    + '将跳过源码编译，直接下载发行包并校验替换。\n'
    + (res.packageBytes > 0 ? '包大小：' + (res.packageBytes / 1048576).toFixed(1) + ' MB\n' : '')
    + '\n完成后管理器将重启，继续？', '开始更新', 'primary');
  if (res2.value !== 'yes') return;
  const r = await call('aum.update', { forcePackage: true }, { timeout: 900000 })
    .catch((err) => ({ ok: false, error: String(err.message || err) }));
  if (r && r.upToDate) toast('info', '已是最新版本');
  else if (r && r.ok === false) { say('error', '更新失败：' + (r.error || '未知错误')); toast('error', '更新失败，详情见日志'); }
  else if (r && r.restarting) toast('success', '已更新到 v' + r.target + '，正在重启');
}

export function renderSettings(pageEl, ctx) {
  const log = ctx.log;
  const say = (level, text) => { if (log && log.append) log.append(level, text); };
  const wrap = h('div.set-page');

  // ---------------------------------------------------------------- 页头
  wrap.appendChild(h('div.page-head', [
    h('div.ph-text', [
      h('div.page-title', { text: '设置与关于' }),
      h('div.page-sub', { text: '版本信息 · 仓库链接 · 实用工具' }),
    ]),
  ]));

  // ---------------------------------------------------------------- 关于
  wrap.appendChild(h('div.card.set-about', [
    h('div.set-appname', { text: 'ServerS4A21 管理器' }),
    h('div.set-ver', { text: 'v' + (ctx.version || '2.21') + ' · ServerS4A21-AUM' }),
    h('div.set-desc', {
      text: '基于 .NET 10 + WebView2 构建的自绘界面单机服务端管理工具：一键启停游戏服务、存档管理、增量更新、GM 工具等。',
    }),
  ]));

  // ---------------------------------------------------------------- 仓库与链接
  const links = h('div.card.set-card');
  links.appendChild(h('div.sec-title', [h('span', { text: '仓库与链接' })]));
  for (const L of LINKS) {
    const row = h('div.set-link', {
      role: 'link',
      tabindex: '0',
      'data-url': L.url,
      onclick: () => openLink(L, say),
      onkeydown: (ev) => {
        if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); openLink(L, say); }
      },
    }, [
      h('span.set-link-name', { text: L.name }),
      h('span.set-link-url', { text: L.text }),
    ]);
    links.appendChild(row);
  }
  wrap.appendChild(links);

  // ---------------------------------------------------------------- 实用工具
  const tools = h('div.card.set-card');
  tools.appendChild(h('div.sec-title', [h('span', { text: '实用工具' })]));

  const rowBuild = toolRow('进行本地编译', 'primary', 'tool', BUILD_TIP, () => doBuildLocal(say));
  const rowPending = toolRow('待定', '', 'tool', PENDING_TIP, () => {
    // 旧 UI 只写日志，不弹窗
    say('secondary', '【待定】原"修复赛丽亚房间问题"功能已暂时移除（S4A21 已无此问题），按钮保留');
    toast('info', '【待定】功能已暂时移除（S4A21 版本已无此问题）');
  });
  const rowPatch = toolRow('新DLL安装', 'success', 'download', PATCH_TIP, () => doInstallPatch(say, ctx));
  // 强制发行包：SDK 环境齐全但仍想省去编译时间的用户，可直接走发行包替换。
  const rowPkg = toolRow('更新AUM（强制发行包）', '', 'download',
    '跳过源码编译，直接下载 GitHub Release 的发行包并校验替换（适合机器已装 SDK 但不想等编译）',
    () => doForcePackageUpdate(say, ctx));

  tools.appendChild(rowBuild);
  tools.appendChild(rowPending);
  tools.appendChild(rowPatch);
  tools.appendChild(rowPkg);
  wrap.appendChild(tools);

  pageEl.appendChild(wrap);
  return null;
}

/** 一行：[按钮] + 右侧说明小字（说明可换行，卡片高度按内容撑开）。 */
function toolRow(text, kind, iconName, tip, onClick) {
  const btn = h('button.btn' + (kind ? '.' + kind : '') + '.set-tool-btn', {
    type: 'button',
    'data-tool': text,
    onclick: onClick,
  }, [icon(iconName), h('span', { text })]);
  return h('div.set-tool', [btn, h('div.set-tool-tip', { text: tip })]);
}

async function openLink(L, say) {
  say('secondary', '>>> 打开链接: ' + L.url);
  let res = null;
  try { res = await call('shell.open', { kind: 'url', value: L.url }); }
  catch (err) { toast('error', '打开链接失败：' + String(err.message || err)); return; }
  if (!res || res.ok === false) toast('error', '打开链接失败：' + ((res && res.error) || '宿主无响应'));
}

async function doBuildLocal(say) {
  say('secondary', '>>> 进行本地编译');
  let res = null;
  try { res = await call('build.local', {}); }
  catch (err) { res = { ok: false, error: String(err.message || err) }; }
  if (res && res.ok) {
    toast('success', '已启动本地编译脚本');
    return;
  }
  const msg = (res && res.error) || '宿主无响应';
  toast('error', msg);
}

async function doInstallPatch(say, ctx) {
  const r = await confirmModal('新DLL安装', PATCH_TIP, '开始安装', '');
  if (r.value !== 'yes') return;

  say('secondary', '>>> 新DLL安装：开始安装客户端补丁');
  let res = null;
  try { res = await call('dll.installPatch', {}); }
  catch (err) { res = { ok: false, error: String(err.message || err) }; }

  if (!res || res.ok === false) {
    const msg = (res && res.error) || '安装失败：宿主无响应';
    toast('error', msg);
    return;
  }

  const copied = Number(res.copied) || 0;
  const enabled = Number(res.enabled) || 0;
  const failed = Array.isArray(res.failed) ? res.failed : [];
  if (failed.length) {
    toast('warning', '客户端补丁已安装：复制 ' + copied + ' 个文件，启用插件 ' + enabled + ' 个；有 ' + failed.length + ' 个文件未安装成功（可能被游戏占用）');
  } else {
    toast('success', '客户端补丁已安装：复制 ' + copied + ' 个文件，启用插件 ' + enabled + ' 个');
  }
  // 安装会改 GameGaurd.ini → DLL 页若之后打开会自行拉取；这里只提示日志
  say('success', '>>> [新DLL安装] 客户端补丁已安装: 复制 ' + copied + ' 个文件, 启用插件 ' + enabled + ' 个 → GameGaurd.ini');
  if (failed.length) {
    say('warning', '>>> [新DLL安装] 未安装成功的文件: ' + failed.join(' | '));
  }
}
