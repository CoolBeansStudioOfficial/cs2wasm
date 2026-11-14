// Core types the C# compiler needs to bind the library as a corlib, and small types programs use.
#pragma warning disable CS0626, CS0436, CS0660, CS0661, CS0659, CS0114, CS0108, CS0169, CS0649
using Cs2Wasm.Runtime;

namespace System
{
    public struct Void { }

    public abstract class Delegate
    {
        // layout used by the compiler: target at 12, function table index at 16, invocation list at 20
        internal object _target;
        internal int _fn;
        internal Delegate[] _list;

        public object Target => _target;

        public static Delegate Combine(Delegate a, Delegate b)
        {
            if (a == null) return b;
            if (b == null) return a;
            int n = a.Count + b.Count;
            var list = new Delegate[n];
            int k = 0;
            a.CopyTo(list, ref k);
            b.CopyTo(list, ref k);
            var d = Rt.Take<Delegate>(Rt.AllocObject(24, Rt.TypeId(a)));
            d._target = list[0]._target;
            d._fn = list[0]._fn;
            d._list = list;
            return d;
        }

        public static Delegate Remove(Delegate source, Delegate value)
        {
            if (source == null) return null;
            if (value == null) return source;
            int sn = source.Count, vn = value.Count;
            var s = new Delegate[sn]; int k = 0; source.CopyTo(s, ref k);
            var v = new Delegate[vn]; k = 0; value.CopyTo(v, ref k);
            // remove the last occurrence of the value's invocation list
            for (int i = sn - vn; i >= 0; i--)
            {
                bool match = true;
                for (int j = 0; j < vn; j++) if (!Same(s[i + j], v[j])) { match = false; break; }
                if (!match) continue;
                int rn = sn - vn;
                if (rn == 0) return null;
                if (rn == 1) return i == 0 ? s[vn] : s[0];
                var r = new Delegate[rn];
                int m = 0;
                for (int j = 0; j < sn; j++) if (j < i || j >= i + vn) r[m++] = s[j];
                var d = Rt.Take<Delegate>(Rt.AllocObject(24, Rt.TypeId(source)));
                d._target = r[0]._target; d._fn = r[0]._fn; d._list = r;
                return d;
            }
            return source;
        }

        static bool Same(Delegate a, Delegate b) => Rt.AddressOf(a._target) == Rt.AddressOf(b._target) && a._fn == b._fn;

        int Count => _list == null ? 1 : _list.Length;

        void CopyTo(Delegate[] arr, ref int k)
        {
            if (_list == null) { arr[k++] = this; return; }
            for (int i = 0; i < _list.Length; i++) arr[k++] = _list[i];
        }

        public override bool Equals(object obj)
        {
            if (obj is not Delegate d || Rt.TypeId(d) != Rt.TypeId(this)) return false;
            if (Count != d.Count) return false;
            if (_list == null) return Same(this, d);
            for (int i = 0; i < _list.Length; i++) if (!Same(_list[i], d._list[i])) return false;
            return true;
        }
        public override int GetHashCode() => _fn;
        public static bool operator ==(Delegate d1, Delegate d2) => d1 is null ? d2 is null : d1.Equals(d2);
        public static bool operator !=(Delegate d1, Delegate d2) => !(d1 == d2);

        public Delegate[] GetInvocationList()
        {
            if (_list == null) return [this];
            var r = new Delegate[_list.Length];
            for (int i = 0; i < r.Length; i++) r[i] = _list[i];
            return r;
        }
    }

    public abstract class MulticastDelegate : Delegate
    {
        public static bool operator ==(MulticastDelegate d1, MulticastDelegate d2) => (Delegate)d1 == (Delegate)d2;
        public static bool operator !=(MulticastDelegate d1, MulticastDelegate d2) => !((Delegate)d1 == (Delegate)d2);
        public override bool Equals(object obj) => base.Equals(obj);
        public override int GetHashCode() => base.GetHashCode();
    }

    public struct IntPtr
    {
        int _value;
        public static int Size => 4;
        public override string ToString() => Number.Int32ToString(_value);
    }

    public struct UIntPtr
    {
        uint _value;
        public static int Size => 4;
        public override string ToString() => Number.UInt32ToString(_value);
    }

    public struct RuntimeTypeHandle { }
    public struct RuntimeFieldHandle { }
    public struct RuntimeMethodHandle { }
    public struct TypedReference { }
    public struct ArgIterator { }

    public struct Nullable<T> where T : struct
    {
        readonly bool hasValue;
        internal T value;
        public Nullable(T value) { this.value = value; hasValue = true; }
        public bool HasValue => hasValue;
        public T Value
        {
            get
            {
                if (!hasValue) throw new InvalidOperationException("Cannot read the Value property of a Nullable object that has no value. Check HasValue before reading Value.");
                return value;
            }
        }
        public T GetValueOrDefault() => value;
        public T GetValueOrDefault(T defaultValue) => hasValue ? value : defaultValue;
        public override string ToString() => hasValue ? value.ToString() : "";
        public override bool Equals(object other)
        {
            if (!hasValue) return other == null;
            if (other == null) return false;
            return value.Equals(other);
        }
        public override int GetHashCode() => hasValue ? value.GetHashCode() : 0;
        public static implicit operator T?(T value) => new T?(value);
        public static explicit operator T(T? value) => value.Value;
    }

    // ---- attributes the C# compiler uses ----
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class AttributeUsageAttribute : Attribute
    {
        public AttributeUsageAttribute(AttributeTargets validOn) { }
        public bool AllowMultiple { get; set; }
        public bool Inherited { get; set; }
    }

    [Flags]
    public enum AttributeTargets
    {
        Assembly = 1, Module = 2, Class = 4, Struct = 8, Enum = 16, Constructor = 32, Method = 64, Property = 128,
        Field = 256, Event = 512, Interface = 1024, Parameter = 2048, Delegate = 4096, ReturnValue = 8192,
        GenericParameter = 16384, All = 32767,
    }

    [AttributeUsage(AttributeTargets.Parameter)] public sealed class ParamArrayAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Enum)] public class FlagsAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class ObsoleteAttribute : Attribute { public ObsoleteAttribute() { } public ObsoleteAttribute(string message) { } public ObsoleteAttribute(string message, bool error) { } }
    [AttributeUsage(AttributeTargets.All)] public sealed class SerializableAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class CLSCompliantAttribute : Attribute { public CLSCompliantAttribute(bool isCompliant) { } }
    [AttributeUsage(AttributeTargets.Field)] public class ThreadStaticAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class NonSerializedAttribute : Attribute { }

    public enum StringComparison
    {
        CurrentCulture = 0, CurrentCultureIgnoreCase = 1, InvariantCulture = 2, InvariantCultureIgnoreCase = 3, Ordinal = 4, OrdinalIgnoreCase = 5,
    }

    [Flags]
    public enum StringSplitOptions { None = 0, RemoveEmptyEntries = 1, TrimEntries = 2 }

    public enum MidpointRounding { ToEven = 0, AwayFromZero = 1, ToZero = 2, ToNegativeInfinity = 3, ToPositiveInfinity = 4 }

    public interface IDisposable { void Dispose(); }
    public interface IComparable { int CompareTo(object obj); }
    public interface IComparable<in T> { int CompareTo(T other); }
    public interface IEquatable<T> { bool Equals(T other); }
    public interface IFormatProvider { object GetFormat(Type formatType); }
    public interface IFormattable { string ToString(string format, IFormatProvider formatProvider); }
    public interface ICloneable { object Clone(); }

    public delegate void Action();
    public delegate void Action<in T>(T obj);
    public delegate void Action<in T1, in T2>(T1 arg1, T2 arg2);
    public delegate void Action<in T1, in T2, in T3>(T1 arg1, T2 arg2, T3 arg3);
    public delegate void Action<in T1, in T2, in T3, in T4>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
    public delegate TResult Func<out TResult>();
    public delegate TResult Func<in T, out TResult>(T arg);
    public delegate TResult Func<in T1, in T2, out TResult>(T1 arg1, T2 arg2);
    public delegate TResult Func<in T1, in T2, in T3, out TResult>(T1 arg1, T2 arg2, T3 arg3);
    public delegate TResult Func<in T1, in T2, in T3, in T4, out TResult>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);
    public delegate bool Predicate<in T>(T obj);
    public delegate int Comparison<in T>(T x, T y);
    public delegate TOutput Converter<in TInput, out TOutput>(TInput input);
    public delegate void EventHandler(object sender, EventArgs e);
    public delegate void EventHandler<TEventArgs>(object sender, TEventArgs e);

    public class EventArgs { public static readonly EventArgs Empty = new EventArgs(); }

    public static class Environment
    {
        public static string NewLine => "\n";
        public static int TickCount => (int)(Engine.MonotonicNs() / 1000000);
        public static long TickCount64 => Engine.MonotonicNs() / 1000000;
        public static int ProcessorCount => 1;
        public static int CurrentManagedThreadId => 1;
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Property)] public sealed class IndexerNameAttribute : Attribute { public IndexerNameAttribute(string indexerName) { } }
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly)] public sealed class ExtensionAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class CompilerGeneratedAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class IsReadOnlyAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class IsByRefLikeAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)] public sealed class IsUnmanagedAttribute : Attribute { }
    public static class IsExternalInit { }
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)] public sealed class MethodImplAttribute : Attribute { public MethodImplAttribute() { } public MethodImplAttribute(MethodImplOptions o) { } }
    [Flags] public enum MethodImplOptions { NoInlining = 8, AggressiveInlining = 256, AggressiveOptimization = 512, InternalCall = 4096 }
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class CallerMemberNameAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class CallerFilePathAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class CallerLineNumberAttribute : Attribute { }

    public sealed class SwitchExpressionException : InvalidOperationException
    {
        public SwitchExpressionException() : base("Non-exhaustive switch expression failed to match its input.") { }
        public SwitchExpressionException(object unmatchedValue) : base("Non-exhaustive switch expression failed to match its input.") { }
    }

    public static class RuntimeHelpers
    {
        public static int GetHashCode(object o) => o == null ? 0 : Rt.HashOf(o);
        public static bool IsReferenceOrContainsReferences<T>() => Rt.ContainsReferences<T>();
        public static int OffsetToStringData => 16;
    }
}

namespace System.Reflection
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public sealed class DefaultMemberAttribute : Attribute { public DefaultMemberAttribute(string memberName) { } }
}

namespace System.Runtime.InteropServices
{
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class InAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class OutAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnmanagedCallersOnlyAttribute : Attribute
    {
        public string EntryPoint;
        public Type[] CallConvs;
    }
}
