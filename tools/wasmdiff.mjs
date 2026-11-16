// Compares two modules section by section, and the code section function by function (by the names of the name
// section when both have one): what differs first. For checking that the self-hosted compiler's output is the .NET
// build's, byte for byte (SPEC section 7.3).
// usage: node tools/wasmdiff.mjs a.wasm b.wasm [max functions to list]
import { readFileSync } from 'node:fs';

const [fa, fb, maxArg] = process.argv.slice(2);
const max = +(maxArg ?? 10);
const leb = (b, p) => { let r = 0, s = 0, x; do { x = b[p.i++]; r |= (x & 0x7f) << s; s += 7; } while (x & 0x80); return r >>> 0; };
function sections(b) {
  const p = { i: 8 }, out = [];
  while (p.i < b.length) {
    const id = b[p.i++], size = leb(b, p), start = p.i;
    let name = String(id);
    if (id === 0) { const q = { i: start }; const n = leb(b, q); name = 'custom:' + new TextDecoder().decode(b.subarray(q.i, q.i + n)); }
    out.push({ id, name, bytes: b.subarray(start, start + size) });
    p.i = start + size;
  }
  return out;
}
function bodies(code) {
  const p = { i: 0 }, n = leb(code, p), r = [];
  for (let k = 0; k < n; k++) { const size = leb(code, p); r.push(code.subarray(p.i, p.i + size)); p.i += size; }
  return r;
}
function names(secs) {
  const s = secs.find((x) => x.name === 'custom:name');
  if (!s) return null;
  const b = s.bytes, p = { i: 0 }; const n = leb(b, p); p.i += n;
  const m = new Map();
  while (p.i < b.length) {
    const id = b[p.i++], size = leb(b, p), end = p.i + size;
    if (id === 1) { const c = leb(b, p); for (let k = 0; k < c; k++) { const idx = leb(b, p), l = leb(b, p); m.set(idx, new TextDecoder().decode(b.subarray(p.i, p.i + l))); p.i += l; } }
    p.i = end;
  }
  return m;
}
const a = sections(readFileSync(fa)), b = sections(readFileSync(fb));
const keyed = (s) => new Map(s.map((x) => [x.name, x]));
const ka = keyed(a), kb = keyed(b);
for (const k of new Set([...ka.keys(), ...kb.keys()])) {
  const x = ka.get(k), y = kb.get(k);
  const same = x && y && x.bytes.length === y.bytes.length && x.bytes.every((v, i) => v === y.bytes[i]);
  console.log(`${same ? '  ' : '!='} section ${k}: ${x?.bytes.length ?? '-'} / ${y?.bytes.length ?? '-'}`);
}
const ca = ka.get('10'), cb = kb.get('10');
if (ca && cb) {
  const ba = bodies(ca.bytes), bb = bodies(cb.bytes), na = names(a), nb = names(b);
  // imports count (functions before the code section's)
  const imp = (secs) => { const s = secs.find((x) => x.id === 2); if (!s) return 0; const p = { i: 0 }, n = leb(s.bytes, p); let f = 0; for (let k = 0; k < n; k++) { let l = leb(s.bytes, p); p.i += l; l = leb(s.bytes, p); p.i += l; const kind = s.bytes[p.i++]; if (kind === 0) { leb(s.bytes, p); f++; } else if (kind === 1) { p.i++; const fl = s.bytes[p.i++]; leb(s.bytes, p); if (fl & 1) leb(s.bytes, p); } else if (kind === 2) { const fl = s.bytes[p.i++]; leb(s.bytes, p); if (fl & 1) leb(s.bytes, p); } else if (kind === 3) { p.i += 2; } else if (kind === 4) { p.i++; leb(s.bytes, p); } } return f; };
  const ia = imp(a), ib = imp(b);
  console.log(`functions: ${ba.length} / ${bb.length} (imports ${ia} / ${ib})`);
  let shown = 0;
  for (let k = 0; k < Math.max(ba.length, bb.length) && shown < max; k++) {
    const x = ba[k], y = bb[k];
    const same = x && y && x.length === y.length && x.every((v, i) => v === y[i]);
    if (same) continue;
    console.log(`  #${k + ia}: ${na?.get(k + ia) ?? '?'} (${x?.length ?? '-'}) / ${nb?.get(k + ib) ?? '?'} (${y?.length ?? '-'})`);
    shown++;
  }
}
