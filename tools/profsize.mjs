// node tools/profsize.mjs module.wasm profile.cpuprofile (a named build: CS2WASM_NAMES=1)
// Self time by the callee's body size (bytes), from a named build: where calls to small functions cost.
import { readFileSync } from 'node:fs';
const buf = readFileSync(process.argv[2]);
let p = 8;
function u() { let r = 0, s = 0, b; do { b = buf[p++]; r += (b & 0x7f) * 2 ** s; s += 7; } while (b & 0x80); return r; }
let nImports = 0; const bodySize = []; const names = new Map();
while (p < buf.length) {
  const id = buf[p++]; const len = u(); const end = p + len;
  if (id === 2) { const n = u(); for (let i = 0; i < n; i++) { let l = u(); p += l; l = u(); p += l; const k = buf[p++]; if (k === 0) { u(); nImports++; } else if (k === 1) { p++; const f = u(); u(); if (f & 1) u(); } else if (k === 2) { const f = u(); u(); if (f & 1) u(); } else if (k === 3) { p += 2; } else { p++; u(); } } }
  else if (id === 10) { const n = u(); for (let i = 0; i < n; i++) { const l = u(); bodySize.push(l); p += l; } }
  else if (id === 0) {
    const nl = u(); const name = buf.toString('utf8', p, p + nl); p += nl;
    if (name === 'name') while (p < end) { const sid = buf[p++]; const sl = u(); const se = p + sl; if (sid === 1) { const n = u(); for (let i = 0; i < n; i++) { const idx = u(); const l = u(); names.set(buf.toString('utf8', p, p + l), idx); p += l; } } p = se; }
  }
  p = end;
}
const prof = JSON.parse(readFileSync(process.argv[3], 'utf8'));
const self = new Map(); let total = 0;
for (let i = 0; i < prof.samples.length; i++) { const d = prof.timeDeltas[i] ?? 0; self.set(prof.samples[i], (self.get(prof.samples[i]) ?? 0) + d); }
const byName = new Map();
for (const n of prof.nodes) { const t = self.get(n.id) ?? 0; if (!t) continue; total += t; const k = n.callFrame.functionName; byName.set(k, (byName.get(k) ?? 0) + t); }
const buckets = [16, 32, 64, 128, 256, 512, 1024, 4096, Infinity]; const bt = buckets.map(() => 0); const bn = buckets.map(() => 0); let unmatched = 0;
const small = [];
for (const [k, t] of byName) {
  const idx = names.get(k); if (idx === undefined) { unmatched += t; continue; }
  const sz = bodySize[idx - nImports]; const b = buckets.findIndex(x => sz <= x); bt[b] += t; bn[b]++;
  if (sz <= 64) small.push([t, sz, k]);
}
const ms = t => (t / 1000).toFixed(0).padStart(7);
console.log(`total ${ms(total)} ms; not a module function (idle, host, engine) ${ms(unmatched)} ms`);
buckets.forEach((b, i) => console.log(`body <= ${String(b).padStart(8)} bytes: ${ms(bt[i])} ms in ${bn[i]} functions`));
console.log('--- hottest bodies <= 64 bytes');
for (const [t, sz, k] of small.sort((a, b) => b[0] - a[0]).slice(0, 40)) console.log(ms(t), String(sz).padStart(4), k.slice(0, 150));
