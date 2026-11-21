// Medians of tools/roslyn-bind-bench.sh's timing lines (stdin): per build, its wall time, parse and bind phases, and
// the wall time against NativeAOT-LLVM's (the line named naot) and against the first build named.
import { readFileSync } from 'node:fs';
const runs = new Map();
for (const line of readFileSync(0, 'utf8').split(/\r?\n/)) {
  const m = /^(\S+) (\d+) ms rc=0 parse (\d+) ms .* bind (\d+) ms/.exec(line);
  if (!m) continue;
  if (!runs.has(m[1])) runs.set(m[1], []);
  runs.get(m[1]).push([+m[2], +m[3], +m[4]]);
}
const median = (a) => { const s = [...a].sort((x, y) => x - y); const n = s.length; return n % 2 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; };
const med = new Map([...runs].map(([k, v]) => [k, [0, 1, 2].map((i) => median(v.map((r) => r[i])))]));
const naot = med.get('naot'), first = [...med.keys()][0];
console.log('median over rounds:      wall    parse     bind   x naot   vs ' + first);
for (const [k, [w, p, b]] of med)
  console.log(`  ${k.padEnd(14)} ${String(w).padStart(8)} ${String(p).padStart(8)} ${String(b).padStart(8)}   ${naot ? (w / naot[0]).toFixed(3) : '-'}   ${(w / med.get(first)[0]).toFixed(3)}`);
