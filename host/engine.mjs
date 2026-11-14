import { Worker } from 'node:worker_threads';
// Reference implementation of the engine imports cs2wasm's library calls (module "cs2wasm").
// An embedder (Bazoodle's engine) supplies the same functions; this one is used by the tests.
//
//   write(ptr, len)                        console output, UTF-16
//   report_exception(tp, tl, mp, ml, sp, sl) an exception escaped an export
//   now_ms() -> f64, monotonic_ns() -> i64, random_seed() -> i32, utc_offset_minutes(f64) -> i32
//   capture_stack(ptr, cap) -> len         C# stack trace of the caller, .NET format
//   stack_depth() -> i32                   number of visible C# frames
//   thread_spawn(sp, tls, id, arg) -> i32   start a worker instance at __thread_entry (threadImports)

function readU32(bytes, pos) {
  let r = 0, shift = 0, b;
  do { b = bytes[pos.i++]; r |= (b & 0x7f) << shift; shift += 7; } while (b & 0x80);
  return r >>> 0;
}
function readName(bytes, pos) {
  const n = readU32(bytes, pos);
  const s = new TextDecoder().decode(bytes.subarray(pos.i, pos.i + n));
  pos.i += n;
  return s;
}

/** Parses cs2wasm's custom sections: frame names per function index, and the code-offset line table. */
export function debugInfo(module) {
  const frames = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.frames')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    const n = readU32(b, p);
    for (let k = 0; k < n; k++) { const visible = b[p.i++] === 1; const name = readName(b, p); frames.push(visible ? name : null); }
  }
  const files = [], lines = [];
  for (const sec of WebAssembly.Module.customSections(module, 'cs2wasm.lines')) {
    const b = new Uint8Array(sec), p = { i: 0 };
    const nf = readU32(b, p);
    for (let k = 0; k < nf; k++) files.push(readName(b, p));
    const n = readU32(b, p);
    for (let k = 0; k < n; k++) lines.push([readU32(b, p), readU32(b, p), readU32(b, p)]);
    lines.sort((a, c) => a[0] - c[0]);
  }
  const lineAt = (offset) => {
    let lo = 0, hi = lines.length - 1, best = -1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (lines[m][0] <= offset) { best = m; lo = m + 1; } else hi = m - 1; }
    return best < 0 ? null : { file: files[lines[best][1]], line: lines[best][2] };
  };
  return { frames, lineAt };
}

export function cs2wasmImports(module, getMemory, out) {
  const info = debugInfo(module);
  const str = (ptr, len) => {
    const u16 = new Uint16Array(getMemory().buffer, ptr, len);
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
      const name = info.frames[+m[1]];
      if (!name) continue;
      result.push({ name, loc: info.lineAt(parseInt(m[2], 16)) });
    }
    return result;
  };
  return {
    write: (ptr, len) => out.write(str(ptr, len)),
    report_exception: (tp, tl, mp, ml, sp, sl) => out.error(str(tp, tl), str(mp, ml), str(sp, sl)),
    now_ms: () => Date.now(),
    monotonic_ns: () => process.hrtime.bigint(),
    random_seed: () => 12345,
    utc_offset_minutes: () => 0,
    capture_stack: (ptr, cap) => {
      const text = visibleFrames().map(f => `   at ${f.name}` + (f.loc ? ` in ${f.loc.file}:line ${f.loc.line}` : '')).join('\n');
      const n = Math.min(text.length, cap);
      const u16 = new Uint16Array(getMemory().buffer, ptr, n);
      for (let i = 0; i < n; i++) u16[i] = text.charCodeAt(i);
      return n;
    },
    stack_depth: () => visibleFrames().length,
  };
}

/** thread_spawn for Node: a worker_threads Worker per thread, sharing the module and memory. */
export function threadImports(module, memory, out, onSpawn) {
  return {
    thread_spawn: (sp, tls, id, arg) => {
      onSpawn?.();
      const w = new Worker(new URL('./worker.mjs', import.meta.url), { workerData: { module, memory, args: [sp, tls, id, arg] } });
      w.on('message', (m) => { if (m.unhandled) { process.stderr.write(m.unhandled); process.exit(3); } });
      w.on('error', (e) => { process.stderr.write(String(e && e.stack || e) + '\n'); process.exit(4); });
      return 0;
    },
  };
}
