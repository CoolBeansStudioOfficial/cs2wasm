// Frame times of programs with threads (SPEC sections 1 and 4.5; tools/frames.mjs measures programs without): the
// scenes of tools/frames-threads compiled by il2wasm as a program with threads, against the same work in a program
// without threads compiled by il2wasm and by NativeAOT-LLVM for WebAssembly, each run in its own Node (V8) process.
//   idle     tools/frames's five scenes on the main thread while one worker waits on a monitor (what a program pays
//            for having a thread at all), against the same scene in the programs without threads
//   private  N threads (1, 2, 4, 8), each with a particle system (pchurn) or a scene tree (ptree) of its own made on
//            that thread, against one such world in the programs without threads; every worker's every frame is in
//            the statistics, and the wall time of all N says how the work scales
//   shared   64 work items a frame made on the main thread, handed to N workers through a queue under a lock, their
//            results handed back through another (objects made on one thread and freed on another), against the main
//            thread doing the items itself in the programs without threads
//   parts    the churn's and the tree's parts each timed by itself, on a worker and without threads
// NativeAOT-LLVM's WebAssembly with threads does not link with the packages on this machine (its runtime libraries
// are built without atomics: wasm-ld refuses --shared-memory), so that side is its program without threads; say
// --aot-threads to try the build again (tools/frames-threads-naot-wasm, -p:Threads=true).
// Per scene: median, p99, p99.9 and worst frame, the wall time of the measured part and peak memory, each the lowest
// over the runs.
//   node tools/frames-threads.mjs [--runs N] [--no-build] [--no-aot] [--rebuild-aot] [--aot-threads] [--js-young MB] [--json file] [group...]
// groups: idle private shared parts (default: all). A compiler switch in the environment reaches il2wasm's builds.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const valued = new Set(['--runs', '--json', '--js-young']);
const runs = +opt('--runs', 3);
const young = opt('--js-young', '32');
const nodeFlags = [`--min-semi-space-size=${+young}`, `--max-semi-space-size=${+young}`];
const picked = args.filter((a, i) => !a.startsWith('--') && !valued.has(args[i - 1]));
const groups = picked.length ? picked : ['idle', 'private', 'shared', 'parts'];
const noAot = args.includes('--no-aot');
const out = join(root, 'tests', 'out', 'frames-threads');
mkdirSync(out, { recursive: true });
const src = join(root, 'tools', 'frames-threads'), frames = join(root, 'tools', 'frames', 'Frames.cs');
const newest = Math.max(statSync(frames).mtimeMs, ...readdirSync(src).filter((f) => f.endsWith('.cs')).map((f) => statSync(join(src, f)).mtimeMs));

// NativeAOT's side
const aotDir = join(out, 'naot-single'), aotJs = join(aotDir, 'ft-naot.js');
const aotThreadsDir = join(out, 'naot-threads'), aotThreadsJs = join(aotThreadsDir, 'ft-naot.js');
function publish(dir, threads) {
  const emsdk = process.env.EMSDK ?? (existsSync('C:/tools/emsdk') ? 'C:/tools/emsdk' : null);
  if (!emsdk) throw new Error('NativeAOT for WebAssembly links with Emscripten 3.1.56: set EMSDK to its directory');
  console.error(`publishing the NativeAOT build for WebAssembly${threads ? ' with threads' : ''}...`);
  const sub = (d) => { const p = join(emsdk, d); return existsSync(p) ? join(p, readdirSync(p)[0]) : null; };
  const env = { ...process.env, EMSDK: emsdk, PATH: [emsdk, join(emsdk, 'upstream', 'emscripten'), process.env.PATH].join(process.platform === 'win32' ? ';' : ':') };
  const py = sub('python'), nd = sub('node');
  if (py) env.EMSDK_PYTHON = join(py, process.platform === 'win32' ? 'python.exe' : 'bin/python3');
  if (nd) env.EMSDK_NODE = join(nd, process.platform === 'win32' ? 'node.exe' : 'bin/node');
  const r = spawnSync('dotnet', ['publish', join(root, 'tools', 'frames-threads-naot-wasm', 'frames-threads-naot-wasm.csproj'), '-c', 'Release', ...(threads ? ['-p:Threads=true'] : []), '-o', dir, '-v', 'q', '--nologo'], { encoding: 'utf8', env, maxBuffer: 1 << 26 });
  return r.status === 0 ? null : (r.stdout + r.stderr);
}
if (!noAot && (args.includes('--rebuild-aot') || !existsSync(aotJs) || statSync(aotJs).mtimeMs < newest)) {
  const err = publish(aotDir, false);
  if (err) { console.error(err.slice(-3000)); throw new Error("NativeAOT's build failed"); }
}
let aotThreads = false;
if (!noAot && args.includes('--aot-threads')) {
  const err = publish(aotThreadsDir, true);
  if (err) console.error(`NativeAOT's WebAssembly with threads does not build here: ${(/error[^\n]*/.exec(err)?.[0] ?? err.slice(-300)).slice(0, 300)}`);
  else aotThreads = true;
}

// il2wasm's two programs
const cli = join(root, 'src', 'Il2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'il2wasm.exe' : 'il2wasm');
if (!args.includes('--no-build')) execFileSync('dotnet', ['build', join(root, 'src', 'Il2Wasm.Cli'), '-c', 'Release', '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
const single = join(out, 'single.wasm'), threads = join(out, 'threads.wasm');
execFileSync(cli, [frames, join(src, 'Work.cs'), join(src, 'WasmMainSingle.cs'), '-o', single], { stdio: ['ignore', 'ignore', 'ignore'] });
execFileSync(cli, [frames, join(src, 'Work.cs'), join(src, 'Threaded.cs'), join(src, 'WasmMainThreads.cs'), '-o', threads], { stdio: ['ignore', 'ignore', 'ignore'] });

const peakJs = join(root, 'tools', 'bench-naot-wasm', 'peak.cjs');
function stats(text, memory) {
  const m = /^frames (\S+) (.*)$/m.exec(text);
  if (!m) return null;
  const t = m[2].split(',').map(Number).sort((a, b) => a - b);
  const q = (p) => t[Math.min(t.length - 1, Math.floor(p * t.length))];
  return { median: q(0.5), p99: q(0.99), p999: q(0.999), worst: t[t.length - 1], wall: +(/^wall \S+ (-?\d+)/m.exec(text)?.[1] ?? NaN), peak: memory, check: /^check \S+ (-?\d+)/m.exec(text)?.[1] };
}
const low = (a, b) => { if (!a) return { ...b }; for (const k of ['median', 'p99', 'p999', 'worst', 'wall', 'peak']) if (!(a[k] <= b[k])) a[k] = b[k]; return a; };
function runWasm(module, scene) {
  const w = spawnSync(process.execPath, [...nodeFlags, join(root, 'host', 'run.mjs'), module, scene], { encoding: 'utf8', env: { ...process.env, IL2WASM_PRINT_MEMORY: '1' }, maxBuffer: 1 << 27 });
  if (w.status !== 0) { console.error(w.stdout.slice(-1000), w.stderr.slice(-3000)); throw new Error(`il2wasm's module failed on ${scene}`); }
  return { text: w.stdout, memory: +(/memory: (\d+) pages/.exec(w.stderr)?.[1] ?? 0) * 65536 };
}
function runAot(js, dir, scene) {
  const a = spawnSync(process.execPath, [...nodeFlags, '-r', peakJs, js, scene], { encoding: 'utf8', cwd: dir, maxBuffer: 1 << 27 });
  if (a.status !== 0) { console.error(a.stdout.slice(-1000), a.stderr.slice(-3000)); throw new Error(`NativeAOT's build failed on ${scene}`); }
  return { text: a.stdout, memory: +(/peak_bytes=(\d+)/.exec(a.stdout)?.[1] ?? 0) };
}
// a row: the scene with threads, the same work without (il2wasm's and NativeAOT's scene names)
const rows = [];
// (idle_<scene>: the scene called by name; aidle_<scene>: handed over as an Action, the delegate type the workers of
// other scenes invoke too)
if (groups.includes('idle')) for (const s of ['churn', 'collections', 'strings', 'tree', 'bigdrop']) { rows.push({ scene: 'idle_' + s, base: s, group: 'idle' }); rows.push({ scene: 'aidle_' + s, base: s, group: 'idle' }); }
if (groups.includes('private')) for (const k of ['pchurn', 'ptree']) for (const n of [1, 2, 4, 8]) rows.push({ scene: k + n, base: k + '0', group: 'private', n });
if (groups.includes('shared')) for (const n of [1, 2, 4]) rows.push({ scene: 'shared' + n, base: 'shared0', group: 'shared', n });
const res = {};
const baseDone = {};
// (N threads of arithmetic alone, spinN: what the machine itself gives N threads, beside the private scenes' scaling)
const spins = {};
if (groups.includes('private'))
  for (let i = 0; i < runs; i++)
    for (const n of [1, 2, 4, 8]) { const t = runWasm(threads, 'spin' + n); spins[n] = low(spins[n], stats(t.text, t.memory)); }
for (let i = 0; i < runs; i++)
  for (const r of rows) {
    const e = res[r.scene] ??= { row: r };
    const t = runWasm(threads, r.scene);
    e.threads = low(e.threads, stats(t.text, t.memory));
    if (aotThreads) { const a = runAot(aotThreadsJs, aotThreadsDir, r.scene); e.aotThreads = low(e.aotThreads, stats(a.text, a.memory)); }
    // (the programs without threads: once a round for each scene, whichever rows share it)
    if (baseDone[r.base + ':' + i]) continue;
    baseDone[r.base + ':' + i] = true;
    const b = res['=' + r.base] ??= {};
    const s = runWasm(single, r.base);
    b.single = low(b.single, stats(s.text, s.memory));
    if (!noAot) { const a = runAot(aotJs, aotDir, r.base); b.aot = low(b.aot, stats(a.text, a.memory)); }
  }

const ms = (ns) => (ns / 1e6).toFixed(3).padStart(8);
const mb = (b) => (b / 1048576).toFixed(1).padStart(6);
const line = (name, side, s, ref) => console.log(`${name.padEnd(17)} ${side.padEnd(22)} ${ms(s.median)} ${ms(s.p99)} ${ms(s.p999)} ${ms(s.worst)} ${Number.isFinite(s.wall) ? (s.wall / 1e6).toFixed(0).padStart(8) : '       -'}  ${mb(s.peak)}${ref ? `   ${(s.median / ref.median).toFixed(2)}x median, ${(s.worst / ref.worst).toFixed(2)}x worst` : ''}`);
let bad = 0;
if (rows.length) console.log('scene             side                   median ms   p99 ms p99.9 ms worst ms  wall ms  peak MB   against NativeAOT without threads');
let lastBase = null;
for (const r of rows) {
  const e = res[r.scene], b = res['=' + r.base];
  if (r.base !== lastBase) {
    if (b.aot) line(r.base, 'NativeAOT, no threads', b.aot, null);
    line(b.aot ? '' : r.base, 'il2wasm, no threads', b.single, b.aot);
    if (b.aot && b.aot.check !== b.single.check) { bad++; console.log(`  CHECKSUM DIFFERS: ${b.aot.check} vs ${b.single.check}`); }
    lastBase = r.base;
  }
  line(r.scene, 'il2wasm, threads', e.threads, b.aot ?? b.single);
  if (e.aotThreads) line('', 'NativeAOT, threads', e.aotThreads, b.aot);
  // (one world's checksum is the base's; N private worlds differ by their seeds: compared with NativeAOT's when it ran)
  if (r.group !== 'private' || r.n === 1) { if (e.threads.check !== b.single.check) { bad++; console.log(`  CHECKSUM DIFFERS from the program without threads: ${e.threads.check} vs ${b.single.check}`); } }
  if (e.aotThreads && e.aotThreads.check !== e.threads.check) { bad++; console.log(`  CHECKSUM DIFFERS: ${e.aotThreads.check} vs ${e.threads.check}`); }
  if (r.group === 'private' && r.n > 1) {
    const one = res[r.scene.replace(/\d+$/, '1')].threads;
    const spin = spins[r.n] && spins[1] ? `; arithmetic alone on this machine: ${(r.n * spins[1].wall / spins[r.n].wall).toFixed(2)}x` : '';
    console.log(`${''.padEnd(17)} ${`scaling, ${r.n} threads`.padEnd(22)} ${(r.n * one.wall / e.threads.wall).toFixed(2)}x the work of one thread in the same time (${r.n}.00x: each thread as fast as one alone${spin})`);
  }
}
// the churn's parts
if (groups.includes('parts')) {
  const part = (text) => { const m = /^parts \S+ make (\d+) pass (\d+) drop (\d+) check (-?\d+)/m.exec(text); return m ? { make: +m[1], pass: +m[2], drop: +m[3], check: m[4] } : null; };
  const lowP = (a, b) => { if (!a) return b; for (const k of ['make', 'pass', 'drop']) a[k] = Math.min(a[k], b[k]); return a; };
  let pt = null, ps = null, pa = null;
  for (let i = 0; i < runs; i++) {
    pt = lowP(pt, part(runWasm(threads, 'parts1').text));
    ps = lowP(ps, part(runWasm(single, 'parts0').text));
    if (!noAot) pa = lowP(pa, part(runAot(aotJs, aotDir, 'parts0').text));
  }
  res.parts = { threads: pt, single: ps, aot: pa };
  const us = (ns) => (ns / 1e3).toFixed(0).padStart(7);
  console.log('the churn in parts, us for 20,000 particles      make    pass    drop');
  if (pa) console.log(`  NativeAOT, no threads                       ${us(pa.make)} ${us(pa.pass)} ${us(pa.drop)}`);
  console.log(`  il2wasm, no threads                         ${us(ps.make)} ${us(ps.pass)} ${us(ps.drop)}`);
  console.log(`  il2wasm, on a worker                        ${us(pt.make)} ${us(pt.pass)} ${us(pt.drop)}`);
  if (pt.check !== ps.check || pa && pa.check !== ps.check) { bad++; console.log('  CHECKSUM DIFFERS'); }
  // the tree's parts
  const tpart = (text) => { const m = /^tparts \S+ build (\d+) walk (\d+) replace (\d+) drop (\d+) check (-?\d+)/m.exec(text); return m ? { build: +m[1], walk: +m[2], replace: +m[3], drop: +m[4], check: m[5] } : null; };
  const lowT = (a, b) => { if (!a) return b; for (const k of ['build', 'walk', 'replace', 'drop']) a[k] = Math.min(a[k], b[k]); return a; };
  let tt = null, ts = null, ta = null;
  for (let i = 0; i < runs; i++) {
    tt = lowT(tt, tpart(runWasm(threads, 'tparts1').text));
    ts = lowT(ts, tpart(runWasm(single, 'tparts0').text));
    if (!noAot) ta = lowT(ta, tpart(runAot(aotJs, aotDir, 'tparts0').text));
  }
  res.tparts = { threads: tt, single: ts, aot: ta };
  console.log('the tree in parts, us for 3,906 entities        build    walk replace    drop');
  if (ta) console.log(`  NativeAOT, no threads                       ${us(ta.build)} ${us(ta.walk)} ${us(ta.replace)} ${us(ta.drop)}`);
  console.log(`  il2wasm, no threads                         ${us(ts.build)} ${us(ts.walk)} ${us(ts.replace)} ${us(ts.drop)}`);
  console.log(`  il2wasm, on a worker                        ${us(tt.build)} ${us(tt.walk)} ${us(tt.replace)} ${us(tt.drop)}`);
  if (tt.check !== ts.check || ta && ta.check !== ts.check) { bad++; console.log('  CHECKSUM DIFFERS'); }
}
const json = opt('--json', null);
if (json) writeFileSync(json, JSON.stringify(res, null, 1));
process.exit(bad ? 1 : 0);
