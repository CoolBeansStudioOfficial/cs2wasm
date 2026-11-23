// NetWasm's test suite run against il2wasm, with .NET as the oracle (SPEC 13).
//
// NetWasm's main compiler suite (tests/NetWasm.Compiler.Tests) is under the NetWasm Community License, so none of it is
// ever copied into this repository: this harness fetches a pinned NetWasm commit into a cache outside the repository,
// extracts each differential case there into a standalone net11.0 console project (tools/netwasm-suite/Extract: the
// case's sources, unchanged, and a generated Main that calls its entry point over its inputs and prints every
// observation), runs each project under .NET to record the expected output (never NetWasm's stored expectations), then
// compiles the same project's IL with il2wasm and runs the module under host/run.mjs, comparing byte for byte, checked
// and as shipped, optionally under load. The open-source programs of eng/open-source-suts (MIT) are read from the same
// checkout.
//
//   node tools/netwasm-suite.mjs <command> [options]
// commands:
//   fetch      clone the pinned commit into the cache (git; nothing is written into this repository)
//   extract    turn the checkout's cases into projects in the work directory
//   setup      assemble the oracle's .NET runtime (win-x86 by default: wasm32 semantics, the owner's decision)
//   build      dotnet build every case (Release, the oracle's runtime identifier)
//   oracle     run each built case under .NET --runs times (default 3); record the expected output and flag the
//              cases whose output differs between runs (unstable)
//   run        compile each case with il2wasm (--compile) and run the module (--run); compare with the oracle
//   report     print the totals and failures from the last run; write report.json
//   all        fetch, extract, setup, build, oracle, run (if a compiler is given), report
//   homelab <branch> [options]   run `all` on the homelab through tools/homelab.sh (the heavy-run mode)
// options:
//   --cache <dir>       default %LOCALAPPDATA%/il2wasm/netwasm (or $IL2WASM_NETWASM_CACHE)
//   --commit <sha>      default the pinned commit below
//   --checkout <dir>    use an existing checkout of that commit instead of fetching
//   --filter <regex>    only the cases whose id matches
//   --par <n>           processes at once (builds, oracle runs, module runs); default half the cores
//   --runs <n>          oracle runs per case (default 3)
//   --rid <rid>         the oracle's runtime identifier (default win-x86; win-x64 to compare 64-bit .NET)
//   --timeout <s>       per process (default 120)
//   --compile "<cmd>"   il2wasm's compile step ($IL2WASM_COMPILE), with {dll} {project} {casedir} {out} {mode} {checked}
//                       ("dotnet-standin": no compiler, the .NET build stands in for the module, to prove the plumbing)
//   --run "<cmd>"       how a module runs (default "node host/run.mjs {out}")
//   --modes <list>      checked,shipped (default both)
//   --repeat <n>        run each module n times at once, every run must match (under load; default 1)
//   --force             redo oracle runs and compiles that already have results
import { spawn, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, readdirSync, cpSync, rmSync, statSync } from 'node:fs';
import { cpus, homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const PINNED = 'd0cecda45cc8b8bc82862af7a57e26da5f81bd00';
const REPO = 'https://github.com/zion-sati/NetWasm';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const argv = process.argv.slice(2);
const command = argv[0] ?? 'all';
const opt = (name, dflt) => { const i = argv.indexOf(name); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : dflt; };
const flag = (name) => argv.includes(name);

const commit = opt('--commit', PINNED);
const cache = resolve(opt('--cache', process.env.IL2WASM_NETWASM_CACHE ?? join(process.env.LOCALAPPDATA ?? join(homedir(), '.cache'), 'il2wasm', 'netwasm')));
const checkout = resolve(opt('--checkout', join(cache, `checkout-${commit.slice(0, 12)}`)));
const work = join(cache, `work-${commit.slice(0, 12)}`);
const par = +opt('--par', Math.max(1, Math.floor(cpus().length / 2)));
const runs = +opt('--runs', 3);
const rid = opt('--rid', 'win-x86');
const timeoutMs = +opt('--timeout', 120) * 1000;
const filter = opt('--filter') ? new RegExp(opt('--filter')) : null;
const compileCmd = opt('--compile', process.env.IL2WASM_COMPILE);
const runCmd = opt('--run', 'node host/run.mjs {out}');
const modes = opt('--modes', 'checked,shipped').split(',');
const repeat = +opt('--repeat', 1);
const force = flag('--force');
const config = 'Release';

if (resolve(cache).toLowerCase().startsWith(root.toLowerCase())) {
  console.error(`the cache (${cache}) must be outside the repository: NetWasm's Community-licensed tests are never kept in it`);
  process.exit(2);
}

// ---- processes ----

function run(cmd, args, { cwd, env, timeout = timeoutMs, input } = {}) {
  return new Promise((done) => {
    const t0 = Date.now();
    const p = spawn(cmd, args, { cwd, env: { ...process.env, ...env }, windowsHide: true });
    const out = [], err = [];
    let timedOut = false;
    const timer = setTimeout(() => { timedOut = true; killTree(p.pid); }, timeout);
    p.stdout.on('data', (d) => out.push(d));
    p.stderr.on('data', (d) => err.push(d));
    if (input !== undefined) p.stdin.end(input); else p.stdin.end();
    p.on('error', (e) => { clearTimeout(timer); done({ code: -1, stdout: '', stderr: String(e), ms: Date.now() - t0, timedOut }); });
    p.on('close', (code) => { clearTimeout(timer); done({ code, stdout: Buffer.concat(out).toString('latin1'), stderr: Buffer.concat(err).toString('utf8'), ms: Date.now() - t0, timedOut }); });
  });
}

function killTree(pid) {
  try {
    if (process.platform === 'win32') execFileSync('taskkill', ['/pid', String(pid), '/t', '/f'], { stdio: 'ignore' });
    else process.kill(-pid, 'SIGKILL');
  } catch { }
}

async function pool(items, n, fn) {
  let next = 0, doneCount = 0;
  const t0 = Date.now();
  const worker = async () => {
    while (next < items.length) {
      const i = next++;
      await fn(items[i], i);
      doneCount++;
      if (process.stdout.isTTY && (doneCount % 10 === 0 || doneCount === items.length))
        process.stdout.write(`\r  ${doneCount}/${items.length} (${((Date.now() - t0) / 1000).toFixed(0)} s)   `);
    }
  };
  await Promise.all(Array.from({ length: Math.min(n, items.length) }, worker));
  if (process.stdout.isTTY) process.stdout.write('\n');
}

// Splits a command template into words (double quotes group) and fills its placeholders.
function template(cmd, vars) {
  const words = cmd.match(/"[^"]*"|\S+/g) ?? [];
  return words.map((w) => w.replace(/^"|"$/g, '').replace(/\{(\w+)\}/g, (_, k) => vars[k] ?? '')).filter((w) => w !== '');
}

const readJson = (p, dflt) => existsSync(p) ? JSON.parse(readFileSync(p, 'utf8')) : dflt;
const writeJson = (p, v) => { mkdirSync(dirname(p), { recursive: true }); writeFileSync(p, JSON.stringify(v, null, 1)); };

// ---- fetch ----

async function fetchCheckout() {
  if (existsSync(join(checkout, 'tests', 'NetWasm.Compiler.Tests'))) {
    const head = await run('git', ['-C', checkout, 'rev-parse', 'HEAD']);
    if (head.stdout.trim() === commit || opt('--checkout')) { console.log(`checkout: ${checkout} (${head.stdout.trim().slice(0, 12)})`); return; }
  }
  console.log(`fetching ${REPO} ${commit} into ${checkout}`);
  mkdirSync(checkout, { recursive: true });
  for (const a of [['init', '-q'], ['remote', 'add', 'origin', REPO], ['fetch', '-q', '--depth', '1', 'origin', commit], ['checkout', '-q', 'FETCH_HEAD']]) {
    const r = await run('git', ['-C', checkout, ...a], { timeout: 600_000 });
    if (r.code !== 0 && !(a[0] === 'remote' && /exists/.test(r.stderr))) { console.error(`git ${a.join(' ')}: ${r.stderr}`); process.exit(1); }
  }
}

// ---- extract ----

async function extract() {
  const proj = join(root, 'tools', 'netwasm-suite', 'Extract');
  const b = await run('dotnet', ['build', proj, '-c', 'Release', '-v', 'q', '-nologo'], { timeout: 600_000 });
  if (b.code !== 0) { console.error(b.stdout + b.stderr); process.exit(1); }
  mkdirSync(work, { recursive: true });
  const r = await run('dotnet', [join(proj, 'bin', 'Release', 'net11.0', 'Extract.dll'), '--checkout', checkout, '--work', work, '--commit', commit, '--shim', join(root, 'tools', 'netwasm-suite', 'Shim', 'TUnitShim.cs')], { timeout: 600_000 });
  process.stdout.write(r.stdout);
  if (r.code !== 0) { console.error(r.stderr); process.exit(1); }
  // the build settings every case shares (the same for the .NET run and il2wasm's: one IL)
  writeFileSync(join(work, 'Directory.Build.props'), `<Project>
  <!-- generated by il2wasm's tools/netwasm-suite.mjs: the settings every case project shares -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <InvariantGlobalization>true</InvariantGlobalization>
    <UseSystemResourceKeys>false</UseSystemResourceKeys>
    <Deterministic>true</Deterministic>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <WarningLevel>0</WarningLevel>
    <NoWarn>$(NoWarn);CS8981;CS0618;CS0612;CS1998;CS8500;SYSLIB0050;SYSLIB0011;SYSLIB5005</NoWarn>
    <RuntimeIdentifier Condition="'$(Il2WasmRid)' != ''">$(Il2WasmRid)</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
  </PropertyGroup>
</Project>
`);
  writeFileSync(join(work, 'Directory.Build.targets'), '<Project />\n');
  writeFileSync(join(work, 'Directory.Packages.props'), '<Project />\n');
}

function loadCases() {
  const all = readJson(join(work, 'cases.json'), null);
  if (!all) { console.error(`no ${join(work, 'cases.json')}: run extract first`); process.exit(1); }
  return all.filter((c) => !filter || filter.test(c.id) || c.aliases.some((a) => filter.test(a)));
}

// ---- setup: the oracle's runtime ----

// A .NET root for the oracle's runtime identifier, assembled from the runtime pack NuGet restores (no machine-wide
// install): host/fxr/<v>/hostfxr.dll and shared/Microsoft.NETCore.App/<v>/. The case's apphost finds it through
// DOTNET_ROOT_<ARCH>.
const dotnetRoot = join(cache, `dotnet-${rid}`);
async function setup() {
  if (!existsSync(join(dotnetRoot, 'host', 'fxr'))) await assembleRuntime();
  // the check: a framework-dependent program, built and run as the cases are, says which runtime it ran on
  const checkFile = join(dotnetRoot, 'check.txt');
  if (!existsSync(checkFile)) {
    const dir = join(cache, `runtime-check-${rid}`);
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'check.csproj'), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><RuntimeIdentifier>${rid}</RuntimeIdentifier><SelfContained>false</SelfContained><AssemblyName>check</AssemblyName></PropertyGroup></Project>\n`);
    writeFileSync(join(dir, 'Check.cs'), 'System.Console.WriteLine(".NET " + System.Environment.Version + ", " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + ", IntPtr.Size " + System.IntPtr.Size + ", from " + System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location));\n');
    const b = await run('dotnet', ['build', dir, '-c', config, '-v', 'q', '-nologo'], { timeout: 600_000 });
    if (b.code !== 0) { console.error(b.stdout + b.stderr); process.exit(1); }
    const r = await run(join(dir, 'bin', config, 'net11.0', rid, process.platform === 'win32' ? 'check.exe' : 'check'), [], { env: oracleEnv() });
    if (r.code !== 0) { console.error(`the oracle's runtime does not start: ${r.stdout}${r.stderr}`); process.exit(1); }
    writeFileSync(checkFile, r.stdout);
  }
  console.log(`oracle runtime: ${readFileSync(checkFile, 'utf8').trim()}`);
}

async function assembleRuntime() {
  const probe = join(cache, `runtime-probe-${rid}`);
  mkdirSync(probe, { recursive: true });
  writeFileSync(join(probe, 'probe.csproj'), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><RuntimeIdentifier>${rid}</RuntimeIdentifier><SelfContained>true</SelfContained></PropertyGroup></Project>\n`);
  writeFileSync(join(probe, 'Probe.cs'), 'System.Console.WriteLine(System.Environment.Version + " " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + " " + System.IntPtr.Size);\n');
  const r = await run('dotnet', ['restore', probe, '-nologo'], { timeout: 600_000 });
  if (r.code !== 0) { console.error(r.stdout + r.stderr); process.exit(1); }
  const assets = readJson(join(probe, 'obj', 'project.assets.json'));
  const folder = Object.keys(assets.packageFolders)[0];
  // (a runtime pack is a download dependency of the project, not a library of it)
  const name = `microsoft.netcore.app.runtime.${rid}`;
  const dep = Object.values(assets.project.frameworks)[0].downloadDependencies.find((d) => d.name.toLowerCase() === name);
  if (!dep) { console.error(`no Microsoft.NETCore.App.Runtime.${rid} in the restore`); process.exit(1); }
  const version = /\[([^,\]]+)/.exec(dep.version)[1].trim();
  const pack = join(folder, name, version, 'runtimes', rid);
  const fx = readdirSync(join(pack, 'lib'))[0];
  const shared = join(dotnetRoot, 'shared', 'Microsoft.NETCore.App', version);
  mkdirSync(shared, { recursive: true });
  for (const sub of [join(pack, 'lib', fx), join(pack, 'native')])
    for (const f of readdirSync(sub)) cpSync(join(sub, f), join(shared, f), { recursive: true });
  mkdirSync(join(dotnetRoot, 'host', 'fxr', version), { recursive: true });
  cpSync(join(shared, 'hostfxr.dll'), join(dotnetRoot, 'host', 'fxr', version, 'hostfxr.dll'));
  console.log(`oracle runtime: ${dotnetRoot} (${version})`);
}

const oracleEnv = () => ({
  [`DOTNET_ROOT_${rid.split('-')[1].toUpperCase()}`]: dotnetRoot,
  ...(rid === 'win-x86' ? { 'DOTNET_ROOT(x86)': dotnetRoot } : {}),
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
});

// ---- build ----

const binDir = (c) => join(work, 'cases', c.dir, 'bin', config, 'net11.0', rid);
const exePath = (c) => join(binDir(c), process.platform === 'win32' ? 'case.exe' : 'case');
const dllPath = (c) => join(binDir(c), 'case.dll');

async function build(cases) {
  const todo = cases.filter((c) => !c.driverProblem);
  // one traversal project, one msbuild with -m: far faster than one dotnet build a project (a solution will not
  // do: every project is named case)
  const all = join(work, 'all.proj');
  writeFileSync(all, `<Project>
  <ItemGroup>
${todo.map((c) => `    <CaseProject Include="cases/${c.dir}/case.csproj" />`).join('\n')}
  </ItemGroup>
  <Target Name="Restore"><MSBuild Projects="@(CaseProject)" Targets="Restore" BuildInParallel="true" ContinueOnError="true" /></Target>
  <Target Name="Build"><MSBuild Projects="@(CaseProject)" Targets="Build" BuildInParallel="true" ContinueOnError="true" /></Target>
</Project>
`);
  console.log(`building ${todo.length} cases (${rid}, ${config}, -m:${par})`);
  const r = await run('dotnet', ['msbuild', all, '-restore', '-t:Build', `-p:Configuration=${config}`, `-m:${par}`, '-v:q', '-nologo', '-clp:NoSummary', `-p:Il2WasmRid=${rid}`, '-nodeReuse:false'], { cwd: work, timeout: 7_200_000 });
  const errors = {};
  for (const line of (r.stdout + r.stderr).split(/\r?\n/)) {
    const m = / error [^:]*:.*\[(.*case\.csproj)[^\]]*\]$/.exec(line);
    if (m) { const d = /cases[\\/]([^\\/]+)[\\/]case\.csproj/.exec(m[1])?.[1]; if (d) (errors[d] ??= new Set()).add(line.replace(/\s*\[[^\]]*\]$/, '').replace(/^.*cases[\\/][^\\/]+[\\/]/, '')); }
  }
  const status = {};
  for (const c of cases) {
    if (c.driverProblem) status[c.dir] = { ok: false, reason: `driver: ${c.driverProblem}` };
    else if (existsSync(dllPath(c)) && !errors[c.dir]) status[c.dir] = { ok: true };
    else status[c.dir] = { ok: false, reason: errors[c.dir] ? [...errors[c.dir]].slice(0, 8).join('\n') : 'no output (see the build log)' };
  }
  writeFileSync(join(work, 'build.log'), r.stdout + r.stderr);
  const prev = readJson(join(work, 'build.json'), {});
  writeJson(join(work, 'build.json'), { ...prev, ...status });
  const ok = Object.values(status).filter((s) => s.ok).length;
  console.log(`built ${ok} of ${cases.length}; ${cases.length - ok} did not build (build.json)`);
}

// ---- oracle ----

const observed = (r) => r.timedOut ? `${r.stdout}timeout\n` : `${r.stdout}exit ${r.code}\n`;

async function oracle(cases) {
  const built = readJson(join(work, 'build.json'), {});
  const todo = cases.filter((c) => built[c.dir]?.ok && (force || !existsSync(join(work, 'oracle', `${c.dir}.json`))));
  console.log(`oracle: ${todo.length} cases x ${runs} runs under .NET ${rid} (${par} at once)`);
  await pool(todo, par, async (c) => {
    const results = [];
    for (let k = 0; k < runs; k++) results.push(await run(exePath(c), [], { cwd: binDir(c), env: oracleEnv() }));
    // the open-source programs: once more with hardware intrinsics off (a library's portable paths must agree)
    if (c.origin === 'sut') results.push({ ...(await run(exePath(c), [], { cwd: binDir(c), env: { ...oracleEnv(), DOTNET_EnableHWIntrinsic: '0' } })), variant: 'no-hw-intrinsics' });
    const outs = results.map(observed);
    const variants = [...new Set(outs.slice(0, runs))];
    const rec = {
      id: c.id, dir: c.dir,
      stable: variants.length === 1 && !results.slice(0, runs).some((r) => r.timedOut),
      timedOut: results.slice(0, runs).some((r) => r.timedOut),
      variants: variants.length,
      ms: results.map((r) => r.ms),
      exit: results.map((r) => r.code),
      stderr: results.map((r) => r.stderr.slice(-2000)).filter((s, i, a) => s && a.indexOf(s) === i),
      expected: outs[0],
      others: variants.slice(1),
    };
    if (c.origin === 'sut' && outs[runs] !== outs[0]) { rec.hwDependent = true; rec.noHwIntrinsics = outs[runs]; }
    writeJson(join(work, 'oracle', `${c.dir}.json`), rec);
    mkdirSync(join(work, 'expected'), { recursive: true });
    writeFileSync(join(work, 'expected', `${c.dir}.txt`), outs[0]);
  });
}

// ---- run: il2wasm ----

async function runIl2wasm(cases) {
  if (!compileCmd) { console.log('run: no compiler given (--compile or IL2WASM_COMPILE); skipped'); return; }
  const standin = compileCmd === 'dotnet-standin';
  const results = readJson(join(work, 'results.json'), {});
  const todo = [];
  for (const c of cases) {
    const o = readJson(join(work, 'oracle', `${c.dir}.json`), null);
    if (!o) continue;
    for (const mode of modes) todo.push({ c, o, mode });
  }
  console.log(`run: ${todo.length} case-modes (${standin ? 'the .NET build standing in for the module' : compileCmd}), ${par} at once${repeat > 1 ? `, each run ${repeat} times at once` : ''}`);
  await pool(todo, par, async ({ c, o, mode }) => {
    const key = `${c.dir}|${mode}`;
    if (!force && results[key]?.status === 'pass' && results[key].compiler === compileCmd) return;
    const out = join(work, 'modules', mode, `${c.dir}.wasm`);
    mkdirSync(dirname(out), { recursive: true });
    const vars = { dll: dllPath(c), project: join(work, 'cases', c.dir, 'case.csproj'), casedir: join(work, 'cases', c.dir), out, mode, checked: mode === 'checked' ? '--checked' : '', root };
    let compile = { code: 0, ms: 0, stderr: '', stdout: '' };
    if (!standin) {
      const words = template(compileCmd, vars);
      compile = await run(words[0], words.slice(1), { cwd: root, timeout: Math.max(timeoutMs, 600_000) });
      if (compile.code !== 0 || !existsSync(out)) {
        results[key] = { status: 'compile-fail', compiler: compileCmd, ms: compile.ms, detail: (compile.stdout + compile.stderr).slice(-3000) };
        return;
      }
    }
    const runOnce = () => {
      if (standin) return run(exePath(c), [], { cwd: binDir(c), env: oracleEnv() });
      const words = template(runCmd, vars);
      return run(words[0], words.slice(1), { cwd: binDir(c) });
    };
    const rs = await Promise.all(Array.from({ length: repeat }, runOnce));
    const outs = rs.map(observed);
    const bad = outs.findIndex((x) => x !== o.expected);
    const status = bad < 0 ? 'pass' : rs[bad].timedOut ? 'timeout' : 'fail';
    results[key] = { status, compiler: compileCmd, compileMs: compile.ms, ms: rs.map((r) => r.ms), oracleStable: o.stable, ...(bad >= 0 ? { diff: diff(o.expected, outs[bad]), stderr: rs[bad].stderr.slice(-2000) } : {}) };
  });
  writeJson(join(work, 'results.json'), results);
}

function diff(expected, actual) {
  const e = expected.split('\n'), a = actual.split('\n');
  let i = 0;
  while (i < e.length && i < a.length && e[i] === a[i]) i++;
  return { line: i + 1, expectedLines: e.length, actualLines: a.length, expected: e.slice(i, i + 6), actual: a.slice(i, i + 6) };
}

// ---- report ----

function report(cases) {
  const built = readJson(join(work, 'build.json'), {});
  const results = readJson(join(work, 'results.json'), {});
  const cls = readJson(join(work, 'classification.json'), {});
  const notExtracted = readJson(join(work, 'not-extracted.json'), []);
  const rows = [];
  const t = { cases: cases.length, inputs: 0, built: 0, buildFailed: 0, oracle: 0, stable: 0, unstable: 0, oracleTimeout: 0, hwDependent: 0, modes: {} };
  for (const c of cases) {
    t.inputs += c.inputs;
    const b = built[c.dir];
    const o = readJson(join(work, 'oracle', `${c.dir}.json`), null);
    if (b?.ok) t.built++; else if (b) t.buildFailed++;
    if (o) { t.oracle++; if (o.stable) t.stable++; else if (o.timedOut) t.oracleTimeout++; else t.unstable++; if (o.hwDependent) t.hwDependent++; }
    const row = { id: c.id, dir: c.dir, origin: c.origin, file: c.file, tags: c.tags, aliases: c.aliases, build: b?.ok ? 'ok' : b ? b.reason : 'not built', oracle: o ? (o.stable ? 'stable' : o.timedOut ? 'timeout' : `unstable (${o.variants} outputs)`) : 'not run', results: {} };
    for (const mode of modes) {
      const r = results[`${c.dir}|${mode}`];
      if (!r) continue;
      const m = (t.modes[mode] ??= { pass: 0, fail: 0, compileFail: 0, timeout: 0, passOfStable: 0, stable: 0 });
      if (r.status === 'pass') m.pass++; else if (r.status === 'compile-fail') m.compileFail++; else if (r.status === 'timeout') m.timeout++; else m.fail++;
      if (r.oracleStable) { m.stable++; if (r.status === 'pass') m.passOfStable++; }
      row.results[mode] = r;
    }
    rows.push(row);
  }
  const rep = { commit, rid, config, date: new Date().toISOString(), totals: t, notExtracted: notExtracted.length, classification: Object.fromEntries(Object.entries(cls).filter(([k, v]) => Array.isArray(v) && k !== 'acceptedDifferences' && k !== 'specDeviationsDoc').map(([k, v]) => [k, v.length])), cases: rows };
  writeJson(join(work, 'report.json'), rep);
  if (opt('--json')) writeJson(resolve(opt('--json')), rep);
  console.log(`== NetWasm ${commit.slice(0, 12)} against .NET ${rid}: ${t.cases} cases (${t.inputs} inputs), ${notExtracted.length} test methods not extracted`);
  console.log(`   built ${t.built}, did not build ${t.buildFailed}; oracle ran ${t.oracle}: ${t.stable} stable over ${runs} runs, ${t.unstable} unstable, ${t.oracleTimeout} timed out${t.hwDependent ? `, ${t.hwDependent} differ without hardware intrinsics` : ''}`);
  for (const [mode, m] of Object.entries(t.modes))
    console.log(`== ${mode}: ${m.pass} passed, ${m.fail} failed, ${m.compileFail} did not compile, ${m.timeout} timed out (${m.passOfStable} of ${m.stable} with a stable oracle)`);
  const show = +opt('--show', 40);
  // the oracle's own problems: cases that did not build, outputs that differ between .NET runs
  for (const r of rows.filter((r) => r.build !== 'ok' && r.build !== 'not built').slice(0, show))
    console.log(`NOBUILD ${r.id}: ${r.build.split('\n')[0].slice(0, 200)}`);
  for (const c of cases) {
    const o = readJson(join(work, 'oracle', `${c.dir}.json`), null);
    if (!o || (o.stable && !o.hwDependent)) continue;
    const d = o.others.length ? diff(o.expected, o.others[0]) : o.noHwIntrinsics ? diff(o.expected, o.noHwIntrinsics) : null;
    console.log(`${o.timedOut ? 'TIMEOUT' : o.stable ? 'HW-DEPENDENT' : 'UNSTABLE'} ${c.id} (${o.variants} outputs${o.hwDependent ? ', differs without hardware intrinsics' : ''})${d ? ` first at line ${d.line}: ${d.expected[0]?.slice(0, 120)} | ${d.actual[0]?.slice(0, 120)}` : ''}`);
  }
  let shown = 0;
  for (const r of rows) for (const [mode, x] of Object.entries(r.results)) {
    if (x.status === 'pass' || shown++ >= show) continue;
    console.log(`FAIL ${r.id} [${mode}] ${x.status}${x.diff ? ` at line ${x.diff.line}` : ''}`);
    if (x.diff) { for (const l of x.diff.expected.slice(0, 3)) console.log(`  - ${l}`); for (const l of x.diff.actual.slice(0, 3)) console.log(`  + ${l}`); }
    else if (x.detail) console.log(x.detail.split('\n').slice(-6).map((l) => '  ' + l).join('\n'));
  }
  console.log(`(report: ${join(work, 'report.json')})`);
  return rep;
}

// ---- main ----

if (command === 'homelab') {
  const branch = argv[1];
  if (!branch) { console.error('usage: node tools/netwasm-suite.mjs homelab <pushed branch> [options]'); process.exit(2); }
  const rest = argv.slice(2);
  const p = spawn('bash', [join(root, 'tools', 'homelab.sh'), branch, 'node', 'tools/netwasm-suite.mjs', 'all', ...rest], { stdio: 'inherit' });
  p.on('close', (code) => process.exit(code ?? 1));
} else {
  const t0 = Date.now();
  if (command === 'fetch' || command === 'all') await fetchCheckout();
  if (command === 'extract' || command === 'all') await extract();
  if (command === 'setup' || command === 'all' || command === 'build' || command === 'oracle' || command === 'run') await setup();
  const cases = ['build', 'oracle', 'run', 'report', 'all'].includes(command) ? loadCases() : [];
  if (command === 'build' || command === 'all') await build(cases);
  if (command === 'oracle' || command === 'all') await oracle(cases);
  if (command === 'run' || command === 'all') await runIl2wasm(cases);
  if (['report', 'all', 'run', 'oracle'].includes(command)) {
    const rep = report(cases);
    if (command !== 'oracle' && Object.values(rep.totals.modes).some((m) => m.fail + m.compileFail + m.timeout > 0)) process.exitCode = 1;
  }
  console.log(`(${((Date.now() - t0) / 1000).toFixed(0)} s)`);
}
