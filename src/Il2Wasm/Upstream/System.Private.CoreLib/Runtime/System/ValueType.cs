// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over the compiler's type tables (Il2Wasm.Runtime.TypeInfo, by type id).

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System
{
    [Serializable]
    [System.Runtime.CompilerServices.TypeForwardedFrom("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")]
    public abstract class ValueType
    {
#if TARGET_BROWSER
        // Tracking issue https://github.com/dotnet/runtime/issues/47909
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, "System.Runtime.InteropServices.JavaScript.JSFunctionBinding", "System.Runtime.InteropServices.JavaScript")]
#endif
        protected ValueType()
        {
        }

        // This is also used by RuntimeHelpers
        internal static bool DefaultEquals(object o1, object o2)
        {
            RuntimeType o1_type = (RuntimeType)o1.GetType();
            RuntimeType o2_type = (RuntimeType)o2.GetType();

            if (o1_type != o2_type)
                return false;

            // il2wasm: the bytes outside the reference slots compare raw; each reference slot compares by object.Equals, as
            // .NET does for a field (the slots: TypeInfo.RefOffsets, flattened through the fields)
            int id = Il2Wasm.Runtime.Rt.TypeId(o1);
            int off = Il2Wasm.Runtime.TypeInfo.BoxOffset(id), n = Il2Wasm.Runtime.TypeInfo.Size(id);
            int a = Il2Wasm.Runtime.Rt.AddressOf(o1) + off, b = Il2Wasm.Runtime.Rt.AddressOf(o2) + off;
            int refs = RefOffsetsOf(id), count = refs == 0 ? 0 : Il2Wasm.Runtime.Mem.LoadI32(refs);
            int pos = 0;
            for (int k = 0; k < count; k++)
            {
                int r = Il2Wasm.Runtime.Mem.LoadI32(refs + 4 + 4 * k);
                if (!SameBytes(a + pos, b + pos, r - pos))
                    return false;
                object? meVal = Il2Wasm.Runtime.Rt.Borrow<object>(Il2Wasm.Runtime.Mem.LoadI32(a + r));
                object? youVal = Il2Wasm.Runtime.Rt.Borrow<object>(Il2Wasm.Runtime.Mem.LoadI32(b + r));
                if (!object.Equals(meVal, youVal))
                    return false;
                pos = r + 4;
            }

            return SameBytes(a + pos, b + pos, n - pos);
        }

        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            if (obj == null)
                return false;

            return DefaultEquals(this, obj);
        }

        public override int GetHashCode()
        {
            // il2wasm: as .NET, the first field that is not a null reference decides: the bytes before a reference slot
            // hash if any come first (a value is never null), a reference slot hashes its object, and a value with no such
            // field hashes its trailing bytes (0 when none are left). A value with no reference slot hashes its bytes.
            int id = Il2Wasm.Runtime.Rt.TypeId(this);
            int a = Il2Wasm.Runtime.Rt.AddressOf(this) + Il2Wasm.Runtime.TypeInfo.BoxOffset(id), n = Il2Wasm.Runtime.TypeInfo.Size(id);
            int refs = RefOffsetsOf(id), count = refs == 0 ? 0 : Il2Wasm.Runtime.Mem.LoadI32(refs);
            if (count == 0)
                return BytesHash(a, n);
            int pos = 0;
            for (int k = 0; k < count; k++)
            {
                int r = Il2Wasm.Runtime.Mem.LoadI32(refs + 4 + 4 * k);
                if (r > pos)
                    return BytesHash(a + pos, r - pos);
                object? value = Il2Wasm.Runtime.Rt.Borrow<object>(Il2Wasm.Runtime.Mem.LoadI32(a + r));
                if (value != null)
                    return value.GetHashCode();
                pos = r + 4;
            }

            return n > pos ? BytesHash(a + pos, n - pos) : 0;
        }

        public override string? ToString()
        {
            return GetType().ToString();
        }


                /// <summary>FNV-1a over n bytes at p.</summary>
        private static int BytesHash(int p, int n)
        {
            uint h = 2166136261;
            for (int i = 0; i < n; i++) h = (h ^ (uint)Il2Wasm.Runtime.Mem.LoadU8(p + i)) * 16777619;
            return (int)h;
        }

        /// <summary>The reference slots' table of a value type (TypeInfo.RefTable, searched by id): [count, offsets...], or
        /// 0 when the type has none.</summary>
        private static int RefOffsetsOf(int id)
        {
            int t = Il2Wasm.Runtime.TypeInfo.RefTable(), lo = 0, hi = Il2Wasm.Runtime.Mem.LoadI32(t) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, p = t + 4 + 8 * mid, x = Il2Wasm.Runtime.Mem.LoadI32(p);
                if (x == id) return Il2Wasm.Runtime.Mem.LoadI32(p + 4);
                if (x < id) lo = mid + 1; else hi = mid - 1;
            }
            return 0;
        }

        private static bool SameBytes(int a, int b, int n)
        {
            for (int i = 0; i < n; i++) if (Il2Wasm.Runtime.Mem.LoadU8(a + i) != Il2Wasm.Runtime.Mem.LoadU8(b + i)) return false;
            return true;
        }
    }
}
