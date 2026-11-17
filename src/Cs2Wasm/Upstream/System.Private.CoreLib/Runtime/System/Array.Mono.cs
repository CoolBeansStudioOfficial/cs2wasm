// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's arrays (header, length at 12, elements from 16;
// other ranks: the lengths from 16, elements after them, 8-aligned).

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    public partial class Array
    {
        // cs2wasm's array layout: after the object header the length, then the elements from 16 (other ranks keep
        // their lengths there and start their elements after them: Cs2WasmArrays.Data)
        [StructLayout(LayoutKind.Sequential)]
        internal sealed class RawData
        {
            public uint Count;
            public byte Data;
        }

        public int Length
        {
            get => (int)Unsafe.As<RawData>(this).Count;
        }

        // This could return a length greater than int.MaxValue
        internal nuint NativeLength
        {
            get => (nuint)Unsafe.As<RawData>(this).Count;
        }

        public long LongLength
        {
            get
            {
                long length = GetLength(0);

                for (int i = 1; i < Rank; i++)
                {
                    length *= GetLength(i);
                }
                return length;
            }
        }

        public int Rank
        {
            get => Cs2Wasm.Runtime.Cs2WasmArrays.RankOf(this);
        }

        public static unsafe void Clear(Array array)
        {
            if (array == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);

            ref byte ptr = ref MemoryMarshal.GetArrayDataReference(array);
            nuint byteLength = array.NativeLength * (nuint)(uint)array.GetElementSize() /* force zero-extension */;

            // cs2wasm: the references the elements hold are released (zeros alone would leave their objects for ever)
            if (Cs2Wasm.Runtime.Cs2WasmArrays.ClearCounted(array, 0, (int)array.NativeLength)) return;
            if (RuntimeHelpers.ObjectHasReferences(array))
                SpanHelpers.ClearWithReferences(ref Unsafe.As<byte, IntPtr>(ref ptr), byteLength / (uint)sizeof(IntPtr));
            else
                SpanHelpers.ClearWithoutReferences(ref ptr, byteLength);
        }

        public static unsafe void Clear(Array array, int index, int length)
        {
            if (array == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);

            int lowerBound = array.GetLowerBound(0);
            int elementSize = array.GetElementSize();
            nuint numComponents = array.NativeLength;

            int offset = index - lowerBound;

            if (index < lowerBound || offset < 0 || length < 0 || (uint)(offset + length) > numComponents)
                ThrowHelper.ThrowIndexOutOfRangeException();

            ref byte ptr = ref Unsafe.AddByteOffset(ref MemoryMarshal.GetArrayDataReference(array), (uint)offset * (nuint)elementSize);
            nuint byteLength = (uint)length * (nuint)elementSize;

            if (Cs2Wasm.Runtime.Cs2WasmArrays.ClearCounted(array, offset, length)) return;
            if (RuntimeHelpers.ObjectHasReferences(array))
                SpanHelpers.ClearWithReferences(ref Unsafe.As<byte, IntPtr>(ref ptr), byteLength / (uint)sizeof(IntPtr));
            else
                SpanHelpers.ClearWithoutReferences(ref ptr, byteLength);
        }

        public static void Copy(Array sourceArray, Array destinationArray, int length)
        {
            ArgumentNullException.ThrowIfNull(sourceArray);
            ArgumentNullException.ThrowIfNull(destinationArray);

            Copy(sourceArray, sourceArray.GetLowerBound(0), destinationArray,
                destinationArray.GetLowerBound(0), length);
        }

        public static void Copy(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length)
        {
            CopyImpl(sourceArray, sourceIndex, destinationArray, destinationIndex, length, false);
        }

        public static void ConstrainedCopy(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length)
        {
            CopyImpl(sourceArray, sourceIndex, destinationArray, destinationIndex, length, true);
        }

        private static void CopyImpl(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length, bool reliable)
        {
            ArgumentNullException.ThrowIfNull(sourceArray);
            ArgumentNullException.ThrowIfNull(destinationArray);

            ArgumentOutOfRangeException.ThrowIfNegative(length);

            if (sourceArray.Rank != destinationArray.Rank)
                throw new RankException(SR.Rank_MustMatch);

            if (sourceIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex), SR.ArgumentOutOfRange_NeedNonNegNum);

            if (destinationIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(destinationIndex), SR.ArgumentOutOfRange_NeedNonNegNum);

            var src = sourceArray;
            var dst = destinationArray;
            if (FastCopy(ObjectHandleOnStack.Create(ref src), sourceIndex, ObjectHandleOnStack.Create(ref dst), destinationIndex, length))
                return;

            CopySlow(sourceArray, sourceIndex, destinationArray, destinationIndex, length, reliable);
        }

        // cs2wasm: the copies FastCopy does not do, by element type id (Cs2WasmArrays.GetBoxed/SetBoxed): references
        // with a check per element, boxing into and unboxing out of reference arrays. Primitive widening (int[] into
        // long[]) is not supported yet.
        private static void CopySlow(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length, bool reliable)
        {
            int source_pos = sourceIndex - sourceArray.GetLowerBound(0);
            int dest_pos = destinationIndex - destinationArray.GetLowerBound(0);

            if (source_pos < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex), SR.ArgumentOutOfRange_ArrayLB);

            if (dest_pos < 0)
                throw new ArgumentOutOfRangeException(nameof(destinationIndex), SR.ArgumentOutOfRange_ArrayLB);

            // re-ordered to avoid possible integer overflow
            if (source_pos > sourceArray.Length - length)
                throw new ArgumentException(SR.Arg_LongerThanSrcArray, nameof(sourceArray));

            if (dest_pos > destinationArray.Length - length)
                throw new ArgumentException(SR.Arg_LongerThanDestArray, nameof(destinationArray));

            int se = Cs2Wasm.Runtime.TypeInfo.ElementId(Cs2Wasm.Runtime.Rt.TypeId(sourceArray));
            int de = Cs2Wasm.Runtime.TypeInfo.ElementId(Cs2Wasm.Runtime.Rt.TypeId(destinationArray));
            bool sv = (Cs2Wasm.Runtime.TypeInfo.Flags(se) & 1) != 0, dv = (Cs2Wasm.Runtime.TypeInfo.Flags(de) & 1) != 0;
            if (sv && dv)
            {
                if (Cs2Wasm.Runtime.Cs2WasmArrays.PrimitiveKind(se) != 0 && Cs2Wasm.Runtime.Cs2WasmArrays.PrimitiveKind(de) != 0 && !reliable)
                    throw Cs2Wasm.Runtime.TypeInfo.NotYet("Array.Copy with primitive widening");
                throw new ArrayTypeMismatchException(SR.ArrayTypeMismatch_CantAssignType);
            }
            if (sv ? !Cs2Wasm.Runtime.TypeInfo.IsAssignable(de, se)
                   : !dv && !Cs2Wasm.Runtime.TypeInfo.IsAssignable(de, se) && !Cs2Wasm.Runtime.TypeInfo.IsAssignable(se, de)
                     && (Cs2Wasm.Runtime.TypeInfo.Flags(se) & 8) == 0 && (Cs2Wasm.Runtime.TypeInfo.Flags(de) & 8) == 0
                     || dv && !Cs2Wasm.Runtime.TypeInfo.IsAssignable(se, de))
                throw new ArrayTypeMismatchException(SR.ArrayTypeMismatch_CantAssignType);

            bool forward = !ReferenceEquals(sourceArray, destinationArray) || source_pos > dest_pos;
            for (int k = 0; k < length; k++)
            {
                int i = forward ? k : length - 1 - k;
                object? v = Cs2Wasm.Runtime.Cs2WasmArrays.GetBoxed(sourceArray, source_pos + i);
                if (dv ? v == null : v != null && !Cs2Wasm.Runtime.TypeInfo.IsAssignable(de, Cs2Wasm.Runtime.Rt.TypeId(v)))
                    throw new InvalidCastException(SR.InvalidCast_DownCastArrayElement);
                Cs2Wasm.Runtime.Cs2WasmArrays.SetBoxed(destinationArray, dest_pos + i, v);
            }
        }

        private static ArrayTypeMismatchException CreateArrayTypeMismatchException()
        {
            return new ArrayTypeMismatchException();
        }

        private static bool CanAssignArrayElement(Type source, Type target)
        {
            if (!target.IsValueType && !target.IsPointer)
            {
                if (!source.IsValueType && !source.IsPointer)
                {
                    // Reference to reference copy
                    return
                        source.IsInterface || target.IsInterface ||
                        source.IsAssignableFrom(target) || target.IsAssignableFrom(source);
                }
                else
                {
                    // Value to reference copy
                    if (source.IsPointer)
                        return false;
                    return target.IsAssignableFrom(source);
                }
            }
            else
            {
                if (source.IsEquivalentTo(target))
                {
                    return true;
                }
                else if (source.IsPointer && target.IsPointer)
                {
                    return target.IsAssignableFrom(source);
                }
                else if (source.IsPrimitive && target.IsPrimitive)
                {
                    // Allow primitive type widening
                    return CanChangePrimitive(ObjectHandleOnStack.Create(ref source), ObjectHandleOnStack.Create(ref target), false);
                }
                else if (!source.IsValueType && !source.IsPointer)
                {
                    // Source is base class or interface of destination type
                    if (target.IsPointer)
                        return false;
                    return source.IsAssignableFrom(target);
                }
            }

            return false;
        }

        private static unsafe Array InternalCreate(RuntimeType elementType, int rank, int* lengths, int* lowerBounds)
        {
            Array? array = null;
            InternalCreate(ref array, elementType._impl.Value, rank, lengths, lowerBounds);
            GC.KeepAlive(elementType);
            return array!;
        }

        private static unsafe void InternalCreate(ref Array? result, IntPtr elementType, int rank, int* lengths, int* lowerBounds)
        {
            // cs2wasm: one-dimensional arrays of a type the program makes arrays of (TypeInfo.ArrayOf)
            int e = (int)elementType;
            int at = rank == 1 && (lowerBounds == null || lowerBounds[0] == 0) ? Cs2Wasm.Runtime.TypeInfo.ArrayOf(e) : -1;
            if (at < 0) throw Cs2Wasm.Runtime.TypeInfo.NotYet("Array.CreateInstance of an array type the program never makes");
            int size = (Cs2Wasm.Runtime.TypeInfo.Flags(e) & 1) != 0 ? Cs2Wasm.Runtime.TypeInfo.Size(e) : 4;
            result = Cs2Wasm.Runtime.Rt.Take<Array>(Cs2Wasm.Runtime.Rt.AllocArray(lengths[0], size, at));
        }

        private static unsafe Array InternalCreateFromArrayType(Type arrayType, int rank, int* pLengths, int* pLowerBounds)
        {
            return InternalCreate((arrayType.GetElementType() as RuntimeType)!, rank, pLengths, pLowerBounds);
        }

        private unsafe nint GetFlattenedIndex(int rawIndex)
        {
            // Checked by the caller
            Debug.Assert(Rank == 1);

            int index = rawIndex - GetLowerBound(0);
            int length = GetLength(0);

            if ((uint)index >= (uint)length)
                ThrowHelper.ThrowIndexOutOfRangeException();

            Debug.Assert((uint)index < (nuint)LongLength);
            return index;
        }

        private unsafe nint GetFlattenedIndex(ReadOnlySpan<int> indices)
        {
            // Checked by the caller
            Debug.Assert(indices.Length == Rank);

            nint flattenedIndex = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i] - GetLowerBound(i);
                int length = GetLength(i);
                if ((uint)index >= (uint)length)
                    ThrowHelper.ThrowIndexOutOfRangeException();
                flattenedIndex = (length * flattenedIndex) + index;
            }
            Debug.Assert((nuint)flattenedIndex < (nuint)LongLength);
            return flattenedIndex;
        }

        internal object? InternalGetValue(nint index)
        {
            if (GetType().GetElementType()!.IsPointer)
                throw new NotSupportedException(SR.NotSupported_Type);

            Array self = this;
            object? res = null;
            GetValueImpl(ObjectHandleOnStack.Create(ref self), ObjectHandleOnStack.Create(ref res), (int)index);
            return res;
        }

        internal void InternalSetValue(object? value, nint index)
        {
            if (GetType().GetElementType()!.IsPointer)
                throw new NotSupportedException(SR.NotSupported_Type);

            Array self = this;
            SetValueImpl(ObjectHandleOnStack.Create(ref self), ObjectHandleOnStack.Create(ref value), (int)index);
        }

        public void Initialize()
        {
            object arr = this;
            InitializeInternal(ObjectHandleOnStack.Create(ref arr));
        }

        private static int IndexOfImpl<T>(T[] array, T value, int startIndex, int count)
        {
            return EqualityComparer<T>.Default.IndexOf(array, value, startIndex, count);
        }

        private static int LastIndexOfImpl<T>(T[] array, T value, int startIndex, int count)
        {
            return EqualityComparer<T>.Default.LastIndexOf(array, value, startIndex, count);
        }

        public int GetUpperBound(int dimension)
        {
            return GetLowerBound(dimension) + GetLength(dimension) - 1;
        }

        internal CorElementType GetCorElementTypeOfElementType()
        {
            object arr = this;
            return GetCorElementTypeOfElementTypeInternal(ObjectHandleOnStack.Create(ref arr));
        }

        private bool IsValueOfElementType(object value)
        {
            object arr = this;
            return IsValueOfElementTypeInternal(ObjectHandleOnStack.Create(ref arr), ObjectHandleOnStack.Create(ref value));
        }

        [Intrinsic] // when dimension is `0` constant
        public int GetLength(int dimension)
        {
            object arr = this;
            return GetLengthInternal(ObjectHandleOnStack.Create(ref arr), dimension);
        }

        [Intrinsic] // when dimension is `0` constant
        public int GetLowerBound(int dimension)
        {
            object arr = this;
            return GetLowerBoundInternal(ObjectHandleOnStack.Create(ref arr), dimension);
        }

        [Intrinsic]
#pragma warning disable CA1822 // Mark members as static
        internal int GetElementSize() => Cs2Wasm.Runtime.Cs2WasmArrays.ElementSize(this);
#pragma warning restore

        // cs2wasm: from the array's type (the module's type tables), as Mono reads its class: an enum is its underlying
        // type, the primitives their own element types, any other value type or reference VALUETYPE or CLASS
        private static CorElementType GetCorElementTypeOfElementTypeInternal(ObjectHandleOnStack arr)
        {
            Type t = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(arr).GetType().GetElementType()!;
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t == typeof(bool)) return CorElementType.ELEMENT_TYPE_BOOLEAN;
            if (t == typeof(char)) return CorElementType.ELEMENT_TYPE_CHAR;
            if (t == typeof(sbyte)) return CorElementType.ELEMENT_TYPE_I1;
            if (t == typeof(byte)) return CorElementType.ELEMENT_TYPE_U1;
            if (t == typeof(short)) return CorElementType.ELEMENT_TYPE_I2;
            if (t == typeof(ushort)) return CorElementType.ELEMENT_TYPE_U2;
            if (t == typeof(int)) return CorElementType.ELEMENT_TYPE_I4;
            if (t == typeof(uint)) return CorElementType.ELEMENT_TYPE_U4;
            if (t == typeof(long)) return CorElementType.ELEMENT_TYPE_I8;
            if (t == typeof(ulong)) return CorElementType.ELEMENT_TYPE_U8;
            if (t == typeof(float)) return CorElementType.ELEMENT_TYPE_R4;
            if (t == typeof(double)) return CorElementType.ELEMENT_TYPE_R8;
            if (t == typeof(nint)) return CorElementType.ELEMENT_TYPE_I;
            if (t == typeof(nuint)) return CorElementType.ELEMENT_TYPE_U;
            return t.IsValueType ? CorElementType.ELEMENT_TYPE_VALUETYPE : CorElementType.ELEMENT_TYPE_CLASS;
        }

        // (Mono: the value's class is the element class)
        private static bool IsValueOfElementTypeInternal(ObjectHandleOnStack arr, ObjectHandleOnStack obj)
        {
            object? value = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<object>(obj);
            return value != null && value.GetType() == Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(arr).GetType().GetElementType();
        }

        private static bool CanChangePrimitive(ObjectHandleOnStack srcType, ObjectHandleOnStack dstType, bool reliable)
        => false;

        internal static bool FastCopy(ObjectHandleOnStack source, int source_idx, ObjectHandleOnStack dest, int dest_idx, int length)
        {
            // cs2wasm: arrays of one type, or both of counted references (covariance is not checked yet)
            Array src = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(source), dst = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(dest);
            int size = Cs2Wasm.Runtime.Cs2WasmArrays.ElementSize(src);
            bool same = Cs2Wasm.Runtime.Rt.TypeId(src) == Cs2Wasm.Runtime.Rt.TypeId(dst)
                || (Cs2Wasm.Runtime.Cs2WasmArrays.HasCountedElements(src) && Cs2Wasm.Runtime.Cs2WasmArrays.HasCountedElements(dst) && size == Cs2Wasm.Runtime.Cs2WasmArrays.ElementSize(dst) && size == 4);
            if (!same && !Cs2Wasm.Runtime.Cs2WasmArrays.HasCountedElements(src) && !Cs2Wasm.Runtime.Cs2WasmArrays.HasCountedElements(dst)
                && size == Cs2Wasm.Runtime.Cs2WasmArrays.ElementSize(dst) && Cs2Wasm.Runtime.Cs2WasmArrays.RankOf(src) == Cs2Wasm.Runtime.Cs2WasmArrays.RankOf(dst))
            {
                // an enum and its underlying type (or two enums over it) share their elements' bits
                int se = Cs2Wasm.Runtime.TypeInfo.ElementId(Cs2Wasm.Runtime.Rt.TypeId(src)), de = Cs2Wasm.Runtime.TypeInfo.ElementId(Cs2Wasm.Runtime.Rt.TypeId(dst));
                int sk = Cs2Wasm.Runtime.Cs2WasmArrays.PrimitiveKind(se);
                same = sk != 0 && sk == Cs2Wasm.Runtime.Cs2WasmArrays.PrimitiveKind(de);
            }
            if (!same) return false;
            Cs2Wasm.Runtime.Cs2WasmArrays.CopyElements(src, source_idx, dst, dest_idx, length, size);
            return true;
        }

        private static int GetLengthInternal(ObjectHandleOnStack arr, int dimension)
        {
            Array a = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(arr);
            return Cs2Wasm.Runtime.Cs2WasmArrays.RankOf(a) == 1 ? a.Length : Cs2Wasm.Runtime.Mem.LoadI32(Cs2Wasm.Runtime.Rt.AddressOf(a) + 16 + 4 * dimension);
        }

        private static int GetLowerBoundInternal(ObjectHandleOnStack arr, int dimension)
        => 0;

        // CAUTION! No bounds checking!
        private static unsafe void GetGenericValue_icall<T>(ObjectHandleOnStack self, int pos, out T value)
        {
            Array a = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(self);
            value = Unsafe.AsRef<T>((void*)(Cs2Wasm.Runtime.Cs2WasmArrays.Data(a) + pos * Unsafe.SizeOf<T>()));
        }

        // CAUTION! No bounds checking!
        private static void GetValueImpl(ObjectHandleOnStack arr, ObjectHandleOnStack res, int pos)
        {
            Array a = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(arr);
            Cs2Wasm.Runtime.TypeInfo.Set(res, Cs2Wasm.Runtime.Cs2WasmArrays.GetBoxed(a, pos));
        }

        // CAUTION! No bounds checking!
        private static unsafe void SetGenericValue_icall<T>(ObjectHandleOnStack arr, int pos, ref T value)
        {
            Array a = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(arr);
            Unsafe.AsRef<T>((void*)(Cs2Wasm.Runtime.Cs2WasmArrays.Data(a) + pos * Unsafe.SizeOf<T>())) = value;
        }

        [Intrinsic]
        private void GetGenericValueImpl<T>(int pos, out T value)
        {
            Array self = this;
            GetGenericValue_icall(ObjectHandleOnStack.Create(ref self), pos, out value);
        }

        [Intrinsic]
        private void SetGenericValueImpl<T>(int pos, ref T value)
        {
            Array self = this;
            SetGenericValue_icall(ObjectHandleOnStack.Create(ref self), pos, ref value);
        }

        // CAUTION! No bounds checking!
        private static void SetValueImpl(ObjectHandleOnStack arr, ObjectHandleOnStack value, int pos)
        {
            Array a = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(arr);
            Cs2Wasm.Runtime.Cs2WasmArrays.SetBoxed(a, pos, Cs2Wasm.Runtime.Cs2WasmArrays.Handle<object>(value));
        }

        private static void InitializeInternal(ObjectHandleOnStack arr)
        {
        }

        // CAUTION! No bounds checking!
        private static void SetValueRelaxedImpl(ObjectHandleOnStack arr, ObjectHandleOnStack value, int pos)
        {
            Array a = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Array>(arr);
            Cs2Wasm.Runtime.Cs2WasmArrays.SetBoxed(a, pos, Cs2Wasm.Runtime.Cs2WasmArrays.Handle<object>(value));
        }

#pragma warning disable CA1822
        /*
         * These methods are used to implement the implicit generic interfaces
         * implemented by arrays in NET 2.0.
         * Only make those methods generic which really need it, to avoid
         * creating useless instantiations.
         */
        internal int InternalArray__ICollection_get_Count()
        {
            return Length;
        }

        internal bool InternalArray__ICollection_get_IsReadOnly()
        {
            return true;
        }

        internal IEnumerator<T> InternalArray__IEnumerable_GetEnumerator<T>()
        {
            int length = Length;
            return length == 0 ? SZGenericArrayEnumerator<T>.Empty : new SZGenericArrayEnumerator<T>(Unsafe.As<T[]>(this), length);
        }

        internal void InternalArray__ICollection_Clear()
        {
            ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ReadOnlyCollection);
        }

        internal void InternalArray__ICollection_Add<T>(T _)
        {
            ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection);
        }

        internal bool InternalArray__ICollection_Remove<T>(T _)
        {
            ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection);
            return default;
        }

        internal bool InternalArray__ICollection_Contains<T>(T item)
        {
            return IndexOf((T[])this, item, 0, Length) >= 0;
        }

        internal void InternalArray__ICollection_CopyTo<T>(T[] array, int arrayIndex)
        {
            Copy(this, GetLowerBound(0), array, arrayIndex, Length);
        }

        internal T InternalArray__IReadOnlyList_get_Item<T>(int index)
        {
            if ((uint)index >= (uint)Length)
                ThrowHelper.ThrowArgumentOutOfRange_IndexMustBeLessException();

            T value;
            // Do not change this to call GetGenericValue_icall directly, due to special casing in the runtime.
            GetGenericValueImpl(index, out value);
            return value;
        }

        internal int InternalArray__IReadOnlyCollection_get_Count()
        {
            return Length;
        }

        internal void InternalArray__Insert<T>(int _, T _1)
        {
            ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection);
        }

        internal void InternalArray__RemoveAt(int _)
        {
            ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_FixedSizeCollection);
        }

        internal int InternalArray__IndexOf<T>(T item)
        {
            return IndexOf((T[])this, item, 0, Length);
        }

        internal T InternalArray__get_Item<T>(int index)
        {
            if ((uint)index >= (uint)Length)
                ThrowHelper.ThrowArgumentOutOfRange_IndexMustBeLessException();

            T value;
            // Do not change this to call GetGenericValue_icall directly, due to special casing in the runtime.
            GetGenericValueImpl(index, out value);
            return value;
        }

        internal void InternalArray__set_Item<T>(int index, T item)
        {
            if ((uint)index >= (uint)Length)
                ThrowHelper.ThrowArgumentOutOfRange_IndexMustBeLessException();

            if (this is object?[] oarray)
            {
                oarray[index] = item;
                return;
            }

            // Do not change this to call SetGenericValue_icall directly, due to special casing in the runtime.
            SetGenericValueImpl(index, ref item);
        }
#pragma warning restore CA1822
    }
}
