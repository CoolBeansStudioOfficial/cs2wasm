// Speculative borrows (Memory/Borrows.cs, docs/MEMORY-IR.md 4.6) weighted by executed counts: which counted loads a
// program would borrow and why the others flip, counted per load executed rather than per load site.
//
//   IL2WASM_MEM_NAIVE=1 IL2WASM_BORROW_STATS=1 IL2WASM_BORROW_SITES=borrows.tsv IL2WASM_COUNT_RC=1 ... (one naive build;
//   tools/il-rcstats.mjs run --naive-only --keep dir makes the build and the run: set the two borrow variables too)
//   node tools/il-borrow-weight.mjs <sites.tsv> <run.json> <borrows.tsv> [--top N]
//
// The naive plan retains every counted load right after it, so the executed count of a load's retain site is how
// often the load ran. borrows.tsv lists each function's loads in node order with their decision (borrowed, flip:<why>,
// generic); il-rcstats' sites.tsv lists each function's retain sites in module order with their role (field,
// element, static, local). The two are zipped per (function, role). A (function, role) whose numbers of loads and
// sites differ (the back end inlined or merged sites) is reported as unmatched, with its executed count.
import { readFileSync } from 'node:fs';

const [sitesFile, runFile, borrowsFile, ...rest] = process.argv.slice(2);
if (!borrowsFile) { console.error('usage: il-borrow-weight.mjs sites.tsv run.json borrows.tsv [--top N]'); process.exit(2); }
const top = rest.includes('--top') ? +rest[rest.indexOf('--top') + 1] : 10;
const roles = new Set(['field', 'element', 'static', 'local']);
const norm = fn => fn.replace(/^shared /, '');

const exec = new Map();
for (const [id, n] of JSON.parse(readFileSync(runFile, 'utf8')).counts) exec.set(id, n);
const sites = new Map(); // fn\trole -> [executed...] in id order
for (const line of readFileSync(sitesFile, 'utf8').split(/\r?\n/)) {
  if (!line || line.startsWith('#')) continue;
  const f = line.split('\t');
  if (f[1] !== 'retain' || !roles.has(f[3])) continue;
  const key = norm(f[5]) + '\t' + f[3];
  if (!sites.has(key)) sites.set(key, []);
  sites.get(key).push(exec.get(+f[0]) ?? 0);
}
const loads = new Map(); // fn\trole -> [decision...]
for (const line of readFileSync(borrowsFile, 'utf8').split(/\r?\n/)) {
  if (!line) continue;
  const [fn, role, d] = line.split('\t');
  const key = norm(fn) + '\t' + role;
  if (!loads.has(key)) loads.set(key, []);
  loads.get(key).push(d);
}
const by = new Map(); let total = 0, unmatched = 0, unmatchedKeys = 0, matchedKeys = 0;
const add = (d, n) => { by.set(d, (by.get(d) ?? 0) + n); total += n; };
const hot = [];
for (const [key, ds] of loads) {
  const ex = sites.get(key) ?? [];
  const sum = ex.reduce((a, b) => a + b, 0);
  if (ex.length !== ds.length) { if (sum) { unmatched += sum; unmatchedKeys++; hot.push([sum, key, 'unmatched (' + ds.length + ' loads, ' + ex.length + ' sites)']); } continue; }
  matchedKeys++;
  ds.forEach((d, i) => { add(d, ex[i]); if (ex[i]) hot.push([ex[i], key, d]); });
}
const pct = n => total ? (100 * n / total).toFixed(1) + '%' : '-';
console.log(`executed counted loads (matched): ${total.toLocaleString()}; unmatched ${unmatched.toLocaleString()} in ${unmatchedKeys} function-roles (${matchedKeys} matched)`);
const borrowed = by.get('borrowed') ?? 0;
console.log(`  borrowed ${borrowed.toLocaleString()} (${pct(borrowed)})`);
for (const [d, n] of [...by].filter(([d]) => d !== 'borrowed').sort((a, b) => b[1] - a[1])) console.log(`  ${d.padEnd(16)} ${n.toLocaleString().padStart(14)} (${pct(n)})`);
console.log('hottest loads:');
for (const [n, key, d] of hot.sort((a, b) => b[0] - a[0]).slice(0, top)) console.log(`  ${n.toLocaleString().padStart(12)}  ${d.padEnd(16)} ${key.replace('\t', ' ')}`);
