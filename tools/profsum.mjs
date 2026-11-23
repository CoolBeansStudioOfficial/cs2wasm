// Sums the self time of V8 CPU profiles (node --cpu-prof, one per thread) by function, and by a coarse category of
// il2wasm's runtime: counting (retain/release, sharing), dispatch stubs, allocation, locks and waits, the rest.
// usage: node tools/profsum.mjs <dir of .cpuprofile> [top N]
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const [dir, topArg] = process.argv.slice(2);
const top = +(topArg ?? 40);
const self = new Map();
let total = 0;
for (const f of readdirSync(dir).filter((f) => f.endsWith('.cpuprofile'))) {
  const p = JSON.parse(readFileSync(join(dir, f), 'utf8'));
  const byId = new Map(p.nodes.map((n) => [n.id, n]));
  const dt = p.timeDeltas;
  for (let i = 0; i < p.samples.length; i++) {
    const n = byId.get(p.samples[i]);
    const name = n.callFrame.functionName || `(${n.callFrame.url.split('/').pop()})`;
    const t = (dt[i] ?? 0) / 1000;
    self.set(name, (self.get(name) ?? 0) + t);
    total += t;
  }
}
const cat = (n) =>
  /^\(idle\)|^\(program\)|^\(garbage collector\)/.test(n) ? 'idle/engine'
  : /^rt\.(retain|release|share|publish)|Sharing\.|Limbo\.|rt\.copy_|rt\.retain_struct|rt\.release_struct|destroy /.test(n) ? 'counting'
  : /^virtual |^interface |dispatch|^rt\.stub/.test(n) ? 'dispatch stubs'
  : /Rt\.Alloc|Rt\.Free|FreeCore|AllocCore|rt\.alloc|AllocObject|AllocArray|AllocString/.test(n) ? 'allocation'
  : /RuntimeLock|RuntimeUnlock|rt\.lock_|Monitor\.|Wait32|WaitGeneration|Atomic\./.test(n) ? 'locks/waits'
  : /^(wait|Atomics)/.test(n) ? 'locks/waits'
  : 'program';
const cats = new Map();
for (const [n, t] of self) cats.set(cat(n), (cats.get(cat(n)) ?? 0) + t);
console.log(`total ${total.toFixed(0)} ms (all threads)`);
for (const [c, t] of [...cats].sort((a, b) => b[1] - a[1])) console.log(`  ${c.padEnd(16)} ${t.toFixed(0).padStart(8)} ms  ${(100 * t / total).toFixed(1)}%`);
console.log('top functions (self):');
for (const [n, t] of [...self].sort((a, b) => b[1] - a[1]).slice(0, top)) console.log(`  ${t.toFixed(0).padStart(7)} ms  ${(100 * t / total).toFixed(1).padStart(5)}%  ${cat(n).padEnd(14)} ${n.slice(0, 140)}`);
