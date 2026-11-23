// The code size of each function of two modules built with IL2WASM_NAMES=1, by name: what grew, what shrank, what
// came and went. For finding where a change's bytes are.
// usage: node tools/fnsizes.mjs old.wasm new.wasm [lines]
import { readFileSync } from 'node:fs';

const leb = (b, p) => { let r = 0, s = 0, x; do { x = b[p.i++]; r |= (x & 0x7f) << s; s += 7; } while (x & 0x80); return r >>> 0; };
function sizes(file) {
  const b = readFileSync(file), p = { i: 8 };
  let code = null, names = new Map(), imports = 0;
  while (p.i < b.length) {
    const id = b[p.i++], size = leb(b, p), start = p.i;
    if (id === 2) { const q = { i: start }; const n = leb(b, q); for (let k = 0; k < n; k++) { let l = leb(b, q); q.i += l; l = leb(b, q); q.i += l; const kind = b[q.i++]; if (kind === 0) { leb(b, q); imports++; } else if (kind === 1) { q.i++; const f = b[q.i++]; leb(b, q); if (f & 1) leb(b, q); } else if (kind === 2) { const f = b[q.i++]; leb(b, q); if (f & 1) leb(b, q); } else if (kind === 3) q.i += 2; else if (kind === 4) { q.i++; leb(b, q); } } }
    if (id === 10) code = b.subarray(start, start + size);
    if (id === 0) {
      const q = { i: start }; const n = leb(b, q);
      if (new TextDecoder().decode(b.subarray(q.i, q.i + n)) === 'name') {
        q.i += n;
        while (q.i < start + size) {
          const sid = b[q.i++], len = leb(b, q), end = q.i + len;
          if (sid === 1) { const c = leb(b, q); for (let k = 0; k < c; k++) { const idx = leb(b, q), l = leb(b, q); names.set(idx, new TextDecoder().decode(b.subarray(q.i, q.i + l))); q.i += l; } }
          q.i = end;
        }
      }
    }
    p.i = start + size;
  }
  const out = new Map(), q = { i: 0 }, n = leb(code, q);
  for (let k = 0; k < n; k++) { const s = leb(code, q); const name = names.get(imports + k) ?? '#' + (imports + k); out.set(name, (out.get(name) ?? 0) + s); q.i += s; }
  return out;
}
const [fa, fb, maxArg] = process.argv.slice(2);
const a = sizes(fa), b = sizes(fb), rows = [];
for (const [k, v] of b) rows.push([v - (a.get(k) ?? 0), a.get(k) ?? 0, v, k]);
for (const [k, v] of a) if (!b.has(k)) rows.push([-v, v, 0, k]);
rows.sort((x, y) => Math.abs(y[0]) - Math.abs(x[0]));
let total = 0; for (const r of rows) total += r[0];
console.log(`code: ${total >= 0 ? '+' : ''}${total} bytes`);
for (const r of rows.slice(0, +(maxArg ?? 40))) if (r[0] !== 0) console.log(`${String(r[0]).padStart(6)}  ${String(r[1]).padStart(5)} -> ${String(r[2]).padStart(5)}  ${r[3].slice(0, 110)}`);
