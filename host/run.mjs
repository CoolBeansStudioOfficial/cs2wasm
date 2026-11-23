// Minimal engine for tests: creates the shared memory, supplies il2wasm's engine imports, logs boundary calls.
// usage: node host/run.mjs module.wasm [export=main] [--pages N] [--max-pages N] [-- program arguments...]
// (the program's command line is the module's file name, then the arguments after --)
import { readFileSync, writeSync } from 'node:fs';
import { il2wasmImports, threadImports } from './engine.mjs';
import { startIo } from './http.mjs';
import { openSnapshot } from './snapshot.mjs';

const args = process.argv.slice(2);
const file = args[0];
let entry = 'main', pages = 256, maxPages = 16384, userMax = false;
globalThis.__il2wasmArgs = [file];
for (let i = 1; i < args.length; i++) {
  if (args[i] === '--') { globalThis.__il2wasmArgs.push(...args.slice(i + 1)); break; }
  if (args[i] === '--pages') pages = +args[++i];
  else if (args[i] === '--max-pages') { maxPages = +args[++i]; userMax = true; }
  else entry = args[i];
}

const bytes = readFileSync(file);
const tRead = performance.now();
const module = new WebAssembly.Module(bytes);
const tModule = performance.now();
// the memory the module asks for (its import of env.memory: at least, at most), unless the command line says more
{
  const leb = (p) => { let r = 0, s = 0, x; do { x = bytes[p.i++]; r += (x & 0x7f) * 2 ** s; s += 7; } while (x & 0x80); return r; };
  const p = { i: 8 };
  while (p.i < bytes.length) {
    const id = bytes[p.i++], size = leb(p), end = p.i + size;
    if (id === 2) {
      for (let n = leb(p); n > 0; n--) {
        const name = () => { const l = leb(p); const s = Buffer.from(bytes.subarray(p.i, p.i + l)).toString('utf8'); p.i += l; return s; };
        const mod = name(), field = name(), kind = bytes[p.i++];
        if (kind === 0) leb(p);
        else if (kind === 1) { p.i++; const f = leb(p); leb(p); if (f & 1) leb(p); }
        else if (kind === 2) {
          const f = leb(p), min = leb(p), max = f & 1 ? leb(p) : 65536;
          if (mod === 'env' && field === 'memory') { pages = Math.max(pages, min); maxPages = Math.min(Math.max(maxPages, pages), max); if (!userMax) maxPages = max; }
        }
        else if (kind === 3) p.i += 2;
        else if (kind === 4) { p.i++; leb(p); }
      }
      break;
    }
    p.i = end;
  }
}
// IL2WASM_SNAPSHOT=<file>: the module's state kept from one process to the next (host/snapshot.mjs)
const snapshot = process.env.IL2WASM_SNAPSHOT ? openSnapshot(process.env.IL2WASM_SNAPSHOT, bytes, globalThis.__il2wasmArgs.slice(1)) : null;
let threadsStarted = false, httpUsed = false;
const memory = new WebAssembly.Memory({ initial: pages, maximum: maxPages, shared: true });
let buf = '';
const flush = () => { if (buf) { writeSync(1, buf); buf = ''; } };
let threaded = process.env.IL2WASM_UNBUFFERED === "1"; // once a worker exists (or IL2WASM_UNBUFFERED=1), output is written through at once so it interleaves in order
const out = {
  mainCanBlock: true, // (a Node process: its main thread may wait)
  write: (s) => { if (threaded) { writeSync(1, s); return; } buf += s; if (buf.length > 65536) flush(); },
  error: (type, message, stack) => {
    flush();
    process.stderr.write(`Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n`);
    process.exitCode = 3;
  },
};
const spawned = () => { threadsStarted = true; if (!threaded) { flush(); threaded = true; } };
const imports = { env: { memory }, il2wasm: { ...il2wasmImports(module, () => memory, out), ...threadImports(module, memory, out, spawned) } };
// any other import is an engine boundary method: log the call (stub boundary, proof 3)
for (const imp of WebAssembly.Module.imports(module)) {
  if (imp.kind !== 'function') continue;
  imports[imp.module] ??= {};
  if (imports[imp.module][imp.name]) continue;
  imports[imp.module][imp.name] = (...a) => { buf += `[engine] ${imp.module}.${imp.name}(${a.join(', ')})\n`; return 0; };
}
// (a module with threads: a worker is made ready now, so its first thread starts at once)
if (WebAssembly.Module.exports(module).some((x) => x.name === '__thread_entry')) imports.il2wasm.__prewarm();
startIo(module, memory);
if (snapshot) for (const k of Object.keys(imports.il2wasm)) if (k.startsWith('http_')) { const f = imports.il2wasm[k]; imports.il2wasm[k] = (...a) => { httpUsed = true; return f(...a); }; }
const t0 = performance.now();
const instance = new WebAssembly.Instance(module, imports);
const restored = snapshot?.restore(instance, memory) ?? false;
if (!restored) instance.exports.init();
if (process.env.IL2WASM_HOST_TIMES) process.stderr.write(`[host] node up and the file read ${tRead.toFixed(0)} ms, the module made ${(tModule - tRead).toFixed(0)} ms, instantiate and ${restored ? 'the snapshot put back' : 'init'} ${(performance.now() - t0).toFixed(0)} ms (since start ${performance.now().toFixed(0)} ms)
`);
const fn = instance.exports[entry];
if (!fn) { console.error(`no export '${entry}'`); process.exit(2); }
let r, failed = true;
// IL2WASM_REPEAT=n (a measurement): the entry called n times in this instance, each call's time on stderr: what an
// embedder that keeps a module (a compiler) instantiated pays for a later call
for (let n = +(process.env.IL2WASM_REPEAT || 1), i = 1; i <= n; i++) {
  const t = performance.now();
  failed = true;
  try { r = fn(); failed = false; } finally { flush(); }
  if (n > 1) process.stderr.write(`[host] call ${i} of ${entry}: ${(performance.now() - t).toFixed(0)} ms
`);
}
const tReturned = performance.now();
if (snapshot && entry === 'main') snapshot.save(instance, memory, { result: r, failed: failed || process.exitCode !== undefined, threads: threadsStarted, io: httpUsed });
// (the process lasts while a thread that is not a background one runs, as in .NET)
imports.il2wasm.__mainReturned?.();
if (process.env.IL2WASM_HOST_TIMES) { process.stderr.write(`[host] ${entry} returned at ${tReturned.toFixed(0)} ms${globalThis.__il2wasmStackStats ? `; ${globalThis.__il2wasmStackStats.calls} stacks asked for, ${globalThis.__il2wasmStackStats.ms.toFixed(0)} ms` : ''}
`); process.on('exit', () => process.stderr.write(`[host] exit at ${performance.now().toFixed(0)} ms
`)); }
if (instance.exports.il2wasm_rc_counts) { // (a module built with IL2WASM_COUNT_RC=1; IL2WASM_RC_NAMES: its sites file)
  const at = instance.exports.il2wasm_rc_counts(), v = new BigInt64Array(memory.buffer, at, 3);
  process.stderr.write(`[host] count operations run: ${v[0]} retains, ${v[1]} releases
`);
  // the dynamic measure (section 4.1): calls of retain and release run, against two for every reference value pushed
  if (instance.exports.il2wasm_rc_sites && process.env.IL2WASM_RC_NAMES) {
    const names = (await import('node:fs')).readFileSync(process.env.IL2WASM_RC_NAMES, 'utf8').split('\n');
    const c = new BigInt64Array(memory.buffer, instance.exports.il2wasm_rc_sites() + 8, names.length);
    let calls = 0n; names.forEach((n, i) => { if (n.startsWith('retain ') || n.startsWith('release ')) calls += c[i]; });
    const base = 2n * v[2];
    process.stderr.write(`[host] count calls run: ${calls} against ${base} for counting every reference value pushed (${v[2]} values): ${base > 0n ? (100 - Number(calls * 10000n / base) / 100).toFixed(1) : '0'}% removed (dynamic); ${v[0] + v[1]} of the calls changed a count (the others were of null or of an immortal object): ${base > 0n ? (100 - Number((v[0] + v[1]) * 10000n / base) / 100).toFixed(1) : '0'}% removed (dynamic, counts changed)
`);
  }
  if (process.env.IL2WASM_RC_NAMES && instance.exports.il2wasm_rc_sites) {
    const names = (await import('node:fs')).readFileSync(process.env.IL2WASM_RC_NAMES, 'utf8').split('\n');
    const c = new BigInt64Array(memory.buffer, instance.exports.il2wasm_rc_sites() + 8, names.length);
    const top = names.map((n, i) => [n, c[i]]).filter((x) => x[1] > 0n).sort((x, y) => (y[1] > x[1] ? 1 : -1)).slice(0, +(process.env.IL2WASM_COUNT_RC_TOP ?? 30));
    for (const [n, k] of top) process.stderr.write(`${String(k).padStart(10)}  ${n}
`);
  }
}
// (a module built with IL2WASM_MEMSTATS=1: where its heap's memory is, at its last growth and now)
if (instance.exports.il2wasm_memstats) { try { instance.exports.il2wasm_memstats(); } finally { flush(); } }
if (r !== undefined && process.env.IL2WASM_PRINT_RESULT) console.log(`=> ${r}`);
if (process.env.IL2WASM_PRINT_MEMORY) console.error(`memory: ${memory.buffer.byteLength / 65536} pages`);
