// Proof 5 (SPEC section 10): hello2d in C#, built in the browser, played in the engine. The steps, each a command:
//   node tools/proof5.mjs oracle [out.txt]   the game under stock Godot 4.7.2 .NET on the desktop: its engine-call log
//                                            (default: tests/Godot/proof5_hello2d/oracle-calls.txt)
//   node tools/proof5.mjs stub               the game compiled by the .NET-hosted cs2wasmc, run on the Node stub
//                                            engine replaying the oracle's answers; the calls must be the oracle's
//   node tools/proof5.mjs web                the web variant of the Godot project, exported with the stock templates,
//                                            and everything the page serves (tests/out/proof5/site)
//   node tools/proof5.mjs check              server + headless browser: builds the game in the browser with the
//                                            self-hosted compiler, plays the fixed frames, compares the log
// Environment: CS2WASM_GODOT_EDITOR (the stock editor's console executable), CS2WASM_CACHE, CS2WASM_SELF (the
// self-hosted compiler, default tests/out/self2/cs2wasm.wasm here or in the main checkout), CS2WASM_BROWSER.
import { existsSync, mkdirSync, readdirSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const root = join(dirname(fileURLToPath(import.meta.url)), '..');
export const project = join(root, 'tests', 'Godot', 'proof5_hello2d');
export const outDir = join(root, 'tests', 'out', 'proof5');
export const version = '4.7.2';
export const cache = process.env.CS2WASM_CACHE || join(process.env.LOCALAPPDATA ?? join(process.env.HOME ?? '.', '.local', 'share'), 'cs2wasm');
export const api = join(cache, 'godot-' + version, 'extension_api.json');
export const nuget = process.env.NUGET_PACKAGES ?? join(process.env.USERPROFILE ?? process.env.HOME, '.nuget', 'packages');
export const godotSharp = join(nuget, 'godotsharp', version, 'lib', 'net8.0', 'GodotSharp.dll');
export const generators = join(nuget, 'godot.sourcegenerators', version, 'analyzers', 'dotnet', 'cs', 'Godot.SourceGenerators.dll');
export const frames = 60; // (the frame rate both runs are fixed to: one physics tick a frame)

export function editor() {
  if (process.env.CS2WASM_GODOT_EDITOR) return process.env.CS2WASM_GODOT_EDITOR;
  const install = 'C:\\Program Files\\Godot';
  const find = (d) => readdirSync(d, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? find(join(d, e.name)) : new RegExp(`^Godot_v${version}-stable_mono_.*_console\\.exe$`).test(e.name) ? [join(d, e.name)] : []);
  const found = existsSync(install) ? find(install) : [];
  if (!found.length) throw new Error('no stock Godot editor: set CS2WASM_GODOT_EDITOR');
  return found[0];
}

export function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { stdio: 'inherit', ...opts });
  if (r.error) throw r.error;
  return r.status;
}

async function main() {
  const [step, ...rest] = process.argv.slice(2);
  mkdirSync(outDir, { recursive: true });
  if (step === 'oracle') {
    const out = resolve(rest[0] ?? join(project, 'oracle-calls.txt'));
    if (run('dotnet', ['build', '-c', 'Debug', '-v', 'q', '--nologo'], { cwd: project }) !== 0) process.exit(1);
    run(editor(), ['--headless', '--path', project, '--import']);
    const status = run(editor(), ['--headless', '--path', project, '--fixed-fps', String(frames), '--', '--proof5-drive'],
      { env: { ...process.env, CS2WASM_CALL_LOG: out, CS2WASM_GODOT_API: api } });
    console.log(`oracle: stock Godot exited ${status}; log at ${out}`);
  } else if (step === 'stub') {
    process.exit(await (await import('../tests/Godot/proof5_hello2d/check/stub.mjs')).stub(rest));
  } else if (step === 'web') {
    process.exit(await (await import('../tests/Godot/proof5_hello2d/check/web.mjs')).web(rest));
  } else if (step === 'check') {
    process.exit(await (await import('../tests/Godot/proof5_hello2d/check/browser.mjs')).check(rest));
  } else {
    console.error('usage: node tools/proof5.mjs oracle|stub|web|check');
    process.exit(2);
  }
}
if (process.argv[1] && fileURLToPath(import.meta.url) === join(process.argv[1])) await main();
