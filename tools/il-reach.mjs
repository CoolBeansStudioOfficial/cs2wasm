// What a module built by the IL front end is made of, and why each part is reached.
//
// usage: node tools/il-reach.mjs <reach file> <sizes file> [--top n] [--chain text] [--ns] [--types text]
//   (IL2WASM_IL_REACH=<reach file> IL2WASM_SIZES_FILE=<sizes file> when compiling)
//
// Prints the code's total, the namespaces by size, and the methods whose first-reach subtree is largest (each
// method's own size plus that of everything it first reached: what cutting it alone would remove, as an upper bound).
// --chain text: the first-reach chain of each method whose name contains text. --types text: the constructed types
// whose name contains text, with what made each.
import fs from 'node:fs';

const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const [reachFile, sizesFile] = args;
const top = +opt('--top', 50);
const sizes = new Map();
for (const l of fs.readFileSync(sizesFile, 'utf8').split(/\r?\n/)) {
  if (!l) continue;
  const t = l.indexOf('\t');
  sizes.set(l.slice(t + 1), (sizes.get(l.slice(t + 1)) ?? 0) + +l.slice(0, t));
}
const parent = new Map(), via = new Map(), types = [];
for (const l of fs.readFileSync(reachFile, 'utf8').split(/\r?\n/)) {
  if (!l) continue;
  const p = l.split('\t');
  if (p[0] === 'M') {
    if (p[2] === 'D') { parent.set(p[1], p[3]); via.set(p[1], p[4]); }
    else parent.set(p[1], p[3]);
  } else if (p[0] === 'T') types.push([p[1], p[2]]);
}
const size = n => sizes.get(n) ?? 0;
let total = 0;
for (const v of sizes.values()) total += v;
console.log(`code ${total} bytes in ${sizes.size} functions; ${parent.size} methods and stubs reached`);

// namespaces (the method's type's namespace: the text before the last '.' ahead of the name's '(')
const nsOf = n => {
  let s = n.replace(/^\[[^\]]*\]/, '');
  const paren = s.indexOf('(');
  if (paren >= 0) s = s.slice(0, paren);
  s = s.replace(/<[^<>]*>/g, '').replace(/<[^<>]*>/g, '');
  const parts = s.split('.');
  // namespace: the parts that start lower-case-free up to the type (heuristic: drop the last two: type and method)
  return parts.slice(0, Math.max(1, parts.length - 2)).join('.');
};
if (args.includes('--ns') || true) {
  const by = new Map();
  for (const [n, v] of sizes) { const k = nsOf(n); by.set(k, (by.get(k) ?? 0) + v); }
  console.log('\nby namespace:');
  for (const [k, v] of [...by].sort((a, b) => b[1] - a[1]).slice(0, 30)) console.log(`  ${String(v).padStart(8)}  ${k}`);
}

// inclusive sizes over the first-reach tree
const children = new Map();
for (const [c, p] of parent) { if (!children.has(p)) children.set(p, []); children.get(p).push(c); }
const incl = new Map();
const inclOf = n => {
  if (incl.has(n)) return incl.get(n);
  incl.set(n, 0);
  let s = size(n);
  for (const c of children.get(n) ?? []) s += inclOf(c);
  incl.set(n, s);
  return s;
};
for (const n of parent.keys()) inclOf(n);
for (const p of children.keys()) inclOf(p);
console.log(`\nlargest first-reach subtrees (own, inclusive):`);
for (const [n, v] of [...incl].sort((a, b) => b[1] - a[1]).slice(0, top))
  console.log(`  ${String(size(n)).padStart(7)} ${String(v).padStart(8)}  ${n.length > 150 ? n.slice(0, 150) : n}${via.has(n) ? '  [on ' + via.get(n) + ']' : ''}`);

const chainText = opt('--chain');
if (chainText) {
  for (const n of parent.keys()) {
    if (!n.includes(chainText)) continue;
    console.log(`\nchain of ${n}:`);
    const seen = new Set();
    for (let x = n; x && !seen.has(x); x = parent.get(x)) {
      seen.add(x);
      console.log(`  ${x}${via.has(x) ? '  [on ' + via.get(x) + ']' : ''}`);
    }
  }
}
const typesText = opt('--types');
if (typesText !== undefined) {
  console.log('\nconstructed types:');
  for (const [t, by] of types) if (t.includes(typesText)) console.log(`  ${t}  <- ${by}`);
}
