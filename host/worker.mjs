// A worker thread: a new instance of the module on the shared memory, started at __thread_entry (section 4.5). Made by
// the thread starter (host/spawner.mjs), with its thread's start or ready for the next one.
import { workerData, parentPort } from 'node:worker_threads';
import { writeSync } from 'node:fs';
import { cs2wasmImports, threadImports } from './engine.mjs';

const { module, memory } = workerData;
globalThis.__cs2wasmFs = workerData.fs; // (the process's file system state: fs.mjs)
globalThis.__cs2wasmIo = workerData.io; // (and its I/O worker's: http.mjs)
globalThis.__cs2wasmArgs = workerData.argv; // (and its command line)
globalThis.__cs2wasmRing = workerData.ring; // (and the thread starter's requests: engine.mjs)
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
const trace = process.env.CS2WASM_TRACE_THREADS ? (m) => writeSync(2, `[worker ${m}]\n`) : () => {};
const instance = new WebAssembly.Instance(module, imports);
function run({ args, background }) {
  trace(`run thread ${args[2]}`);
  // (the memory may have grown since it was sent to this worker: growing it by nothing makes this thread's instance see
  // its current size, which bulk memory operations check against)
  memory.grow(0);
  try { instance.exports.__thread_entry(...args); trace(`thread ${args[2]} ended`); }
  catch (e) {
    // the engine thread may be busy in a wait and never see a message: report here and end the process
    writeSync(2, `worker failed: ${e && e.stack || e}\n`);
    process.kill(process.pid);
  }
  // (one fewer thread that keeps the process alive: engine.mjs, __mainReturned)
  if (!background) { const c = new Int32Array(workerData.ring); Atomics.sub(c, 1, 1); Atomics.notify(c, 1); }
}
// A worker made ahead of its thread (the starter keeps one ready) waits here for the thread's start.
if (workerData.start) run(workerData.start); else parentPort.once('message', run);
