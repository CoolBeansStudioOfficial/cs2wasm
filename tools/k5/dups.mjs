// node dups.mjs a.wasm : identical function bodies (bytes), and identical up to calling functions that are themselves
// identical (call targets replaced by their class, iterated to a fixpoint): how many functions and bytes could go
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
const buf = readFileSync(process.argv[2]);
let p = 8;
function u() { let r = 0, s = 0, b; do { b = buf[p++]; r += (b & 0x7f) * 2 ** s; s += 7; } while (b & 0x80); return r; }
let nImports = 0; const bodies = []; const types = []; let funcTypes = [];
while (p < buf.length) {
  const id = buf[p++]; const len = u(); const end = p + len;
  if (id === 2) { const n = u(); for (let i = 0; i < n; i++) { let l = u(); p += l; l = u(); p += l; const k = buf[p++]; if (k === 0) { u(); nImports++; } else if (k === 1) { p++; const f = u(); u(); if (f & 1) u(); } else if (k === 2) { const f = u(); u(); if (f & 1) u(); } else if (k === 3) { p += 2; } else { p++; u(); } } }
  else if (id === 3) { const n = u(); for (let i = 0; i < n; i++) funcTypes.push(u()); }
  else if (id === 10) { const n = u(); for (let i = 0; i < n; i++) { const l = u(); bodies.push(buf.subarray(p, p + l)); p += l; } }
  p = end;
}
// call sites: scan for 0x10 opcodes is ambiguous inside immediates; approximate by decoding only call targets heuristically is unsafe,
// so the second measure re-encodes nothing: it hashes bodies with every LEB after a 0x10 byte replaced by its target's class
// only where the byte stream parses as instructions (a simple decoder for the opcodes the compiler emits)
function scanCalls(b) {
  const calls = []; let q = 0;
  function U() { let r = 0, s = 0, c; do { c = b[q++]; r += (c & 0x7f) * 2 ** s; s += 7; } while (c & 0x80); return r; }
  function S() { let c; do { c = b[q++]; } while (c & 0x80); }
  const groups = U(); for (let g = 0; g < groups; g++) { U(); q++; }
  while (q < b.length) {
    const start = q; const op = b[q++];
    if (op === 0x10) { const t0 = q; const t = U(); calls.push([t0, q, t]); }
    else if (op === 0x41 && process.env.MASK_CONST) { const t0 = q; S(); calls.push([t0, q, -1]); }
    else if (op === 0x11) { U(); U(); }
    else if (op === 0x02 || op === 0x03 || op === 0x04) { const bt = b[q]; if (bt === 0x40 || bt >= 0x7b) q++; else S(); }
    else if (op === 0x0c || op === 0x0d || (op >= 0x20 && op <= 0x24)) U();
    else if (op === 0x0e) { const n = U(); for (let i = 0; i <= n; i++) U(); }
    else if (op === 0x08) U();
    else if (op === 0x1f) { const bt = b[q]; if (bt === 0x40 || bt >= 0x7b) q++; else S(); const n = U(); for (let i = 0; i < n; i++) { const k = b[q++]; if (k < 2) U(); U(); } }
    else if (op >= 0x28 && op <= 0x3e) { U(); U(); }
    else if (op === 0x3f || op === 0x40) q++;
    else if (op === 0x41 || op === 0x42) S();
    else if (op === 0x43) q += 4;
    else if (op === 0x44) q += 8;
    else if (op === 0xfc) { const sub = U(); if (sub === 8) { U(); q++; } else if (sub === 9) U(); else if (sub === 10) q += 2; else if (sub === 11) q++; }
    else if (op === 0xfd) { const sub = U(); if (sub <= 11) { U(); U(); } else if (sub === 12) q += 16; }
    else if (op === 0xfe) { U(); if (b[q - 1] !== 3) { U(); U(); } }
    else if (op === 0x1c) { const n = U(); q += n; }
  }
  return calls;
}
const n = bodies.length;
const sum = (a) => a.reduce((x, y) => x + y, 0);
function classes(key) { const m = new Map(); const cls = new Array(n); for (let i = 0; i < n; i++) { const k = key(i); if (!m.has(k)) m.set(k, m.size); cls[i] = m.get(k); } return [cls, m.size]; }
let [cls, count] = classes((i) => funcTypes[i] + ':' + createHash('sha1').update(bodies[i]).digest('hex'));
const saved = (c) => { const first = new Set(); let b = 0, f = 0; for (let i = 0; i < n; i++) { if (first.has(c[i])) { b += bodies[i].length; f++; } else first.add(c[i]); } return [f, b]; };
let [f1, b1] = saved(cls);
console.log(`${n} functions, ${(sum(bodies.map((x) => x.length)) / 1e6).toFixed(2)} MB; byte-identical: ${f1} functions, ${(b1 / 1e6).toFixed(2)} MB could go`);
const calls = bodies.map(scanCalls);
for (let it = 0; it < 8; it++) {
  const prev = count;
  [cls, count] = classes((i) => {
    const b = bodies[i]; const parts = []; let last = 0;
    for (const [s, e, t] of calls[i]) { parts.push(b.subarray(last, s).toString('latin1')); parts.push(t === -1 ? '#k' : '#' + (t < nImports ? 'i' + t : 'c' + cls[t - nImports])); last = e; }
    parts.push(b.subarray(last).toString('latin1'));
    return funcTypes[i] + ':' + createHash('sha1').update(parts.join('')).digest('hex');
  });
  if (count === prev) break;
}
let [f2, b2] = saved(cls);
console.log(`identical up to calls of identical functions: ${f2} functions, ${(b2 / 1e6).toFixed(2)} MB could go`);
