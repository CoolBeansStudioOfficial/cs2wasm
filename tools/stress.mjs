// Runs one built module many times, several at once, and counts its distinct outputs: for rare failures that show
// only under load (test 139's leak, 072_http's out of bounds).
//   node tools/stress.mjs <module.wasm> [--runs n] [--par k] [--expect "text"] [-- extra host args]
// A run that exits with an error has its output and whole stderr appended to <module.wasm>.fail.txt.
// Prints each distinct output (stdout, stderr's last lines, exit code) with how often it came; exit code 1 when any
// run differed from --expect (or, without it, when there was more than one distinct output).
import { spawn } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const wasm = args[0];
const runs = +opt('--runs', 100), par = +opt('--par', 15);
const expect = opt('--expect', null);
const timeout = +opt('--timeout', 120000);
const counts = new Map();
let started = 0, finished = 0, bad = 0;
const t0 = Date.now();
function one() {
  if (started >= runs) return Promise.resolve();
  started++;
  return new Promise((done) => {
    const p = spawn('node', [join(root, 'host', 'run.mjs'), wasm, 'main'], { cwd: root });
    let out = '', err = '';
    p.stdout.on('data', (d) => { out += d; });
    p.stderr.on('data', (d) => { err += d; });
    const timer = setTimeout(() => { err += '\n(timeout)'; p.kill(); }, timeout);
    p.on('close', (code) => {
      clearTimeout(timer);
      finished++;
      const o = out.trim();
      const key = `${o}${code ? ` [exit ${code}] ${err.trim().split(/\r?\n/).slice(-6).join(' | ')}` : ''}`;
      counts.set(key, (counts.get(key) ?? 0) + 1);
      // (the whole of a failed run's stderr, next to the module)
      if (code) appendFileSync(wasm + '.fail.txt', `==== run ${finished}, exit ${code}\n${out}\n---- stderr\n${err}\n`);
      if (expect != null ? (o !== expect || code !== 0) : false) { bad++; process.stdout.write(`run ${finished}: ${key}\n`); }
      if (finished % 25 === 0) process.stdout.write(`(${finished}/${runs}, ${bad} bad, ${((Date.now() - t0) / 1000).toFixed(0)} s)\n`);
      done(one());
    });
  });
}
await Promise.all(Array.from({ length: par }, one));
for (const [k, n] of [...counts].sort((a, b) => b[1] - a[1])) console.log(`${String(n).padStart(5)}  ${k}`);
console.log(`${runs} runs, ${par} at once, ${((Date.now() - t0) / 1000).toFixed(0)} s`);
process.exit(expect != null ? (bad ? 1 : 0) : (counts.size > 1 ? 1 : 0));
