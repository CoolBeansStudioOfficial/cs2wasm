// il2wasm: libSystem.Native's entry points that the stock browser-wasm libraries above CoreLib call (docs/CORELIB.md
// section 5; the binding is natives.txt beside the manifest). Each is the function of System.Native's C as the browser
// runtime builds it with Emscripten (src/native/libs/System.Native/pal_io.c, pal_errno.c, pal_random.c at v11.0.0):
// the same results and the same errno (Emscripten's numbers, Errno.g.cs), with the host's imports where Emscripten
// has its file system and console. The signatures are the native ones: a pointer or intptr_t is an int (wasm32),
// an enum its underlying type.
using System;
using System.Runtime.InteropServices;

namespace Il2Wasm.Runtime.Natives
{
    internal static unsafe class SystemNative
    {
        static void SetErrno(int platformErrno) => Interop.Sys.SetErrNo(platformErrno);

        /// <summary>A host result: negative is a PAL error code, which becomes errno; -1 is returned for it.</summary>
        static int R(int r)
        {
            if (r >= 0) return r;
            SetErrno(Errno.PalToPlatform(-r));
            return -1;
        }

        // ---------------- pal_errno.c ----------------
        public static int SystemNative_ConvertErrorPlatformToPal(int platformErrno) => Errno.PlatformToPal(platformErrno);

        public static int SystemNative_ConvertErrorPalToPlatform(int error) => Errno.PalToPlatform(error);

        /// <summary>pal_error_common.h's StrErrorR over musl's XSI strerror_r: the message into the buffer (cut to fit
        /// and null-terminated); the buffer, or null when the message did not fit (ERANGE).</summary>
        public static byte* SystemNative_StrErrorR(int platformErrno, byte* buffer, int bufferSize)
        {
            if (bufferSize < 0) return null;
            string msg;
            bool exact = false;
            if (platformErrno < 0 && platformErrno == Errno.EHOSTNOTFOUND) { msg = "Name does not resolve"; exact = true; }   // gai_strerror(EAI_NONAME)
            else if (platformErrno < 0 && platformErrno == Errno.ESOCKETERROR) { msg = "Unknown socket error"; exact = true; }
            else msg = Errno.Message(platformErrno);
            // (SafeStringCopy for the two above: cut to fit, never ERANGE)
            if (msg.Length >= bufferSize)
            {
                if (bufferSize > 0)
                {
                    for (int i = 0; i < bufferSize - 1; i++) buffer[i] = (byte)msg[i];
                    buffer[bufferSize - 1] = 0;
                }
                return exact ? buffer : null;
            }
            for (int i = 0; i < msg.Length; i++) buffer[i] = (byte)msg[i];
            buffer[msg.Length] = 0;
            return buffer;
        }

        // ---------------- pal_io.c: descriptors ----------------
        /// <summary>write(2). Descriptors 0 to 2 are the console: their UTF-8 goes to the engine's log import as UTF-16
        /// (as Emscripten's TTY sends stdout and stderr to the console); others are the host's files.</summary>
        public static int SystemNative_Write(int fd, byte* buffer, int bufferSize)
        {
            if (fd < 0) { SetErrno(Errno.EBADF); return -1; }
            if (fd > 2) return R(Interop.Sys.Fs.Write(fd, buffer, bufferSize));
            if (bufferSize <= 0) return 0;
            char[] text = new char[bufferSize];
            int n = System.Text.Encoding.UTF8.GetChars(buffer, bufferSize, (char*)Rt.ArrayData(text), bufferSize);
            Engine.Write(Rt.ArrayData(text), n);
            return bufferSize;
        }

        public static int SystemNative_FTruncate(int fd, long length) => R(Interop.Sys.Fs.FTruncate(fd, length));

        /// <summary>fcntl(F_SETFD): Emscripten accepts it and keeps nothing (close-on-exec means nothing in one
        /// process); a descriptor it does not have is EBADF.</summary>
        public static int SystemNative_FcntlSetFD(int fd, int flags)
        {
            if (fd > 2)
            {
                long* s = stackalloc long[16];
                if (Interop.Sys.Fs.FStat(fd, s) is var r && r < 0) return R(r);
            }
            else if (fd < 0) { SetErrno(Errno.EBADF); return -1; }
            return 0;
        }

        public static int SystemNative_FStat(int fd, Interop.Sys.FileStatus* output)
        {
            long* s = stackalloc long[16];
            int r = R(Interop.Sys.Fs.FStat(fd, s));
            if (r == 0) Interop.Sys.FromStat(s, out *output);
            return r;
        }

        public static int SystemNative_Stat(byte* path, Interop.Sys.FileStatus* output)
        {
            long* s = stackalloc long[16];
            string p = Utf8(path);
            int r;
            fixed (char* c = p) r = R(Interop.Sys.Fs.Stat(c, p.Length, 1, s));
            if (r == 0) Interop.Sys.FromStat(s, out *output);
            return r;
        }

        public static int SystemNative_Unlink(byte* path)
        {
            string p = Utf8(path);
            fixed (char* c = p) return R(Interop.Sys.Fs.Unlink(c, p.Length));
        }

        static string Utf8(byte* s)
        {
            int n = 0;
            while (s[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(s, n);
        }

        // ---------------- pal_random.c ----------------
        /// <summary>The host's randomness (SPEC 7, 9), as CoreLib's own; 0 for success.</summary>
        public static int SystemNative_GetCryptographicallySecureRandomBytes(byte* buffer, int bufferLength) =>
            Interop.Sys.GetCryptographicallySecureRandomBytes(buffer, bufferLength);

        // ---------------- pal_io.c: memory maps, as Emscripten's mmap (system/lib/libc/emscripten_mmap.c) ----------------
        const int PalProtRead = 1, PalProtWrite = 2, PalProtExec = 4;
        const int PalMapShared = 1, PalMapPrivate = 2, PalMapAnonymous = 0x10;
        const int PalMsAsync = 1, PalMsSync = 2, PalMsInvalidate = 0x10;
        const int PalMadvDontFork = 1;
        const int PalScClkTck = 1, PalScPageSize = 2;
        /// <summary>Emscripten's page (sysconf(_SC_PAGESIZE)) and the alignment of its anonymous maps: a WASM page.</summary>
        const int WasmPage = 65536;

        sealed class Map
        {
            public long Length;
            public int Fd, Flags, Prot;
            public long Offset;
        }
        static readonly System.Collections.Generic.Dictionary<int, Map> s_maps = [];

        public static int SystemNative_MMap(int address, ulong length, int protection, int flags, int fd, long offset)
        {
            if (length > uint.MaxValue) { SetErrno(Errno.ERANGE); return 0; }
            if ((protection & ~(PalProtRead | PalProtWrite | PalProtExec)) != 0 || (flags & ~(PalMapShared | PalMapPrivate | PalMapAnonymous)) != 0)
            { SetErrno(Errno.EINVAL); return 0; }
            // (musl's mmap: an offset not a multiple of its unit is EINVAL, a length from PTRDIFF_MAX up ENOMEM)
            if ((offset & 4095) != 0) { SetErrno(Errno.EINVAL); return 0; }
            if (length >= int.MaxValue) { SetErrno(Errno.ENOMEM); return 0; }
            // (Emscripten maps nothing at a requested address)
            if (address != 0) { SetErrno(Errno.EINVAL); return 0; }
            int len = (int)length;
            byte* p;
            if ((flags & PalMapAnonymous) != 0)
            {
                p = (byte*)NativeMemory.AlignedAlloc((nuint)((len + 15) & ~15), WasmPage);
                if (p == null) { SetErrno(Errno.ENOMEM); return 0; }
                NativeMemory.Clear(p, (nuint)len);
                fd = -1;
            }
            else
            {
                // FS.mmap: the file's bytes from the offset (zeros past its end) in memory of its own
                // (a descriptor it does not have: EBADF; the console's has no mmap: ENODEV; a length of 0: EINVAL.
                // Not checked here, as the host does not say how a descriptor was opened: FS.mmap's EACCES for a
                // shared writable map of a file not opened for reading and writing)
                long* s = stackalloc long[16];
                if (fd < 0 || fd > 2 && Interop.Sys.Fs.FStat(fd, s) < 0) { SetErrno(Errno.EBADF); return 0; }
                if (fd <= 2) { SetErrno(Errno.ENODEV); return 0; }
                if (len == 0) { SetErrno(Errno.EINVAL); return 0; }
                p = (byte*)NativeMemory.AlignedAlloc((nuint)len, WasmPage);
                if (p == null) { SetErrno(Errno.ENOMEM); return 0; }
                NativeMemory.Clear(p, (nuint)len);
                long done = 0;
                while (done < len)
                {
                    int n = Interop.Sys.Fs.PRead(fd, p + done, (int)Math.Min(len - done, 1 << 20), offset + done);
                    if (n < 0) { NativeMemory.AlignedFree(p); R(n); return 0; }
                    if (n == 0) break;
                    done += n;
                }
            }
            lock (s_maps) s_maps[(int)p] = new Map { Length = len, Fd = fd, Flags = flags, Prot = protection, Offset = offset };
            return (int)p;
        }

        /// <summary>doMsync: a shared map's bytes back to its file (a private one keeps them).</summary>
        static void WriteBack(int address, Map m, long length)
        {
            if ((m.Flags & PalMapPrivate) != 0 || m.Fd < 0) return;
            long n = Math.Min(length, m.Length), done = 0;
            while (done < n)
            {
                int w = Interop.Sys.Fs.PWrite(m.Fd, (byte*)address + done, (int)Math.Min(n - done, 1 << 20), m.Offset + done);
                if (w <= 0) break;
                done += w;
            }
        }

        public static int SystemNative_MUnmap(int address, ulong length)
        {
            if (length > uint.MaxValue) { SetErrno(Errno.ERANGE); return -1; }
            Map? m;
            lock (s_maps)
            {
                // (only a whole map, by its start and length)
                if (!s_maps.TryGetValue(address, out m) || length == 0 || (ulong)m.Length != length) { SetErrno(Errno.EINVAL); return -1; }
                s_maps.Remove(address);
            }
            if ((m.Flags & PalMapAnonymous) == 0 && (m.Prot & PalProtWrite) != 0) WriteBack(address, m, (long)length);
            NativeMemory.AlignedFree((void*)address);
            return 0;
        }

        public static int SystemNative_MSync(int address, ulong length, int flags)
        {
            if (length > uint.MaxValue) { SetErrno(Errno.ERANGE); return -1; }
            if ((flags & ~(PalMsSync | PalMsAsync | PalMsInvalidate)) != 0) { SetErrno(Errno.EINVAL); return -1; }
            Map? m;
            lock (s_maps) s_maps.TryGetValue(address, out m);
            if (m == null) { SetErrno(Errno.EINVAL); return -1; }
            if ((m.Flags & PalMapAnonymous) == 0) WriteBack(address, m, (long)length);
            return 0;
        }

        public static int SystemNative_MAdvise(int address, ulong length, int advice)
        {
            if (length > uint.MaxValue) { SetErrno(Errno.ERANGE); return -1; }
            // (MADV_DONTFORK is not on WASM targets)
            SetErrno(advice == PalMadvDontFork ? Errno.ENOTSUP : Errno.EINVAL);
            return -1;
        }

        public static long SystemNative_SysConf(int name)
        {
            if (name == PalScClkTck) return 100;
            if (name == PalScPageSize) return WasmPage;
            SetErrno(Errno.EINVAL);
            return -1;
        }

        // ---------------- pal_io.c: what Emscripten does not have ----------------
        /// <summary>No shm_open that works with mmap: ENOTSUP.</summary>
        public static int SystemNative_ShmOpen(byte* name, int flags, int mode) { SetErrno(Errno.ENOTSUP); return -1; }

        public static int SystemNative_ShmUnlink(byte* name) { SetErrno(Errno.ENOTSUP); return -1; }

        /// <summary>No memfd_create: ENOTSUP.</summary>
        public static int SystemNative_MemfdCreate(byte* name, int isReadonly) { SetErrno(Errno.ENOTSUP); return -1; }

        public static int SystemNative_IsMemfdSupported() { SetErrno(Errno.ENOTSUP); return 0; }
    }
}
