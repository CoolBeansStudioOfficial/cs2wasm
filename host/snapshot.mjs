// A module's state kept from one process to the next (SPEC section 7.2: a module's whole state is its memory and its
// mutable globals; section 7.6: the analysis of library compilations that did not change may be cached). For the
// self-hosted compiler: its libraries stay parsed and bound from build to build, as in one process they do under
// `--watch` (Library.s_built), so a later build of a small program does not read them again.
//
//   IL2WASM_SNAPSHOT=<file>   node host/run.mjs module.wasm main -- arguments...
//
// No snapshot (or one that no longer holds): the module runs from its start, and when its entry has returned 0 its
// memory and globals are written to the file. A snapshot that holds: the memory and globals are put back in place of
// `init`, and the entry runs on them: the second call of the entry in one process, in a new one.
//
// A snapshot holds when everything the kept state was made from is what it was:
//   - the module (its bytes' hash) and the environment block the module is given (every variable: a module reads it
//     once and keeps what it made of it);
//   - every path the module asked the file system about while the state was made (opened, listed, asked whether
//     there): the same size and time of change, or still absent. Left out: what the entry is given anew on every run
//     and keeps nothing of, which is the paths its command line names (an argument that is no option and no
//     `name=value`; those under it; those in an `@file` of arguments), and the paths it wrote.
// A snapshot is never taken of a module that started a thread (a thread's stack is not in memory: section 7.2), has
// a file or directory open, moved its working directory, or uses HTTP (the host's side of those is not in memory).
// After a run on a snapshot the file is written again only when the module read files the snapshot did not cover
// (a library the earlier programs did not need); otherwise every build starts from the same state.
// What is written is the memory as the entry left it, the last build's own leftovers with the libraries: after a
// large program the snapshot is large, and every later run starts at that size.
//   IL2WASM_SNAPSHOT_KEEP=1: a snapshot that holds is never written again (one made with a small program stays
//   small; a program that needs more libraries then reads them on every run)
//   IL2WASM_SNAPSHOT_WHY=1: why a snapshot was not used, and what was written, on stderr
import * as fs from 'node:fs';
import * as path from 'node:path';
import { createHash } from 'node:crypto';

const MAGIC = 'CS2WSNP2';
const PAGE = 65536;
const why = (s) => { if (process.env.IL2WASM_SNAPSHOT_WHY === '1') process.stderr.write(`[snapshot] ${s}\n`); };

/** A path's state as a snapshot records it: absent, a directory (its time of change: an entry added or removed), or
 * a file (size and time of change). */
function signature(p) {
  let st;
  try { st = fs.statSync(p, { bigint: true }); } catch { return 'x'; }
  return st.isDirectory() ? `d${st.mtimeNs}` : `${st.size}:${st.mtimeNs}`;
}

const envBlock = () => Object.entries(process.env).map(([k, v]) => `${k}=${v}\0`).join('');

const fold = (p) => (process.platform === 'win32' ? p.toLowerCase() : p);

/** The paths a command line names, as paths of this machine (the module sees `root` as "/"): each argument that is
 * no option (and no `name=value`), and such lines of an `@file` of arguments. One that is the root itself names
 * nothing (everything would be under it). */
function argumentPaths(args, root) {
  const out = [];
  const real = (v) => path.join(root, ...path.posix.resolve('/', v).split('/').filter(Boolean));
  const add = (a) => {
    if (!a || a.startsWith('-') || a.includes('=')) return;
    const p = real(a);
    if (path.relative(root, p) !== '') out.push(fold(p));
  };
  for (const a of args) {
    if (a.startsWith('@')) {
      add(a.slice(1));
      try { for (const l of fs.readFileSync(real(a.slice(1)), 'utf8').split(/\r?\n/)) add(l); } catch { /* (not there: the module says so) */ }
    } else add(a);
  }
  return out;
}

/**
 * The snapshot of one run. `file`: the snapshot's file; `moduleBytes`: the module's; `programArgs`: the module's
 * command line without its own name. Call `restore(instance, memory)` in place of the module's `init` (false: there
 * is none that holds, call `init`), and `save(instance, memory, state)` when the entry has returned.
 */
export function openSnapshot(file, moduleBytes, programArgs) {
  const root = process.env.IL2WASM_FS_ROOT ?? process.cwd();
  const moduleHash = createHash('sha256').update(moduleBytes).digest('hex');
  const envHash = createHash('sha256').update(envBlock()).digest('hex');
  const named = argumentPaths(programArgs, root);
  const isNamed = (p) => { const l = fold(p); return named.some((n) => l === n || l.startsWith(n + path.sep) || l.startsWith(n + '/')); };
  // what this run asked the file system about: path -> its signature when first asked; and what it wrote
  const seen = new Map(), written = new Set();
  let open = 0, unsafe = null, covered = new Map();
  globalThis.__il2wasmFsWatch = {
    read: (p) => { if (!seen.has(p) && !covered.has(p)) seen.set(p, signature(p)); },
    write: (p) => { written.add(p); },
    opened: () => { open++; }, closed: () => { open--; },
    unsafe: (what) => { unsafe ??= what; },
  };

  let header = null, fd = -1, dataStart = 0;
  try {
    fd = fs.openSync(file, 'r');
    const head = Buffer.alloc(12);
    fs.readSync(fd, head, 0, 12, 0);
    if (head.toString('latin1', 0, 8) !== MAGIC) throw new Error('another format');
    const n = head.readUInt32LE(8);
    const json = Buffer.alloc(n);
    fs.readSync(fd, json, 0, n, 12);
    header = JSON.parse(json.toString('utf8'));
    dataStart = 12 + n;
    if (header.module !== moduleHash) throw new Error('another module');
    if (header.env !== envHash) {
      const now = new Map(Object.entries(process.env)), names = new Set([...now.keys(), ...Object.keys(header.envNames ?? {})]);
      const diff = [...names].filter((k) => createHash('sha256').update(`${k}=${now.get(k)}`).digest('hex').slice(0, 16) !== (header.envNames ?? {})[k]);
      throw new Error('another environment (' + diff.slice(0, 6).join(', ') + ')');
    }
    for (const [p, sig] of header.deps) if (signature(p) !== sig) throw new Error('changed: ' + p);
    // (all of it there: one cut short, by a full disk or a copy that did not end, is not put back in part)
    if (fs.fstatSync(fd).size !== dataStart + header.runs.reduce((s, r) => s + r[1], 0) * PAGE) throw new Error('cut short');
    covered = new Map(header.deps);
  } catch (e) {
    if (fd >= 0) { fs.closeSync(fd); fd = -1; }
    if (e.code !== 'ENOENT') why(`not used: ${e.message}`);
    header = null;
  }

  return {
    /** Puts the kept state in place (the instance made, `init` not called). */
    restore(instance, memory) {
      if (!header) return false;
      const t0 = performance.now();
      const have = memory.buffer.byteLength / PAGE;
      // (a memory that cannot be made that large, the host's limit lower than when the snapshot was written: from the start)
      try { if (header.pages > have) memory.grow(header.pages - have); }
      catch { why(`not used: the memory cannot grow to ${header.pages} pages`); fs.closeSync(fd); fd = -1; header = null; covered = new Map(); return false; }
      const u8 = new Uint8Array(memory.buffer);
      // (pages the snapshot has as zero: those the instantiation wrote data into are cleared; grown ones are zero)
      let at = 0, pos = dataStart;
      const chunk = Buffer.allocUnsafe(64 * PAGE);
      for (const [start, count] of header.runs) {
        if (start > at && at < have) u8.fill(0, at * PAGE, Math.min(start, have) * PAGE);
        for (let done = 0; done < count * PAGE; ) {
          const n = Math.min(chunk.length, count * PAGE - done);
          const r = fs.readSync(fd, chunk, 0, n, pos);
          if (r !== n) throw new Error(`snapshot ${file} is cut short`);
          u8.set(r === chunk.length ? chunk : chunk.subarray(0, r), start * PAGE + done);
          done += n; pos += n;
        }
        at = start + count;
      }
      if (at < have) u8.fill(0, at * PAGE, have * PAGE);
      fs.closeSync(fd); fd = -1;
      for (const [name, v] of Object.entries(header.globals)) instance.exports[name].value = typeof v === 'string' ? BigInt(v) : v;
      why(`restored ${header.pages} pages (${header.runs.reduce((s, r) => s + r[1], 0)} written) and ${Object.keys(header.globals).length} globals in ${(performance.now() - t0).toFixed(0)} ms; ${header.deps.length} paths checked`);
      return true;
    },

    /** Writes the state, when the run ended as one that may be kept. `state`: { result, failed, threads, io }. */
    save(instance, memory, state) {
      const restored = header != null;
      const fresh = [...seen].filter(([p]) => !written.has(p) && !isNamed(p));
      if (restored && fresh.length === 0) return;
      // (IL2WASM_SNAPSHOT_KEEP=1: a snapshot that holds is never written again)
      if (restored && process.env.IL2WASM_SNAPSHOT_KEEP === '1') { why(`not written again (IL2WASM_SNAPSHOT_KEEP): ${fresh.length} paths it does not cover were read`); return; }
      const no = state.failed ? 'the entry failed' : state.result !== 0 && state.result !== undefined ? `the entry returned ${state.result}` : state.threads ? 'the module started a thread'
        : state.io ? 'the module uses HTTP' : open !== 0 ? 'a file is open' : unsafe;
      if (no) { why(`not written: ${no}`); return; }
      const t0 = performance.now();
      const u8 = new Uint8Array(memory.buffer), pages = u8.length / PAGE;
      const u64 = new BigUint64Array(memory.buffer, 0, u8.length / 8);
      const runs = [];
      for (let p = 0; p < pages; p++) {
        let zero = true;
        for (let i = p * (PAGE / 8), e = i + PAGE / 8; i < e; i++) if (u64[i] !== 0n) { zero = false; break; }
        if (zero) continue;
        if (runs.length && runs.at(-1)[0] + runs.at(-1)[1] === p) runs.at(-1)[1]++; else runs.push([p, 1]);
      }
      const globals = {};
      for (const [name, g] of Object.entries(instance.exports)) {
        if (!(g instanceof WebAssembly.Global)) continue;
        const v = g.value;
        try { g.value = v; } catch { continue; } // (not mutable: the instantiation gives it)
        globals[name] = typeof v === 'bigint' ? String(v) : v;
      }
      const deps = [...covered, ...fresh];
      const envNames = Object.fromEntries(Object.entries(process.env).map(([k, v]) => [k, createHash('sha256').update(`${k}=${v}`).digest('hex').slice(0, 16)]));
      const json = Buffer.from(JSON.stringify({ module: moduleHash, env: envHash, envNames, pages, runs, globals, deps }), 'utf8');
      const head = Buffer.alloc(12);
      head.write(MAGIC, 0, 'latin1'); head.writeUInt32LE(json.length, 8);
      const tmp = `${file}.${process.pid}.tmp`;
      try {
        fs.mkdirSync(path.dirname(path.resolve(file)), { recursive: true });
        const out = fs.openSync(tmp, 'w');
        fs.writeSync(out, head); fs.writeSync(out, json);
        for (const [start, count] of runs)
          for (let done = 0; done < count * PAGE; ) {
            const n = Math.min(64 * PAGE, count * PAGE - done);
            // (through a copy: the memory is shared, which the file system does not take)
            fs.writeSync(out, Buffer.from(u8.slice(start * PAGE + done, start * PAGE + done + n)));
            done += n;
          }
        fs.closeSync(out);
        fs.renameSync(tmp, file);
        why(`written: ${pages} pages (${runs.reduce((s, r) => s + r[1], 0)} not zero), ${deps.length} paths (${fresh.length} new) in ${(performance.now() - t0).toFixed(0)} ms`);
      } catch (e) {
        try { fs.unlinkSync(tmp); } catch { /* */ }
        why(`not written: ${e.message}`);
      }
    },
  };
}
