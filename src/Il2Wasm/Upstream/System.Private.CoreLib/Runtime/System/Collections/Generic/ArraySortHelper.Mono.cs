// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Collections.Generic
{
    internal interface IArraySortHelper<TKey>
    {
        void Sort(Span<TKey> keys, IComparer<TKey>? comparer);
        int BinarySearch(TKey[] keys, int index, int length, TKey value, IComparer<TKey>? comparer);
    }

    internal sealed partial class ArraySortHelper<T>
        : IArraySortHelper<T>
    {
        private static readonly IArraySortHelper<T> s_defaultArraySortHelper = CreateArraySortHelper();

        public static IArraySortHelper<T> Default => s_defaultArraySortHelper;

        private static IArraySortHelper<T> CreateArraySortHelper()
        {
            IArraySortHelper<T> defaultArraySortHelper;

            // il2wasm: chosen when T is known
            if (Il2Wasm.Runtime.Generics.Implements<T>("System.IComparable`1"))
            {
                defaultArraySortHelper = (IArraySortHelper<T>)Il2Wasm.Runtime.Generics.New<T>("System.Collections.Generic.GenericArraySortHelper`1");
            }
            else
            {
                defaultArraySortHelper = new ArraySortHelper<T>();
            }
            return defaultArraySortHelper;
        }
    }

    internal sealed partial class GenericArraySortHelper<T>
        : IArraySortHelper<T>
    {
    }

    internal interface IArraySortHelper<TKey, TValue>
    {
        void Sort(Span<TKey> keys, Span<TValue> values, IComparer<TKey>? comparer);
    }

    internal sealed partial class ArraySortHelper<TKey, TValue>
        : IArraySortHelper<TKey, TValue>
    {
        private static readonly IArraySortHelper<TKey, TValue> s_defaultArraySortHelper = CreateArraySortHelper();

        public static IArraySortHelper<TKey, TValue> Default => s_defaultArraySortHelper;

        private static IArraySortHelper<TKey, TValue> CreateArraySortHelper()
        {
            IArraySortHelper<TKey, TValue> defaultArraySortHelper;

            if (Il2Wasm.Runtime.Generics.Implements<TKey>("System.IComparable`1"))
            {
                defaultArraySortHelper = (IArraySortHelper<TKey, TValue>)Il2Wasm.Runtime.Generics.New<TKey, TValue>("System.Collections.Generic.GenericArraySortHelper`2");
            }
            else
            {
                defaultArraySortHelper = new ArraySortHelper<TKey, TValue>();
            }
            return defaultArraySortHelper;
        }
    }

    internal sealed partial class GenericArraySortHelper<TKey, TValue>
        : IArraySortHelper<TKey, TValue>
    {
    }
}
