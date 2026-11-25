// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's arrays (header, length at 12, elements from 16;
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
        // il2wasm's array layout: after the object header the length, then the elements from 16 (other ranks keep
        // their lengths there and start their elements after them: Il2WasmArrays.Data)
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
            get => Il2Wasm.Runtime.Il2WasmArrays.RankOf(this);
        }

        public static unsafe void Clear(Array array)
        {
            if (array == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);

            ref byte ptr = ref MemoryMarshal.GetArrayDataReference(array);
            nuint byteLength = array.NativeLength * (nuint)(uint)array.GetElementSize() /* force zero-extension */;

            // il2wasm: the references the elements hold are released (zeros alone would leave their objects for ever)
            if (Il2Wasm.Runtime.Il2WasmArrays.ClearCounted(array, 0, (int)array.NativeLength)) return;
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

            if (Il2Wasm.Runtime.Il2WasmArrays.ClearCounted(array, offset, length)) return;
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

            // il2wasm: the arrays themselves, not handles to local copies (a count taken and let go for each)
            if (FastCopy(sourceArray, sourceIndex, destinationArray, destinationIndex, length))
                return;

            CopySlow(sourceArray, sourceIndex, destinationArray, destinationIndex, length, reliable);
        }

        // il2wasm: the copies FastCopy does not do, by element type id (Il2WasmArrays.GetBoxed/SetBoxed): references
        // with a check per element, boxing into and unboxing out of reference arrays, and primitives into a wider
        // primitive type (int[] into long[]) or the type of the same size and other sign (the CLR's rules).
        static int WidenKind(int k) => k == 0x18 ? 0x08 : k == 0x19 ? 0x09 : k;

        // (bit d of entry s: a value of kind s goes into an element of kind d; kinds are CorElementTypes 2..13)
        static bool CanWiden(int s, int d)
        {
            int to = s switch
            {
                0x02 => 1 << 0x02,
                0x03 => 1 << 0x03 | 1 << 0x07 | 1 << 0x08 | 1 << 0x09 | 1 << 0x0A | 1 << 0x0B | 1 << 0x0C | 1 << 0x0D,
                0x04 => 1 << 0x04 | 1 << 0x06 | 1 << 0x08 | 1 << 0x0A | 1 << 0x0C | 1 << 0x0D,
                0x05 => 1 << 0x05 | 1 << 0x03 | 1 << 0x06 | 1 << 0x07 | 1 << 0x08 | 1 << 0x09 | 1 << 0x0A | 1 << 0x0B | 1 << 0x0C | 1 << 0x0D,
                0x06 => 1 << 0x06 | 1 << 0x08 | 1 << 0x0A | 1 << 0x0C | 1 << 0x0D,
                0x07 => 1 << 0x07 | 1 << 0x03 | 1 << 0x08 | 1 << 0x09 | 1 << 0x0A | 1 << 0x0B | 1 << 0x0C | 1 << 0x0D,
                0x08 => 1 << 0x08 | 1 << 0x0A | 1 << 0x0C | 1 << 0x0D,
                0x09 => 1 << 0x09 | 1 << 0x0A | 1 << 0x0B | 1 << 0x0C | 1 << 0x0D,
                0x0A => 1 << 0x0A | 1 << 0x0C | 1 << 0x0D,
                0x0B => 1 << 0x0B | 1 << 0x0C | 1 << 0x0D,
                0x0C => 1 << 0x0C | 1 << 0x0D,
                0x0D => 1 << 0x0D,
                _ => 0,
            };
            return (to >> d & 1) != 0;
        }

        static bool SameBits(int s, int d) => s == d || (s ^ d) == 1 && s >= 0x04 && s <= 0x0B && d >= 0x04 && d <= 0x0B && (s & ~1) == (d & ~1);

        static unsafe void WidenCopy(Array src, int si, int sk, Array dst, int di, int dk, int n)
        {
            int ss = Il2Wasm.Runtime.Il2WasmArrays.ElementSize(src), ds = Il2Wasm.Runtime.Il2WasmArrays.ElementSize(dst);
            byte* p = (byte*)Il2Wasm.Runtime.Il2WasmArrays.Data(src) + si * ss, q = (byte*)Il2Wasm.Runtime.Il2WasmArrays.Data(dst) + di * ds;
            for (int i = 0; i < n; i++, p += ss, q += ds)
            {
                if (sk == 0x0C) { if (dk == 0x0C) *(float*)q = *(float*)p; else *(double*)q = *(float*)p; continue; }
                if (sk == 0x0D) { *(double*)q = *(double*)p; continue; }
                long v = sk switch { 0x02 or 0x05 => *p, 0x04 => *(sbyte*)p, 0x06 => *(short*)p, 0x03 or 0x07 => *(ushort*)p, 0x08 => *(int*)p, 0x09 => *(uint*)p, _ => *(long*)p };
                switch (dk)
                {
                    case 0x02: case 0x04: case 0x05: *q = (byte)v; break;
                    case 0x03: case 0x06: case 0x07: *(short*)q = (short)v; break;
                    case 0x08: case 0x09: *(int*)q = (int)v; break;
                    case 0x0A: case 0x0B: *(long*)q = v; break;
                    case 0x0C: *(float*)q = sk == 0x0B ? (float)(ulong)v : (float)v; break;
                    default: *(double*)q = sk == 0x0B ? (double)(ulong)v : (double)v; break;
                }
            }
        }

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

            int se = Il2Wasm.Runtime.TypeInfo.ElementId(Il2Wasm.Runtime.Rt.TypeId(sourceArray));
            int de = Il2Wasm.Runtime.TypeInfo.ElementId(Il2Wasm.Runtime.Rt.TypeId(destinationArray));
            bool sv = (Il2Wasm.Runtime.TypeInfo.Flags(se) & 1) != 0, dv = (Il2Wasm.Runtime.TypeInfo.Flags(de) & 1) != 0;
            if (sv && dv)
            {
                int sk = WidenKind(Il2Wasm.Runtime.Il2WasmArrays.PrimitiveKind(se)), dk = WidenKind(Il2Wasm.Runtime.Il2WasmArrays.PrimitiveKind(de));
                if (sk != 0 && dk != 0 && !reliable)
                {
                    if (SameBits(sk, dk))
                    {
                        Il2Wasm.Runtime.Il2WasmArrays.CopyElements(sourceArray, source_pos, destinationArray, dest_pos, length, Il2Wasm.Runtime.Il2WasmArrays.ElementSize(sourceArray));
                        return;
                    }
                    if (CanWiden(sk, dk)) { WidenCopy(sourceArray, source_pos, sk, destinationArray, dest_pos, dk, length); return; }
                }
                throw new ArrayTypeMismatchException(SR.ArrayTypeMismatch_CantAssignType);
            }
            if (sv ? !Il2Wasm.Runtime.TypeInfo.IsAssignable(de, se)
                   : !dv && !Il2Wasm.Runtime.TypeInfo.IsAssignable(de, se) && !Il2Wasm.Runtime.TypeInfo.IsAssignable(se, de)
                     && (Il2Wasm.Runtime.TypeInfo.Flags(se) & 8) == 0 && (Il2Wasm.Runtime.TypeInfo.Flags(de) & 8) == 0
                     || dv && !Il2Wasm.Runtime.TypeInfo.IsAssignable(se, de))
                throw new ArrayTypeMismatchException(SR.ArrayTypeMismatch_CantAssignType);

            bool forward = !ReferenceEquals(sourceArray, destinationArray) || source_pos > dest_pos;
            for (int k = 0; k < length; k++)
            {
                int i = forward ? k : length - 1 - k;
                object? v = Il2Wasm.Runtime.Il2WasmArrays.GetBoxed(sourceArray, source_pos + i);
                if (dv ? v == null : v != null && !Il2Wasm.Runtime.TypeInfo.IsAssignable(de, Il2Wasm.Runtime.Rt.TypeId(v)))
                    throw new InvalidCastException(SR.InvalidCast_DownCastArrayElement);
                Il2Wasm.Runtime.Il2WasmArrays.SetBoxed(destinationArray, dest_pos + i, v);
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
            // il2wasm: one-dimensional arrays of a type the program makes arrays of (TypeInfo.ArrayOf)
            int e = (int)elementType;
            int at = rank == 1 && (lowerBounds == null || lowerBounds[0] == 0) ? Il2Wasm.Runtime.TypeInfo.ArrayOf(e) : -1;
            if (at < 0) throw Il2Wasm.Runtime.TypeInfo.NotYet("Array.CreateInstance of an array type the program never makes");
            int size = (Il2Wasm.Runtime.TypeInfo.Flags(e) & 1) != 0 ? Il2Wasm.Runtime.TypeInfo.Size(e) : 4;
            result = Il2Wasm.Runtime.Rt.Take<Array>(Il2Wasm.Runtime.Rt.AllocArray(lengths[0], size, at));
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
            // il2wasm: the array itself, not a handle to a local copy of `this` (a count taken and let go per call)
            return GetLengthOf(this, dimension);
        }

        [Intrinsic] // when dimension is `0` constant
        public int GetLowerBound(int dimension)
        {
            // il2wasm: what GetLowerBoundInternal answers, without a handle to a local copy of `this`
            return GetLowerBoundInternal(default, dimension);
        }

        [Intrinsic]
#pragma warning disable CA1822 // Mark members as static
        internal int GetElementSize() => Il2Wasm.Runtime.Il2WasmArrays.ElementSize(this);
#pragma warning restore

        // il2wasm: from the array's type (the module's type tables), as Mono reads its class: an enum is its underlying
        // type, the primitives their own element types, any other value type or reference VALUETYPE or CLASS
        private static CorElementType GetCorElementTypeOfElementTypeInternal(ObjectHandleOnStack arr)
        {
            Type t = Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(arr).GetType().GetElementType()!;
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
            object? value = Il2Wasm.Runtime.Il2WasmArrays.Handle<object>(obj);
            return value != null && value.GetType() == Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(arr).GetType().GetElementType();
        }

        private static bool CanChangePrimitive(ObjectHandleOnStack srcType, ObjectHandleOnStack dstType, bool reliable)
        => false;

        internal static bool FastCopy(ObjectHandleOnStack source, int source_idx, ObjectHandleOnStack dest, int dest_idx, int length)
            => FastCopy(Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(source), source_idx, Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(dest), dest_idx, length);

        internal static bool FastCopy(Array src, int source_idx, Array dst, int dest_idx, int length)
        {
            // il2wasm: arrays of one type, or both of counted references (covariance is not checked yet)
            int size = Il2Wasm.Runtime.Il2WasmArrays.ElementSize(src);
            bool same = Il2Wasm.Runtime.Rt.TypeId(src) == Il2Wasm.Runtime.Rt.TypeId(dst)
                || (Il2Wasm.Runtime.Il2WasmArrays.HasCountedElements(src) && Il2Wasm.Runtime.Il2WasmArrays.HasCountedElements(dst) && size == Il2Wasm.Runtime.Il2WasmArrays.ElementSize(dst) && size == 4);
            if (!same && !Il2Wasm.Runtime.Il2WasmArrays.HasCountedElements(src) && !Il2Wasm.Runtime.Il2WasmArrays.HasCountedElements(dst)
                && size == Il2Wasm.Runtime.Il2WasmArrays.ElementSize(dst) && Il2Wasm.Runtime.Il2WasmArrays.RankOf(src) == Il2Wasm.Runtime.Il2WasmArrays.RankOf(dst))
            {
                // an enum and its underlying type (or two enums over it) share their elements' bits
                int se = Il2Wasm.Runtime.TypeInfo.ElementId(Il2Wasm.Runtime.Rt.TypeId(src)), de = Il2Wasm.Runtime.TypeInfo.ElementId(Il2Wasm.Runtime.Rt.TypeId(dst));
                int sk = Il2Wasm.Runtime.Il2WasmArrays.PrimitiveKind(se);
                same = sk != 0 && sk == Il2Wasm.Runtime.Il2WasmArrays.PrimitiveKind(de);
            }
            if (!same) return false;
            // References into an array of another element type (object[] to string[]): each must be an element of
            // the destination's; those before the first that is not are copied, then InvalidCastException.
            int st = Il2Wasm.Runtime.Rt.TypeId(src), dt = Il2Wasm.Runtime.Rt.TypeId(dst);
            if (st != dt && Il2Wasm.Runtime.Il2WasmArrays.HasCountedElements(src) && size == 4
                && (Il2Wasm.Runtime.TypeInfo.Flags(Il2Wasm.Runtime.TypeInfo.ElementId(st)) & 1) == 0
                && !Il2Wasm.Runtime.Rt.ElementFits(Il2Wasm.Runtime.TypeInfo.ElementId(dt), Il2Wasm.Runtime.TypeInfo.ElementId(st)))
            {
                int de = Il2Wasm.Runtime.TypeInfo.ElementId(dt), data = Il2Wasm.Runtime.Il2WasmArrays.Data(src) + 4 * source_idx;
                for (int i = 0; i < length; i++)
                {
                    int v = Il2Wasm.Runtime.Mem.LoadI32(data + 4 * i);
                    if (v != 0 && !Il2Wasm.Runtime.Rt.ElementFits(de, Il2Wasm.Runtime.Mem.LoadI32(v + 4)))
                    {
                        Il2Wasm.Runtime.Il2WasmArrays.CopyElements(src, source_idx, dst, dest_idx, i, size);
                        throw new InvalidCastException(SR.InvalidCast_DownCastArrayElement);
                    }
                }
            }
            Il2Wasm.Runtime.Il2WasmArrays.CopyElements(src, source_idx, dst, dest_idx, length, size);
            return true;
        }

        private static int GetLengthInternal(ObjectHandleOnStack arr, int dimension) => GetLengthOf(Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(arr), dimension);

        private static int GetLengthOf(Array a, int dimension)
        {
            return Il2Wasm.Runtime.Il2WasmArrays.RankOf(a) == 1 ? a.Length : Il2Wasm.Runtime.Mem.LoadI32(Il2Wasm.Runtime.Rt.AddressOf(a) + 16 + 4 * dimension);
        }

        private static int GetLowerBoundInternal(ObjectHandleOnStack arr, int dimension)
        => 0;

        // CAUTION! No bounds checking!
        private static unsafe void GetGenericValue_icall<T>(ObjectHandleOnStack self, int pos, out T value)
        {
            Array a = Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(self);
            value = Unsafe.AsRef<T>((void*)(Il2Wasm.Runtime.Il2WasmArrays.Data(a) + pos * Unsafe.SizeOf<T>()));
        }

        // CAUTION! No bounds checking!
        private static void GetValueImpl(ObjectHandleOnStack arr, ObjectHandleOnStack res, int pos)
        {
            Array a = Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(arr);
            Il2Wasm.Runtime.TypeInfo.Set(res, Il2Wasm.Runtime.Il2WasmArrays.GetBoxed(a, pos));
        }

        // CAUTION! No bounds checking!
        private static unsafe void SetGenericValue_icall<T>(ObjectHandleOnStack arr, int pos, ref T value)
        {
            Array a = Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(arr);
            Unsafe.AsRef<T>((void*)(Il2Wasm.Runtime.Il2WasmArrays.Data(a) + pos * Unsafe.SizeOf<T>())) = value;
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
            Array a = Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(arr);
            Il2Wasm.Runtime.Il2WasmArrays.SetBoxed(a, pos, WidenedForStore(a, Il2Wasm.Runtime.Il2WasmArrays.Handle<object>(value)));
        }

        // il2wasm: a primitive stored into an array of another primitive type is converted when the CLR widens it (int
        // into long[], char into double[]); any other primitive is an ArgumentException, as the CLR's SetValue gives it.
        static object? WidenedForStore(Array a, object? v)
        {
            if (v is null) return null;
            int ve = Il2Wasm.Runtime.Rt.TypeId(v), de = Il2Wasm.Runtime.TypeInfo.ElementId(Il2Wasm.Runtime.Rt.TypeId(a));
            int sk = Il2Wasm.Runtime.Il2WasmArrays.PrimitiveKind(ve), dk = Il2Wasm.Runtime.Il2WasmArrays.PrimitiveKind(de);
            if (sk == 0 || dk == 0 || sk == dk) return v;
            sk = WidenKind(sk); dk = WidenKind(dk);
            if (!CanWiden(sk, dk)) throw new ArgumentException(SR.Format(SR.Arg_ObjObjEx, v.GetType(), a.GetType().GetElementType()));
            return WidenValue(v, dk);
        }

        // (the value converted to the element kind d; CanWiden's conversions are exact, rounding only into float and double)
        static object WidenValue(object v, int d)
        {
            long sv = 0;
            ulong uv = 0;
            bool signed = true;
            switch (v)
            {
                case float f: return d == 0x0D ? (double)f : (object)f;
                case double x: return x;
                case char c: uv = c; signed = false; break;
                case byte b: uv = b; signed = false; break;
                case ushort us: uv = us; signed = false; break;
                case uint ui: uv = ui; signed = false; break;
                case ulong ul: uv = ul; signed = false; break;
                case sbyte sb: sv = sb; break;
                case short ss: sv = ss; break;
                case int i: sv = i; break;
                case long l: sv = l; break;
                default: throw new InvalidCastException(SR.InvalidCast_StoreArrayElement);
            }
            switch (d)
            {
                case 0x03: return (char)uv;
                case 0x04: return signed ? (sbyte)sv : (sbyte)uv;
                case 0x05: return signed ? (byte)sv : (byte)uv;
                case 0x06: return signed ? (short)sv : (short)uv;
                case 0x07: return signed ? (ushort)sv : (ushort)uv;
                case 0x08: return signed ? (int)sv : (int)uv;
                case 0x09: return signed ? (uint)sv : (uint)uv;
                case 0x0A: return signed ? sv : (long)uv;
                case 0x0B: return signed ? (ulong)sv : uv;
                case 0x0C: return signed ? (float)sv : (float)uv;
                case 0x0D: return signed ? (double)sv : (double)uv;
                default: throw new InvalidCastException(SR.InvalidCast_StoreArrayElement);
            }
        }

        private static void InitializeInternal(ObjectHandleOnStack arr)
        {
        }

        // CAUTION! No bounds checking!
        private static void SetValueRelaxedImpl(ObjectHandleOnStack arr, ObjectHandleOnStack value, int pos)
        {
            Array a = Il2Wasm.Runtime.Il2WasmArrays.Handle<Array>(arr);
            Il2Wasm.Runtime.Il2WasmArrays.SetBoxed(a, pos, Il2Wasm.Runtime.Il2WasmArrays.Handle<object>(value));
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
