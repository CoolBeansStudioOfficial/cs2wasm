// Frame-time spikes against NativeAOT (FINISH.md requirement 4, "no frame-time spikes"): game-like frame loops
// (tools/frames/Frames.cs) compiled by cs2wasm and by NativeAOT-LLVM for WebAssembly (tools/frames-naot-wasm), each
// run in its own Node (V8) process, default tiers: 1,000 warm-up frames (the engine's optimizing tier has the code
// after them), then 6,000 frames each timed with Stopwatch inside the module. Per scene: median, p99, p99.9 and worst
// frame, and peak memory (the module's linear memory at exit; it never shrinks). Each statistic is the lowest over the
// runs (a spike the program makes comes back every run; a pause of the machine does not).
//   node tools/frames.mjs [--runs N] [--rebuild-aot] [--no-build] [--no-aot] [--worst N] [--json file] [--js-young MB] [scene...]
// --no-aot: cs2wasm's side alone (a machine without the Emscripten SDK: two builds of this side against each other,
// by a compiler switch in the environment); nothing is compared, exit 0.
// --js-young MB: both sides' Node processes with a young generation of that size (V8's semi-space). cs2wasm's host
// returns the clock as a BigInt (monotonic_ns is an i64), two a frame for the Stopwatch here: one scavenge of Node's
// own heap in a run, 0.3 to 0.9 ms, at the same frame of every run of a module (strings 2995, collections 3175, churn
// 3270), inside the timed part of the frame or not by how many allocations came before it (the module's size, the
// directory, the environment). With 32 there is none in a run: what the module does, without the harness's own pause.
// Exits 1 when a scene's worst frame or p99.9 is above NativeAOT's, or a checksum differs.
// NativeAOT's side needs the Emscripten SDK 3.1.56 (EMSDK, or C:/tools/emsdk); from Git Bash, MSYS_NO_PATHCONV=1.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const runs = +opt('--runs', 3);
const valued = new Set(['--runs', '--json', '--worst', '--js-young']);
const young = opt('--js-young', null);
const nodeFlags = young ? [`--min-semi-space-size=${+young}`, `--max-semi-space-size=${+young}`] : [];
const worstN = +opt('--worst', 0);
const all = ['churn', 'collections', 'strings', 'tree', 'bigdrop'];
const picked = args.filter((a, i) => !a.startsWith('--') && !valued.has(args[i - 1]));
const scenes = picked.length ? picked : all;
const out = join(root, 'tests', 'out', 'frames');
mkdirSync(out, { recursive: true });
const src = join(root, 'tools', 'frames');
const newest = Math.max(...readdirSync(src).filter((f) => f.endsWith('.cs')).map((f) => statSync(join(src, f)).mtimeMs));

const aotDir = join(out, 'naot-wasm'), aotJs = join(aotDir, 'frames-naot.js');
const noAot = args.includes('--no-aot');
if (!noAot && (args.includes('--rebuild-aot') || !existsSync(aotJs) || statSync(aotJs).mtimeMs < newest)) {
  const emsdk = process.env.EMSDK ?? (existsSync('C:/tools/emsdk') ? 'C:/tools/emsdk' : null);
  if (!emsdk) throw new Error('NativeAOT for WebAssembly links with Emscripten 3.1.56: set EMSDK to its directory');
  console.error('publishing the NativeAOT build for WebAssembly...');
  const sub = (d) => { const p = join(emsdk, d); return existsSync(p) ? join(p, readdirSync(p)[0]) : null; };
  const env = { ...process.env, EMSDK: emsdk, PATH: [emsdk, join(emsdk, 'upstream', 'emscripten'), process.env.PATH].join(process.platform === 'win32' ? ';' : ':') };
  const py = sub('python'), nd = sub('node');
  if (py) env.EMSDK_PYTHON = join(py, process.platform === 'win32' ? 'python.exe' : 'bin/python3');
  if (nd) env.EMSDK_NODE = join(nd, process.platform === 'win32' ? 'node.exe' : 'bin/node');
  execFileSync('dotnet', ['publish', join(root, 'tools', 'frames-naot-wasm', 'frames-naot-wasm.csproj'), '-c', 'Release', '-o', aotDir, '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'], env });
}
const cli = join(root, 'src', 'Cs2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
if (!args.includes('--no-build')) execFileSync('dotnet', ['build', join(root, 'src', 'Cs2Wasm.Cli'), '-c', 'Release', '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
const wasm = join(out, 'frames.wasm');
execFileSync(cli, [join(src, 'Frames.cs'), join(src, 'WasmMain.cs'), '-o', wasm], { stdio: ['ignore', 'ignore', 'inherit'] });

const peakJs = join(root, 'tools', 'bench-naot-wasm', 'peak.cjs');
function stats(text, scene) {
  const m = new RegExp(`^frames ${scene} (.*)$`, 'm').exec(text);
  if (!m) return null;
  const raw = m[1].split(',').map(Number), t = [...raw].sort((a, b) => a - b);
  // (--worst N: the N slowest frames, as frame number (after the warm-up) and time, to tell what they did)
  if (worstN) console.error(`${scene}: slowest frames ${raw.map((v, i) => [i, v]).sort((a, b) => b[1] - a[1]).slice(0, worstN).map(([i, v]) => `${i}:${(v / 1e6).toFixed(2)}`).join(' ')}`);
  const q = (p) => t[Math.min(t.length - 1, Math.floor(p * t.length))];
  return { mean: raw.reduce((x, y) => x + y, 0) / raw.length, median: q(0.5), p99: q(0.99), p999: q(0.999), worst: t[t.length - 1], check: new RegExp(`^check ${scene} (-?\\d+)`, 'm').exec(text)?.[1] };
}
const low = (a, b) => { if (!a) return { ...b }; for (const k of ['mean', 'median', 'p99', 'p999', 'worst', 'peak']) a[k] = Math.min(a[k], b[k]); return a; };
const res = {};
for (const scene of scenes) {
  let aot = null, mod = null;
  for (let i = 0; i < runs; i++) {
    if (!noAot) {
      const a = spawnSync(process.execPath, [...nodeFlags, '-r', peakJs, aotJs, scene], { encoding: 'utf8', cwd: aotDir, maxBuffer: 1 << 26 });
      const sa = a.status === 0 && stats(a.stdout, scene);
      if (!sa) { console.error(a.stdout.slice(-2000), a.stderr); throw new Error(`NativeAOT's build failed on ${scene}`); }
      sa.peak = +(/peak_bytes=(\d+)/.exec(a.stdout)?.[1] ?? 0);
      aot = low(aot, sa);
    }
    const w = spawnSync(process.execPath, [...nodeFlags, join(root, 'host', 'run.mjs'), wasm, scene], { encoding: 'utf8', env: { ...process.env, CS2WASM_PRINT_MEMORY: '1' }, maxBuffer: 1 << 26 });
    const sw = w.status === 0 && stats(w.stdout, scene);
    if (!sw) { console.error(w.stdout.slice(-2000), w.stderr); throw new Error(`cs2wasm's module failed on ${scene}`); }
    sw.peak = +(/memory: (\d+) pages/.exec(w.stderr)?.[1] ?? 0) * 65536;
    mod = low(mod, sw);
  }
  res[scene] = { aot, cs2wasm: mod };
}

const ms = (ns) => (ns / 1e6).toFixed(3).padStart(8);
const mb = (b) => (b / 1048576).toFixed(1).padStart(6);
let bad = 0;
console.log('scene        side        median ms   p99 ms  p99.9 ms  worst ms   peak MB');
for (const [scene, { aot, cs2wasm }] of Object.entries(res)) {
  if (noAot) { console.log(`${scene.padEnd(12)} ${'cs2wasm'.padEnd(10)} ${ms(cs2wasm.median)} ${ms(cs2wasm.p99)} ${ms(cs2wasm.p999)} ${ms(cs2wasm.worst)}  ${mb(cs2wasm.peak)}`); continue; }
  const over = cs2wasm.worst > aot.worst || cs2wasm.p999 > aot.p999;
  const differs = aot.check !== cs2wasm.check;
  if (over || differs) bad++;
  for (const [side, s] of [['NativeAOT', aot], ['cs2wasm', cs2wasm]])
    console.log(`${(side === 'NativeAOT' ? scene : '').padEnd(12)} ${side.padEnd(10)} ${ms(s.median)} ${ms(s.p99)} ${ms(s.p999)} ${ms(s.worst)}  ${mb(s.peak)}`);
  console.log(`${''.padEnd(12)} ${'ratio'.padEnd(10)} ${(cs2wasm.median / aot.median).toFixed(2).padStart(7)}x ${(cs2wasm.p99 / aot.p99).toFixed(2).padStart(7)}x ${(cs2wasm.p999 / aot.p999).toFixed(2).padStart(7)}x ${(cs2wasm.worst / aot.worst).toFixed(2).padStart(7)}x  ${(cs2wasm.peak / aot.peak).toFixed(2).padStart(5)}x${over ? '   SPIKES ABOVE NATIVEAOT' : ''}${differs ? `   CHECKSUM DIFFERS: ${aot.check} vs ${cs2wasm.check}` : ''}`);
}
// (the mean frame, with the collector's pauses and the rebuilds in it: what a whole run takes)
if (!noAot) {
  let g = 1, n = 0;
  console.log('mean frame ms ' + Object.entries(res).map(([scene, { aot, cs2wasm }]) => { g *= cs2wasm.mean / aot.mean; n++; return `${scene} ${(cs2wasm.mean / 1e6).toFixed(4)} vs ${(aot.mean / 1e6).toFixed(4)} (${(cs2wasm.mean / aot.mean).toFixed(2)}x)`; }).join(', '));
  console.log(`mean frame, geometric mean over the scenes: cs2wasm takes ${Math.pow(g, 1 / n).toFixed(2)}x NativeAOT's; medians: ${Math.pow(Object.values(res).reduce((x, { aot, cs2wasm }) => x * cs2wasm.median / aot.median, 1), 1 / n).toFixed(2)}x`);
}
const json = opt('--json', null);
if (json) writeFileSync(json, JSON.stringify(res, null, 1));
process.exit(bad ? 1 : 0);
