// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's runtime half of Delegate, over the compiler's delegates (Calls.cs): the target at 12, the
// function (its table index) at 16, and for a combined delegate its invocation list at 20. The compiler creates and
// invokes them; this file combines, compares and lists them. Delegates over methods found by reflection are not
// supported yet.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cs2Wasm.Runtime;
using TypeInfo = Cs2Wasm.Runtime.TypeInfo;

namespace System
{
    [StructLayout(LayoutKind.Sequential)]
    public abstract partial class Delegate
    {
        // the compiler's layout (DelegateTarget, DelegateFn, the invocation list read by the invoke thunk)
        internal object? _target;
        internal int _fn;
        internal Delegate[]? _list;

        [RequiresUnreferencedCode("The target method might be removed")]
        protected Delegate(object target, string method) => throw TypeInfo.NotYet("delegates created from a method name");

        protected Delegate([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllMethods)] Type target, string method) => throw TypeInfo.NotYet("delegates created from a method name");

        public object? Target => GetTarget();

        internal virtual object? GetTarget() => _list == null ? _target : _list[^1]._target;

        public partial bool HasSingleTarget => _list == null;

        protected virtual Delegate CombineImpl(Delegate? d)
        {
            if (d is null) return this;
            if (!InternalEqualTypes(this, d)) throw new ArgumentException(SR.Arg_DlgtTypeMis);
            int n = Count + d.Count;
            var list = new Delegate[n];
            int k = 0;
            CopyTo(list, ref k);
            d.CopyTo(list, ref k);
            return WithList(list);
        }

        protected virtual Delegate? RemoveImpl(Delegate d)
        {
            int sn = Count, vn = d.Count;
            var s = new Delegate[sn]; int k = 0; CopyTo(s, ref k);
            var v = new Delegate[vn]; k = 0; d.CopyTo(v, ref k);
            // the last occurrence of the value's invocation list goes
            for (int i = sn - vn; i >= 0; i--)
            {
                bool match = true;
                for (int j = 0; j < vn && match; j++) match = Same(s[i + j], v[j]);
                if (!match) continue;
                int rn = sn - vn;
                if (rn == 0) return null;
                if (rn == 1) return i == 0 ? s[vn] : s[0];
                var r = new Delegate[rn];
                int m = 0;
                for (int j = 0; j < sn; j++) if (j < i || j >= i + vn) r[m++] = s[j];
                return WithList(r);
            }
            return this;
        }

        /// <summary>A delegate of this one's type invoking the list in order (its own target and function: the first's).</summary>
        Delegate WithList(Delegate[] list)
        {
            int size = TypeInfo.Size(Rt.TypeId(this));
            var d = Rt.Take<Delegate>(Rt.AllocObject(size < 24 ? 24 : size, Rt.TypeId(this)));
            d._target = list[0]._target;
            d._fn = list[0]._fn;
            d._list = list;
            return d;
        }

        static bool Same(Delegate a, Delegate b) => ReferenceEquals(a._target, b._target) && a._fn == b._fn;

        int Count => _list == null ? 1 : _list.Length;

        void CopyTo(Delegate[] arr, ref int k)
        {
            if (_list == null) { arr[k++] = this; return; }
            for (int i = 0; i < _list.Length; i++) arr[k++] = _list[i];
        }

        public Delegate[] GetInvocationList()
        {
            if (_list == null) return [this];
            var r = new Delegate[_list.Length];
            Array.Copy(_list, r, _list.Length);
            return r;
        }

        /// <summary>The delegate at an index of the invocation list, or null past its end (InvocationListEnumerator).</summary>
        internal Delegate? TryGetAt(int index) =>
            _list == null ? (index == 0 ? this : null) : (uint)index < (uint)_list.Length ? _list[index] : null;

        public static Delegate CreateDelegate(Type type, object? firstArgument, MethodInfo method, bool throwOnBindFailure) => throw TypeInfo.NotYet("Delegate.CreateDelegate");
        public static Delegate? CreateDelegate(Type type, MethodInfo method, bool throwOnBindFailure) => throw TypeInfo.NotYet("Delegate.CreateDelegate");
        public static Delegate? CreateDelegate(Type type, object target, string method, bool ignoreCase, bool throwOnBindFailure) => throw TypeInfo.NotYet("Delegate.CreateDelegate");
        public static Delegate? CreateDelegate(Type type, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllMethods)] Type target, string method, bool ignoreCase, bool throwOnBindFailure) => throw TypeInfo.NotYet("Delegate.CreateDelegate");

        protected virtual object? DynamicInvokeImpl(object?[]? args) => throw TypeInfo.NotYet("Delegate.DynamicInvoke");

        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            if (obj is not Delegate d || !InternalEqualTypes(this, d)) return false;
            if (Count != d.Count) return false;
            if (_list == null) return Same(this, d);
            for (int i = 0; i < _list.Length; i++) if (!Same(_list[i], d._list![i])) return false;
            return true;
        }

        public override int GetHashCode() => _fn ^ (_target == null ? 0 : RuntimeHelpers.GetHashCode(_target));

        protected virtual MethodInfo GetMethodImpl() => throw TypeInfo.NotYet("Delegate.Method");

        internal static bool InternalEqualTypes(object source, object value) => Rt.TypeId(source) == Rt.TypeId(value);
    }
}
