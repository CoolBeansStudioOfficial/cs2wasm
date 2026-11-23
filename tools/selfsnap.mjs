// The self-hosted compiler on a snapshot of its state (host/snapshot.mjs, SPEC sections 7.2 and 7.6): its libraries
// stay parsed and bound from one build to the next. Checks that every module it writes on a snapshot is the
// .NET-hosted compiler's byte for byte, and times a build from the start, the build that writes the snapshot, and
// builds on it, in turn with NativeAOT-LLVM's build of the compiler when there is one.
// usage: node tools/selfsnap.mjs [--self toolchain.wasm] [--rounds 3] [--naot <dir of cs2wasmc-naot.js>] first.cs [other.cs...]
//   first.cs: the program whose build writes the snapshot and is timed; the others are then built on that snapshot
//   (one that needs more libraries reads them and the snapshot is written again), each twice, and the first again.
// exits 1 when a module differs or a build fails.
import { existsSync, mkdirSync, readFileSync, rmSync, statSync } from 'node:fs';
import { join, resolve, basename } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { selfPath, selfEnv, referenceDir, cache } from '../tests/Godot/proof5_hello2d/check/common.mjs';

const root = join(fileURLToPath(import.meta.url), '..', '..');
const args = process.argv.slice(2);
let self = join(root, 'tests', 'out', 'selfph', 'self.wasm'), rounds = 3, naot = null;
const programs = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--self') self = resolve(args[++i]);
  else if (args[i] === '--rounds') rounds = +args[++i];
  else if (args[i] === '--naot') naot = resolve(args[++i]);
  else programs.push(resolve(args[i]));
}
if (!existsSync(self) || programs.length === 0) { console.error('usage: node tools/selfsnap.mjs [--self toolchain.wasm] [--rounds n] [--naot dir] first.cs [other.cs...]'); process.exit(2); }
const exe = join(root, 'src', 'Cs2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
const out = join(root, 'tests', 'out', 'selfsnap');
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });
const snap = join(out, 'compiler.snap');
const env = { ...selfEnv(), CS2WASM_REFERENCE_DIR: selfPath(referenceDir()), CS2WASM_CACHE: selfPath(cache), CS2WASM_PROCESSORS: '1', CS2WASM_PRINT_MEMORY: '1' };
let failed = false;
const times = {};

// one build: the program, by a compiler, into tests/out/selfsnap/<kind>/<program>/program.wasm (the output's name is
// in the module, so every compiler writes a file of the same name); returns seconds
function build(kind, label, program, cmd, cmdArgs, extraEnv, quiet) {
  const dir = join(out, kind, basename(program, '.cs'));
  mkdirSync(dir, { recursive: true });
  const target = join(dir, 'program.wasm');
  rmSync(target, { force: true });
  const t0 = performance.now();
  const r = spawnSync(cmd, [...cmdArgs, selfPath(program), ...(process.platform === 'win32' ? ['--pathmap', '/=/'] : []), '-o', selfPath(target)],
    { env: { ...env, ...extraEnv }, encoding: 'utf8', maxBuffer: 1 << 28, timeout: 600000 });
  const s = (performance.now() - t0) / 1000;
  // (the compiler's own lines are on the standard output: a module's Console.Error is the engine's log too)
  const err = (r.stdout || '') + (r.stderr || '');
  const pages = /memory: (\d+) pages/.exec(err)?.[1];
  const notes = err.split('\n').filter((l) => l.startsWith('[snapshot]')).map((l) => l.slice(11)).join('; ');
  if (r.status !== 0 || !existsSync(target)) {
    failed = true;
    console.log(`${label}: FAILED (exit ${r.status}${r.error ? ', ' + r.error.code : ''}) in ${s.toFixed(1)} s\n${err.slice(-1500)}`);
    return { s, err };
  }
  let same = '';
  if (kind !== 'net') {
    const ref = join(out, 'net', basename(program, '.cs'), 'program.wasm');
    if (readFileSync(target).equals(readFileSync(ref))) same = ', = net byte for byte';
    else { same = ', DIFFERS from net'; failed = true; }
  }
  if (!quiet) console.log(`${label}: ${s.toFixed(2)} s, ${statSync(target).size.toLocaleString('en-US')} bytes${same}${pages ? `, memory ${(pages * 65536 / 1048576).toFixed(1)} MB` : ''}${notes ? ` [${notes}]` : ''}`);
  (times[label.replace(/ \d+$/, '')] ??= []).push(s);
  return { s, err };
}
const net = (p) => build('net', `net ${basename(p, '.cs')}`, p, exe, [], {});
const selfCmd = [join(root, 'host', 'run.mjs'), self, 'main', '--'];
const cold = (p, n) => build('cold', `from the start ${basename(p, '.cs')} ${n}`, p, 'node', selfCmd, {});
const warm = (p, n, extra = {}) => build('snap', `on the snapshot ${basename(p, '.cs')} ${n}`, p, 'node', selfCmd, { CS2WASM_SNAPSHOT: snap, CS2WASM_SNAPSHOT_WHY: '1', ...extra });
// NativeAOT-LLVM's build of the compiler (as tools/selfhost-ab.sh runs it: the drive under /c)
function native(p, n) {
  const dir = join(out, 'naot'); mkdirSync(dir, { recursive: true });
  const target = join(dir, 'program.wasm');
  const t0 = performance.now();
  const r = spawnSync('node', ['cs2wasmc-naot.js', '/c' + selfPath(p), '-o', '/c' + selfPath(target)],
    { cwd: naot, env: { ...process.env, CS2WASM_CACHE: '/c' + selfPath(cache), CS2WASM_REFERENCE_DIR: '/c' + selfPath(referenceDir()) }, encoding: 'utf8', maxBuffer: 1 << 28, timeout: 600000 });
  const s = (performance.now() - t0) / 1000;
  console.log(`NativeAOT-LLVM ${basename(p, '.cs')} ${n}: ${s.toFixed(2)} s${r.status === 0 ? '' : ` (exit ${r.status}) ${(r.stderr || '').slice(-300)}`}`);
  (times[`NativeAOT-LLVM ${basename(p, '.cs')}`] ??= []).push(s);
}

const first = programs[0];
for (const p of programs) net(p);
console.log(`--- ${basename(first)}: the build that writes the snapshot, then in turn`);
warm(first, 'writing');
const size = existsSync(snap) ? statSync(snap).size : 0;
console.log(`the snapshot: ${(size / 1048576).toFixed(1)} MB`);
for (let r = 1; r <= rounds; r++) {
  cold(first, r);
  warm(first, r);
  if (naot && existsSync(join(naot, 'cs2wasmc-naot.js'))) native(first, r);
}
// the phases of a build on the snapshot (CS2WASM_TIMES is another environment: its own snapshot)
{
  const snap2 = { CS2WASM_SNAPSHOT: join(out, 'times.snap'), CS2WASM_TIMES: '1', CS2WASM_HOST_TIMES: '1' };
  build('snap', 'with the phases printed, writing', first, 'node', selfCmd, snap2, true);
  const r = build('snap', 'with the phases printed, on the snapshot', first, 'node', selfCmd, snap2, true);
  console.log('--- the phases of a build on the snapshot (CS2WASM_TIMES=1 CS2WASM_HOST_TIMES=1)');
  console.log(r.err.split('\n').filter((l) => l.trim() && !/^\s+library:.*files, |^ +\d+ ms +\d+ MB +(finish|arenas|static)/.test(l)).map((l) => l.slice(0, 170)).join('\n'));
}
if (programs.length > 1) console.log('--- other programs on the same snapshot, each twice, then the first again');
for (const p of programs.slice(1)) { warm(p, 1); warm(p, 2); }
if (programs.length > 1) { warm(first, 'after the others'); console.log(`the snapshot: ${(statSync(snap).size / 1048576).toFixed(1)} MB`); }
console.log('--- seconds, lowest to highest (median)');
for (const [k, v] of Object.entries(times)) {
  if (v.length < 2) continue;
  const s = [...v].sort((a, b) => a - b);
  console.log(`${k}: ${s[0].toFixed(2)} to ${s.at(-1).toFixed(2)} (${s[s.length >> 1].toFixed(2)}), ${v.length} runs`);
}
console.log(failed ? 'selfsnap: FAILED' : 'selfsnap: every module on a snapshot is the .NET-hosted compiler\'s, byte for byte');
process.exit(failed ? 1 : 0);
