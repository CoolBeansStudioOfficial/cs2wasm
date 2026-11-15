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
                Cs2Wasm.Runtime.Rt.RetainElements<T>(src, (int)elementCount);
                Cs2Wasm.Runtime.Rt.ReleaseElements<T>(dst, (int)elementCount);
                SpanHelpers.Memmove(ref Unsafe.As<T, byte>(ref destination), ref Unsafe.As<T, byte>(ref source), elementCount * (nuint)sizeof(T));
            }
        }
    }
}
