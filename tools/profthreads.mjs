// One line per thread of a run profiled with node --cpu-prof: how long it was sampled, how much of that it was busy
// (not idle, not waiting), and its busiest functions. Shows whether a slow phase is one thread working while the
// others wait, or all of them working slowly.
// usage: node tools/profthreads.mjs <dir of .cpuprofile> [top=4]
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const [dir, topArg] = process.argv.slice(2);
const top = +(topArg ?? 4);
const waiting = (n) => /^\(idle\)|BlockHolding|^Atomics|^wait/.test(n);
const rows = [];
for (const f of readdirSync(dir).filter((f) => f.endsWith('.cpuprofile'))) {
  const p = JSON.parse(readFileSync(join(dir, f), 'utf8'));
  const byId = new Map(p.nodes.map((n) => [n.id, n]));
  const self = new Map();
  let total = 0, busy = 0;
  for (let i = 0; i < p.samples.length; i++) {
    const name = byId.get(p.samples[i]).callFrame.functionName || '(anonymous)';
    const t = (p.timeDeltas[i] ?? 0) / 1000;
    total += t;
    if (!waiting(name)) { busy += t; self.set(name, (self.get(name) ?? 0) + t); }
  }
  rows.push({ f, total, busy, tops: [...self].sort((a, b) => b[1] - a[1]).slice(0, top) });
}
rows.sort((a, b) => b.busy - a.busy);
let sum = 0;
for (const r of rows) {
  sum += r.busy;
  console.log(`${r.busy.toFixed(0).padStart(7)} ms busy of ${r.total.toFixed(0).padStart(7)}  ${r.tops.map(([n, t]) => `${n.length > 48 ? n.slice(0, 48) + '…' : n} ${t.toFixed(0)}`).join(' | ')}`);
}
console.log(`${sum.toFixed(0)} ms busy in all, ${rows.length} threads`);
