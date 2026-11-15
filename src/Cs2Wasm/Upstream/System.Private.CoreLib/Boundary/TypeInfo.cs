using System;
using System.Runtime.CompilerServices;

namespace Cs2Wasm.Runtime
{
    /// <summary>What the compiler knows of a type at run time, by type id (the compiler's TypeInfo.cs emits the
    /// tables a program reaches), for reflection's runtime half.</summary>
    internal static unsafe class TypeInfo
    {
        [Intrinsic] public static extern string FullName(int id);
        [Intrinsic] public static extern string Name(int id);
        [Intrinsic] public static extern string Namespace(int id);
        /// <summary>1 value type, 2 enum, 4 array, 8 interface, 16 generic, 32 primitive.</summary>
        [Intrinsic] public static extern int Flags(int id);
        /// <summary>1 holds counted references (in its fields or elements), 2 ref struct, 4 generic instance, 8 nested, 16 delegate, 32 pointer.</summary>
        [Intrinsic] public static extern int Flags2(int id);
        [Intrinsic] public static extern int BaseId(int id);
        [Intrinsic] public static extern int ElementId(int id);
        [Intrinsic] public static extern int DeclaringId(int id);
        [Intrinsic] public static extern int Rank(int id);
        /// <summary>System.Reflection.TypeAttributes.</summary>
        [Intrinsic] public static extern int Attributes(int id);
        /// <summary>Mono's element kind (CorElementType).</summary>
        [Intrinsic] public static extern int CorElementType(int id);
        /// <summary>A value type's size; an object's instance size.</summary>
        [Intrinsic] public static extern int Size(int id);
        /// <summary>Where a box of the value type keeps its data.</summary>
        [Intrinsic] public static extern int BoxOffset(int id);
        /// <summary>An enum's table: [count, flags, size, unsigned], then (value: i64, name) per member, by unsigned value.</summary>
        [Intrinsic] public static extern int EnumInfo(int id);
        /// <summary>[count, ids...]</summary>
        [Intrinsic] public static extern int GenericArgs(int id);
        /// <summary>[count, ids...]: all the interfaces the type implements.</summary>
        [Intrinsic] public static extern int Interfaces(int id);

        /// <summary>The type id a QCall handle carries (its handle: the RuntimeType's handle value).</summary>
        public static int Id(QCallTypeHandle h) => (int)Unsafe.Add(ref Unsafe.As<QCallTypeHandle, IntPtr>(ref h), 1);

        public static RuntimeType Type(int id) => RuntimeType.FromId(id);
        public static RuntimeType? TypeOrNull(int id) => id < 0 ? null : RuntimeType.FromId(id);

        /// <summary>Stores a result into the slot a QCall handle points at.</summary>
        public static void Set(ObjectHandleOnStack res, object? value)
        {
            int slot = Unsafe.As<ObjectHandleOnStack, int>(ref res);
            Unsafe.AsRef<object?>((void*)slot) = value;
        }

        public static RuntimeType[] Types(int blob)
        {
            int n = Mem.LoadI32(blob);
            var r = new RuntimeType[n];
            for (int i = 0; i < n; i++) r[i] = RuntimeType.FromId(Mem.LoadI32(blob + 4 + 4 * i));
            return r;
        }

        /// <summary>Whether a value of type `from` is a `to`: the same type, a base, or an implemented interface.</summary>
        public static bool IsAssignable(int to, int from)
        {
            if (to == from) return true;
            if ((Flags(to) & 8) != 0)
            {
                int blob = Interfaces(from), n = Mem.LoadI32(blob);
                for (int i = 0; i < n; i++) if (Mem.LoadI32(blob + 4 + 4 * i) == to) return true;
                return false;
            }
            for (int b = BaseId(from); b >= 0; b = BaseId(b)) if (b == to) return true;
            return false;
        }

        public static NotSupportedException NotYet(string what) => new NotSupportedException("cs2wasm: " + what + " is not supported yet");
    }
}
