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
        /// <summary>Its grace is over (Grace): this release destroys it.</summary>
        const int Dying = 4;
        /// <summary>Bit 30 of an object's hash word: it has (or had) a mark, so its destruction looks it up; every other
        /// object's destruction skips the table and its lock. (The identity hash leaves the bit out: Rt.HashOf.)</summary>
        internal const int MarkedBit = 0x40000000;
        static Dictionary<int, int>? s_marks;
        static int s_count, s_busy;

        public static void Set(object o, int flag) => SetAt(Rt.AddressOf(o), flag);

        static void SetAt(int a, int flag)
        {
            Rt.RuntimeLock();
            s_busy++;
            s_marks ??= new Dictionary<int, int>();
            s_marks.TryGetValue(a, out int f);
            if (f == 0) s_count++;
            s_marks[a] = f | flag;
            s_busy--;
            Rt.RuntimeUnlock();
            if (Mem.LoadI32(a) > 0)
            {
                ref int w = ref Mem.RefI32(a + 8);
                while (true) { int v = Atomic.Load32(ref w); if ((v & MarkedBit) != 0 || Atomic.CompareExchange32(ref w, v, v | MarkedBit) == v) break; }
            }
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

        // An object that only weak references refer to lives until a collection under .NET, and code counts on it: it
        // makes an object, keeps a weak reference only, and reads the reference back (XNamespace's cache of
        // namespaces, in a loop until that works). So an object with a weak reference whose count reaches zero is
        // not destroyed at once: the thread keeps it, in a ring, until GC.Collect, the thread's end, or until as many
        // such objects have followed as the ring holds; a reference taken from the weak one meanwhile keeps it alive
        // as usual. (rt.destroy asks before it destroys anything, when the program has marks at all.)
        const int RingCap = 64;
        [System.ThreadStatic] static int t_ring, t_head, t_n;

        /// <summary>The object at `addr` (count zero) is about to be destroyed: true if it lives on instead.</summary>
        public static bool Grace(int addr)
        {
            if (s_count == 0 || (Mem.LoadI32(addr + 8) & MarkedBit) == 0) return false;
            Rt.RuntimeLock();
            bool weak = s_busy == 0 && s_marks != null && s_marks.TryGetValue(addr, out int f) && (f & (Weak | Dying)) == Weak;
            Rt.RuntimeUnlock();
            if (!weak) return false;
            // (the ring's reference; with threads the object is shared: a weak reference's target is)
            Mem.StoreI32(addr, Rt.SingleThreaded() ? 1 : 1 | 0x40000000);
            if (t_ring == 0) t_ring = Rt.Alloc(RingCap * 4);
            if (t_n == RingCap) DropOldest();
            Mem.StoreI32(t_ring + 4 * ((t_head + t_n) % RingCap), addr);
            t_n++;
            return true;
        }

        static void DropOldest()
        {
            int a = Mem.LoadI32(t_ring + 4 * t_head);
            t_head = (t_head + 1) % RingCap;
            t_n--;
            // only the ring holds it: releasing it destroys it, with no second grace
            if ((Mem.LoadI32(a) & 0x1FFFFFFF) == 1) SetAt(a, Dying);
            var o = Rt.Take<object>(a);
        }

        /// <summary>A collection (GC.Collect, a thread's end): the objects kept for their weak references go.</summary>
        public static void Drain()
        {
            while (t_n > 0) DropOldest();
        }

        /// <summary>The object at `addr` is dying: clear weak handles to it; 0 if its finalizer must not run.</summary>
        public static int OnDestroy(int addr)
        {
            if (s_count == 0 || s_busy != 0) return 1;
            if (Mem.LoadI32(addr) > -2 && (Mem.LoadI32(addr + 8) & MarkedBit) == 0) return 1;
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
