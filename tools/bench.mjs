// Speed, peak memory and size against NativeAOT (SPEC sections 1, 7.6, 10) on the same target: NativeAOT compiled to
// WebAssembly (NativeAOT-LLVM, tools/bench-naot-wasm, linked by Emscripten) and il2wasm's module, both run in Node (V8).
// Builds tools/bench with both, runs both, and prints the comparison. Run it after every change that can move speed:
//   node tools/bench.mjs [--runs N] [--rebuild-aot] [--json file]
// NativeAOT's side needs the Emscripten SDK 3.1.56 (EMSDK, or C:/tools/emsdk).
// Both sides run in Node (V8), as the tests do; the times are the best of five in-process repetitions (so the
// engine's optimizing tier has the code), taken as the best over the runs, with the first repetition's beside it.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync, writeFileSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const runs = +opt('--runs', 3);
const out = join(root, 'tests', 'out', 'bench');
mkdirSync(out, { recursive: true });
const src = join(root, 'tools', 'bench');
const newest = Math.max(...readdirSync(src).filter((f) => f.endsWith('.cs') || f.endsWith('.csproj')).map((f) => statSync(join(src, f)).mtimeMs));

// NativeAOT, compiled to WebAssembly
const aotDir = join(out, 'naot-wasm');
const aotJs = join(aotDir, 'bench-naot.js'), aotWasm = join(aotDir, 'bench-naot.wasm');
const aotSrc = join(root, 'tools', 'bench-naot-wasm');
if (args.includes('--rebuild-aot') || !existsSync(aotWasm) || statSync(aotWasm).mtimeMs < newest) {
  const emsdk = process.env.EMSDK ?? (existsSync('C:/tools/emsdk') ? 'C:/tools/emsdk' : null);
  if (!emsdk) throw new Error('NativeAOT for WebAssembly links with Emscripten 3.1.56: set EMSDK to its directory');
  console.error('publishing the NativeAOT build for WebAssembly...');
  const sub = (d) => { const p = join(emsdk, d); return existsSync(p) ? join(p, readdirSync(p)[0]) : null; };
  const env = { ...process.env, EMSDK: emsdk, PATH: [emsdk, join(emsdk, 'upstream', 'emscripten'), process.env.PATH].join(process.platform === 'win32' ? ';' : ':') };
  const py = sub('python'), nd = sub('node');
  if (py) env.EMSDK_PYTHON = join(py, process.platform === 'win32' ? 'python.exe' : 'bin/python3');
  if (nd) env.EMSDK_NODE = join(nd, process.platform === 'win32' ? 'node.exe' : 'bin/node');
  execFileSync('dotnet', ['publish', join(aotSrc, 'bench-naot-wasm.csproj'), '-c', 'Release', '-o', aotDir, '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'], env });
}
// (a module's size without its custom sections: names and debugging information are not what runs)
const codeSize = (file) => {
  const b = readFileSync(file); let i = 8, n = 8;
  while (i < b.length) { const id = b[i++]; let size = 0, s = 0, x; do { x = b[i++]; size += (x & 0x7f) * 2 ** s; s += 7; } while (x & 0x80); if (id !== 0) n += size + 2; i += size; }
  return n;
};

// il2wasm
const cli = join(root, 'src', 'Il2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'il2wasm.exe' : 'il2wasm');
execFileSync('dotnet', ['build', join(root, 'src', 'Il2Wasm.Cli'), '-c', 'Release', '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
const wasm = join(out, 'bench.wasm');
const t0 = Date.now();
execFileSync(cli, [join(src, 'Programs.cs'), join(src, 'WasmMain.cs'), '-o', wasm], { stdio: ['ignore', 'ignore', 'inherit'] });
const buildMs = Date.now() - t0;

function parse(text) {
  const r = {};
  for (const line of text.split(/\r?\n/)) {
    const m = /^bench (\S+) best_us=(\d+) first_us=(\d+) check=(-?\d+)/.exec(line);
    if (m) r[m[1]] = { best: +m[2], first: +m[3], check: m[4] };
  }
  return r;
}
function best(into, r) {
  for (const [k, v] of Object.entries(r)) {
    if (!into[k]) into[k] = { ...v };
    else { into[k].best = Math.min(into[k].best, v.best); into[k].first = Math.min(into[k].first, v.first); }
  }
}

const aot = {}, mod = {};
let aotPeak = 0, wasmPeak = 0;
for (let i = 0; i < runs; i++) {
  const a = spawnSync(process.execPath, ['-r', join(aotSrc, 'peak.cjs'), aotJs], { encoding: 'utf8', cwd: aotDir });
  if (a.status !== 0) { console.error(a.stderr); throw new Error('the NativeAOT WebAssembly build failed to run'); }
  best(aot, parse(a.stdout));
  const pk = /peak_bytes=(\d+)/.exec(a.stdout);
  if (pk) aotPeak = Math.max(aotPeak, +pk[1]);
  const w = spawnSync(process.execPath, [join(root, 'host', 'run.mjs'), wasm, 'main'], { encoding: 'utf8', env: { ...process.env, IL2WASM_PRINT_MEMORY: '1' } });
  if (w.status !== 0) { console.error(w.stdout, w.stderr); throw new Error('the module failed to run'); }
  best(mod, parse(w.stdout));
  const pg = /memory: (\d+) pages/.exec(w.stderr);
  if (pg) wasmPeak = Math.max(wasmPeak, +pg[1] * 65536);
}

const ms = (us) => (us / 1000).toFixed(1).padStart(9);
let logSum = 0, n = 0, bad = 0;
console.log('benchmark     NativeAOT wasm ms  il2wasm ms   ratio   il2wasm first ms');
for (const k of Object.keys(aot)) {
  const a = aot[k], w = mod[k];
  if (!w) { console.log(`${k.padEnd(14)} ${ms(a.best)}      (no result from the module)`); bad++; continue; }
  const ratio = w.best / a.best;
  logSum += Math.log(ratio); n++;
  const same = a.check === w.check;
  if (!same) bad++;
  console.log(`${k.padEnd(14)} ${ms(a.best)}   ${ms(w.best)}  ${ratio.toFixed(2).padStart(6)}x  ${ms(w.first)}${same ? '' : `   CHECKSUM DIFFERS: ${a.check} vs ${w.check}`}`);
}
const geo = n ? Math.exp(logSum / n) : 0;
const mb = (b) => (b / 1048576).toFixed(1) + ' MB';
console.log(`\ntime, geometric mean: il2wasm takes ${geo.toFixed(2)}x NativeAOT's (below 1 is faster)`);
console.log(`peak memory (the module's linear memory): NativeAOT ${mb(aotPeak)}, il2wasm ${mb(wasmPeak)} (16 MB at least)`);
const kb = (b) => (b / 1024).toFixed(1) + ' KB';
console.log(`size (module without custom sections): NativeAOT ${kb(codeSize(aotWasm))} + ${kb(statSync(aotJs).size)} of JavaScript, il2wasm ${kb(codeSize(wasm))}; il2wasm build ${buildMs} ms`);
const json = opt('--json', null);
if (json) writeFileSync(json, JSON.stringify({ aot, il2wasm: mod, geomean: geo, aotPeak, wasmPeak, aotSize: codeSize(aotWasm), aotJsSize: statSync(aotJs).size, wasmSize: codeSize(wasm), buildMs }, null, 1));
process.exit(bad ? 1 : 0);
