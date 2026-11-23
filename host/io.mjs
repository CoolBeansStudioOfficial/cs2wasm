// The I/O worker (http.mjs): every fetch of the process lives here, on an event loop no module thread blocks. It
// answers the threads' synchronous calls (request slots in the control block), reading and writing the module's
// shared memory itself, and reports settled fetches as completions in the ring.
import { workerData } from 'node:worker_threads';
import { IO } from './http.mjs';

const { memory, ctl: ctlBuffer } = workerData;
const c = new Int32Array(ctlBuffer);
const dv = () => new DataView(memory.buffer);
const u8 = () => new Uint8Array(memory.buffer);
const str = (p, len) => { let s = ''; const m = dv(); for (let i = 0; i < len; i++) s += String.fromCharCode(m.getUint16(p + 2 * i, true)); return s; };
// a string block: the count, then each string's length and characters
const readBlock = (p, len) => {
  const m = dv(); const at = (i) => m.getUint16(p + 2 * i, true);
  const n = len > 0 ? at(0) : 0; const r = []; let k = 1;
  for (let i = 0; i < n; i++) { const l = at(k++); let s = ''; for (let j = 0; j < l; j++) s += String.fromCharCode(at(k + j)); r.push(s); k += l; }
  return r;
};
const writeChars = (chars, buf, size) => {
  if (buf && size >= chars.length) { const m = dv(); chars.forEach((ch, i) => m.setUint16(buf + 2 * i, ch, true)); }
  return chars.length;
};
const blockChars = (strings) => {
  const chars = [strings.length];
  for (const s of strings) { chars.push(s.length); for (let i = 0; i < s.length; i++) chars.push(s.charCodeAt(i)); }
  return chars;
};

const messages = new Map();
const complete = (op, result, message) => {
  if (message !== undefined) messages.set(op, message);
  const w = c[1];
  const e = IO.RING + 3 * (w % IO.RING_SIZE);
  c[e] = op; c[e + 1] = result; c[e + 2] = message === undefined ? -1 : message.length;
  Atomics.store(c, 1, w + 1);
  Atomics.notify(c, 1);
};
// as the browser runtime's JSException has it: "TypeError: fetch failed", "AbortError: ..."
const fail = (op, e) => complete(op, 0, String(e?.name && e?.message ? `${e.name}: ${e.message}` : e));

const controllers = new Map(); let next = 1;
const handlers = {
  [IO.create_controller]: () => { const h = next++; controllers.set(h, { abort: new AbortController() }); return h; },
  [IO.abort]: (h) => { controllers.get(h)?.abort.abort(); return 0; },
  [IO.dispose]: (h) => { controllers.delete(h); return 0; },
  [IO.fetch]: (h, op, uri, ulen, block, blen, body, bodyLen) => {
    const ctr = controllers.get(h);
    const all = readBlock(block, blen);
    const hn = Number(all[0]); const names = all.slice(1, 1 + hn), values = all.slice(1 + hn, 1 + 2 * hn);
    const rest = all.slice(1 + 2 * hn); const on = Number(rest[0]);
    const init = { headers: names.map((n, i) => [n, values[i]]), signal: ctr.abort.signal };
    for (let i = 0; i < on; i++) init[rest[1 + i]] = rest[1 + on + i];
    if (bodyLen >= 0 && body) init.body = u8().slice(body >>> 0, (body >>> 0) + bodyLen);
    fetch(str(uri, ulen), init).then((r) => { ctr.response = r; complete(op, 0); }, (e) => fail(op, e));
    return 0;
  },
  [IO.status]: (h) => controllers.get(h)?.response?.status ?? 0,
  [IO.type]: (h, buf, size) => {
    const t = controllers.get(h)?.response?.type ?? '';
    const m = dv(); for (let i = 0; i < Math.min(t.length, size); i++) m.setUint16(buf + 2 * i, t.charCodeAt(i), true);
    return t.length;
  },
  [IO.headers]: (h, buf, size) => {
    const r = controllers.get(h)?.response;
    const names = [], values = [];
    if (r) for (const [n, v] of r.headers) { names.push(n); values.push(v); }
    return writeChars(blockChars([...names, ...values]), buf, size);
  },
  [IO.body_length]: (h, op) => {
    const ctr = controllers.get(h);
    ctr.response.arrayBuffer().then((ab) => { ctr.body = new Uint8Array(ab); complete(op, ctr.body.length); }, (e) => fail(op, e));
    return 0;
  },
  [IO.body]: (h, buf, size) => {
    const b = controllers.get(h)?.body; if (!b) return 0;
    const n = Math.min(size, b.length); u8().set(b.subarray(0, n), buf >>> 0); return n;
  },
  [IO.stream_read]: (h, op, buf, size) => {
    const ctr = controllers.get(h);
    const give = () => {
      const n = Math.min(size, ctr.chunk.length);
      u8().set(ctr.chunk.subarray(0, n), buf >>> 0);
      ctr.chunk = ctr.chunk.subarray(n);
      complete(op, n);
    };
    if (ctr.chunk?.length) { give(); return 0; }
    if (!ctr.response.body) { complete(op, 0); return 0; }
    ctr.reader ??= ctr.response.body.getReader();
    const next = () => ctr.reader.read().then(({ done, value }) => {
      if (done) { complete(op, 0); return; }
      if (!value.length) { next(); return; }
      ctr.chunk = value; give();
    }, (e) => fail(op, e));
    next();
    return 0;
  },
  [IO.message]: (op, buf, len) => {
    const s = messages.get(op) ?? ''; messages.delete(op);
    const m = dv(); for (let i = 0; i < Math.min(len, s.length); i++) m.setUint16(buf + 2 * i, s.charCodeAt(i), true);
    return 0;
  },
};

// (a pending Atomics.waitAsync does not keep a worker alive: this does, while the process lives; the engine
// unrefs the worker, so it does not keep the process)
setInterval(() => {}, 1 << 30);

// the requests: slots in state 2 (written), answered in place (state 3); between scans the event loop runs (the
// fetches' callbacks)
for (;;) {
  const now = Atomics.load(c, 0);
  for (let s = 0; s < IO.SLOT_COUNT; s++) {
    const at = IO.SLOTS + s * IO.SLOT_WORDS;
    if (Atomics.load(c, at) !== 2) continue;
    const args = Array.from(c.subarray(at + 2, at + 14));
    let r = 0;
    try { r = handlers[c[at + 1]](...args) | 0; } catch (e) { process.stderr.write(`il2wasm I/O: ${e && e.stack || e}
`); }
    c[at + 15] = r;
    Atomics.store(c, at, 3);
    Atomics.notify(c, at);
  }
  if (Atomics.load(c, 0) === now) { const w = Atomics.waitAsync(c, 0, now); if (w.async) await w.value; }
  else await new Promise((r) => setImmediate(r));
}
