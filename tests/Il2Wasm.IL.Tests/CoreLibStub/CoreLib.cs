// A tiny hand-written System.Private.CoreLib for the IL front end's tests, until the IL CoreLib (claude/il-corelib)
// lands. It is compiled by Roslyn to IL and then by il2wasm, like any program's code. Its runtime part (Il2Wasm.Runtime)
// keeps the names and signatures of today's runtime (Boundary/Runtime.cs) where it has the same function, so tests move
// to the real CoreLib unchanged.
#pragma warning disable CS0626, CS0660, CS0661, CS0659, CS8597, CS0169, CS0649, CS0414
namespace System
{
    public class Object
    {
        public Object() { }
        ~Object() { }
        public virtual string ToString() => GetType().FullNameForToString();
        public virtual bool Equals(object obj) => (object)this == obj;
        public virtual int GetHashCode() => Il2Wasm.Runtime.Rt.AddressOf(this);
        public Type GetType() => new Type(Il2Wasm.Runtime.Rt.TypeId(this));
        protected object MemberwiseClone() => null;
    }

    public abstract class ValueType { public override string ToString() => "ValueType"; }
    public abstract class Enum : ValueType { }
    public sealed class Type
    {
        readonly int _id;
        internal Type(int id) { _id = id; }
        internal string FullNameForToString() => Il2Wasm.Runtime.Rt.TypeNameById(_id);
        public override string ToString() => FullNameForToString();
        public string Name => Il2Wasm.Runtime.Rt.TypeNameById(_id);
    }
    public struct Void { }
    public struct Boolean { public override string ToString() => this ? "True" : "False"; }
    public struct Char { public override string ToString() => string.FromChar(this); }
    public struct SByte { }
    public struct Byte { }
    public struct Int16 { }
    public struct UInt16 { }
    public struct Int32
    {
        public const int MaxValue = 0x7fffffff, MinValue = unchecked((int)0x80000000);
        public override string ToString() => Number.Format(this);
    }
    public struct UInt32 { }
    public struct Int64 { public override string ToString() => Number.Format(this); }
    public struct UInt64 { }
    public struct IntPtr { }
    public struct UIntPtr { }
    public struct Single { }
    public struct Double { }
    public struct Nullable<T> where T : struct { readonly bool hasValue; T value; }
    public struct RuntimeTypeHandle { }
    public struct RuntimeMethodHandle { }
    public struct RuntimeFieldHandle { }
    public struct TypedReference { }
    public ref struct RuntimeArgumentHandle { }

    public abstract class Array
    {
        public int Length => Il2Wasm.Runtime.Mem.LoadI32(Il2Wasm.Runtime.Rt.AddressOf(this) + 12);
    }
    public abstract class Delegate { }
    public abstract class MulticastDelegate : Delegate { }
    public interface IDisposable { void Dispose(); }

    public sealed class String
    {
        // (the layout il2wasm's runtime uses: the length at 12, the characters from 16)
        readonly int _stringLength;
        char _firstChar;
        public int Length => _stringLength;
        [System.Runtime.CompilerServices.IndexerName("Chars")]
        public char this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_stringLength) throw new IndexOutOfRangeException();
                return (char)Il2Wasm.Runtime.Mem.LoadU16(Il2Wasm.Runtime.Rt.AddressOf(this) + 16 + index * 2);
            }
        }
        internal static string Alloc(int length) => Il2Wasm.Runtime.Rt.Take<string>(Il2Wasm.Runtime.Rt.AllocString(length));
        internal int CharsAddress => Il2Wasm.Runtime.Rt.AddressOf(this) + 16;
        internal static string FromChar(char c)
        {
            var s = Alloc(1);
            Il2Wasm.Runtime.Mem.StoreU16(s.CharsAddress, c);
            return s;
        }
        public static string Concat(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";
            var s = Alloc(a.Length + b.Length);
            Il2Wasm.Runtime.Mem.Copy(s.CharsAddress, a.CharsAddress, a.Length * 2);
            Il2Wasm.Runtime.Mem.Copy(s.CharsAddress + a.Length * 2, b.CharsAddress, b.Length * 2);
            return s;
        }
        public static string Concat(string a, string b, string c) => Concat(Concat(a, b), c);
        public static string Concat(string a, string b, string c, string d) => Concat(Concat(a, b), Concat(c, d));
        public static string Concat(params string[] values)
        {
            string r = "";
            for (int i = 0; i < values.Length; i++) r = Concat(r, values[i]);
            return r;
        }
        public static string Concat(object a, object b, object c) => Concat(a?.ToString(), b?.ToString(), c?.ToString());
        public static string Concat(object a, object b) => Concat(a?.ToString(), b?.ToString());
        public static bool operator ==(string a, string b) => Equals(a, b);
        public static bool operator !=(string a, string b) => !Equals(a, b);
        public static bool Equals(string a, string b)
        {
            if ((object)a == b) return true;
            if (a is null || b is null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        public override bool Equals(object o) => o is string s && Equals(this, s);
        public override string ToString() => this;
        public override int GetHashCode()
        {
            int h = 5381;
            for (int i = 0; i < Length; i++) h = h * 33 + this[i];
            return h;
        }
    }

    static class Number
    {
        public static string Format(long v)
        {
            if (v == 0) return "0";
            bool neg = v < 0;
            ulong u = neg ? (ulong)(-(v + 1)) + 1 : (ulong)v;
            int n = 0;
            for (ulong t = u; t != 0; t /= 10) n++;
            var s = string.Alloc(n + (neg ? 1 : 0));
            int a = s.CharsAddress;
            if (neg) Il2Wasm.Runtime.Mem.StoreU16(a, '-');
            for (int i = n - 1 + (neg ? 1 : 0); u != 0; i--, u /= 10) Il2Wasm.Runtime.Mem.StoreU16(a + i * 2, (int)('0' + u % 10));
            return s;
        }
    }

    public static class Console
    {
        public static void WriteLine(string s)
        {
            if (s != null) Il2Wasm.Runtime.Engine.Write(s.CharsAddress, s.Length);
            Il2Wasm.Runtime.Engine.Write(Il2Wasm.Runtime.Rt.AddressOf("\n") + 16, 1);
        }
        public static void WriteLine(object o) => WriteLine(o?.ToString());
        public static void WriteLine(int v) => WriteLine(Number.Format(v));
        public static void WriteLine(long v) => WriteLine(Number.Format(v));
        public static void WriteLine(bool v) => WriteLine(v ? "True" : "False");
        public static void WriteLine(char v) => WriteLine(string.FromChar(v));
        public static void WriteLine() => WriteLine("");
        public static void Write(string s) { if (s != null) Il2Wasm.Runtime.Engine.Write(s.CharsAddress, s.Length); }
        public static void Write(int v) => Write(Number.Format(v));
    }

    public static class Math
    {
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Abs(int a) => a < 0 ? -a : a;
    }

    public class Exception
    {
        string _message;
        public Exception() { }
        public Exception(string message) { _message = message; }
        public virtual string Message => _message ?? ("Exception of type '" + GetType().ToString() + "' was thrown.");
        public override string ToString() => GetType().ToString() + ": " + Message;
    }
    public class SystemException : Exception { public SystemException() { } public SystemException(string m) : base(m) { } }
    public class ArithmeticException : SystemException { public ArithmeticException() : base("Arithmetic operation resulted in an overflow.") { } public ArithmeticException(string m) : base(m) { } }
    public class DivideByZeroException : ArithmeticException { public DivideByZeroException() : base("Attempted to divide by zero.") { } }
    public class OverflowException : ArithmeticException { public OverflowException() : base("Arithmetic operation resulted in an overflow.") { } }
    public class IndexOutOfRangeException : SystemException { public IndexOutOfRangeException() : base("Index was outside the bounds of the array.") { } }
    public class NullReferenceException : SystemException { public NullReferenceException() : base("Object reference not set to an instance of an object.") { } }
    public class InvalidCastException : SystemException { public InvalidCastException() : base("Specified cast is not valid.") { } }
    public class InvalidOperationException : SystemException { public InvalidOperationException() { } public InvalidOperationException(string m) : base(m) { } }
    public class ArgumentException : SystemException { public ArgumentException() { } public ArgumentException(string m) : base(m) { } }
    public class ArrayTypeMismatchException : SystemException { public ArrayTypeMismatchException() : base("Attempted to access an element as a type incompatible with the array.") { } }
    public class OutOfMemoryException : SystemException { }

    public class Attribute { }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AttributeUsageAttribute : Attribute
    {
        public AttributeUsageAttribute(AttributeTargets t) { }
        public bool AllowMultiple { get; set; }
        public bool Inherited { get; set; }
    }
    public enum AttributeTargets { Assembly = 1, Module = 2, Class = 4, Struct = 8, Enum = 16, Constructor = 32, Method = 64, Property = 128, Field = 256, Event = 512, Interface = 1024, Parameter = 2048, Delegate = 4096, ReturnValue = 8192, GenericParameter = 16384, All = 32767 }
    public sealed class ParamArrayAttribute : Attribute { }
    public sealed class FlagsAttribute : Attribute { }
    public sealed class ObsoleteAttribute : Attribute { }
}

namespace System.Collections
{
    public interface IEnumerable { IEnumerator GetEnumerator(); }
    public interface IEnumerator { bool MoveNext(); object Current { get; } void Reset(); }
}

namespace System.Runtime.CompilerServices
{
    public sealed class IndexerNameAttribute : Attribute { public IndexerNameAttribute(string n) { } }
    public sealed class CompilerGeneratedAttribute : Attribute { }
    public sealed class IsReadOnlyAttribute : Attribute { }
    public sealed class ExtensionAttribute : Attribute { }
    public static class RuntimeFeature
    {
        public const string DefaultImplementationsOfInterfaces = nameof(DefaultImplementationsOfInterfaces);
        public const string VirtualStaticsInInterfaces = nameof(VirtualStaticsInInterfaces);
        public const string CovariantReturnsOfClasses = nameof(CovariantReturnsOfClasses);
        public const string UnmanagedSignatureCallingConvention = nameof(UnmanagedSignatureCallingConvention);
        public const string NumericIntPtr = nameof(NumericIntPtr);
        public const string ByRefFields = nameof(ByRefFields);
        public const string ByRefLikeGenerics = nameof(ByRefLikeGenerics);
    }
    public static class RuntimeHelpers
    {
        public static int OffsetToStringData => 16;
    }
}

namespace System.Runtime.InteropServices
{
    public sealed class StructLayoutAttribute : Attribute
    {
        public StructLayoutAttribute(LayoutKind k) { }
        public int Pack, Size;
    }
    public enum LayoutKind { Sequential = 0, Explicit = 2, Auto = 3 }
    public sealed class FieldOffsetAttribute : Attribute { public FieldOffsetAttribute(int o) { } }
}

namespace System.Reflection
{
    public sealed class DefaultMemberAttribute : Attribute { public DefaultMemberAttribute(string n) { } }
}

namespace Il2Wasm.Runtime
{
    /// <summary>On a method: emitted inline by the compiler (as Boundary/Runtime.cs).</summary>
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Field)]
    public sealed class IntrinsicAttribute : System.Attribute { }

    /// <summary>A WASM import of the engine (module, name).</summary>
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class ImportAttribute : System.Attribute { public ImportAttribute(string module, string name) { } }

    public static class Mem
    {
        [Intrinsic] public static extern int LoadI32(int addr);
        [Intrinsic] public static extern int LoadU8(int addr);
        [Intrinsic] public static extern int LoadU16(int addr);
        [Intrinsic] public static extern long LoadI64(int addr);
        [Intrinsic] public static extern void StoreI32(int addr, int v);
        [Intrinsic] public static extern void StoreU8(int addr, int v);
        [Intrinsic] public static extern void StoreU16(int addr, int v);
        [Intrinsic] public static extern void StoreI64(int addr, long v);
        [Intrinsic] public static extern void Copy(int dst, int src, int n);
        [Intrinsic] public static extern void Fill(int dst, int value, int n);
        [Intrinsic] public static extern int Size();
        [Intrinsic] public static extern int Grow(int pages);
    }

    public static class Engine
    {
        [Import("il2wasm", "write")] public static extern void Write(int ptr, int len);
        [Import("il2wasm", "report_exception")] public static extern void ReportException(int typePtr, int typeLen, int msgPtr, int msgLen, int stackPtr, int stackLen);
    }

    public static class ThrowHelpers
    {
        public static void NullReference() => throw new System.NullReferenceException();
        public static void IndexOutOfRange() => throw new System.IndexOutOfRangeException();
        public static void Overflow() => throw new System.OverflowException();
        public static void DivideByZero() => throw new System.DivideByZeroException();
        public static void InvalidCast(object o, string target) => throw new System.InvalidCastException();
        public static void ArrayTypeMismatch() => throw new System.ArrayTypeMismatchException();
    }

    public static class Rt
    {
        [Intrinsic] public static extern int AddressOf(object o);
        /// <summary>Wraps an address whose count the caller owns.</summary>
        [Intrinsic] public static extern T Take<T>(int addr);
        /// <summary>Reads an address as a borrowed reference.</summary>
        [Intrinsic] public static extern T Borrow<T>(int addr);
        [Intrinsic] public static extern int TypeId(object o);
        [Intrinsic] public static extern int TypeIdOf<T>();
        [Intrinsic] public static extern int HeapBase();
        [Intrinsic] public static extern void Unreachable();
        /// <summary>The name of a type by its id (the compiler's table).</summary>
        [Intrinsic] public static extern string TypeNameById(int id);

        // The allocator: blocks of 8-byte units from a bump pointer, with free lists by size up to 1 KiB. (A stand-in
        // for Boundary/Runtime.cs's, with its entry points' names and signatures.)
        static int s_top, s_end, s_live;
        static int FreeList(int units) => HeapBase() - 1024 + units * 4;

        static int AllocBlock(int size)
        {
            size = (size + 7) & ~7;
            if (size <= 1016)
            {
                int list = FreeList(size >> 3), head = Mem.LoadI32(list);
                if (head != 0) { Mem.StoreI32(list, Mem.LoadI32(head)); Mem.Fill(head, 0, size); s_live++; return head; }
            }
            if (s_top == 0) { s_top = HeapBase(); s_end = Mem.Size() * 65536; }
            int p = s_top;
            if (p + size > s_end)
            {
                int pages = ((p + size - s_end) >> 16) + 16;
                if (Mem.Grow(pages) < 0) throw new System.OutOfMemoryException();
                s_end += pages << 16;
            }
            s_top = p + size;
            s_live++;
            return p;
        }

        static void FreeBlock(int p, int size)
        {
            size = (size + 7) & ~7;
            s_live--;
            if (size > 1016) return;
            int list = FreeList(size >> 3);
            Mem.StoreI32(p, Mem.LoadI32(list));
            Mem.StoreI32(list, p);
        }

        /// <summary>A new object: count 1, its type id, no hash yet, fields zero.</summary>
        public static int AllocObject(int size, int typeId)
        {
            int p = AllocBlock(size);
            Mem.StoreI32(p, 1);
            Mem.StoreI32(p + 4, typeId);
            return p;
        }

        /// <summary>A new array (or string): the header, the length at 12, the elements from 16.</summary>
        public static int AllocArray(int length, int elemSize, int typeId)
        {
            if (length < 0) throw new System.OverflowException();
            int p = AllocBlock(16 + length * elemSize);
            Mem.StoreI32(p, 1);
            Mem.StoreI32(p + 4, typeId);
            Mem.StoreI32(p + 12, length);
            return p;
        }

        public static void FreeObject(int addr, int size) => FreeBlock(addr, size);

        /// <summary>A new string (as the IL CoreLib makes one): type id 1, the length at 12, the characters from 16, a
        /// trailing NUL.</summary>
        public static int AllocString(int length)
        {
            if (length < 0) throw new System.OverflowException();
            int a = AllocObject(16 + 2 * length + 2, 1);
            Mem.StoreI32(a + 12, length);
            return a;
        }

        /// <summary>An exception escaped the entry point: the host is told (type, message).</summary>
        public static void ReportUnhandled(System.Exception e)
        {
            string t = e.GetType().ToString(), m = e.Message;
            Engine.ReportException(t.CharsAddress, t.Length, m.CharsAddress, m.Length, 0, 0);
        }

        /// <summary>Blocks allocated and not freed (the tests' leak check).</summary>
        public static int LiveBlocks => s_live;
    }
}
