// node tools/profphase.mjs in.cpuprofile out.cpuprofile [regex]
// A profile restricted to one phase: the samples whose stack has a frame matching the regex (default: the bind
// bench's bind phase, GetOperation or Descendants, in cs2wasm's names and NativeAOT-LLVM's) keep their time; the
// others get none. The other profile tools (profcompare, profsize, profsum) then see the phase alone.
import { readFileSync, writeFileSync } from 'node:fs';
const [, , inp, out, re = 'GetOperation|Descendants'] = process.argv;
const rx = new RegExp(re);
const p = JSON.parse(readFileSync(inp, 'utf8'));
const parent = new Map(), byId = new Map();
for (const n of p.nodes) { byId.set(n.id, n); for (const c of n.children ?? []) parent.set(c, n.id); }
const memo = new Map();
function inPhase(id) {
  if (memo.has(id)) return memo.get(id);
  const n = byId.get(id);
  const r = rx.test(n.callFrame.functionName) || (parent.has(id) ? inPhase(parent.get(id)) : false);
  memo.set(id, r);
  return r;
}
let kept = 0, all = 0;
for (let i = 0; i < p.samples.length; i++) {
  const d = p.timeDeltas[i] ?? 0;
  all += d;
  // (a sample's time is the delta that precedes the next one: profcompare charges timeDeltas[i] to samples[i])
  if (inPhase(p.samples[i])) kept += d; else p.timeDeltas[i] = 0;
}
writeFileSync(out, JSON.stringify(p));
console.log(`${out}: ${(kept / 1000).toFixed(0)} ms of ${(all / 1000).toFixed(0)} ms in the phase`);
