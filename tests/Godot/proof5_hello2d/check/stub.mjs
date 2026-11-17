// Proof 5, the step before the browser (node tools/proof5.mjs stub [--module game.wasm] [--project dir]): the game's scripts compiled
// by the .NET-hosted cs2wasmc against stock GodotSharp with Godot's source generators (as proof 3), run in Node on
// host/godot-bridge.mjs with the oracle's log replayed (host/godot-replay.mjs). The module's engine calls must be the
// oracle's, line for line.
import { existsSync, readFileSync, writeFileSync, readdirSync, writeSync } from 'node:fs';
import { join } from 'node:path';
import { root, project, outDir, api, godotSharp, generators, run, frames } from './common.mjs';
import { cs2wasmImports, threadImports } from '../../../../host/engine.mjs';
import { startIo } from '../../../../host/http.mjs';
import { GodotBoundary, signatures, comparable } from '../../../../host/godot-bridge.mjs';
import { ReplayBackend, ReplayMismatch, parseScene, playScene } from '../../../../host/godot-replay.mjs';

export const scripts = () => readdirSync(project).filter((f) => f.endsWith('.cs')).sort().map((f) => join(project, f));
export const ticks = 170; // (the driver's last tick: driver.gd)

/** The arguments every build of the game has, whichever compiler runs (paths through px). */
export function buildArgs(px = (s) => s) {
  return [...scripts().map(px), '-r', px(godotSharp), '-a', px(generators),
    '--property', 'GodotProjectDir=' + px(project.replaceAll('\\', '/')) + '/', '--property', 'GodotDisabledSourceGenerators=GodotPluginsInitializer'];
}

export function compilerExe() {
  for (const c of ['Release', 'Debug']) {
    const p = join(root, 'src', 'Cs2Wasm.Cli', 'bin', c, 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
    if (existsSync(p)) return p;
  }
  throw new Error('no cs2wasmc: dotnet build src/Cs2Wasm.Cli -c Release');
}

/** Compares a log with the oracle's: the lines that differ (at most 20), as text; '' when equal. */
export function compareLogs(expected, actual) {
  let text = '', shown = 0;
  for (let i = 0; i < Math.max(expected.length, actual.length) && shown < 20; i++) {
    const x = expected[i] ?? '<missing>', y = actual[i] ?? '<missing>';
    if (comparable(x) !== comparable(y)) { text += `line ${i + 1}: stock «${x}»\n           module «${y}»\n`; shown++; }
  }
  return text;
}

export async function stub(args) {
  let wasm = null, dir = project, name = 'oracle-calls.txt';
  for (let i = 0; i < args.length; i++) { if (args[i] === '--module') wasm = args[++i]; else if (args[i] === '--project') dir = args[++i]; else if (args[i] === '--panic') name = 'oracle-calls-panic.txt'; }
  if (!wasm) {
    wasm = join(outDir, 'game-net.wasm');
    const t0 = performance.now();
    if (run(compilerExe(), [...buildArgs(), '-o', wasm]) !== 0) { console.error('stub: cs2wasmc failed'); return 1; }
    console.log(`stub: compiled by the .NET-hosted cs2wasmc in ${((performance.now() - t0) / 1000).toFixed(1)} s`);
  }
  const expected = (existsSync(join(dir, name)) ? readFileSync(join(dir, name), 'utf8') : '').replaceAll('\r\n', '\n').split('\n');
  if (expected.at(-1) === '') expected.pop();
  const module = new WebAssembly.Module(readFileSync(wasm));
  const memory = new WebAssembly.Memory({ initial: 256, maximum: 16384, shared: true });
  const lines = [];
  const out = {
    write: (s) => writeSync(1, s),
    error: (type, message, stack) => { process.stderr.write(`Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n`); process.exitCode = 3; },
  };
  const backend = new ReplayBackend(expected, parseScene(readFileSync(join(dir, 'main.tscn'), 'utf8')), out);
  const bridge = new GodotBoundary(module, signatures(JSON.parse(readFileSync(api, 'utf8'))), backend, { log: (l) => lines.push(l) });
  const imports = { env: { memory }, cs2wasm: { ...cs2wasmImports(module, () => memory, out), ...threadImports(module, memory, out, () => {}) }, godot: bridge.imports() };
  startIo(module, memory);
  const instance = new WebAssembly.Instance(module, imports);
  bridge.attach(instance, memory);
  instance.exports.init();
  let failure = '';
  try { playScene(bridge, backend, ticks, frames); }
  catch (e) { failure = e instanceof ReplayMismatch ? `the module's calls leave the oracle's at\n${e.message}\n` : `the run failed: ${e?.stack ?? e}\n`; }
  const log = join(outDir, 'stub-calls.txt');
  writeFileSync(log, lines.join('\n') + '\n');
  failure ||= compareLogs(expected, lines);
  console.log(`stub: ${wasm} (${readFileSync(wasm).length} bytes): ${lines.length} log lines (${bridge.calls} engine calls), the oracle has ${expected.length}; log at ${log}`);
  if (failure) { console.log('stub: FAILED\n' + failure); return 1; }
  console.log('stub: the engine-call log equals the oracle\'s (compared as host/godot-bridge.mjs comparable() has it)');
  return 0;
}
