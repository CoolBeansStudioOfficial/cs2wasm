// Executed count operations per site for the IL front end (docs/MEMORY-IR.md 9.1, Lobster's --rcstats), the removal
// share against naive counting, and live objects per type at exit (9.2).
//
// A module built with IL2WASM_COUNT_RC=1 (Lower/RcStats.cs) has a counter at every call of rt.retain and rt.release,
// and at every call of a struct helper (rt.retain_struct, rt.release_struct, whose own counts are charged to the sites
// that call them). IL2WASM_COUNT_RC_SITES=sites.tsv at compile time writes the site table; IL2WASM_RCSTATS_OUT=run.json
// when host/run.mjs runs it writes the counts. With IL2WASM_IL_COUNT_OBJECTS=1 as well, live objects per type.
//
//   node tools/il-rcstats.mjs report sites.tsv run.json [--top N] [--by role|category|owner|function|cause]
//   node tools/il-rcstats.mjs share planSites.tsv plan.json naiveSites.tsv naive.json
//   node tools/il-rcstats.mjs live sites.tsv run.json [--oracle oracle.json --asm <program assembly>] [--top N]
//   node tools/il-rcstats.mjs run <program.cs | program.dll> [--naive-only] [--top N] [--keep dir] [--json out.json]
//        [--live [--oracle oracle.json]] [--expected out.txt] [--fw dir] [--cli il2wasm-il.dll] [--tests Il2Wasm.IL.Tests.dll] [-- program arguments]
//     compiles the program twice (as configured, and with IL2WASM_MEM_NAIVE=1), runs both, and reports each and the
//     removal share. A .cs is compiled against the framework by the IL tests' `framework` mode, a .dll (a dotnet build
//     output) by the CLI. The switches of the environment (IL2WASM_MEM_PLAN=1, IL2WASM_NO_*) go to the first build.
//     Nothing .NET runs: only the modules, under host/run.mjs. --live: live objects per type at exit too;
//     --expected: the output each build must print (a NetWasm suite case's expected/<case>.txt).
//
// The oracle for `live` is {"types": {"<type name>": count, ...}}: tools/il-live-oracle's heap walk of the same program
// under .NET after it exits, made on the homelab (never .NET cases on the desk): bash tools/homelab.sh <branch> bash
// tools/il-live-oracle/run.sh <case id>... prints one per case. Names are compared without assembly prefixes or outer
// types; the program's own types must be equal, the runtime's and library's internal objects differ by design.
//
// Owners: program, containers (System.Collections*), library (the rest of CoreLib and the stock libraries), runtime
// (Il2Wasm.Runtime's C#), generated (rt.destroy and the other generated functions), helper (struct helpers' own
// counts, charged to their callers). Roles: what the count is for (parameter, local, return, field, element, static,
// indirect, phi, unwind, argument, cast, exception, new, destroy, store-retain, store-old, ...): the lowering's tag
// where FunctionLowering gave one (category = its RcCategory), else read off the emitting compiler method and the
// instructions around the call (category "untagged"). Popular: the object's count was 8 or more before the operation.
import { readFileSync, writeFileSync, mkdirSync, existsSync, mkdtempSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join, resolve, dirname, basename, extname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

export function readSites(file) {
  const sites = [], types = new Map();
  for (const line of readFileSync(file, 'utf8').split(/\r?\n/)) {
    if (!line) continue;
    const f = line.split('\t');
    if (f[0] === '#type') { types.set(+f[1], f[2]); continue; }
    if (f[0].startsWith('#')) continue;
    sites[+f[0]] = { id: +f[0], kind: f[1], owner: f[2], role: f[3], category: f[4], fn: f[5], cause: f[6], context: f[7] };
  }
  return { sites, types };
}

/// The count operations a run executed, per site, with the struct helpers' own operations charged to their callers.
export function analyze(sitesFile, runFile) {
  const { sites, types } = readSites(sitesFile);
  const run = JSON.parse(readFileSync(runFile, 'utf8'));
  const exec = new Map(); // id -> [executed, changed, popular]
  for (const [id, n, ch, pop] of run.counts) exec.set(id, [n, ch, pop]);
  // a helper's operations per call (its own, and those of the helpers it calls)
  const helperOps = new Map(), helperCalls = new Map(), helperNested = new Map();
  for (const [id, v] of exec) {
    const s = sites[id];
    if (!s) continue;
    if (s.kind.startsWith('call ')) {
      const h = s.kind.slice(5);
      helperCalls.set(h, (helperCalls.get(h) ?? 0) + v[0]);
      if (s.owner === 'helper') { const m = helperNested.get(s.fn) ?? new Map(); m.set(h, (m.get(h) ?? 0) + v[0]); helperNested.set(s.fn, m); }
    } else if (s.owner === 'helper') {
      const o = helperOps.get(s.fn) ?? [0, 0, 0, 0, 0];
      o[s.kind === 'retain' ? 0 : 1] += v[0]; o[2] += v[1]; o[3] += v[2];
      helperOps.set(s.fn, o);
    }
  }
  const memo = new Map();
  const perCall = (h, d = 0) => {
    if (memo.has(h)) return memo.get(h);
    const calls = helperCalls.get(h) ?? 0;
    if (!calls || d > 30) return [0, 0, 0, 0];
    const o = helperOps.get(h) ?? [0, 0, 0, 0];
    const r = [o[0], o[1], o[2], o[3]];
    for (const [g, c] of helperNested.get(h) ?? []) { const p = perCall(g, d + 1); for (let i = 0; i < 4; i++) r[i] += c * p[i]; }
    const v = r.map((x) => x / calls);
    memo.set(h, v);
    return v;
  };
  // rows: one per counted site (not helpers' own sites), each with retains, releases, changed, popular
  const rows = [];
  for (const [id, v] of exec) {
    const s = sites[id];
    if (!s || s.owner === 'helper') continue;
    if (s.kind.startsWith('call ')) {
      const p = perCall(s.kind.slice(5));
      rows.push({ ...s, retains: v[0] * p[0], releases: v[0] * p[1], changed: v[0] * p[2], popular: v[0] * p[3], via: s.kind.slice(5), calls: v[0] });
    } else rows.push({ ...s, retains: s.kind === 'retain' ? v[0] : 0, releases: s.kind === 'release' ? v[0] : 0, changed: v[1], popular: v[2] });
  }
  for (const r of rows) r.ops = r.retains + r.releases;
  return { run, sites, types, rows };
}

const sum = (rows, f = (r) => r.ops) => rows.reduce((a, r) => a + f(r), 0);
const fmt = (n) => Math.round(n).toLocaleString('en-US');
const pct = (a, b) => (b > 0 ? (100 * a / b).toFixed(1) + '%' : '-');
const isRuntime = (r) => r.owner === 'runtime' || r.owner === 'generated' && r.role !== 'destroy';

export function totals(a) {
  const t = { ops: sum(a.rows), retains: sum(a.rows, (r) => r.retains), releases: sum(a.rows, (r) => r.releases), changed: sum(a.rows, (r) => r.changed), popular: sum(a.rows, (r) => r.popular) };
  t.withoutRuntime = sum(a.rows.filter((r) => !isRuntime(r)));
  t.byOwner = group(a.rows, (r) => r.owner);
  t.byRole = group(a.rows, (r) => r.role);
  t.byCategory = group(a.rows, (r) => r.category);
  return t;
}

function group(rows, key) {
  const m = new Map();
  for (const r of rows) {
    const k = key(r), g = m.get(k) ?? { ops: 0, retains: 0, releases: 0, popular: 0, sites: 0 };
    g.ops += r.ops; g.retains += r.retains; g.releases += r.releases; g.popular += r.popular; g.sites++;
    m.set(k, g);
  }
  return Object.fromEntries([...m].sort((x, y) => y[1].ops - x[1].ops));
}

function table(title, obj, total) {
  const lines = [`${title}:`];
  for (const [k, g] of Object.entries(obj)) lines.push(`  ${k.padEnd(16)} ${fmt(g.ops).padStart(14)} ${pct(g.ops, total).padStart(7)}   ${fmt(g.retains).padStart(12)} retains ${fmt(g.releases).padStart(12)} releases ${fmt(g.popular).padStart(10)} popular  (${g.sites} sites)`);
  return lines.join('\n');
}

export function report(a, { top = 20, by = 'site' } = {}) {
  const t = totals(a), out = [];
  const b = a.run.buckets;
  out.push(`executed count operations: ${fmt(t.ops)} (${fmt(t.retains)} retains, ${fmt(t.releases)} releases); ${fmt(t.changed)} changed a count (${pct(t.changed, t.ops)}), ${fmt(t.popular)} on popular objects (${pct(t.popular, t.ops)}); ${fmt(t.withoutRuntime)} outside the runtime`);
  if (b) out.push(`object count before the operation [null, immortal, 1, 2, 3-7, 8-63, 64+]: retains ${b.retain.join(' ')}; releases ${b.release.join(' ')}`);
  if (a.run.entered) {
    const r = b.retain.reduce((x, y) => x + y, 0), l = b.release.reduce((x, y) => x + y, 0);
    if (r !== a.run.entered.retain || l !== a.run.entered.release) out.push(`WARNING: rt.retain was entered ${a.run.entered.retain} times and rt.release ${a.run.entered.release}, the sites counted ${r} and ${l}: count calls no site saw (a copy written into a caller)`);
  }
  out.push(table('by owner', t.byOwner, t.ops));
  out.push(table('by role', t.byRole, t.ops));
  out.push(table('by category', t.byCategory, t.ops));
  if (by !== 'site') out.push(table(`by ${by}`, group(a.rows, (r) => r[by === 'function' ? 'fn' : by] ?? ''), t.ops));
  const tops = [...a.rows].sort((x, y) => y.ops - x.ops).slice(0, top);
  out.push(`top ${tops.length} sites:`);
  for (const r of tops) out.push(`  ${fmt(r.ops).padStart(12)} ${pct(r.ops, t.ops).padStart(6)}  #${r.id} ${r.kind} ${r.owner}/${r.role}/${r.category} pop ${pct(r.popular, r.ops)}  ${r.fn}${r.cause ? '  {' + r.cause + '}' : ''}  [${r.context}]`);
  return out.join('\n');
}

export function share(plan, naive) {
  const p = totals(plan), n = totals(naive);
  const s = (a, b) => (b > 0 ? 1 - a / b : 0);
  return {
    plan: p.ops, naive: n.ops, share: s(p.ops, n.ops),
    planWithoutRuntime: p.withoutRuntime, naiveWithoutRuntime: n.withoutRuntime, shareWithoutRuntime: s(p.withoutRuntime, n.withoutRuntime),
    byOwner: Object.fromEntries(Object.keys({ ...n.byOwner, ...p.byOwner }).map((k) => [k, { plan: p.byOwner[k]?.ops ?? 0, naive: n.byOwner[k]?.ops ?? 0, share: s(p.byOwner[k]?.ops ?? 0, n.byOwner[k]?.ops ?? 0) }])),
  };
}

function shareText(x) {
  const lines = [`removal share: ${(100 * x.share).toFixed(1)}% (${fmt(x.plan)} of ${fmt(x.naive)} naive count operations run); without the runtime ${(100 * x.shareWithoutRuntime).toFixed(1)}% (${fmt(x.planWithoutRuntime)} of ${fmt(x.naiveWithoutRuntime)})`];
  for (const [k, v] of Object.entries(x.byOwner)) lines.push(`  ${k.padEnd(12)} ${fmt(v.plan).padStart(14)} of ${fmt(v.naive).padStart(14)}  ${(100 * v.share).toFixed(1)}% removed`);
  return lines.join('\n');
}

// (il2wasm's type names and the runtime's heap-walk names, made comparable: no assembly prefixes, il2wasm's primitive
// names as .NET's, a generic's arguments in brackets, a nested type by its own name: the walk gives no outer type)
const PRIM = { bool: 'System.Boolean', char: 'System.Char', int8: 'System.SByte', uint8: 'System.Byte', int16: 'System.Int16', uint16: 'System.UInt16', int32: 'System.Int32', uint32: 'System.UInt32', int64: 'System.Int64', uint64: 'System.UInt64', float32: 'System.Single', float64: 'System.Double', string: 'System.String', object: 'System.Object', 'native int': 'System.IntPtr', 'native uint': 'System.UIntPtr', void: 'System.Void' };
export function typeKey(name) {
  // (a generic's arguments, <...> after a name, in brackets; a compiler-generated name's own <>, as in <>c, kept)
  let n = '', open = [];
  const src = name.replace(/\[[A-Za-z0-9_.\-]+\](?=[A-Za-z_<])/g, '');
  for (let i = 0; i < src.length; i++) {
    const ch = src[i];
    if (ch === '<') { const gen = i > 0 && /[A-Za-z0-9_`\]]/.test(src[i - 1]); open.push(gen); n += gen ? '[' : '<'; }
    else if (ch === '>') n += open.pop() ? ']' : '>';
    else n += ch;
  }
  n = n.replace(/native u?int|[A-Za-z_][A-Za-z0-9_]*/g, (w, at) => (PRIM[w] && !/[.+`]$/.test(n.slice(0, at)) && !/^[.`]/.test(n.slice(at + w.length)) ? PRIM[w] : w));
  return n.replace(/[A-Za-z0-9_.`<>]*\+/g, '');
}

/// Live objects per type at exit (IL2WASM_IL_COUNT_OBJECTS=1); with an oracle (tools/il-live-oracle's JSON), the
/// comparison: equal per type on the program's own types (those of assembly asm), the other differences listed (the
/// runtime's and the library's internal objects differ by design: reflection caches, event sources, the console).
export function live(a, oracle, asm) {
  const types = a.types, rows = [];
  for (const [id, n] of a.run.live ?? []) rows.push({ type: types.get(id) ?? `type #${id}`, id, n });
  const out = { total: rows.reduce((x, r) => x + r.n, 0), types: Object.fromEntries(rows.sort((x, y) => y.n - x.n).map((r) => [r.type, r.n])) };
  if (oracle) {
    const mine = new Map(), theirs = new Map(), program = new Set();
    for (const [k, n] of Object.entries(out.types)) mine.set(typeKey(k), (mine.get(typeKey(k)) ?? 0) + n);
    for (const [k, n] of Object.entries(oracle.types ?? oracle)) theirs.set(typeKey(k), (theirs.get(typeKey(k)) ?? 0) + n);
    // (the program's types, but not the compiler's closure caches, <>c: the walk names every one of them, the library's
    // too, by that short name)
    if (asm) for (const name of types.values()) if (name.startsWith(`[${asm}]`) && !typeKey(name).startsWith('<')) program.add(typeKey(name));
    const diff = [];
    for (const k of new Set([...mine.keys(), ...theirs.keys()])) {
      const m = mine.get(k) ?? 0, o = theirs.get(k) ?? 0;
      if (m !== o) diff.push({ type: k, il2wasm: m, dotnet: o, program: program.has(k) });
    }
    out.diff = diff.sort((x, y) => (y.program - x.program) || Math.abs(y.il2wasm - y.dotnet) - Math.abs(x.il2wasm - x.dotnet));
    out.programTypes = program.size;
    out.programDiff = diff.filter((d) => d.program).length;
    out.equal = out.programDiff === 0;
    out.dotnetTotal = [...theirs.values()].reduce((x, y) => x + y, 0);
  }
  return out;
}

function liveText(l, top) {
  const lines = [`live objects at exit: ${fmt(l.total)} in ${Object.keys(l.types).length} types`];
  for (const [k, n] of Object.entries(l.types).slice(0, top)) lines.push(`  ${String(n).padStart(8)}  ${k}`);
  if (l.diff) {
    lines.push(`against .NET (${fmt(l.dotnetTotal)} live there): ${l.equal ? `equal on the program's ${l.programTypes} types` : `${l.programDiff} of the program's ${l.programTypes} types differ`}; ${l.diff.length - l.programDiff} other types differ (il2wasm, .NET):`);
    for (const d of l.diff.slice(0, top)) lines.push(`  ${String(d.il2wasm).padStart(8)} ${String(d.dotnet).padStart(8)}  ${d.program ? '(program) ' : ''}${d.type}`);
  }
  return lines.join('\n');
}

// ------------------------------------------------------------------ the `run` driver

function compileAndRun(input, out, env, o) {
  const sitesFile = out + '.sites.tsv', runFile = out + '.run.json';
  const cenv = { ...process.env, ...env, IL2WASM_COUNT_RC: '1', IL2WASM_COUNT_RC_SITES: sitesFile };
  const t0 = Date.now();
  const c = extname(input).toLowerCase() === '.cs'
    ? spawnSync('dotnet', [o.tests, 'framework', o.fw, input, out], { env: cenv, encoding: 'utf8' })
    : spawnSync('dotnet', [o.cli, input, '-o', out, '--framework', o.fw], { env: cenv, encoding: 'utf8' });
  if (c.status !== 0) throw new Error(`compile failed (${input}):\n${c.stderr}${c.stdout}`);
  const compileMs = Date.now() - t0;
  const renv = { ...process.env, IL2WASM_RCSTATS_OUT: runFile };
  const r = spawnSync('node', [join(root, 'host', 'run.mjs'), out, ...(o.args.length ? ['--', ...o.args] : [])], { env: renv, encoding: 'utf8', cwd: dirname(input), maxBuffer: 1 << 28 });
  if (!existsSync(runFile)) throw new Error(`no counts from the run (${input}, exit ${r.status}):\n${r.stderr}`);
  return { sitesFile, runFile, compileMs, exit: r.status, stdout: r.stdout };
}

function runCmd(input, o) {
  input = resolve(input);
  const dir = o.keep ? resolve(o.keep) : mkdtempSync(join(tmpdir(), 'il-rcstats-'));
  mkdirSync(dir, { recursive: true });
  const name = basename(input, extname(input));
  try {
    const result = { program: input };
    const naive = compileAndRun(input, join(dir, name + '.naive.wasm'), { IL2WASM_MEM_NAIVE: '1' }, o);
    const na = analyze(naive.sitesFile, naive.runFile);
    result.naive = { ...totals(na), exit: naive.exit };
    if (o.live) result.naive.live = live(na, o.oracle ? JSON.parse(readFileSync(o.oracle, 'utf8')) : null, name);
    console.log(`== ${name}: naive counting (IL2WASM_MEM_NAIVE=1)\n` + report(na, o));
    if (o.live && na.run.live) console.log(liveText(result.naive.live, o.top));
    const norm = (t) => t.replace(/\r\n/g, '\n');
    const expected = o.expected ? norm(readFileSync(o.expected, 'utf8')) : null;
    // (as the suite observes a run: its output, then its exit code)
    const observed = (r) => norm(r.stdout) + (/(^|\n)exit -?\d+\n?$/.test(expected ?? '') ? `exit ${r.exit}\n` : '');
    if (expected != null) { result.naive.matches = observed(naive) === expected; console.log(result.naive.matches ? 'output: as expected' : 'WARNING: the output differs from the expected'); }
    if (!o.naiveOnly) {
      const plan = compileAndRun(input, join(dir, name + '.plan.wasm'), {}, o);
      const pa = analyze(plan.sitesFile, plan.runFile);
      result.plan = { ...totals(pa), exit: plan.exit };
      console.log(`== ${name}: as configured (${Object.entries(process.env).filter(([k]) => /^IL2WASM_(MEM_|NO_)/.test(k)).map(([k, v]) => k + '=' + v).join(' ') || 'the defaults'})\n` + report(pa, o));
      if (plan.stdout !== naive.stdout) console.log('WARNING: the two builds printed different output');
      if (expected != null) { result.plan.matches = observed(plan) === expected; if (!result.plan.matches) console.log("WARNING: the output of the build as configured differs from the expected"); }
      result.share = share(pa, na);
      console.log(shareText(result.share));
    }
    if (o.json) writeFileSync(o.json, JSON.stringify(result, null, 1));
    return result;
  } finally { if (!o.keep) rmSync(dir, { recursive: true, force: true }); }
}

// ------------------------------------------------------------------ command line

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const argv = process.argv.slice(2);
  const dd = argv.indexOf('--');
  const args = dd >= 0 ? argv.slice(0, dd) : argv, progArgs = dd >= 0 ? argv.slice(dd + 1) : [];
  const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
  const valued = ['--top', '--by', '--oracle', '--keep', '--json', '--fw', '--cli', '--tests', '--expected', '--asm'];
  const pos = args.filter((a, i) => !a.startsWith('--') && !(i > 0 && valued.includes(args[i - 1])));
  const top = +opt('--top', 20), by = opt('--by', 'site');
  const cmd = pos[0];
  if (cmd === 'report') {
    const a = analyze(pos[1], pos[2]);
    console.log(report(a, { top, by }));
    if (a.run.live) console.log(liveText(live(a), top));
  } else if (cmd === 'share') {
    console.log(shareText(share(analyze(pos[1], pos[2]), analyze(pos[3], pos[4]))));
  } else if (cmd === 'live') {
    const o = opt('--oracle') ? JSON.parse(readFileSync(opt('--oracle'), 'utf8')) : null;
    console.log(liveText(live(analyze(pos[1], pos[2]), o, opt('--asm')), top));
  } else if (cmd === 'run') {
    const o = {
      top, by, args: progArgs, naiveOnly: args.includes('--naive-only'), live: args.includes('--live'), keep: opt('--keep'), json: opt('--json'), expected: opt('--expected'), oracle: opt('--oracle'),
      fw: opt('--fw', process.env.IL2WASM_FRAMEWORK ?? join(root, 'artifacts', 'framework')),
      cli: opt('--cli', join(root, 'src', 'Il2Wasm.IL.Cli', 'bin', 'Release', 'net11.0', 'il2wasm-il.dll')),
      tests: opt('--tests', join(root, 'tests', 'Il2Wasm.IL.Tests', 'bin', 'Release', 'net11.0', 'Il2Wasm.IL.Tests.dll')),
    };
    if (o.live) process.env.IL2WASM_IL_COUNT_OBJECTS = '1';
    for (const p of pos.slice(1)) runCmd(p, o);
  } else {
    console.error(readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').filter((l) => l.startsWith('//')).map((l) => l.slice(3)).join('\n'));
    process.exit(2);
  }
}
