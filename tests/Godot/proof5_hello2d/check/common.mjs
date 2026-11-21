// What the steps of proof 5 share (tools/proof5.mjs): the places and the stock editor.
import { existsSync, mkdirSync, readdirSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..');
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

/** The self-hosted compiler (SPEC section 9): CS2WASM_SELF, or the toolchain `node tools/proof5.mjs toolchain` makes
 * (tests/out/proof5/toolchain.wasm, Godot's source generators compiled in), or tests/out/self2/cs2wasm.wasm here or,
 * from a git worktree, in the main checkout (without the generators: the game does not build with it). */
export function selfHosted() {
  const candidates = [process.env.CS2WASM_SELF, join(outDir, 'toolchain.wasm'), join(root, 'tests', 'out', 'self2', 'cs2wasm.wasm'), join(root, '..', '..', '..', 'tests', 'out', 'self2', 'cs2wasm.wasm')];
  const found = candidates.find((p) => p && existsSync(p));
  if (!found) throw new Error('no self-hosted compiler: node tools/proof5.mjs toolchain (or set CS2WASM_SELF)');
  return resolve(found);
}

/** The .NET reference assemblies the compiler reads (CS2WASM_REFERENCE_DIR, or the newest targeting pack). */
export function referenceDir() {
  if (process.env.CS2WASM_REFERENCE_DIR) return process.env.CS2WASM_REFERENCE_DIR;
  const packs = join(process.env.DOTNET_ROOT ?? (process.platform === 'win32' ? 'C:\\Program Files\\dotnet' : '/usr/share/dotnet'), 'packs', 'Microsoft.NETCore.App.Ref');
  const v = readdirSync(packs).sort().reverse().map((d) => join(packs, d, 'ref', 'net11.0')).find((d) => existsSync(d));
  if (!v) throw new Error('no .NET 11 reference assemblies: set CS2WASM_REFERENCE_DIR');
  return v;
}

/** A path of this machine as the self-hosted compiler under Node sees it (host/fs.mjs: on Windows the drive is the
 * root, CS2WASM_FS_ROOT, and paths are written without it). */
export const selfPath = (s) => (process.platform === 'win32' ? s.replace(/(^|=)[A-Za-z]:/, '$1').replaceAll('\\', '/') : s);
export function selfEnv() {
  const env = { ...process.env, CS2WASM_REFERENCE_DIR: selfPath(referenceDir()), CS2WASM_CACHE: selfPath(cache) };
  if (process.platform === 'win32') env.CS2WASM_FS_ROOT = root.slice(0, 3);
  return env;
}

