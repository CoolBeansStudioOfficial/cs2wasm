// Runs the conformance suite in shards side by side (tests/Cs2Wasm.Tests --shard k/n), checked and as shipped at
// once, and prints the failures and the totals: the whole suite in a few minutes instead of a quarter of an hour.
//   node tools/suite.mjs [--shards n] [--checked-only | --unchecked-only] [filter] [-- extra runner args]
// Each runner holds a compiler (1 to 2 GB at the largest programs): the default, 5 per mode, keeps ten of them under
// 20 GB. Builds the runner once first. Exit code: 1 when anything failed.
import { spawn, execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const shards = +opt('--shards', 5);
const modes = args.includes('--checked-only') ? [false] : args.includes('--unchecked-only') ? [true] : [false, true];
const dd = args.indexOf('--');
const extra = dd >= 0 ? args.slice(dd + 1) : [];
const filter = args.slice(0, dd >= 0 ? dd : args.length).find((a, i, all) => !a.startsWith('--') && all[i - 1] !== '--shards');

execFileSync('dotnet', ['build', join(root, 'tests', 'Cs2Wasm.Tests'), '-c', 'Debug', '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
const t0 = Date.now();
const runs = [];
for (const unchecked of modes)
  for (let k = 0; k < shards; k++)
    runs.push(new Promise((done) => {
      const a = ['run', '--project', join(root, 'tests', 'Cs2Wasm.Tests'), '--no-build', '--', ...(filter ? [filter] : []), '--shard', `${k}/${shards}`, ...(unchecked ? ['--unchecked'] : []), ...extra];
      const p = spawn('dotnet', a, { cwd: root });
      let out = '';
      p.stdout.on('data', (d) => { out += d; });
      p.stderr.on('data', (d) => { out += d; });
      p.on('close', (code) => done({ unchecked, k, out, code }));
    }));
const results = await Promise.all(runs);
let failed = false;
for (const unchecked of modes) {
  let pass = 0, fail = 0, gaps = 0;
  const failures = [];
  for (const r of results.filter((r) => r.unchecked === unchecked)) {
    const m = /(\d+) passed, (\d+) failed(?:, (\d+) known gap)?/.exec(r.out);
    if (!m) { failures.push(`shard ${r.k}: no summary (exit ${r.code})\n${r.out.slice(-2000)}`); fail++; continue; }
    pass += +m[1]; fail += +m[2]; gaps += +(m[3] ?? 0);
    // each FAIL with its indented lines
    const lines = r.out.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++)
      if (lines[i].startsWith('FAIL ')) { const b = [lines[i]]; while (i + 1 < lines.length && lines[i + 1].startsWith('  ')) b.push(lines[++i]); failures.push(b.slice(0, 14).join('\n')); }
  }
  console.log(`== ${unchecked ? 'as shipped' : 'checked'}: ${pass} passed, ${fail} failed${gaps ? `, ${gaps} known gaps` : ''}`);
  for (const f of failures) console.log(f);
  if (fail > 0) failed = true;
}
console.log(`(${((Date.now() - t0) / 1000).toFixed(0)} s, ${shards} shards per mode)`);
process.exit(failed ? 1 : 0);
