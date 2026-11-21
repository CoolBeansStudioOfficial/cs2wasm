// Proof 5, the toolchain the page runs (node tools/proof5.mjs toolchain [--threads] [selfhost.mjs options...]):
// cs2wasm compiled by the .NET-hosted cs2wasmc (tools/selfhost.mjs) into tests/out/proof5/toolchain.wasm, with
// Godot's source generators compiled in (SPEC 7.1: the page names them with `-a Godot.SourceGenerators.dll`, as the
// .NET-hosted compiler's command line does) and without threads (CS2WASM_NO_THREADS: the build NativeAOT-LLVM's
// browser-wasm is compared with; the page tells the compiler it has one core either way).
// The generators' sources are stock Godot's at the commit GodotSharp's are taken from (Library.cs: GodotCommit), in
// the same cache: added to its sparse checkout here. What Godot's build writes beside them (VersionDocsUrl) is
// ../toolchain/Common.Constants.cs.
// Environment: CS2WASM_GODOT_GENERATORS (a directory of the generators' sources, in place of the cache's).
import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { root, project, outDir, cache, version, run } from './common.mjs';
import { compilerExe } from './stub.mjs';

export const toolchainPath = join(outDir, 'toolchain.wasm');

function generatorSources() {
  if (process.env.CS2WASM_GODOT_GENERATORS) return process.env.CS2WASM_GODOT_GENERATORS;
  const tree = join(cache, 'godot-' + version);
  const rel = 'modules/mono/editor/Godot.NET.Sdk/Godot.SourceGenerators';
  const dir = join(tree, ...rel.split('/'));
  if (!existsSync(join(dir, 'ScriptMethodsGenerator.cs'))) {
    if (!existsSync(join(tree, '.git'))) throw new Error(`no Godot sources in ${tree}: build a Godot script with cs2wasmc once (it fetches them), or set CS2WASM_GODOT_GENERATORS`);
    console.log(`toolchain: adding Godot's source generators to ${tree} (git sparse-checkout)`);
    if (run('git', ['-C', tree, 'sparse-checkout', 'add', '/' + rel + '/']) !== 0) throw new Error('git sparse-checkout add failed');
  }
  return dir;
}

export async function toolchain(args) {
  const threads = args.includes('--threads');
  const extra = args.filter((a) => a !== '--threads');
  mkdirSync(outDir, { recursive: true });
  // (a copy of the compiler's build: the tree's own may be rebuilt meanwhile)
  const exe = compilerExe();
  const cli = join(outDir, 'toolchain-cli');
  rmSync(cli, { recursive: true, force: true });
  cpSync(join(exe, '..'), cli, { recursive: true });
  const env = { ...process.env, CS2WASM_ROSLYN_DIR: cli, DOTNET_GCHeapHardLimit: process.env.DOTNET_GCHeapHardLimit ?? '0x380000000' };
  if (!threads) env.CS2WASM_NO_THREADS = '1';
  const t0 = performance.now();
  const status = run('node', [join(root, 'tools', 'selfhost.mjs'), join(cli, 'cs2wasmc' + (process.platform === 'win32' ? '.exe' : '')), toolchainPath,
    '--generators', 'Godot.SourceGenerators=' + generatorSources(), '--plugin-source', join(project, 'toolchain', 'Common.Constants.cs'), ...extra], { env });
  if (status !== 0) { console.error(`toolchain: tools/selfhost.mjs exited ${status}`); return 1; }
  console.log(`toolchain: ${toolchainPath} in ${((performance.now() - t0) / 60000).toFixed(1)} min${threads ? '' : ' (no threads)'}; next: node tools/proof5.mjs web`);
  return 0;
}
