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

        // cs2wasm: a delegate of the type over the method's reflection thunk (the compiler makes such delegates for the
        // delegate types the program names: Internal.Runtime.Augments.DynamicDelegateAugments); the method is called as
        // a delegate call would, without wrapping its exceptions
        public static Delegate CreateDelegate(Type type, object? firstArgument, MethodInfo method, bool throwOnBindFailure)
            => Bind(type, firstArgument, method, throwOnBindFailure, closedAllowed: true);

        static Delegate Bind(Type type, object? firstArgument, MethodInfo method, bool throwOnBindFailure, bool closedAllowed)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(method);
            if (type is not RuntimeType rt || !rt.IsSubclassOf(typeof(MulticastDelegate)))
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));
            int n = rt.GetMethod("Invoke")!.GetParameters().Length;
            int k = method.GetParameters().Length;
            Func<object?[], object?>? h = null;
            if (!method.IsStatic)
            {
                if (k == n) h = args => Call(method, firstArgument, args);
                else if (k == n - 1 && firstArgument == null) h = args => Call(method, args[0], args[1..]); // open instance
            }
            else if (k == n && firstArgument == null) h = args => Call(method, null, args);
            else if (k == n + 1 && closedAllowed) h = args => { var all = new object?[args.Length + 1]; all[0] = firstArgument; Array.Copy(args, 0, all, 1, args.Length); return Call(method, null, all); };
            if (h == null)
            {
                if (throwOnBindFailure) throw new ArgumentException(SR.Arg_DlgtTargMeth);
                return null!;
            }
            return Internal.Runtime.Augments.DynamicDelegateAugments.CreateObjectArrayDelegate(type, h);
        }

        static object? Call(MethodInfo m, object? target, object?[] args) => m.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null);

        // (static, or open instance: nothing is bound first)
        public static Delegate? CreateDelegate(Type type, MethodInfo method, bool throwOnBindFailure) => Bind(type, null, method, throwOnBindFailure, closedAllowed: false);
        public static Delegate? CreateDelegate(Type type, object target, string method, bool ignoreCase, bool throwOnBindFailure)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(method);
            var m = FindForDelegate(type, target.GetType(), method, ignoreCase, BindingFlags.Instance);
            if (m == null) { if (throwOnBindFailure) throw new ArgumentException(SR.Arg_DlgtTargMeth); return null; }
            return Bind(type, target, m, throwOnBindFailure, closedAllowed: true);
        }

        public static Delegate? CreateDelegate(Type type, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllMethods)] Type target, string method, bool ignoreCase, bool throwOnBindFailure)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(method);
            if (target.ContainsGenericParameters) throw new ArgumentException(SR.Arg_UnboundGenParam, nameof(target));
            var m = FindForDelegate(type, target, method, ignoreCase, BindingFlags.Static);
            if (m == null) { if (throwOnBindFailure) throw new ArgumentException(SR.Arg_DlgtTargMeth); return null; }
            return Bind(type, null, m, throwOnBindFailure, closedAllowed: false);
        }

        /// <summary>The method of that name whose parameters match the delegate's Invoke (by count and type).</summary>
        static MethodInfo? FindForDelegate(Type type, Type target, string name, bool ignoreCase, BindingFlags kind)
        {
            if (type is not RuntimeType rt || !rt.IsSubclassOf(typeof(MulticastDelegate)))
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));
            var invoke = rt.GetMethod("Invoke")!;
            var want = invoke.GetParameters();
            var flags = kind | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy | (ignoreCase ? BindingFlags.IgnoreCase : 0);
            foreach (var m in target.GetMethods(flags))
            {
                if (!string.Equals(m.Name, name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                var ps = m.GetParameters();
                if (ps.Length != want.Length || !invoke.ReturnType.IsAssignableFrom(m.ReturnType)) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++) ok = ps[i].ParameterType.IsAssignableFrom(want[i].ParameterType);
                if (ok) return m;
            }
            return null;
        }

        // cs2wasm: through the compiler's thunk for the delegate type's Invoke (Compiler/DynamicDelegates.cs)
        protected virtual object? DynamicInvokeImpl(object?[]? args)
        {
            int n = Cs2Wasm.Runtime.Rt.DelegateArity(this);
            if (n < 0) throw new PlatformNotSupportedException("cs2wasm: DynamicInvoke of " + GetType() + " (a delegate with ref or span parameters)");
            if ((args?.Length ?? 0) != n) throw new System.Reflection.TargetParameterCountException(SR.Arg_ParmCnt);
            try { return Cs2Wasm.Runtime.Rt.DynamicInvokeRaw(this, args); }
            catch (Exception e) { throw new System.Reflection.TargetInvocationException(e); }
        }

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
