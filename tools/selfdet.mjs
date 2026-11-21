// The self-hosted compiler's output is the .NET-hosted compiler's, byte for byte, on any number of cores (SPEC 7.3):
// one program built by the .NET build of cs2wasmc and by a self-hosted toolchain told each core count, with the same
// paths (on Windows given without the drive, as the self-hosted compiler sees them: host/fs.mjs) and the same output
// file name (it is the assembly's name); exits 1 when any module differs, with where (tools/wasmdiff.mjs).
// usage: node tools/selfdet.mjs [--self toolchain.wasm] [--cores 1,4] <program.cs>... [cs2wasmc options: -r x.dll ...]
// --self: default tests/out/proof5/toolchain.wasm (node tools/proof5.mjs toolchain [--threads]); a toolchain without
// threads is told one core whatever --cores says (it starts none). Environment: CS2WASM_REFERENCE_DIR, CS2WASM_CACHE.
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { selfPath, selfEnv, referenceDir, cache } from '../tests/Godot/proof5_hello2d/check/common.mjs';

const root = join(fileURLToPath(import.meta.url), '..', '..');
const args = process.argv.slice(2);
let self = join(root, 'tests', 'out', 'proof5', 'toolchain.wasm'), cores = [1];
const rest = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--self') self = resolve(args[++i]);
  else if (args[i] === '--cores') cores = args[++i].split(',').map(Number);
  else rest.push(args[i]);
}
if (!existsSync(self)) { console.error(`selfdet: no toolchain at ${self}: node tools/proof5.mjs toolchain`); process.exit(2); }
const exe = ['Release', 'Debug'].map((c) => join(root, 'src', 'Cs2Wasm.Cli', 'bin', c, 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc')).find(existsSync);
if (!exe) { console.error('selfdet: no cs2wasmc: dotnet build src/Cs2Wasm.Cli -c Release'); process.exit(2); }
// (a path argument made absolute, then as the self-hosted compiler sees it: both compilers get the same strings)
const prog = rest.map((a) => (existsSync(a) ? selfPath(resolve(a)) : a));
// (Windows: the .NET build takes a drive-less path as on the current drive and writes C:..., the module writes /...:
// the file names in the module both write are the same with the drive's root mapped to '/')
if (process.platform === 'win32' && !prog.includes('--pathmap')) prog.push('--pathmap', '/=/');
const out = join(root, 'tests', 'out', 'selfdet');
rmSync(out, { recursive: true, force: true });
const env = { ...selfEnv(), CS2WASM_REFERENCE_DIR: selfPath(referenceDir()), CS2WASM_CACHE: selfPath(cache) };
const runs = [];
function build(name, cmd, cmdArgs, extraEnv) {
  const dir = join(out, name);
  mkdirSync(dir, { recursive: true });
  const target = join(dir, 'program.wasm');
  const t0 = performance.now();
  const r = spawnSync(cmd, [...cmdArgs, ...prog, '-o', selfPath(target)], { env: { ...env, ...extraEnv }, encoding: 'utf8', maxBuffer: 1 << 28 });
  const s = ((performance.now() - t0) / 1000).toFixed(1);
  if (r.status !== 0 || !existsSync(target)) { console.log(`selfdet: ${name}: failed (exit ${r.status}) in ${s} s\n${(r.stderr || r.stdout || '').slice(-2000)}`); process.exit(1); }
  console.log(`selfdet: ${name}: ${readFileSync(target).length.toLocaleString('en-US')} bytes in ${s} s`);
  runs.push({ name, target });
}
build('net', exe, [], {});
for (const n of cores) build(`self-${n}`, 'node', [join(root, 'host', 'run.mjs'), self, 'main', '--'], { CS2WASM_PROCESSORS: String(n) });
let failed = false;
const ref = readFileSync(runs[0].target);
for (const r of runs.slice(1)) {
  if (readFileSync(r.target).equals(ref)) { console.log(`selfdet: ${r.name} = net, byte for byte`); continue; }
  failed = true;
  console.log(`selfdet: ${r.name} DIFFERS from net:`);
  const d = spawnSync('node', [join(root, 'tools', 'wasmdiff.mjs'), runs[0].target, r.target, '8'], { encoding: 'utf8' });
  console.log(d.stdout.split('\n').filter((l) => l.startsWith('!=') || l.startsWith('  #') || l.startsWith('functions')).join('\n'));
}
process.exit(failed ? 1 : 0);
