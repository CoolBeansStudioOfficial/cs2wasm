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
                // Non-blittable memmove. cs2wasm: the copies count; the overwritten references are released (all
                // relative to the values before the move, so overlapping ranges count right)
                int dst = (int)Unsafe.AsPointer(ref destination), src = (int)Unsafe.AsPointer(ref source);
                if (!Cs2Wasm.Runtime.Rt.SingleThreaded())
                {
                    // threads: counted and exchanged with no store able to come in between; the old references, set
                    // aside, are released after
                    int bytes = (int)(elementCount * (nuint)sizeof(T));
                    int tmp = Cs2Wasm.Runtime.Rt.Alloc(bytes);
                    Cs2Wasm.Runtime.Rt.BulkEnter();
                    Cs2Wasm.Runtime.Rt.RetainElements<T>(src, (int)elementCount);
                    Cs2Wasm.Runtime.Mem.Copy(tmp, dst, bytes);
                    // (word by word: another thread may read one of the references meanwhile, and must find it whole)
                    if ((uint)(dst - src) >= (uint)bytes) { for (int i = 0; i < bytes; i += 4) Cs2Wasm.Runtime.Mem.StoreI32(dst + i, Cs2Wasm.Runtime.Mem.LoadI32(src + i)); }
                    else for (int i = bytes - 4; i >= 0; i -= 4) Cs2Wasm.Runtime.Mem.StoreI32(dst + i, Cs2Wasm.Runtime.Mem.LoadI32(src + i));
                    Cs2Wasm.Runtime.Rt.BulkExit();
                    Cs2Wasm.Runtime.Rt.ReleaseElements<T>(tmp, (int)elementCount);
                    Cs2Wasm.Runtime.Rt.Free(tmp, bytes);
                    return;
                }
                Cs2Wasm.Runtime.Rt.RetainElements<T>(src, (int)elementCount);
                Cs2Wasm.Runtime.Rt.ReleaseElements<T>(dst, (int)elementCount);
                SpanHelpers.Memmove(ref Unsafe.As<T, byte>(ref destination), ref Unsafe.As<T, byte>(ref source), elementCount * (nuint)sizeof(T));
            }
        }
    }
}
