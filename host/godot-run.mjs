// Runs a module built from a Godot script on the stub engine (host/godot.mjs) and writes its engine-call log.
// usage: node host/godot-run.mjs module.wasm extension_api.json res://Script.cs NativeClass [log.txt]
import { readFileSync, writeFileSync, writeSync } from 'node:fs';
import { cs2wasmImports, threadImports } from './engine.mjs';
import { startIo } from './http.mjs';
import { GodotStub } from './godot.mjs';

const [file, api, scriptPath, nodeClass, logFile] = process.argv.slice(2);
const module = new WebAssembly.Module(readFileSync(file));
const memory = new WebAssembly.Memory({ initial: 256, maximum: 16384, shared: true });
const lines = [];
const out = {
  write: (s) => writeSync(1, s),
  error: (type, message, stack) => { process.stderr.write(`Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n`); process.exitCode = 3; },
};
const engine = new GodotStub(module, api, { log: (l) => lines.push(l), write: out.write });
const imports = { env: { memory }, cs2wasm: { ...cs2wasmImports(module, () => memory, out), ...threadImports(module, memory, out, () => {}) }, godot: engine.imports() };
startIo(module, memory);
const instance = new WebAssembly.Instance(module, imports);
engine.attach(instance, memory);
instance.exports.init();
engine.run(scriptPath, nodeClass);
// GODOT_BINDS=<file>: the method binds looked up, in order (class.method)
if (process.env.GODOT_BINDS) writeFileSync(process.env.GODOT_BINDS, engine.binds.slice(1).map(b => b.cls + '.' + b.name).join('\n') + '\n');
const text = lines.join('\n') + '\n';
if (logFile) writeFileSync(logFile, text); else process.stdout.write(text);
