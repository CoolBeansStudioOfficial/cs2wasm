// The self-hosted compiler on a snapshot (host/snapshot.mjs) never writes from stale state: each thing a snapshot's
// state was made from is changed in turn, and the build after it must say why the snapshot was let go (or, for what
// the compiler reads anew on every build, use it) and write the .NET-hosted compiler's module byte for byte.
//   another version of a source (the path the command line names: read anew, the snapshot holds); a source that
//   needs another library; another build of the compiler (another module); another library set (another reference
//   directory: another environment; a reference assembly changed in place, and one added to the directory: changed)
// Size and time of change are what a path's state is (as make and MSBuild judge): a file rewritten with the same size
// and the same time of change is not seen. Not covered here: that (by design), and references given with -r.
// usage: node tools/selfsnap-stale.mjs [--self tests/out/selfsw/as-is.wasm]     exits 1 when a step is not as expected
import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync, utimesSync, statSync, appendFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { selfPath, selfEnv, referenceDir, cache } from '../tests/Godot/proof5_hello2d/check/common.mjs';

const root = join(fileURLToPath(import.meta.url), '..', '..');
const args = process.argv.slice(2);
let self = join(root, 'tests', 'out', 'selfsw', 'as-is.wasm');
let refSelf = false;
for (let i = 0; i < args.length; i++) if (args[i] === '--self') self = resolve(args[++i]); else if (args[i] === '--ref-self') refSelf = true;
if (!existsSync(self)) { console.error(`selfsnap-stale: no compiler at ${self}`); process.exit(2); }
const exe = join(root, 'src', 'Cs2Wasm.Cli', 'bin', 'Release', 'net11.0', process.platform === 'win32' ? 'cs2wasmc.exe' : 'cs2wasmc');
const out = join(root, 'tests', 'out', 'selfsnap-stale');
rmSync(out, { recursive: true, force: true });
mkdirSync(join(out, 'src'), { recursive: true });
const prog = join(out, 'src', 'w.cs');
const hello = readFileSync(join(root, 'tests', 'Conformance', '145_size_hello.cs'), 'utf8');
const v1 = hello;
const v2 = hello.replace('=> Console.WriteLine(42)', '=> Console.WriteLine("v2 " + 43.5)');
const v3 = hello.replace('using System;', 'using System;\nusing System.Linq;').replace('=> Console.WriteLine(42)', '=> Console.WriteLine(new[] { 3, 1, 2 }.OrderBy(x => x).Sum())');
if (v2 === v1 || v3 === v1) { console.error('selfsnap-stale: test 145 changed shape'); process.exit(2); }
let failed = false, n = 0;

function net(refDir, tag) {
  const target = join(out, 'net-' + tag, 'w.wasm');
  mkdirSync(join(out, 'net-' + tag), { recursive: true });
  // (--ref-self: the reference is the self-hosted compiler from the start, no snapshot: to try this script with a
  // build of the compiler older than the checkout)
  const r = refSelf
    ? spawnSync('node', [join(root, 'host', 'run.mjs'), self, 'main', '--', selfPath(prog), ...(process.platform === 'win32' ? ['--pathmap', '/=/'] : []), '-o', selfPath(target)],
      { env: { ...selfEnv(), CS2WASM_REFERENCE_DIR: selfPath(refDir), CS2WASM_CACHE: selfPath(cache), CS2WASM_PROCESSORS: '1' }, encoding: 'utf8', maxBuffer: 1 << 28, timeout: 600000 })
    : spawnSync(exe, [selfPath(prog), ...(process.platform === 'win32' ? ['--pathmap', '/=/'] : []), '-o', selfPath(target)],
    { env: { ...process.env, CS2WASM_REFERENCE_DIR: refDir }, encoding: 'utf8', timeout: 600000 });
  if (r.status !== 0) { console.log(`net ${tag}: FAILED ${r.stdout}${r.stderr}`); process.exit(1); }
  return readFileSync(target);
}
// one build by the self-hosted compiler; `expect`: a regular expression the [snapshot] lines must match
function step(what, { module = self, snap, refDir = referenceDir(), expect, ref, notRef }) {
  const target = join(out, `s${++n}`, 'w.wasm');
  mkdirSync(join(out, `s${n}`), { recursive: true });
  const env = { ...selfEnv(), CS2WASM_REFERENCE_DIR: selfPath(refDir), CS2WASM_CACHE: selfPath(cache), CS2WASM_PROCESSORS: '1', CS2WASM_SNAPSHOT: snap, CS2WASM_SNAPSHOT_WHY: '1' };
  const t0 = performance.now();
  const r = spawnSync('node', [join(root, 'host', 'run.mjs'), module, 'main', '--', selfPath(prog), ...(process.platform === 'win32' ? ['--pathmap', '/=/'] : []), '-o', selfPath(target)],
    { env, encoding: 'utf8', maxBuffer: 1 << 28, timeout: 600000 });
  const s = ((performance.now() - t0) / 1000).toFixed(2);
  const all = (r.stdout || '') + (r.stderr || '');
  const notes = all.split('\n').filter((l) => l.startsWith('[snapshot]')).map((l) => l.slice(11).replace(/ in \d+ ms/, '').replace(/\d+ pages \(\d+ (written|not zero)\)/, 'N pages')).join('; ');
  let ok = r.status === 0 && existsSync(target), why = '';
  if (!ok) why = `exit ${r.status} ${all.slice(-800)}`;
  if (ok && !expect.test(notes)) { ok = false; why = `snapshot notes not ${expect}`; }
  const bytes = ok ? readFileSync(target) : null;
  if (ok && !bytes.equals(ref)) { ok = false; why = 'DIFFERS from the .NET-hosted compiler\'s module'; }
  if (ok && notRef && bytes.equals(notRef)) { ok = false; why = 'the same module as the earlier version of the source'; }
  if (!ok) failed = true;
  console.log(`${n}. ${what}: ${ok ? 'as expected' : 'NOT AS EXPECTED: ' + why} (${s} s) [${notes}]`);
}

const refDir = referenceDir();
const S = join(out, 'compiler.snap');
writeFileSync(prog, v1); const net1 = net(refDir, 'v1');
writeFileSync(prog, v2); const net2 = net(refDir, 'v2');
writeFileSync(prog, v3); const net3 = net(refDir, 'v3');
console.log(`the .NET-hosted compiler: v1 ${net1.length} bytes, v2 ${net2.length}, v3 ${net3.length}`);

writeFileSync(prog, v1);
step('no snapshot: from the start, written', { snap: S, expect: /^written: /, ref: net1 });
step('the same source on it', { snap: S, expect: /^restored .*; \d+ paths checked$/, ref: net1 });
writeFileSync(prog, v2);
step('another version of the source at the same path: the snapshot holds, the source is read anew', { snap: S, expect: /^restored .*paths checked$/, ref: net2, notRef: net1 });
writeFileSync(prog, v3);
step('a version that needs another library (System.Linq): on the snapshot (written again if it read files the snapshot does not cover)', { snap: S, expect: /^restored .*paths checked(; written: .*)?$/, ref: net3 });
writeFileSync(prog, v1);
step('back to the first version on that snapshot', { snap: S, expect: /^restored .*paths checked$/, ref: net1, notRef: net3 });

// another build of the compiler: the same module with one more custom section
const other = join(out, 'other.wasm');
writeFileSync(other, Buffer.concat([readFileSync(self), Buffer.from([0, 5, 4, 0x73, 0x6e, 0x61, 0x70])]));
// (one snapshot file throughout: its path is in the environment the module is given)
const keep = join(out, 'kept.snap'); copyFileSync(S, keep);
step('another build of the compiler on that snapshot: let go', { module: other, snap: S, expect: /^not used: another module; written: /, ref: net1 });

// another library set: a copy of the reference directory (the same files)
const refCopy = join(out, 'ref');
cpSync(refDir, refCopy, { recursive: true, preserveTimestamps: true });
copyFileSync(keep, S); const S3 = S;
const netCopy = net(refCopy, 'copy');
console.log(`the .NET-hosted compiler with the copy: ${netCopy.equals(net1) ? 'the same module' : 'another module'}`);
step('another reference directory (the same files): another environment, let go', { snap: S3, refDir: refCopy, expect: /^not used: another environment \(CS2WASM_REFERENCE_DIR\); written: /, ref: netCopy });
step('on its snapshot', { snap: S3, refDir: refCopy, expect: /^restored .*paths checked$/, ref: netCopy });
const norm = (p) => p.replaceAll('\\', '/').toLowerCase();
const inCopy = (p) => norm(p).startsWith(norm(refCopy) + '/');
const deps = JSON.parse(readFileSync(S3).subarray(12, 12 + readFileSync(S3).readUInt32LE(8)).toString('utf8')).deps.map(([p]) => p);
const lib = deps.find((p) => inCopy(p) && /system\.runtime\.dll$/i.test(p)) ?? deps.find((p) => inCopy(p) && /\.dll$/i.test(p));
console.log(`the snapshot depends on ${deps.length} paths; ${deps.filter(inCopy).length} in the reference directory${lib ? `; changed in place: ${lib}` : ''}`);
if (!lib) { console.log('NOT AS EXPECTED: no reference assembly among the paths the snapshot depends on'); failed = true; }
else {
  const t = statSync(lib).mtime; utimesSync(lib, t, new Date(t.getTime() + 2000));
  step('a reference assembly changed in place (its time of change): let go', { snap: S3, refDir: refCopy, expect: /^not used: changed: .*; written: /, ref: netCopy });
  step('on the new one', { snap: S3, refDir: refCopy, expect: /^restored .*paths checked$/, ref: netCopy });
  const dirDep = deps.find((p) => norm(p) === norm(refCopy));
  if (dirDep) {
    writeFileSync(join(refCopy, 'added.txt'), 'a file added');
    step('a file added to the reference directory: let go', { snap: S3, refDir: refCopy, expect: /^not used: changed: .*; written: /, ref: net(refCopy, 'added') });
  } else console.log('(the reference directory itself is not listed by the compiler: a file added to it is not a dependency)');
}
console.log(failed ? 'selfsnap-stale: FAILED' : 'selfsnap-stale: every snapshot that no longer holds was let go, every module the .NET-hosted compiler\'s byte for byte');
process.exit(failed ? 1 : 0);
