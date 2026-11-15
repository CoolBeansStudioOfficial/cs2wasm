// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's runtime half of Monitor. An object's lock state lives in a table in linear memory keyed by the
// object's address (cs2wasm's object header has no sync word), under the runtime lock. A thread that has to wait
// sleeps on the runtime's generation counter (Cs2Wasm.Runtime.Threads), which every exit and pulse bumps; the engine
// thread never blocks: it spins (section 4.5).

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Cs2Wasm.Runtime;

namespace System.Threading
{
    public static partial class Monitor
    {
        // entry: [0] object address, [4] owner thread id, [8] depth, [12] waiters, [16] pending pulses
        const int EntrySize = 20;
        static int s_table, s_cap, s_used;

        /// <summary>The entry of an object (under the runtime lock); 0 when it has none and create is false.</summary>
        static int Find(int key, bool create)
        {
            if (s_cap == 0) { if (!create) return 0; Grow(); }
            while (true)
            {
                int mask = s_cap - 1;
                int i = (int)(((uint)key >> 3) * 2654435761u) & mask;
                while (true)
                {
                    int e = s_table + i * EntrySize;
                    int k = Mem.LoadI32(e);
                    if (k == key) return e;
                    if (k == 0) break;
                    i = (i + 1) & mask;
                }
                if (!create) return 0;
                if ((s_used + 1) * 2 > s_cap) { Grow(); continue; }
                int ne = s_table + i * EntrySize;
                Mem.StoreI32(ne, key);
                s_used++;
                return ne;
            }
        }

        /// <summary>Rehashes the entries in use (owned or waited on) into a table with room: idle ones are dropped (an
        /// object freed and another allocated at its address finds no stale state).</summary>
        static void Grow()
        {
            int oldTable = s_table, oldCap = s_cap, live = 0;
            for (int i = 0; i < oldCap; i++)
            {
                int e = oldTable + i * EntrySize;
                if (Mem.LoadI32(e) != 0 && InUse(e)) live++;
            }
            int cap = 16;
            while (cap < live * 4) cap *= 2;
            s_table = Rt.Alloc(cap * EntrySize);
            Mem.Fill(s_table, 0, cap * EntrySize);
            s_cap = cap;
            s_used = 0;
            for (int i = 0; i < oldCap; i++)
            {
                int e = oldTable + i * EntrySize;
                if (Mem.LoadI32(e) == 0 || !InUse(e)) continue;
                int ne = Find(Mem.LoadI32(e), true);
                Mem.Copy(ne + 4, e + 4, EntrySize - 4);
            }
            if (oldCap != 0) Rt.Free(oldTable, oldCap * EntrySize);
        }

        static bool InUse(int e) => Mem.LoadI32(e + 4) != 0 || Mem.LoadI32(e + 12) != 0;

        /// <summary>Takes the lock if it is free or ours; false if another thread holds it.</summary>
        static bool TryTake(object obj)
        {
            int me = Rt.ThreadId();
            Rt.RuntimeLock();
            int e = Find(Rt.AddressOf(obj), true);
            int owner = Mem.LoadI32(e + 4);
            bool ok = owner == 0 || owner == me;
            if (ok) { Mem.StoreI32(e + 4, me); Mem.StoreI32(e + 8, Mem.LoadI32(e + 8) + 1); }
            Rt.RuntimeUnlock();
            return ok;
        }

        /// <summary>Waits until the lock is taken, or the timeout (ms, -1 forever) passes.</summary>
        static bool Take(object obj, int millisecondsTimeout)
        {
            // (with no other thread the lock is free or ours)
            if (Rt.SingleThreaded()) return TryTake(obj);
            long end = millisecondsTimeout < 0 ? long.MaxValue : Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000;
            while (true)
            {
                int g = Atomic.Load32(ref Threads.s_generation);
                if (TryTake(obj)) return true;
                if (millisecondsTimeout == 0 || Engine.MonotonicNs() >= end) return false;
                Threads.WaitGeneration(g);
            }
        }

        public static void Enter(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            Take(obj, -1);
        }

        public static void Enter(object obj, ref bool lockTaken)
        {
            if (lockTaken)
                throw new ArgumentException(SR.Argument_MustBeFalse, nameof(lockTaken));

            ReliableEnterTimeout(obj, (int)Timeout.Infinite, ref lockTaken);
        }

        public static void Exit(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            int me = Rt.ThreadId();
            Rt.RuntimeLock();
            int e = Find(Rt.AddressOf(obj), false);
            if (e == 0 || Mem.LoadI32(e + 4) != me)
            {
                Rt.RuntimeUnlock();
                throw new SynchronizationLockException();
            }
            int depth = Mem.LoadI32(e + 8) - 1;
            Mem.StoreI32(e + 8, depth);
            if (depth == 0) Mem.StoreI32(e + 4, 0);
            Rt.RuntimeUnlock();
            if (depth == 0 && !Rt.SingleThreaded()) Threads.WakeAll();
        }

        public static bool TryEnter(object obj)
        {
            bool lockTaken = false;
            TryEnter(obj, 0, ref lockTaken);
            return lockTaken;
        }

        public static void TryEnter(object obj, ref bool lockTaken)
        {
            if (lockTaken)
                throw new ArgumentException(SR.Argument_MustBeFalse, nameof(lockTaken));

            ReliableEnterTimeout(obj, 0, ref lockTaken);
        }

        public static bool TryEnter(object obj, int millisecondsTimeout)
        {
            bool lockTaken = false;
            TryEnter(obj, millisecondsTimeout, ref lockTaken);
            return lockTaken;
        }

        public static void TryEnter(object obj, int millisecondsTimeout, ref bool lockTaken)
        {
            if (lockTaken)
                throw new ArgumentException(SR.Argument_MustBeFalse, nameof(lockTaken));
            ReliableEnterTimeout(obj, millisecondsTimeout, ref lockTaken);
        }

        public static bool IsEntered(object obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            Rt.RuntimeLock();
            int e = Find(Rt.AddressOf(obj), false);
            bool r = e != 0 && Mem.LoadI32(e + 4) == Rt.ThreadId();
            Rt.RuntimeUnlock();
            return r;
        }

        public static bool Wait(object obj, int millisecondsTimeout)
        {
            ArgumentNullException.ThrowIfNull(obj);
            if (millisecondsTimeout < 0 && millisecondsTimeout != (int)Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            int me = Rt.ThreadId(), key = Rt.AddressOf(obj);
            Rt.RuntimeLock();
            int e = Find(key, false);
            if (e == 0 || Mem.LoadI32(e + 4) != me)
            {
                Rt.RuntimeUnlock();
                throw new SynchronizationLockException();
            }
            // release the lock entirely, and wait for a pulse
            int depth = Mem.LoadI32(e + 8);
            Mem.StoreI32(e + 4, 0);
            Mem.StoreI32(e + 8, 0);
            Mem.StoreI32(e + 12, Mem.LoadI32(e + 12) + 1);
            Rt.RuntimeUnlock();
            Threads.WakeAll();
            long end = millisecondsTimeout < 0 ? long.MaxValue : Engine.MonotonicNs() + (long)millisecondsTimeout * 1000000;
            bool signaled = false;
            while (true)
            {
                int g = Atomic.Load32(ref Threads.s_generation);
                Rt.RuntimeLock();
                e = Find(key, false);
                int pulses = Mem.LoadI32(e + 16);
                if (pulses > 0) { Mem.StoreI32(e + 16, pulses - 1); signaled = true; }
                bool done = signaled || Engine.MonotonicNs() >= end;
                if (done) Mem.StoreI32(e + 12, Mem.LoadI32(e + 12) - 1);
                Rt.RuntimeUnlock();
                if (done) break;
                Threads.WaitGeneration(g);
            }
            // take the lock back, at the depth it had
            Take(obj, -1);
            Rt.RuntimeLock();
            e = Find(key, false);
            Mem.StoreI32(e + 8, depth);
            Rt.RuntimeUnlock();
            return signaled;
        }

        public static void Pulse(object obj) => Signal(obj, false);

        public static void PulseAll(object obj) => Signal(obj, true);

        static void Signal(object obj, bool all)
        {
            ArgumentNullException.ThrowIfNull(obj);
            Rt.RuntimeLock();
            int e = Find(Rt.AddressOf(obj), false);
            if (e == 0 || Mem.LoadI32(e + 4) != Rt.ThreadId())
            {
                Rt.RuntimeUnlock();
                throw new SynchronizationLockException();
            }
            int waiters = Mem.LoadI32(e + 12), pulses = Mem.LoadI32(e + 16);
            if (pulses < waiters) Mem.StoreI32(e + 16, all ? waiters : pulses + 1);
            Rt.RuntimeUnlock();
            Threads.WakeAll();
        }

        private static void ReliableEnterTimeout(object obj, int timeout, ref bool lockTaken)
        {
            ArgumentNullException.ThrowIfNull(obj);

            if (timeout < 0 && timeout != (int)Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            lockTaken = Take(obj, timeout);
        }

        public static long LockContentionCount => Lock.ContentionCount;
    }
}
