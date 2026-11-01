// Author: Codex (GPT-6), 2026-10-21
'use strict';
const http = require('node:http');
function formatDescription(value) {
  if (!value || value.kind !== 'OurNotesLive2D' || !value.libraries) return null;
  const a = value.libraries.bdon, b = value.libraries.gbp;
  if (!a || !b || a.cubism !== 4 || b.cubism !== 2 ||
      !Number.isInteger(a.models) || a.models < 1 ||
      !Number.isInteger(b.models) || b.models < 0 ||
      value.models !== a.models + b.models) return null;
  return 'Our Notes ' + a.models + ' 个模型；少女乐团派对 ' + b.models +
    ' 个模型；切换角色、服装、动作和表情。';
}
function readHealth() {
  return new Promise(resolve => {
    let finished = false, timer;
    const finish = value => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      resolve(value);
    };
    const request = http.get({
      host: '127.0.0.1', port: 8999, path: '/api/health',
      headers: { accept: 'application/json' }
    }, response => {
      if (response.statusCode !== 200) { response.resume(); return finish(null); }
      let length = 0, text = '';
      response.setEncoding('utf8');
      response.on('data', chunk => {
        length += Buffer.byteLength(chunk);
        if (length > 16384) { request.destroy(); finish(null); }
        else text += chunk;
      });
      response.on('end', () => {
        try { finish(JSON.parse(text)); } catch { finish(null); }
      });
      response.on('error', () => finish(null));
    });
    timer = setTimeout(() => { request.destroy(); finish(null); }, 650);
    request.on('error', () => finish(null));
  });
}
async function describeLive2d(service) {
  if (service.id !== 'live2d') return service.desc;
  return formatDescription(await readHealth()) || service.desc;
}
module.exports = { describeLive2d, formatDescription };
