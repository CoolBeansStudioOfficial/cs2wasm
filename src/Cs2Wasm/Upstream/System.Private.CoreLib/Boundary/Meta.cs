using System;
using System.Collections.Generic;
using System.Reflection;

namespace Cs2Wasm.Runtime
{
    /// <summary>
    /// The reflection records the compiler keeps (Compiler/Reflection.cs) for what the program's reflection can be
    /// proven to reach, read for Mono's reflection icalls. A member's handle is the address of its entry; a
    /// constructor's handle is its entry's address + 1. Entries (i32s):
    /// type [nfields, fields, nprops, props, nmethods, methods, nctors, ctors, nattrs, attrs, base id + 1, ...];
    /// field [name, type, flags, thunk, declaring, nattrs, attrs]; ctor [flags, thunk, nparams, param types,
    /// param names, nattrs, attrs, declaring]; method [name, return type (-1 void), flags, thunk, nparams, param
    /// types, param names, nattrs, attrs, declaring]; property [name, type, flags, get thunk, set thunk, declaring,
    /// nattrs, attrs]. Attributes are [type id, factory] pairs. Thunks are (target, object[] args) -> object.
    /// </summary>
    internal static class Meta
    {
        public const int FieldSize = 8, CtorSize = 10, MethodSize = 13, PropSize = 9;

        /// <summary>A parameter's default value from a defaults blob (Compiler/Reflection.cs: Defaults); DBNull when none.</summary>
        /// <summary>A field of a record entry (a closure's captured variable, for expression trees).</summary>
        public static System.Reflection.FieldInfo FieldFromEntry(int entry) => new System.Reflection.RuntimeFieldInfo(entry, TypeAt(I(entry, 4)));

        /// <summary>The `index`th generic method of the name and parameter count, made with the type arguments.</summary>
        public static System.Reflection.MethodInfo GenericMethod(Type owner, string name, int parameters, int index, Type[] typeArgs)
        {
            int i = 0;
            foreach (var m in owner.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.Name != name || !m.IsGenericMethodDefinition || m.GetGenericArguments().Length != typeArgs.Length || m.GetParameters().Length != parameters) continue;
                if (i++ == index) return m.MakeGenericMethod(typeArgs);
            }
            throw new MissingMethodException(owner.FullName, name);
        }

        public static object? DefaultValue(int defs, int i, Type parameterType)
        {
            int k = defs == 0 ? 0 : I(defs, 3 * i);
            if (k == 0) return DBNull.Value;
            int lo = I(defs, 3 * i + 1), hi = I(defs, 3 * i + 2);
            long wide = (uint)lo | ((long)hi << 32);
            object? v = (k & 0xFF) switch
            {
                1 => null,
                2 => lo != 0,
                3 => (char)lo,
                4 => (sbyte)lo,
                5 => (byte)lo,
                6 => (short)lo,
                7 => (ushort)lo,
                8 => lo,
                9 => (uint)lo,
                10 => wide,
                11 => (ulong)wide,
                12 => BitConverter.Int32BitsToSingle(lo),
                13 => BitConverter.Int64BitsToDouble(wide),
                14 => S(lo),
                _ => DBNull.Value,
            };
            if ((k & 0x100) != 0 && v != null)
            {
                var et = parameterType.IsByRef ? parameterType.GetElementType()! : parameterType;
                v = Enum.ToObject(et, v);
            }
            return v;
        }
        /// <summary>A by-ref type's id: its element's with this bit; a parameter word's out and in bits.</summary>
        public const int ByRef = 0x40000000, ParamOut = 0x20000000, ParamIn = 0x10000000;
        public const int FStatic = 1, FPublic = 2, FVirtual = 4, FAbstract = 8, FReadOnly = 16, FPrivate = 32, FFamily = 64, FAssembly = 128;

        public static int I(int addr, int index) => Mem.LoadI32(addr + 4 * index);
        public static string S(int addr) => addr == 0 ? "" : Rt.Borrow<string>(addr);
        public static RuntimeType TypeAt(int id) => id == -1 ? (RuntimeType)typeof(void) : TypeInfo.Type(id);

        /// <summary>The record of a type, or 0 when the program's reflection does not reach it.</summary>
        public static int Record(RuntimeType t) => TypeInfo.Meta(t.TypeId);

        public static int Access(int flags) =>
            (flags & FPublic) != 0 ? 6 : (flags & FPrivate) != 0 ? 1 : (flags & (FFamily | FAssembly)) == (FFamily | FAssembly) ? 5 : (flags & FFamily) != 0 ? 4 : 3;

        public static MethodAttributes MethodAttrs(int flags)
        {
            int a = Access(flags) | 0x80; // HideBySig
            if ((flags & FStatic) != 0) a |= 0x10;
            if ((flags & FVirtual) != 0) a |= 0x40;
            if ((flags & FAbstract) != 0) a |= 0x400;
            if ((flags & (1 << 13)) != 0) a |= 0x800; // SpecialName
            if ((flags & (1 << 14)) != 0) a |= 0x20;  // Final
            if ((flags & (1 << 15)) != 0) a |= 0x100; // NewSlot
            return (MethodAttributes)a;
        }

        public static FieldAttributes FieldAttrs(int flags)
        {
            int a = Access(flags);
            if ((flags & FStatic) != 0) a |= 0x10;
            if ((flags & FReadOnly) != 0) a |= 0x20;
            return (FieldAttributes)a;
        }

        /// <summary>Whether a member passes the binding flags (Mono's filtering in mono_class_get_methods_by_name).</summary>
        public static bool Binds(BindingFlags f, int flags, bool inherited)
        {
            bool isPublic = (flags & FPublic) != 0, isStatic = (flags & FStatic) != 0;
            if (isPublic ? (f & BindingFlags.Public) == 0 : (f & BindingFlags.NonPublic) == 0) return false;
            if (isStatic ? (f & BindingFlags.Static) == 0 : (f & BindingFlags.Instance) == 0) return false;
            if (inherited)
            {
                if ((f & BindingFlags.DeclaredOnly) != 0) return false;
                if (isStatic && (f & BindingFlags.FlattenHierarchy) == 0) return false;
                if ((flags & FPrivate) != 0) return false;
            }
            return true;
        }

        public static bool NameMatches(string? name, RuntimeType.MemberListType listType, string candidate) =>
            listType switch
            {
                RuntimeType.MemberListType.CaseSensitive => name == candidate,
                RuntimeType.MemberListType.CaseInsensitive => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase),
                _ => true,
            };

        public static RuntimeType[] ParamTypes(int count, int addr)
        {
            var r = new RuntimeType[count];
            for (int i = 0; i < count; i++) r[i] = TypeAt(I(addr, i));
            return r;
        }

        /// <summary>A method's parameter types as a signature key, for hiding base methods by signature.</summary>
        public static string SigKey(int entry)
        {
            int n = I(entry, 4), pt = I(entry, 5);
            var s = S(I(entry, 0)) + "(";
            for (int i = 0; i < n; i++) s += I(pt, i) + ",";
            return s;
        }

        /// <summary>The objects a thunk is called with: Mono's argument pointers (a reference's slot, a value's box data).</summary>
        public static unsafe object?[]? ArgsFrom(IntPtr* args, RuntimeType[] types)
        {
            if (types.Length == 0) return null;
            var r = new object?[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                int p = (int)args[i];
                var t = (types[i].TypeId & ByRef) != 0 ? TypeInfo.Type(types[i].TypeId & ~ByRef) : types[i];
                r[i] = t.IsValueType ? Rt.Borrow<object>(p - Rt.BoxDataOffset<int>()) : Rt.Borrow<object>(Mem.LoadI32(p));
            }
            return r;
        }

        /// <summary>After a call: a by-ref reference argument the thunk wrote into the array goes back to the invoker's
        /// variable (a by-ref value's box was written in place).</summary>
        public static unsafe void CopyBack(IntPtr* args, RuntimeType[] types, object?[]? r)
        {
            if (r == null) return;
            for (int i = 0; i < types.Length; i++)
                if ((types[i].TypeId & ByRef) != 0 && !TypeInfo.Type(types[i].TypeId & ~ByRef).IsValueType)
                    System.Runtime.CompilerServices.Unsafe.AsRef<object?>((void*)args[i]) = r[i];
        }

        /// <summary>The attributes of an attribute list that are a `filter` (all when null).</summary>
        public static Attribute[] Attributes(int count, int addr, Type? filter)
        {
            var list = new List<Attribute>();
            for (int i = 0; i < count; i++)
            {
                var at = TypeInfo.Type(I(addr, 2 * i));
                if (filter != null && !filter.IsAssignableFrom(at)) continue;
                list.Add((Attribute)TypeInfo.Invoke(I(addr, 2 * i + 1), null, null)!);
            }
            return list.ToArray();
        }

        public static bool AnyAttribute(int count, int addr, Type? filter)
        {
            for (int i = 0; i < count; i++)
                if (filter == null || filter.IsAssignableFrom(TypeInfo.Type(I(addr, 2 * i)))) return true;
            return false;
        }
    }
}
