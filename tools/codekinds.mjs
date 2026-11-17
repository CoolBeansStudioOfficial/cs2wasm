// Where a module's code is, by kind of function (a module built with CS2WASM_NAMES=1): dispatch stubs, destroy
// functions, initializers, copies made for ownership or constants, the namespaces of the rest; and its largest
// functions. For the size of large programs (the self-hosted compiler).
// usage: node tools/codekinds.mjs module.wasm [largest N]
import { readFileSync } from 'node:fs';

const leb = (b, p) => { let r = 0, s = 0, x; do { x = b[p.i++]; r += (x & 0x7f) * 2 ** s; s += 7; } while (x & 0x80); return r; };
const b = readFileSync(process.argv[2]), p = { i: 8 };
let code = null, imports = 0;
const names = new Map(), sections = [];
while (p.i < b.length) {
  const id = b[p.i++], size = leb(b, p), start = p.i;
  let label = String(id);
  if (id === 2) { const q = { i: start }; const n = leb(b, q); for (let k = 0; k < n; k++) { let l = leb(b, q); q.i += l; l = leb(b, q); q.i += l; const kind = b[q.i++]; if (kind === 0) { leb(b, q); imports++; } else if (kind === 1) { q.i++; const f = b[q.i++]; leb(b, q); if (f & 1) leb(b, q); } else if (kind === 2) { const f = b[q.i++]; leb(b, q); if (f & 1) leb(b, q); } else if (kind === 3) q.i += 2; else if (kind === 4) { q.i++; leb(b, q); } } }
  if (id === 10) code = b.subarray(start, start + size);
  if (id === 0) {
    const q = { i: start }; const n = leb(b, q);
    label = 'custom:' + new TextDecoder().decode(b.subarray(q.i, q.i + n));
    if (label === 'custom:name') {
      q.i += n;
      while (q.i < start + size) {
        const sid = b[q.i++], len = leb(b, q), end = q.i + len;
        if (sid === 1) { const c = leb(b, q); for (let k = 0; k < c; k++) { const idx = leb(b, q), l = leb(b, q); names.set(idx, new TextDecoder().decode(b.subarray(q.i, q.i + l))); q.i += l; } }
        q.i = end;
      }
    }
  }
  sections.push([label, size]);
  p.i = start + size;
}
console.log('sections: ' + sections.map(([l, s]) => `${l} ${(s / 1048576).toFixed(2)} MB`).filter((x) => !x.endsWith(' 0.00 MB')).join(', '));
const kindOf = (n) =>
  n.startsWith('virtual ') ? 'dispatch stubs'
  : /^destroy |^arena\.|^rt\.release|^rt\.retain_struct|^rt\.release_struct|^rt\.release_range|^rt\.retain_range|^rt\.copy|^rt\.move/.test(n) ? 'destroy, release and copy functions'
  : n.startsWith('static init') ? 'static initializers'
  : n.startsWith('rt.') || n.startsWith('Cs2Wasm.Runtime.') ? 'runtime'
  : n.startsWith('thunk ') || n.startsWith('frameobj.') || n.startsWith('export ') ? 'thunks'
  : /\[own \d+\]/.test(n) ? 'copies: owned arguments'
  : / \[this by value\]$/.test(n) ? 'copies: this by value'
  : /\[\d+:[^\]]*\]$/.test(n) ? 'copies: constant arguments'
  : n.startsWith('Microsoft.CodeAnalysis.CSharp') ? 'Microsoft.CodeAnalysis.CSharp'
  : n.startsWith('Microsoft.CodeAnalysis') ? 'Microsoft.CodeAnalysis'
  : n.startsWith('System.Collections') ? 'System.Collections'
  : n.startsWith('System.Linq') ? 'System.Linq'
  : n.startsWith('System.') ? 'System (other)'
  : n.startsWith('Cs2Wasm.') ? 'Cs2Wasm'
  : n.startsWith('local ') || n.startsWith('lambda ') ? 'lambdas and local functions'
  : 'other';
const kinds = new Map(), all = [];
{
  const q = { i: 0 }, n = leb(code, q);
  for (let k = 0; k < n; k++) {
    const s = leb(code, q), name = names.get(imports + k) ?? '#' + (imports + k);
    const kd = kindOf(name), e = kinds.get(kd) ?? [0, 0];
    e[0] += s; e[1]++; kinds.set(kd, e);
    all.push([s, name]);
    q.i += s;
  }
}
console.log(`code: ${(code.length / 1048576).toFixed(2)} MB in ${all.length} functions`);
for (const [k, [s, c]] of [...kinds].sort((x, y) => y[1][0] - x[1][0])) console.log(`${(s / 1048576).toFixed(2).padStart(8)} MB ${String(c).padStart(7)} fns ${String(Math.round(s / c)).padStart(6)} avg  ${k}`);
all.sort((x, y) => y[0] - x[0]);
for (const [s, n] of all.slice(0, +(process.argv[3] ?? 25))) console.log(`${String(s).padStart(8)}  ${n.slice(0, 130)}`);
