// Proof 5 (SPEC section 10): hello2d in C#, built in the browser, played in the engine. The steps, each a command:
//   node tools/proof5.mjs oracle [dir]       the game under stock Godot 4.7.2 .NET on the desktop: its engine-call logs
//                                            (oracle-calls.txt and oracle-calls-panic.txt, default in
//                                            tests/Godot/proof5_hello2d)
//   node tools/proof5.mjs stub               the game compiled by the .NET-hosted cs2wasmc, run on the Node stub
//                                            engine replaying the oracle's answers; the calls must be the oracle's
//   node tools/proof5.mjs web                the web variant of the Godot project, exported with the stock templates,
//                                            and everything the page serves (tests/out/proof5/site)
//   node tools/proof5.mjs check              server + headless browser: builds the game in the browser with the
//                                            self-hosted compiler, plays the fixed frames, compares the log
// Environment: CS2WASM_GODOT_EDITOR (the stock editor's console executable), CS2WASM_CACHE, CS2WASM_SELF (the
// self-hosted compiler, default tests/out/self2/cs2wasm.wasm here or in the main checkout), CS2WASM_BROWSER.
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { project, outDir, api, frames, editor, run } from '../tests/Godot/proof5_hello2d/check/common.mjs';

async function main() {
  const [step, ...rest] = process.argv.slice(2);
  mkdirSync(outDir, { recursive: true });
  if (step === 'oracle') {
    if (run('dotnet', ['build', '-c', 'Debug', '-v', 'q', '--nologo'], { cwd: project }) !== 0) process.exit(1);
    run(editor(), ['--headless', '--path', project, '--import']);
    // two fixed inputs: the game played, and the same with the key that makes the script throw
    for (const [file, extra] of [['oracle-calls.txt', []], ['oracle-calls-panic.txt', ['--proof5-panic']]]) {
      const out = resolve(rest[0] ?? project, file);
      const status = run(editor(), ['--headless', '--path', project, '--fixed-fps', String(frames), '--', '--proof5-drive', ...extra],
        { env: { ...process.env, CS2WASM_CALL_LOG: out, CS2WASM_GODOT_API: api } });
      console.log(`oracle: stock Godot exited ${status}; log at ${out}`);
    }
  } else if (step === 'stub') {
    process.exit(await (await import('../tests/Godot/proof5_hello2d/check/stub.mjs')).stub(rest));
  } else if (step === 'web') {
    process.exit(await (await import('../tests/Godot/proof5_hello2d/check/web.mjs')).web(rest));
  } else if (step === 'check') {
    process.exit(await (await import('../tests/Godot/proof5_hello2d/check/browser.mjs')).check(rest));
  } else {
    console.error('usage: node tools/proof5.mjs oracle|stub|web|check');
    process.exit(2);
  }
}
await main();
