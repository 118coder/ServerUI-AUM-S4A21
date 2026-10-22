/* ==========================================================================
   bridge.js — 与宿主（C# NativeBridge）的唯一通道
   协议（§7.1）
     前端 → 宿主: { id, method, args }            JSON 字符串，postMessage
     宿主 → 前端: { id, ok, result|error }        调用应答
     宿主 → 前端: { type, payload }               推送（log/progress/status/...）
   本文件不发起任何网络请求。
   ========================================================================== */

const WV = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

let seq = 0;
const pending = new Map();     // id -> { resolve, reject, timer }
const listeners = new Map();   // type -> Set<fn>

/** 宿主是否可用（无头测试会注入 mock，同样返回 true）。 */
export function isHosted() { return !!WV; }

/** 发送原始报文（仅供本模块与测试使用）。 */
export function post(obj) {
  if (!WV) throw new Error('bridge: 宿主不可用');
  WV.postMessage(JSON.stringify(obj));
}

/**
 * 调用宿主方法。
 * @param {string} method 方法名（§7.2）
 * @param {object} [args] 参数
 * @param {{timeout?: number}} [opts] timeout=0 表示不设超时
 * @returns {Promise<any>}
 */
export function call(method, args, opts) {
  const timeout = opts && typeof opts.timeout === 'number' ? opts.timeout : 30000;
  return new Promise((resolve, reject) => {
    if (!WV) { reject(new Error('bridge: 宿主不可用（' + method + '）')); return; }
    // id 用字符串：宿主 JSON 解析后按字符串回显，数字 id 会被回成字符串而匹配不上
    const id = 'c' + (++seq);
    let timer = null;
    if (timeout > 0) {
      timer = setTimeout(() => {
        pending.delete(id);
        reject(new Error('bridge: 调用超时（' + method + '）'));
      }, timeout);
    }
    pending.set(id, { resolve, reject, timer, method });
    try {
      post({ id, method, args: args || {} });
    } catch (err) {
      pending.delete(id);
      if (timer) clearTimeout(timer);
      reject(err);
    }
  });
}

/** 订阅宿主推送。返回取消订阅函数。 */
export function on(type, fn) {
  if (!listeners.has(type)) listeners.set(type, new Set());
  listeners.get(type).add(fn);
  return () => off(type, fn);
}

export function off(type, fn) {
  const set = listeners.get(type);
  if (set) set.delete(fn);
}

function emit(type, payload) {
  const set = listeners.get(type);
  if (!set || set.size === 0) return;
  for (const fn of Array.from(set)) {
    try { fn(payload); }
    catch (err) { console.error('[bridge] 推送处理异常 ' + type, err); }
  }
}

function onMessage(ev) {
  let data = ev && ev.data;
  if (typeof data === 'string') {
    try { data = JSON.parse(data); } catch { return; }
  }
  if (!data || typeof data !== 'object') return;

  // 调用应答
  if (Object.prototype.hasOwnProperty.call(data, 'id') && data.id !== null && data.id !== undefined) {
    const slot = pending.get(data.id);
    if (!slot) return;
    pending.delete(data.id);
    if (slot.timer) clearTimeout(slot.timer);
    if (data.ok) slot.resolve(data.result);
    else slot.reject(new Error(data.error || '宿主返回失败'));
    return;
  }

  // 推送
  if (data.type) emit(data.type, data.payload || {});
}

if (WV) WV.addEventListener('message', onMessage);

/** 页面卸载时清空挂起调用，避免退出时悬挂 Promise。 */
window.addEventListener('beforeunload', () => {
  for (const [, slot] of pending) {
    if (slot.timer) clearTimeout(slot.timer);
    try { slot.reject(new Error('bridge: 页面正在关闭')); } catch { }
  }
  pending.clear();
});
