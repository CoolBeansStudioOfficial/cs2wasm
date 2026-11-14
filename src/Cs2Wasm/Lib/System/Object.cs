#pragma warning disable CS0626, CS0436, CS0660, CS0661, CS0659, CS0114, CS0108
using Cs2Wasm.Runtime;

namespace System
{
    public class Object
    {
        public Object() { }
        public virtual string ToString() => Rt.TypeName(this);
        public virtual bool Equals(object obj) => Rt.AddressOf(this) == Rt.AddressOf(obj);
        public virtual int GetHashCode() => Rt.HashOf(this);
        public static bool Equals(object objA, object objB)
        {
            if (Rt.AddressOf(objA) == Rt.AddressOf(objB)) return true;
            if (objA == null || objB == null) return false;
            return objA.Equals(objB);
        }
        public static bool ReferenceEquals(object objA, object objB) => Rt.AddressOf(objA) == Rt.AddressOf(objB);
        public Type GetType() => Type.FromId(Rt.TypeId(this));
    }

    public abstract class ValueType
    {
        public override string ToString() => Rt.TypeName(this);
        public override bool Equals(object obj) => Rt.AddressOf(this) == Rt.AddressOf(obj);
        public override int GetHashCode() => 0;
    }

    public abstract class Enum : ValueType
    {
        public override string ToString() => EnumFormat.Format(this);
    }

    internal static class EnumFormat
    {
        [Intrinsic] public static extern int Info(object boxedEnum);

        public static string Format(object e)
        {
            // table: [count][flags][size of underlying] then (value i64, name) pairs
            int t = Info(e);
            long v = Value(e, Mem.LoadI32(t + 8));
            int count = Mem.LoadI32(t);
            for (int i = 0; i < count; i++)
            {
                int entry = t + 16 + i * 16;
                if (Mem.LoadI64(entry) == v) return Rt.Borrow<string>(Mem.LoadI32(entry + 8));
            }
            if (Mem.LoadI32(t + 4) != 0 && v != 0)
            {
                // [Flags]: names of the set bits, highest value first in .NET's order (ascending in output)
                long rest = v;
                string result = null;
                for (int i = count - 1; i >= 0; i--)
                {
                    int entry = t + 16 + i * 16;
                    long ev = Mem.LoadI64(entry);
                    if (ev != 0 && (rest & ev) == ev)
                    {
                        rest &= ~ev;
                        string name = Rt.Borrow<string>(Mem.LoadI32(entry + 8));
                        result = result == null ? name : name + ", " + result;
                    }
                }
                if (rest == 0 && result != null) return result;
            }
            bool unsigned = Mem.LoadI32(t + 12) != 0;
            return unsigned ? Number.UInt64ToString((ulong)v) : Number.Int64ToString(v);
        }

        static long Value(object e, int size)
        {
            int a = Rt.AddressOf(e) + 12;
            if (size == 1) return Mem.LoadI8(a);
            if (size == 2) return Mem.LoadI16(a);
            if (size == 8) return Mem.LoadI64(a + 4);
            return Mem.LoadI32(a);
        }
    }

    public abstract class Attribute { }


    public static class GC
    {
        public static void Collect() { }
        public static void Collect(int generation) { }
        public static void KeepAlive(object obj) { }
        public static void SuppressFinalize(object obj)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            Cs2Wasm.Runtime.ObjectMarks.Set(obj, Cs2Wasm.Runtime.ObjectMarks.FinalizerSuppressed);
        }
        public static void ReRegisterForFinalize(object obj)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            Cs2Wasm.Runtime.ObjectMarks.Clear(obj, Cs2Wasm.Runtime.ObjectMarks.FinalizerSuppressed);
        }
        public static void WaitForPendingFinalizers() { }
        public static long GetTotalMemory(bool forceFullCollection) => Cs2Wasm.Runtime.Rt.HeapUsed();
        public static int CollectionCount(int generation) => 0;
        public static int MaxGeneration => 0;
        public static int GetGeneration(object obj) => 0;
    }
}
