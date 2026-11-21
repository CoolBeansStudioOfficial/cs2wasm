// node generics.mjs named.wasm : code of generic instantiations by definition family. A family is the name with its
// type arguments taken out (Foo<A,B>.M<C>(x) -> Foo<,>.M<>(x-with-args-out)) plus the wasm signature. Bytes beyond
// each family's largest member = what one shared body per family would save (an upper bound: over value types bodies
// differ for real). "ref-only": members whose type arguments all look like reference types.
import { readFileSync } from 'node:fs';
const buf = readFileSync(process.argv[2]);
let p = 8;
function u() { let r = 0, s = 0, b; do { b = buf[p++]; r += (b & 0x7f) * 2 ** s; s += 7; } while (b & 0x80); return r; }
let nImports = 0; const sizes = []; const ftype = []; const names = new Map(); const types = [];
while (p < buf.length) {
  const id = buf[p++]; const len = u(); const end = p + len;
  if (id === 1) { const n = u(); for (let i = 0; i < n; i++) { p++; const np = u(); const ps = []; for (let k = 0; k < np; k++) ps.push(buf[p++]); const nr = u(); const rs = []; for (let k = 0; k < nr; k++) rs.push(buf[p++]); types.push(ps.join(',') + '>' + rs.join(',')); } }
  else if (id === 2) { const n = u(); for (let i = 0; i < n; i++) { let l = u(); p += l; l = u(); p += l; const k = buf[p++]; if (k === 0) { u(); nImports++; } else if (k === 1) { p++; const f = u(); u(); if (f & 1) u(); } else if (k === 2) { const f = u(); u(); if (f & 1) u(); } else if (k === 3) { p += 2; } else { p++; u(); } } }
  else if (id === 3) { const n = u(); for (let i = 0; i < n; i++) ftype.push(u()); }
  else if (id === 10) { const n = u(); for (let i = 0; i < n; i++) { const l = u(); sizes.push(l); p += l; } }
  else if (id === 0) { const nl = u(); const name = buf.toString('utf8', p, p + nl); p += nl; if (name === 'name') while (p < end) { const sid = buf[p++]; const sl = u(); const se = p + sl; if (sid === 1) { const n = u(); for (let i = 0; i < n; i++) { const idx = u(); const l = u(); names.set(idx, buf.toString('utf8', p, p + l)); p += l; } } p = se; } }
  p = end;
}
const valueish = /^(int|long|short|byte|sbyte|uint|ulong|ushort|char|bool|float|double|decimal|nint|nuint|System\.Int|System\.UInt|System\.Byte|System\.Char|System\.Boolean|System\.Double|System\.Single|\(|System\.ValueTuple|System\.Collections\.Generic\.KeyValuePair|System\.Collections\.Immutable\.ImmutableArray|Microsoft\.CodeAnalysis\.SyntaxToken$|Microsoft\.CodeAnalysis\.SyntaxNodeOrToken|Microsoft\.CodeAnalysis\.SyntaxList|Microsoft\.CodeAnalysis\.SeparatedSyntaxList|Microsoft\.CodeAnalysis\.TypedConstant|Microsoft\.CodeAnalysis\.CSharp\.Symbols\.TypeWithAnnotations|Microsoft\.CodeAnalysis\.CSharp\.SyntaxKind|Microsoft\.CodeAnalysis\.ArrayElement|System\.ReadOnlyMemory|System\.Memory|System\.Span|System\.ReadOnlySpan|System\.Nullable)/;
function split(args) { const out = []; let d = 0, s = 0; for (let i = 0; i < args.length; i++) { const c = args[i]; if (c === '<' || c === '(') d++; else if (c === '>' || c === ')') d--; else if (c === ',' && d === 0) { out.push(args.slice(s, i).trim()); s = i + 1; } } out.push(args.slice(s).trim()); return out; }
function family(name) {
  // strip every <...> (outermost groups) to <> and collect the arguments
  let out = '', args = [], d = 0, start = -1;
  const head = name.split('(')[0];
  for (let i = 0; i < head.length; i++) {
    const c = head[i];
    if (c === '<') { if (d === 0) { start = i + 1; out += '<'; } d++; }
    else if (c === '>') { d--; if (d === 0) { args.push(...split(head.slice(start, i))); out += '>'; } }
    else if (d === 0) out += c;
  }
  return [out, args];
}
const fam = new Map();
let genBytes = 0, genFns = 0;
for (let i = 0; i < sizes.length; i++) {
  const nm = names.get(i + nImports); if (!nm || !nm.includes('<') || /^(rt\.|arena\.|destroy |virtual |interface |static init)/.test(nm)) continue;
  const [f, args] = family(nm); if (args.length === 0) continue;
  const refOnly = args.every((a) => !valueish.test(a));
  const key = f + ' | ' + types[ftype[i]];
  const e = fam.get(key) ?? { n: 0, bytes: 0, max: 0, refN: 0, refBytes: 0, refMax: 0 };
  e.n++; e.bytes += sizes[i]; e.max = Math.max(e.max, sizes[i]);
  if (refOnly) { e.refN++; e.refBytes += sizes[i]; e.refMax = Math.max(e.refMax, sizes[i]); }
  fam.set(key, e); genBytes += sizes[i]; genFns++;
}
let all = 0, ref = 0;
for (const e of fam.values()) { all += e.bytes - e.max; if (e.refN > 1) ref += e.refBytes - e.refMax; }
const total = sizes.reduce((a, b) => a + b, 0);
console.log(`code ${(total / 1e6).toFixed(1)} MB; generic instantiations ${genFns} functions, ${(genBytes / 1e6).toFixed(1)} MB in ${fam.size} families`);
console.log(`one body per family (upper bound): -${(all / 1e6).toFixed(1)} MB; over reference type arguments only: -${(ref / 1e6).toFixed(1)} MB`);
const byRoot = new Map();
for (const [k, e] of fam) { const root = k.split(' | ')[0].replace(/\.[^.<]*(<>)?$/, ''); const r = byRoot.get(root) ?? { n: 0, save: 0 }; r.n += e.refN; r.save += e.refN > 1 ? e.refBytes - e.refMax : 0; byRoot.set(root, r); }
console.log('--- types whose instantiations over reference types would share most');
for (const [k, r] of [...byRoot].sort((a, b) => b[1].save - a[1].save).slice(0, +(process.argv[3] ?? 25))) console.log((r.save / 1e3).toFixed(0).padStart(6), 'KB', String(r.n).padStart(6), 'fns ', k.slice(0, 100));
