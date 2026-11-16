// HTTP for a module through the host's fetch (SPEC section 8: every module gets HTTP as .NET's browser target does):
// the cs2wasm.http_* imports of System.Net.Http's browser handler (src/Cs2Wasm/Upstream/System.Net.Http/Boundary/
// BrowserHttpInterop.cs).
//
// A fetch settles on the event loop of the JavaScript thread that started it, and a module's threads seldom return to
// theirs (a Task.Wait spins or waits). So every fetch lives in one I/O worker (io.mjs), plain JavaScript, whose event
// loop is always free: each thread's http_* import is a synchronous call into it over shared memory (a request slot,
// Atomics.wait for the answer; the worker reads and writes the module's memory itself). A settled fetch is a
// completion in a shared ring; the module's pump thread (Cs2Wasm.Runtime.HostOps) takes them through
// host_next_completion and completes the operations' tasks.
//
// The control block (Int32 words): [0] request count (the worker waits on it), [1] completions written,
// [2] completions read, [16..) the completion ring (op, result, message length), [IO_SLOTS..) the request slots
// (state, function, arguments..., result).
import { Worker } from 'node:worker_threads';

export const IO = {
  RING: 16, RING_SIZE: 1024, SLOTS: 16 + 3 * 1024, SLOT_WORDS: 16, SLOT_COUNT: 64,
  // functions
  create_controller: 1, abort: 2, dispose: 3, fetch: 4, status: 5, type: 6, headers: 7, body_length: 8, body: 9, message: 10, stream_read: 11,
};

/** The process's I/O state (shared with every thread: worker.mjs). */
export function ioShared() {
  return globalThis.__cs2wasmIo ??= { ctl: new SharedArrayBuffer(4 * (IO.SLOTS + IO.SLOT_WORDS * IO.SLOT_COUNT)) };
}

/** Starts the I/O worker for a module that imports HTTP (once, on the engine thread). */
export function startIo(module, memory) {
  if (!WebAssembly.Module.imports(module).some((i) => i.module === 'cs2wasm' && i.name === 'http_fetch')) return;
  const shared = ioShared();
  if (shared.started) return;
  shared.started = true;
  const w = new Worker(new URL('./io.mjs', import.meta.url), { workerData: { memory, ctl: shared.ctl } });
  w.on('error', (e) => { process.stderr.write(String(e && e.stack || e) + '\n'); process.exit(4); });
  w.unref(); // (pending fetches do not keep the process alive once the program is done)
}

export function httpImports(getMemory) {
  const ctl = () => new Int32Array(ioShared().ctl);
  // a synchronous call into the I/O worker
  const call = (fn, ...args) => {
    const c = ctl();
    for (;;) {
      for (let s = 0; s < IO.SLOT_COUNT; s++) {
        const at = IO.SLOTS + s * IO.SLOT_WORDS;
        if (Atomics.compareExchange(c, at, 0, 1) !== 0) continue;
        c[at + 1] = fn;
        args.forEach((a, i) => { c[at + 2 + i] = a; });
        Atomics.store(c, at, 2);
        Atomics.add(c, 0, 1); Atomics.notify(c, 0);
        while (Atomics.load(c, at) !== 3) Atomics.wait(c, at, 2, 50);
        const r = c[at + 15];
        Atomics.store(c, at, 0);
        return r;
      }
      Atomics.wait(c, 0, Atomics.load(c, 0), 1); // every slot busy: try again shortly
    }
  };
  return {
    http_create_controller: () => call(IO.create_controller),
    http_abort: (h) => { call(IO.abort, h); },
    http_dispose: (h) => { call(IO.dispose, h); },
    http_fetch: (h, op, uri, ulen, block, blen, body, bodyLen) => { call(IO.fetch, h, op, uri, ulen, block, blen, body, bodyLen); },
    http_status: (h) => call(IO.status, h),
    http_type: (h, buf, size) => call(IO.type, h, buf, size),
    http_headers: (h, buf, size) => call(IO.headers, h, buf, size),
    http_body_length: (h, op) => { call(IO.body_length, h, op); },
    http_body: (h, buf, size) => call(IO.body, h, buf, size),
    http_stream_read: (h, op, buf, size) => { call(IO.stream_read, h, op, buf, size); },
    /** The pump's wait for the next completion: its operation, with the result and message length written at the
     * pointers (a message of length 0 or more is then read with host_completion_message). */
    host_next_completion: (resultPtr, lenPtr) => {
      const c = ctl();
      for (;;) {
        const w = Atomics.load(c, 1), r = c[2];
        if (r !== w) {
          const e = IO.RING + 3 * (r % IO.RING_SIZE);
          const dv = new DataView(getMemory().buffer);
          dv.setInt32(resultPtr >>> 0, c[e + 1], true);
          dv.setInt32(lenPtr >>> 0, c[e + 2], true);
          const op = c[e];
          Atomics.store(c, 2, r + 1);
          return op;
        }
        Atomics.wait(c, 1, w);
      }
    },
    host_completion_message: (op, buf, len) => { call(IO.message, op, buf, len); },
  };
}
