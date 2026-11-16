// The host's file system for a module (SPEC section 8: every module gets files over its host's file system): the
// cs2wasm.fs_* imports System.Native's file functions call (src/Cs2Wasm/Upstream/System.Private.CoreLib/Boundary/
// Interop.Files.cs), with POSIX meaning. The module sees a directory of this machine as "/" (CS2WASM_FS_ROOT, else
// the current directory); its working directory starts at "/", as in .NET's browser target. Failures are negative
// PAL error codes (Interop.Error).
import * as fs from 'node:fs';
import * as path from 'node:path';

const E = { EACCES: 0x10002, EBADF: 0x10008, EBUSY: 0x1000A, EEXIST: 0x10014, EINVAL: 0x1001C, EIO: 0x1001D, EISDIR: 0x1001F,
  ELOOP: 0x10020, EMFILE: 0x10021, ENAMETOOLONG: 0x10025, ENFILE: 0x10029, ENOENT: 0x1002D, ENOSPC: 0x10034, ENOSYS: 0x10037,
  ENOTDIR: 0x10039, ENOTEMPTY: 0x1003A, ENOTSUP: 0x1003D, EPERM: 0x10042, EROFS: 0x10048, ESPIPE: 0x10049, EXDEV: 0x1004F };
const err = (e) => -(E[e?.code] ?? E.EIO);

/** The file system's process-wide state (descriptor positions, the working directory), shared with worker threads
 * (their instances are threads of one process, as on POSIX): made once, handed to workers by thread_spawn. */
export function fsShared() {
  return globalThis.__cs2wasmFs ??= { pos: new SharedArrayBuffer(8 * 4096), cwd: new SharedArrayBuffer(4 + 4096) };
}

export function fsImports(getMemory, root = process.env.CS2WASM_FS_ROOT ?? process.cwd()) {
  const shared = fsShared();
  const posArr = new BigInt64Array(shared.pos);
  const cwdLen = new Int32Array(shared.cwd, 0, 1), cwdBytes = new Uint8Array(shared.cwd, 4);
  const mem = () => getMemory().buffer;
  const u8 = () => new Uint8Array(mem());
  const dv = () => new DataView(mem());
  const getCwd = () => cwdLen[0] === 0 ? '/' : Buffer.from(cwdBytes.slice(0, cwdLen[0])).toString('utf8');
  const setCwd = (v) => { const b = Buffer.from(v, 'utf8'); cwdBytes.set(b.subarray(0, 4096)); cwdLen[0] = Math.min(b.length, 4096); };
  const str = (p, len) => { let s = ''; const m = dv(); for (let i = 0; i < len; i++) s += String.fromCharCode(m.getUint16(p + 2 * i, true)); return s; };
  // a module path ("/a/b", or relative to its working directory) as a path of this machine, inside the root
  const real = (p, len) => {
    const v = path.posix.resolve(getCwd(), str(p, len));
    return path.join(root, ...v.split('/').filter(Boolean));
  };
  const virt = (r) => '/' + path.relative(root, r).split(path.sep).filter(Boolean).join('/');
  // reads and writes through a copy (the module's memory may be shared, which the file system does not take)
  const readInto = (fd, p, n, pos) => { const b = Buffer.alloc(n); const r = fs.readSync(fd, b, 0, n, pos); u8().set(b.subarray(0, r), p); return r; };
  const writeFrom = (fd, p, n, pos) => fs.writeSync(fd, Buffer.from(u8().slice(p, p + n)), 0, n, pos);
  const utf8Into = (s, p, size) => { const b = Buffer.from(s, 'utf8'); u8().set(b.subarray(0, Math.min(size, b.length)), p); return b.length; };
  const statInto = (st, out) => {
    const m = dv();
    const ns = (v) => v % 1000000000n, sec = (v) => v / 1000000000n;
    const vals = [BigInt(st.mode), st.size, BigInt(st.uid), BigInt(st.gid), sec(st.atimeNs), ns(st.atimeNs), sec(st.mtimeNs), ns(st.mtimeNs),
      sec(st.ctimeNs), ns(st.ctimeNs), sec(st.birthtimeNs), ns(st.birthtimeNs), BigInt(st.dev), BigInt(st.rdev), BigInt(st.ino), BigInt(st.nlink)];
    vals.forEach((v, i) => m.setBigInt64(out + 8 * i, BigInt.asIntN(64, BigInt(v)), true));
    return 0;
  };
  const flagsOf = (f) => {
    const c = fs.constants;
    let r = [c.O_RDONLY, c.O_WRONLY, c.O_RDWR][f & 3];
    if (f & 0x20) r |= c.O_CREAT;
    if (f & 0x40) r |= c.O_EXCL;
    if (f & 0x80) r |= c.O_TRUNC;
    if (f & 0x100 && c.O_SYNC) r |= c.O_SYNC;
    if (f & 0x200 && c.O_NOFOLLOW) r |= c.O_NOFOLLOW;
    return r;
  };
  const timesOf = (p) => { if (p === 0) { const n = Date.now() / 1000; return [n, n]; } const m = dv(); return [Number(m.getBigInt64(p, true)) + Number(m.getBigInt64(p + 8, true)) / 1e9, Number(m.getBigInt64(p + 16, true)) + Number(m.getBigInt64(p + 24, true)) / 1e9]; };
  const dirs = new Map(); let nextDir = 1;
  // each descriptor's file position (Node keeps none it reports), as the kernel keeps it, for every thread
  const pos = { set: (fd, v) => { posArr[fd & 4095] = BigInt(v); }, delete: (fd) => { posArr[fd & 4095] = 0n; } };
  const at = (fd) => Number(posArr[fd & 4095]);
  const pending = new Map(); // a directory's entry read but not yet taken (its name did not fit)
  const NT = (d) => d.isDirectory() ? 4 : d.isFile() ? 8 : d.isSymbolicLink() ? 10 : d.isFIFO() ? 1 : d.isCharacterDevice() ? 2 : d.isBlockDevice() ? 6 : d.isSocket() ? 12 : 0;
  const tryDo = (f) => { try { return f(); } catch (e) { return err(e); } };
  return {
    fs_open: (p, len, flags, mode) => tryDo(() => {
      const r = real(p, len);
      const fd = (flags & 3) === 0 && !(flags & 0x20) && fs.existsSync(r) && fs.statSync(r).isDirectory() ? fs.openSync(r, 'r') : fs.openSync(r, flagsOf(flags), mode);
      pos.set(fd, 0);
      return fd;
    }),
    fs_close: (fd) => tryDo(() => { fs.closeSync(fd); pos.delete(fd); return 0; }),
    fs_read: (fd, p, n) => tryDo(() => { const r = readInto(fd, p, n, at(fd)); pos.set(fd, at(fd) + r); return r; }),
    fs_pread: (fd, p, n, off) => tryDo(() => readInto(fd, p, n, Number(off))),
    fs_write: (fd, p, n) => tryDo(() => { const r = writeFrom(fd, p, n, at(fd)); pos.set(fd, at(fd) + r); return r; }),
    fs_pwrite: (fd, p, n, off) => tryDo(() => writeFrom(fd, p, n, Number(off))),
    fs_lseek: (fd, off, whence) => {
      try {
        const o = Number(off);
        const p = whence === 0 ? o : whence === 1 ? at(fd) + o : whence === 2 ? fs.fstatSync(fd).size + o : NaN;
        if (!(p >= 0)) return BigInt(-E.EINVAL);
        pos.set(fd, p);
        return BigInt(p);
      } catch (e) { return BigInt(err(e)); }
    },
    fs_fstat: (fd, out) => tryDo(() => statInto(fs.fstatSync(fd, { bigint: true }), out)),
    fs_stat: (p, len, follow, out) => tryDo(() => statInto((follow ? fs.statSync : fs.lstatSync)(real(p, len), { bigint: true }), out)),
    fs_ftruncate: (fd, n) => tryDo(() => { fs.ftruncateSync(fd, Number(n)); return 0; }),
    fs_fsync: (fd) => tryDo(() => { fs.fsyncSync(fd); return 0; }),
    fs_access: (p, len, mode) => tryDo(() => { fs.accessSync(real(p, len), mode); return 0; }),
    fs_unlink: (p, len) => tryDo(() => { fs.unlinkSync(real(p, len)); return 0; }),
    fs_mkdir: (p, len, mode) => tryDo(() => { fs.mkdirSync(real(p, len), { mode }); return 0; }),
    fs_rmdir: (p, len) => tryDo(() => { fs.rmdirSync(real(p, len)); return 0; }),
    fs_rename: (a, al, b, bl) => tryDo(() => { fs.renameSync(real(a, al), real(b, bl)); return 0; }),
    fs_link: (a, al, b, bl) => tryDo(() => { fs.linkSync(real(a, al), real(b, bl)); return 0; }),
    fs_symlink: (t, tl, l, ll) => tryDo(() => { fs.symlinkSync(str(t, tl), real(l, ll)); return 0; }),
    fs_readlink: (p, len, buf, size) => tryDo(() => utf8Into(fs.readlinkSync(real(p, len)), buf, size)),
    fs_opendir: (p, len) => tryDo(() => { const d = fs.opendirSync(real(p, len)); const h = nextDir++; dirs.set(h, d); return h; }),
    fs_readdir: (h, buf, size, typeOut) => tryDo(() => {
      const d = dirs.get(h);
      if (!d) return -E.EBADF;
      const e = pending.get(h) ?? d.readSync();
      if (!e) return 0;
      const name = Buffer.from(e.name, 'utf8');
      if (name.length > size) { pending.set(h, e); return name.length; }
      pending.delete(h);
      u8().set(name, buf);
      dv().setInt32(typeOut, NT(e), true);
      return name.length;
    }),
    fs_closedir: (h) => tryDo(() => { dirs.get(h)?.closeSync(); dirs.delete(h); pending.delete(h); return 0; }),
    fs_getcwd: (buf, size) => utf8Into(getCwd(), buf, size),
    fs_chdir: (p, len) => tryDo(() => { const r = real(p, len); if (!fs.statSync(r).isDirectory()) return -E.ENOTDIR; setCwd(virt(r)); return 0; }),
    fs_chmod: (p, len, mode) => tryDo(() => { fs.chmodSync(real(p, len), mode); return 0; }),
    fs_fchmod: (fd, mode) => tryDo(() => { fs.fchmodSync(fd, mode); return 0; }),
    fs_utimens: (p, len, t) => tryDo(() => { const [a, m] = timesOf(t); fs.utimesSync(real(p, len), a, m); return 0; }),
    fs_futimens: (fd, t) => tryDo(() => { const [a, m] = timesOf(t); fs.futimesSync(fd, a, m); return 0; }),
  };
}
