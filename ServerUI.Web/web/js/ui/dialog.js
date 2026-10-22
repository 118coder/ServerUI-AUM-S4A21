/* ==========================================================================
   ui/dialog.js — 宿主询问对话框（协议新增项）
   宿主推送 { type:'ask', payload:{ id, title, text, level, buttons:[{text,value,primary,kind}], input } }
   前端弹出主题化模态，回答 window.__bridge.call('dialog.answer', { id, result, input })
   ========================================================================== */

import { call } from '../bridge.js';
import { openModal } from './modal.js';

const queue = [];
let showing = false;

function next() {
  if (showing || queue.length === 0) return;
  showing = true;
  const ask = queue.shift();
  const opt = {
    title: ask.title || '提示',
    text: ask.text || '',
    level: ask.level || 'info',
    buttons: ask.buttons && ask.buttons.length ? ask.buttons : [{ text: '确定', value: 'ok', primary: true }],
    input: ask.input || null,
  };
  openModal(opt).then(res => {
    call('dialog.answer', { id: ask.id, result: res.value, input: res.input }).catch(() => { });
    showing = false;
    next();
  }).catch(() => {
    call('dialog.answer', { id: ask.id, result: null }).catch(() => { });
    showing = false;
    next();
  });
}

/** 收到宿主 ask 推送时调用。 */
export function handleAsk(payload) {
  if (!payload || payload.id === undefined) return;
  // 宿主已带默认超时；这里立刻入队，保证不阻塞推送线程语义
  queue.push({
    id: payload.id,
    title: payload.title,
    text: payload.text,
    level: payload.level,
    buttons: payload.buttons,
    input: payload.input,
  });
  next();
}
