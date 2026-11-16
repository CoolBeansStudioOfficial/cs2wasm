// Speed, peak memory and size against NativeAOT (SPEC sections 1, 7.6, 10): builds tools/bench with both, runs both,
// and prints the comparison. Run it after every change that can move speed:
//   node tools/bench.mjs [--runs N] [--rebuild-aot] [--json file]
// The cs2wasm side runs in Node (V8), as the tests do; the times are the best of five in-process repetitions (so the
// engine's optimizing tier has the code), taken as the best over the runs, with the first repetition's beside it.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync, writeFileSync, readdirSync } from 'node:fs';
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

// NativeAOT
const rid = process.platform === 'win32' ? 'win-x64' : process.platform === 'darwin' ? 'osx-arm64' : 'linux-x64';
const aotExe = join(out, 'aot', process.platform === 'win32' ? 'bench-aot.exe' : 'bench-aot');
if (args.includes('--rebuild-aot') || !existsSync(aotExe) || statSync(aotExe).mtimeMs < newest) {
  console.error('publishing the NativeAOT build...');
  execFileSync('dotnet', ['publish', join(src, 'bench.csproj'), '-c', 'Release', '-r', rid, '-o', join(out, 'aot'), '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
}

// cs2wasm
const cli = join(root, 'src', 'Cs2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
execFileSync('dotnet', ['build', join(root, 'src', 'Cs2Wasm.Cli'), '-c', 'Release', '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
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
  const a = spawnSync(aotExe, [], { encoding: 'utf8' });
  if (a.status !== 0) { console.error(a.stderr); throw new Error('the NativeAOT build failed to run'); }
  best(aot, parse(a.stdout));
  const pk = /peak_bytes=(\d+)/.exec(a.stdout);
  if (pk) aotPeak = Math.max(aotPeak, +pk[1]);
  const w = spawnSync(process.execPath, [join(root, 'host', 'run.mjs'), wasm, 'main'], { encoding: 'utf8', env: { ...process.env, CS2WASM_PRINT_MEMORY: '1' } });
  if (w.status !== 0) { console.error(w.stdout, w.stderr); throw new Error('the module failed to run'); }
  best(mod, parse(w.stdout));
  const pg = /memory: (\d+) pages/.exec(w.stderr);
  if (pg) wasmPeak = Math.max(wasmPeak, +pg[1] * 65536);
}

const ms = (us) => (us / 1000).toFixed(1).padStart(9);
let logSum = 0, n = 0, bad = 0;
console.log('benchmark        NativeAOT ms  cs2wasm ms   ratio   cs2wasm first ms');
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
console.log(`\ntime, geometric mean: cs2wasm takes ${geo.toFixed(2)}x NativeAOT's (below 1 is faster)`);
console.log(`peak memory: NativeAOT ${mb(aotPeak)} (working set), cs2wasm ${mb(wasmPeak)} (the module's memory, 16 MB at least)`);
console.log(`size: NativeAOT ${mb(statSync(aotExe).size)}, cs2wasm ${(statSync(wasm).size / 1024).toFixed(1)} KB; cs2wasm build ${buildMs} ms`);
const json = opt('--json', null);
if (json) writeFileSync(json, JSON.stringify({ aot, cs2wasm: mod, geomean: geo, aotPeak, wasmPeak, aotSize: statSync(aotExe).size, wasmSize: statSync(wasm).size, buildMs }, null, 1));
process.exit(bad ? 1 : 0);
