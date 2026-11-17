// A profile's time under a function (itself and everything it calls), split by what it spent it in: for one phase of
// a run (the final pass of the self-hosted compiler: `Cs2Wasm.Wasm.WasmModule.Encode`), which functions took the time.
// usage: node tools/profincl.mjs <profile dir> <function name contains> [lines]
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const [dir, want, linesArg] = process.argv.slice(2);
const lines = +(linesArg ?? 40);
const self = new Map();
let total = 0, runs = 0;
for (const f of readdirSync(dir).filter((x) => x.endsWith('.cpuprofile'))) {
  const p = JSON.parse(readFileSync(join(dir, f), 'utf8'));
  const byId = new Map(p.nodes.map((n) => [n.id, n]));
  const parent = new Map();
  for (const n of p.nodes) for (const c of n.children ?? []) parent.set(c, n.id);
  const dt = p.timeDeltas, under = new Map();
  const isUnder = (id) => {
    if (under.has(id)) return under.get(id);
    let r = false;
    for (let x = id; x !== undefined; x = parent.get(x)) if (byId.get(x).callFrame.functionName.includes(want)) { r = true; break; }
    under.set(id, r);
    return r;
  };
  for (let i = 0; i < p.samples.length; i++) {
    const id = p.samples[i];
    if (!isUnder(id)) continue;
    const name = byId.get(id).callFrame.functionName || '(anonymous)';
    self.set(name, (self.get(name) ?? 0) + dt[i]);
    total += dt[i];
  }
  runs++;
}
console.log(`under "${want}": ${(total / 1000).toFixed(0)} ms in ${runs} profile(s)`);
for (const [n, t] of [...self].sort((a, b) => b[1] - a[1]).slice(0, lines)) console.log(`${(t / 1000).toFixed(0).padStart(8)} ms ${(100 * t / total).toFixed(1).padStart(5)}%  ${n.slice(0, 130)}`);
