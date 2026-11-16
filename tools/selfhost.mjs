// Builds the self-hosted compiler (SPEC section 9, proof 4): cs2wasm's sources compiled by a cs2wasmc into one
// module, with the project's embedded resources (the library manifests and sources) as manifest resources and Roslyn's
// assemblies as references.
// usage: node tools/selfhost.mjs <cs2wasmc> <out.wasm> [cs2wasmc options...]
// (<cs2wasmc> is a command: the .NET build's cs2wasmc.exe, or `node host/run.mjs cs2wasm.wasm main --` for the
// self-hosted one)
import { readdirSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, dirname } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const [compiler, out, ...extra] = process.argv.slice(2);
const walk = (d) => readdirSync(d).flatMap((n) => { const p = join(d, n); return statSync(p).isDirectory() ? (n === 'bin' || n === 'obj' ? [] : walk(p)) : [p]; });
const src = join(root, 'src', 'Cs2Wasm');
const rel = (p) => relative(src, p).replaceAll('\\', '/');
const lines = [];
// the compiler's sources (as Cs2Wasm.csproj compiles them), the command line, the entry
for (const f of walk(src).filter((f) => f.endsWith('.cs') && !/^(Upstream|Godot)\//.test(rel(f))).sort()) lines.push(f);
lines.push(join(root, 'src', 'Cs2Wasm.Cli', 'Cli.cs'), ...walk(join(root, 'src', 'Cs2Wasm.Wasm')).filter((f) => f.endsWith('.cs')).sort());
// its embedded resources, by the names Cs2Wasm.csproj gives them
for (const f of walk(join(src, 'Godot')).filter((f) => f.endsWith('.cs')).sort()) lines.push('--resource', `godot/${rel(f).slice(6)}=${f}`);
for (const f of walk(join(src, 'Upstream')).filter((f) => f.endsWith('.cs') || f.endsWith('manifest.txt')).sort()) lines.push('--resource', `upstream/${rel(f).slice(9)}=${f}`);
// Roslyn: the assemblies beside the .NET build of cs2wasmc
const bin = join(root, 'tests', 'out', 'up', 'bin');
lines.push('-r', join(bin, 'Microsoft.CodeAnalysis.dll'), '-r', join(bin, 'Microsoft.CodeAnalysis.CSharp.dll'));
lines.push('-o', out, ...extra);
const rsp = out + '.rsp';
writeFileSync(rsp, lines.join('\n') + '\n');
const [cmd, ...pre] = compiler.split(' ');
const r = spawnSync(cmd, [...pre, '@' + rsp], { stdio: 'inherit' });
process.exit(r.status ?? 1);
