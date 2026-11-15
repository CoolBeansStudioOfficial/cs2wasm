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
