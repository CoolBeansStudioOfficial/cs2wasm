// node tinl.mjs trace.txt named.wasm : inlining decisions by callee name (refused for budget, inlined)
import { readFileSync } from 'node:fs';
const buf = readFileSync(process.argv[3]);
let p = 8;
function u() { let r = 0, s = 0, b; do { b = buf[p++]; r += (b & 0x7f) * 2 ** s; s += 7; } while (b & 0x80); return r; }
const names = new Map();
while (p < buf.length) {
  const id = buf[p++]; const len = u(); const end = p + len;
  if (id === 0) { const nl = u(); const name = buf.toString('utf8', p, p + nl); p += nl; if (name === 'name') while (p < end) { const sid = buf[p++]; const sl = u(); const se = p + sl; if (sid === 1) { const n = u(); for (let i = 0; i < n; i++) { const idx = u(); const l = u(); names.set(idx, buf.toString('utf8', p, p + l)); p += l; } } p = se; } }
  p = end;
}
const norm = (n) => (n ?? '?').replace(/<[^<>]*>/g, '<>').replace(/<[^<>]*>/g, '<>').slice(0, 90);
const refused = new Map(), inlined = new Map(), notOften = new Map();
let rs = 0, is = 0;
for (const line of readFileSync(process.argv[2], 'utf8').split('\n')) {
  const m = /considering call #\d+, case #\d+, to function (\d+) \(count=(\d+), size=(\d+)/.exec(line);
  if (!m) continue;
  const k = norm(names.get(+m[1]));
  if (line.includes('not enough inlining budget')) { refused.set(k, (refused.get(k) ?? 0) + 1); rs++; }
  else if (line.includes('decided to inline')) { inlined.set(k, (inlined.get(k) ?? 0) + 1); is++; }
}
const top = (m, n) => [...m].sort((a, b) => b[1] - a[1]).slice(0, n);
console.log(`refused for budget ${rs}, inlined ${is}`);
console.log('--- refused most'); for (const [k, c] of top(refused, 25)) console.log(String(c).padStart(6), k);
console.log('--- inlined most'); for (const [k, c] of top(inlined, 12)) console.log(String(c).padStart(6), k);
