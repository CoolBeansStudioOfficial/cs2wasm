// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices
{
    public partial struct GCHandle
    {
                internal static IntPtr InternalAlloc(object? value, GCHandleType type)
        {
            int slot = Cs2Wasm.Runtime.Rt.Alloc(4);
            Cs2Wasm.Runtime.Mem.StoreI32(slot, 0);
            if (value != null) { Cs2Wasm.Runtime.Rt.Retain(value); Cs2Wasm.Runtime.Mem.StoreI32(slot, Cs2Wasm.Runtime.Rt.AddressOf(value)); }
            return (IntPtr)(slot | (type == GCHandleType.Pinned ? 1 : 0));
        }

                internal static void InternalFree(IntPtr handle)
        {
            int slot = (int)handle & ~1;
            int v = Cs2Wasm.Runtime.Mem.LoadI32(slot);
            if (v != 0) Cs2Wasm.Runtime.Rt.Release(Cs2Wasm.Runtime.Rt.Borrow<object>(v));
            Cs2Wasm.Runtime.Rt.Free(slot, 4);
        }

                internal static object? InternalGet(IntPtr handle) => Cs2Wasm.Runtime.Rt.Borrow<object>(Cs2Wasm.Runtime.Mem.LoadI32((int)handle & ~1));

                internal static void InternalSet(IntPtr handle, object? value)
        {
            int slot = (int)handle & ~1;
            if (value != null) Cs2Wasm.Runtime.Rt.Retain(value);
            int old = Cs2Wasm.Runtime.Mem.LoadI32(slot);
            Cs2Wasm.Runtime.Mem.StoreI32(slot, value == null ? 0 : Cs2Wasm.Runtime.Rt.AddressOf(value));
            if (old != 0) Cs2Wasm.Runtime.Rt.Release(Cs2Wasm.Runtime.Rt.Borrow<object>(old));
        }
    }
}
