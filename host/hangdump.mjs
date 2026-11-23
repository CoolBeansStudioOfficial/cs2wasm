// CS2WASM_HANG_DUMP=<seconds> (host/run.mjs): a run still going after that long prints the stack of every thread,
// the main thread's and every worker's (the module's threads, the starter), to stderr, then ends the process
// (killed). For a rare hang: what each thread waits in. A watcher thread of its own, which runs none of the
// module, reaches the others through the inspector (a thread in a wasm loop or a wait is interrupted to answer).
//   CS2WASM_HANG_DUMP=20 node host/run.mjs hang.wasm main
import { Worker, isMainThread, workerData } from 'node:worker_threads';

export function startHangDump(seconds) {
  const w = new Worker(new URL(import.meta.url), { workerData: { hangDump: +seconds } });
  w.unref();
}

if (!isMainThread && workerData?.hangDump) {
  const { Session } = await import('node:inspector');
  const { writeSync } = await import('node:fs');
  const ms = workerData.hangDump * 1000;
  await new Promise((r) => setTimeout(r, ms));
  // (an inspector's answer does not keep this thread's loop alive)
  setInterval(() => {}, 1 << 30);
  const session = new Session();
  session.connectToMainThread();
  // a target: the main thread's session, or a worker's reached through its parent's (sendMessageToWorker)
  let nextId = 1;
  const pending = new Map();   // "target/id" -> resolve
  const handlers = new Map();  // target -> (method, params) => void
  const targets = [];          // { key, title, send }
  const lines = [];
  const say = (s) => lines.push(s);
  const makeTarget = (key, title, rawSend) => {
    const t = { key, title, send: (method, params = {}) => new Promise((res) => { const id = nextId++; pending.set(`${key}/${id}`, res); rawSend(JSON.stringify({ id, method, params })); }) };
    targets.push(t);
    return t;
  };
  const onMessage = (key, msg) => {
    if (msg.id !== undefined) { const r = pending.get(`${key}/${msg.id}`); if (r) { pending.delete(`${key}/${msg.id}`); r(msg.result ?? msg); } return; }
    handlers.get(key)?.(msg.method, msg.params);
  };
  const main = makeTarget('main', 'main thread', null);
  main.send = (method, params = {}) => new Promise((res) => session.post(method, params, (err, r) => res(err ? { error: String(err) } : r)));
  const paused = new Map(); // key -> call frames
  const attach = (t) => {
    handlers.set(t.key, (method, params) => {
      if (method === 'Debugger.paused') paused.get(t.key)?.(params.callFrames);
      else if (method === 'NodeWorker.attachedToWorker') {
        const sid = params.sessionId;
        if (String(params.workerInfo.url).includes('hangdump')) return; // (this watcher)
        const child = makeTarget(`${t.key}>${sid}`, `${params.workerInfo.title || 'worker'} ${params.workerInfo.workerId}`,
          (m) => t.send('NodeWorker.sendMessageToWorker', { sessionId: sid, message: m }));
        attach(child);
        found.push(child);
      } else if (method === 'NodeWorker.receivedMessageFromWorker') {
        onMessage(`${t.key}>${params.sessionId}`, JSON.parse(params.message));
      }
    });
  };
  session.on('inspectorNotification', (m) => handlers.get('main')?.(m.method, m.params));
  attach(main);
  const found = [main];
  const stackOf = async (t) => {
    await t.send('Debugger.enable', {});
    const frames = await new Promise((res) => { paused.set(t.key, res); t.send('Debugger.pause', {}); setTimeout(() => res(null), 3000); });
    if (!frames) return '  (did not pause in 3 s: idle, no code running: the thread starter or a spare worker)';
    const out = frames.map((f) => `  at ${f.functionName || '(anonymous)'} (${f.url || ''}:${f.location.lineNumber}:${f.location.columnNumber})`).join('\n');
    t.send('Debugger.resume', {});
    return out;
  };
  // every thread: the main thread's workers, then theirs
  for (let i = 0; i < found.length; i++) {
    const t = found[i];
    if (t.key.split('>').length > 4) continue;
    await t.send('NodeWorker.enable', { waitForDebuggerOnStart: false });
    await new Promise((r) => setTimeout(r, 300));
  }
  say(`[hang dump] after ${workerData.hangDump} s, ${found.length} threads:`);
  for (const t of found) say(`-- ${t.title}\n${await stackOf(t)}`);
  writeSync(2, lines.join('\n') + '\n');
  process.kill(process.pid, 'SIGKILL');
}
