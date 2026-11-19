namespace Cs2Wasm.Runtime
{
    /// <summary>
    /// A map from a positive int (an address) to an int, in memory of the runtime's own: no object, so the runtime's
    /// tables share no type with the program (a Dictionary&lt;int, int&gt; of its own in a static made every one of the
    /// program's another thread can reach, and its counts atomic, Compiler/Sharing.cs). The caller holds the runtime
    /// lock. A map is its header (Rt.Alloc(12)): [0] its pairs (key, value), [4] their number (a power of two), [8]
    /// the keys in use and the removed ones. Key 0: an empty pair; -1: a removed one.
    /// </summary>
    internal static class IntMap
    {
        public static int New()
        {
            int h = Rt.Alloc(12);
            int cap = 16, e = Rt.Alloc(cap * 8);
            Mem.Fill(e, 0, cap * 8);
            Mem.StoreI32(h, e); Mem.StoreI32(h + 4, cap); Mem.StoreI32(h + 8, 0);
            return h;
        }

        static int Hash(int key, int cap) => (int)(((uint)key * 0x9E3779B1u) >> 3) & (cap - 1);

        /// <summary>The pair of the key, or 0.</summary>
        static int Find(int h, int key)
        {
            int e = Mem.LoadI32(h), cap = Mem.LoadI32(h + 4);
            for (int i = Hash(key, cap); ; i = (i + 1) & (cap - 1))
            {
                int k = Mem.LoadI32(e + 8 * i);
                if (k == key) return e + 8 * i;
                if (k == 0) return 0;
            }
        }

        public static bool TryGet(int h, int key, out int value)
        {
            int p = Find(h, key);
            value = p != 0 ? Mem.LoadI32(p + 4) : 0;
            return p != 0;
        }

        public static void Set(int h, int key, int value)
        {
            int p = Find(h, key);
            if (p != 0) { Mem.StoreI32(p + 4, value); return; }
            int cap = Mem.LoadI32(h + 4);
            if ((Mem.LoadI32(h + 8) + 1) * 4 > cap * 3) Rehash(h, Count(h) * 4 > cap * 2 ? cap * 2 : cap);
            Put(h, key, value);
            Mem.StoreI32(h + 8, Mem.LoadI32(h + 8) + 1);
        }

        public static bool Remove(int h, int key)
        {
            int p = Find(h, key);
            if (p == 0) return false;
            Mem.StoreI32(p, -1); Mem.StoreI32(p + 4, 0);
            return true;
        }

        static void Put(int h, int key, int value)
        {
            int e = Mem.LoadI32(h), cap = Mem.LoadI32(h + 4);
            int i = Hash(key, cap);
            while (Mem.LoadI32(e + 8 * i) is not (0 or -1)) i = (i + 1) & (cap - 1);
            Mem.StoreI32(e + 8 * i, key); Mem.StoreI32(e + 8 * i + 4, value);
        }

        static int Count(int h)
        {
            int e = Mem.LoadI32(h), cap = Mem.LoadI32(h + 4), n = 0;
            for (int i = 0; i < cap; i++) if (Mem.LoadI32(e + 8 * i) is not (0 or -1)) n++;
            return n;
        }

        static void Rehash(int h, int cap)
        {
            int old = Mem.LoadI32(h), oldCap = Mem.LoadI32(h + 4);
            int e = Rt.Alloc(cap * 8);
            Mem.Fill(e, 0, cap * 8);
            Mem.StoreI32(h, e); Mem.StoreI32(h + 4, cap);
            int n = 0;
            for (int i = 0; i < oldCap; i++)
            {
                int k = Mem.LoadI32(old + 8 * i);
                if (k is 0 or -1) continue;
                Put(h, k, Mem.LoadI32(old + 8 * i + 4)); n++;
            }
            Mem.StoreI32(h + 8, n);
            Rt.Free(old, oldCap * 8);
        }
    }

    /// <summary>
    /// Per-object flags kept beside the heap, by address (section 4.4): a weak GC handle's target, an object whose
    /// finalizer GC.SuppressFinalize skipped. When a marked object's count reaches zero, the destroy path
    /// (rt.on_destroy, compiled in only when the program sets a mark) clears the weak handles to it before its
    /// finalizer would run, as .NET's short weak references are cleared, and skips a suppressed finalizer.
    /// </summary>
    public static class ObjectMarks
    {
        public const int Weak = 1, FinalizerSuppressed = 2;
        /// <summary>Bit 30 of an object's hash word: it has (or had) a mark, so its destruction looks it up; every other
        /// object's destruction skips the table and its lock. (The identity hash leaves the bit out: Rt.HashOf.)</summary>
        internal const int MarkedBit = 0x40000000;
        static int s_marks;
        static int s_count;
        // (the table's own allocations free objects, whose destruction comes back here: that thread's, not another's)
        [System.ThreadStatic] static int t_busy;

        public static void Set(object o, int flag) => SetAt(Rt.AddressOf(o), flag);

        static void SetAt(int a, int flag)
        {
            Rt.RuntimeLock();
            t_busy++;
            if (s_marks == 0) s_marks = IntMap.New();
            IntMap.TryGet(s_marks, a, out int f);
            if (f == 0) s_count++;
            IntMap.Set(s_marks, a, f | flag);
            t_busy--;
            Rt.RuntimeUnlock();
            // (a live object, an arena's member too: not an immortal one, whose word never changes)
            if (Mem.LoadI32(a) is > 0 or < -1)
            {
                ref int w = ref Mem.RefI32(a + 8);
                while (true) { int v = Atomic.Load32(ref w); if ((v & MarkedBit) != 0 || Atomic.CompareExchange32(ref w, v, v | MarkedBit) == v) break; }
            }
        }

        public static void Clear(object o, int flag) => ClearAt(Rt.AddressOf(o), flag);

        internal static void ClearAt(int a, int flag)
        {
            Rt.RuntimeLock();
            t_busy++;
            if (s_marks != 0 && IntMap.TryGet(s_marks, a, out int f))
            {
                f &= ~flag;
                if (f == 0) { IntMap.Remove(s_marks, a); s_count--; } else IntMap.Set(s_marks, a, f);
            }
            t_busy--;
            Rt.RuntimeUnlock();
        }

        /// <summary>The object at `addr` is dying: clear weak handles to it; 0 if its finalizer must not run.</summary>
        public static int OnDestroy(int addr)
        {
            if (s_count == 0 || t_busy != 0) return 1;
            if ((Mem.LoadI32(addr + 8) & MarkedBit) == 0) return 1;
            Rt.RuntimeLock();
            t_busy++;
            bool has = IntMap.TryGet(s_marks, addr, out int f);
            if (has) { IntMap.Remove(s_marks, addr); s_count--; }
            t_busy--;
            Rt.RuntimeUnlock();
            if (!has) return 1;
            if ((f & Weak) != 0) WeakSlots.Cleared(addr);
            return (f & FinalizerSuppressed) != 0 ? 0 : 1;
        }
    }

    /// <summary>The slots of weak GC handles, by the address of their target (GCHandle.Mono.cs): a map from the
    /// address to a chain of [slot, next] blocks, in the runtime's own memory (IntMap).</summary>
    internal static class WeakSlots
    {
        static int s_byAddr;

        public static void Link(int slot, object target)
        {
            int a = Rt.AddressOf(target);
            Rt.RuntimeLock();
            if (s_byAddr == 0) s_byAddr = IntMap.New();
            IntMap.TryGet(s_byAddr, a, out int head);
            int n = Rt.Alloc(8);
            Mem.StoreI32(n, slot); Mem.StoreI32(n + 4, head);
            IntMap.Set(s_byAddr, a, n);
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
            if (a != 0 && s_byAddr != 0 && IntMap.TryGet(s_byAddr, a, out int head))
            {
                // (the slot's block out of the chain)
                for (int prev = 0, n = head; n != 0; prev = n, n = Mem.LoadI32(n + 4))
                {
                    if (Mem.LoadI32(n) != slot) continue;
                    int next = Mem.LoadI32(n + 4);
                    if (prev == 0) head = next; else Mem.StoreI32(prev + 4, next);
                    Rt.Free(n, 8);
                    break;
                }
                if (head == 0) { IntMap.Remove(s_byAddr, a); last = true; }
                else IntMap.Set(s_byAddr, a, head);
            }
            Rt.RuntimeUnlock();
            if (last) ObjectMarks.ClearAt(a, ObjectMarks.Weak);
        }

        internal static void Cleared(int addr)
        {
            Rt.RuntimeLock();
            if (s_byAddr != 0 && IntMap.TryGet(s_byAddr, addr, out int head))
            {
                IntMap.Remove(s_byAddr, addr);
                for (int n = head; n != 0;) { Mem.StoreI32(Mem.LoadI32(n), 0); int next = Mem.LoadI32(n + 4); Rt.Free(n, 8); n = next; }
            }
            Rt.RuntimeUnlock();
        }
    }
}
