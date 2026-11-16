using System;
using Cs2Wasm.Runtime;

// cs2wasm: the System.Native functions the library calls that are the engine's (SPEC 7.4): the clock, the seeded
// randomness, the waits of the low-level monitor (over the threads proposal's atomics), and errno bookkeeping.
// Every other platform invoke is a build error when reached (Generated/Interop.Unavailable.g.cs).
internal static partial class Interop
{
    internal static unsafe partial class Sys
    {
        /// <summary>Monotonic time in ns (Stopwatch's frequency on Unix is 1e9).</summary>
        internal static partial long GetTimestamp() => Engine.MonotonicNs();

        /// <summary>Ticks (100 ns) since the Unix epoch.</summary>
        internal static partial long GetSystemTimeAsTicks() => (long)(Engine.NowMs() * 10000.0);

        /// <summary>Milliseconds of monotonic time (Environment.TickCount64).</summary>
        internal static partial long GetLowResolutionTimestamp() => Engine.MonotonicNs() / 1000000;

        static ulong s_random;
        static int s_randomLock;

        /// <summary>System.Random's seeds and the like: from the engine's seeded import (it makes them match across
        /// peers), stretched with splitmix64.</summary>
        /// <summary>Guid.NewGuid, RandomNumberGenerator and the like: the sandbox's randomness is the engine's (section
        /// 7.4), as secure as the engine's seed makes it.</summary>
        internal static unsafe partial int GetCryptographicallySecureRandomBytes(byte* buffer, int length)
        {
            GetNonCryptographicallySecureRandomBytes(buffer, length);
            return 0;
        }

        internal static unsafe partial void GetNonCryptographicallySecureRandomBytes(byte* buffer, int length)
        {
            while (Atomic.CompareExchange32(ref s_randomLock, 0, 1) != 0) { }
            if (s_random == 0) s_random = ((ulong)(uint)Engine.RandomSeed() << 1) | 1;
            ulong z = 0;
            for (int i = 0; i < length; i++)
            {
                if ((i & 7) == 0)
                {
                    z = s_random += 0x9E3779B97F4A7C15UL;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    z ^= z >> 31;
                }
                buffer[i] = (byte)(z >> ((i & 7) * 8));
            }
            Atomic.Store32(ref s_randomLock, 0);
        }

        internal static partial int SchedGetCpu() => -1;

        /// <summary>No environment exists in the sandbox: every variable is unset.</summary>
        internal static unsafe partial byte* GetEnv(string name) => null;

        internal static partial Error ConvertErrorPlatformToPal(int platformErrno) => (Error)platformErrno;

        internal static partial int ConvertErrorPalToPlatform(Error error) => (int)error;

        private static unsafe partial byte* StrErrorR(int platformErrno, byte* buffer, int bufferSize)
        {
            // "error <n>", ASCII, null-terminated
            if (bufferSize <= 0) return buffer;
            string s = "error " + platformErrno.ToString();
            int n = Math.Min(s.Length, bufferSize - 1);
            for (int i = 0; i < n; i++) buffer[i] = (byte)s[i];
            buffer[n] = 0;
            return buffer;
        }

        // ---------------- native memory (NativeMemory, Marshal.AllocHGlobal): blocks of the module's heap ----------------
        // header before the block: [-8] bytes taken from the heap, [-4] offset of the block from what was taken
        static void* Take(nuint size, nuint alignment)
        {
            if (alignment < 8) alignment = 8;
            long total = (long)size + (long)alignment + 8;
            if (total > 0x7FFF0000L) throw new OutOfMemoryException();
            int b = Rt.Alloc((int)total);
            int p = (b + 8 + (int)alignment - 1) & ~((int)alignment - 1);
            Mem.StoreI32(p - 8, (int)total);
            Mem.StoreI32(p - 4, p - b);
            return (void*)p;
        }

        static nuint Usable(void* ptr) => (nuint)(Mem.LoadI32((int)ptr - 8) - Mem.LoadI32((int)ptr - 4));

        internal static partial void* Malloc(nuint size) => Take(size, 8);

        internal static partial void* Calloc(nuint num, nuint size)
        {
            nuint n = num * size;
            void* p = Take(n, 8);
            Mem.Fill((int)p, 0, (int)n);
            return p;
        }

        internal static partial void Free(void* ptr)
        {
            if (ptr == null) return;
            int p = (int)ptr;
            Rt.Free(p - Mem.LoadI32(p - 4), Mem.LoadI32(p - 8));
        }

        internal static partial void* Realloc(void* ptr, nuint new_size) => AlignedRealloc(ptr, 8, new_size);

        internal static partial void* AlignedAlloc(nuint alignment, nuint size) => Take(size, alignment);

        internal static partial void AlignedFree(void* ptr) => Free(ptr);

        internal static partial void* AlignedRealloc(void* ptr, nuint alignment, nuint new_size)
        {
            void* q = Take(new_size, alignment);
            if (ptr != null)
            {
                nuint keep = Usable(ptr);
                Mem.Copy((int)q, (int)ptr, (int)(keep < new_size ? keep : new_size));
                Free(ptr);
            }
            return q;
        }

        // ---------------- descriptors (files: Interop.Files.cs, over the host's file system) ----------------
        internal static partial int FLock(IntPtr fd, LockOperations operation) => 0;

        internal static partial int FLock(Microsoft.Win32.SafeHandles.SafeFileHandle fd, LockOperations operation) => 0;

        /// <summary>No file system: no file can be hidden.</summary>
        private static partial int LChflagsCanSetHiddenFlag() => 0;

        private static partial int CanGetHiddenFlag() => 0;


        // ---------------- the low-level monitor: [0] lock (0 free, 1 held), [4] signal sequence ----------------
        internal static partial IntPtr LowLevelMonitor_Create()
        {
            int m = Rt.Alloc(8);
            Mem.StoreI32(m, 0);
            Mem.StoreI32(m + 4, 0);
            return m;
        }

        internal static partial void LowLevelMonitor_Destroy(IntPtr monitor) => Rt.Free((int)monitor, 8);

        internal static partial void LowLevelMonitor_Acquire(IntPtr monitor)
        {
            ref int l = ref Mem.RefI32((int)monitor);
            while (Atomic.CompareExchange32(ref l, 0, 1) != 0)
            {
                // the engine thread never blocks (section 4.5): it spins
                if (Rt.ThreadId() != 1) Atomic.Wait32(ref l, 1, 1000000);
            }
        }

        internal static partial void LowLevelMonitor_Release(IntPtr monitor)
        {
            ref int l = ref Mem.RefI32((int)monitor);
            Atomic.Store32(ref l, 0);
            Atomic.Notify(ref l, 1);
        }

        internal static partial void LowLevelMonitor_Wait(IntPtr monitor) => LowLevelMonitor_TimedWait(monitor, -1);

        internal static partial bool LowLevelMonitor_TimedWait(IntPtr monitor, int timeoutMilliseconds)
        {
            ref int seq = ref Mem.RefI32((int)monitor + 4);
            int s = Atomic.Load32(ref seq);
            LowLevelMonitor_Release(monitor);
            long end = timeoutMilliseconds < 0 ? long.MaxValue : Engine.MonotonicNs() + (long)timeoutMilliseconds * 1000000;
            bool signaled = false;
            while (true)
            {
                if (Atomic.Load32(ref seq) != s) { signaled = true; break; }
                long left = end - Engine.MonotonicNs();
                if (left <= 0) break;
                Threads.SafePoint();
                if (Rt.ThreadId() != 1) Atomic.Wait32(ref seq, s, left > 1000000 ? 1000000 : left);
            }
            LowLevelMonitor_Acquire(monitor);
            return signaled;
        }

        internal static partial void LowLevelMonitor_Signal_Release(IntPtr monitor)
        {
            ref int seq = ref Mem.RefI32((int)monitor + 4);
            Atomic.Add32(ref seq, 1);
            Atomic.Notify(ref seq, 1);
            LowLevelMonitor_Release(monitor);
        }
    }
}
