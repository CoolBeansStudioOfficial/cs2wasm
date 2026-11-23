// A thread of a module run in a browser (SPEC section 9: the toolchain in a Web Worker; section 4.5: threads are
// instances on one shared memory). The page (host/web/run-module.mjs) starts one worker in the role "main", which
// runs the module's entry and blocks as a thread may, and a pool in the role "thread": a browser starts a worker
// only from an event loop that is free, and a module's thread that asks for another is not, so the pool is made by
// the page before the entry runs and a thread_spawn hands its arguments to an idle one through shared memory.
//
// The control block (Int32 words), a slot of 8 words per pool worker: [state, sp, tls, id, arg, background]; state
// 0 idle, 1 claimed by a spawner, 2 arguments written, 3 running, 4 not there yet.
import { webImports } from './il2wasm-web.mjs';
import { vfsImports } from './vfs.mjs';

const SLOT = 8;
const never = new Int32Array(new SharedArrayBuffer(4));

self.onmessage = (ev) => {
  const m = ev.data;
  if (m.role === 'thread') thread(m); else main(m);
};

function setup(m, canWrite, fdBase) {
  const { module, memory } = m;
  const ctl = new Int32Array(m.ctl);
  const out = {
    mainCanBlock: true, // (a worker may wait)
    write: (text) => self.postMessage({ out: text }),
    writeError: (text) => self.postMessage({ err: text }),
    error: (type, message, stack) => self.postMessage({ err: `Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n`, unhandled: true }),
  };
  const fs = vfsImports(() => memory, m.shared, { canWrite, fdBase, out });
  const threads = {
    processor_count: () => m.processors,
    thread_spawn: (sp, tls, id, arg, background) => {
      for (let s = 0; s < m.pool; s++) {
        const at = s * SLOT;
        if (Atomics.compareExchange(ctl, at, 0, 1) !== 0) continue;
        ctl[at + 1] = sp; ctl[at + 2] = tls; ctl[at + 3] = id; ctl[at + 4] = arg; ctl[at + 5] = background;
        Atomics.store(ctl, at, 2);
        Atomics.notify(ctl, at);
        return 0;
      }
      out.writeError(`il2wasm threads: thread ${id} asked for, and all ${m.pool} workers of the pool are busy\n`);
      return -1;
    },
    // (no HTTP in this host: the module's completion pump waits for completions that never come)
    host_next_completion: () => { Atomics.wait(never, 0, 0); return 0; },
  };
  const imports = { env: { memory }, il2wasm: webImports(module, () => memory, out, { args: m.args, env: m.env, epoch: m.epoch, fs: fs.imports, threads }) };
  // any other import is an engine boundary this host does not have
  for (const imp of WebAssembly.Module.imports(module)) {
    if (imp.kind !== 'function') continue;
    imports[imp.module] ??= {};
    imports[imp.module][imp.name] ??= () => { throw new Error(`${imp.module}.${imp.name} is not available in this host`); };
  }
  return { imports, fs, ctl };
}

function thread(m) {
  const { imports, ctl } = setup(m, false, (m.slot + 2) << 16);
  const at = m.slot * SLOT;
  Atomics.store(ctl, at, 0);
  self.postMessage({ ready: m.slot });
  for (;;) {
    Atomics.wait(ctl, at, 0);
    if (Atomics.compareExchange(ctl, at, 2, 3) !== 2) continue;
    try {
      const instance = new WebAssembly.Instance(m.module, imports);
      m.memory.grow(0); // (this thread's view of a memory that grew before it started: host/worker.mjs)
      instance.exports.__thread_entry(ctl[at + 1], ctl[at + 2], ctl[at + 3], ctl[at + 4]);
    } catch (e) {
      self.postMessage({ err: `worker thread failed: ${e?.stack ?? e}\n`, failed: true });
    }
    Atomics.store(ctl, at, 0);
  }
}

function main(m) {
  try {
    const { imports, fs } = setup(m, true, 3);
    const t0 = performance.now();
    const instance = new WebAssembly.Instance(m.module, imports);
    instance.exports.init();
    const t1 = performance.now();
    const exit = instance.exports[m.entry ?? 'main']();
    const t2 = performance.now();
    const files = [...fs.written].map(([path, w]) => [path, w.data.slice(0, w.size)]);
    self.postMessage({ done: true, exit, files, initMs: t1 - t0, runMs: t2 - t1, pages: m.memory.buffer.byteLength / 65536 }, files.map((f) => f[1].buffer));
  } catch (e) {
    self.postMessage({ err: `the module failed: ${e?.stack ?? e}\n`, failed: true, done: true, exit: -1, files: [] });
  }
}
