// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Collections.Generic
{
    public partial class Comparer<T>
    {
        private static volatile Comparer<T>? defaultComparer;

        public static Comparer<T> Default
        {
            get
            {
                Comparer<T>? comparer = defaultComparer;
                if (comparer == null)
                {
                    // Do not use static constructor. Generic static constructors are problematic for Mono AOT.
                    Interlocked.CompareExchange(ref defaultComparer, CreateComparer(), null);
                    comparer = defaultComparer;
                }
                return comparer;
            }
        }

        // il2wasm: the comparer type is chosen when T is known (each instantiation is compiled on its own)
        private static Comparer<T> CreateComparer()
        {
            if (Il2Wasm.Runtime.Generics.Implements<T>("System.IComparable`1"))
                return (Comparer<T>)Il2Wasm.Runtime.Generics.New<T>("System.Collections.Generic.GenericComparer`1");
            if (Il2Wasm.Runtime.Generics.IsNullable<T>())
                return (Comparer<T>)Il2Wasm.Runtime.Generics.NewOfNullableArg<T>("System.Collections.Generic.NullableComparer`1");
            if (Il2Wasm.Runtime.Generics.IsEnum<T>())
                return (Comparer<T>)Il2Wasm.Runtime.Generics.New<T>("System.Collections.Generic.EnumComparer`1");
            return new ObjectComparer<T>();
        }
    }

    internal partial class EnumComparer<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int Compare(T x, T y) => JitHelpers.EnumCompareTo(x, y);
    }
}
