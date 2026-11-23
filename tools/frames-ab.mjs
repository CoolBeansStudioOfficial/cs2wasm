// The frame scenes (tools/frames/Frames.cs) built by cs2wasm with and without compiler switches, run in turn: per
// scene and build the median, p99, p99.9 and worst frame, each the lowest over the rounds, and every round's median and p99.9 (the
// desk machine's noise is larger than most changes: the rounds beside each other show it).
//   node tools/frames-ab.mjs [--rounds N] [--js-young MB] <name>=<SWITCH=1,...> ... [scene...]
//   node tools/frames-ab.mjs --rounds 5 --js-young 32 on= off=CS2WASM_NO_FIELD_HOIST=1 churn tree
// A build named with no switches (`on=`) is the compiler as it is.
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const valued = new Set(['--rounds', '--js-young']);
const rounds = +opt('--rounds', 3);
const young = opt('--js-young', null);
const nodeFlags = young ? [`--min-semi-space-size=${+young}`, `--max-semi-space-size=${+young}`] : [];
const rest = args.filter((a, i) => !a.startsWith('--') && !valued.has(args[i - 1]));
const builds = rest.filter((a) => a.includes('=')).map((a) => { const k = a.indexOf('='); return { name: a.slice(0, k), env: Object.fromEntries(a.slice(k + 1).split(',').filter(Boolean).map((s) => s.split('='))) }; });
const picked = rest.filter((a) => !a.includes('='));
const scenes = picked.length ? picked : ['churn', 'collections', 'strings', 'tree', 'bigdrop'];
const out = join(root, 'tests', 'out', 'frames-ab');
mkdirSync(out, { recursive: true });
const src = join(root, 'tools', 'frames');
const cli = join(root, 'src', 'Cs2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
for (const b of builds) {
  b.wasm = join(out, `frames-${b.name}.wasm`);
  execFileSync(cli, [join(src, 'Frames.cs'), join(src, 'WasmMain.cs'), '-o', b.wasm], { stdio: ['ignore', 'ignore', 'inherit'], env: { ...process.env, ...b.env } });
}
function stats(text, scene) {
  const m = new RegExp(`^frames ${scene} (.*)$`, 'm').exec(text);
  if (!m) return null;
  const t = m[1].split(',').map(Number).sort((a, b) => a - b);
  const q = (p) => t[Math.min(t.length - 1, Math.floor(p * t.length))];
  return { median: q(0.5), p99: q(0.99), p999: q(0.999), worst: t[t.length - 1], check: new RegExp(`^check ${scene} (-?\\d+)`, 'm').exec(text)?.[1] };
}
const ms = (ns) => (ns / 1e6).toFixed(4);
console.log('scene        build       median ms   p99 ms  p99.9 ms  worst ms   each round\'s median; each round\'s p99.9');
let bad = 0;
for (const scene of scenes) {
  const res = builds.map(() => ({ medians: [], p999s: [], median: Infinity, p99: Infinity, p999: Infinity, worst: Infinity, check: null }));
  for (let r = 0; r < rounds; r++)
    for (let k = 0; k < builds.length; k++) {
      const i = (k + r) % builds.length;
      const w = spawnSync(process.execPath, [...nodeFlags, join(root, 'host', 'run.mjs'), builds[i].wasm, scene], { encoding: 'utf8', maxBuffer: 1 << 26 });
      const s = w.status === 0 && stats(w.stdout, scene);
      if (!s) { console.error(w.stdout.slice(-1000), w.stderr); throw new Error(`${builds[i].name} failed on ${scene}`); }
      const x = res[i];
      x.medians.push(s.median); x.p999s.push(s.p999); x.median = Math.min(x.median, s.median); x.p99 = Math.min(x.p99, s.p99); x.p999 = Math.min(x.p999, s.p999); x.worst = Math.min(x.worst, s.worst);
      if (x.check != null && x.check !== s.check) bad++;
      x.check = s.check;
    }
  for (let i = 0; i < builds.length; i++) {
    if (res[i].check !== res[0].check) { bad++; console.log(`${scene}: ${builds[i].name}'s checksum ${res[i].check} is not ${builds[0].name}'s ${res[0].check}`); }
    console.log(`${(i === 0 ? scene : '').padEnd(12)} ${builds[i].name.padEnd(10)} ${ms(res[i].median).padStart(9)} ${ms(res[i].p99).padStart(8)} ${ms(res[i].p999).padStart(9)} ${ms(res[i].worst).padStart(9)}   ${res[i].medians.map(ms).join(' ')}; ${res[i].p999s.map((v) => (v / 1e6).toFixed(3)).join(' ')}`);
  }
}
process.exit(bad ? 1 : 0);
