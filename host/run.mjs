// Minimal engine for tests: creates the shared memory, supplies cs2wasm's engine imports, logs boundary calls.
// usage: node host/run.mjs module.wasm [export=main] [--pages N] [--max-pages N]
import { readFileSync } from 'node:fs';
import { cs2wasmImports } from './engine.mjs';

const args = process.argv.slice(2);
const file = args[0];
let entry = 'main', pages = 256, maxPages = 16384;
for (let i = 1; i < args.length; i++) {
  if (args[i] === '--pages') pages = +args[++i];
  else if (args[i] === '--max-pages') maxPages = +args[++i];
  else entry = args[i];
}

const module = new WebAssembly.Module(readFileSync(file));
const memory = new WebAssembly.Memory({ initial: pages, maximum: maxPages, shared: true });
let buf = '';
const flush = () => { if (buf) { process.stdout.write(buf); buf = ''; } };
const out = {
  write: (s) => { buf += s; if (buf.length > 65536) flush(); },
  error: (type, message, stack) => {
    flush();
    process.stderr.write(`Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n`);
    process.exitCode = 3;
  },
};
const imports = { env: { memory }, cs2wasm: cs2wasmImports(module, () => memory, out) };
// any other import is an engine boundary method: log the call (stub boundary, proof 3)
for (const imp of WebAssembly.Module.imports(module)) {
  if (imp.kind !== 'function') continue;
  imports[imp.module] ??= {};
  if (imports[imp.module][imp.name]) continue;
  imports[imp.module][imp.name] = (...a) => { buf += `[engine] ${imp.module}.${imp.name}(${a.join(', ')})\n`; return 0; };
}
const instance = new WebAssembly.Instance(module, imports);
instance.exports.init();
const fn = instance.exports[entry];
if (!fn) { console.error(`no export '${entry}'`); process.exit(2); }
let r;
try { r = fn(); } finally { flush(); }
if (r !== undefined && process.env.CS2WASM_PRINT_RESULT) console.log(`=> ${r}`);
if (process.env.CS2WASM_PRINT_MEMORY) console.error(`memory: ${memory.buffer.byteLength / 65536} pages`);
