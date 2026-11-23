import { Worker } from 'node:worker_threads';
import { availableParallelism } from 'node:os';
import { writeSync } from 'node:fs';
import { fsImports, fsShared } from './fs.mjs';
import { httpImports, ioShared } from './http.mjs';
// Reference implementation of the engine imports cs2wasm's library calls (module "cs2wasm").
// An embedder (Bazoodle's engine) supplies the same functions; this one is used by the tests.
//
//   write(ptr, len)                        console output, UTF-16
//   report_exception(tp, tl, mp, ml, sp, sl) an exception escaped an export
//   now_ms() -> f64, monotonic_ns() -> i64, random_seed() -> i32, utc_offset_minutes(f64) -> i32
//   capture_stack(ptr, cap) -> len         C# stack trace of the caller, .NET format
//   stack_depth() -> i32                   number of visible C# frames
//   thread_spawn(sp, tls, id, arg, bg) -> i32 start a worker instance at __thread_entry (threadImports)
//   processor_count() -> i32               cores for the thread pool

function readU32(bytes, pos) {
  let r = 0, shift = 0, b;
  do { b = bytes[pos.i++]; r |= (b & 0x7f) << shift; shift += 7; } while (b & 0x80);
  return r >>> 0;
}
function readS32(bytes, pos) {
  let r = 0, shift = 0, b;
  do { b = bytes[pos.i++]; r |= (b & 0x7f) << shift; shift += 7; } while (b & 0x80);
  if (shift < 32 && (b & 0x40)) r |= -1 << shift;
  return r | 0;
}
// a packed custom section: format byte 0 (plain) or 1 (LZ: plain length, then runs of
// (literal count, literals, match length - 4, distance), every number LEB128)
function unpackSection(b) {
  if (b[0] === 0) return b.subarray(1);
  const p = { i: 1 }, n = readU32(b, p), out = new Uint8Array(n);
  let o = 0;
  for (;;) {
    const l = readU32(b, p);
    out.set(b.subarray(p.i, p.i + l), o); p.i += l; o += l;
    if (o >= n) break;
    let m = readU32(b, p) + 4;
    const d = readU32(b, p);
    while (m--) { out[o] = out[o - d]; o++; }
  }
  return out;
}
function readName(bytes, pos) {
  const n = readU32(bytes, pos);
  const s = new TextDecoder().decode(bytes.subarray(pos.i, pos.i + n));
  pos.i += n;
  return s;
}

/** Reads cs2wasm's custom sections: frame names per function index, and the code-offset line table. What a stack
 * needs is found when it is asked for: a name is decoded when a frame with it is shown (a large module has a hundred
 * thousand, a stack some tens), and the tables are numbers in typed arrays. A program's first throw pays for this. */
export function debugInfo(module) {
  const decoder = new TextDecoder();
  const skipName = (b, p) => { const n = readU32(b, p); p.i += n; };
  // function names: the name section's (where each is, by function index), then the frames section's own
  let nameBytes = null;
  const nameStart = [], nameLen = [];
  for (const sec of WebAssembly.Module.customSections(module, 'name')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    nameBytes = b;
    while (p.i < b.length) {
      const id = b[p.i++], len = readU32(b, p), end = p.i + len;
      if (id === 1) { const n = readU32(b, p); for (let k = 0; k < n; k++) { const idx = readU32(b, p), l = readU32(b, p); nameStart[idx] = p.i; nameLen[idx] = l; p.i += l; } }
      p.i = end;
    }
  }
  // frames: a bit per function index, set for .NET frames; strings; a name made of strings for some functions
  let fb = null, visibleAt = 0, count = 0;
  const strStart = [], strs = [], shapeAt = new Map(), lenders = new Set();
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.frames')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    fb = b;
    count = readU32(b, p);
    visibleAt = p.i;
    p.i += (count + 7) >> 3;
    const ns = readU32(b, p);
    for (let k = 0; k < ns; k++) { strStart.push(p.i); skipName(b, p); }
    const nl = readU32(b, p);
    for (let k = 0; k < nl; k++) {
      const idx = readU32(b, p);
      shapeAt.set(idx, p.i);
      const shape = readU32(b, p);
      for (let q = shape === 0 ? 1 : 2 * shape; q > 0; q--) readU32(b, p);
    }
    // (then the functions that lend their lines to the frame that calls them: a body shared by methods whose frame
    // names differ, each method's frame a stub that calls it; absent in older modules)
    if (p.i < b.length) { let at = 0; for (let k = readU32(b, p); k > 0; k--) lenders.add(at += readU32(b, p)); }
  }
  const str = (k) => strs[k] ??= readName(fb, { i: strStart[k] });
  const made = new Map();
  /** A function's frame name (undefined: it has none). */
  const nameOf = (idx) => {
    if (made.has(idx)) return made.get(idx);
    let name;
    const at = shapeAt.get(idx);
    if (at !== undefined) {
      const p = { i: at }, shape = readU32(fb, p);
      if (shape === 0) name = str(readU32(fb, p));
      else {
        const type = str(readU32(fb, p)), method = str(readU32(fb, p)), ps = [];
        for (let q = 1; q < shape; q++) ps.push(str(readU32(fb, p)) + ' ' + str(readU32(fb, p)));
        name = type + '.' + method + '(' + ps.join(', ') + ')';
      }
    } else if (nameStart[idx] !== undefined) name = decoder.decode(nameBytes.subarray(nameStart[idx], nameStart[idx] + nameLen[idx]));
    made.set(idx, name);
    return name;
  };
  const isVisible = (idx) => idx < count && ((fb[visibleAt + (idx >> 3)] >> (idx & 7)) & 1) === 1;
  /** A function's name when it is a .NET frame, else null. */
  const frameOf = (idx) => (isVisible(idx) ? nameOf(idx) : null);
  // lines: files, then (offset delta, line delta << 1 | file changes[, file]); read when a place is first asked for
  let lineTable = null;
  const readLines = () => {
    const files = [];
    let offs = new Int32Array(0), fileOf = new Int32Array(0), lineOf = new Int32Array(0), n = 0;
    for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.lines')) {
      const b = new Uint8Array(sec), p = { i: 0 };
      const nf = readU32(b, p);
      for (let k = 0; k < nf; k++) files.push(readName(b, p));
      n = readU32(b, p);
      offs = new Int32Array(n); fileOf = new Int32Array(n); lineOf = new Int32Array(n);
      let off = 0, file = 0, line = 0;
      for (let k = 0; k < n; k++) {
        off += readU32(b, p);
        const v = readS32(b, p);
        line += v >> 1;
        if (v & 1) file = readU32(b, p);
        offs[k] = off; fileOf[k] = file; lineOf[k] = line;
      }
    }
    return { files, offs, fileOf, lineOf, n };
  };
  const lineAt = (offset) => {
    const t = (lineTable ??= readLines());
    let lo = 0, hi = t.n - 1, best = -1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (t.offs[m] <= offset) { best = m; lo = m + 1; } else hi = m - 1; }
    return best < 0 || t.files[t.fileOf[best]] === undefined ? null : { file: t.files[t.fileOf[best]], line: t.lineOf[best] };
  };
  // inlined calls: the scopes (parent, name, where the call is), and the scope of each call or throw in inlined code
  // (the offsets ascending: found by halving)
  let ib = null, scopeParent = new Int32Array(0), scopeName = new Int32Array(0), scopeFile = new Int32Array(0), scopeLine = new Int32Array(0);
  let inlineOff = new Int32Array(0), inlineScope = new Int32Array(0);
  const inNameStart = [], inNames = [], inFileStart = [], inFiles = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.inline')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    ib = b;
    for (let k = readU32(b, p); k > 0; k--) { inNameStart.push(p.i); skipName(b, p); }
    for (let k = readU32(b, p); k > 0; k--) { inFileStart.push(p.i); skipName(b, p); }
    const nsc = readU32(b, p);
    scopeParent = new Int32Array(nsc); scopeName = new Int32Array(nsc); scopeFile = new Int32Array(nsc); scopeLine = new Int32Array(nsc);
    for (let k = 0; k < nsc; k++) { scopeParent[k] = readU32(b, p) - 1; scopeName[k] = readU32(b, p); scopeFile[k] = readU32(b, p) - 1; scopeLine[k] = readU32(b, p); }
    const ni = readU32(b, p);
    inlineOff = new Int32Array(ni); inlineScope = new Int32Array(ni);
    let off = 0;
    for (let k = 0; k < ni; k++) { off += readU32(b, p); inlineOff[k] = off; inlineScope[k] = readU32(b, p) - 1; }
  }
  const inlineAt = (offset) => {
    let lo = 0, hi = inlineOff.length - 1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (inlineOff[m] === offset) return inlineScope[m]; if (inlineOff[m] < offset) lo = m + 1; else hi = m - 1; }
    return -1;
  };
  const scopeNameOf = (s) => inNames[scopeName[s]] ??= readName(ib, { i: inNameStart[scopeName[s]] });
  const scopeCall = (s) => (scopeFile[s] < 0 ? null : { file: inFiles[scopeFile[s]] ??= readName(ib, { i: inFileStart[scopeFile[s]] }), line: scopeLine[s] });
  // The frames a stack trace shows for one frame of the engine's (a function and a code offset in it), innermost
  // first: the methods inlined at that offset, each where the code or its inlined call is, then the function itself
  // (if it is a .NET frame) at the outermost inlined call, or at the offset when nothing is inlined there. `walk`
  // carries a lender's place to the frame that called it (one object per stack walked, innermost frame first).
  // `places` false: the names alone (a count of frames needs no line table).
  const framesAt = (index, offset, walk, places = true) => {
    const out = [];
    let loc = !places ? null : walk?.lent !== undefined ? walk.lent : lineAt(offset);
    if (walk) walk.lent = undefined;
    for (let s = inlineAt(offset); s >= 0; s = scopeParent[s]) { out.push({ name: scopeNameOf(s), loc }); loc = places ? scopeCall(s) : null; }
    if (lenders.has(index) && walk) walk.lent = loc;
    else if (isVisible(index)) { const name = nameOf(index); if (name) out.push({ name, loc }); }
    return out;
  };
  // (whether a function's name is one, and not empty, without making it)
  const hasName = (idx) => {
    const at = shapeAt.get(idx);
    if (at === undefined) return nameStart[idx] !== undefined && nameLen[idx] > 0;
    const p = { i: at };
    return readU32(fb, p) !== 0 || readU32(fb, { i: strStart[readU32(fb, p)] }) > 0;
  };
  /** How many frames framesAt gives (no name is decoded, no line looked up). */
  const countAt = (index, offset) => {
    let n = 0;
    for (let s = inlineAt(offset); s >= 0; s = scopeParent[s]) n++;
    if (!lenders.has(index) && isVisible(index) && hasName(index)) n++;
    return n;
  };
  return {
    get frames() { const all = []; for (let k = 0; k < count; k++) all.push(frameOf(k)); return all; },
    get names() { const all = []; for (let k = 0; k < Math.max(count, nameStart.length); k++) { const v = nameOf(k); if (v !== undefined) all[k] = v; } return all; },
    lineAt, framesAt, countAt, frameOf, nameOf,
  };
}

export function cs2wasmImports(module, getMemory, out) {
  // (the frame names and lines, read when a stack trace is first wanted: every thread's instance would pay for them)
  let infoCache = null;
  const info = { get frames() { return (infoCache ??= debugInfo(module)).frames; }, lineAt: (o) => (infoCache ??= debugInfo(module)).lineAt(o), framesAt: (i, o, w) => (infoCache ??= debugInfo(module)).framesAt(i, o, w), countAt: (i, o) => (infoCache ??= debugInfo(module)).countAt(i, o) };
  // (an address comes as a signed 32-bit number: above 2 GB it is negative, hence `>>> 0` wherever one is used)
  const str = (ptr, len) => {
    const u16 = new Uint16Array(getMemory().buffer, ptr >>> 0, len);
    let s = '';
    for (let i = 0; i < len; i += 4096) s += String.fromCharCode.apply(null, u16.subarray(i, Math.min(len, i + 4096)));
    return s;
  };
  // (CS2WASM_HOST_TIMES: how many stacks the module asked for, a throw's two included, and the time they took here)
  const stats = process.env.CS2WASM_HOST_TIMES ? (globalThis.__cs2wasmStackStats ??= { calls: 0, ms: 0 }) : null;
  const visibleFrames = () => {
    if (!stats) return visibleFramesNow();
    const t0 = performance.now();
    try { return visibleFramesNow(); } finally { stats.calls++; stats.ms += performance.now() - t0; }
  };
  const engineStack = () => {
    const saved = Error.stackTraceLimit;
    Error.stackTraceLimit = Infinity;
    const stack = new Error().stack;
    Error.stackTraceLimit = saved;
    return stack;
  };
  const visibleFramesNow = () => {
    const result = [], walk = {};
    for (const line of engineStack().split('\n')) {
      const m = /wasm-function\[(\d+)\]:0x([0-9a-f]+)/.exec(line);
      if (!m) continue;
      result.push(...info.framesAt(+m[1], parseInt(m[2], 16), walk));
    }
    return result;
  };
  // (the number of frames visibleFrames gives, without their names and places: a throw asks for it twice)
  const visibleDepth = () => {
    const t0 = stats ? performance.now() : 0;
    let n = 0;
    for (const line of engineStack().split('\n')) {
      const m = /wasm-function\[(\d+)\]:0x([0-9a-f]+)/.exec(line);
      if (m) n += info.countAt(+m[1], parseInt(m[2], 16));
    }
    if (stats) { stats.calls++; stats.ms += performance.now() - t0; }
    return n;
  };
  return {
    ...fsImports(getMemory),
    ...httpImports(getMemory),
    write: (ptr, len) => out.write(str(ptr, len)),
    // the environment (name=value, each ended by a zero byte, UTF-8) and the command line (a string block), the
    // process's: shared with the workers through globalThis.__cs2wasmArgs
    env_block: (buf, size) => {
      const bytes = new TextEncoder().encode(Object.entries(process.env).map(([k, v]) => `${k}=${v}\0`).join(''));
      if (buf && size >= bytes.length) new Uint8Array(getMemory().buffer).set(bytes, buf >>> 0);
      return bytes.length;
    },
    args_block: (buf, size) => {
      const args = globalThis.__cs2wasmArgs ?? [''];
      const chars = [args.length];
      for (const a of args) { chars.push(a.length); for (let i = 0; i < a.length; i++) chars.push(a.charCodeAt(i)); }
      if (buf && size >= chars.length) new Uint16Array(getMemory().buffer, buf >>> 0, chars.length).set(chars);
      return chars.length;
    },
    report_exception: (tp, tl, mp, ml, sp, sl) => out.error(str(tp, tl), str(mp, ml), str(sp, sl)),
    // whether the module's main thread may block (out.mainCanBlock: a Node process yes, an engine's thread no)
    main_can_block: () => (out.mainCanBlock ? 1 : 0),
    now_ms: () => Date.now(),
    monotonic_ns: () => process.hrtime.bigint(),
    random_seed: () => 12345,
    utc_offset_minutes: () => 0,
    capture_stack: (ptr, cap) => {
      const text = visibleFrames().map(f => `   at ${f.name}` + (f.loc ? ` in ${f.loc.file}:line ${f.loc.line}` : '')).join('\n');
      // (CS2WASM_HOST_TIMES=2: each stack the module took, which is where it threw: what a run's exceptions are)
      if (process.env.CS2WASM_HOST_TIMES === '2') writeSync(2, `[host] stack taken:\n${text.split('\n').slice(0, 12).join('\n')}\n`);
      const n = Math.min(text.length, cap);
      const u16 = new Uint16Array(getMemory().buffer, ptr >>> 0, n);
      for (let i = 0; i < n; i++) u16[i] = text.charCodeAt(i);
      return n;
    },
    stack_depth: () => visibleDepth(),
  };
}

/** thread_spawn for Node: a worker_threads Worker per thread, sharing the module and memory, each started by the
 * thread starter (host/spawner.mjs: the main thread's worker whose event loop is never busy), whichever thread asks. */
export function threadImports(module, memory, out, onSpawn) {
  const RingSlots = 64;
  // the main thread makes the ring and the starter (at the first thread, or __prewarm); a worker has them from its start
  let spawner = null;
  const ring = () => {
    if (!globalThis.__cs2wasmRing) {
      globalThis.__cs2wasmRing = new SharedArrayBuffer((2 + RingSlots * 6) * 4);
      spawner = new Worker(new URL('./spawner.mjs', import.meta.url), { workerData: { module, memory, fs: fsShared(), io: ioShared(), argv: globalThis.__cs2wasmArgs, ring: globalThis.__cs2wasmRing } });
      spawner.on('message', (m) => {
        if (m.unhandled) { process.stderr.write(m.unhandled); process.exit(3); }
        if (m.error) { process.stderr.write(m.error + '\n'); process.exit(4); }
      });
      spawner.unref();
    }
    return new Int32Array(globalThis.__cs2wasmRing);
  };
  return {
    // (not an import: the host calls it once, before the program starts, when the module has threads: the starter and
    // its spare worker are made now, so the first thread starts at once)
    __prewarm: () => { ring(); },
    // (not an import: the host calls it when the program's entry returned; while threads that keep the process alive
    // run, the starter keeps it, as .NET's process lasts while a foreground thread runs)
    __mainReturned: () => {
      if (!globalThis.__cs2wasmRing) return;
      const c = ring();
      const watch = () => {
        for (;;) {
          const n = Atomics.load(c, 1);
          if (n <= 0) { spawner?.unref(); return; }
          spawner?.ref();
          // (a thread that ended between the load and the wait: the wait answers at once, "not-equal", with no
          // promise; `.value.then` of that answer was a TypeError and the process's exit code 1, about one loaded
          // run in some thousands of a program whose threads end as its entry returns)
          const r = Atomics.waitAsync(c, 1, n);
          if (r.async) { r.value.then(watch); return; }
        }
      };
      watch();
    },
    // (CS2WASM_PROCESSORS: the cores the module is told it has)
    processor_count: () => +(process.env.CS2WASM_PROCESSORS || availableParallelism()),
    thread_spawn: (sp, tls, id, arg, background) => {
      if (process.env.CS2WASM_TRACE_THREADS) writeSync(2, `[thread_spawn id=${id} background=${background}]\n`);
      onSpawn?.();
      const c = ring();
      if (!background) Atomics.add(c, 1, 1);
      // a free slot (all taken: the starter is behind, try again shortly)
      for (;;) {
        for (let i = 0; i < RingSlots; i++) {
          const at = 2 + i * 6;
          if (Atomics.compareExchange(c, at, 0, 2) !== 0) continue;
          c[at + 1] = sp; c[at + 2] = tls; c[at + 3] = id; c[at + 4] = arg; c[at + 5] = background ? 1 : 0;
          Atomics.store(c, at, 1);
          Atomics.add(c, 0, 1); Atomics.notify(c, 0);
          return 0;
        }
        Atomics.wait(c, 0, Atomics.load(c, 0), 1);
      }
    },
  };
}
