/* ==========================================================================
   ui/mode.js — 窗口模式请求（full / mini / classic）
   页面按钮只调用 requestMode()，真正的宿主调用与状态落地在 app.js，
   避免页面与 app.js 互相 import 造成循环依赖。
   ========================================================================== */

let handler = null;

/** app.js 启动时注入。 */
export function initMode(fn) { handler = fn; }

/** 请求切换窗口模式；非法值由 app.js 回落 full。 */
export function requestMode(mode) {
  if (typeof handler === 'function') handler(mode);
}

let getter = null;
/** app.js 启动时注入当前模式读取器（页面如需只读判断可用）。 */
export function initModeGetter(fn) { getter = fn; }
/** 当前模式（只读；未初始化时视为 full）。 */
export function currentMode() { return getter ? getter() : 'full'; }
