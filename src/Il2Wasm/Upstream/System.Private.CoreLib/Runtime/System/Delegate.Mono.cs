// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's runtime half of Delegate, over the compiler's delegates (Calls.cs): the target at 12, the
// function (its table index) at 16, and for a combined delegate its invocation list at 20. The compiler creates and
// invokes them; this file combines, compares and lists them. Delegates over methods found by reflection are not
// supported yet.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2Wasm.Runtime;
using TypeInfo = Il2Wasm.Runtime.TypeInfo;

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

        // il2wasm: a lambda that captures nothing is a method of its class's singleton (`<>c`) under .NET, one that
        // captures `this` alone a method of that object, a local function that captures nothing a static method:
        // the compiler knows such a delegate by its function, and says what its Target is (the delegate holds what
        // the function's code is called with, which is not that object)
        internal virtual object? GetTarget() => _list == null ? (Binding is { } b ? b.Target : TargetOf(_fn, _target)) : _list[^1].GetTarget();

        /// <summary>What .NET's Target is for a delegate of the function that holds the object: the object itself
        /// but for the functions of lambdas and local functions (Compiler/GeneratedReflection.cs EmitDelegateSingleton).</summary>
        [Il2Wasm.Runtime.Intrinsic] static extern object? TargetOf(int fn, object? target);

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

        // il2wasm: a delegate of the type over the method's reflection thunk (the compiler makes such delegates for the
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
            // (the count from the compiler's table: a reflection lookup of Invoke would need a record for every delegate type)
            int n = Il2Wasm.Runtime.TypeInfo.DelegateArity(rt.TypeId);
            if (n < 0) throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(type));
            int k = method.GetParameters().Length;
            int mode = -1;
            if (!method.IsStatic)
            {
                if (k == n) mode = DelegateBinding.Closed;
                else if (k == n - 1 && firstArgument == null) mode = DelegateBinding.OpenInstance;
            }
            else if (k == n && firstArgument == null) mode = DelegateBinding.Static;
            else if (k == n + 1 && closedAllowed) mode = DelegateBinding.ClosedStatic;
            if (mode < 0)
            {
                if (throwOnBindFailure) throw new ArgumentException(SR.Arg_DlgtTargMeth);
                return null!;
            }
            return Internal.Runtime.Augments.DynamicDelegateAugments.CreateObjectArrayDelegate(type, new DelegateBinding(method, firstArgument, mode).Invoke);
        }

        /// <summary>il2wasm: what a delegate CreateDelegate made calls (its Method and Target).</summary>
        internal sealed class DelegateBinding(MethodInfo method, object? first, int mode)
        {
            public const int Closed = 0, OpenInstance = 1, Static = 2, ClosedStatic = 3;
            public readonly MethodInfo Method = method;
            public object? Target => mode is Closed or ClosedStatic ? first : null;

            public object? Invoke(object?[] args)
            {
                switch (mode)
                {
                    case Closed: return Call(Method, first, args);
                    case OpenInstance: return Call(Method, args[0], args[1..]);
                    case Static: return Call(Method, null, args);
                    default:
                        var all = new object?[args.Length + 1];
                        all[0] = first;
                        Array.Copy(args, 0, all, 1, args.Length);
                        return Call(Method, null, all);
                }
            }
        }

        DelegateBinding? Binding => _target is Delegate { _target: DelegateBinding b } ? b : null;

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

        // il2wasm: through the compiler's thunk for the delegate type's Invoke (Compiler/DynamicDelegates.cs)
        protected virtual object? DynamicInvokeImpl(object?[]? args)
        {
            int n = Il2Wasm.Runtime.Rt.DelegateArity(this);
            if (n < 0) throw new PlatformNotSupportedException("il2wasm: DynamicInvoke of " + GetType() + " (a delegate with ref or span parameters)");
            if ((args?.Length ?? 0) != n) throw new System.Reflection.TargetParameterCountException(SR.Arg_ParmCnt);
            // (the arguments as their parameters take them, before the call: an ArgumentException is not wrapped)
            object?[] converted = Il2Wasm.Runtime.Rt.DynamicArguments(this, args);
            try { return Il2Wasm.Runtime.Rt.DynamicInvokeRaw(this, converted); }
            catch (Exception e) { throw new System.Reflection.TargetInvocationException(e); }
        }

        /// <summary>il2wasm: what CoreCLR's argument check does for a parameter of a value type (called by the compiler's
        /// dynamic argument thunks): the argument itself when it is of the type, a primitive widened to it (typeCode:
        /// the parameter's System.TypeCode, 0 for other types), null for a null argument (the thunk makes the default
        /// value), otherwise ArgumentException.</summary>
        internal static object? DynamicValueArgument(object? arg, int typeId, int typeCode, string paramName)
        {
            if (arg == null || Il2Wasm.Runtime.Rt.TypeId(arg) == typeId) return arg;
            if (typeCode != 0 && WidenedPrimitive(arg, typeCode) is { } widened) return widened;
            DynamicArgumentFailed(arg, paramName);
            return null;
        }

        /// <summary>il2wasm: the check of an argument for a reference parameter (called by the compiler's dynamic argument
        /// thunks when the argument is not an instance of the parameter's type).</summary>
        internal static void DynamicArgumentFailed(object arg, string paramName) =>
            throw new ArgumentException("Object of type '" + arg.GetType().ToString() + "' cannot be converted to type '" + paramName + "'.");

        /// <summary>The primitive widened to the type (a widening conversion, CoreCLR's CanChangePrimitive), or null.</summary>
        static object? WidenedPrimitive(object arg, int typeCode)
        {
            int from = arg switch
            {
                bool => (int)TypeCode.Boolean, char => (int)TypeCode.Char, sbyte => (int)TypeCode.SByte, byte => (int)TypeCode.Byte,
                short => (int)TypeCode.Int16, ushort => (int)TypeCode.UInt16, int => (int)TypeCode.Int32, uint => (int)TypeCode.UInt32,
                long => (int)TypeCode.Int64, ulong => (int)TypeCode.UInt64, float => (int)TypeCode.Single, double => (int)TypeCode.Double,
                _ => 0,
            };
            if (!PrimitiveWidens(from, typeCode)) return null;
            bool signed = from is (int)TypeCode.SByte or (int)TypeCode.Int16 or (int)TypeCode.Int32 or (int)TypeCode.Int64;
            ulong u = arg switch { byte v => v, char v => v, ushort v => v, uint v => v, ulong v => v, _ => 0 };
            long l = signed ? arg switch { sbyte v => v, short v => v, int v => v, long v => v, _ => 0 } : (long)u;
            switch ((TypeCode)typeCode)
            {
                case TypeCode.Char: return (char)u;
                case TypeCode.Int16: return (short)l;
                case TypeCode.Int32: return (int)l;
                case TypeCode.Int64: return l;
                case TypeCode.UInt16: return (ushort)u;
                case TypeCode.UInt32: return (uint)u;
                case TypeCode.UInt64: return u;
                case TypeCode.Single:
                    return from == (int)TypeCode.Single ? (float)arg : signed ? (float)l : (float)u;
                case TypeCode.Double:
                    return from == (int)TypeCode.Single ? (double)(float)arg : signed ? (double)l : (double)u;
            }
            return null;
        }

        // the primitive widenings: from each source type, the types it widens to (the source itself included)
        static bool PrimitiveWidens(int from, int to)
        {
            if (from == to) return false; // (the same type: the argument is of it already)
            return (TypeCode)from switch
            {
                TypeCode.Char => to is (int)TypeCode.UInt16 or (int)TypeCode.UInt32 or (int)TypeCode.Int32 or (int)TypeCode.UInt64 or (int)TypeCode.Int64 or (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.SByte => to is (int)TypeCode.Int16 or (int)TypeCode.Int32 or (int)TypeCode.Int64 or (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.Byte => to is (int)TypeCode.Char or (int)TypeCode.Int16 or (int)TypeCode.UInt16 or (int)TypeCode.Int32 or (int)TypeCode.UInt32 or (int)TypeCode.Int64 or (int)TypeCode.UInt64 or (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.Int16 => to is (int)TypeCode.Int32 or (int)TypeCode.Int64 or (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.UInt16 => to is (int)TypeCode.UInt32 or (int)TypeCode.Int32 or (int)TypeCode.UInt64 or (int)TypeCode.Int64 or (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.Int32 => to is (int)TypeCode.Int64 or (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.UInt32 => to is (int)TypeCode.UInt64 or (int)TypeCode.Int64 or (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.Int64 => to is (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.UInt64 => to is (int)TypeCode.Single or (int)TypeCode.Double,
                TypeCode.Single => to is (int)TypeCode.Double,
                _ => false,
            };
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

        // il2wasm: the method's reflection record entry, found from the delegate's function (the compiler keeps the
        // methods of the program's method-group delegates when the program asks for Delegate.Method)
        protected virtual MethodInfo GetMethodImpl()
        {
            if (_list != null) return _list[^1].Method;
            if (Binding is { } b) return b.Method;
            int e = Rt.DelegateMethodEntry(_fn);
            if (e == 0) throw TypeInfo.NotYet("Delegate.Method of a lambda or local function");
            return new RuntimeMethodInfo(e, Meta.S(Meta.I(e, 0)), RuntimeType.FromId(Meta.I(e, 9)));
        }

        internal static bool InternalEqualTypes(object source, object value) => Rt.TypeId(source) == Rt.TypeId(value);
    }
}
