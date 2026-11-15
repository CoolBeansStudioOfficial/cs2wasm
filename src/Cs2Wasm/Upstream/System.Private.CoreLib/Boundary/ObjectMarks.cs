using System.Collections.Generic;

namespace Cs2Wasm.Runtime
{
    /// <summary>
    /// Per-object flags kept beside the heap, by address (section 4.4): a weak GC handle's target, an object whose
    /// finalizer GC.SuppressFinalize skipped. When a marked object's count reaches zero, the destroy path
    /// (rt.on_destroy, compiled in only when the program sets a mark) clears the weak handles to it before its
    /// finalizer would run, as .NET's short weak references are cleared, and skips a suppressed finalizer.
    /// </summary>
    public static class ObjectMarks
    {
        public const int Weak = 1, FinalizerSuppressed = 2;
        static Dictionary<int, int>? s_marks;
        static int s_count, s_busy;

        public static void Set(object o, int flag)
        {
            Rt.RuntimeLock();
            s_busy++;
            s_marks ??= new Dictionary<int, int>();
            int a = Rt.AddressOf(o);
            s_marks.TryGetValue(a, out int f);
            if (f == 0) s_count++;
            s_marks[a] = f | flag;
            s_busy--;
            Rt.RuntimeUnlock();
        }

        public static void Clear(object o, int flag) => ClearAt(Rt.AddressOf(o), flag);

        internal static void ClearAt(int a, int flag)
        {
            Rt.RuntimeLock();
            s_busy++;
            if (s_marks != null && s_marks.TryGetValue(a, out int f))
            {
                f &= ~flag;
                if (f == 0) { s_marks.Remove(a); s_count--; } else s_marks[a] = f;
            }
            s_busy--;
            Rt.RuntimeUnlock();
        }

        /// <summary>The object at `addr` is dying: clear weak handles to it; 0 if its finalizer must not run.</summary>
        public static int OnDestroy(int addr)
        {
            if (s_count == 0 || s_busy != 0) return 1;
            Rt.RuntimeLock();
            s_busy++;
            bool has = s_marks!.TryGetValue(addr, out int f);
            if (has) { s_marks.Remove(addr); s_count--; }
            s_busy--;
            Rt.RuntimeUnlock();
            if (!has) return 1;
            if ((f & Weak) != 0) WeakSlots.Cleared(addr);
            return (f & FinalizerSuppressed) != 0 ? 0 : 1;
        }
    }

    /// <summary>The slots of weak GC handles, by the address of their target (GCHandle.Mono.cs).</summary>
    internal static class WeakSlots
    {
        static Dictionary<int, List<int>>? s_byAddr;

        public static void Link(int slot, object target)
        {
            int a = Rt.AddressOf(target);
            Rt.RuntimeLock();
            s_byAddr ??= new Dictionary<int, List<int>>();
            if (!s_byAddr.TryGetValue(a, out var list)) { list = new List<int>(); s_byAddr[a] = list; }
            list.Add(slot);
            Mem.StoreI32(slot, a);
            Rt.RuntimeUnlock();
            ObjectMarks.Set(target, ObjectMarks.Weak);
        }

        public static void Unlink(int slot)
        {
            Rt.RuntimeLock();
            int a = Mem.LoadI32(slot);
            Mem.StoreI32(slot, 0);
            bool last = false;
            if (a != 0 && s_byAddr != null && s_byAddr.TryGetValue(a, out var list))
            {
                list.Remove(slot);
                if (list.Count == 0) { s_byAddr.Remove(a); last = true; }
            }
            Rt.RuntimeUnlock();
            if (last) ObjectMarks.ClearAt(a, ObjectMarks.Weak);
        }

        internal static void Cleared(int addr)
        {
            Rt.RuntimeLock();
            if (s_byAddr != null && s_byAddr.TryGetValue(addr, out var list))
            {
                s_byAddr.Remove(addr);
                foreach (int slot in list) Mem.StoreI32(slot, 0);
            }
            Rt.RuntimeUnlock();
        }
    }
}
