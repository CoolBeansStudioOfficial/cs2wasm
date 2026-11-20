// The thread starter (Node): a worker of the main thread that runs none of the module, only this loop, so its event
// loop is never busy. Every thread of the module is started here, whichever thread asks (engine.mjs, thread_spawn):
// a Node worker's own workers need its event loop to start, and a thread of the module never gives its loop back
// (it runs the module until it ends): a worker started by such a thread could wait forever to start, and the thread
// waiting for it with it (the self-hosted compiler on 12 threads: PLINQ's producers were such threads, never run).
// The ring (in memory every thread shares): [0] a bell rung after a request, [1] threads running that keep the process
// alive, then slots of six words: state (0 free, 2 being written, 1 asked), stack top, thread block, id, argument,
// background.
import { workerData, parentPort, Worker } from 'node:worker_threads';

const { module, memory, fs, io, argv, ring } = workerData;
const c = new Int32Array(ring);
const slots = (c.length - 2) / 6;
// A worker takes tens of milliseconds to start (a new engine instance, its imports, the module's instance): one is
// kept ready, made now and again each time it is taken, so a thread started now and then runs at once.
let spare = null;
const make = (start) => {
  const w = new Worker(new URL('./worker.mjs', import.meta.url), { workerData: { module, memory, start, fs, io, argv, ring } });
  // (what a worker reports goes to the main thread, which ends the process)
  w.on('message', (m) => parentPort.postMessage(m));
  w.on('error', (e) => parentPort.postMessage({ error: String(e && e.stack || e) }));
  return w;
};
const makeSpare = () => { spare = make(null); spare.unref(); };
makeSpare();
// (a pending Atomics.waitAsync does not keep a worker alive: this does, while the process lives; the main thread
// unrefs this worker but while threads that keep the process alive run, ring word 1)
setInterval(() => {}, 1 << 30);

for (;;) {
  // (the bell read before the slots: a request written after the scan rings it again, and the wait sees the change)
  const bell = Atomics.load(c, 0);
  for (let i = 0; i < slots; i++) {
    const at = 2 + i * 6;
    if (Atomics.load(c, at) !== 1) continue;
    const start = { args: [c[at + 1], c[at + 2], c[at + 3], c[at + 4]], background: c[at + 5] };
    Atomics.store(c, at, 0);
    let w = spare;
    spare = null;
    if (w) { if (!start.background) w.ref(); w.postMessage(start); }
    else { w = make(start); if (start.background) w.unref(); } // background threads do not keep the process alive, as in .NET
    makeSpare();
  }
  const r = Atomics.waitAsync(c, 0, bell);
  if (r.async) await r.value;
}
