// Who calls a function, by the time spent in it: sums the self time of V8 CPU profiles (node --cpu-prof) for one
// function by its callers, `depth` frames up (the frames joined with " < ").
// usage: node tools/profcallers.mjs <dir of .cpuprofile> <function name (substring)> [depth=2] [top=25]
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const [dir, want, depthArg, topArg] = process.argv.slice(2);
const depth = +(depthArg ?? 2), top = +(topArg ?? 25);
const by = new Map();
let total = 0;
for (const f of readdirSync(dir).filter((f) => f.endsWith('.cpuprofile'))) {
  const p = JSON.parse(readFileSync(join(dir, f), 'utf8'));
  const byId = new Map(p.nodes.map((n) => [n.id, n]));
  const parent = new Map();
  for (const n of p.nodes) for (const c of n.children ?? []) parent.set(c, n.id);
  const selfMs = new Map();
  for (let i = 0; i < p.samples.length; i++) selfMs.set(p.samples[i], (selfMs.get(p.samples[i]) ?? 0) + (p.timeDeltas[i] ?? 0) / 1000);
  for (const [id, ms] of selfMs) {
    const n = byId.get(id);
    if (!(n.callFrame.functionName || '').includes(want)) continue;
    const chain = [];
    let at = parent.get(id);
    // (frames of the function itself, when it calls itself, are passed over)
    while (at != null && chain.length < depth) {
      const name = byId.get(at).callFrame.functionName || '(anonymous)';
      if (!name.includes(want)) chain.push(name.length > 90 ? name.slice(0, 90) + '…' : name);
      at = parent.get(at);
    }
    const key = chain.join(' < ');
    by.set(key, (by.get(key) ?? 0) + ms);
    total += ms;
  }
}
console.log(`${want}: ${total.toFixed(0)} ms (all threads)`);
for (const [k, v] of [...by].sort((a, b) => b[1] - a[1]).slice(0, top)) console.log(`${v.toFixed(0).padStart(9)} ms  ${(100 * v / total).toFixed(1).padStart(5)}%  ${k}`);
