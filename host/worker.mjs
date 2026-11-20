// A worker thread: a new instance of the module on the shared memory, started at __thread_entry (section 4.5).
import { workerData, parentPort } from 'node:worker_threads';
import { writeSync } from 'node:fs';
import { cs2wasmImports, threadImports } from './engine.mjs';

const { module, memory, args } = workerData;
globalThis.__cs2wasmFs = workerData.fs; // (the process's file system state: fs.mjs)
globalThis.__cs2wasmIo = workerData.io; // (and its I/O worker's: http.mjs)
globalThis.__cs2wasmArgs = workerData.argv; // (and its command line)
if (workerData.pool) globalThis.__cs2wasmPool = workerData.pool; // (CS2WASM_PREWORKERS: engine.mjs)
const out = {
  write: (s) => writeSync(1, s),
  error: (type, message, stack) => parentPort.postMessage({ unhandled: `Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n` }),
};
const imports = { env: { memory }, cs2wasm: { ...cs2wasmImports(module, () => memory, out), ...threadImports(module, memory, out) } };
for (const imp of WebAssembly.Module.imports(module)) {
  if (imp.kind !== 'function') continue;
  imports[imp.module] ??= {};
  imports[imp.module][imp.name] ??= () => 0;
}
const trace = process.env.CS2WASM_TRACE_THREADS ? (m) => writeSync(2, `[worker ${m}]
`) : () => {};
trace('instantiating');
const instance = new WebAssembly.Instance(module, imports);
trace('instantiated');
function run(args) {
  trace(`run thread ${args[2]}`);
  // (the memory may have grown since it was sent to this worker: growing it by nothing makes this thread's instance see
  // its current size, which bulk memory operations check against)
  memory.grow(0);
  try { instance.exports.__thread_entry(...args); trace(`thread ${args[2]} ended`); }
  catch (e) {
    // the engine thread may be busy in a wait and never see a message: report here and end the process
    writeSync(2, `worker failed: ${e && e.stack || e}
`);
    process.kill(process.pid);
  }
}
// A worker made ahead of its thread (engine.mjs: thread_spawn keeps one ready) waits here for the thread's start.
if (workerData.slot !== undefined) {
  // (one of the main thread's workers made up front: it waits for a thread to start, CS2WASM_PREWORKERS)
  const c = new Int32Array(workerData.pool), at = workerData.slot * 5;
  while (Atomics.load(c, at) !== 1) Atomics.wait(c, at, Atomics.load(c, at), 50);
  run([c[at + 1], c[at + 2], c[at + 3], c[at + 4]]);
} else if (args) run(args); else parentPort.once('message', (m) => run(m.args));
