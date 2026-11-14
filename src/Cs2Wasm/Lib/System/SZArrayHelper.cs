#pragma warning disable CS0436
using System.Collections;
using System.Collections.Generic;

namespace System
{
    /// <summary>Arrays' implementations of the collection interfaces (the CLR's SZArrayHelper). `a` is the array.</summary>
    internal static class SZArrayHelper
    {
        public static IEnumerator<T> GetEnumerator<T>(T[] a) => new ArrayEnumerator<T>(a);
        public static int get_Count<T>(T[] a) => a.Length;
        public static bool get_IsReadOnly<T>(T[] a) => false;
        public static T get_Item<T>(T[] a, int index) => a[index];
        public static void set_Item<T>(T[] a, int index, T value) => a[index] = value;
        public static bool Contains<T>(T[] a, T value) => Array.IndexOf(a, value) >= 0;
        public static int IndexOf<T>(T[] a, T value) => Array.IndexOf(a, value);
        public static void CopyTo<T>(T[] a, T[] array, int index)
        {
            for (int i = 0; i < a.Length; i++) array[index + i] = a[i];
        }
        public static void Add<T>(T[] a, T value) => throw new NotSupportedException("Collection was of a fixed size.");
        public static void Insert<T>(T[] a, int index, T value) => throw new NotSupportedException("Collection was of a fixed size.");
        public static bool Remove<T>(T[] a, T value) => throw new NotSupportedException("Collection was of a fixed size.");
        public static void RemoveAt<T>(T[] a, int index) => throw new NotSupportedException("Collection was of a fixed size.");
        public static void Clear<T>(T[] a) => throw new NotSupportedException("Collection was of a fixed size.");
    }

    internal sealed class ArrayEnumerator<T> : IEnumerator<T>
    {
        readonly T[] _a; int _i = -1;
        public ArrayEnumerator(T[] a) { _a = a; }
        public bool MoveNext() { if (_i < _a.Length) _i++; return _i < _a.Length; }
        public T Current
        {
            get
            {
                if (_i < 0) throw new InvalidOperationException("Enumeration has not started. Call MoveNext.");
                if (_i >= _a.Length) throw new InvalidOperationException("Enumeration already finished.");
                return _a[_i];
            }
        }
        object IEnumerator.Current => Current;
        public void Reset() { _i = -1; }
        public void Dispose() { }
    }
}
