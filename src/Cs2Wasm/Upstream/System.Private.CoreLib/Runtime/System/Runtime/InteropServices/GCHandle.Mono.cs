// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices
{
    // cs2wasm: a handle is the address of a slot [target address, kind] (4-aligned, so bit 0 can mark pinned ones).
    // A strong handle (Normal, Pinned) counts its target; a weak one does not, and is cleared when the target is
    // destroyed (Cs2Wasm.Runtime.ObjectMarks). A handle is global (any thread may read it, as a static field): its
    // target is shared (section 4.5).
    public partial struct GCHandle
    {
        static bool IsWeakKind(int kind) => kind <= (int)GCHandleType.WeakTrackResurrection;

        internal static IntPtr InternalAlloc(object? value, GCHandleType type)
        {
            int slot = Cs2Wasm.Runtime.Rt.Alloc(8);
            Cs2Wasm.Runtime.Mem.StoreI32(slot, 0);
            Cs2Wasm.Runtime.Mem.StoreI32(slot + 4, (int)type);
            if (value != null) Store(slot, value);
            return (IntPtr)(slot | (type == GCHandleType.Pinned ? 1 : 0));
        }

        static void Store(int slot, object value)
        {
            Cs2Wasm.Runtime.Rt.Share(value);
            if (IsWeakKind(Cs2Wasm.Runtime.Mem.LoadI32(slot + 4))) Cs2Wasm.Runtime.WeakSlots.Link(slot, value);
            else { Cs2Wasm.Runtime.Rt.Retain(value); Cs2Wasm.Runtime.Mem.StoreI32(slot, Cs2Wasm.Runtime.Rt.AddressOf(value)); }
        }

        static void Drop(int slot)
        {
            int v = Cs2Wasm.Runtime.Mem.LoadI32(slot);
            if (v == 0) return;
            if (IsWeakKind(Cs2Wasm.Runtime.Mem.LoadI32(slot + 4))) Cs2Wasm.Runtime.WeakSlots.Unlink(slot);
            else { Cs2Wasm.Runtime.Mem.StoreI32(slot, 0); Cs2Wasm.Runtime.Rt.Release(Cs2Wasm.Runtime.Rt.Borrow<object>(v)); }
        }

        internal static void InternalFree(IntPtr handle)
        {
            int slot = (int)handle & ~1;
            Drop(slot);
            Cs2Wasm.Runtime.Rt.Free(slot, 8);
        }

        internal static object? InternalGet(IntPtr handle)
        {
            int slot = (int)handle & ~1;
            if (Cs2Wasm.Runtime.Rt.SingleThreaded())
            {
                int v = Cs2Wasm.Runtime.Mem.LoadI32(slot);
                return v == 0 ? null : Cs2Wasm.Runtime.Rt.Borrow<object>(v);
            }
            // threads: the target may be on its way out on another thread (a weak handle holds no count); it is
            // counted here only if it is still alive, under the lock the clearing of weak handles takes
            Cs2Wasm.Runtime.Rt.RuntimeLock();
            int a = Cs2Wasm.Runtime.Mem.LoadI32(slot);
            bool alive = a != 0 && Cs2Wasm.Runtime.Rt.TryRetain(a);
            Cs2Wasm.Runtime.Rt.RuntimeUnlock();
            return alive ? Cs2Wasm.Runtime.Rt.Take<object>(a) : null;
        }

        internal static void InternalSet(IntPtr handle, object? value)
        {
            int slot = (int)handle & ~1;
            if (value != null) Cs2Wasm.Runtime.Rt.Share(value);
            if (value != null && !IsWeakKind(Cs2Wasm.Runtime.Mem.LoadI32(slot + 4))) Cs2Wasm.Runtime.Rt.Retain(value); // before the old one goes
            int old = Cs2Wasm.Runtime.Mem.LoadI32(slot);
            if (value != null && !IsWeakKind(Cs2Wasm.Runtime.Mem.LoadI32(slot + 4)))
            {
                Cs2Wasm.Runtime.Mem.StoreI32(slot, Cs2Wasm.Runtime.Rt.AddressOf(value));
                if (old != 0) Cs2Wasm.Runtime.Rt.Release(Cs2Wasm.Runtime.Rt.Borrow<object>(old));
                return;
            }
            Drop(slot);
            if (value != null) Store(slot, value);
        }
    }
}
