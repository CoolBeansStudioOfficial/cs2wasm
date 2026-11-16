// A worker thread: a new instance of the module on the shared memory, started at __thread_entry (section 4.5).
import { workerData, parentPort } from 'node:worker_threads';
import { writeSync } from 'node:fs';
import { cs2wasmImports, threadImports } from './engine.mjs';

const { module, memory, args } = workerData;
globalThis.__cs2wasmFs = workerData.fs; // (the process's file system state: fs.mjs)
globalThis.__cs2wasmIo = workerData.io; // (and its I/O worker's: http.mjs)
globalThis.__cs2wasmArgs = workerData.argv; // (and its command line)
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
const instance = new WebAssembly.Instance(module, imports);
try { instance.exports.__thread_entry(...args); }
catch (e) {
  // the engine thread may be busy in a wait and never see a message: report here and end the process
  writeSync(2, `worker failed: ${e && e.stack || e}
`);
  process.kill(process.pid);
}
