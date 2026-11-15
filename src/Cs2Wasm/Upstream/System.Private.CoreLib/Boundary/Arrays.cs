using System;
using System.Runtime.CompilerServices;

namespace Cs2Wasm.Runtime
{
    /// <summary>What the compiler knows of an array at run time (from its type id), for Array's runtime half.</summary>
    internal static unsafe class Cs2WasmArrays
    {
        [Intrinsic] internal static extern int RankOf(Array a);
        [Intrinsic] internal static extern int ElementSize(Array a);
        [Intrinsic] internal static extern bool HasCountedElements(Array a);
        /// <summary>Copies elements of arrays of one element type, counting references (overlap allowed).</summary>
        [Intrinsic] internal static extern void CopyElements(Array src, int si, Array dst, int di, int length, int size);
        [Intrinsic] internal static extern void ClearElements(Array a, int index, int length, int size);

        /// <summary>The address of element 0.</summary>
        internal static int Data(Array a)
        {
            int rank = RankOf(a);
            return Rt.AddressOf(a) + (rank == 1 ? 16 : (16 + 4 * rank + 7) / 8 * 8);
        }

        /// <summary>The object a QCall handle points at (a slot holding the reference).</summary>
        internal static T Handle<T>(ObjectHandleOnStack h) where T : class => Rt.Borrow<T>(Mem.LoadI32(Unsafe.As<ObjectHandleOnStack, int>(ref h)));
    }
}
