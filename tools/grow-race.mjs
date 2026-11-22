// What a thread sees of a shared memory another thread has grown (the engine's behaviour that src/Cs2Wasm/Wasm/MemSync.cs
// answers; found as test 139's one trap in some tens of thousands of loaded runs: `memory access out of bounds` at an
// atomic load of a word that was there).
// One worker runs a long straight-line wasm function (no loop, no call: no stack check after its entry) that polls a
// cell for an address and then uses the memory at that address. The main thread grows the memory while the worker is
// inside the function, writes a word in the new pages, and puts its address in the cell. By what the function does
// with the address:
//   atomic   i32.atomic.load of the word
//   plain    i32.load of the word
//   fill     memory.fill of the word
//   size     memory.size, as the worker's instance answers it then
//   guard    if ((address >>> 16) >= memory.size) memory.grow(0); then the atomic load (what MemSync.cs writes)
//   gfill    the same test of the last address, then the fill
//   node tools/grow-race.mjs [kind ...] [--trials n]       (all kinds by default)
//   node --no-liftoff tools/grow-race.mjs                  (the optimizing tier alone; REPS=30000 PAD=6: it compiles the
//                                                           function at once, which takes long for the default size)
// Node 24.19 (V8 13.6), default tiers (the function runs in Liftoff): atomic traps in 19 or 20 trials of 20, plain in
// none, fill in all, size answers the pages before the growth, guard and gfill trap in none. With --no-liftoff: atomic
// traps in none (TurboFan's atomic accesses are not checked against the instance's number), fill in all.
import { Worker, isMainThread, workerData, parentPort } from 'node:worker_threads';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const kinds = ['atomic', 'plain', 'fill', 'size', 'guard', 'gfill'];
const FILLED = 0x07070707;
function leb(n) { const o = []; do { let b = n & 0x7f; n >>>= 7; if (n) b |= 0x80; o.push(b); } while (n); return o; }
function moduleBytes(reps) {
  // (local 1: the address; the test: local.get 1, [const 3, add,] const 16, shr_u, memory.size, ge_u, if, const 0, memory.grow, drop, end)
  const test = (last) => [0x20, 0x01, ...(last ? [0x41, last, 0x6a] : []), 0x41, 0x10, 0x76, 0x3f, 0x00, 0x4f, 0x04, 0x40, 0x41, 0x00, 0x40, 0x00, 0x1a, 0x0b];
  const atomic = [0x20, 0x01, 0xfe, 0x10, 0x02, 0x00], fill = [0x20, 0x01, 0x41, 0x07, 0x41, 0x04, 0xfc, 0x0b, 0x00, 0x20, 0x01, 0x28, 0x02, 0x00];
  const use = { atomic, plain: [0x20, 0x01, 0x28, 0x02, 0x00], fill, size: [0x3f, 0x00], guard: [...test(0), ...atomic], gfill: [...test(3), ...fill] };
  // (PAD: atomic adds of nothing between two looks at the cell, so that fewer repetitions take as long)
  const pad = []; for (let i = 0; i < +(process.env.PAD ?? 0); i++) pad.push(0x41, 0xd0, 0x00, 0x41, 0x00, 0xfe, 0x1e, 0x02, 0x00, 0x1a);
  const body = (kind) => {
    const rep = [...pad, 0x20, 0x00, 0xfe, 0x10, 0x02, 0x00, 0x22, 0x01, 0x04, 0x40, ...use[kind], 0x0f, 0x0b];
    const code = new Uint8Array(3 + rep.length * reps + 3);
    code.set([0x01, 0x01, 0x7f], 0); // one local i32
    for (let i = 0; i < reps; i++) code.set(rep, 3 + i * rep.length);
    code.set([0x41, 0x7f, 0x0b], 3 + rep.length * reps); // i32.const -1; end
    return code;
  };
  const sec = (id, bytes) => [id, ...leb(bytes.length), ...bytes];
  const f = body(workerData?.which ?? process.argv[3]);
  const head = [0, 0x61, 0x73, 0x6d, 1, 0, 0, 0, ...sec(1, [1, 0x60, 1, 0x7f, 1, 0x7f]),
    ...sec(2, [1, 3, 0x65, 0x6e, 0x76, 6, 0x6d, 0x65, 0x6d, 0x6f, 0x72, 0x79, 0x02, 0x03, ...leb(16), ...leb(4096)]),
    ...sec(3, [1, 0]), ...sec(7, [1, 1, 0x66, 0, 0]), 10, ...leb(1 + leb(f.length).length + f.length), 1, ...leb(f.length)];
  const out = new Uint8Array(head.length + f.length);
  out.set(head, 0); out.set(f, head.length);
  return out;
}

const CELL = 64;
if (isMainThread && process.argv[2] !== '--one') {
  const args = process.argv.slice(2), ti = args.indexOf('--trials'), trials = ti >= 0 ? args[ti + 1] : '20';
  const picked = args.filter((a, i) => kinds.includes(a) && args[i - 1] !== '--trials');
  for (const k of picked.length ? picked : kinds) {
    const p = spawnSync(process.execPath, [...process.execArgv, fileURLToPath(import.meta.url), '--one', k, trials], { stdio: 'inherit' });
    if (p.status !== 0) process.exitCode = 1;
  }
} else if (isMainThread) {
  const which = process.argv[3], trials = +process.argv[4], reps = +(process.env.REPS ?? (which === 'gfill' ? 150000 : 200000));
  const memory = new WebAssembly.Memory({ initial: 16, maximum: 4096, shared: true });
  const ctl = new Int32Array(new SharedArrayBuffer(16));
  const module = new WebAssembly.Module(moduleBytes(reps));
  const w = new Worker(new URL(import.meta.url), { workerData: { memory, ctl: ctl.buffer, module, which } });
  await new Promise((res) => w.once('message', res));
  let traps = 0, late = 0, ok = 0; const other = [];
  for (let t = 1; t <= trials; t++) {
    new Int32Array(memory.buffer)[CELL >> 2] = 0;
    Atomics.store(ctl, 1, 0);
    Atomics.store(ctl, 0, t); Atomics.notify(ctl, 0);
    while (Atomics.load(ctl, 1) !== 1) { }
    const old = memory.grow(16);
    const addr = old * 65536 + 128;
    const view = new Int32Array(memory.buffer);
    view[addr >> 2] = 1234;
    Atomics.store(view, CELL >> 2, addr);
    const r = await new Promise((res) => w.once('message', res));
    if (r === 'trap') traps++; else if (r === -1) late++; else if (r === 1234 || r === FILLED) ok++; else other.push(which === 'size' ? `${r} of ${old + 16}` : r);
  }
  const rest = other.length ? (which === 'size' ? `; memory.size in the worker, of the pages there were: ${other.slice(0, 6).join(', ')}${other.length > 6 ? ', ..' : ''}` : `; other answers: ${other.slice(0, 6).join(', ')}`) : '';
  console.log(`${which.padEnd(6)} ${trials} trials: trapped (memory access out of bounds) ${traps}, used the word ${ok}, the function had ended before the growth ${late}${rest}`);
  await w.terminate();
} else {
  const { memory, ctl: buf, module, which } = workerData;
  const ctl = new Int32Array(buf);
  const instance = new WebAssembly.Instance(module, { env: { memory } });
  const fn = instance.exports.f;
  // (once before the first trial: compiled, and the cell read)
  new Int32Array(memory.buffer)[CELL >> 2] = 256; fn(CELL);
  parentPort.postMessage('ready');
  let seen = 0;
  for (;;) {
    while (Atomics.load(ctl, 0) === seen) Atomics.wait(ctl, 0, seen);
    seen = Atomics.load(ctl, 0);
    let r;
    Atomics.store(ctl, 1, 1);
    try { r = fn(CELL); } catch (e) { r = /out of bounds/.test(String(e)) ? 'trap' : String(e); }
    parentPort.postMessage(r);
  }
}
