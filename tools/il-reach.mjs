// What a module built by the IL front end is made of, and why each part is reached.
//
// usage: node tools/il-reach.mjs <reach file> <sizes file> [--top n] [--chain text] [--ns] [--types text]
//   (IL2WASM_IL_REACH=<reach file> IL2WASM_SIZES_FILE=<sizes file> when compiling)
//   [--tree minBytes] (the first-reach tree from the roots, every subtree of at least minBytes, largest first)
//   [--literals] (the literals and other data blobs by what asked for them: method code, or the compiler's generator)
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
const parent = new Map(), via = new Map(), types = [], data = [], literals = [], blobs = [];
for (const l of fs.readFileSync(reachFile, 'utf8').split(/\r?\n/)) {
  if (!l) continue;
  const p = l.split('\t');
  if (p[0] === 'M') {
    if (p[2] === 'D') { parent.set(p[1], p[3]); via.set(p[1], p[4]); }
    // (G: reached by the compiler's generated code, no method's: the generator's own methods stand as its parent)
    else if (p[2] === 'G') { parent.set(p[1], p[4]); if (!parent.has(p[4])) parent.set(p[4], ''); }
    else parent.set(p[1], p[3]);
  } else if (p[0] === 'T') types.push([p[1], p[2]]);
  else if (p[0] === 'D') data.push([+p[1], p[2]]);
  else if (p[0] === 'L') literals.push([+p[1], p[2], p[3]]);
  else if (p[0] === 'B') blobs.push([+p[1], p[2]]);
}
const size = n => sizes.get(n) ?? 0;
let total = 0;
for (const v of sizes.values()) total += v;
console.log(`code ${total} bytes in ${sizes.size} functions; ${parent.size} methods and stubs reached`);
console.log(`data ${data.reduce((s, d) => s + d[0], 0)} bytes; largest:`);
for (const [n, what] of data.sort((a, b) => b[0] - a[0]).slice(0, 12)) console.log(`  ${String(n).padStart(8)}  ${what}`);

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

const treeMin = opt('--tree');
if (treeMin !== undefined) {
  console.log(`\nfirst-reach tree (subtrees of ${treeMin} bytes or more; inclusive (own)):`);
  const walk = (n, d) => {
    if ((incl.get(n) ?? 0) < +treeMin) return;
    console.log(`${'  '.repeat(d + 1)}${incl.get(n)} (${size(n)}) ${n.length > 150 ? n.slice(0, 150) : n}${via.has(n) ? '  [on ' + via.get(n) + ']' : ''}`);
    for (const c of (children.get(n) ?? []).slice().sort((a, b) => incl.get(b) - incl.get(a))) walk(c, d + 1);
  };
  for (const r of (children.get('') ?? []).slice().sort((a, b) => incl.get(b) - incl.get(a))) walk(r, 0);
}
if (args.includes('--literals')) {
  const by = new Map();
  for (const [n, who] of literals) { const k = who.startsWith('gen ') ? who : 'method code'; const e = by.get(k) ?? [0, 0]; e[0] += n; e[1]++; by.set(k, e); }
  for (const [n, who] of blobs) { const k = 'blob ' + who; const e = by.get(k) ?? [0, 0]; e[0] += n; e[1]++; by.set(k, e); }
  console.log('\nliterals and blobs by what asked for them (bytes, count):');
  for (const [k, [n, c]] of [...by].sort((a, b) => b[1][0] - a[1][0])) console.log(`  ${String(n).padStart(8)} ${String(c).padStart(5)}  ${k}`);
}
