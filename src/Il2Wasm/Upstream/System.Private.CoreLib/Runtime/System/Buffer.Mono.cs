// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System
{
    public partial class Buffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void Memmove<T>(ref T destination, ref T source, nuint elementCount)
        {
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                // Blittable memmove
                SpanHelpers.Memmove(
                    ref Unsafe.As<T, byte>(ref destination),
                    ref Unsafe.As<T, byte>(ref source),
                    elementCount * (nuint)sizeof(T));
            }
            else if (elementCount > 0)
            {
                // Non-blittable memmove. il2wasm: the copies count; the overwritten references are released (all
                // relative to the values before the move, so overlapping ranges count right)
                int dst = (int)Unsafe.AsPointer(ref destination), src = (int)Unsafe.AsPointer(ref source);
                // (references into slots a cycle can close through: one by one, as stores: Rt.ArenaMove)
                if (Il2Wasm.Runtime.Rt.IsReference<T>() && Il2Wasm.Runtime.Rt.MoveRefs(dst, src, (int)elementCount)) return;
                if (!Il2Wasm.Runtime.Rt.SingleThreaded())
                {
                    // threads: counted and exchanged with no store able to come in between; the old references, set
                    // aside, are released after
                    int bytes = (int)(elementCount * (nuint)sizeof(T));
                    int tmp = Il2Wasm.Runtime.Rt.Alloc(bytes);
                    Il2Wasm.Runtime.Rt.BulkEnter();
                    Il2Wasm.Runtime.Rt.RetainElements<T>(src, (int)elementCount);
                    Il2Wasm.Runtime.Mem.Copy(tmp, dst, bytes);
                    // (word by word: another thread may read one of the references meanwhile, and must find it whole)
                    if ((uint)(dst - src) >= (uint)bytes) { for (int i = 0; i < bytes; i += 4) Il2Wasm.Runtime.Mem.StoreI32(dst + i, Il2Wasm.Runtime.Mem.LoadI32(src + i)); }
                    else for (int i = bytes - 4; i >= 0; i -= 4) Il2Wasm.Runtime.Mem.StoreI32(dst + i, Il2Wasm.Runtime.Mem.LoadI32(src + i));
                    Il2Wasm.Runtime.Rt.BulkExit();
                    Il2Wasm.Runtime.Rt.ReleaseElements<T>(tmp, (int)elementCount);
                    Il2Wasm.Runtime.Rt.Free(tmp, bytes);
                    return;
                }
                if (!Il2Wasm.Runtime.Rt.IsReference<T>())
                {
                    // structs that hold references: each element assigned (a struct's assignment stores its
                    // references as stores by address, so they count as they would through the array), in the order
                    // that reads every source before it is written
                    int n = (int)elementCount;
                    if ((uint)dst <= (uint)src) for (int i = 0; i < n; i++) Unsafe.Add(ref destination, i) = Unsafe.Add(ref source, i);
                    else for (int i = n - 1; i >= 0; i--) Unsafe.Add(ref destination, i) = Unsafe.Add(ref source, i);
                    return;
                }
                Il2Wasm.Runtime.Rt.RetainElements<T>(src, (int)elementCount);
                Il2Wasm.Runtime.Rt.ReleaseElements<T>(dst, (int)elementCount);
                SpanHelpers.Memmove(ref Unsafe.As<T, byte>(ref destination), ref Unsafe.As<T, byte>(ref source), elementCount * (nuint)sizeof(T));
            }
        }
    }
}
