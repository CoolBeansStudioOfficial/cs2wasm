// The benchmarks (tools/bench/Programs.cs) built by cs2wasm with and without compiler switches, run in turn: per
// benchmark and build the best time over the rounds (each run's own best of five) and every round's, in ms. No
// NativeAOT side: runs where there is no Emscripten (the homelab), for a change's own effect.
//   node tools/bench-ab.mjs [--rounds N] <name>=<SWITCH=1,...> ...
//   node tools/bench-ab.mjs --rounds 5 on= off=CS2WASM_NO_FIELD_HOIST=1
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const ri = args.indexOf('--rounds');
const rounds = ri >= 0 ? +args[ri + 1] : 3;
const builds = args.filter((a, i) => a.includes('=') && !a.startsWith('--') && i !== ri + 1).map((a) => { const k = a.indexOf('='); return { name: a.slice(0, k), env: Object.fromEntries(a.slice(k + 1).split(',').filter(Boolean).map((s) => s.split('='))) }; });
const out = join(root, 'tests', 'out', 'bench-ab');
mkdirSync(out, { recursive: true });
const src = join(root, 'tools', 'bench');
const cli = join(root, 'src', 'Cs2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
for (const b of builds) {
  b.wasm = join(out, `bench-${b.name}.wasm`);
  execFileSync(cli, [join(src, 'Programs.cs'), join(src, 'WasmMain.cs'), '-o', b.wasm], { stdio: ['ignore', 'ignore', 'inherit'], env: { ...process.env, ...b.env } });
  b.times = new Map(); b.checks = new Map();
}
let names = [], bad = 0;
for (let r = 0; r < rounds; r++)
  for (let k = 0; k < builds.length; k++) {
    const b = builds[(k + r) % builds.length];
    const w = spawnSync(process.execPath, [join(root, 'host', 'run.mjs'), b.wasm, 'main'], { encoding: 'utf8' });
    if (w.status !== 0) { console.error(w.stdout.slice(-1000), w.stderr); throw new Error(`${b.name} failed`); }
    for (const m of w.stdout.matchAll(/^bench (\S+) best_us=(\d+) first_us=\d+ check=(-?\d+)/gm)) {
      if (!names.includes(m[1])) names.push(m[1]);
      if (!b.times.has(m[1])) b.times.set(m[1], []);
      b.times.get(m[1]).push(+m[2] / 1000);
      b.checks.set(m[1], m[3]);
    }
  }
console.log(`benchmark     ${builds.map((b) => (b.name + ' ms').padStart(12)).join('')}   each round (${builds.map((b) => b.name).join(' | ')})`);
const geo = builds.map(() => 0);
for (const n of names) {
  const best = builds.map((b) => Math.min(...b.times.get(n)));
  best.forEach((v, i) => { geo[i] += Math.log(v / best[0]); });
  for (const b of builds) if (b.checks.get(n) !== builds[0].checks.get(n)) { bad++; console.log(`${n}: ${b.name}'s checksum differs`); }
  console.log(`${n.padEnd(13)} ${best.map((v) => v.toFixed(2).padStart(12)).join('')}   ${builds.map((b) => b.times.get(n).map((v) => v.toFixed(2)).join(' ')).join(' | ')}`);
}
console.log(`geometric mean against ${builds[0].name}: ${geo.map((g) => Math.exp(g / names.length).toFixed(3) + 'x').join('  ')}`);
process.exit(bad ? 1 : 0);
