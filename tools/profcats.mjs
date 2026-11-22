// node tools/profcats.mjs ours.cpuprofile naot.cpuprofile [top]
// Self time by kind of code, cs2wasm (a named build) against NativeAOT-LLVM: dispatch stubs, type tests and casts,
// reference counts, allocation and destruction, other runtime, hashing and dictionaries, strings, the rest; then
// each side's busiest functions. Use with profphase.mjs's restricted profiles to see one phase.
import { readFileSync } from 'node:fs';
function load(f) {
  const p = JSON.parse(readFileSync(f, 'utf8'));
  const self = new Map();
  for (let i = 0; i < p.samples.length; i++) { const d = p.timeDeltas[i] ?? 0; if (d) self.set(p.samples[i], (self.get(p.samples[i]) ?? 0) + d); }
  const by = new Map();
  for (const n of p.nodes) { const t = self.get(n.id) ?? 0; if (t) by.set(n.callFrame.functionName, (by.get(n.callFrame.functionName) ?? 0) + t); }
  return by;
}
const cats = [
  ['engine/idle', /^\((idle|program|garbage collector)\)$/],
  ['dispatch stubs', /^virtual |^box thunk|RhpResolveInterface|InterfaceDispatch|RhpCidResolve|CachedInterfaceDispatch|GenericVirtualMethod|GVMLookup|RhResolveDispatch/],
  ['type tests, casts', /^rt\.isinst|^rt\.unbox|^rt\.cast|^rt\.box test|TypeCast|RhTypeCast|IsInstanceOf|CheckCast|ChkCast|AreTypesAssignable|CastCache|RhUnbox/],
  ['counts (retain, release)', /^rt\.(retain|release)|^rt\.release of |^rt\.(retain|release)_struct|^rt\.copy_|^rt\.move_/],
  ['alloc, free, destroy', /^rt\.(alloc|free|destroy)|^destroy |^arena\.|Alloc|RhpNew|RhNew|dlmalloc|dlfree|^malloc|^free$|memset|memcpy|memmove|WKS::|SVR::|gc_heap|GCHeap/],
  ['other runtime', /^rt\.|^Cs2Wasm\.Runtime\.|^Rhp|^Rhu|^Rh[A-Z]|emscripten_|^S_P_CoreLib_Internal_Runtime/],
  ['hashing, dictionaries', /Dictionary|HashSet|GetHashCode|Marvin|Hash\b|HashCode|EqualityComparer|ConcurrentDictionary|ConcurrentCache|SmallDictionary|PooledDictionary|MultiDictionary/],
  ['strings', /System[._]String[._]|SpanHelpers|StringTable|StringComparer/],
  ['host', /^(read|lstat|open|close|fstat|stat|normalize|lookupPath|resolve|get|isAbs|realpath|readdir|wasm-to-js|js-to-wasm|instantiate|compileForInternalLoader)$|^$/],
];
const cat = k => (cats.find(([, r]) => r.test(k)) ?? ['code'])[0];
const ours = load(process.argv[2]), naot = load(process.argv[3]);
const top = +(process.argv[4] ?? 40);
function sum(by) { const c = new Map(); let tot = 0; for (const [k, t] of by) { tot += t; c.set(cat(k), (c.get(cat(k)) ?? 0) + t); } return [c, tot]; }
const [co, to] = sum(ours), [cn, tn] = sum(naot);
const ms = t => (t / 1000).toFixed(0).padStart(7);
const pct = (a, b) => (100 * a / b).toFixed(1).padStart(5) + '%';
console.log(`${'kind'.padEnd(26)} cs2wasm ms      %     naot ms      %`);
for (const k of [...cats.map(c => c[0]), 'code']) console.log(`${k.padEnd(26)} ${ms(co.get(k) ?? 0)} ${pct(co.get(k) ?? 0, to)}   ${ms(cn.get(k) ?? 0)} ${pct(cn.get(k) ?? 0, tn)}`);
console.log(`${'total'.padEnd(26)} ${ms(to)}          ${ms(tn)}`);
for (const [name, by, tot] of [['cs2wasm', ours, to], ['naot', naot, tn]]) {
  console.log(`--- ${name}: busiest (self)`);
  for (const [k, t] of [...by].sort((a, b) => b[1] - a[1]).slice(0, top)) console.log(ms(t), pct(t, tot), cat(k).padEnd(24).slice(0, 24), k.slice(0, 150));
}
