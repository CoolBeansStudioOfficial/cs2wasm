// Small operations one by one against NativeAOT compiled to WebAssembly (allocation, strings, collections, iterators, dispatch, lists):
// where a benchmark of tools/bench.mjs loses, this says which operation. Each is its own function, called 25
// times, the best time taken (an engine never replaces a loop that is running: a phase measured inside one
// long function is measured in the baseline tier).
//   node tools/micro.mjs [--rebuild-aot] [filter]
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const filter = args.find((a) => !a.startsWith('--'));
const out = join(root, 'tests', 'out', 'micro');
mkdirSync(out, { recursive: true });
const src = join(root, 'tools', 'micro');
const newest = Math.max(...readdirSync(src).filter((f) => f.endsWith('.cs') || f.endsWith('.csproj')).map((f) => statSync(join(src, f)).mtimeMs));
// NativeAOT compiled to WebAssembly (NativeAOT-LLVM, linked by Emscripten 3.1.56: EMSDK, or C:/tools/emsdk), run in Node as cs2wasm's module is
const aotDir = join(out, 'naot-wasm'), aotJs = join(aotDir, 'micro-naot.js');
if (args.includes('--rebuild-aot') || !existsSync(aotJs) || statSync(aotJs).mtimeMs < newest) {
  const emsdk = process.env.EMSDK ?? (existsSync('C:/tools/emsdk') ? 'C:/tools/emsdk' : null);
  if (!emsdk) throw new Error('NativeAOT for WebAssembly links with Emscripten 3.1.56: set EMSDK to its directory');
  console.error('publishing the NativeAOT build for WebAssembly...');
  const sub = (d) => { const p = join(emsdk, d); return existsSync(p) ? join(p, readdirSync(p)[0]) : null; };
  const env = { ...process.env, EMSDK: emsdk, PATH: [emsdk, join(emsdk, 'upstream', 'emscripten'), process.env.PATH].join(process.platform === 'win32' ? ';' : ':') };
  const py = sub('python'), nd = sub('node');
  if (py) env.EMSDK_PYTHON = join(py, process.platform === 'win32' ? 'python.exe' : 'bin/python3');
  if (nd) env.EMSDK_NODE = join(nd, process.platform === 'win32' ? 'node.exe' : 'bin/node');
  execFileSync('dotnet', ['publish', join(root, 'tools', 'micro-naot-wasm', 'micro-naot-wasm.csproj'), '-c', 'Release', '-o', aotDir, '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'], env });
}
const cli = join(root, 'src', 'Cs2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
execFileSync('dotnet', ['build', join(root, 'src', 'Cs2Wasm.Cli'), '-c', 'Release', '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
const wasm = join(out, 'micro.wasm');
execFileSync(cli, [join(src, 'Micro.cs'), join(src, 'WasmMain.cs'), '-o', wasm], { stdio: ['ignore', 'ignore', 'ignore'] });
const parse = (text) => Object.fromEntries(text.split(/\r?\n/).filter((l) => l.startsWith('phase ')).map((l) => { const p = l.split(' '); return [p[1], +p[2]]; }));
const best = (a, b) => { for (const k in b) a[k] = Math.min(a[k] ?? Infinity, b[k]); return a; };
const aot = {}, mod = {};
let checks = [];
for (let i = 0; i < 2; i++) {
  const a = spawnSync(process.execPath, [aotJs], { encoding: 'utf8', cwd: aotDir });
  const w = spawnSync(process.execPath, [join(root, 'host', 'run.mjs'), wasm, 'main'], { encoding: 'utf8' });
  if (a.status !== 0 || w.status !== 0) { console.error(a.stderr, w.stdout, w.stderr); throw new Error('a run failed'); }
  best(aot, parse(a.stdout)); best(mod, parse(w.stdout));
  checks = [/check (-?\d+)/.exec(a.stdout)?.[1], /check (-?\d+)/.exec(w.stdout)?.[1]];
}
console.log('operation         NativeAOT wasm us  cs2wasm us   ratio');
let logSum = 0, n = 0;
for (const k in aot) {
  if (filter && !k.includes(filter)) continue;
  const r = mod[k] / Math.max(aot[k], 1);
  logSum += Math.log(r); n++;
  console.log(`${k.padEnd(20)} ${String(aot[k]).padStart(11)} ${String(mod[k]).padStart(11)}  ${r.toFixed(2).padStart(6)}x`);
}
console.log(`\ngeometric mean: ${Math.exp(logSum / Math.max(n, 1)).toFixed(2)}x${checks[0] === checks[1] ? '' : `   CHECKSUM DIFFERS: ${checks[0]} vs ${checks[1]}`}`);
