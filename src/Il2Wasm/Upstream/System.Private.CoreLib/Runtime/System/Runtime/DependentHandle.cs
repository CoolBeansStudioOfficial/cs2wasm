// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime
{
    internal struct Ephemeron
    {
        public object? Key;
        public object? Value;
    }

    // il2wasm (2026-11-22; before, Mono's Ephemeron[] with the key counted: a ConditionalWeakTable kept every key
    // and every value while it lived). A handle is the address of a slot of the runtime's own memory,
    // [target's address, dependent's address]: the target is not counted, the dependent is. When the target is
    // destroyed (its last reference goes: Il2Wasm.Runtime.ObjectMarks.OnDestroy, before its finalizer, as .NET clears
    // a short weak reference) the slot's target is cleared and the dependent let go (WeakSlots.Cleared). As in
    // .NET the slot itself lives until Dispose (ConditionalWeakTable's container disposes its entries' handles).
    // Like a GC handle the slot is global: what it holds is shared (section 4.5).
    // Not .NET's: a dependent that holds its own target (directly or through other objects) keeps both, as any
    // cycle of counted references not known to the compiler does; .NET's collector frees both (docs/AUDIT.md).
    public struct DependentHandle : IDisposable
    {
        private int _slot;

        public DependentHandle(object? target, object? dependent)
        {
            int slot = Il2Wasm.Runtime.Rt.Alloc(8);
            Il2Wasm.Runtime.Mem.StoreI32(slot, 0);
            Il2Wasm.Runtime.Mem.StoreI32(slot + 4, 0);
            _slot = slot;
            if (target is null) return;
            Il2Wasm.Runtime.Rt.Share(target);
            if (dependent is not null)
            {
                Il2Wasm.Runtime.Rt.Share(dependent);
                // (a dependent that is its own target is not counted: the slot holds it for as long as the target
                // lives, which is as long as it lives; counted, it kept itself for good, where .NET frees it)
                if (!ReferenceEquals(dependent, target)) Il2Wasm.Runtime.Rt.Retain(dependent);
                Il2Wasm.Runtime.Mem.StoreI32(slot + 4, Il2Wasm.Runtime.Rt.AddressOf(dependent));
            }
            Il2Wasm.Runtime.WeakSlots.LinkDependent(slot, target);
        }

        public bool IsAllocated => _slot != 0;

        public object? Target
        {
            get => UnsafeGetTarget();
            set
            {
                if (_slot == 0 || value is not null)
                {
                    ThrowHelper.ThrowInvalidOperationException();

                    return;
                }

                UnsafeSetTargetToNull();
            }
        }

        public object? Dependent
        {
            get => UnsafeGetDependent();
            set => UnsafeSetDependent(value);
        }

        public (object? Target, object? Dependent) TargetAndDependent
        {
            get
            {
                object? target = UnsafeGetTargetAndDependent(out object? dependent);

                return (target, dependent);
            }
        }

        internal object? UnsafeGetTarget()
        {
            int slot = _slot;

            if (slot == 0)
            {
                ThrowHelper.ThrowInvalidOperationException();

                return default;
            }

            if (Il2Wasm.Runtime.Rt.SingleThreaded())
            {
                int v = Il2Wasm.Runtime.Mem.LoadI32(slot);
                return v == 0 ? null : Il2Wasm.Runtime.Rt.Borrow<object>(v);
            }
            // threads: the target may be on its way out on another thread (the slot holds no count of it); it is
            // counted here only if it is still alive, under the lock the clearing of the slot takes
            Il2Wasm.Runtime.Rt.RuntimeLock();
            int a = Il2Wasm.Runtime.Mem.LoadI32(slot);
            bool alive = a != 0 && Il2Wasm.Runtime.Rt.TryRetain(a);
            Il2Wasm.Runtime.Rt.RuntimeUnlock();
            return alive ? Il2Wasm.Runtime.Rt.Take<object>(a) : null;
        }

        internal object? UnsafeGetDependent()
        {
            int slot = _slot;

            if (slot == 0)
            {
                ThrowHelper.ThrowInvalidOperationException();

                return default;
            }

            if (Il2Wasm.Runtime.Rt.SingleThreaded())
            {
                int v = Il2Wasm.Runtime.Mem.LoadI32(slot) == 0 ? 0 : Il2Wasm.Runtime.Mem.LoadI32(slot + 4);
                return v == 0 ? null : Il2Wasm.Runtime.Rt.Borrow<object>(v);
            }
            // threads: the slot's count of the dependent is there while the lock is held (the clearing lets it go
            // after taking the dependent out of the slot under the lock)
            Il2Wasm.Runtime.Rt.RuntimeLock();
            int d = Il2Wasm.Runtime.Mem.LoadI32(slot) == 0 ? 0 : Il2Wasm.Runtime.Mem.LoadI32(slot + 4);
            if (d != 0) Il2Wasm.Runtime.Rt.Retain(Il2Wasm.Runtime.Rt.Borrow<object>(d));
            Il2Wasm.Runtime.Rt.RuntimeUnlock();
            return d != 0 ? Il2Wasm.Runtime.Rt.Take<object>(d) : null;
        }

        internal object? UnsafeGetTargetAndDependent(out object? dependent)
        {
            int slot = _slot;

            if (slot == 0)
            {
                ThrowHelper.ThrowInvalidOperationException();

                dependent = null;

                return null;
            }

            if (Il2Wasm.Runtime.Rt.SingleThreaded())
            {
                int v = Il2Wasm.Runtime.Mem.LoadI32(slot);
                if (v == 0) { dependent = null; return null; }
                int dv = Il2Wasm.Runtime.Mem.LoadI32(slot + 4);
                dependent = dv == 0 ? null : Il2Wasm.Runtime.Rt.Borrow<object>(dv);
                return Il2Wasm.Runtime.Rt.Borrow<object>(v);
            }
            Il2Wasm.Runtime.Rt.RuntimeLock();
            int a = Il2Wasm.Runtime.Mem.LoadI32(slot);
            bool alive = a != 0 && Il2Wasm.Runtime.Rt.TryRetain(a);
            int d = alive ? Il2Wasm.Runtime.Mem.LoadI32(slot + 4) : 0;
            if (d != 0) Il2Wasm.Runtime.Rt.Retain(Il2Wasm.Runtime.Rt.Borrow<object>(d));
            Il2Wasm.Runtime.Rt.RuntimeUnlock();
            dependent = d != 0 ? Il2Wasm.Runtime.Rt.Take<object>(d) : null;
            return alive ? Il2Wasm.Runtime.Rt.Take<object>(a) : null;
        }

        internal void UnsafeSetTargetToNull()
        {
            int slot = _slot;

            if (slot == 0)
            {
                ThrowHelper.ThrowInvalidOperationException();

                return;
            }

            // (the dependent goes with the target: nothing is to keep it any more)
            Il2Wasm.Runtime.WeakSlots.UnlinkDependent(slot);
        }

        internal void UnsafeSetDependent(object? dependent)
        {
            int slot = _slot;

            if (slot == 0)
            {
                ThrowHelper.ThrowInvalidOperationException();

                return;
            }

            if (dependent is not null)
            {
                Il2Wasm.Runtime.Rt.Share(dependent);
                Il2Wasm.Runtime.Rt.Retain(dependent);
            }
            // (a target that is gone keeps no dependent)
            int old = Il2Wasm.Runtime.WeakSlots.ExchangeDependent(slot, dependent is null ? 0 : Il2Wasm.Runtime.Rt.AddressOf(dependent), out bool stored);
            if (old != 0) Il2Wasm.Runtime.Rt.Release(Il2Wasm.Runtime.Rt.Borrow<object>(old));
            if (!stored && dependent is not null) Il2Wasm.Runtime.Rt.Release(dependent);
        }

        public void Dispose()
        {
            int slot = _slot;
            if (slot == 0) return;
            _slot = 0;
            Il2Wasm.Runtime.WeakSlots.UnlinkDependent(slot);
            Il2Wasm.Runtime.Rt.Free(slot, 8);
        }
    }
}
