// The libraries a program is compiled with (UpstreamSources.Needed: CoreLib and the core libraries up to the last
// whose reference assembly the program uses) against all the core libraries (CS2WASM_ALL_CORE_LIBS=1): each program
// compiled both ways by cs2wasmc must give the same module byte for byte, or fail the same way.
// usage: node tools/corelibs-check.mjs [--par 4] [--env NAME=value]... [program.cs ...]     (default:
// tests/Conformance/*.cs and the benchmark programs; exits 1 when one differs)
// --env: the second compile's environment in place of CS2WASM_ALL_CORE_LIBS=1 (another rule of what is read, by its
// switch: CS2WASM_NO_LIB_DOC_SKIP=1, the libraries' documentation comments parsed; CS2WASM_NO_LIB_RESTART=1, every
// library Roslyn reports as used)
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { join, basename } from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = join(fileURLToPath(import.meta.url), '..', '..');
const args = process.argv.slice(2);
let par = 4, other = null;
const files = [];
for (let i = 0; i < args.length; i++) { if (args[i] === '--par') par = +args[++i]; else if (args[i] === '--env') { const kv = args[++i].split('='); (other ??= {})[kv[0]] = kv.slice(1).join('='); } else files.push(args[i]); }
if (files.length === 0) {
  const dir = join(root, 'tests', 'Conformance');
  files.push(...readdirSync(dir).filter((f) => f.endsWith('.cs')).sort().map((f) => join(dir, f)));
  for (const d of ['frames', 'bench', 'micro']) {
    const p = join(root, 'tools', d);
    if (existsSync(p)) files.push(p);
  }
  // (programs without Console, which the conformance programs all use: CoreLib alone, with System.Collections, with System.Linq)
  const own = join(root, 'tools', 'corelibs');
  files.push(...readdirSync(own).filter((f) => f.endsWith('.cs')).sort().map((f) => join(own, f)));
}
const exe = ['Release', 'Debug'].map((c) => join(root, 'src', 'Cs2Wasm.Cli', 'bin', c, 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc')).find(existsSync);
if (!exe) { console.error('corelibs-check: no cs2wasmc: dotnet build src/Cs2Wasm.Cli -c Release'); process.exit(2); }
const out = join(root, 'tests', 'out', 'corelibs');
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });
const run = (file, target, env) => new Promise((done) => {
  const p = spawn(exe, [file, '-o', target], { env: { ...process.env, ...env } });
  let err = '';
  p.stderr.on('data', (d) => (err += d));
  p.stdout.on('data', () => {});
  p.on('close', (code) => done({ code, err: err.replace(/ in \d+ ms/g, '').replaceAll(target, 'OUT') }));
});
let next = 0, same = 0, failedBoth = 0, differ = 0;
const again = [];
const counts = new Map();
async function worker() {
  while (next < files.length) {
    const f = files[next++], name = basename(f).replace(/\.cs$/, '');
    // (the same output name both times: it is the module's assembly name)
    const a = join(out, 'a', name), b = join(out, 'b', name);
    mkdirSync(a, { recursive: true }); mkdirSync(b, { recursive: true });
    const ra = await run(f, join(a, 'p.wasm'), { CS2WASM_TIMES: '1' });
    const rb = await run(f, join(b, 'p.wasm'), other ?? { CS2WASM_ALL_CORE_LIBS: '1' });
    const n = /what the program needs \((\d+) libraries/.exec(ra.err)?.[1] ?? '?';
    counts.set(n, (counts.get(n) ?? 0) + 1);
    // (a program compiled again with every reported library: one it was begun without was needed)
    if (/again, with every library reported/.test(ra.err)) again.push(`${name} (begun with ${n})`);
    // (the first run's phase lines are not part of what is compared)
    const errA = ra.err.split('\n').filter((l) => !/^\s+(library:|\d+ ms|of it binding|static initializers again:|memory layout:)/.test(l) && !/^cs2wasm: heap after/.test(l)).join('\n').replaceAll(join(a, 'p.wasm'), 'OUT');
    const errB = rb.err.replaceAll(join(b, 'p.wasm'), 'OUT');
    const ma = existsSync(join(a, 'p.wasm')) ? readFileSync(join(a, 'p.wasm')) : null, mb = existsSync(join(b, 'p.wasm')) ? readFileSync(join(b, 'p.wasm')) : null;
    if (ma && mb && ma.equals(mb)) same++;
    else if (!ma && !mb && ra.code === rb.code && errA.trim() === errB.trim()) failedBoth++;
    else { differ++; console.log(`DIFFERS ${name} (${n} libraries): exit ${ra.code} / ${rb.code}, ${ma?.length ?? 'no module'} / ${mb?.length ?? 'no module'} bytes\n${errA.slice(0, 600)}\n---\n${errB.slice(0, 600)}`); }
  }
}
await Promise.all(Array.from({ length: par }, worker));
console.log(`corelibs-check (against ${Object.entries(other ?? { CS2WASM_ALL_CORE_LIBS: '1' }).map(([k, v]) => k + '=' + v).join(' ')}): ${files.length} programs: ${same} the same module, ${failedBoth} not compiled either way with the same messages, ${differ} differ`);
console.log(`compiled again with every reported library: ${again.length}${again.length ? ': ' + again.sort().join(', ') : ''}`);
console.log(`libraries needed (at the start): ${[...counts].sort((x, y) => +x[0] - +y[0]).map(([n, c]) => `${n}: ${c} programs`).join(', ')}`);
process.exit(differ ? 1 : 0);
