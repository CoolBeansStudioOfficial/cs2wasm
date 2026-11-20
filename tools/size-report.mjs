// Where a module's bytes are: its sections; its code by kind of function (user code by namespace and type, generic
// instantiations, runtime helpers, destroy and arena functions, dispatch stubs, reflection, initializers, copies);
// and its data segment (expanded when packed) split into string literals, manifest resources and the rest (type
// tables, reflection records, blobs). For the size of large programs (the self-hosted compiler).
// The module must carry a name section for every function: build it with CS2WASM_NAMES=1.
// usage: node tools/size-report.mjs module.wasm [--rsp <the build's .rsp, to find its resources>] [--top N]
//        [--group <prefix>] (types under one namespace prefix, by type)
import { readFileSync, existsSync } from 'node:fs';

const args = process.argv.slice(2);
const file = args[0];
const opt = (k, d) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : d; };
const top = +opt('--top', 30), group = opt('--group', null);
const rsp = opt('--rsp', existsSync(file + '.rsp') ? file + '.rsp' : null);

const leb = (b, p) => { let r = 0, s = 0, x; do { x = b[p.i++]; r += (x & 0x7f) * 2 ** s; s += 7; } while (x & 0x80); return r; };
const b = readFileSync(file), p = { i: 8 };
let code = null, imports = 0, data = [];
const names = new Map(), sections = [];
const td = new TextDecoder();
while (p.i < b.length) {
  const id = b[p.i++], size = leb(b, p), start = p.i;
  let label = ['custom', 'type', 'import', 'function', 'table', 'memory', 'global', 'export', 'start', 'element', 'code', 'data', 'datacount', 'tag'][id] ?? String(id);
  if (id === 2) { const q = { i: start }; const n = leb(b, q); for (let k = 0; k < n; k++) { let l = leb(b, q); q.i += l; l = leb(b, q); q.i += l; const kind = b[q.i++]; if (kind === 0) { leb(b, q); imports++; } else if (kind === 1) { q.i++; const f = b[q.i++]; leb(b, q); if (f & 1) leb(b, q); } else if (kind === 2) { const f = b[q.i++]; leb(b, q); if (f & 1) leb(b, q); } else if (kind === 3) q.i += 2; else if (kind === 4) { q.i++; leb(b, q); } } }
  if (id === 10) code = b.subarray(start, start + size);
  if (id === 11) {
    const q = { i: start }, n = leb(b, q);
    for (let k = 0; k < n; k++) {
      const flags = leb(b, q);
      if (flags === 0 || flags === 2) { if (flags === 2) leb(b, q); while (b[q.i++] !== 0x0b); }
      const len = leb(b, q); data.push(b.subarray(q.i, q.i + len)); q.i += len;
    }
  }
  if (id === 0) {
    const q = { i: start }; const n = leb(b, q);
    label = 'custom:' + td.decode(b.subarray(q.i, q.i + n));
    if (label === 'custom:name') {
      q.i += n;
      while (q.i < start + size) {
        const sid = b[q.i++], len = leb(b, q), end = q.i + len;
        if (sid === 1) { const c = leb(b, q); for (let k = 0; k < c; k++) { const idx = leb(b, q), l = leb(b, q); names.set(idx, td.decode(b.subarray(q.i, q.i + l))); q.i += l; } }
        q.i = end;
      }
    }
  }
  sections.push([label, size]);
  p.i = start + size;
}
const MB = (n) => (n / 1048576).toFixed(2).padStart(7) + ' MB';
const pct = (n, t) => (100 * n / t).toFixed(1).padStart(5) + '%';
console.log(`${file}: ${MB(b.length)}`);
for (const [l, s] of sections) if (s > 1024) console.log(`  ${MB(s)} ${pct(s, b.length)}  ${l}`);
if (names.size < 1000) console.log('(few function names: build with CS2WASM_NAMES=1 for the code breakdown)');

// ---- code ----
const fns = [];
{ const q = { i: 0 }, n = leb(code, q); for (let k = 0; k < n; k++) { const s = leb(code, q); fns.push([s, names.get(imports + k) ?? '#' + (imports + k)]); q.i += s; } }
const totalCode = code.length;
// what a function is, by its compiler name (Driver.Display, RuntimeGen, Reflection)
const kindOf = (n) =>
  n.startsWith('virtual ') ? 'dispatch stubs (virtual/interface)'
  : /^destroy |^rt\.destroy|^rt\.release|^rt\.retain|^rt\.copy|^rt\.move/.test(n) ? 'destroy, retain, release, copy'
  : /^arena\.|^rt\.arena/.test(n) ? 'arena (cycle) machinery'
  : /^rt\.isinst|^rt\.cast|^rt\.type|^rt\.meta|^rt\.reflect|^rt\.manifest|^rt\.assembl|^reflect|^invoke /.test(n) ? 'type tests, reflection'
  : n.startsWith('static init') ? 'static initializers'
  : n.startsWith('rt.') || n.startsWith('Cs2Wasm.Runtime.') ? 'runtime helpers'
  : n.startsWith('thunk ') || n.startsWith('frameobj.') || n.startsWith('export ') ? 'thunks'
  : /\[own \d+\]/.test(n) || / \[this by value\]$/.test(n) || /\[\d+:[^\]]*\]$/.test(n) ? 'specialized copies'
  : n.startsWith('MoveNext of ') ? 'iterators, async state machines'
  : n.startsWith('local ') || n.startsWith('lambda ') ? 'lambdas, local functions'
  : n.startsWith('#') ? 'unnamed'
  : /[<`]/.test(n.split('(')[0]) ? 'methods: generic instantiations'
  : 'methods';
// the type a method belongs to: its name up to the method (generic arguments taken off)
const strip = (s) => { let o = '', d = 0; for (const c of s) { if (c === '<' || c === '[') d++; else if (c === '>' || c === ']') d--; else if (d === 0) o += c; } return o; };
const base = (n) => n.replace(/^(MoveNext of |static init body |static init |virtual |destroy |thunk |local |lambda )/, '');
const typeOf = (n) => { const head = strip(base(n).split('(')[0]); const parts = head.split('.'); parts.pop(); return parts.join('.').replace(/\+.*/, '') || '(none)'; };
const nsOf = (n) => { const t = typeOf(n).split('.'); t.pop(); return t.slice(0, 3).join('.') || '(none)'; };
const tally = (key) => { const m = new Map(); for (const [s, n] of fns) { const k = key(n); if (k == null) continue; const e = m.get(k) ?? [0, 0]; e[0] += s; e[1]++; m.set(k, e); } return [...m].sort((x, y) => y[1][0] - x[1][0]); };
const show = (title, rows, n = top) => {
  console.log(`\n${title}`);
  for (const [k, [s, c]] of rows.slice(0, n)) console.log(`  ${MB(s)} ${pct(s, totalCode)} ${String(c).padStart(7)} fns ${String(Math.round(s / c)).padStart(6)} avg  ${k.slice(0, 120)}`);
};
console.log(`\ncode: ${MB(totalCode)} in ${fns.length} functions`);
show('by kind', tally(kindOf), 99);
show('by namespace (methods of every kind)', tally(nsOf));
show(group ? `types under ${group}` : 'by type', tally((n) => !group || typeOf(n).startsWith(group) ? typeOf(n) : null));
// generic methods and types instantiated many times: the same source method, its copies' total
show('one source method, its instantiations and copies (count > 1)', tally((n) => strip(base(n).split('(')[0]) + '(..)').filter(([, [, c]]) => c > 1));
console.log('\nlargest functions');
for (const [s, n] of [...fns].sort((x, y) => y[0] - x[0]).slice(0, top)) console.log(`  ${String(s).padStart(8)}  ${n.slice(0, 130)}`);

// ---- data ----
let raw = Buffer.concat(data);
const packed = [...names.values()].includes('rt.unpack_data');
if (packed) {
  // DataCompression: (literal count, literals, match length - 4, distance)..., a last literal run
  const o = [], q = { i: 0 };
  while (q.i < raw.length) {
    const n = leb(raw, q); for (let k = 0; k < n; k++) o.push(raw[q.i++]);
    if (q.i >= raw.length) break;
    const len = leb(raw, q) + 4, dist = leb(raw, q);
    for (let k = 0; k < len; k++) o.push(o[o.length - dist]);
  }
  raw = Buffer.from(o);
}
console.log(`\ndata: ${MB(data.reduce((a, d) => a + d.length, 0))} in the module${packed ? `, ${MB(raw.length)} expanded` : ''}`);
const owner = new Uint8Array(raw.length); // 0 rest, 1 string literal, 2 resource
// string literals: rc -1, type 1, hash 0, length, UTF-16 chars, terminator (Driver.StringLiteral), at 8-aligned offsets
let lits = 0, litBytes = 0;
for (let i = 0; i + 16 <= raw.length; i += 8) {
  if (raw.readInt32LE(i) !== -1 || raw.readInt32LE(i + 4) !== 1 || raw.readInt32LE(i + 8) !== 0) continue;
  const len = raw.readInt32LE(i + 12), end = i + 16 + 2 * len + 2;
  if (len < 0 || end > raw.length || raw[end - 1] !== 0 || raw[end - 2] !== 0) continue;
  owner.fill(1, i, end); lits++; litBytes += end - i; i = (end + 7 & ~7) - 8;
}
// manifest resources: the files the build named with --resource, found by their contents
const res = [];
if (rsp) {
  const lines = readFileSync(rsp, 'utf8').split(/\r?\n/);
  for (let k = 0; k < lines.length; k++) if (lines[k] === '--resource') {
    const v = lines[k + 1], eq = v.indexOf('='), name = v.slice(0, eq);
    let path = v.slice(eq + 1); if (!existsSync(path) && existsSync('C:' + path)) path = 'C:' + path;
    if (!existsSync(path)) continue;
    const bytes = readFileSync(path);
    const at = bytes.length >= 16 ? raw.indexOf(bytes) : -1;
    if (at >= 0) { owner.fill(2, at, at + bytes.length); res.push([bytes.length, name]); }
  }
}
// the libraries' compiled .resx resources (System.Resources' format starts with 0xBEEFCACE): how many
let resx = 0;
for (let i = raw.indexOf(Buffer.from([0xce, 0xca, 0xef, 0xbe])); i >= 0; i = raw.indexOf(Buffer.from([0xce, 0xca, 0xef, 0xbe]), i + 4)) if (!owner[i]) resx++;
const count = (v) => { let n = 0; for (let i = 0; i < owner.length; i++) if (owner[i] === v) n++; return n; };
const resBytes = count(2), rest = count(0);
console.log(`  ${MB(litBytes)} ${pct(litBytes, raw.length)}  string literals (${lits})`);
console.log(`  ${MB(resBytes)} ${pct(resBytes, raw.length)}  manifest resources found by content (${res.length})${rsp ? '' : ' (no .rsp given)'}`);
console.log(`  ${MB(rest)} ${pct(rest, raw.length)}  the rest: type tables, reflection records, .resx resources (${resx} found), blobs`);
const rg = new Map(); for (const [s, n] of res) { const k = n.split('/').slice(0, 2).join('/'); rg.set(k, (rg.get(k) ?? 0) + s); }
for (const [k, s] of [...rg].sort((x, y) => y[1] - x[1]).slice(0, 12)) console.log(`    ${MB(s)}  ${k}`);
// the longest string literals
const longest = [];
for (let i = 0; i + 16 <= raw.length; i += 8) if (owner[i] === 1 && (i === 0 || owner[i - 1] !== 1 || raw.readInt32LE(i) === -1 && raw.readInt32LE(i + 4) === 1 && raw.readInt32LE(i + 8) === 0)) {
  if (raw.readInt32LE(i) !== -1) continue;
  const len = raw.readInt32LE(i + 12); if (len > 2000) longest.push([len, raw.subarray(i + 16, i + 16 + Math.min(80, len) * 2).toString('utf16le')]);
}
if (longest.length) { console.log('  string literals over 2,000 chars:'); for (const [l, s] of longest.sort((x, y) => y[0] - x[0]).slice(0, 10)) console.log(`    ${String(l).padStart(8)}  ${JSON.stringify(s).slice(0, 90)}`); }
