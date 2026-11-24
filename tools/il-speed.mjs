// Speed of the IL front end's modules against NativeAOT-LLVM's WebAssembly build in the same engine (Node, V8): tools/bench
// (bench.mjs's kernels) and tools/frames (frames.mjs's scenes), each compiled by the IL front end in several
// configurations (by environment: naive counting, the ownership plan). bench.mjs and frames.mjs build with the old
// front end; this builds the same sources with the IL front end (one .cs each: the sources plus a Main, through the IL
// tests' `framework` mode) and times them the way those tools do. Building needs the Emscripten SDK (the desk);
// running needs only Node, so a build directory can be copied to another machine (the homelab) and run there.
//   node tools/il-speed.mjs build <dir> [--fw <framework>] [--tests <Il2Wasm.IL.Tests.dll>] [--config name=K=V,K=V ...] [--rebuild-aot]
//   node tools/il-speed.mjs run <dir> [--runs N] [--js-young MB] [--json file] [--only bench|frames]
// Default configs: naive (IL2WASM_MEM_NAIVE=1) and plan (IL2WASM_MEM_PLAN=1). Runs are interleaved (NativeAOT, then
// each config, per run); each statistic is the best (bench: the best of five in-process repetitions, then the best
// over the runs; frames: the lowest over the runs, as frames.mjs does).
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync, copyFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const cmd = args[0], dir = resolve(args[1] ?? '');
const scenes = ['churn', 'collections', 'strings', 'tree', 'bigdrop'];

function emsdkEnv() {
  const emsdk = process.env.EMSDK ?? (existsSync('C:/tools/emsdk') ? 'C:/tools/emsdk' : null);
  if (!emsdk) throw new Error('NativeAOT for WebAssembly links with Emscripten 3.1.56: set EMSDK to its directory');
  const sub = (d) => { const p = join(emsdk, d); return existsSync(p) ? join(p, readdirSync(p)[0]) : null; };
  const env = { ...process.env, EMSDK: emsdk, PATH: [emsdk, join(emsdk, 'upstream', 'emscripten'), process.env.PATH].join(process.platform === 'win32' ? ';' : ':') };
  const py = sub('python'), nd = sub('node');
  if (py) env.EMSDK_PYTHON = join(py, process.platform === 'win32' ? 'python.exe' : 'bin/python3');
  if (nd) env.EMSDK_NODE = join(nd, process.platform === 'win32' ? 'node.exe' : 'bin/node');
  return env;
}

const codeSize = (file) => {
  const b = readFileSync(file); let i = 8, n = 8;
  while (i < b.length) { const id = b[i++]; let size = 0, s = 0, x; do { x = b[i++]; size += (x & 0x7f) * 2 ** s; s += 7; } while (x & 0x80); if (id !== 0) n += size + 2; i += size; }
  return n;
};

if (cmd === 'build') {
  mkdirSync(dir, { recursive: true });
  for (const [proj, sub] of [['bench-naot-wasm', 'naot-bench'], ['frames-naot-wasm', 'naot-frames']]) {
    if (existsSync(join(dir, sub)) && !args.includes('--rebuild-aot')) continue;
    const env = emsdkEnv();
    console.error(`publishing ${proj} (NativeAOT-LLVM, WebAssembly)...`);
    execFileSync('dotnet', ['publish', join(root, 'tools', proj, `${proj}.csproj`), '-c', 'Release', '-o', join(dir, sub), '-v', 'q', '--nologo'], { stdio: ['ignore', 'ignore', 'inherit'], env });
  }
  copyFileSync(join(root, 'tools', 'bench-naot-wasm', 'peak.cjs'), join(dir, 'peak.cjs'));
  writeFileSync(join(dir, 'bench.cs'), readFileSync(join(root, 'tools', 'bench', 'Programs.cs'), 'utf8') + '\nstatic class IlMain { static void Main() => Bench.RunAll(); }\n');
  // (one module per scene: the IL front end's main passes Main(string[]) no arguments yet, IlCompiler.Finish)
  for (const sc of scenes) writeFileSync(join(dir, `frames_${sc}.cs`), readFileSync(join(root, 'tools', 'frames', 'Frames.cs'), 'utf8') + `\nstatic class IlMain { static void Main() => Frames.Run("${sc}"); }\n`);
  const fw = opt('--fw', process.env.IL2WASM_FRAMEWORK ?? join(root, 'artifacts', 'framework'));
  const tests = opt('--tests', join(root, 'tests', 'Il2Wasm.IL.Tests', 'bin', 'Release', 'net11.0', 'Il2Wasm.IL.Tests.dll'));
  const configs = args.flatMap((a, i) => (a === '--config' ? [args[i + 1]] : []));
  const list = configs.length ? configs : ['naive=IL2WASM_MEM_NAIVE=1', 'plan=IL2WASM_MEM_PLAN=1'];
  const meta = { configs: {} };
  for (const c of list) {
    const [name, ...kv] = c.split('=');
    const cenv = { ...process.env };
    for (const k of ['IL2WASM_MEM_NAIVE', 'IL2WASM_MEM_PLAN']) delete cenv[k];
    for (const p of kv.join('=').split(',')) { const [k, v] = p.split('='); if (k) cenv[k] = v; }
    meta.configs[name] = { env: kv.join('='), build: {} };
    for (const prog of ['bench', ...scenes.map((sc) => 'frames_' + sc)]) {
      const t0 = Date.now();
      execFileSync('dotnet', [tests, 'framework', fw, join(dir, prog + '.cs'), join(dir, `${prog}-${name}.wasm`)], { stdio: ['ignore', 'ignore', 'inherit'], env: cenv });
      meta.configs[name].build[prog] = { ms: Date.now() - t0, size: statSync(join(dir, `${prog}-${name}.wasm`)).size, code: codeSize(join(dir, `${prog}-${name}.wasm`)) };
      console.error(`${prog}-${name}.wasm: ${meta.configs[name].build[prog].size} B in ${meta.configs[name].build[prog].ms} ms`);
    }
  }
  meta.naot = { bench: codeSize(join(dir, 'naot-bench', 'bench-naot.wasm')), frames: codeSize(join(dir, 'naot-frames', 'frames-naot.wasm')) };
  meta.scenes = scenes;
  writeFileSync(join(dir, 'meta.json'), JSON.stringify(meta, null, 1));
} else if (cmd === 'run') {
  const meta = JSON.parse(readFileSync(join(dir, 'meta.json'), 'utf8'));
  const names = Object.keys(meta.configs);
  const runs = +opt('--runs', 3), young = opt('--js-young', null), only = opt('--only', null);
  const nodeFlags = young ? [`--min-semi-space-size=${+young}`, `--max-semi-space-size=${+young}`] : [];
  const peakJs = join(dir, 'peak.cjs'), host = join(root, 'host', 'run.mjs');
  const out = { bench: {}, frames: {} };
  const fail = (what, r) => { console.error(r.stdout?.slice(-2000), r.stderr?.slice(-2000)); throw new Error(what); };

  // bench: "bench <name> best_us=.. first_us=.. check=.."
  const parse = (text) => { const r = {}; for (const line of text.split(/\r?\n/)) { const m = /^bench (\S+) best_us=(\d+) first_us=(\d+) check=(-?\d+)/.exec(line); if (m) r[m[1]] = { best: +m[2], first: +m[3], check: m[4] }; } return r; };
  const best = (into, r) => { for (const [k, v] of Object.entries(r)) { if (!into[k]) into[k] = { ...v }; else { into[k].best = Math.min(into[k].best, v.best); into[k].first = Math.min(into[k].first, v.first); } } };
  if (only !== 'frames') {
    const sides = { naot: {}, ...Object.fromEntries(names.map((n) => [n, {}])) }, peak = { naot: 0, ...Object.fromEntries(names.map((n) => [n, 0])) };
    for (let i = 0; i < runs; i++) {
      const a = spawnSync(process.execPath, [...nodeFlags, '-r', peakJs, join(dir, 'naot-bench', 'bench-naot.js')], { encoding: 'utf8', cwd: join(dir, 'naot-bench') });
      if (a.status !== 0) fail('the NativeAOT WebAssembly bench failed', a);
      best(sides.naot, parse(a.stdout)); peak.naot = Math.max(peak.naot, +(/peak_bytes=(\d+)/.exec(a.stdout)?.[1] ?? 0));
      for (const n of names) {
        const w = spawnSync(process.execPath, [...nodeFlags, host, join(dir, `bench-${n}.wasm`)], { encoding: 'utf8', env: { ...process.env, IL2WASM_PRINT_MEMORY: '1' } });
        if (w.status !== 0) fail(`bench-${n} failed`, w);
        best(sides[n], parse(w.stdout)); peak[n] = Math.max(peak[n], +(/memory: (\d+) pages/.exec(w.stderr)?.[1] ?? 0) * 65536);
      }
    }
    out.bench = { sides, peak };
    const ms = (us) => (us / 1000).toFixed(1).padStart(9);
    console.log(`benchmark      NativeAOT wasm ms ${names.map((n) => `${n.padStart(9)} ms   ratio`).join(' ')}`);
    const logs = Object.fromEntries(names.map((n) => [n, []]));
    for (const k of Object.keys(sides.naot)) {
      const a = sides.naot[k];
      let line = `${k.padEnd(14)} ${ms(a.best)}        `;
      for (const n of names) {
        const w = sides[n][k];
        if (!w) { line += '   (none)        '; continue; }
        const r = w.best / a.best; logs[n].push(Math.log(r));
        line += ` ${ms(w.best)} ${r.toFixed(2).padStart(6)}x${w.check === a.check ? '' : ' CHECKSUM DIFFERS'}`;
      }
      console.log(line);
    }
    console.log(`geometric mean: ${names.map((n) => `${n} ${Math.exp(logs[n].reduce((x, y) => x + y, 0) / logs[n].length).toFixed(2)}x`).join(', ')} NativeAOT's (below 1 is faster)`);
    console.log(`peak memory MB: NativeAOT ${(peak.naot / 1048576).toFixed(1)}, ${names.map((n) => `${n} ${(peak[n] / 1048576).toFixed(1)}`).join(', ')}`);
    console.log(`code size KB (no custom sections): NativeAOT ${(meta.naot.bench / 1024).toFixed(1)}, ${names.map((n) => `${n} ${(meta.configs[n].build.bench.code / 1024).toFixed(1)}`).join(', ')}`);
  }
  if (only !== 'bench') {
    const stats = (text, scene) => {
      const m = new RegExp(`^frames ${scene} (.*)$`, 'm').exec(text);
      if (!m) return null;
      const raw = m[1].split(',').map(Number), t = [...raw].sort((a, b) => a - b);
      const q = (p) => t[Math.min(t.length - 1, Math.floor(p * t.length))];
      return { mean: raw.reduce((x, y) => x + y, 0) / raw.length, median: q(0.5), p99: q(0.99), p999: q(0.999), worst: t[t.length - 1], check: new RegExp(`^check ${scene} (-?\\d+)`, 'm').exec(text)?.[1] };
    };
    const low = (a, b) => { if (!a) return { ...b }; for (const k of ['mean', 'median', 'p99', 'p999', 'worst', 'peak']) a[k] = Math.min(a[k], b[k]); return a; };
    for (const scene of scenes) {
      const res = { naot: null, ...Object.fromEntries(names.map((n) => [n, null])) };
      for (let i = 0; i < runs; i++) {
        const a = spawnSync(process.execPath, [...nodeFlags, '-r', peakJs, join(dir, 'naot-frames', 'frames-naot.js'), scene], { encoding: 'utf8', cwd: join(dir, 'naot-frames'), maxBuffer: 1 << 26 });
        const sa = a.status === 0 && stats(a.stdout, scene);
        if (!sa) fail(`NativeAOT's frames failed on ${scene}`, a);
        sa.peak = +(/peak_bytes=(\d+)/.exec(a.stdout)?.[1] ?? 0);
        res.naot = low(res.naot, sa);
        for (const n of names) {
          const w = spawnSync(process.execPath, [...nodeFlags, host, join(dir, `frames_${scene}-${n}.wasm`)], { encoding: 'utf8', env: { ...process.env, IL2WASM_PRINT_MEMORY: '1' }, maxBuffer: 1 << 26 });
          const sw = w.status === 0 && stats(w.stdout, scene);
          if (!sw) fail(`frames_${scene}-${n} failed on ${scene}`, w);
          sw.peak = +(/memory: (\d+) pages/.exec(w.stderr)?.[1] ?? 0) * 65536;
          res[n] = low(res[n], sw);
        }
      }
      out.frames[scene] = res;
    }
    const ms = (ns) => (ns / 1e6).toFixed(3).padStart(8);
    const mb = (b) => (b / 1048576).toFixed(1).padStart(6);
    console.log('\nscene        side        median ms   p99 ms  p99.9 ms  worst ms   mean ms   peak MB');
    for (const [scene, res] of Object.entries(out.frames)) {
      const a = res.naot;
      console.log(`${scene.padEnd(12)} ${'NativeAOT'.padEnd(10)} ${ms(a.median)} ${ms(a.p99)} ${ms(a.p999)} ${ms(a.worst)} ${ms(a.mean)}  ${mb(a.peak)}`);
      for (const n of names) {
        const s = res[n];
        console.log(`${''.padEnd(12)} ${n.padEnd(10)} ${ms(s.median)} ${ms(s.p99)} ${ms(s.p999)} ${ms(s.worst)} ${ms(s.mean)}  ${mb(s.peak)}${s.check === a.check ? '' : `   CHECKSUM DIFFERS: ${a.check} vs ${s.check}`}`);
        const r = (k) => (s[k] / a[k]).toFixed(2).padStart(7) + 'x';
        console.log(`${''.padEnd(12)} ${('  ratio').padEnd(10)} ${r('median')} ${r('p99')} ${r('p999')} ${r('worst')} ${r('mean')} ${(s.peak / a.peak).toFixed(2).padStart(6)}x${s.worst > a.worst || s.p999 > a.p999 ? '   SPIKES ABOVE NATIVEAOT' : ''}`);
      }
    }
    for (const n of names) {
      const g = (k) => Math.pow(Object.values(out.frames).reduce((x, r) => x * r[n][k] / r.naot[k], 1), 1 / Object.keys(out.frames).length).toFixed(2);
      console.log(`${n}: geometric mean over the scenes, mean frame ${g('mean')}x NativeAOT's, median ${g('median')}x, worst ${g('worst')}x`);
    }
  }
  if (opt('--json')) writeFileSync(opt('--json'), JSON.stringify(out, null, 1));
} else {
  console.error(readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').filter((l) => l.startsWith('//')).map((l) => l.slice(3)).join('\n'));
  process.exit(2);
}
