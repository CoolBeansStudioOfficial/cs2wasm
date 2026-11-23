// il2wasm: System.Native's memory mapping (SPEC section 8: the platform .NET's browser target gets). As there
// (Emscripten's mmap over its file system), a mapping is memory of the module's holding the file's bytes, read when it
// is made; a shared, writable mapping writes them back on msync and munmap. Pages are 4 KiB; there is no shared memory
// object (shm_open) or memfd, as on the browser target. Shared into every library that declares these functions
// (tools/CoreLibManifest: Generated/Interop.Boundary.g.cs); helpers come from CoreLib's boundary (Interop.Files.cs).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static unsafe partial class Sys
    {
        const int PageSize = 4096;

        /// <summary>The mappings to write back: address, then length, descriptor and file offset.</summary>
        static readonly Dictionary<nint, (ulong Length, int Fd, long Offset)> s_writeBack = [];

        internal static partial IntPtr MMap(IntPtr addr, ulong len, MemoryMappedProtections prot, MemoryMappedFlags flags, SafeFileHandle fd, long offset)
            => MapCore(len, prot, flags, fd.IsInvalid ? -1 : (int)fd.DangerousGetHandle(), offset);

        internal static partial IntPtr MMap(IntPtr addr, ulong len, MemoryMappedProtections prot, MemoryMappedFlags flags, IntPtr fd, long offset)
            => MapCore(len, prot, flags, (int)fd, offset);

        static IntPtr MapCore(ulong len, MemoryMappedProtections prot, MemoryMappedFlags flags, int fd, long offset)
        {
            if (len == 0 || len > int.MaxValue || offset % PageSize != 0) { Marshal.SetLastPInvokeError((int)ConvertErrorPalToPlatform(Error.EINVAL)); return IntPtr.Zero; }
            byte* p = (byte*)NativeMemory.AlignedAlloc((nuint)len, PageSize);
            if (p == null) { Marshal.SetLastPInvokeError((int)ConvertErrorPalToPlatform(Error.ENOMEM)); return IntPtr.Zero; }
            NativeMemory.Clear(p, (nuint)len);
            if ((flags & MemoryMappedFlags.MAP_ANONYMOUS) == 0)
            {
                // the file's bytes (beyond its end, zeros)
                long done = 0;
                while (done < (long)len)
                {
                    int n = Fs.PRead(fd, p + done, (int)Math.Min((long)len - done, 1 << 20), offset + done);
                    if (n < 0) { NativeMemory.AlignedFree(p); Marshal.SetLastPInvokeError((int)ConvertErrorPalToPlatform((Error)(-n))); return IntPtr.Zero; }
                    if (n == 0) break;
                    done += n;
                }
                if ((flags & MemoryMappedFlags.MAP_SHARED) != 0 && (prot & MemoryMappedProtections.PROT_WRITE) != 0)
                    lock (s_writeBack) s_writeBack[(nint)p] = (len, fd, offset);
            }
            return (IntPtr)p;
        }

        static void WriteBack(nint addr, ulong len)
        {
            (ulong Length, int Fd, long Offset) m;
            lock (s_writeBack) { if (!s_writeBack.TryGetValue(addr, out m)) return; }
            long n = (long)Math.Min(len, m.Length), done = 0;
            while (done < n)
            {
                int w = Fs.PWrite(m.Fd, (byte*)addr + done, (int)Math.Min(n - done, 1 << 20), m.Offset + done);
                if (w <= 0) break;
                done += w;
            }
        }

        internal static partial int MUnmap(IntPtr addr, ulong len)
        {
            WriteBack(addr, len);
            lock (s_writeBack) s_writeBack.Remove(addr);
            NativeMemory.AlignedFree((void*)addr);
            return 0;
        }

        internal static partial int MSync(IntPtr addr, ulong len, MemoryMappedSyncFlags flags)
        {
            WriteBack(addr, len);
            return 0;
        }

        internal static partial int MAdvise(IntPtr addr, ulong length, MemoryAdvice advice) => 0;

        internal static partial long SysConf(SysConfName name) => name switch
        {
            SysConfName._SC_PAGESIZE => PageSize,
            SysConfName._SC_CLK_TCK => 100,
            _ => Fail(Error.EINVAL),
        };

        static long Fail(Error e) { Marshal.SetLastPInvokeError((int)ConvertErrorPalToPlatform(e)); return -1; }

        internal static partial SafeFileHandle ShmOpen(string name, OpenFlags flags, int mode)
        {
            Marshal.SetLastPInvokeError((int)ConvertErrorPalToPlatform(Error.ENOTSUP));
            return new SafeFileHandle();
        }

        internal static partial int ShmUnlink(string name) => (int)Fail(Error.ENOTSUP);

        internal static partial SafeFileHandle MemfdCreate(string name, int isReadonly)
        {
            Marshal.SetLastPInvokeError((int)ConvertErrorPalToPlatform(Error.ENOTSUP));
            return new SafeFileHandle();
        }

        private static partial int MemfdSupportedImpl() => 0;
    }
}
