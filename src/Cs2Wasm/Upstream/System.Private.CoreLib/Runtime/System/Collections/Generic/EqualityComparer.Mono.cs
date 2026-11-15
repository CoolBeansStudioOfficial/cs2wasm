// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Collections.Generic
{
    public partial class EqualityComparer<T>
    {
        private static volatile EqualityComparer<T>? defaultComparer;

        public static EqualityComparer<T> Default
        {
            [MethodImplAttribute(MethodImplOptions.AggressiveInlining)]
            get
            {
                EqualityComparer<T>? comparer = defaultComparer;
                if (comparer == null)
                {
                    // Do not use static constructor. Generic static constructors are problematic for Mono AOT.
                    Interlocked.CompareExchange(ref defaultComparer, CreateComparer(), null);
                    comparer = defaultComparer;
                }
                return comparer;
            }
        }

        // cs2wasm: the comparer type is chosen when T is known (each instantiation is compiled on its own)
        private static EqualityComparer<T> CreateComparer()
        {
            if (typeof(T) == typeof(string))
                return (EqualityComparer<T>)(object)new StringEqualityComparer();
            if (Cs2Wasm.Runtime.Generics.Implements<T>("System.IEquatable`1"))
                return (EqualityComparer<T>)Cs2Wasm.Runtime.Generics.New<T>("System.Collections.Generic.GenericEqualityComparer`1");
            if (Cs2Wasm.Runtime.Generics.IsNullable<T>())
                return (EqualityComparer<T>)Cs2Wasm.Runtime.Generics.NewOfNullableArg<T>("System.Collections.Generic.NullableEqualityComparer`1");
            if (Cs2Wasm.Runtime.Generics.IsEnum<T>())
                return (EqualityComparer<T>)Cs2Wasm.Runtime.Generics.New<T>("System.Collections.Generic.EnumEqualityComparer`1");
            return new ObjectEqualityComparer<T>();
        }
    }

    public partial class EnumEqualityComparer<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(T x, T y) => JitHelpers.EnumEquals(x, y);
    }
}
