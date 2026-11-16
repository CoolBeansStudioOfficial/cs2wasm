// Builds the self-hosted compiler (SPEC section 9, proof 4): cs2wasm's sources compiled by a cs2wasmc into one
// module, with the project's embedded resources (the library manifests and sources) as manifest resources and Roslyn's
// assemblies as references.
// usage: node tools/selfhost.mjs <cs2wasmc> <out.wasm> [--plugin-source <file.cs|dir>]... [--plugin-type <Type>]...
//        [cs2wasmc options...]
// --plugin-source, --plugin-type: plugin passes (SPEC section 7.1) compiled into the toolchain: their sources, and the
// full names of their ICompilerPlugin classes (each with a constructor without parameters); the toolchain runs one
// when asked with --plugin <Type>
// (<cs2wasmc> is a command: the .NET build's cs2wasmc.exe, or `node host/run.mjs cs2wasm.wasm main --` for the
// self-hosted one)
import { readdirSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, dirname } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const [compiler, out, ...rest] = process.argv.slice(2);
const extra = [], pluginSources = [], pluginTypes = [];
for (let i = 0; i < rest.length; i++) {
  if (rest[i] === '--plugin-source') pluginSources.push(rest[++i]);
  else if (rest[i] === '--plugin-type') pluginTypes.push(rest[++i]);
  else extra.push(rest[i]);
}
const walk = (d) => readdirSync(d).flatMap((n) => { const p = join(d, n); return statSync(p).isDirectory() ? (n === 'bin' || n === 'obj' ? [] : walk(p)) : [p]; });
const src = join(root, 'src', 'Cs2Wasm');
const rel = (p) => relative(src, p).replaceAll('\\', '/');
const lines = [];
// the compiler's sources (as Cs2Wasm.csproj compiles them), the command line, the entry
for (const f of walk(src).filter((f) => f.endsWith('.cs') && !/^(Upstream|Godot)\//.test(rel(f))).sort()) lines.push(f);
lines.push(join(root, 'src', 'Cs2Wasm.Cli', 'Cli.cs'), ...walk(join(root, 'src', 'Cs2Wasm.Wasm')).filter((f) => f.endsWith('.cs')).sort());
// the plugin passes compiled in: their sources, and the other part of Entry, which makes them known by name
for (const p of pluginSources) lines.push(...(statSync(p).isDirectory() ? walk(p).filter((f) => f.endsWith('.cs')).sort() : [p]));
if (pluginTypes.length > 0) {
  const gen = out + '.plugins.g.cs';
  writeFileSync(gen, `static partial class Entry
{
    static partial void AddPlugins()
    {
${pluginTypes.map((t) => `        Cs2Wasm.Cli.KnownPlugins.Add(new global::${t}());
`).join('')}    }
}
`);
  lines.push(gen);
}
// its embedded resources, by the names Cs2Wasm.csproj gives them
for (const f of walk(join(src, 'Godot')).filter((f) => f.endsWith('.cs')).sort()) lines.push('--resource', `godot/${rel(f).slice(6)}=${f}`);
for (const f of walk(join(src, 'Upstream')).filter((f) => f.endsWith('.cs') || f.endsWith('manifest.txt')).sort()) lines.push('--resource', `upstream/${rel(f).slice(9)}=${f}`);
// Roslyn: the assemblies beside the .NET build of cs2wasmc
const bin = join(root, 'tests', 'out', 'up', 'bin');
lines.push('-r', join(bin, 'Microsoft.CodeAnalysis.dll'), '-r', join(bin, 'Microsoft.CodeAnalysis.CSharp.dll'));
lines.push('-o', out, ...extra);
const rsp = out + '.rsp';
const [cmd, ...pre] = compiler.split(' ');
// The self-hosted compiler sees the host's files under one root with '/' separators (host/fs.mjs: CS2WASM_FS_ROOT):
// on Windows the drive is the root, and the paths are written without it.
const selfHosted = pre.some((a) => a.endsWith('.wasm'));
const px = (s) => selfHosted && process.platform === 'win32' ? s.replace(/(^|=)[A-Za-z]:/, '$1').replaceAll('\\', '/') : s;
const env = { ...process.env };
if (selfHosted && process.platform === 'win32') {
  env.CS2WASM_FS_ROOT ??= root.slice(0, 3);
  if (process.env.LOCALAPPDATA) env.CS2WASM_CACHE ??= px(join(process.env.LOCALAPPDATA, 'cs2wasm'));
}
writeFileSync(rsp, lines.map(px).join('\n') + '\n');
const r = spawnSync(cmd, [...pre, '@' + px(rsp)], { stdio: 'inherit', env });
process.exit(r.status ?? 1);
