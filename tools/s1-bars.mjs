// Slice S1's removal-share bar (docs/MEMORY-IR.md 11, S1; docs/S1-BARS.md) over the kept builds of tools/il-rcstats.mjs
// run --keep <dir>: per program the share overall, without the runtime and per owner, the live objects against .NET,
// and the remaining count operations of the planned build by category and by site.
//   node tools/s1-bars.mjs <keep dir>... [--top N] [--md]
// Each keep dir holds <name>.naive.wasm.{sites.tsv,run.json} and <name>.plan.wasm.{sites.tsv,run.json} (built with
// --live). --oracles <dir>...: directories of tools/il-live-oracle results, <program>.json, for the live comparison.
// Lower/RcStats.cs gives a specialization variant (a function named "<method>|ownN", or "shared <method>|ownN") no
// method, so it reports it as "generated", and il-rcstats leaves "generated" out of "without the runtime". Here a
// variant's sites are given the owner of the method its name names, by the same rules as RcStats.OwnerOf.
import { readdirSync, existsSync, readFileSync } from 'node:fs';
import { join, basename, dirname } from 'node:path';
import { analyze, live } from './il-rcstats.mjs';

const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const top = +opt('--top', 10);
const oracleDirs = []; { const i = args.indexOf('--oracles'); if (i >= 0) for (let j = i + 1; j < args.length && !args[j].startsWith('--'); j++) oracleDirs.push(args[j]); }
const dirs = args.filter((a, i) => !a.startsWith('--') && args[i - 1] !== '--top' && !oracleDirs.includes(a));

export function variantOwner(fn) {
  const m = /^(?:shared )?\[([^\]]+)\]([^(|]*).*\|own\d+/.exec(fn);
  if (!m) return null;
  const asm = m[1], name = m[2];
  if (name.startsWith('Il2Wasm.Runtime')) return 'runtime';
  const lib = asm === 'S.P.CoreLib' || asm === 'System.Private.CoreLib' || asm === 'netstandard' || asm === 'mscorlib' || asm.startsWith('System.') || asm.startsWith('Microsoft.');
  if (!lib) return 'program';
  return name.startsWith('System.Collections') ? 'containers' : 'library';
}

const isRuntime = (r) => r.owner === 'runtime' || r.owner === 'generated' && r.role !== 'destroy';
function load(dir, kind) {
  const f = readdirSync(dir).find((x) => x.endsWith(`.${kind}.wasm.sites.tsv`));
  if (!f) return null;
  const base = join(dir, f.slice(0, -'.sites.tsv'.length));
  const a = analyze(base + '.sites.tsv', base + '.run.json');
  let moved = 0;
  for (const r of a.rows) if (r.owner === 'generated') { const o = variantOwner(r.fn); if (o) { r.owner = o; moved += r.ops; } }
  a.moved = moved;
  return a;
}
const sum = (rows, f = (r) => r.ops) => rows.reduce((x, r) => x + f(r), 0);
const by = (rows, key) => { const m = new Map(); for (const r of rows) m.set(key(r), (m.get(key(r)) ?? 0) + r.ops); return m; };
const pct = (a, b) => (b > 0 ? (100 * a / b).toFixed(1) : '-');
const share = (p, n) => (n > 0 ? 100 * (1 - p / n) : NaN);
const fmt = (n) => Math.round(n).toLocaleString('en-US');

const results = [];
for (const d of dirs) {
  const n = load(d, 'naive'), p = load(d, 'plan');
  if (!n || !p) { console.error(`${d}: no naive or planned build`); continue; }
  const name = basename(d).replace(/^keep-/, '');
  const of = oracleDirs.map((o) => join(o, name + '.json')).find((f) => existsSync(f));
  const oracle = of ? JSON.parse(readFileSync(of, 'utf8')) : null;
  // (the program's assembly: the one its own functions are named with)
  const asm = n.sites.find((s) => s && s.owner === 'program' && /^\[[^\]]+\]/.test(s.fn))?.fn.match(/^\[([^\]]+)\]/)[1];
  const res = { naive: { live: n.run.live ? live(n, oracle, asm) : null }, plan: { live: p.run.live ? live(p, oracle, asm) : null } };
  const owners = ['program', 'containers', 'library', 'runtime', 'generated'];
  const nO = by(n.rows, (r) => r.owner), pO = by(p.rows, (r) => r.owner);
  results.push({
    name, naive: sum(n.rows), plan: sum(p.rows),
    naiveNoRt: sum(n.rows.filter((r) => !isRuntime(r))), planNoRt: sum(p.rows.filter((r) => !isRuntime(r))),
    owners: Object.fromEntries(owners.map((o) => [o, { naive: nO.get(o) ?? 0, plan: pO.get(o) ?? 0 }])),
    moved: p.moved, plan_rows: p.rows, res,
  });
}

const md = args.includes('--md');
console.log('| program | naive ops | planned ops | removed | removed without the runtime | program | containers | library | runtime | generated (destroy etc.) |');
console.log('|---|---|---|---|---|---|---|---|---|---|');
for (const r of results) {
  const o = (k) => { const v = r.owners[k]; return v.naive || v.plan ? `${share(v.plan, v.naive).toFixed(1)}% (${fmt(v.plan)}/${fmt(v.naive)})` : '-'; };
  console.log(`| ${r.name} | ${fmt(r.naive)} | ${fmt(r.plan)} | ${share(r.plan, r.naive).toFixed(1)}% | ${share(r.planNoRt, r.naiveNoRt).toFixed(1)}% | ${o('program')} | ${o('containers')} | ${o('library')} | ${o('runtime')} | ${o('generated')} |`);
}
// totals: summed operations (dominated by the largest runs) and the mean of the per-program shares
const T = (f) => results.reduce((x, r) => x + f(r), 0);
console.log(`\nsummed over the ${results.length} programs: ${share(T((r) => r.plan), T((r) => r.naive)).toFixed(1)}% removed (${fmt(T((r) => r.plan))} of ${fmt(T((r) => r.naive))}); without the runtime ${share(T((r) => r.planNoRt), T((r) => r.naiveNoRt)).toFixed(1)}%`);
console.log(`mean of the per-program shares: ${(T((r) => share(r.plan, r.naive)) / results.length).toFixed(1)}%; without the runtime ${(T((r) => share(r.planNoRt, r.naiveNoRt)) / results.length).toFixed(1)}%; programs at 90% or more: ${results.filter((r) => share(r.plan, r.naive) >= 90).length} of ${results.length}`);
console.log(`(variant sites moved from "generated" to their method's owner: ${results.map((r) => `${r.name} ${fmt(r.moved)}`).join(', ')})`);

// what remains in the planned builds: by category (each program weighted by its naive count, so each program's
// remaining share sums to its 100 - removed), and the top sites
console.log('\nremaining count operations by category, as a share of each program\'s naive count:');
const cats = new Set();
for (const r of results) for (const x of r.plan_rows) cats.add(x.category);
const catRows = [...cats].map((c) => ({ c, v: results.map((r) => 100 * sum(r.plan_rows.filter((x) => x.category === c)) / r.naive) }));
catRows.sort((a, b) => b.v.reduce((x, y) => x + y, 0) - a.v.reduce((x, y) => x + y, 0));
console.log(`| category | mean | ${results.map((r) => r.name).join(' | ')} |`);
console.log(`|---|---|${results.map(() => '---').join('|')}|`);
for (const { c, v } of catRows) console.log(`| ${c} | ${(v.reduce((x, y) => x + y, 0) / v.length).toFixed(1)} | ${v.map((x) => x.toFixed(1)).join(' | ')} |`);

console.log('\ntop remaining sites per program (planned build):');
for (const r of results) {
  console.log(`-- ${r.name}`);
  for (const x of [...r.plan_rows].sort((a, b) => b.ops - a.ops).slice(0, top))
    console.log(`  ${pct(x.ops, r.naive).padStart(5)}% of naive  ${fmt(x.ops).padStart(12)}  ${x.kind} ${x.owner}/${x.role}/${x.category}  ${x.fn}  [${x.context}]`);
}

console.log('\nlive objects at exit (program types against .NET):');
console.log('| program | naive live | naive program types differing | planned live | planned program types differing | differences |');
console.log('|---|---|---|---|---|---|');
for (const r of results) {
  const L = r.res, nl = L?.naive?.live, pl = L?.plan?.live;
  const d = (l) => l?.diff ? (l.equal ? `equal (${l.programTypes} types)` : `${l.programDiff} of ${l.programTypes}`) : 'no oracle';
  const list = (l) => (l?.diff ?? []).filter((x) => x.program).map((x) => `${x.type} ${x.il2wasm} vs ${x.dotnet}`).join('; ');
  console.log(`| ${r.name} | ${nl?.total ?? '-'} | ${d(nl)} | ${pl?.total ?? '-'} | ${d(pl)} | ${[list(nl) && 'naive: ' + list(nl), list(pl) && 'planned: ' + list(pl)].filter(Boolean).join(' / ')} |`);
}
