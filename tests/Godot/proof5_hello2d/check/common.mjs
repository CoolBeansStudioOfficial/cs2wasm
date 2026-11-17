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

