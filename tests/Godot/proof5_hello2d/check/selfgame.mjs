// The game built by the self-hosted compiler under Node (what the page does, without the browser), with the
// arguments the .NET-hosted compiler gets (Godot's source generators are compiled into the toolchain:
// tools/selfhost.mjs --generators):
//   node tests/Godot/proof5_hello2d/check/selfgame.mjs [out.wasm] [--pregenerated]
// --pregenerated: for a toolchain without the generators: their sources are written first by the .NET-hosted
// compiler (IL2WASM_GENERATED) and given as sources.
// Environment: IL2WASM_SELF, IL2WASM_PROCESSORS, IL2WASM_TIMES.
import { readdirSync, statSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { root, outDir, godotSharp, selfHosted, selfPath, selfEnv, run } from './common.mjs';
import { scripts, buildArgs, compilerExe } from './stub.mjs';

const walk = (d) => readdirSync(d).flatMap((n) => { const p = join(d, n); return statSync(p).isDirectory() ? walk(p) : [p]; });
const rest = process.argv.slice(2);
const pregenerated = rest.includes('--pregenerated');
const out = resolve(rest.find((a) => !a.startsWith('--')) ?? join(outDir, 'game-self.wasm'));
let args;
if (pregenerated) {
  const generated = join(outDir, 'generated');
  rmSync(generated, { recursive: true, force: true });
  if (run(compilerExe(), [...buildArgs(), '-o', join(outDir, 'game-net.wasm')], { env: { ...process.env, IL2WASM_GENERATED: generated } }) !== 0) process.exit(1);
  args = [...scripts(), ...walk(generated).sort(), '-r', godotSharp].map(selfPath);
} else args = buildArgs(selfPath);
const t0 = performance.now();
const status = run('node', [join(root, 'host', 'run.mjs'), selfHosted(), 'main', '--', ...args, '-o', selfPath(out)], { env: selfEnv() });
console.log(`selfgame: exit ${status} in ${((performance.now() - t0) / 1000).toFixed(1)} s`);
process.exit(status ?? 1);
