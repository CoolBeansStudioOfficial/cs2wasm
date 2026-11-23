// Proof 5's check (node tools/proof5.mjs check [options]): serves tests/out/proof5/site (node tools/proof5.mjs web
// makes it), opens the page in a headless browser, and there: the game's sources are built by the self-hosted compiler,
// the module is played in stock Godot's web export with the fixed input, and the engine-call log is compared with the
// oracle's (stock Godot .NET on the desktop). Reports the build's time in the browser and the bytes downloaded.
//   --module <game.wasm>   play a module built elsewhere (no build in the browser): the engine's part alone
//   --build-only           build in the browser and stop (no engine)
//   --game hello           build the site's console program (repro/Hello.cs) in place of the game, and stop
//   --panic                the fixed input with the key that makes the script throw (oracle-calls-panic.txt)
//   --timeout <seconds>    how long the page may take (default 3600)
//   --play <file.png>      no fixed input: the game played through the browser's keyboard (right arrow, Space), the
//                          HUD's text before and after, and a picture of the page
//   --headed               a browser window to watch; --keep: leave the server and the browser up at the end
//   --processors <n>       the cores the compiler is told it has (default: the page's choice, one)
//   --times                the compiler's phase times in the page's output (tests/out/proof5/browser-output.txt)
// The browser: IL2WASM_BROWSER, else Chrome or Edge where they are installed.
import { existsSync, mkdtempSync, readFileSync, writeFileSync, cpSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { spawn } from 'node:child_process';
import { root, project, outDir } from './common.mjs';
import { compareLogs } from './stub.mjs';
import { site } from './web.mjs';
import { serve } from '../../../../host/web/server.mjs';

function browserPath() {
  const candidates = [process.env.IL2WASM_BROWSER, 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', 'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'];
  const found = candidates.find((p) => p && existsSync(p));
  if (!found) throw new Error('no browser: set IL2WASM_BROWSER to a Chrome or Edge executable');
  return found;
}

// the DevTools protocol over the browser's WebSocket: send(method, params) -> result, and events to on[method]
async function devtools(wsUrl) {
  const ws = new WebSocket(wsUrl);
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = () => fail(new Error('no DevTools connection')); });
  let next = 1;
  const pending = new Map(), on = {};
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id) { const p = pending.get(m.id); pending.delete(m.id); if (m.error) p.fail(new Error(m.error.message)); else p.ok(m.result); }
    else on[m.method]?.(m.params);
  };
  const send = (method, params = {}) => new Promise((ok, fail) => { const id = next++; pending.set(id, { ok, fail }); ws.send(JSON.stringify({ id, method, params })); });
  return { send, on, close: () => ws.close() };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const n = (x) => Math.round(x).toLocaleString('en-US');

export async function check(args) {
  const opt = { timeout: 3600 };
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--module') opt.module = args[++i];
    else if (args[i] === '--timeout') opt.timeout = +args[++i];
    else if (args[i] === '--processors') opt.processors = args[++i];
    else if (args[i] === '--game') opt.game = args[++i];
    else if (args[i] === '--play') opt.play = args[++i];
    else opt[args[i].replace(/^--/, '').replace(/-(.)/g, (_, c) => c.toUpperCase())] = true;
  }
  if (!existsSync(join(site, 'index.html')) || !existsSync(join(site, 'godot', 'index.wasm'))) { console.error('check: no site: node tools/proof5.mjs web'); return 1; }
  // (the page's copies of host files are the tree's: a site made before an edit would test the old ones)
  for (const f of ['index.html', 'main.mjs']) cpSync(join(project, 'web', f), join(site, f));
  for (const f of ['oracle-calls.txt', 'oracle-calls-panic.txt']) cpSync(join(project, f), join(site, f)); // (the page compares with them too)
  cpSync(join(root, 'host', 'godot-bridge.mjs'), join(site, 'host', 'godot-bridge.mjs'));
  for (const f of ['il2wasm-web.mjs', 'debuginfo.mjs', 'godot-web.mjs', 'module-worker.mjs', 'run-module.mjs', 'vfs.mjs']) cpSync(join(root, 'host', 'web', f), join(site, 'host', 'web', f));
  // host/web/debuginfo.mjs is host/engine.mjs's reader of the debug sections: the two must not drift
  {
    const norm = (s) => s.replaceAll('\r\n', '\n');
    const engine = norm(readFileSync(join(root, 'host', 'engine.mjs'), 'utf8')), copy = norm(readFileSync(join(root, 'host', 'web', 'debuginfo.mjs'), 'utf8'));
    const part = engine.slice(engine.indexOf('function readU32'), engine.indexOf('export function il2wasmImports'));
    if (!copy.endsWith(part)) { console.error('check: host/web/debuginfo.mjs is not host/engine.mjs\'s debugInfo any more: copy it again'); return 1; }
  }
  const query = new URLSearchParams();
  if (opt.module) { cpSync(opt.module, join(site, 'prebuilt.wasm')); query.set('module', 'prebuilt.wasm'); }
  if (opt.buildOnly) query.set('build', 'only'); else query.set('drive', opt.panic ? 'panic' : '1');
  if (opt.processors) query.set('processors', opt.processors);
  if (opt.times) query.set('times', '1');
  if (opt.game) { query.set('game', opt.game); opt.buildOnly = true; query.set('build', 'only'); query.delete('drive'); }
  if (opt.play) query.delete('drive');
  const game = opt.game ?? 'game';

  const server = await serve(site, 0);
  const profile = mkdtempSync(join(tmpdir(), 'il2wasm-proof5-'));
  const exe = browserPath();
  const browser = spawn(exe, [...(opt.headed ? [] : ['--headless=new']), '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
    '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows',
    '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--window-size=1280,900', 'about:blank'], { stdio: 'ignore' });
  let failure = '';
  const finish = async (code) => {
    if (opt.keep) { console.log(`check: left up: ${server.url}?${query} (stop with Ctrl+C)`); await new Promise(() => {}); }
    browser.kill(); server.server.close(); server.server.closeAllConnections?.();
    await sleep(300);
    try { rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch { /* the browser still holds it */ }
    return code;
  };
  try {
    let port = null;
    for (let i = 0; i < 100 && !port; i++) { await sleep(100); const f = join(profile, 'DevToolsActivePort'); if (existsSync(f)) port = readFileSync(f, 'utf8').split('\n')[0]; }
    if (!port) throw new Error('the browser did not start its DevTools port');
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    const page = pages.find((p) => p.type === 'page');
    const cdp = await devtools(page.webSocketDebuggerUrl);
    const lines = [];
    cdp.on['Runtime.consoleAPICalled'] = (p) => { const t = p.args.map((a) => a.value ?? a.description ?? '').join(' '); lines.push(t); if (opt.verbose) console.log('[page] ' + t); };
    cdp.on['Runtime.exceptionThrown'] = (p) => { const t = p.exceptionDetails.exception?.description ?? p.exceptionDetails.text; lines.push(t); console.log('[page exception] ' + t); };
    await cdp.send('Runtime.enable');
    await cdp.send('Page.enable');
    const version = await cdp.send('Browser.getVersion');
    console.log(`check: ${version.product} (${exe}), ${opt.headed ? 'with a window' : 'headless'}; page ${server.url}?${query}`);
    const evaluate = async (expression) => (await cdp.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })).result.value;
    const t0 = performance.now();
    await cdp.send('Page.navigate', { url: `${server.url}?${query}` });
    let state = '', last = '';
    for (;;) {
      await sleep(1000);
      const s = JSON.parse((await evaluate('JSON.stringify(window.proof5 ? { state: proof5.state, error: proof5.error } : { state: "loading" })')) ?? '{"state":"loading"}');
      state = s.state;
      if (state !== last) { console.log(`check: [${((performance.now() - t0) / 1000).toFixed(0)} s] ${state}`); last = state; }
      if (state === 'done' || state === 'failed' || (opt.play && state.startsWith('playing:'))) break;
      if (performance.now() - t0 > opt.timeout * 1000) { failure = `the page did not finish in ${opt.timeout} s (at: ${state})\n`; break; }
    }
    if (opt.play && state.startsWith('playing:')) {
      // the game played by keys, as a person would: the right arrow held for a second, then Space; the HUD's text
      // (the script's last Label.set_text) before and after, and a picture of the page
      const hud = () => evaluate('proof5.live.log.filter((l) => l.startsWith("Label.set_text")).at(-1) ?? ""');
      const key = (type, k, code, vk) => cdp.send('Input.dispatchKeyEvent', { type, key: k, code, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk });
      await evaluate('document.getElementById("canvas").focus()');
      await sleep(1500);
      const before = await hud();
      await key('rawKeyDown', 'ArrowRight', 'ArrowRight', 39); await sleep(1000); await key('keyUp', 'ArrowRight', 'ArrowRight', 39);
      await key('rawKeyDown', ' ', 'Space', 32); await sleep(100); await key('keyUp', ' ', 'Space', 32);
      await sleep(250);
      const shot = await cdp.send('Page.captureScreenshot', { format: 'png' });
      writeFileSync(opt.play, Buffer.from(shot.data, 'base64'));
      await sleep(1500);
      const after = await hud();
      const error = await evaluate('proof5.error');
      console.log(`check: played by keys: the HUD before: ${before}\ncheck:                 and after: ${after}\ncheck: a picture of the page in the air: ${opt.play}`);
      if (error) failure += `the page failed: ${error}\n`;
      if (!/jumps: 1/.test(after) || before === after) failure += 'the keys did not move the character and make it jump\n';
      if (failure) { console.log('check: FAILED\n' + failure); return finish(1); }
      console.log('check: the game answers the keyboard in the engine');
      return finish(0);
    }
    const r = JSON.parse(await evaluate('JSON.stringify({ build: proof5.build, error: proof5.error, log: proof5.log, ran: proof5.ran, calls: proof5.calls, rounds: proof5.rounds, output: proof5.output.slice(-6000), isolated: crossOriginIsolated, cores: navigator.hardwareConcurrency })'));
    const stats = server.stats();
    writeFileSync(join(outDir, 'browser-output.txt'), r.output ?? '');
    if (r.error) failure += `the page failed: ${r.error}\n`;
    if (state === 'failed' || failure) failure += `the page's output (its end):\n${(r.output ?? '').slice(-3000)}\n`;

    // what the browser downloaded: by the server's count of the bytes it sent
    const sent = (prefix) => stats.requests.filter((q) => q.url.startsWith(prefix) && q.status === 200);
    const sum = (list) => list.reduce((s, q) => s + q.bytes, 0);
    if (r.build) {
      const b = r.build;
      const built = await evaluate('(() => { const w = proof5.wasm; let s = ""; for (let i = 0; i < w.length; i += 32768) s += String.fromCharCode.apply(null, w.subarray(i, i + 32768)); return btoa(s); })()').catch(() => null);
      if (built) writeFileSync(join(outDir, 'game-browser.wasm'), Buffer.from(built, 'base64'));
      console.log(`check: built in the browser: game.wasm ${n(b.bytes)} bytes, exit ${b.exit}, in ${(b.ms.total / 1000).toFixed(1)} s (downloads ${(b.ms.download / 1000).toFixed(1)} s, compiling the compiler's module ${(b.ms.compile / 1000).toFixed(1)} s, starting its workers ${(b.ms.start / 1000).toFixed(1)} s, its init ${(b.ms.init / 1000).toFixed(1)} s, the build itself ${(b.ms.run / 1000).toFixed(1)} s); the compiler's memory at the end: ${n(b.pages * 65536 / 1048576)} MB; the browser reports ${r.cores} cores`);
      const tool = sent('/toolchain/').filter((q) => !q.url.endsWith('signatures.json')), src = sent(`/${game}/`);
      console.log(`check: downloaded to build: ${n(sum(tool) + sum(src))} bytes on the wire (${tool.map((q) => `${q.url.slice(11)} ${n(q.bytes)}${q.encoded ? ' Brotli' : ' not compressed'}`).join(', ')}; the program's sources ${n(sum(src))} in ${src.length} files); uncompressed: ${b.downloads.map((d) => n(d.bytes)).join(' + ')} bytes`);
      const others = opt.game === 'hello' ? [['the self-hosted compiler under Node', 'hello-self.wasm']]
        : [['the self-hosted compiler under Node', 'game-self.wasm'], ['the .NET-hosted compiler from the same sources', 'game-net-gen.wasm']];
      for (const [name, file] of others) {
        const p = join(outDir, file);
        if (built && existsSync(p)) console.log(`check: the module built in the browser ${Buffer.from(built, 'base64').equals(readFileSync(p)) ? 'has the same bytes as' : 'DIFFERS from'} the one built by ${name} (${file})`);
      }
      if (r.ran !== undefined) {
        console.log(`check: the built program, run in the page, wrote ${JSON.stringify(r.ran)}`);
        if (opt.game === 'hello' && r.ran.trim() !== '42') failure += 'the built program did not write 42\n';
      }
    }
    if (!opt.buildOnly && r.log) {
      const eng = sent('/godot/');
      console.log(`check: the engine: ${n(sum(eng))} bytes (${eng.map((q) => `${q.url.slice(7)} ${n(q.bytes)}`).join(', ')}); the bridge's table of methods: ${n(sum(sent('/toolchain/signatures.json')))} bytes`);
      const expected = readFileSync(join(project, opt.panic ? 'oracle-calls-panic.txt' : 'oracle-calls.txt'), 'utf8').replaceAll('\r\n', '\n').split('\n');
      if (expected.at(-1) === '') expected.pop();
      writeFileSync(join(outDir, 'browser-calls.txt'), r.log.join('\n') + '\n');
      console.log(`check: played in the engine: ${r.log.length} log lines (${r.calls} engine calls from the module, ${r.rounds} round trips to the engine), the oracle has ${expected.length}; log at ${join(outDir, 'browser-calls.txt')}`);
      const diff = compareLogs(expected, r.log);
      if (diff) failure += `engine calls differ from stock Godot's:\n${diff}`;
    } else if (!opt.buildOnly && !failure) failure += 'the page has no engine-call log\n';
  } catch (e) {
    failure += `the check failed: ${e?.stack ?? e}\n`;
  }
  if (failure) { console.log('check: FAILED\n' + failure); return finish(1); }
  console.log(opt.buildOnly ? 'check: the build in the browser succeeded' : 'check: the engine-call log of the game played in the browser equals the oracle\'s');
  return finish(0);
}
