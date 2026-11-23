// Runs the Invisible API sample (the game compiled by il2wasm with the plugin) as a server would: plays a scripted
// input sequence through the game's exports and asks the plugin's generated server code along the way.
//   node samples/invisible-api/driver.mjs arena.wasm script.txt
// Script lines (# starts a comment):
//   join ID            a player joins
//   input ID ACTION    one input (bits: 1 right, 2 left, 4 down, 8 up, 16 fire, 32 claim); the gate's answer for a
//                      fire is asked first
//   tick [K]           K ticks (default 1), the marks checked after each
//   dump               the game's whole state (its own Describe)
//   view ID            the server's view of a player's private state (generated)
//   predict N          the wave timer N ticks from now, in closed form (generated)
//   advance N          the live wave timer advanced N ticks at once (generated)
//   override ACTION    the next input's action is replaced by the server's
// Output: for each line "> line", then what it printed; after join and input, the marks ("changes: ...") and the
// inputs the plugin saw ("inputs: ...").
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const { il2wasmImports, threadImports } = await import('file://' + join(root, 'host', 'engine.mjs').replace(/\\/g, '/'));

const [file, scriptFile] = process.argv.slice(2);
const bytes = readFileSync(file);
const module = new WebAssembly.Module(bytes);
// the memory the module asks for (its import of env.memory), as host/run.mjs reads it
let pages = 256, maxPages = 65536;
{
  const leb = (p) => { let r = 0, s = 0, b; do { b = bytes[p.i++]; r += (b & 0x7f) * 2 ** s; s += 7; } while (b & 0x80); return r; };
  const p = { i: 8 };
  while (p.i < bytes.length) {
    const id = bytes[p.i++], size = leb(p), end = p.i + size;
    if (id === 2) {
      for (let k = leb(p); k > 0; k--) {
        const name = () => { const l = leb(p); const s = Buffer.from(bytes.subarray(p.i, p.i + l)).toString('utf8'); p.i += l; return s; };
        const mod = name(), field = name(), kind = bytes[p.i++];
        if (kind === 0) leb(p);
        else if (kind === 1) { p.i++; const f = leb(p); leb(p); if (f & 1) leb(p); }
        else if (kind === 2) { const f = leb(p), min = leb(p), max = f & 1 ? leb(p) : 65536; if (mod === 'env' && field === 'memory') { pages = Math.max(pages, min); maxPages = max; } }
        else if (kind === 3) p.i += 2;
        else if (kind === 4) { p.i++; leb(p); }
      }
      break;
    }
    p.i = end;
  }
}
const memory = new WebAssembly.Memory({ initial: pages, maximum: maxPages, shared: true });
let text = '';
const out = {
  mainCanBlock: true,
  write: (s) => { text += s; },
  error: (type, message, stack) => { text += `Unhandled exception. ${type}: ${message}\n`; process.exitCode = 3; },
};
const imports = { env: { memory }, il2wasm: { ...il2wasmImports(module, () => memory, out), ...threadImports(module, memory, out, () => {}) } };
for (const imp of WebAssembly.Module.imports(module)) {
  if (imp.kind !== 'function') continue;
  imports[imp.module] ??= {};
  imports[imp.module][imp.name] ??= () => 0;
}
const x = new WebAssembly.Instance(module, imports).exports;
x.init();

const lines = [];
const flush = () => { if (text) { lines.push(...text.replace(/\n$/, '').split('\n')); text = ''; } };
const marks = () => { x.server_changes(); x.server_inputs(); flush(); };
for (const raw of readFileSync(scriptFile, 'utf8').split(/\r?\n/)) {
  const line = raw.replace(/#.*/, '').trim();
  if (!line) continue;
  const [cmd, ...a] = line.split(/\s+/);
  const n = a.map(Number);
  lines.push('> ' + line);
  switch (cmd) {
    case 'join': x.join(n[0]); flush(); marks(); break;
    case 'input':
      if (n[1] & 16) lines.push('gate ' + x.gate_fire(n[0]));
      x.input(n[0], n[1]); flush(); marks(); break;
    case 'tick':
      for (let k = 0; k < (a.length ? n[0] : 1); k++) {
        x.tick(); flush();
        x.server_changes(); flush();
        // a tick changes nothing the server must send: every write in it is deterministic
        const last = lines.pop();
        if (last !== 'changes: ') lines.push(`tick ${k}: ${last}`);
      }
      break;
    case 'dump': x.dump(); flush(); break;
    case 'view': x.gate_view(n[0]); flush(); break;
    case 'predict': x.server_predict(n[0]); flush(); break;
    case 'advance': x.server_advance(n[0]); flush(); break;
    case 'override': x.server_override(n[0]); flush(); break;
    default: throw new Error('unknown script line: ' + line);
  }
}
flush();
process.stdout.write(lines.join('\n') + '\n');
