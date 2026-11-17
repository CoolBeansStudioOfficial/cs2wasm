// Runs a module with a `main` in a browser page, in Web Workers (SPEC section 9: the compiler in the browser): the
// page's part. It downloads the module and its files (packs: vfs.mjs), makes the shared memory, starts the pool of
// thread workers and the worker that runs the entry (module-worker.mjs), and resolves with what the run wrote.
// The page must be cross-origin isolated (shared memory): host/web/server.mjs sends the headers.
import { memoryLimits } from './cs2wasm-web.mjs';
import { openPack, sharePacks } from './vfs.mjs';

/** A URL's bytes, with what the download was: { bytes, transferred (bytes on the wire, by the browser's own
 * measure; 0 when it does not say), ms }. */
export async function download(url) {
  const t0 = performance.now();
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: ${response.status}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  const entry = performance.getEntriesByName(new URL(url, location.href).href).at(-1);
  return { url, bytes, transferred: entry?.encodedBodySize ?? 0, ms: performance.now() - t0 };
}

/**
 * options: { module: URL, packs: [URL], files: [{ path, bytes }] (added to the packs' files), args: [string] (the
 * command line after the program's name), env: { name: value }, processors (what the module is told it has),
 * pool (workers for its threads), onOutput(text, isError) }.
 * Resolves with { exit, files: Map(path -> Uint8Array) (what the run wrote), output, downloads: [{ url, bytes,
 * transferred, ms }], ms: { download, compile, start, init, run, total }, pages }.
 */
export async function runModule(options) {
  if (!globalThis.crossOriginIsolated) throw new Error('the page is not cross-origin isolated: shared memory needs the COOP and COEP headers');
  const t0 = performance.now();
  const [mod, ...packs] = await Promise.all([download(options.module), ...(options.packs ?? []).map(download)]);
  const t1 = performance.now();
  const module = await WebAssembly.compile(mod.bytes);
  const t2 = performance.now();
  const limits = memoryLimits(mod.bytes);
  const memory = new WebAssembly.Memory({ initial: limits.initial, maximum: limits.maximum, shared: true });
  const shared = sharePacks(packs.map((p) => openPack(p.bytes)), options.files ?? []);
  const pool = options.pool ?? 16, processors = options.processors ?? Math.max(1, Math.min(4, navigator.hardwareConcurrency ?? 1));
  const ctl = new SharedArrayBuffer(4 * 8 * Math.max(pool, 1));
  new Int32Array(ctl).fill(4);
  const common = { module, memory, shared, ctl, pool, processors, args: ['cs2wasm', ...(options.args ?? [])], env: options.env ?? {}, epoch: performance.timeOrigin, entry: options.entry };
  const workers = [];
  let output = '';
  const say = (text, isError) => { output += text; options.onOutput?.(text, isError); };
  const url = new URL('./module-worker.mjs', import.meta.url);
  try {
    const result = await new Promise((done, fail) => {
      let ready = 0;
      const listen = (w) => {
        w.onerror = (e) => fail(new Error(`worker: ${e.message ?? e}`));
        w.onmessage = (ev) => {
          const m = ev.data;
          if (m.out !== undefined) say(m.out, false);
          if (m.err !== undefined) say(m.err, true);
          if (m.ready !== undefined && ++ready === pool) startMain();
          if (m.done) done(m);
        };
      };
      let tStart = 0;
      const startMain = () => {
        tStart = performance.now();
        const w = new Worker(url, { type: 'module' });
        workers.push(w); listen(w);
        w.postMessage({ role: 'main', ...common });
      };
      for (let slot = 0; slot < pool; slot++) {
        const w = new Worker(url, { type: 'module' });
        workers.push(w); listen(w);
        w.postMessage({ role: 'thread', slot, ...common });
      }
      if (pool === 0) startMain();
    });
    const t3 = performance.now();
    return {
      exit: result.exit, files: new Map(result.files), output, pages: result.pages,
      downloads: [mod, ...packs].map(({ url, bytes, transferred, ms }) => ({ url, bytes: bytes.length, transferred, ms })),
      ms: { download: t1 - t0, compile: t2 - t1, start: t3 - t2 - result.initMs - result.runMs, init: result.initMs, run: result.runMs, total: t3 - t0 },
    };
  } finally {
    for (const w of workers) w.terminate();
  }
}
