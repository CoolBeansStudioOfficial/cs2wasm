// node tools/profcompare.mjs ours.cpuprofile naot.cpuprofile
// Self time by category and by Roslyn method (Class.Method), il2wasm against NativeAOT-LLVM.
import { readFileSync } from 'node:fs';
function load(f) {
  const p = JSON.parse(readFileSync(f, 'utf8'));
  const self = new Map();
  for (let i = 0; i < p.samples.length; i++) { const d = p.timeDeltas[i] ?? 0; self.set(p.samples[i], (self.get(p.samples[i]) ?? 0) + d); }
  const by = new Map();
  for (const n of p.nodes) { const t = self.get(n.id) ?? 0; if (t) by.set(n.callFrame.functionName, (by.get(n.callFrame.functionName) ?? 0) + t); }
  return by;
}
const ours = load(process.argv[2]), naot = load(process.argv[3]);
function catO(k) {
  if (/^\((idle|program|garbage collector)\)$/.test(k)) return 'engine/idle';
  if (/^(read|lstat|open|close|fstat|stat|normalize|lookupPath|resolve|get|isAbs|realpath|readdir|wasm-to-js|js-to-wasm)$/.test(k) || !k) return 'host';
  if (/^(rt\.|arena\.|destroy |Il2Wasm\.Runtime\.)/.test(k)) return 'runtime (counts, arenas, alloc)';
  return 'code';
}
function catN(k) {
  if (/^\((idle|program|garbage collector)\)$/.test(k)) return 'engine/idle';
  if (/^(read|lstat|open|close|fstat|stat|normalize|lookupPath|resolve|get|isAbs|realpath|readdir|wasm-to-js|js-to-wasm|instantiate|compileForInternalLoader)$/.test(k) || !k) return 'host';
  if (/^(WKS::|SVR::|GCToOS|Rhp|Rhu|RhNew|RhpNew|gc_|GCHeap|memset|memcpy|memmove|dlmalloc|emscripten_)/.test(k)) return 'runtime (GC, alloc)';
  return 'code';
}
function cats(by, cat) { const c = new Map(); let tot = 0; for (const [k, t] of by) { tot += t; c.set(cat(k), (c.get(cat(k)) ?? 0) + t); } return [c, tot]; }
const [co, to] = cats(ours, catO), [cn, tn] = cats(naot, catN);
const ms = t => (t / 1000).toFixed(0).padStart(7);
console.log(`total: il2wasm ${ms(to)}  naot ${ms(tn)}`);
for (const k of new Set([...co.keys(), ...cn.keys()])) console.log(`${k.padEnd(34)} il2wasm ${ms(co.get(k) ?? 0)}  naot ${ms(cn.get(k) ?? 0)}`);
// Roslyn methods: ours "Ns.Class.Method(args)" / naot "Asm_Ns_..._Class__Method[_n]"
function keyO(k) { const m = k.match(/([A-Za-z0-9_`<>]+)\.([A-Za-z0-9_]+)(?:\.(get|set))?\(/); if (!m) { const g = k.match(/([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\.(get|set)$/); return g ? `${g[1]}.get_${g[2]}` : null; } return `${m[1].replace(/<.*$/, '')}.${m[3] ? m[3] + '_' + m[2] : m[2]}`; }
function keyN(k) { const m = k.match(/_([A-Za-z0-9]+)(?:_\d+<[^>]*>)?__([A-Za-z0-9_]+?)(?:_\d+)?$/); return m ? `${m[1]}.${m[2]}` : null; }
const mo = new Map(), mn = new Map();
for (const [k, t] of ours) if (catO(k) === 'code') { const kk = keyO(k); if (kk) mo.set(kk, (mo.get(kk) ?? 0) + t); }
for (const [k, t] of naot) if (catN(k) === 'code') { const kk = keyN(k); if (kk) mn.set(kk, (mn.get(kk) ?? 0) + t); }
let so = 0, sn = 0; const rows = [];
for (const [k, t] of mo) if (mn.has(k)) { so += t; sn += mn.get(k); rows.push([k, t, mn.get(k)]); }
console.log(`\nmethods in both: ${rows.length}; their self time il2wasm ${ms(so)}  naot ${ms(sn)}  ratio ${(so / sn).toFixed(2)}`);
console.log('--- largest il2wasm-minus-naot');
for (const [k, a, b] of rows.sort((x, y) => (y[1] - y[2]) - (x[1] - x[2])).slice(0, 30)) console.log(ms(a), ms(b), (a / b).toFixed(1).padStart(5) + 'x', k);
