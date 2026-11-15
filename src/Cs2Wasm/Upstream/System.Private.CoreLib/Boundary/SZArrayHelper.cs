using System.Collections.Generic;

namespace System
{
    /// <summary>cs2wasm: arrays' implementations of the generic collection interfaces (as the CLR's SZArrayHelper):
    /// the compiler dispatches an interface call on a T[] to the method of the same name here, with the array first.</summary>
    internal static class SZArrayHelper
    {
        public static IEnumerator<T> GetEnumerator<T>(T[] a) => a.Length == 0 ? SZGenericArrayEnumerator<T>.Empty : new SZGenericArrayEnumerator<T>(a, a.Length);
        public static int get_Count<T>(T[] a) => a.Length;
        public static bool get_IsReadOnly<T>(T[] a) => true;
        public static T get_Item<T>(T[] a, int index) => a[index];
        public static void set_Item<T>(T[] a, int index, T value) => a[index] = value;
        public static bool Contains<T>(T[] a, T value) => Array.IndexOf(a, value, 0, a.Length) >= 0;
        public static int IndexOf<T>(T[] a, T value) => Array.IndexOf(a, value, 0, a.Length);
        public static void CopyTo<T>(T[] a, T[] array, int index) => Array.Copy(a, 0, array, index, a.Length);
        public static void Add<T>(T[] a, T value) => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection);
        public static void Insert<T>(T[] a, int index, T value) => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection);
        public static bool Remove<T>(T[] a, T value) { ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection); return false; }
        public static void RemoveAt<T>(T[] a, int index) => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection);
        public static void Clear<T>(T[] a) => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ReadOnlyCollection);
    }
}
