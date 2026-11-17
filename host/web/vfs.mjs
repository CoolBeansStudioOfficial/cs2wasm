// A module's file system in a browser (SPEC section 8: every module gets files over its host's file system; here the
// host has none, so it is memory): the cs2wasm.fs_* imports (host/fs.mjs has their Node form), over
//   - a pack: one SharedArrayBuffer of read-only files with an index of paths, the same bytes for every thread of the
//     module (the toolchain's inputs: library sources, reference assemblies, the program's sources);
//   - files written at run time, kept by the thread that wrote them (the engine thread writes the output; a worker
//     thread that tries to write is refused, loudly: nothing here needs it yet).
// A pack file: a 4-byte little-endian length, an index in JSON ({ "files": { "/path": [offset, length] } }, offsets
// from the end of the index), then the files' bytes.
const E = { EACCES: 0x10002, EBADF: 0x10008, EEXIST: 0x10014, EINVAL: 0x1001C, EIO: 0x1001D, EISDIR: 0x1001F, ENOENT: 0x1002D,
  ENOSYS: 0x10037, ENOTDIR: 0x10039, ENOTEMPTY: 0x1003A, EROFS: 0x10048 };

/** Builds a pack from files ([{ path, bytes: Uint8Array }]) as a Uint8Array. */
export function makePack(files) {
  const index = { files: {} };
  let at = 0;
  for (const f of files) { index.files[f.path] = [at, f.bytes.length]; at += f.bytes.length; }
  const head = new TextEncoder().encode(JSON.stringify(index));
  const out = new Uint8Array(4 + head.length + at);
  new DataView(out.buffer).setUint32(0, head.length, true);
  out.set(head, 4);
  let o = 4 + head.length;
  for (const f of files) { out.set(f.bytes, o); o += f.bytes.length; }
  return out;
}

/** Reads a pack's index: { files: Map(path -> [absolute offset, length]), bytes } over the given bytes. */
export function openPack(bytes) {
  const n = new DataView(bytes.buffer, bytes.byteOffset, 4).getUint32(0, true);
  // (TextDecoder does not take a view of shared memory: the index is copied out first)
  const index = JSON.parse(new TextDecoder().decode(bytes.slice(4, 4 + n)));
  const files = new Map();
  for (const [p, [o, l]] of Object.entries(index.files)) files.set(p, [4 + n + o, l]);
  return { files, bytes };
}

/** Several packs and loose files as one pack in shared memory: { buffer: SharedArrayBuffer, index: [[path, offset, length]] }. */
export function sharePacks(packs, extra = []) {
  let size = 0;
  for (const p of packs) for (const [, [, l]] of p.files) size += l;
  for (const f of extra) size += f.bytes.length;
  const buffer = new SharedArrayBuffer(Math.max(size, 1)), all = new Uint8Array(buffer), index = [];
  let at = 0;
  for (const p of packs) for (const [path, [o, l]] of p.files) { all.set(p.bytes.subarray(o, o + l), at); index.push([path, at, l]); at += l; }
  for (const f of extra) { all.set(f.bytes, at); index.push([f.path, at, f.bytes.length]); at += f.bytes.length; }
  return { buffer, index };
}

const normalize = (cwd, p) => {
  const parts = [];
  for (const s of ((p.startsWith('/') ? '' : cwd + '/') + p).split('/')) {
    if (s === '' || s === '.') continue;
    if (s === '..') parts.pop(); else parts.push(s);
  }
  return '/' + parts.join('/');
};

/** The fs_* imports. shared: sharePacks' result; options: { canWrite, fdBase, out: { write, writeError } }.
 * Returns { imports, written: Map(path -> Uint8Array) }. */
export function vfsImports(getMemory, shared, options) {
  const pack = new Uint8Array(shared.buffer);
  const files = new Map(), dirs = new Map([['/', new Map()]]);
  const addDirs = (path) => {
    const parts = path.split('/').filter(Boolean);
    let d = '/';
    for (let i = 0; i < parts.length; i++) {
      const isFile = i === parts.length - 1;
      dirs.get(d).set(parts[i], isFile ? 8 : 4);
      if (isFile) break;
      d = d === '/' ? '/' + parts[i] : d + '/' + parts[i];
      if (!dirs.has(d)) dirs.set(d, new Map());
    }
  };
  for (const [path, o, l] of shared.index) { files.set(path, [o, l]); addDirs(path); }
  const written = new Map();                       // path -> { data: Uint8Array, size }
  const fds = new Map(); let nextFd = options.fdBase ?? 3;
  const open = new Map(); let nextDir = 1;         // directory handles
  let cwd = '/';
  const u8 = () => new Uint8Array(getMemory().buffer);
  const dv = () => new DataView(getMemory().buffer);
  const str = (p, len) => { p >>>= 0; const a = new Uint16Array(getMemory().buffer, p, len); let s = ''; for (let i = 0; i < len; i += 4096) s += String.fromCharCode.apply(null, a.subarray(i, Math.min(len, i + 4096))); return s; };
  const real = (p, len) => normalize(cwd, str(p, len));
  const utf8Into = (s, p, size) => { const b = new TextEncoder().encode(s); u8().set(b.subarray(0, Math.min(size, b.length)), p >>> 0); return b.length; };
  const now = () => BigInt(Math.floor(Date.now() / 1000));
  const statInto = (isDir, size, out) => {
    const m = dv(); out >>>= 0;
    const t = now();
    const vals = [BigInt(isDir ? 0o040755 : 0o100644), BigInt(size), 0n, 0n, t, 0n, t, 0n, t, 0n, t, 0n, 1n, 0n, 1n, 1n];
    vals.forEach((v, i) => m.setBigInt64(out + 8 * i, v, true));
    return 0;
  };
  const sizeOf = (path) => (written.has(path) ? written.get(path).size : files.has(path) ? files.get(path)[1] : -1);
  const refuse = (what, path) => { options.out?.writeError?.(`cs2wasm file system: ${what} ${path} from a worker thread is not supported in this host\n`); return -E.EROFS; };
  const content = (f) => (f.w ? f.w.data.subarray(0, f.w.size) : pack.subarray(f.at, f.at + f.len));
  const readAt = (f, p, n, pos) => {
    const c = content(f), k = Math.max(0, Math.min(n, c.length - pos));
    if (k > 0) u8().set(c.subarray(pos, pos + k), p >>> 0);
    return k;
  };
  const writeAt = (f, p, n, pos) => {
    const w = f.w;
    if (pos + n > w.data.length) { const bigger = new Uint8Array(Math.max(2 * w.data.length, pos + n, 4096)); bigger.set(w.data.subarray(0, w.size)); w.data = bigger; }
    w.data.set(u8().subarray(p >>> 0, (p >>> 0) + n), pos);
    w.size = Math.max(w.size, pos + n);
    return n;
  };
  const console = (fd, p, n) => {
    const text = new TextDecoder().decode(u8().slice(p >>> 0, (p >>> 0) + n));
    if (fd === 2) options.out?.writeError?.(text); else options.out?.write?.(text);
    return n;
  };
  const imports = {
    fs_open: (p, len, flags, mode) => {
      const path = real(p, len);
      const access = flags & 3, create = flags & 0x20, excl = flags & 0x40, trunc = flags & 0x80;
      if (dirs.has(path)) { if (access !== 0) return -E.EISDIR; const fd = nextFd++; fds.set(fd, { dir: true, path, pos: 0 }); return fd; }
      const exists = files.has(path) || written.has(path);
      if (!exists && !create) return -E.ENOENT;
      if (exists && create && excl) return -E.EEXIST;
      const fd = nextFd++;
      if (access === 0 && !create && !trunc) {
        fds.set(fd, written.has(path) ? { w: written.get(path), path, pos: 0 } : { at: files.get(path)[0], len: files.get(path)[1], path, pos: 0 });
        return fd;
      }
      if (!options.canWrite) return refuse('writing', path);
      const parent = path.slice(0, path.lastIndexOf('/')) || '/';
      if (!dirs.has(parent)) return -E.ENOENT;
      let w = written.get(path);
      if (!w) {
        w = { data: new Uint8Array(4096), size: 0 };
        if (files.has(path) && !trunc) { const [o, l] = files.get(path); w.data = pack.slice(o, o + l); w.size = l; }
        written.set(path, w); addDirs(path);
      } else if (trunc) w.size = 0;
      fds.set(fd, { w, path, pos: 0 });
      return fd;
    },
    fs_close: (fd) => (fds.delete(fd) ? 0 : -E.EBADF),
    fs_read: (fd, p, n) => { const f = fds.get(fd); if (!f) return -E.EBADF; if (f.dir) return -E.EISDIR; const r = readAt(f, p, n, f.pos); f.pos += r; return r; },
    fs_pread: (fd, p, n, off) => { const f = fds.get(fd); if (!f) return -E.EBADF; return readAt(f, p, n, Number(off)); },
    fs_write: (fd, p, n) => {
      if (fd === 1 || fd === 2) return console(fd, p, n);
      const f = fds.get(fd); if (!f) return -E.EBADF; if (!f.w) return -E.EBADF;
      const r = writeAt(f, p, n, f.pos); f.pos += r; return r;
    },
    fs_pwrite: (fd, p, n, off) => { const f = fds.get(fd); if (!f || !f.w) return -E.EBADF; return writeAt(f, p, n, Number(off)); },
    fs_lseek: (fd, off, whence) => {
      const f = fds.get(fd); if (!f) return BigInt(-E.EBADF);
      const o = Number(off), size = f.dir ? 0 : content(f).length;
      const pos = whence === 0 ? o : whence === 1 ? f.pos + o : whence === 2 ? size + o : NaN;
      if (!(pos >= 0)) return BigInt(-E.EINVAL);
      f.pos = pos;
      return BigInt(pos);
    },
    fs_fstat: (fd, out) => {
      if (fd >= 0 && fd <= 2) { statInto(false, 0, out); dv().setBigInt64(out >>> 0, BigInt(0o020666), true); return 0; }
      const f = fds.get(fd); if (!f) return -E.EBADF;
      return statInto(!!f.dir, f.dir ? 0 : content(f).length, out);
    },
    fs_stat: (p, len, follow, out) => {
      const path = real(p, len);
      if (dirs.has(path)) return statInto(true, 0, out);
      const size = sizeOf(path);
      return size < 0 ? -E.ENOENT : statInto(false, size, out);
    },
    fs_ftruncate: (fd, n) => { const f = fds.get(fd); if (!f || !f.w) return -E.EBADF; const k = Number(n); if (k > f.w.data.length) { const b = new Uint8Array(k); b.set(f.w.data.subarray(0, f.w.size)); f.w.data = b; } else if (k > f.w.size) f.w.data.fill(0, f.w.size, k); f.w.size = k; return 0; },
    fs_fsync: () => 0,
    fs_access: (p, len, mode) => { const path = real(p, len); return dirs.has(path) || sizeOf(path) >= 0 ? 0 : -E.ENOENT; },
    fs_unlink: (p, len) => {
      const path = real(p, len);
      if (!options.canWrite) return refuse('deleting', path);
      if (!written.has(path)) return files.has(path) ? -E.EROFS : -E.ENOENT;
      written.delete(path);
      if (!files.has(path)) dirs.get(path.slice(0, path.lastIndexOf('/')) || '/')?.delete(path.slice(path.lastIndexOf('/') + 1));
      return 0;
    },
    fs_mkdir: (p, len, mode) => {
      const path = real(p, len);
      if (dirs.has(path) || sizeOf(path) >= 0) return -E.EEXIST;
      const parent = path.slice(0, path.lastIndexOf('/')) || '/';
      if (!dirs.has(parent)) return -E.ENOENT;
      if (!options.canWrite) return refuse('making the directory', path);
      dirs.set(path, new Map()); dirs.get(parent).set(path.slice(path.lastIndexOf('/') + 1), 4);
      return 0;
    },
    fs_opendir: (p, len) => { const path = real(p, len); const d = dirs.get(path); if (!d) return sizeOf(path) >= 0 ? -E.ENOTDIR : -E.ENOENT; const h = nextDir++; open.set(h, { entries: [...d], at: 0 }); return h; },
    fs_readdir: (h, buf, size, typeOut) => {
      const d = open.get(h); if (!d) return -E.EBADF;
      if (d.at >= d.entries.length) return 0;
      const [name, type] = d.entries[d.at];
      const b = new TextEncoder().encode(name);
      if (b.length > size) return b.length;
      d.at++;
      u8().set(b, buf >>> 0);
      dv().setInt32(typeOut >>> 0, type, true);
      return b.length;
    },
    fs_closedir: (h) => { open.delete(h); return 0; },
    fs_getcwd: (buf, size) => utf8Into(cwd, buf, size),
    fs_chdir: (p, len) => { const path = real(p, len); if (!dirs.has(path)) return -E.ENOTDIR; cwd = path; return 0; },
  };
  // the rest of host/fs.mjs's functions (links, modes, times): not in this host
  for (const name of ['fs_rmdir', 'fs_rename', 'fs_link', 'fs_symlink', 'fs_readlink', 'fs_chmod', 'fs_fchmod', 'fs_utimens', 'fs_futimens'])
    imports[name] = () => { options.out?.writeError?.(`cs2wasm file system: ${name} is not supported in this host\n`); return -E.ENOSYS; };
  return { imports, written };
}
