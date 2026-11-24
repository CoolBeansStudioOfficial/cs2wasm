// The remaining count operations of planned builds, grouped by cause (docs/S1-BARS.md, "top causes"): each cause is
// a rule over a site (function, role, context, as tools/il-rcstats.mjs gives them), counted as a share of each
// program's naive count operations.
//   node tools/s1-causes.mjs <il-rcstats keep dir>...
import { readdirSync } from 'node:fs';
import { join, basename } from 'node:path';
import { analyze } from './il-rcstats.mjs';

const causes = [
  ['box/unbox pairs in generic comparers (box of a T that is a value type, then unbox or a null test)', (r) => /(GenericArraySortHelper|GenericEqualityComparer|GenericComparer|ObjectEqualityComparer|EqualityComparer)`1<(int|uint|long|ulong|short|byte|char|bool|double|float|native)/.test(r.fn) && /\b(box|new)\b/.test(r.role)],
  ['a recursive call given the owned variant: a field load retained to be handed over, released in the callee', (r) => /\|own\d+/.test(r.fn) && /FlipField|OwnedConvention/.test(r.category) || /\|own\d+/.test(r.fn) && r.role === 'parameter' && /Unwind|Generic/.test(r.category) && /I32Load\+\d+ ; \[Call rt.retain\]|LocalGet p0 ; \[Call rt.release\]/.test(r.context)],
  ['a loop-carried reference phi (n = n.Next): released and retained each iteration', (r) => r.role === 'phi' || r.category === 'Phi'],
  ['an array parameter retained for a returned interior address (MemoryMarshal.GetArrayDataReference and similar byref getters)', (r) => /GetArrayDataReference|GetReference|GetTwoDigitsBytesRef|GetRawStringData|GetNonNullPinnableReference/.test(r.fn)],
  ['a result retained by a getter of a static or of this (EqualityComparer.Default, ArrayPool.Shared, Encoding.UTF8, StringBuilder.Append returning this)', (r) => r.role === 'return' && /Result|Unwind|Generic/.test(r.category) && /(get_Default|get_Shared|get_UTF8|get_InvariantCulture|get_CurrentCulture|StringBuilder\.Append)/.test(r.fn)],
  ['the returned-address keeper ring (Rt.ReturnKeepers)', (r) => /ReturnKeepers/.test(r.fn)],
  ['destroy (children released when an object is freed: a floor for a freed tree)', (r) => r.role === 'destroy'],
  ['old-value releases at stores (StoreOld)', (r) => r.category === 'StoreOld'],
];

const dirs = process.argv.slice(2);
const rows = [];
for (const d of dirs) {
  const f = (k) => readdirSync(d).find((x) => x.endsWith(`.${k}.wasm.sites.tsv`));
  const nf = f('naive'), pf = f('plan');
  if (!nf || !pf) continue;
  const load = (x) => analyze(join(d, x), join(d, x.replace(/\.sites\.tsv$/, '.run.json')));
  const n = load(nf), p = load(pf);
  const naive = n.rows.reduce((a, r) => a + r.ops, 0), plan = p.rows.reduce((a, r) => a + r.ops, 0);
  const got = causes.map(() => 0);
  let rest = 0; const restBy = new Map();
  for (const r of p.rows) { const i = causes.findIndex(([, t]) => t(r)); if (i >= 0) got[i] += r.ops; else { rest += r.ops; const k = `${r.role}/${r.category}`; restBy.set(k, (restBy.get(k) ?? 0) + r.ops); } }
  rows.push({ name: basename(d).replace(/^keep-/, ''), naive, plan, got, rest, restBy });
}
const pc = (a, b) => (100 * a / b).toFixed(1);
console.log(`| cause | mean (points of naive) | programs above 1 point | largest |`);
console.log('|---|---|---|---|');
const order = causes.map((c, i) => ({ c, i, mean: rows.reduce((a, r) => a + 100 * r.got[i] / r.naive, 0) / rows.length })).sort((a, b) => b.mean - a.mean);
for (const { c, i, mean } of order) {
  const big = rows.filter((r) => 100 * r.got[i] / r.naive > 1).sort((a, b) => b.got[i] / b.naive - a.got[i] / a.naive);
  console.log(`| ${c[0]} | ${mean.toFixed(1)} | ${big.length} | ${big.slice(0, 4).map((r) => `${r.name.slice(0, 28)} ${pc(r.got[i], r.naive)}`).join(', ')} |`);
}
console.log(`| everything else | ${(rows.reduce((a, r) => a + 100 * r.rest / r.naive, 0) / rows.length).toFixed(1)} | | |`);
console.log(`| (remaining in all, the mean of 100 - removed) | ${(rows.reduce((a, r) => a + 100 * r.plan / r.naive, 0) / rows.length).toFixed(1)} | | |`);
console.log('\neverything else, by role/category (mean points of naive):');
const keys = new Set(rows.flatMap((r) => [...r.restBy.keys()]));
const kk = [...keys].map((k) => ({ k, mean: rows.reduce((a, r) => a + 100 * (r.restBy.get(k) ?? 0) / r.naive, 0) / rows.length })).sort((a, b) => b.mean - a.mean);
for (const { k, mean } of kk.slice(0, 12)) console.log(`  ${k.padEnd(34)} ${mean.toFixed(2)}`);
