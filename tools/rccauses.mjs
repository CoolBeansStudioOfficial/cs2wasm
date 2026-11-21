// Sums a CS2WASM_COUNT_RC run's count calls by the compiler code that asked for them (the cause after each site), the
// kind of count with it: where the counts a program runs come from. Compile with CS2WASM_COUNT_RC=1,
// CS2WASM_COUNT_RC_SITES=sites.txt and CS2WASM_RC_FAST=0; run with CS2WASM_RC_NAMES=sites.txt and
// CS2WASM_COUNT_RC_TOP=10000000, the host's listing on stderr into run.txt.
//   node tools/rccauses.mjs run.txt [sites.txt] [--top N] [--depth D] [--fn]
// The struct helpers' own counts (rt.retain_struct, rt.release_struct, rt.copy_*, rt.move_*) are charged to the sites
// that call them, in proportion to their calls ("via" rows). --depth D: the cause's first D callers only (coarser
// kinds); --fn: by the function the site is in instead of its cause; sites.txt: the static count of sites beside.
import { readFileSync } from 'node:fs';

const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const files = args.filter((a, i) => !a.startsWith('--') && !(i > 0 && ['--top', '--depth'].includes(args[i - 1])));
const top = +opt('--top', 40), depth = +opt('--depth', 99), byFn = args.includes('--fn');
const isHelper = (n) => /^rt\.(retain_struct|release_struct|copy_|move_)/.test(n);
const parse = (rest) => {
  const m = /^(retain|release|\[[^\]]*\]) (.*?) @\d+(?: \{(.*)\})?\s*$/.exec(rest);
  if (!m) return null;
  return { kind: m[1], fn: m[2], cause: m[3] };
};
const keyOf = (r) => {
  if (byFn) return r.fn;
  if (/^(rt\.|Cs2Wasm\.Runtime\.)/.test(r.fn)) return `(runtime ${r.fn.split(' ')[0]})`;
  return r.cause ? r.cause.split(' <- ').slice(0, depth).join(' <- ') : '(no cause: a runtime function inlined here, or a pass)';
};
const rows = [];
for (const line of readFileSync(files[0], 'utf8').split(/\r?\n/)) {
  const m = /^\s*(\d+)\s+(.*)$/.exec(line);
  const r = m && parse(m[2]);
  if (r) { r.n = +m[1]; rows.push(r); }
}
// a helper's count operations per call, its callees' included
const own = new Map(), calls = new Map(), callsFrom = new Map();
for (const r of rows) {
  if (!r.kind.startsWith('[')) { if (isHelper(r.fn)) own.set(r.fn, (own.get(r.fn) ?? 0) + r.n); continue; }
  const h = r.kind.slice(1, -1);
  calls.set(h, (calls.get(h) ?? 0) + r.n);
  if (isHelper(r.fn)) { const m = callsFrom.get(r.fn) ?? new Map(); m.set(h, (m.get(h) ?? 0) + r.n); callsFrom.set(r.fn, m); }
}
const memo = new Map();
const perCall = (h, d = 0) => {
  if (memo.has(h)) return memo.get(h);
  if (d > 20 || !calls.get(h)) return 0;
  let inner = own.get(h) ?? 0;
  for (const [g, c] of callsFrom.get(h) ?? []) inner += c * perCall(g, d + 1);
  const v = inner / calls.get(h);
  memo.set(h, v);
  return v;
};
const dyn = new Map(), stat = new Map();
let total = 0;
const add = (k, n) => { dyn.set(k, (dyn.get(k) ?? 0) + n); total += n; };
for (const r of rows) {
  if (isHelper(r.fn)) continue;
  if (r.kind.startsWith('[')) {
    const h = r.kind.slice(1, -1);
    add(`via ${h.split(' ')[0]} {${keyOf(r)}}`, r.n * perCall(h));
  } else add(`${r.kind} {${keyOf(r)}}`, r.n);
}
if (files[1]) for (const line of readFileSync(files[1], 'utf8').split(/\r?\n/)) {
  const r = parse(line.trim());
  if (!r || isHelper(r.fn)) continue;
  const k = r.kind.startsWith('[') ? `via ${r.kind.slice(1, -1).split(' ')[0]} {${keyOf(r)}}` : `${r.kind} {${keyOf(r)}}`;
  stat.set(k, (stat.get(k) ?? 0) + 1);
}
console.log(`${Math.round(total).toLocaleString()} count operations (calls of rt.retain, rt.release, and the struct helpers' own counts)`);
for (const [k, n] of [...dyn].sort((a, b) => b[1] - a[1]).slice(0, top))
  console.log(`${Math.round(n).toLocaleString().padStart(13)} ${(n / total * 100).toFixed(1).padStart(5)}% ${files[1] ? String(stat.get(k) ?? 0).padStart(7) + ' sites ' : ''} ${k.slice(0, 230)}`);
