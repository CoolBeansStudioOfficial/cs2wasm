// The engine imports of a module (module "cs2wasm": host/engine.mjs has their Node form and their list) in a browser:
// console, time, the command line and environment, stack traces, and optionally files (vfs.mjs) and threads (a pool of
// workers on the shared memory, threads.mjs). HTTP is not in this host yet: a module that calls it is told so.
import { debugInfo } from './debuginfo.mjs';

/** The memory a module imports (env.memory: its least and greatest size in pages), read from its bytes. */
export function memoryLimits(bytes) {
  const leb = (p) => { let r = 0, s = 0, x; do { x = bytes[p.i++]; r += (x & 0x7f) * 2 ** s; s += 7; } while (x & 0x80); return r; };
  const p = { i: 8 };
  while (p.i < bytes.length) {
    const id = bytes[p.i++], size = leb(p), end = p.i + size;
    if (id === 2) {
      for (let n = leb(p); n > 0; n--) {
        const name = () => { const l = leb(p); const s = new TextDecoder().decode(bytes.subarray(p.i, p.i + l)); p.i += l; return s; };
        const mod = name(), field = name(), kind = bytes[p.i++];
        if (kind === 0) leb(p);
        else if (kind === 1) { p.i++; const f = leb(p); leb(p); if (f & 1) leb(p); }
        else if (kind === 2) { const f = leb(p), min = leb(p), max = f & 1 ? leb(p) : 65536; if (mod === 'env' && field === 'memory') return { initial: min, maximum: max }; }
        else if (kind === 3) p.i += 2;
        else if (kind === 4) { p.i++; leb(p); }
      }
      break;
    }
    p.i = end;
  }
  return { initial: 256, maximum: 16384 };
}

/** out: { write(text), error(type, message, stack), mainCanBlock }; options: { args: [string], env: { name: value },
 * epoch (performance.timeOrigin of the page, so every thread's clock is one clock), fs (vfs imports),
 * threads ({ processor_count, thread_spawn }) }. */
export function webImports(module, getMemory, out, options = {}) {
  let infoCache = null;
  const info = () => (infoCache ??= debugInfo(module));
  const str = (ptr, len) => {
    const u16 = new Uint16Array(getMemory().buffer, ptr >>> 0, len);
    let s = '';
    for (let i = 0; i < len; i += 4096) s += String.fromCharCode.apply(null, u16.subarray(i, Math.min(len, i + 4096)));
    return s;
  };
  const visibleFrames = () => {
    const saved = Error.stackTraceLimit;
    Error.stackTraceLimit = Infinity;
    const stack = new Error().stack ?? '';
    Error.stackTraceLimit = saved;
    const result = [];
    for (const line of stack.split('\n')) {
      const m = /wasm-function\[(\d+)\]:0x([0-9a-f]+)/.exec(line);
      if (!m) continue;
      result.push(...info().framesAt(+m[1], parseInt(m[2], 16)));
    }
    return result;
  };
  const epoch = options.epoch ?? performance.timeOrigin;
  const imports = {
    write: (ptr, len) => out.write(str(ptr, len)),
    env_block: (buf, size) => {
      const bytes = new TextEncoder().encode(Object.entries(options.env ?? {}).map(([k, v]) => `${k}=${v}\0`).join(''));
      if (buf && size >= bytes.length) new Uint8Array(getMemory().buffer).set(bytes, buf >>> 0);
      return bytes.length;
    },
    args_block: (buf, size) => {
      const args = options.args ?? [''];
      const chars = [args.length];
      for (const a of args) { chars.push(a.length); for (let i = 0; i < a.length; i++) chars.push(a.charCodeAt(i)); }
      if (buf && size >= chars.length) new Uint16Array(getMemory().buffer, buf >>> 0, chars.length).set(chars);
      return chars.length;
    },
    report_exception: (tp, tl, mp, ml, sp, sl) => out.error(str(tp, tl), str(mp, ml), str(sp, sl)),
    main_can_block: () => (out.mainCanBlock ? 1 : 0),
    now_ms: () => Date.now(),
    monotonic_ns: () => BigInt(Math.round((performance.timeOrigin - epoch + performance.now()) * 1e6)),
    random_seed: () => 12345,
    utc_offset_minutes: () => 0,
    capture_stack: (ptr, cap) => {
      const text = visibleFrames().map((f) => `   at ${f.name}` + (f.loc ? ` in ${f.loc.file}:line ${f.loc.line}` : '')).join('\n');
      const n = Math.min(text.length, cap);
      const u16 = new Uint16Array(getMemory().buffer, ptr >>> 0, n);
      for (let i = 0; i < n; i++) u16[i] = text.charCodeAt(i);
      return n;
    },
    stack_depth: () => visibleFrames().length,
    processor_count: () => 1,
    thread_spawn: () => { out.error('NotSupportedException', 'this host starts no threads for the module', ''); return -1; },
    ...(options.fs ?? {}),
    ...(options.threads ?? {}),
  };
  // what the module imports and this host does not have (HTTP): said when called
  for (const imp of WebAssembly.Module.imports(module))
    if (imp.module === 'cs2wasm' && imp.kind === 'function' && !imports[imp.name])
      imports[imp.name] = () => { throw new Error(`cs2wasm.${imp.name} is not available in the browser host yet`); };
  return imports;
}
