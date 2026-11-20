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

/** Parses cs2wasm's custom sections: frame names per function index, and the code-offset line table. */
export function debugInfo(module) {
  // function names: the name section's function names (a .NET frame name for each frame)
  const names = [];
  for (const sec of WebAssembly.Module.customSections(module, 'name')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    while (p.i < b.length) {
      const id = b[p.i++], len = readU32(b, p), end = p.i + len;
      if (id === 1) { const n = readU32(b, p); for (let k = 0; k < n; k++) { const idx = readU32(b, p); names[idx] = readName(b, p); } }
      p.i = end;
    }
  }
  // frames: a bit per function index, set for .NET frames
  const frames = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.frames')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    const n = readU32(b, p);
    const visible = [];
    for (let k = 0; k < n; k++) visible.push((b[p.i + (k >> 3)] >> (k & 7)) & 1);
    p.i += (n + 7) >> 3;
    const ns = readU32(b, p), strs = [];
    for (let k = 0; k < ns; k++) strs.push(readName(b, p));
    const nl = readU32(b, p);
    for (let k = 0; k < nl; k++) {
      const idx = readU32(b, p), shape = readU32(b, p);
      if (shape === 0) { names[idx] = strs[readU32(b, p)]; continue; }
      const type = strs[readU32(b, p)], method = strs[readU32(b, p)], ps = [];
      for (let q = 1; q < shape; q++) ps.push(strs[readU32(b, p)] + ' ' + strs[readU32(b, p)]);
      names[idx] = type + '.' + method + '(' + ps.join(', ') + ')';
    }
    for (let k = 0; k < n; k++) frames.push(visible[k] ? names[k] : null);
  }
  // lines: files, then (offset delta, line delta << 1 | file changes[, file])
  const files = [], lines = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.lines')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    const nf = readU32(b, p);
    for (let k = 0; k < nf; k++) files.push(readName(b, p));
    const n = readU32(b, p);
    let off = 0, file = 0, line = 0;
    for (let k = 0; k < n; k++) {
      off += readU32(b, p);
      const v = readS32(b, p);
      line += v >> 1;
      if (v & 1) file = readU32(b, p);
      lines.push([off, file, line]);
    }
  }
  const lineAt = (offset) => {
    let lo = 0, hi = lines.length - 1, best = -1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (lines[m][0] <= offset) { best = m; lo = m + 1; } else hi = m - 1; }
    return best < 0 || files[lines[best][1]] === undefined ? null : { file: files[lines[best][1]], line: lines[best][2] };
  };
  // inlined calls: the scopes (parent, name, where the call is), and the scope of each call or throw in inlined code
  const scopes = [], inlineOf = new Map();
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.inline')) {
    const b = unpackSection(new Uint8Array(sec)), p = { i: 0 };
    const inNames = [], inFiles = [];
    for (let k = readU32(b, p); k > 0; k--) inNames.push(readName(b, p));
    for (let k = readU32(b, p); k > 0; k--) inFiles.push(readName(b, p));
    for (let k = readU32(b, p); k > 0; k--) {
      const parent = readU32(b, p) - 1, name = inNames[readU32(b, p)], file = readU32(b, p) - 1, line = readU32(b, p);
      scopes.push({ parent, name, call: file < 0 ? null : { file: inFiles[file], line } });
    }
    let off = 0;
    for (let k = readU32(b, p); k > 0; k--) { off += readU32(b, p); inlineOf.set(off, readU32(b, p) - 1); }
  }
  // The frames a stack trace shows for one frame of the engine's (a function and a code offset in it), innermost
  // first: the methods inlined at that offset, each where the code or its inlined call is, then the function itself
  // (if it is a .NET frame) at the outermost inlined call, or at the offset when nothing is inlined there.
  const framesAt = (index, offset) => {
    const out = [];
    let loc = lineAt(offset);
    for (let s = inlineOf.get(offset) ?? -1; s >= 0; s = scopes[s].parent) { out.push({ name: scopes[s].name, loc }); loc = scopes[s].call; }
    if (frames[index]) out.push({ name: frames[index], loc });
    return out;
  };
  return { frames, lineAt, names, framesAt };
}

export function cs2wasmImports(module, getMemory, out) {
  // (the frame names and lines, read when a stack trace is first wanted: every thread's instance would pay for them)
  let infoCache = null;
  const info = { get frames() { return (infoCache ??= debugInfo(module)).frames; }, lineAt: (o) => (infoCache ??= debugInfo(module)).lineAt(o), framesAt: (i, o) => (infoCache ??= debugInfo(module)).framesAt(i, o) };
  // (an address comes as a signed 32-bit number: above 2 GB it is negative, hence `>>> 0` wherever one is used)
  const str = (ptr, len) => {
    const u16 = new Uint16Array(getMemory().buffer, ptr >>> 0, len);
    let s = '';
    for (let i = 0; i < len; i += 4096) s += String.fromCharCode.apply(null, u16.subarray(i, Math.min(len, i + 4096)));
    return s;
  };
  const visibleFrames = () => {
    const saved = Error.stackTraceLimit;
    Error.stackTraceLimit = Infinity;
    const stack = new Error().stack;
    Error.stackTraceLimit = saved;
    const result = [];
    for (const line of stack.split('\n')) {
      const m = /wasm-function\[(\d+)\]:0x([0-9a-f]+)/.exec(line);
      if (!m) continue;
      result.push(...info.framesAt(+m[1], parseInt(m[2], 16)));
    }
    return result;
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
      const n = Math.min(text.length, cap);
      const u16 = new Uint16Array(getMemory().buffer, ptr >>> 0, n);
      for (let i = 0; i < n; i++) u16[i] = text.charCodeAt(i);
      return n;
    },
    stack_depth: () => visibleFrames().length,
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
        const n = Atomics.load(c, 1);
        if (n <= 0) { spawner?.unref(); return; }
        spawner?.ref();
        Atomics.waitAsync(c, 1, n).value.then(watch);
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
