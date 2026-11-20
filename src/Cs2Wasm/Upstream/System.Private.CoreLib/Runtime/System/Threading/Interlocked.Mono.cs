// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls of Interlocked, as the threads proposal's atomic instructions (Cs2Wasm.Runtime.Atomic).
// A reference location holds a counted reference: an exchange moves the slot's count to the result.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Cs2Wasm.Runtime;

namespace System.Threading
{
    public static partial class Interlocked
    {
        public static int CompareExchange(ref int location1, int value, int comparand) => Atomic.CompareExchange32(ref location1, comparand, value);

        [return: NotNullIfNotNull(nameof(location1))]
        public static unsafe object? CompareExchange(ref object? location1, object? value, object? comparand)
        {
            ref int slot = ref Unsafe.As<object?, int>(ref location1);
            int v = Rt.AddressOf(value!), c = Rt.AddressOf(comparand!);
            if (Rt.SingleThreaded())
            {
                // (no other thread: the store the code would have written, the slot known by its address)
                int at = (int)Unsafe.AsPointer(ref slot);
                int was = slot;
                if (was != 0) Rt.Retain(Rt.Borrow<object>(was)); // the result's count
                if (was == c)
                {
                    if (v != 0) Rt.Retain(value!);
                    slot = Rt.LinkAt(v, at);
                    Rt.UnlinkAt(was, at);
                }
                return Rt.Take<object>(was);
            }
            if (v != 0) Rt.Retain(value!); // the count the slot takes if the exchange happens
            int ad = (int)Unsafe.AsPointer(ref slot);
            // A slot of an object of a cycle-capable type: the link, the exchange and the unlink are one step, a
            // store's region (the container's group and the value's locked throughout, and the slot's place:
            // Rt.StoreLockAtIf, Rt.RegionCompareExchange; the slot's bits say what it holds: two threads' exchanges of
            // one slot, each linked before it and unlinked after, left them saying another's, and its counts wrong:
            // freed objects still in use, seen as the compiler compiling itself on 24 threads). The exchange is an
            // atomic one; a read that counts still finds the old value counted: its count passes to the result.
            // The link only once the exchange happened: linked before, an exchange that did not happen was taken back
            // as an unlink of the new value with the bits of the value the slot kept, which left the new value owned
            // by the container and counted inside it (a task's continuation added as the task completed: the
            // continuation kept the freed task as its owner, 072_http's rare out-of-bounds read). In the region
            // nothing else reads or changes the slot's bits in between.
            if (Rt.ThreadMode() && Rt.InArenaSlot(ad))
            {
                int took = Rt.StoreLockAtIf(ad);
                int was = Rt.RegionCompareExchange(ad, c, v);
                if (was != 0) Rt.Retain(Rt.Borrow<object>(was)); // the result's count
                if (was == c)
                {
                    Rt.LinkAt(v, ad);
                    Rt.UnlinkAt(was, ad);
                }
                else if (v != 0) Rt.Release(Rt.Borrow<object>(v)); // the slot's count, not taken
                Rt.StoreUnlockIf(took);
                return Rt.Take<object>(was);
            }
            // (linked as the store it may be, before it: taken back as a slot's old value is if it does not happen)
            v = Rt.LinkAt(v, ad);
            // under the place's lock: a read that counts (rt.load_retain) sees the old value still counted, or the new
            int held = Rt.RefLock(ref slot);
            int old = slot;
            bool swapped = old == c;
            if (swapped) slot = v;
            if (old != 0) Rt.Retain(Rt.Borrow<object>(old)); // the result's count, while the slot holds the object
            Rt.RefUnlock(held);
            Rt.UnlinkAt(swapped ? old : v, ad);
            return Rt.Take<object>(old);
        }

        public static int Decrement(ref int location) => Atomic.Add32(ref location, -1) - 1;

        public static long Decrement(ref long location) => Atomic.Add64(ref location, -1) - 1;

        public static int Increment(ref int location) => Atomic.Add32(ref location, 1) + 1;

        public static long Increment(ref long location) => Atomic.Add64(ref location, 1) + 1;

        public static int Exchange(ref int location1, int value) => Atomic.Exchange32(ref location1, value);

        [return: NotNullIfNotNull(nameof(location1))]
        public static unsafe object? Exchange([NotNullIfNotNull(nameof(value))] ref object? location1, object? value)
        {
            ref int slot = ref Unsafe.As<object?, int>(ref location1);
            int v = Rt.AddressOf(value!);
            if (v != 0) Rt.Retain(value!);
            if (Rt.SingleThreaded())
            {
                int at = (int)Unsafe.AsPointer(ref slot);
                int was = slot;
                if (was != 0) Rt.Retain(Rt.Borrow<object>(was)); // the result's count
                slot = Rt.LinkAt(v, at);
                Rt.UnlinkAt(was, at);
                return Rt.Take<object>(was);
            }
            int ad = (int)Unsafe.AsPointer(ref slot);
            // (a slot of an object of a cycle-capable type: one step, a store's region, as CompareExchange)
            if (Rt.ThreadMode() && Rt.InArenaSlot(ad))
            {
                int took = Rt.StoreLockAtIf(ad);
                v = Rt.LinkAt(v, ad);
                int was = Rt.RegionExchange(ad, v);
                if (was != 0) Rt.Retain(Rt.Borrow<object>(was)); // the result's count
                Rt.UnlinkAt(was, ad);
                Rt.StoreUnlockIf(took);
                return Rt.Take<object>(was);
            }
            v = Rt.LinkAt(v, ad);
            int held = Rt.RefLock(ref slot);
            int old = slot;
            slot = v;
            if (old != 0) Rt.Retain(Rt.Borrow<object>(old)); // the result's count
            Rt.RefUnlock(held);
            Rt.UnlinkAt(old, ad);
            return Rt.Take<object>(old);
        }

        public static long CompareExchange(ref long location1, long value, long comparand) => Atomic.CompareExchange64(ref location1, comparand, value);

        public static long Exchange(ref long location1, long value) => Atomic.Exchange64(ref location1, value);

        public static long Read(ref long location) => Atomic.Load64(ref location);

        public static int Add(ref int location1, int value) => Atomic.Add32(ref location1, value) + value;

        public static long Add(ref long location1, long value) => Atomic.Add64(ref location1, value) + value;

        public static void MemoryBarrierProcessWide() => Atomic.Fence();
    }
}
