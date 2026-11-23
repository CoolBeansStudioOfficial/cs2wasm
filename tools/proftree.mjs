// A profile's time under a function by the functions below it, each with everything it calls (inclusive; a function
// on the stack more than once is counted once per sample): which calls of a phase carry its time, where
// tools/profincl.mjs gives the self times.
// usage: node tools/proftree.mjs <profile dir> <function name contains> [lines] [only names containing]
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const [dir, want, linesArg, only] = process.argv.slice(2);
const lines = +(linesArg ?? 40);
const incl = new Map();
let total = 0;
for (const f of readdirSync(dir).filter((f) => f.endsWith('.cpuprofile'))) {
  const p = JSON.parse(readFileSync(join(dir, f), 'utf8'));
  const byId = new Map(p.nodes.map((n) => [n.id, n]));
  const parent = new Map();
  for (const n of p.nodes) for (const c of n.children ?? []) parent.set(c, n.id);
  const self = new Map();
  for (let i = 0; i < p.samples.length; i++) self.set(p.samples[i], (self.get(p.samples[i]) ?? 0) + (p.timeDeltas[i] ?? 0) / 1000);
  for (const [id, t] of self) {
    // the stack from the sample up to the nearest frame of the wanted function
    const names = [];
    let at = id, found = false;
    while (at !== undefined) {
      const name = byId.get(at).callFrame.functionName;
      if (name.includes(want)) { found = true; break; }
      names.push(name);
      at = parent.get(at);
    }
    if (!found) continue;
    total += t;
    for (const n of new Set(names)) incl.set(n, (incl.get(n) ?? 0) + t);
  }
}
console.log(`under "${want}": ${total.toFixed(0)} ms`);
for (const [n, t] of [...incl].filter(([n]) => !only || n.includes(only)).sort((a, b) => b[1] - a[1]).slice(0, lines))
  console.log(`${t.toFixed(0).padStart(8)} ms  ${(100 * t / total).toFixed(1).padStart(5)}%  ${n.slice(0, 150)}`);
