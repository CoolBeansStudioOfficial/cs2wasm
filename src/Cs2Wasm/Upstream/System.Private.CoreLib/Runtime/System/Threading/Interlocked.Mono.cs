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
        public static object? CompareExchange(ref object? location1, object? value, object? comparand)
        {
            ref int slot = ref Unsafe.As<object?, int>(ref location1);
            int v = Rt.AddressOf(value!), c = Rt.AddressOf(comparand!);
            if (v != 0) { Rt.Share(value!); Rt.Retain(value!); } // the count the slot takes if the exchange happens
            int old = Atomic.CompareExchange32(ref slot, c, v);
            if (old == c) return Rt.Take<object>(old); // the slot's count of the old value passes to the result
            if (v != 0) Rt.Release(value!);
            return Rt.Borrow<object>(old);
        }

        public static int Decrement(ref int location) => Atomic.Add32(ref location, -1) - 1;

        public static long Decrement(ref long location) => Atomic.Add64(ref location, -1) - 1;

        public static int Increment(ref int location) => Atomic.Add32(ref location, 1) + 1;

        public static long Increment(ref long location) => Atomic.Add64(ref location, 1) + 1;

        public static int Exchange(ref int location1, int value) => Atomic.Exchange32(ref location1, value);

        [return: NotNullIfNotNull(nameof(location1))]
        public static object? Exchange([NotNullIfNotNull(nameof(value))] ref object? location1, object? value)
        {
            ref int slot = ref Unsafe.As<object?, int>(ref location1);
            int v = Rt.AddressOf(value!);
            if (v != 0) { Rt.Share(value!); Rt.Retain(value!); }
            return Rt.Take<object>(Atomic.Exchange32(ref slot, v));
        }

        public static long CompareExchange(ref long location1, long value, long comparand) => Atomic.CompareExchange64(ref location1, comparand, value);

        public static long Exchange(ref long location1, long value) => Atomic.Exchange64(ref location1, value);

        public static long Read(ref long location) => Atomic.Load64(ref location);

        public static int Add(ref int location1, int value) => Atomic.Add32(ref location1, value) + value;

        public static long Add(ref long location1, long value) => Atomic.Add64(ref location1, value) + value;

        public static void MemoryBarrierProcessWide() => Atomic.Fence();
    }
}
