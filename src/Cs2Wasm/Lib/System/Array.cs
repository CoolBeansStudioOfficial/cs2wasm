#pragma warning disable CS0436
using System.Collections;
using System.Collections.Generic;
using Cs2Wasm.Runtime;

namespace System
{
    /// <summary>Arrays: header, length at 12, elements from 16. The compiler emits element access inline.</summary>
    public abstract class Array
    {
        public int Length => Mem.LoadI32(Rt.AddressOf(this) + 12);
        public long LongLength => Length;
        public int Rank => 1;

        public static T[] Empty<T>() => EmptyArray<T>.Value;

        public static void Copy(Array sourceArray, Array destinationArray, int length) => Copy(sourceArray, 0, destinationArray, 0, length);

        public static void Copy(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length)
        {
            if (sourceArray == null) throw new ArgumentNullException("sourceArray");
            if (destinationArray == null) throw new ArgumentNullException("destinationArray");
            if (Rt.TypeId(sourceArray) != Rt.TypeId(destinationArray)) throw new ArrayTypeMismatchException("Source array type cannot be assigned to destination array type.");
            if (length < 0) throw new ArgumentOutOfRangeException("length", "Non-negative number required.");
            if (sourceIndex < 0 || destinationIndex < 0 || sourceIndex > sourceArray.Length - length || destinationIndex > destinationArray.Length - length)
                throw new ArgumentException("Source array was not long enough. Check the source index, length, and the array's lower bounds.");
            CopyCore(sourceArray, sourceIndex, destinationArray, destinationIndex, length);
        }

        /// <summary>Element-size and reference aware copy (same array type, checked by the caller).</summary>
        internal static void CopyCore(Array src, int si, Array dst, int di, int length)
        {
            int size = ElementSize(src);
            ArrayHelpers.CopyElements(src, si, dst, di, length, size);
        }

        [Intrinsic] internal static extern int ElementSize(Array a);
        [Intrinsic] internal static extern bool HasCountedElements(Array a);

        public static int IndexOf<T>(T[] array, T value) => IndexOf(array, value, 0, array.Length);
        public static int IndexOf<T>(T[] array, T value, int startIndex) => IndexOf(array, value, startIndex, array.Length - startIndex);
        public static int IndexOf<T>(T[] array, T value, int startIndex, int count)
        {
            if (array == null) throw new ArgumentNullException("array");
            var c = EqualityComparer<T>.Default;
            for (int i = startIndex; i < startIndex + count; i++) if (c.Equals(array[i], value)) return i;
            return -1;
        }
        public static int LastIndexOf<T>(T[] array, T value)
        {
            var c = EqualityComparer<T>.Default;
            for (int i = array.Length - 1; i >= 0; i--) if (c.Equals(array[i], value)) return i;
            return -1;
        }
        public static bool Exists<T>(T[] array, Predicate<T> match) => FindIndex(array, match) >= 0;
        public static T Find<T>(T[] array, Predicate<T> match)
        {
            for (int i = 0; i < array.Length; i++) if (match(array[i])) return array[i];
            return default;
        }
        public static int FindIndex<T>(T[] array, Predicate<T> match)
        {
            for (int i = 0; i < array.Length; i++) if (match(array[i])) return i;
            return -1;
        }
        public static T[] FindAll<T>(T[] array, Predicate<T> match)
        {
            var l = new List<T>();
            for (int i = 0; i < array.Length; i++) if (match(array[i])) l.Add(array[i]);
            return l.ToArray();
        }
        public static bool TrueForAll<T>(T[] array, Predicate<T> match)
        {
            for (int i = 0; i < array.Length; i++) if (!match(array[i])) return false;
            return true;
        }
        public static void ForEach<T>(T[] array, Action<T> action)
        {
            for (int i = 0; i < array.Length; i++) action(array[i]);
        }
        public static TOutput[] ConvertAll<TInput, TOutput>(TInput[] array, Converter<TInput, TOutput> converter)
        {
            var r = new TOutput[array.Length];
            for (int i = 0; i < array.Length; i++) r[i] = converter(array[i]);
            return r;
        }

        public static void Reverse<T>(T[] array) => Reverse(array, 0, array.Length);
        public static void Reverse<T>(T[] array, int index, int length)
        {
            int i = index, j = index + length - 1;
            while (i < j) { T t = array[i]; array[i] = array[j]; array[j] = t; i++; j--; }
        }
        public static void Fill<T>(T[] array, T value)
        {
            for (int i = 0; i < array.Length; i++) array[i] = value;
        }
        public static void Fill<T>(T[] array, T value, int startIndex, int count)
        {
            for (int i = startIndex; i < startIndex + count; i++) array[i] = value;
        }
        public static void Clear(Array array) => Clear(array, 0, array.Length);
        public static void Clear(Array array, int index, int length)
        {
            if (array == null) throw new ArgumentNullException("array");
            if (index < 0 || length < 0 || index > array.Length - length) throw new IndexOutOfRangeException();
            ArrayHelpers.ClearElements(array, index, length, ElementSize(array));
        }
        public static void Resize<T>(ref T[] array, int newSize)
        {
            if (newSize < 0) throw new ArgumentOutOfRangeException("newSize", "Non-negative number required.");
            T[] larray = array;
            if (larray == null) { array = new T[newSize]; return; }
            if (larray.Length != newSize)
            {
                T[] newArray = new T[newSize];
                int n = larray.Length > newSize ? newSize : larray.Length;
                for (int i = 0; i < n; i++) newArray[i] = larray[i];
                array = newArray;
            }
        }

        public static void Sort<T>(T[] array) => Sort(array, 0, array.Length, null);
        public static void Sort<T>(T[] array, IComparer<T> comparer) => Sort(array, 0, array.Length, comparer);
        public static void Sort<T>(T[] array, Comparison<T> comparison) => ArraySortHelper<T>.Sort(array, 0, array.Length, comparison);
        public static void Sort<T>(T[] array, int index, int length, IComparer<T> comparer)
        {
            if (array == null) throw new ArgumentNullException("array");
            var c = comparer ?? Comparer<T>.Default;
            ArraySortHelper<T>.Sort(array, index, length, c.Compare);
        }
        public static int BinarySearch<T>(T[] array, T value)
        {
            var c = Comparer<T>.Default;
            int lo = 0, hi = array.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int r = c.Compare(array[mid], value);
                if (r == 0) return mid;
                if (r < 0) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }
        public object Clone()
        {
            int size = ElementSize(this);
            int n = Length;
            int a = Rt.AllocArray(n, size, Rt.TypeId(this));
            Array copy = Rt.Take<Array>(a);
            ArrayHelpers.CopyElements(this, 0, copy, 0, n, size);
            return copy;
        }
    }

    internal static class EmptyArray<T>
    {
        internal static readonly T[] Value = new T[0];
    }

    internal static class ArrayHelpers
    {
        [Intrinsic] internal static extern void CopyElements(Array src, int si, Array dst, int di, int length, int size);
        [Intrinsic] internal static extern void ClearElements(Array a, int index, int length, int size);
    }

    /// <summary>Introspective sort, as .NET's ArraySortHelper (same algorithm, so unstable orderings match).</summary>
    internal static class ArraySortHelper<T>
    {
        const int IntrosortSizeThreshold = 16;

        public static void Sort(T[] keys, int index, int length, Comparison<T> comparer)
        {
            if (length > 1) IntroSort(keys, index, length + index - 1, 2 * (Log2(length) + 1), comparer);
        }

        static int Log2(int v) => 31 - Rt.I32Clz(v);

        static void SwapIfGreater(T[] keys, Comparison<T> comparer, int i, int j)
        {
            if (comparer(keys[i], keys[j]) > 0) { T key = keys[i]; keys[i] = keys[j]; keys[j] = key; }
        }

        static void Swap(T[] a, int i, int j) { T t = a[i]; a[i] = a[j]; a[j] = t; }

        static void IntroSort(T[] keys, int lo, int hi, int depthLimit, Comparison<T> comparer)
        {
            while (hi > lo)
            {
                int partitionSize = hi - lo + 1;
                if (partitionSize <= IntrosortSizeThreshold)
                {
                    if (partitionSize == 2) { SwapIfGreater(keys, comparer, lo, hi); return; }
                    if (partitionSize == 3) { SwapIfGreater(keys, comparer, lo, hi - 1); SwapIfGreater(keys, comparer, lo, hi); SwapIfGreater(keys, comparer, hi - 1, hi); return; }
                    InsertionSort(keys, lo, hi, comparer);
                    return;
                }
                if (depthLimit == 0) { HeapSort(keys, lo, hi, comparer); return; }
                depthLimit--;
                int p = PickPivotAndPartition(keys, lo, hi, comparer);
                IntroSort(keys, p + 1, hi, depthLimit, comparer);
                hi = p - 1;
            }
        }

        static int PickPivotAndPartition(T[] keys, int lo, int hi, Comparison<T> comparer)
        {
            int middle = lo + ((hi - lo) >> 1);
            SwapIfGreater(keys, comparer, lo, middle);
            SwapIfGreater(keys, comparer, lo, hi);
            SwapIfGreater(keys, comparer, middle, hi);
            T pivot = keys[middle];
            Swap(keys, middle, hi - 1);
            int left = lo, right = hi - 1;
            while (left < right)
            {
                while (comparer(keys[++left], pivot) < 0) ;
                while (comparer(pivot, keys[--right]) < 0) ;
                if (left >= right) break;
                Swap(keys, left, right);
            }
            if (left != hi - 1) Swap(keys, left, hi - 1);
            return left;
        }

        static void HeapSort(T[] keys, int lo, int hi, Comparison<T> comparer)
        {
            int n = hi - lo + 1;
            for (int i = n >> 1; i >= 1; i--) DownHeap(keys, i, n, lo, comparer);
            for (int i = n; i > 1; i--) { Swap(keys, lo, lo + i - 1); DownHeap(keys, 1, i - 1, lo, comparer); }
        }

        static void DownHeap(T[] keys, int i, int n, int lo, Comparison<T> comparer)
        {
            T d = keys[lo + i - 1];
            while (i <= n >> 1)
            {
                int child = 2 * i;
                if (child < n && comparer(keys[lo + child - 1], keys[lo + child]) < 0) child++;
                if (!(comparer(d, keys[lo + child - 1]) < 0)) break;
                keys[lo + i - 1] = keys[lo + child - 1];
                i = child;
            }
            keys[lo + i - 1] = d;
        }

        static void InsertionSort(T[] keys, int lo, int hi, Comparison<T> comparer)
        {
            for (int i = lo; i < hi; i++)
            {
                int j = i;
                T t = keys[i + 1];
                while (j >= lo && comparer(t, keys[j]) < 0) { keys[j + 1] = keys[j]; j--; }
                keys[j + 1] = t;
            }
        }
    }
}
