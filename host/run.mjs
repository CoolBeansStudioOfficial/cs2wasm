// Minimal engine for tests: creates the shared memory, supplies cs2wasm's engine imports, logs boundary calls.
// usage: node host/run.mjs module.wasm [export=main] [--pages N] [--max-pages N] [-- program arguments...]
// (the program's command line is the module's file name, then the arguments after --)
import { readFileSync, writeSync } from 'node:fs';
import { cs2wasmImports, threadImports } from './engine.mjs';
import { startIo } from './http.mjs';

const args = process.argv.slice(2);
const file = args[0];
let entry = 'main', pages = 256, maxPages = 16384;
globalThis.__cs2wasmArgs = [file];
for (let i = 1; i < args.length; i++) {
  if (args[i] === '--') { globalThis.__cs2wasmArgs.push(...args.slice(i + 1)); break; }
  if (args[i] === '--pages') pages = +args[++i];
  else if (args[i] === '--max-pages') maxPages = +args[++i];
  else entry = args[i];
}

const module = new WebAssembly.Module(readFileSync(file));
const memory = new WebAssembly.Memory({ initial: pages, maximum: maxPages, shared: true });
let buf = '';
const flush = () => { if (buf) { writeSync(1, buf); buf = ''; } };
let threaded = false; // once a worker exists, output is written through at once so it interleaves in order
const out = {
  mainCanBlock: true, // (a Node process: its main thread may wait)
  write: (s) => { if (threaded) { writeSync(1, s); return; } buf += s; if (buf.length > 65536) flush(); },
  error: (type, message, stack) => {
    flush();
    process.stderr.write(`Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n`);
    process.exitCode = 3;
  },
};
const spawned = () => { if (!threaded) { flush(); threaded = true; } };
const imports = { env: { memory }, cs2wasm: { ...cs2wasmImports(module, () => memory, out), ...threadImports(module, memory, out, spawned) } };
// any other import is an engine boundary method: log the call (stub boundary, proof 3)
for (const imp of WebAssembly.Module.imports(module)) {
  if (imp.kind !== 'function') continue;
  imports[imp.module] ??= {};
  if (imports[imp.module][imp.name]) continue;
  imports[imp.module][imp.name] = (...a) => { buf += `[engine] ${imp.module}.${imp.name}(${a.join(', ')})\n`; return 0; };
}
startIo(module, memory);
const t0 = performance.now();
const instance = new WebAssembly.Instance(module, imports);
instance.exports.init();
if (process.env.CS2WASM_HOST_TIMES) process.stderr.write(`[host] instantiate and init ${(performance.now() - t0).toFixed(0)} ms (since start ${performance.now().toFixed(0)} ms)
`);
const fn = instance.exports[entry];
if (!fn) { console.error(`no export '${entry}'`); process.exit(2); }
let r;
try { r = fn(); } finally { flush(); }
if (process.env.CS2WASM_HOST_TIMES) { process.stderr.write(`[host] ${entry} returned at ${performance.now().toFixed(0)} ms
`); process.on('exit', () => process.stderr.write(`[host] exit at ${performance.now().toFixed(0)} ms
`)); }
if (instance.exports.cs2wasm_rc_counts) { // (a module built with CS2WASM_COUNT_RC=1; CS2WASM_RC_NAMES: its sites file)
  const at = instance.exports.cs2wasm_rc_counts(), v = new BigInt64Array(memory.buffer, at, 3);
  process.stderr.write(`[host] count operations run: ${v[0]} retains, ${v[1]} releases
`);
  // the dynamic measure (section 4.1): calls of retain and release run, against two for every reference value pushed
  if (instance.exports.cs2wasm_rc_sites && process.env.CS2WASM_RC_NAMES) {
    const names = (await import('node:fs')).readFileSync(process.env.CS2WASM_RC_NAMES, 'utf8').split('\n');
    const c = new BigInt64Array(memory.buffer, instance.exports.cs2wasm_rc_sites() + 8, names.length);
    let calls = 0n; names.forEach((n, i) => { if (n.startsWith('retain ') || n.startsWith('release ')) calls += c[i]; });
    const base = 2n * v[2];
    process.stderr.write(`[host] count calls run: ${calls} against ${base} for counting every reference value pushed (${v[2]} values): ${base > 0n ? (100 - Number(calls * 10000n / base) / 100).toFixed(1) : '0'}% removed (dynamic); ${v[0] + v[1]} of the calls changed a count (the others were of null or of an immortal object): ${base > 0n ? (100 - Number((v[0] + v[1]) * 10000n / base) / 100).toFixed(1) : '0'}% removed (dynamic, counts changed)
`);
  }
  if (process.env.CS2WASM_RC_NAMES && instance.exports.cs2wasm_rc_sites) {
    const names = (await import('node:fs')).readFileSync(process.env.CS2WASM_RC_NAMES, 'utf8').split('\n');
    const c = new BigInt64Array(memory.buffer, instance.exports.cs2wasm_rc_sites() + 8, names.length);
    const top = names.map((n, i) => [n, c[i]]).filter((x) => x[1] > 0n).sort((x, y) => (y[1] > x[1] ? 1 : -1)).slice(0, +(process.env.CS2WASM_COUNT_RC_TOP ?? 30));
    for (const [n, k] of top) process.stderr.write(`${String(k).padStart(10)}  ${n}
`);
  }
}
if (r !== undefined && process.env.CS2WASM_PRINT_RESULT) console.log(`=> ${r}`);
if (process.env.CS2WASM_PRINT_MEMORY) console.error(`memory: ${memory.buffer.byteLength / 65536} pages`);
