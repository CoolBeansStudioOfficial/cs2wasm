// A by-phase tool (tools/<name>.cs with a public static Parts.Run() and an entry "parts") on both sides, side by side:
// il2wasm's module and NativeAOT-LLVM's WebAssembly (tools/parts-naot-wasm), each in its own Node process, in turn,
// N times; per row the lowest "mid" (the mean of the middle half of the frames) over the runs, and the range; then
// the lowest p99.
//   node tools/parts.mjs [--runs N] [--no-build] [--js-young MB] [--env NAME=VALUE]... <name>      (name: colstr-parts)
// --env: an environment variable for il2wasm's compile (an IL2WASM_NO_* switch); the module is then built beside the
// plain one and shown as a third column.
// NativeAOT's side needs the Emscripten SDK 3.1.56 (EMSDK, or C:/tools/emsdk); from Git Bash, MSYS_NO_PATHCONV=1.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const valued = new Set(['--runs', '--js-young', '--env']);
const runs = +opt('--runs', 3);
const young = opt('--js-young', '32');
const nodeFlags = [`--min-semi-space-size=${+young}`, `--max-semi-space-size=${+young}`];
const envs = args.map((a, i) => (a === '--env' ? args[i + 1] : null)).filter(Boolean);
const name = args.filter((a, i) => !a.startsWith('--') && !valued.has(args[i - 1]))[0];
if (!name) throw new Error('usage: node tools/parts.mjs [--runs N] <name>');
const out = join(root, 'tests', 'out', 'parts', name);
mkdirSync(out, { recursive: true });
const src = join(root, 'tools', name + '.cs');

const aotDir = join(out, 'naot'), aotJs = join(aotDir, 'parts-naot.js');
if (!existsSync(aotJs) || statSync(aotJs).mtimeMs < statSync(src).mtimeMs) {
  const emsdk = process.env.EMSDK ?? (existsSync('C:/tools/emsdk') ? 'C:/tools/emsdk' : null);
  if (!emsdk) throw new Error('NativeAOT for WebAssembly links with Emscripten 3.1.56: set EMSDK to its directory');
  console.error('publishing the NativeAOT build for WebAssembly...');
  const sub = (d) => { const p = join(emsdk, d); return existsSync(p) ? join(p, readdirSync(p)[0]) : null; };
  const env = { ...process.env, EMSDK: emsdk, PATH: [emsdk, join(emsdk, 'upstream', 'emscripten'), process.env.PATH].join(process.platform === 'win32' ? ';' : ':') };
  const py = sub('python'), nd = sub('node');
  if (py) env.EMSDK_PYTHON = join(py, process.platform === 'win32' ? 'python.exe' : 'bin/python3');
  if (nd) env.EMSDK_NODE = join(nd, process.platform === 'win32' ? 'node.exe' : 'bin/node');
  execFileSync('dotnet', ['publish', join(root, 'tools', 'parts-naot-wasm', 'parts-naot-wasm.csproj'), '-c', 'Release', '-o', aotDir, `-p:Part=${name}`, '-v', 'q', '--nologo', '-nowarn:CS0649'], { stdio: ['ignore', 'ignore', 'inherit'], env });
}
const cli = join(root, 'src', 'Il2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'il2wasm.exe' : 'il2wasm');
if (!args.includes('--no-build')) execFileSync('dotnet', ['build', join(root, 'src', 'Il2Wasm.Cli'), '-c', 'Release', '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
const wasm = join(out, 'parts.wasm');
execFileSync(cli, [src, '-o', wasm], { stdio: ['ignore', 'ignore', 'ignore'] });
let wasm2 = null;
if (envs.length) {
  wasm2 = join(out, 'parts-env.wasm');
  execFileSync(cli, [src, '-o', wasm2], { stdio: ['ignore', 'ignore', 'ignore'], env: { ...process.env, ...Object.fromEntries(envs.map((e) => e.split('='))) } });
}

const sides = [['NativeAOT', () => spawnSync(process.execPath, [...nodeFlags, aotJs], { encoding: 'utf8', cwd: aotDir })],
  ['il2wasm', () => spawnSync(process.execPath, [...nodeFlags, join(root, 'host', 'run.mjs'), wasm, 'parts'], { encoding: 'utf8' })]];
if (wasm2) sides.push([envs.join(' '), () => spawnSync(process.execPath, [...nodeFlags, join(root, 'host', 'run.mjs'), wasm2, 'parts'], { encoding: 'utf8' })]);
const rows = [], data = new Map();
for (let r = 0; r < runs; r++)
  for (const [side, run] of sides) {
    const p = run();
    if (p.status !== 0) { console.error(p.stdout.slice(-2000), p.stderr); throw new Error(`${side} failed`); }
    for (const line of p.stdout.split(/\r?\n/)) {
      const m = /^(.*?)\s+mid\s+(\d+)\s+median\s+(\d+)\s+mean\s+(\d+)\s+p99\s+(\d+)/.exec(line);
      const c = /^(\w+) sum of mids (\d+)\s+check (-?\d+)/.exec(line);
      const key = m ? m[1].trim() : c ? `${c[1]} sum of mids` : null;
      if (!key) continue;
      if (!data.has(key)) { data.set(key, {}); rows.push(key); }
      const d = data.get(key);
      (d[side] ??= []).push(+(m ? m[2] : c[2]));
      if (m) ((d.p99 ??= {})[side] ??= []).push(+m[5]);
      if (c) { d.check ??= c[3]; if (d.check !== c[3]) d.differs = true; }
    }
  }
const cell = (v) => (v ? `${Math.min(...v)}`.padStart(7) + ` (${Math.min(...v)}-${Math.max(...v)})`.padEnd(15) : ''.padEnd(22));
console.log(`${'ns, mid: lowest (range)'.padEnd(36)} ${sides.map(([s]) => s.padEnd(22)).join(' ')} ratio    p99: lowest, ${sides.map(([s]) => s).join(' / ')}`);
for (const key of rows) {
  const d = data.get(key);
  const a = Math.min(...d.NativeAOT), w = Math.min(...d.il2wasm);
  console.log(`${key.padEnd(36)} ${sides.map(([s]) => cell(d[s])).join(' ')} ${(a ? (w / a).toFixed(2) + 'x' : '').padEnd(8)} ${d.p99 ? sides.map(([s]) => `${Math.min(...d.p99[s])}`.padStart(7)).join(' /') : ''}${d.differs ? '   CHECKSUM DIFFERS' : ''}`);
}
