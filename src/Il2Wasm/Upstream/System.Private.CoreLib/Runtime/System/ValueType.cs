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

            // il2wasm: as CoreCLR's CanCompareBits: a value with no reference, float, overriding or padding is compared by its
            // bytes; any other is compared field by field (its plan: TypeInfo.FieldPlans). A reference compares by
            // object.Equals, a float by double.Equals, an overriding struct by its own Equals, a Nullable by its value.
            int id = Il2Wasm.Runtime.Rt.TypeId(o1);
            int off = Il2Wasm.Runtime.TypeInfo.BoxOffset(id), n = Il2Wasm.Runtime.TypeInfo.Size(id);
            int a = Il2Wasm.Runtime.Rt.AddressOf(o1) + off, b = Il2Wasm.Runtime.Rt.AddressOf(o2) + off;
            int plan = PlanOf(id);
            if (plan == 0)
                return SameBytes(a, b, n);

            int count = Il2Wasm.Runtime.Mem.LoadI32(plan);
            for (int k = 0; k < count; k++)
            {
                int p = plan + 4 + 20 * k;
                int kind = Il2Wasm.Runtime.Mem.LoadI32(p), so = Il2Wasm.Runtime.Mem.LoadI32(p + 4), sz = Il2Wasm.Runtime.Mem.LoadI32(p + 8);
                int x = Il2Wasm.Runtime.Mem.LoadI32(p + 12), y = Il2Wasm.Runtime.Mem.LoadI32(p + 16);
                switch (kind)
                {
                    case 0:
                        if (!SameBytes(a + so, b + so, sz)) return false;
                        break;
                    case 1:
                        if (!object.Equals(Il2Wasm.Runtime.Rt.Borrow<object>(Il2Wasm.Runtime.Mem.LoadI32(a + so)), Il2Wasm.Runtime.Rt.Borrow<object>(Il2Wasm.Runtime.Mem.LoadI32(b + so)))) return false;
                        break;
                    case 2:
                        if (!FloatEquals(Il2Wasm.Runtime.Mem.LoadF32(a + so), Il2Wasm.Runtime.Mem.LoadF32(b + so))) return false;
                        break;
                    case 3:
                        if (!DoubleEquals(Il2Wasm.Runtime.Mem.LoadF64(a + so), Il2Wasm.Runtime.Mem.LoadF64(b + so))) return false;
                        break;
                    case 4:
                        if (!object.Equals(BoxField(x, a + so, sz), BoxField(x, b + so, sz))) return false;
                        break;
                    default:
                        if (!object.Equals(BoxNullable(x, y, a + so), BoxNullable(x, y, b + so))) return false;
                        break;
                }
            }

            return true;
        }

        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            if (obj == null)
                return false;

            return DefaultEquals(this, obj);
        }

        public override int GetHashCode()
        {
            // il2wasm: as .NET, the first field that decides is hashed: a reference that is not null by its object, a float by
            // its value (0.0 and -0.0 alike, NaN alike), a struct by its own GetHashCode, the bytes of a field otherwise
            int id = Il2Wasm.Runtime.Rt.TypeId(this);
            int a = Il2Wasm.Runtime.Rt.AddressOf(this) + Il2Wasm.Runtime.TypeInfo.BoxOffset(id), n = Il2Wasm.Runtime.TypeInfo.Size(id);
            int plan = PlanOf(id);
            if (plan == 0)
                return BytesHash(a, n);

            int count = Il2Wasm.Runtime.Mem.LoadI32(plan);
            for (int k = 0; k < count; k++)
            {
                int p = plan + 4 + 20 * k;
                int kind = Il2Wasm.Runtime.Mem.LoadI32(p), so = Il2Wasm.Runtime.Mem.LoadI32(p + 4), sz = Il2Wasm.Runtime.Mem.LoadI32(p + 8);
                int x = Il2Wasm.Runtime.Mem.LoadI32(p + 12), y = Il2Wasm.Runtime.Mem.LoadI32(p + 16);
                switch (kind)
                {
                    case 0:
                        return BytesHash(a + so, sz);
                    case 1:
                        object? value = Il2Wasm.Runtime.Rt.Borrow<object>(Il2Wasm.Runtime.Mem.LoadI32(a + so));
                        if (value != null)
                            return value.GetHashCode();
                        break;
                    case 2:
                        return FloatHash(a + so);
                    case 3:
                        return DoubleHash(a + so);
                    case 4:
                        return BoxField(x, a + so, sz).GetHashCode();
                    default:
                        object? boxed = BoxNullable(x, y, a + so);
                        return boxed == null ? 0 : boxed.GetHashCode();
                }
            }

            return 0;
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

        private static bool SameBytes(int a, int b, int n)
        {
            for (int i = 0; i < n; i++) if (Il2Wasm.Runtime.Mem.LoadU8(a + i) != Il2Wasm.Runtime.Mem.LoadU8(b + i)) return false;
            return true;
        }

        private static bool FloatEquals(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));

        private static bool DoubleEquals(double a, double b) => a == b || (double.IsNaN(a) && double.IsNaN(b));

        // (a float's hash from its bits at addr; 0.0 and -0.0 hash alike, and every NaN)
        private static int FloatHash(int addr)
        {
            float f = Il2Wasm.Runtime.Mem.LoadF32(addr);
            if (f != f) return 0x7FC00000;
            if (f == 0) return 0;
            return Il2Wasm.Runtime.Mem.LoadI32(addr);
        }

        private static int DoubleHash(int addr)
        {
            double d = Il2Wasm.Runtime.Mem.LoadF64(addr);
            if (d != d) return 0x7FF80000;
            if (d == 0) return 0;
            return Il2Wasm.Runtime.Mem.LoadI32(addr) ^ Il2Wasm.Runtime.Mem.LoadI32(addr + 4);
        }

        /// <summary>A struct field of type `e` (`size` bytes) at addr, as a box of its own. A struct with references is not
        /// supported yet (the box would need to count them).</summary>
        private static object BoxField(int e, int addr, int size)
        {
            if ((Il2Wasm.Runtime.TypeInfo.Flags2(e) & 1) != 0)
                throw new NotSupportedException("il2wasm: comparing a struct field with references is not supported yet");
            int off = Il2Wasm.Runtime.TypeInfo.BoxOffset(e);
            int box = Il2Wasm.Runtime.Rt.AllocObject(off + size, e);
            Il2Wasm.Runtime.Mem.Copy(box + off, addr, size);
            return Il2Wasm.Runtime.Rt.Take<object>(box);
        }

        /// <summary>A Nullable of argument `e` at addr, as its boxed value: null when it has no value.</summary>
        private static object? BoxNullable(int e, int valueOff, int addr)
        {
            if (Il2Wasm.Runtime.Mem.LoadU8(addr) == 0)
                return null;
            return BoxField(e, addr + valueOff, Il2Wasm.Runtime.TypeInfo.Size(e));
        }

        /// <summary>The field plan of a value type (TypeInfo.FieldPlans, searched by id): its list, or 0 when its bytes compare.</summary>
        private static int PlanOf(int id)
        {
            int t = Il2Wasm.Runtime.TypeInfo.FieldPlans(), lo = 0, hi = Il2Wasm.Runtime.Mem.LoadI32(t) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, p = t + 4 + 8 * mid, x = Il2Wasm.Runtime.Mem.LoadI32(p);
                if (x == id) return Il2Wasm.Runtime.Mem.LoadI32(p + 4);
                if (x < id) lo = mid + 1; else hi = mid - 1;
            }
            return 0;
        }
    }
}
