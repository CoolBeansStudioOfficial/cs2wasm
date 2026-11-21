import { readFileSync } from 'node:fs';
const buf = readFileSync(process.argv[2]); let p = 8;
function u() { let r = 0, s = 0, b; do { b = buf[p++]; r += (b & 0x7f) * 2 ** s; s += 7; } while (b & 0x80); return r; }
const sizes = [], names = new Map(); let nImp = 0; const secs = [];
while (p < buf.length) { const id = buf[p++]; const len = u(); const end = p + len; let nm = '';
  if (id === 2) { const n = u(); for (let i = 0; i < n; i++) { let l = u(); p += l; l = u(); p += l; const k = buf[p++]; if (k === 0) { u(); nImp++; } else if (k === 1) { p++; const f = u(); u(); if (f & 1) u(); } else if (k === 2) { const f = u(); u(); if (f & 1) u(); } else if (k === 3) { p += 2; } else { p++; u(); } } }
  if (id === 10) { const n = u(); for (let i = 0; i < n; i++) { const l = u(); sizes.push(l); p += l; } }
  if (id === 0) { const nl = u(); nm = buf.toString('utf8', p, p + nl); p += nl; if (nm === 'name') while (p < end) { const sid = buf[p++]; const sl = u(); const se = p + sl; if (sid === 1) { const n = u(); for (let i = 0; i < n; i++) { const idx = u(); const l = u(); names.set(idx, buf.toString('utf8', p, p + l)); p += l; } } p = se; } }
  secs.push(id + (nm ? ':' + nm : '') + ' ' + len); p = end; }
console.error(secs.join('\n'));
for (let i = 0; i < sizes.length; i++) console.log((names.get(i + nImp) ?? 'f' + (i + nImp)) + ' ' + sizes[i]);
