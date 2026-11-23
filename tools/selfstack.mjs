// Where a self-hosted compiler that does not end is: runs it on a program under Node's inspector and, if it is still
// running after --after seconds, pauses it --samples times a second apart and prints each stack (the function names
// of the module's name section: build the compiler with IL2WASM_NAMES=1 for all of them), then stops it. A run that
// ends by itself says how (its exit code, the last of what it wrote) and how long it took.
//   node tools/selfstack.mjs --self tests/out/selfvar/nohoist.wasm [--after 40] [--samples 5] [--depth 40] [--runs 1] <program.cs> [il2wasm options]
import { spawn } from 'node:child_process';
import { join, resolve } from 'node:path';
import { existsSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { selfPath, selfEnv, referenceDir, cache } from '../tests/Godot/proof5_hello2d/check/common.mjs';

const root = join(fileURLToPath(import.meta.url), '..', '..');
const args = process.argv.slice(2);
let self = null, after = 40, samples = 5, depth = 40, runs = 1;
const rest = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--self') self = resolve(args[++i]);
  else if (args[i] === '--after') after = +args[++i];
  else if (args[i] === '--samples') samples = +args[++i];
  else if (args[i] === '--depth') depth = +args[++i];
  else if (args[i] === '--runs') runs = +args[++i];
  else rest.push(args[i]);
}
if (!self || !existsSync(self)) { console.error('selfstack: --self <compiler.wasm>'); process.exit(2); }
const prog = rest.map((a) => (existsSync(a) ? selfPath(resolve(a)) : a));
if (process.platform === 'win32' && !prog.includes('--pathmap')) prog.push('--pathmap', '/=/');
const out = join(root, 'tests', 'out', 'selfstack');
mkdirSync(out, { recursive: true });
const env = { ...selfEnv(), IL2WASM_REFERENCE_DIR: selfPath(referenceDir()), IL2WASM_CACHE: selfPath(cache), IL2WASM_PROCESSORS: '1' };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function once(run) {
  const port = 9300 + Math.floor(Math.random() * 500);
  const t0 = performance.now();
  const child = spawn('node', [`--inspect=127.0.0.1:${port}`, join(root, 'host', 'run.mjs'), self, 'main', '--', ...prog, '-o', selfPath(join(out, 'program.wasm'))], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  let text = '', ended = null;
  child.stdout.on('data', (d) => { text += d; });
  child.stderr.on('data', (d) => { text += d; });
  child.on('close', (code) => { ended = code ?? -1; });
  while (ended === null && performance.now() - t0 < after * 1000) await sleep(200);
  const s = () => ((performance.now() - t0) / 1000).toFixed(1);
  if (ended !== null) {
    const last = text.split(/\r?\n/).filter((l) => l && !/Debugger listening|For help, see|CW0202/.test(l)).slice(-3).join(' | ').slice(0, 400);
    console.log(`run ${run}: ended with ${ended} in ${s()} s${last ? ': ' + last : ''}`);
    return;
  }
  console.log(`run ${run}: still running after ${s()} s`);
  try {
    const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
    const ws = new WebSocket(list[0].webSocketDebuggerUrl);
    await new Promise((ok, bad) => { ws.onopen = ok; ws.onerror = bad; });
    let id = 0, paused = null;
    ws.onmessage = (m) => { const j = JSON.parse(m.data); if (j.method === 'Debugger.paused' && paused) paused(j.params); };
    const send = (method, params = {}) => ws.send(JSON.stringify({ id: ++id, method, params }));
    send('Debugger.enable');
    for (let k = 0; k < samples; k++) {
      const p = await new Promise((ok) => { paused = ok; send('Debugger.pause'); setTimeout(() => ok(null), 15000); });
      if (!p) { console.log(`  sample ${k}: no pause`); break; }
      console.log(`  sample ${k}:`);
      for (const f of p.callFrames.slice(0, depth)) console.log(`    ${f.functionName || '(anonymous)'}${f.location ? ' @' + f.location.columnNumber : ''}`);
      send('Debugger.resume');
      await sleep(1000);
    }
    ws.close();
  } catch (e) { console.log('  inspector: ' + e.message); }
  child.kill();
  await sleep(500);
}
for (let r = 1; r <= runs; r++) await once(r);
process.exit(0);
