// Proof 5's page (SPEC section 10): builds the game's C# sources in the browser with the self-hosted compiler (in Web
// Workers), instantiates the module beside stock Godot's web export, and plays it in the engine through the bridge.
// The page's address chooses:
//   ?module=<url>    a module built elsewhere, in place of the build in the browser (to try the engine's part alone)
//   ?drive=1         the fixed input (driver.gd), after which the game quits; ?drive=panic: with the key that throws
//   ?build=only      build, and do not start the engine
//   ?game=hello      the sources to build: a directory of the site with a sources.json (default: game)
// What happened is left in window.proof5 ({ state, build, log, error }) for tests/Godot/proof5_hello2d/check/browser.mjs.
import { runModule } from './host/web/run-module.mjs';
import { webImports, memoryLimits } from './host/web/il2wasm-web.mjs';
import { attachGame } from './host/web/godot-web.mjs';
import { comparable } from './host/godot-bridge.mjs';

const params = new URLSearchParams(location.search);
const result = (window.proof5 = { state: 'starting', build: null, log: null, error: null, output: '' });
const status = (text) => { document.getElementById('status').textContent = text; result.state = text; };
const print = (text) => { result.output += text; const o = document.getElementById('output'); o.textContent += text; o.scrollTop = o.scrollHeight; };

async function build() {
  status('downloading the game\'s sources');
  const manifest = await (await fetch((params.get('game') ?? 'game') + '/sources.json')).json();
  const files = await Promise.all(manifest.sources.map(async (s) => ({ path: s.path, bytes: new Uint8Array(await (await fetch(s.url)).arrayBuffer()) })));
  files.push({ path: '/out/.keep', bytes: new Uint8Array(0) });
  status('building in the browser (downloading the toolchain, then compiling)');
  const r = await runModule({
    module: 'toolchain/il2wasm.wasm', packs: ['toolchain/library.pack'], files,
    args: [...manifest.sources.map((s) => s.path), ...manifest.args, '-o', '/out/game.wasm'],
    env: { IL2WASM_CACHE: '/cache', IL2WASM_REFERENCE_DIR: '/ref', ...(params.has('times') ? { IL2WASM_TIMES: '1' } : {}) },
    // (one core unless the address says more: with several, the toolchain's builds of a program with GodotSharp did
    // not end under Node; ?times=1: the compiler's phase times in the output)
    processors: params.has('processors') ? +params.get('processors') : 1,
    onOutput: (text) => print(text),
  });
  const wasm = r.files.get('/out/game.wasm');
  result.build = { exit: r.exit, bytes: wasm?.length ?? 0, downloads: r.downloads, ms: r.ms, pages: r.pages, sources: files.reduce((s, f) => s + f.bytes.length, 0) };
  if (r.exit !== 0 || !wasm) throw new Error(`the build failed (exit ${r.exit})`);
  print(`built game.wasm: ${wasm.length} bytes in ${(r.ms.total / 1000).toFixed(1)} s (download ${(r.ms.download / 1000).toFixed(1)} s, compiling the toolchain's module ${(r.ms.compile / 1000).toFixed(1)} s, the build ${(r.ms.run / 1000).toFixed(1)} s)\n`);
  result.wasm = wasm;
  // a console program (its sources.json names its entry): run here, in the page, and its output kept
  if (manifest.run) {
    const module = await WebAssembly.compile(wasm);
    const limits = memoryLimits(wasm);
    const memory = new WebAssembly.Memory({ initial: limits.initial, maximum: limits.maximum, shared: true });
    let text = '';
    const out = { mainCanBlock: false, write: (s) => { text += s; }, error: (type, message, stack) => { text += `Unhandled exception. ${type}: ${message}\n${stack}\n`; } };
    const instance = await WebAssembly.instantiate(module, { env: { memory }, il2wasm: webImports(module, () => memory, out, {}) });
    instance.exports.init();
    instance.exports[manifest.run]();
    result.ran = text;
    print(`ran its ${manifest.run}: ${JSON.stringify(text)}\n`);
  }
  return wasm;
}

async function play(wasm) {
  status('starting the engine');
  result.wasm = wasm;
  const sigs = await (await fetch('toolchain/signatures.json')).json();
  const module = await WebAssembly.compile(wasm);
  const limits = memoryLimits(wasm);
  let ended;
  const done = new Promise((r) => { ended = r; });
  const out = {
    mainCanBlock: false, // (the page's thread may not wait)
    write: print,
    print: (text) => print(text + '\n'),
    printError: (text) => print('ERROR: ' + text + '\n'),
    error: (type, message, stack) => print(`Unhandled exception. ${type}: ${message}${stack ? '\n' + stack : ''}\n`),
    failed: (e) => { result.error ??= String(e?.stack ?? e); },
    ended: () => ended(),
  };
  const game = await attachGame(module, sigs, out, async (godot) => {
    const memory = new WebAssembly.Memory({ initial: limits.initial, maximum: limits.maximum, shared: true });
    const instance = await WebAssembly.instantiate(module, { env: { memory }, il2wasm: webImports(module, () => memory, out, {}), godot });
    return { instance, memory };
  });
  const config = await (await fetch('godot/config.json')).json();
  const drive = params.get('drive');
  // (the fixed input runs at a fixed step, one physics tick a frame however long a frame takes, as the oracle's run
  // does; a person plays in real time)
  const args = drive ? ['--fixed-fps', '60', '--', '--proof5-drive', ...(drive === 'panic' ? ['--proof5-panic'] : [])] : [];
  const engine = new Engine({
    ...config, executable: 'godot/index', mainPack: 'godot/index.pck', canvas: document.getElementById('canvas'), args, canvasResizePolicy: 0,
    onPrint: (...a) => print('[engine] ' + a.join(' ') + '\n'), onPrintError: (...a) => print('[engine error] ' + a.join(' ') + '\n'),
    onExit: () => ended(),
  });
  await engine.startGame();
  result.live = game;
  status(drive ? 'playing the fixed input' : 'playing: arrows or A/D move, Space jumps, P throws');
  if (!drive) { document.getElementById('canvas').focus(); return; }
  await done;
  result.log = game.log;
  result.calls = game.bridge.calls;
  result.rounds = game.backend.round;
  // stock Godot .NET's log of the same input (the site has a copy): the verdict on the page, for a person watching
  // (check/browser.mjs compares again on its own side)
  const oracle = await fetch(drive === 'panic' ? 'oracle-calls-panic.txt' : 'oracle-calls.txt');
  if (oracle.ok) {
    const expected = (await oracle.text()).replaceAll('\r\n', '\n').split('\n');
    if (expected.at(-1) === '') expected.pop();
    let first = -1;
    for (let i = 0; i < Math.max(expected.length, game.log.length) && first < 0; i++) if (comparable(expected[i] ?? '') !== comparable(game.log[i] ?? '')) first = i;
    result.equal = first < 0;
    print(first < 0 ? `the engine-call log (${game.log.length} lines) equals stock Godot .NET's\n`
      : `the engine-call log DIFFERS from stock Godot .NET's at line ${first + 1}:\n  stock  ${expected[first]}\n  module ${game.log[first]}\n`);
  }
  status('done');
  if (result.equal !== undefined) document.getElementById('status').textContent = `done: the engine calls ${result.equal ? 'equal' : 'DIFFER from'} stock Godot .NET's`;
}

try {
  const wasm = params.has('module') ? new Uint8Array(await (await fetch(params.get('module'))).arrayBuffer()) : await build();
  if (params.get('build') === 'only') status('done'); else await play(wasm);
} catch (e) {
  result.error = String(e?.stack ?? e);
  print('FAILED: ' + result.error + '\n');
  status('failed');
}
