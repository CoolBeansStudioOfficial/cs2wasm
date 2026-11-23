// A quick look at one module of tools/frames-threads.mjs's scenes, without NativeAOT's side: per scene the median,
// p99, p99.9 and worst frame, the wall time and the memory, lowest of the runs.
//   node tools/frames-threads-quick.mjs <module.wasm> [--runs N] scene...
import { spawnSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const wasm = args[0];
const ri = args.indexOf('--runs');
const runs = ri >= 0 ? +args[ri + 1] : 3;
const scenes = args.slice(1).filter((a, i, l) => !a.startsWith('--') && l[i - 1] !== '--runs');
const flags = ['--min-semi-space-size=32', '--max-semi-space-size=32'];
for (const scene of scenes) {
  let best = null;
  for (let i = 0; i < runs; i++) {
    const w = spawnSync(process.execPath, [...flags, join(root, 'host', 'run.mjs'), wasm, scene], { encoding: 'utf8', env: { ...process.env, IL2WASM_PRINT_MEMORY: '1' }, maxBuffer: 1 << 27 });
    const m = /^frames (\S+) (.*)$/m.exec(w.stdout);
    if (w.status !== 0 || !m) { console.error(w.stdout.slice(-500), w.stderr.slice(-2000)); process.exit(1); }
    const t = m[2].split(',').map(Number).sort((a, b) => a - b);
    const q = (p) => t[Math.min(t.length - 1, Math.floor(p * t.length))];
    const s = { median: q(0.5), p99: q(0.99), p999: q(0.999), worst: t[t.length - 1], wall: +(/^wall \S+ (-?\d+)/m.exec(w.stdout)?.[1] ?? 0), peak: +(/memory: (\d+) pages/.exec(w.stderr)?.[1] ?? 0) * 65536, check: /^check \S+ (-?\d+)/m.exec(w.stdout)?.[1] };
    if (!best) best = s; else for (const k of ['median', 'p99', 'p999', 'worst', 'wall', 'peak']) best[k] = Math.min(best[k], s[k]);
  }
  const ms = (ns) => (ns / 1e6).toFixed(3).padStart(8);
  console.log(`${scene.padEnd(18)} median ${ms(best.median)}  p99 ${ms(best.p99)}  p99.9 ${ms(best.p999)}  worst ${ms(best.worst)}  wall ${(best.wall / 1e6).toFixed(0).padStart(6)} ms  ${(best.peak / 1048576).toFixed(1).padStart(6)} MB  check ${best.check}`);
}
