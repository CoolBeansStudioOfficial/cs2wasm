// Prints the stack of every thread (the main one and each worker, workers' workers too) of a node process started
// with --inspect=<port>: for a module that hangs (a deadlock: who waits where). A thread that does not pause is in a
// wait with no limit, a host call, or the engine.
//   node tools/stacks.mjs [port=9229] [frames=25]      (PAUSE_MS: how long to wait for the threads to pause, 3000)
const port = +(process.argv[2] ?? 9229), depth = +(process.argv[3] ?? 25);
const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
const ws = new WebSocket(list[0].webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let id = 0;
// A session: the main thread's (path []) or a worker's, reached through the sessions of its parents (path of ids).
const sessions = [];
const pending = new Map();
function wrap(path, msg) {
  // the innermost first: each level is a NodeWorker.sendMessageToWorker of the one inside it
  let m = msg;
  for (let i = path.length - 1; i >= 0; i--) m = { id: ++id, method: 'NodeWorker.sendMessageToWorker', params: { sessionId: path[i], message: JSON.stringify(m) } };
  return m;
}
function send(s, method, params = {}) {
  return new Promise((r) => { const i = ++id; pending.set(`${s.path.join('/')}#${i}`, r); ws.send(JSON.stringify(wrap(s.path, { id: i, method, params }))); });
}
function onMessage(path, m) {
  if (process.env.STACKS_DEBUG && m.method) console.log('msg', path.join('/'), m.method, m.method === 'NodeWorker.attachedToWorker' ? m.params.workerInfo.title : '');
  if (process.env.STACKS_DEBUG && m.error) console.log('err', path.join('/'), JSON.stringify(m.error));
  const s = sessions.find((x) => x.path.join('/') === path.join('/'));
  if (m.method === 'NodeWorker.receivedMessageFromWorker') return onMessage([...path, m.params.sessionId], JSON.parse(m.params.message));
  const key = `${path.join('/')}#${m.id}`;
  if (m.id && pending.has(key)) { pending.get(key)(m); pending.delete(key); return; }
  if (m.method === 'NodeWorker.attachedToWorker') {
    const child = { path: [...path, m.params.sessionId], title: m.params.workerInfo.title, frames: null };
    sessions.push(child);
    send(child, 'NodeWorker.enable', { waitForDebuggerOnStart: false });
  }
  if (m.method === 'Debugger.paused' && s) s.frames = m.params.callFrames;
}
ws.addEventListener('message', (ev) => onMessage([], JSON.parse(ev.data)));
const main = { path: [], title: 'main', frames: null };
sessions.push(main);
await send(main, 'NodeWorker.enable', { waitForDebuggerOnStart: false });
await sleep(3000); // (the workers, and theirs, attach)
for (const s of sessions) { send(s, 'Debugger.enable').then(() => send(s, 'Debugger.pause')); }
await sleep(+(process.env.PAUSE_MS ?? 3000));
for (const s of sessions) {
  console.log(`== ${s.title} (${s.path.length ? 'depth ' + s.path.length : 'main'})`);
  if (process.env.STACKS_RAW && s.frames && !s.frames.some((f) => (f.url || '').startsWith('wasm'))) console.log(JSON.stringify(s.frames.slice(0, 3).map((f) => ({ n: f.functionName, u: f.url, l: f.location, sc: f.scopeChain.map((c) => c.type) }))));
  if (!s.frames) { console.log('   (did not pause: a wait with no limit, a host call, the engine, or idle)'); continue; }
  for (const f of s.frames.slice(0, depth)) console.log('   ' + (f.functionName || '(anonymous)') + (f.url && !f.url.startsWith('wasm') ? '  ' + f.url.split('/').pop() + ':' + (f.location.lineNumber + 1) : ''));
}
process.exit(0);
