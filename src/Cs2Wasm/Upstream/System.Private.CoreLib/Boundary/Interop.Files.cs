// cs2wasm: System.Native's file system (SPEC section 8: every module gets the platform .NET's browser target gets,
// files over the host's file system). .NET's browser runtime runs System.Native's C over Emscripten's file system; here
// the host serves it through imports, with POSIX meaning. Paths go as UTF-16 (address, length in chars); a failure
// comes back as a negative PAL error code (Interop.Error), which becomes the last platform invoke error, as the
// library's generated marshalling would set it. Descriptors 0-2 are the console.
using System;
using System.Runtime.InteropServices;
using Cs2Wasm.Runtime;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static unsafe partial class Sys
    {
        static class Fs
        {
            [Import("cs2wasm", "fs_open")] public static extern int Open(char* path, int len, int flags, int mode);
            [Import("cs2wasm", "fs_close")] public static extern int Close(int fd);
            [Import("cs2wasm", "fs_read")] public static extern int Read(int fd, byte* buffer, int count);
            [Import("cs2wasm", "fs_pread")] public static extern int PRead(int fd, byte* buffer, int count, long offset);
            [Import("cs2wasm", "fs_write")] public static extern int Write(int fd, byte* buffer, int count);
            [Import("cs2wasm", "fs_pwrite")] public static extern int PWrite(int fd, byte* buffer, int count, long offset);
            [Import("cs2wasm", "fs_lseek")] public static extern long LSeek(int fd, long offset, int whence);
            /// <summary>Into out: 16 longs (mode, size, uid, gid, atime s/ns, mtime s/ns, ctime s/ns, birth s/ns, dev, rdev, ino, nlink).</summary>
            [Import("cs2wasm", "fs_fstat")] public static extern int FStat(int fd, long* stat);
            [Import("cs2wasm", "fs_stat")] public static extern int Stat(char* path, int len, int follow, long* stat);
            [Import("cs2wasm", "fs_ftruncate")] public static extern int FTruncate(int fd, long length);
            [Import("cs2wasm", "fs_fsync")] public static extern int FSync(int fd);
            [Import("cs2wasm", "fs_access")] public static extern int Access(char* path, int len, int mode);
            [Import("cs2wasm", "fs_unlink")] public static extern int Unlink(char* path, int len);
            [Import("cs2wasm", "fs_mkdir")] public static extern int MkDir(char* path, int len, int mode);
            [Import("cs2wasm", "fs_rmdir")] public static extern int RmDir(char* path, int len);
            [Import("cs2wasm", "fs_rename")] public static extern int Rename(char* from, int fromLen, char* to, int toLen);
            [Import("cs2wasm", "fs_link")] public static extern int Link(char* from, int fromLen, char* to, int toLen);
            [Import("cs2wasm", "fs_symlink")] public static extern int SymLink(char* target, int targetLen, char* link, int linkLen);
            /// <summary>The link's target as UTF-8 into the buffer: its length in bytes (all of it, even past the buffer).</summary>
            [Import("cs2wasm", "fs_readlink")] public static extern int ReadLink(char* path, int len, byte* buffer, int size);
            [Import("cs2wasm", "fs_opendir")] public static extern int OpenDir(char* path, int len);
            /// <summary>The next entry's name as UTF-8 into the buffer: its length (0 at the end), the node type into *type.</summary>
            [Import("cs2wasm", "fs_readdir")] public static extern int ReadDir(int dir, byte* name, int size, int* type);
            [Import("cs2wasm", "fs_closedir")] public static extern int CloseDir(int dir);
            /// <summary>The working directory as UTF-8 into the buffer: its length in bytes.</summary>
            [Import("cs2wasm", "fs_getcwd")] public static extern int GetCwd(byte* buffer, int size);
            [Import("cs2wasm", "fs_chdir")] public static extern int ChDir(char* path, int len);
            [Import("cs2wasm", "fs_chmod")] public static extern int ChMod(char* path, int len, int mode);
            [Import("cs2wasm", "fs_fchmod")] public static extern int FChMod(int fd, int mode);
            /// <summary>Access and modification times (seconds and nanoseconds each; null: now).</summary>
            [Import("cs2wasm", "fs_utimens")] public static extern int UTimens(char* path, int len, long* times);
            [Import("cs2wasm", "fs_futimens")] public static extern int FUTimens(int fd, long* times);
        }

        /// <summary>A host result: negative is a PAL error code, kept as the last error; -1 is returned for it.</summary>
        static int R(int r)
        {
            if (r >= 0) return r;
            Marshal.SetLastPInvokeError(-r);
            return -1;
        }

        static long R(long r)
        {
            if (r >= 0) return r;
            Marshal.SetLastPInvokeError((int)-r);
            return -1;
        }

        static int Fd(SafeHandle h) => (int)h.DangerousGetHandle();

        // ---------------- descriptors ----------------
        internal static partial SafeFileHandle Open(string filename, OpenFlags flags, int mode)
        {
            int fd;
            fixed (char* p = filename) fd = R(Fs.Open(p, filename.Length, (int)flags, mode));
            var h = new SafeFileHandle();
            Marshal.InitHandle(h, fd);
            return h;
        }

        internal static partial int Close(IntPtr fd) => (int)fd <= 2 ? 0 : R(Fs.Close((int)fd));

        internal static unsafe partial int Read(SafeHandle fd, byte* buffer, int count) => R(Fs.Read(Fd(fd), buffer, count));

        internal static unsafe partial int PRead(SafeHandle fd, byte* buffer, int bufferSize, long fileOffset) => R(Fs.PRead(Fd(fd), buffer, bufferSize, fileOffset));

        internal static unsafe partial int ReadFromNonblocking(SafeHandle fd, byte* buffer, int count) => R(Fs.Read(Fd(fd), buffer, count));

        internal static unsafe partial int Write(SafeHandle fd, byte* buffer, int bufferSize) => Write(fd.DangerousGetHandle(), buffer, bufferSize);

        /// <summary>Standard output and error go to the engine's log import (SPEC 7.4), as UTF-16; other descriptors are
        /// the host's files.</summary>
        internal static unsafe partial int Write(IntPtr fd, byte* buffer, int bufferSize)
        {
            if ((int)fd > 2) return R(Fs.Write((int)fd, buffer, bufferSize));
            if (bufferSize <= 0) return 0;
            char[] text = new char[bufferSize];
            int n = System.Text.Encoding.UTF8.GetChars(buffer, bufferSize, (char*)Rt.ArrayData(text), bufferSize);
            Engine.Write(Rt.ArrayData(text), n);
            return bufferSize;
        }

        internal static unsafe partial int WriteToNonblocking(SafeHandle fd, byte* buffer, int bufferSize) => Write(fd, buffer, bufferSize);

        internal static unsafe partial int PWrite(SafeHandle fd, byte* buffer, int bufferSize, long fileOffset) => R(Fs.PWrite(Fd(fd), buffer, bufferSize, fileOffset));

        // scatter and gather: the vectors in order, as readv/writev, stopping at a short transfer
        internal static unsafe partial long ReadV(SafeHandle fd, IOVector* vectors, int vectorCount) => Vectors(fd, vectors, vectorCount, -1, write: false);
        internal static unsafe partial long PReadV(SafeHandle fd, IOVector* vectors, int vectorCount, long fileOffset) => Vectors(fd, vectors, vectorCount, fileOffset, write: false);
        internal static unsafe partial long WriteV(SafeHandle fd, IOVector* vectors, int vectorCount) => Vectors(fd, vectors, vectorCount, -1, write: true);
        internal static unsafe partial long PWriteV(SafeHandle fd, IOVector* vectors, int vectorCount, long fileOffset) => Vectors(fd, vectors, vectorCount, fileOffset, write: true);

        static long Vectors(SafeHandle fd, IOVector* vectors, int count, long offset, bool write)
        {
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                int len = (int)vectors[i].Count;
                int r = offset < 0
                    ? (write ? Fs.Write(Fd(fd), vectors[i].Base, len) : Fs.Read(Fd(fd), vectors[i].Base, len))
                    : (write ? Fs.PWrite(Fd(fd), vectors[i].Base, len, offset + total) : Fs.PRead(Fd(fd), vectors[i].Base, len, offset + total));
                if (r < 0) return total > 0 ? total : R(r);
                total += r;
                if (r < len) break;
            }
            return total;
        }

        internal static partial long LSeek(SafeFileHandle fd, long offset, SeekWhence whence) => R(Fs.LSeek(Fd(fd), offset, (int)whence));

        internal static partial int FTruncate(SafeFileHandle fd, long length) => R(Fs.FTruncate(Fd(fd), length));

        internal static partial int FSync(SafeFileHandle fd) => R(Fs.FSync(Fd(fd)));

        /// <summary>Emscripten's file system has no preallocation (posix_fallocate reports it unsupported).</summary>
        internal static partial int FAllocate(SafeFileHandle fd, long offset, long length) { Marshal.SetLastPInvokeError((int)Error.EOPNOTSUPP); return -1; }

        internal static partial int PosixFAdvise(SafeFileHandle fd, long offset, long length, FileAdvice advice) => 0;

        internal static partial bool FileSystemSupportsLocking(SafeFileHandle fd, LockOperations lockOperation, bool accessWrite) => true;

        internal static partial class Fcntl
        {
            internal static partial int GetFD(SafeHandle fd) => 0;

            internal static partial int SetFD(SafeHandle fd, int flags) => 0;

            internal static partial int GetIsNonBlocking(SafeHandle fd, out bool isNonBlocking) { isNonBlocking = false; return 0; }
        }

        /// <summary>A copy of a whole file, by reading and writing (System.Native's fallback without copy_file_range).</summary>
        internal static partial int CopyFile(SafeFileHandle source, SafeFileHandle destination, long sourceLength)
        {
            byte[] buffer = new byte[64 * 1024];
            byte* b = (byte*)Rt.ArrayData(buffer);
            while (true)
            {
                int n = Fs.Read(Fd(source), b, buffer.Length);
                if (n < 0) return R(n);
                if (n == 0) return 0;
                for (int done = 0; done < n;)
                {
                    int w = Fs.Write(Fd(destination), b + done, n - done);
                    if (w < 0) return R(w);
                    done += w;
                }
            }
        }

        // ---------------- status ----------------
        static void FromStat(long* s, out FileStatus o)
        {
            o = default;
            o.Mode = (int)s[0];
            o.Size = s[1];
            o.Uid = (uint)s[2];
            o.Gid = (uint)s[3];
            o.ATime = s[4]; o.ATimeNsec = s[5];
            o.MTime = s[6]; o.MTimeNsec = s[7];
            o.CTime = s[8]; o.CTimeNsec = s[9];
            o.BirthTime = s[10]; o.BirthTimeNsec = s[11];
            o.Flags = s[10] != 0 || s[11] != 0 ? FileStatusFlags.HasBirthTime : FileStatusFlags.None;
            o.Dev = s[12]; o.RDev = s[13]; o.Ino = s[14];
            o.HardLinkCount = (int)s[15];
        }

        internal static partial int FStat(SafeHandle fd, out FileStatus output)
        {
            long* s = stackalloc long[16];
            int r = R(Fs.FStat(Fd(fd), s));
            FromStat(s, out output);
            return r;
        }

        static int StatCore(ReadOnlySpan<char> path, bool follow, out FileStatus output)
        {
            long* s = stackalloc long[16];
            int r;
            fixed (char* p = path) r = R(Fs.Stat(p, path.Length, follow ? 1 : 0, s));
            FromStat(s, out output);
            return r;
        }

        internal static partial int Stat(string path, out FileStatus output) => StatCore(path, true, out output);
        internal static partial int Stat(ReadOnlySpan<char> path, out FileStatus output) => StatCore(path, true, out output);
        internal static partial int LStat(string path, out FileStatus output) => StatCore(path, false, out output);
        internal static partial int LStat(ReadOnlySpan<char> path, out FileStatus output) => StatCore(path, false, out output);

        internal static partial int Access(string path, AccessMode mode) { fixed (char* p = path) return R(Fs.Access(p, path.Length, (int)mode)); }

        // ---------------- names ----------------
        internal static partial int Unlink(string pathname) { fixed (char* p = pathname) return R(Fs.Unlink(p, pathname.Length)); }

        internal static partial int MkDir(ReadOnlySpan<char> path, int mode) { fixed (char* p = path) return R(Fs.MkDir(p, path.Length, mode)); }

        internal static partial int RmDir(string path) { fixed (char* p = path) return R(Fs.RmDir(p, path.Length)); }

        internal static partial int Rename(ReadOnlySpan<char> oldPath, ReadOnlySpan<char> newPath)
        {
            fixed (char* a = oldPath) fixed (char* b = newPath) return R(Fs.Rename(a, oldPath.Length, b, newPath.Length));
        }

        internal static partial int Link(string source, string link)
        {
            fixed (char* a = source) fixed (char* b = link) return R(Fs.Link(a, source.Length, b, link.Length));
        }

        internal static partial int SymLink(string target, string linkPath)
        {
            fixed (char* a = target) fixed (char* b = linkPath) return R(Fs.SymLink(a, target.Length, b, linkPath.Length));
        }

        private static partial int ReadLink(ReadOnlySpan<char> path, ref byte buffer, int bufferSize)
        {
            fixed (char* p = path) fixed (byte* b = &buffer)
            {
                int n = R(Fs.ReadLink(p, path.Length, b, bufferSize));
                // (readlink fills at most the buffer: the caller grows it when the result fills it)
                return n < 0 ? n : Math.Min(n, bufferSize);
            }
        }

        internal static partial int ChMod(string path, int mode) { fixed (char* p = path) return R(Fs.ChMod(p, path.Length, mode)); }

        internal static partial int FChMod(SafeFileHandle fd, int mode) => R(Fs.FChMod(Fd(fd), mode));

        internal static unsafe partial int UTimensat(string path, TimeSpec* times) { fixed (char* p = path) return R(Fs.UTimens(p, path.Length, (long*)times)); }

        internal static unsafe partial int FUTimens(SafeHandle fd, TimeSpec* times) => R(Fs.FUTimens(Fd(fd), (long*)times));

        /// <summary>Emscripten's file system keeps no BSD flags: setting none succeeds, others are unsupported.</summary>
        internal static partial int LChflags(string path, uint flags) { if (flags == 0) return 0; Marshal.SetLastPInvokeError((int)Error.ENOTSUP); return -1; }

        internal static partial int FChflags(SafeHandle fd, uint flags) { if (flags == 0) return 0; Marshal.SetLastPInvokeError((int)Error.ENOTSUP); return -1; }

        // ---------------- directories ----------------
        /// <summary>An open directory: the host's handle, and the buffer its current entry's name is in (valid until
        /// the next ReadDir, as readdir's).</summary>
        sealed class Dir
        {
            public int Handle;
            public byte[] Name = new byte[1024];
        }
        static readonly System.Collections.Generic.Dictionary<nint, Dir> s_dirs = [];
        static nint s_nextDir = 1;

        internal static partial nint OpenDir(string path)
        {
            int h;
            fixed (char* p = path) h = R(Fs.OpenDir(p, path.Length));
            if (h < 0) return 0;
            lock (s_dirs)
            {
                nint id = s_nextDir++;
                s_dirs[id] = new Dir { Handle = h };
                return id;
            }
        }

        internal static unsafe partial int ReadDir(nint dir, DirectoryEntry* outputEntry)
        {
            Dir d;
            lock (s_dirs) d = s_dirs[dir];
            int type;
            int n = Fs.ReadDir(d.Handle, (byte*)Rt.ArrayData(d.Name), d.Name.Length, &type);
            if (n > d.Name.Length)
            {
                d.Name = new byte[n];
                n = Fs.ReadDir(d.Handle, (byte*)Rt.ArrayData(d.Name), d.Name.Length, &type);
            }
            if (n < 0) return R(n);
            // (0: the end, as readdir's -1 with no error)
            if (n == 0) return -1;
            outputEntry->Name = (byte*)Rt.ArrayData(d.Name);
            outputEntry->NameLength = n;
            outputEntry->InodeType = (NodeType)type;
            return 0;
        }

        internal static partial int CloseDir(nint dir)
        {
            Dir? d;
            lock (s_dirs) { s_dirs.Remove(dir, out d); }
            return d == null ? 0 : R(Fs.CloseDir(d.Handle));
        }

        // ---------------- the working directory ----------------
        private static unsafe partial byte* GetCwd(byte* buffer, int bufferLength)
        {
            int n = Fs.GetCwd(buffer, bufferLength);
            if (n < 0) { R(n); return null; }
            if (n >= bufferLength) { Marshal.SetLastPInvokeError((int)Error.ERANGE); return null; }
            buffer[n] = 0;
            return buffer;
        }

        internal static partial int ChDir(string path) { fixed (char* p = path) return R(Fs.ChDir(p, path.Length)); }
    }
}
