using System;
using System.Runtime.CompilerServices;

namespace Il2Wasm.Runtime
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
        /// <summary>The index of the type's assembly (0: the program's).</summary>
        [Intrinsic] public static extern int AssemblyOf(int id);
        /// <summary>A generic parameter's position, its GenericParameterAttributes above bit 16; -1 for other types.</summary>
        [Intrinsic] public static extern int GenericParam(int id);
        /// <summary>A generic parameter's constraint types (an id list).</summary>
        [Intrinsic] public static extern int GenericConstraints(int id);
        /// <summary>A method's generic parameter: its method's reflection record entry (0 when not reflected).</summary>
        [Intrinsic] public static extern int GenericParamMethod(int id);
        /// <summary>The assembly table: [count, full name...] (string addresses).</summary>
        [Intrinsic] public static extern int AssemblyNames();
        /// <summary>The manifest resources: [count, (assembly name, resource name, offset, length, packed length)...]
        /// (string addresses; where the bytes are in their segment: ResourceInit).</summary>
        [Intrinsic] public static extern int ManifestResources();
        /// <summary>A resource's bytes as they are kept (packed, or not when the entry's two lengths are equal): `length`
        /// bytes of their segment from `offset`, to dst.</summary>
        [Intrinsic] public static extern void ResourceInit(int dst, int offset, int length);
        /// <summary>Packed bytes at src expanded into dst..end.</summary>
        [Intrinsic] public static extern void ResourceUnpack(int src, int dst, int end);
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
        /// <summary>The value types with reference slots: [count, (type id, offsets)...] ascending by id, where offsets is
        /// [count, offsets...] from the start of the value.</summary>
        [Intrinsic] public static extern int RefTable();
        /// <summary>An enum's table: [count, flags, size, unsigned], then (value: i64, name) per member, by unsigned value.</summary>
        [Intrinsic] public static extern int EnumInfo(int id);
        /// <summary>[count, ids...]</summary>
        [Intrinsic] public static extern int GenericArgs(int id);
        /// <summary>The id of the type's one-dimensional array type, -1 when the program has none.</summary>
        [Intrinsic] public static extern int ArrayOf(int id);
        /// <summary>The type's reflection record (Meta), 0 when reflection does not reach it.</summary>
        [Intrinsic] public static extern int Meta(int id);
        /// <summary>Calls a reflection thunk or attribute factory by its table index + 1.</summary>
        [Intrinsic] public static extern object? Invoke(int thunk, object? target, object?[]? args);
        /// <summary>[count, ids...]: all the interfaces the type implements.</summary>
        [Intrinsic] public static extern int Interfaces(int id);
        /// <summary>[count, (full name, type id)...]: the program's and CoreLib's named types (Type.GetType); a name made
        /// when first asked for (FullName) is minus its length.</summary>
        [Intrinsic] public static extern int NameTable();
        /// <summary>[count, ids...]: the program's own types, in the order the C# compiler writes them (Assembly.GetTypes).</summary>
        [Intrinsic] public static extern int ProgramTypes();
        /// <summary>[count, ids...]: the type's nested types (not generic ones), in declaration order.</summary>
        [Intrinsic] public static extern int Nested(int id);
        /// <summary>[count, (definition id, instance id, n, argument ids...)...]: the generic instances the program makes.</summary>
        [Intrinsic] public static extern int Instances();

        public static bool IsGenericTypeDefinition(int id) => (Flags2(id) & 64) != 0;

        /// <summary>MakeGenericType: the instance the program makes of the definition with these arguments (there is no
        /// code for an instance it does not make).</summary>
        public static void MakeGeneric(Type gt, Type[] types, ObjectHandleOnStack res)
        {
            int defId = ((RuntimeType)gt).TypeId;
            int t = Instances(), n = Mem.LoadI32(t), p = t + 4;
            for (int i = 0; i < n; i++)
            {
                int def = Mem.LoadI32(p), inst = Mem.LoadI32(p + 4), c = Mem.LoadI32(p + 8);
                if (def == defId && c == types.Length)
                {
                    bool same = true;
                    for (int k = 0; k < c && same; k++) same = types[k] is RuntimeType rk && Mem.LoadI32(p + 12 + 4 * k) == rk.TypeId;
                    if (same) { Set(res, Type(inst)); return; }
                }
                p += 12 + 4 * c;
            }
            throw new PlatformNotSupportedException("il2wasm: " + gt + " with these type arguments is not a type the program makes (no code exists for it)");
        }

        /// <summary>GetGenericTypeDefinition: the definition of an instance.</summary>
        public static void GenericDefinition(int id, ObjectHandleOnStack res)
        {
            if (IsGenericTypeDefinition(id)) { Set(res, Type(id)); return; }
            int t = Instances(), n = Mem.LoadI32(t), p = t + 4;
            for (int i = 0; i < n; i++)
            {
                int c = Mem.LoadI32(p + 8);
                if (Mem.LoadI32(p + 4) == id) { Set(res, Type(Mem.LoadI32(p))); return; }
                p += 12 + 4 * c;
            }
            throw new InvalidOperationException(SR.InvalidOperation_NotGenericType);
        }

        /// <summary>RuntimeTypeHandle.GetTypeByName (manifest substitution): the type of that name among the program's
        /// and CoreLib's, as Type.GetType looks (an assembly-qualified name: its type part).</summary>
        public static RuntimeType? GetTypeByName(string typeName, bool throwOnError, bool ignoreCase, ref System.Threading.StackCrawlMark stackMark)
        {
            ArgumentNullException.ThrowIfNull(typeName);
            string name = typeName.Trim();
            int comma = name.IndexOf(',');
            if (comma >= 0 && name.IndexOf('[') < 0) name = name.Substring(0, comma).Trim();
            int t = NameTable(), n = Mem.LoadI32(t);
            for (int i = 0; i < n; i++)
            {
                // (a name the module makes when first asked for is minus its length here: made only for a name as
                // long as the one looked for)
                int np = Mem.LoadI32(t + 4 + 8 * i);
                if (np < 0 && -np != name.Length) continue;
                string s = np < 0 ? FullName(Mem.LoadI32(t + 8 + 8 * i)) : Rt.Borrow<string>(np);
                if (string.Equals(s, name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return RuntimeType.FromId(Mem.LoadI32(t + 8 + 8 * i));
            }
            if (throwOnError) throw new TypeLoadException("Could not load type '" + name + "'.");
            return null;
        }

        /// <summary>The type id a QCall handle carries (its handle: the RuntimeType's handle value).</summary>
        public static int Id(QCallTypeHandle h) => (int)Unsafe.Add(ref Unsafe.As<QCallTypeHandle, IntPtr>(ref h), 1);

        public static RuntimeType Type(int id) => RuntimeType.FromId(id);
        public static RuntimeType? TypeOrNull(int id) => id < 0 ? null : RuntimeType.FromId(id);

        /// <summary>Stores a result into the slot a QCall handle points at.</summary>
        [RawStores("frame")]
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

        public static NotSupportedException NotYet(string what) => new NotSupportedException("il2wasm: " + what + " is not supported yet");
    }
}
