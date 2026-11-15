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

        /// <summary>A primitive's element kind (CorElementType), an enum's its underlying type's; 0 for other types.</summary>
        internal static int PrimitiveKind(int id)
        {
            int flags = TypeInfo.Flags(id);
            if ((flags & 2) != 0)
            {
                int info = TypeInfo.EnumInfo(id);
                int size = Mem.LoadI32(info + 8);
                bool unsigned = Mem.LoadI32(info + 12) != 0;
                return size switch { 1 => unsigned ? 0x05 : 0x04, 2 => unsigned ? 0x07 : 0x06, 4 => unsigned ? 0x09 : 0x08, _ => unsigned ? 0x0B : 0x0A };
            }
            return (flags & 32) != 0 ? TypeInfo.CorElementType(id) : 0;
        }

        /// <summary>Element `pos` as an object: a reference as it is, a value boxed (Array.GetValue).</summary>
        internal static object? GetBoxed(Array a, int pos)
        {
            int e = TypeInfo.ElementId(Rt.TypeId(a));
            int size = ElementSize(a);
            int addr = Data(a) + pos * size;
            if ((TypeInfo.Flags(e) & 1) == 0) return Rt.Borrow<object>(Mem.LoadI32(addr));
            if ((TypeInfo.Flags2(e) & 1) != 0 || IsNullable(e)) throw TypeInfo.NotYet("boxing an array element of a struct with references");
            int off = TypeInfo.BoxOffset(e);
            int box = Rt.AllocObject(off + size, e);
            Mem.Copy(box + off, addr, size);
            return Rt.Take<object>(box);
        }

        /// <summary>Stores an object into element `pos` (Array.SetValue): a reference of an assignable type, or a box
        /// of the element type (or of a primitive or enum with its bits); null clears a value element.</summary>
        internal static void SetBoxed(Array a, int pos, object? value)
        {
            int e = TypeInfo.ElementId(Rt.TypeId(a));
            int size = ElementSize(a);
            int addr = Data(a) + pos * size;
            if ((TypeInfo.Flags(e) & 1) == 0)
            {
                if (value != null && !TypeInfo.IsAssignable(e, Rt.TypeId(value))) throw new InvalidCastException(SR.InvalidCast_StoreArrayElement);
                Unsafe.AsRef<object?>((void*)addr) = value;
                return;
            }
            if ((TypeInfo.Flags2(e) & 1) != 0 || IsNullable(e)) throw TypeInfo.NotYet("storing a boxed struct with references into an array");
            if (value == null) { new Span<byte>((void*)addr, size).Clear(); return; }
            int v = Rt.TypeId(value);
            if (v != e && (PrimitiveKind(v) == 0 || PrimitiveKind(v) != PrimitiveKind(e))) throw new InvalidCastException(SR.InvalidCast_StoreArrayElement);
            Mem.Copy(addr, Rt.AddressOf(value) + TypeInfo.BoxOffset(v), size);
        }

        static bool IsNullable(int id) => (TypeInfo.Flags2(id) & 4) != 0 && TypeInfo.FullName(id).StartsWith("System.Nullable`1", StringComparison.Ordinal);

        /// <summary>The object a QCall handle points at (a slot holding the reference).</summary>
        internal static T Handle<T>(ObjectHandleOnStack h) where T : class => Rt.Borrow<T>(Mem.LoadI32(Unsafe.As<ObjectHandleOnStack, int>(ref h)));
    }
}
