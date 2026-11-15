// cs2wasm runtime boundary: memory, counting, exceptions, engine imports.
// Compiled by cs2wasm itself together with the program. Methods marked [Intrinsic] are emitted inline by the compiler.
#pragma warning disable CS0626, CS0436, CS0660, CS0661, CS0659
using System;

namespace Cs2Wasm.Runtime
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class IntrinsicAttribute : Attribute { }

    /// <summary>Marks a library method that is a WASM import of the engine (module, name).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ImportAttribute : Attribute
    {
        public ImportAttribute(string module, string name) { }
    }

    public static unsafe class Mem
    {
        [Intrinsic] public static extern int LoadI32(int addr);
        [Intrinsic] public static extern int LoadU8(int addr);
        [Intrinsic] public static extern int LoadI8(int addr);
        [Intrinsic] public static extern int LoadU16(int addr);
        [Intrinsic] public static extern int LoadI16(int addr);
        [Intrinsic] public static extern long LoadI64(int addr);
        [Intrinsic] public static extern float LoadF32(int addr);
        [Intrinsic] public static extern double LoadF64(int addr);
        [Intrinsic] public static extern void StoreI32(int addr, int v);
        [Intrinsic] public static extern void StoreU8(int addr, int v);
        [Intrinsic] public static extern void StoreU16(int addr, int v);
        [Intrinsic] public static extern void StoreI64(int addr, long v);
        [Intrinsic] public static extern void StoreF32(int addr, float v);
        [Intrinsic] public static extern void StoreF64(int addr, double v);
        [Intrinsic] public static extern void Copy(int dst, int src, int n);
        [Intrinsic] public static extern void Fill(int dst, int value, int n);
        [Intrinsic] public static extern int Size();
        [Intrinsic] public static extern int Grow(int pages);
        /// <summary>The int at an address, as a reference (for the atomic operations).</summary>
        [Intrinsic] public static extern ref int RefI32(int addr);
    }

    /// <summary>Engine imports used by the library (section 7.4). The engine supplies these.</summary>
    public static class Engine
    {
        /// <summary>Starts a worker instance that calls __thread_entry(stackTop, tls, id, arg); 0 when started.</summary>
        [Import("cs2wasm", "thread_spawn")] public static extern int ThreadSpawn(int stackTop, int tls, int id, int arg, int background);
        /// <summary>Cores the pool may use (navigator.hardwareConcurrency on the web).</summary>
        [Import("cs2wasm", "processor_count")] public static extern int ProcessorCount();
        /// <summary>Console output: UTF-16 text at (ptr, length in chars).</summary>
        [Import("cs2wasm", "write")] public static extern void Write(int ptr, int len);
        /// <summary>An exception escaped an export: type name, message, stack trace (UTF-16 ptr/len pairs).</summary>
        [Import("cs2wasm", "report_exception")] public static extern void ReportException(int typePtr, int typeLen, int msgPtr, int msgLen, int stackPtr, int stackLen);
        /// <summary>Wall-clock time, milliseconds since the Unix epoch.</summary>
        [Import("cs2wasm", "now_ms")] public static extern double NowMs();
        /// <summary>Monotonic time in nanoseconds.</summary>
        [Import("cs2wasm", "monotonic_ns")] public static extern long MonotonicNs();
        /// <summary>
        /// Writes the C# stack trace of the calling thread (.NET format, one "   at ..." line per visible frame, newest first)
        /// into the buffer as UTF-16 and returns its length in chars (at most capacity).
        /// </summary>
        [Import("cs2wasm", "capture_stack")] public static extern int CaptureStack(int bufPtr, int capacityChars);
        /// <summary>The number of visible C# frames on the calling thread's stack.</summary>
        [Import("cs2wasm", "stack_depth")] public static extern int StackDepth();
        /// <summary>A seed for System.Random's default constructor (the engine makes it match across peers).</summary>
        [Import("cs2wasm", "random_seed")] public static extern int RandomSeed();
        /// <summary>Local time zone offset from UTC in minutes, at the given UTC time.</summary>
        [Import("cs2wasm", "utc_offset_minutes")] public static extern int UtcOffsetMinutes(double utcMs);
    }

    public static class Rt
    {
        [Intrinsic] public static extern int AddressOf(object o);
        /// <summary>A null reference (address 0): what `fixed` over an empty span gives.</summary>
        [Intrinsic] public static extern ref T NullRef<T>();
        /// <summary>Marks an object, and what it reaches, as shared between threads (section 4.5).</summary>
        [Intrinsic] public static extern void Share(object o);
        /// <summary>Wraps an address whose count the caller owns.</summary>
        [Intrinsic] public static extern T Take<T>(int addr);
        /// <summary>Reads an address as a borrowed reference.</summary>
        [Intrinsic] public static extern T Borrow<T>(int addr);
        [Intrinsic] public static extern int TypeId(object o);
        [Intrinsic] public static extern int TypeIdOf<T>();
        [Intrinsic] public static extern int MarkConstructed<T>();
        [Intrinsic] public static extern int HeapBase();
        [Intrinsic] public static extern void Retain(object o);
        [Intrinsic] public static extern void Release(object o);
        [Intrinsic] public static extern string TypeName(object o);
        [Intrinsic] public static extern bool IsInstanceOf<T>(object o);
        [Intrinsic] public static extern void Unreachable();
        [Intrinsic] public static extern long F64Bits(double d);
        [Intrinsic] public static extern double F64FromBits(long l);
        [Intrinsic] public static extern int F32Bits(float f);
        [Intrinsic] public static extern float F32FromBits(int i);
        [Intrinsic] public static extern double Sqrt(double d);
        [Intrinsic] public static extern double Floor(double d);
        [Intrinsic] public static extern double Ceiling(double d);
        [Intrinsic] public static extern double Truncate(double d);
        [Intrinsic] public static extern double RoundEven(double d);
        [Intrinsic] public static extern double Abs(double d);
        [Intrinsic] public static extern double Min(double a, double b);
        [Intrinsic] public static extern double Max(double a, double b);
        [Intrinsic] public static extern double CopySign(double a, double b);
        [Intrinsic] public static extern float SqrtF(float d);
        [Intrinsic] public static extern float FloorF(float d);
        [Intrinsic] public static extern float CeilingF(float d);
        [Intrinsic] public static extern float TruncateF(float d);
        [Intrinsic] public static extern float RoundEvenF(float d);
        [Intrinsic] public static extern float AbsF(float d);
        [Intrinsic] public static extern float MinF(float a, float b);
        [Intrinsic] public static extern float MaxF(float a, float b);
        [Intrinsic] public static extern int I32Clz(int v);
        [Intrinsic] public static extern int I32Ctz(int v);
        [Intrinsic] public static extern int I32Popcnt(int v);
        [Intrinsic] public static extern long I64Clz(long v);
        [Intrinsic] public static extern long I64Ctz(long v);
        [Intrinsic] public static extern long I64Popcnt(long v);
        [Intrinsic] public static extern int I32Rotl(int v, int n);
        [Intrinsic] public static extern int I32Rotr(int v, int n);
        [Intrinsic] public static extern long I64Rotl(long v, long n);
        [Intrinsic] public static extern long I64Rotr(long v, long n);
        [Intrinsic] public static extern int SizeOf<T>();
        [Intrinsic] public static extern bool IsReference<T>();
        [Intrinsic] public static extern bool ContainsReferences<T>();
        [Intrinsic] public static extern int BoxDataOffset<T>();
        [Intrinsic] public static extern void ReleaseElements<T>(int addr, int count);
        [Intrinsic] public static extern void RetainElements<T>(int addr, int count);
        /// <summary>Throws an exception object again without resetting its stack trace.</summary>
        [Intrinsic] public static extern void Rethrow(object ex);

        // ------------------------------------------------------------------
        // Allocator: size classes of 8 bytes up to 2 KiB with free lists, first-fit list above.
        // All state is in linear memory (section 4.6); freed space is reused to keep the heap compact.
        // ------------------------------------------------------------------
        const int MaxSmall = 2048;
        const int Classes = MaxSmall / 8 + 1;
        static int s_top, s_end, s_table, s_large, s_hash;

        static void InitHeap()
        {
            int b = (HeapBase() + 15) & ~15;
            s_table = b;
            s_top = b + Classes * 4;
            s_top = (s_top + 15) & ~15;
            s_end = Mem.Size() * 65536;
        }

        [Intrinsic] public static extern int ThreadId();
        [Intrinsic] public static extern int TlsBase();
        [Intrinsic] public static extern int TlsSize();
        [Intrinsic] public static extern void RuntimeLock();
        [Intrinsic] public static extern void RuntimeUnlock();

        /// <summary>Bytes from the heap's start to its top (freed blocks included).</summary>
        public static long HeapUsed() => s_top == 0 ? 0 : s_top - HeapBase();

        public static int Alloc(int size)
        {
            RuntimeLock();
            int a = AllocCore(size);
            RuntimeUnlock();
            return a;
        }

        static int AllocCore(int size)
        {
            if (s_top == 0) InitHeap();
            size = (size + 7) & ~7;
            if (size < 16) size = 16;
            if (size <= MaxSmall)
            {
                int slot = s_table + (size >> 3) * 4;
                int head = Mem.LoadI32(slot);
                if (head != 0)
                {
                    Mem.StoreI32(slot, Mem.LoadI32(head + 8));
                    Mem.Fill(head, 0, size);
                    return head;
                }
            }
            else
            {
                // first fit among freed large blocks: [next, size]
                int prev = 0, cur = s_large;
                while (cur != 0)
                {
                    int csize = Mem.LoadI32(cur + 4);
                    if (csize >= size && csize <= size + size / 4)
                    {
                        int next = Mem.LoadI32(cur);
                        if (prev == 0) s_large = next; else Mem.StoreI32(prev, next);
                        Mem.Fill(cur, 0, csize);
                        return cur;
                    }
                    prev = cur; cur = Mem.LoadI32(cur);
                }
            }
            if (s_top + size > s_end || s_top + size < s_top)
            {
                int need = s_top + size - s_end;
                int pages = (need + 65535) / 65536;
                if (Mem.Grow(pages) < 0) ThrowHelpers.OutOfMemory();
                s_end = Mem.Size() * 65536;
            }
            int a = s_top;
            s_top += size;
            return a;
        }

        public static void Free(int addr, int size)
        {
            RuntimeLock();
            FreeCore(addr, size);
            RuntimeUnlock();
        }

        static void FreeCore(int addr, int size)
        {
            size = (size + 7) & ~7;
            if (size < 16) size = 16;
            if (size <= MaxSmall)
            {
                // the link is at 8: word 0 (an object's count) stays 0, dead, for a reference read just before the free
                int slot = s_table + (size >> 3) * 4;
                Mem.StoreI32(addr, 0);
                Mem.StoreI32(addr + 8, Mem.LoadI32(slot));
                Mem.StoreI32(slot, addr);
                return;
            }
            if (addr + size == s_top) { Mem.Fill(addr, 0, size); s_top = addr; return; } // bump allocation expects zeroed memory
            Mem.StoreI32(addr, s_large);
            Mem.StoreI32(addr + 4, size);
            s_large = addr;
        }

        /// <summary>New object: count 1, type id, hash code from a per-instance counter in allocation order (section 7.3).</summary>
        public static int AllocObject(int size, int typeId)
        {
            int a = Alloc(size);
            Mem.StoreI32(a, 1);
            Mem.StoreI32(a + 4, typeId);
            Mem.StoreI32(a + 8, Atomic.Add32(ref s_hash, 1) + 1);
            return a;
        }

        public static int AllocArray(int length, int elemSize, int typeId)
        {
            if (length < 0) ThrowHelpers.Overflow();
            long total = 16L + (long)length * elemSize;
            if (total > 0x7FFF0000L) ThrowHelpers.OutOfMemory();
            int a = AllocObject((int)total, typeId);
            Mem.StoreI32(a + 12, length);
            return a;
        }

        public static int AllocArrayWithHeader(int length, int elemSize, int typeId, int dataOffset)
        {
            long total = dataOffset + (long)length * elemSize;
            if (total > 0x7FFF0000L) ThrowHelpers.OutOfMemory();
            int a = AllocObject((int)total, typeId);
            Mem.StoreI32(a + 12, length);
            return a;
        }

        public static int AllocString(int length)
        {
            int a = AllocObject(16 + length * 2 + 2, 1);
            Mem.StoreI32(a + 12, length);
            return a;
        }

        public static int HashOf(object o)
        {
            int a = AddressOf(o);
            int c = Mem.LoadI32(a);
            if (c < -1) return Mem.LoadI32(ArenaEntry(-c, Mem.LoadI32(a + 8)) + 4);
            return Mem.LoadI32(a + 8) & 0x7FFFFFFF;
        }

        // ------------------------------------------------------------------
        // Arenas (section 4.3). Compiled into a module only when the compiler found a store that can close a cycle.
        //
        // Arena record (24 bytes): [0] total outside count, [4] members, [8] capacity, [12] member table,
        //   [16] dirty (outside counts that fell to zero and uncounted references removed since the last sweep), [20] flags.
        // Member table entry (16 bytes): [0] address, [4] hash code, [8] outside count, [12] mark.
        // A member's header: [0] = -arena (any value below -1; -1 is immortal), [8] = its index in the table.
        // References between members of one arena are not counted. Outside references (locals, statics, other
        // objects and arenas) count on the member they point at and on the arena total; the arena is freed when the
        // total reaches zero. A sweep (a mark from members with outside counts, through member-to-member slots only)
        // frees members that became unreachable while the arena lives; it runs after the arena has seen as many
        // dirty events as half its size, so its cost is amortized over the operations that produced the garbage.
        // A pinned object (stored in a static) has bit 31 of its hash word set and never joins an arena.
        // ------------------------------------------------------------------
        // ArenaShared: another thread can reach the arena's members (Sharing): it takes no more members, and the
        // objects its members reach are shared. Shared objects never join an arena (their counts are atomic, an
        // arena's are under the runtime lock); cycles among them are not collected.
        const int ArenaBusy = 1, ArenaPendingFree = 2, ArenaShared = 4;
        const int SharedCount = 0x40000000;
        static int s_visitArena, s_visitMode, s_markStack, s_markTop;

        [Intrinsic] static extern int SccOf(int typeId);
        [Intrinsic] static extern void ArenaClearObject(int addr);
        [Intrinsic] static extern void ArenaFreeObject(int addr);
        [Intrinsic] static extern void ArenaVisitObject(int addr);

        static int ArenaEntry(int ar, int idx) => Mem.LoadI32(ar + 12) + idx * 16;

        static int NewArena()
        {
            int ar = Alloc(24);
            Mem.Fill(ar, 0, 24);
            Mem.StoreI32(ar + 8, 4);
            Mem.StoreI32(ar + 12, Alloc(4 * 16));
            return ar;
        }

        /// <summary>Adds a plain object (count c > 0) to the arena; its count becomes its outside count.</summary>
        static void ArenaAdd(int ar, int obj)
        {
            int n = Mem.LoadI32(ar + 4);
            int cap = Mem.LoadI32(ar + 8);
            if (n == cap)
            {
                int nt = Alloc(cap * 2 * 16);
                Mem.Copy(nt, Mem.LoadI32(ar + 12), n * 16);
                Free(Mem.LoadI32(ar + 12), cap * 16);
                Mem.StoreI32(ar + 12, nt);
                Mem.StoreI32(ar + 8, cap * 2);
            }
            int c = Mem.LoadI32(obj);
            int e = ArenaEntry(ar, n);
            Mem.StoreI32(e, obj);
            Mem.StoreI32(e + 4, Mem.LoadI32(obj + 8));
            Mem.StoreI32(e + 8, c);
            Mem.StoreI32(e + 12, 0);
            Mem.StoreI32(ar, Mem.LoadI32(ar) + c);
            Mem.StoreI32(ar + 4, n + 1);
            Mem.StoreI32(obj, -ar);
            Mem.StoreI32(obj + 8, n);
        }

        /// <summary>Moves the smaller arena's members into the larger; returns the survivor.</summary>
        static int ArenaMerge(int a, int b)
        {
            if (Mem.LoadI32(a + 4) < Mem.LoadI32(b + 4)) { int t = a; a = b; b = t; }
            int n = Mem.LoadI32(b + 4);
            int total = Mem.LoadI32(a);
            for (int i = 0; i < n; i++)
            {
                int e = ArenaEntry(b, i);
                int obj = Mem.LoadI32(e);
                int ext = Mem.LoadI32(e + 8);
                Mem.StoreI32(obj, ext > 0 ? ext : 1); // ArenaAdd takes the count from the header
                ArenaAdd(a, obj);
                int ne = ArenaEntry(a, Mem.LoadI32(obj + 8));
                Mem.StoreI32(ne + 4, Mem.LoadI32(e + 4));
                Mem.StoreI32(ne + 8, ext);
            }
            Mem.StoreI32(a, total + Mem.LoadI32(b));
            Mem.StoreI32(a + 16, Mem.LoadI32(a + 16) + Mem.LoadI32(b + 16));
            Free(Mem.LoadI32(b + 12), Mem.LoadI32(b + 8) * 16);
            Free(b, 24);
            return a;
        }

        /// <summary>Retain of an arena member: one more outside reference.</summary>
        public static void ArenaRetain(int p)
        {
            int ar = -Mem.LoadI32(p);
            int e = ArenaEntry(ar, Mem.LoadI32(p + 8));
            Mem.StoreI32(e + 8, Mem.LoadI32(e + 8) + 1);
            Mem.StoreI32(ar, Mem.LoadI32(ar) + 1);
        }

        /// <summary>Release of an arena member: one outside reference fewer.</summary>
        public static void ArenaRelease(int p)
        {
            int ar = -Mem.LoadI32(p);
            int e = ArenaEntry(ar, Mem.LoadI32(p + 8));
            int ext = Mem.LoadI32(e + 8) - 1;
            Mem.StoreI32(e + 8, ext);
            int total = Mem.LoadI32(ar) - 1;
            Mem.StoreI32(ar, total);
            if (total == 0) { ArenaFree(ar); return; }
            if (ext == 0) ArenaDirty(ar);
        }

        static void ArenaDirty(int ar)
        {
            int d = Mem.LoadI32(ar + 16) + 1;
            Mem.StoreI32(ar + 16, d);
            if (d > (Mem.LoadI32(ar + 4) >> 1) + 8 && (Mem.LoadI32(ar + 20) & ArenaBusy) == 0) ArenaSweep(ar);
        }

        /// <summary>
        /// A store of value (owned) into a slot of container that can close a cycle: if both are of one cycle-capable
        /// group, they end up in one arena and the stored reference becomes uncounted. Returns the value to store.
        /// </summary>
        public static int ArenaLink(int val, int container)
        {
            if (val == 0 || container == 0) return val;
            RuntimeLock();
            int r = ArenaLinkCore(val, container);
            RuntimeUnlock();
            return r;
        }

        static int ArenaLinkCore(int val, int container)
        {
            int cv = Mem.LoadI32(val), ca = Mem.LoadI32(container);
            if (cv > 0 && (cv & SharedCount) != 0 || ca > 0 && (ca & SharedCount) != 0) return val; // shared: counted
            if (cv == -1 || cv == 0 || ca == -1 || ca == 0) return val; // immortal, or being destroyed
            int g = SccOf(Mem.LoadI32(val + 4));
            if (g == 0 || g != SccOf(Mem.LoadI32(container + 4))) return val;
            if (ca > 0 && Mem.LoadI32(container + 8) < 0) return val; // pinned
            if (cv > 0 && Mem.LoadI32(val + 8) < 0) return val;
            int ra = ca < -1 ? -ca : 0, rv = cv < -1 ? -cv : 0;
            if (ra != 0 && (Mem.LoadI32(ra + 20) & (ArenaBusy | ArenaShared)) != 0) return val;
            if (rv != 0 && (Mem.LoadI32(rv + 20) & (ArenaBusy | ArenaShared)) != 0) return val;
            int ar = ra;
            if (ar == 0) { ar = rv != 0 ? rv : NewArena(); ArenaAdd(ar, container); }
            cv = Mem.LoadI32(val); // val may be the container itself
            if (cv > 0) ArenaAdd(ar, val);
            else if (-cv != ar) ar = ArenaMerge(ar, -cv);
            // the stored reference is now inside the arena: drop the count the store took
            int e = ArenaEntry(ar, Mem.LoadI32(val + 8));
            Mem.StoreI32(e + 8, Mem.LoadI32(e + 8) - 1);
            Mem.StoreI32(ar, Mem.LoadI32(ar) - 1);
            return val;
        }

        /// <summary>The old value of a slot that can close a cycle was overwritten.</summary>
        public static void ArenaUnlink(int old, int container)
        {
            if (old == 0) return;
            RuntimeLock();
            int co = Mem.LoadI32(old);
            bool member = co < -1 && co == Mem.LoadI32(container);
            if (member) ArenaDirty(-co); // uncounted: it may be garbage now
            RuntimeUnlock();
            if (!member) Release(Borrow<object>(old));
        }

        /// <summary>Sharing reached a member of the arena: the arena becomes shared, and every member is queued so
        /// that what the members reach is shared too.</summary>
        internal static void ShareArena(int ar)
        {
            RuntimeLock();
            int flags = Mem.LoadI32(ar + 20);
            if ((flags & ArenaShared) == 0)
            {
                Mem.StoreI32(ar + 20, flags | ArenaShared);
                int n = Mem.LoadI32(ar + 4);
                for (int i = 0; i < n; i++) Sharing.PushMember(Mem.LoadI32(ArenaEntry(ar, i)));
            }
            RuntimeUnlock();
        }

        /// <summary>A store into a static field: the object stays out of arenas (statics never end).</summary>
        public static int ArenaPin(int val)
        {
            if (val != 0 && Mem.LoadI32(val) > 0) Mem.StoreI32(val + 8, Mem.LoadI32(val + 8) | int.MinValue);
            return val;
        }

        /// <summary>Called by the generated visitor for each member-to-member slot of a member.</summary>
        public static void ArenaVisit(int slot)
        {
            int child = Mem.LoadI32(slot);
            if (child == 0 || Mem.LoadI32(child) != -s_visitArena) return;
            if (s_visitMode == 1) { Mem.StoreI32(slot, 0); return; } // cut: the reference was not counted
            int e = ArenaEntry(s_visitArena, Mem.LoadI32(child + 8));
            if (Mem.LoadI32(e + 12) != 0) return;
            Mem.StoreI32(e + 12, 1);
            Mem.StoreI32(s_markStack + s_markTop * 4, child);
            s_markTop++;
        }

        /// <summary>Destroys the given members: cut member-to-member slots, run finalizers and release outside references, free.</summary>
        static void ArenaDestroyMembers(int ar, int list, int count)
        {
            int sa = s_visitArena, sm = s_visitMode;
            s_visitArena = ar; s_visitMode = 1;
            for (int i = 0; i < count; i++) ArenaVisitObject(Mem.LoadI32(list + i * 4));
            s_visitArena = sa; s_visitMode = sm;
            for (int i = 0; i < count; i++) ArenaClearObject(Mem.LoadI32(list + i * 4));
            for (int i = 0; i < count; i++) ArenaFreeObject(Mem.LoadI32(list + i * 4));
        }

        /// <summary>No outside reference remains: the whole arena goes.</summary>
        static void ArenaFree(int ar)
        {
            int flags = Mem.LoadI32(ar + 20);
            if ((flags & ArenaBusy) != 0) { Mem.StoreI32(ar + 20, flags | ArenaPendingFree); return; }
            Mem.StoreI32(ar + 20, ArenaBusy);
            int n = Mem.LoadI32(ar + 4);
            int list = Alloc(n * 4);
            for (int i = 0; i < n; i++) Mem.StoreI32(list + i * 4, Mem.LoadI32(ArenaEntry(ar, i)));
            ArenaDestroyMembers(ar, list, n);
            Free(list, n * 4);
            Free(Mem.LoadI32(ar + 12), Mem.LoadI32(ar + 8) * 16);
            Free(ar, 24);
        }

        /// <summary>Frees the members no longer reachable from a member with outside references.</summary>
        static void ArenaSweep(int ar)
        {
            Mem.StoreI32(ar + 20, ArenaBusy);
            Mem.StoreI32(ar + 16, 0);
            int n = Mem.LoadI32(ar + 4);
            int stack = Alloc(n * 4);
            int ss = s_markStack, st = s_markTop, sa = s_visitArena, sm = s_visitMode;
            s_markStack = stack; s_markTop = 0; s_visitArena = ar; s_visitMode = 0;
            for (int i = 0; i < n; i++)
            {
                int e = ArenaEntry(ar, i);
                bool root = Mem.LoadI32(e + 8) > 0;
                Mem.StoreI32(e + 12, root ? 1 : 0);
                if (root) { Mem.StoreI32(stack + s_markTop * 4, Mem.LoadI32(e)); s_markTop++; }
            }
            while (s_markTop > 0)
            {
                s_markTop--;
                ArenaVisitObject(Mem.LoadI32(stack + s_markTop * 4));
            }
            s_markStack = ss; s_markTop = st; s_visitArena = sa; s_visitMode = sm;
            // garbage: unmarked members (the mark stack is free again; reuse it as the list)
            int g = 0;
            for (int i = 0; i < n; i++)
            {
                int e = ArenaEntry(ar, i);
                if (Mem.LoadI32(e + 12) == 0) { Mem.StoreI32(stack + g * 4, Mem.LoadI32(e)); g++; }
            }
            if (g > 0)
            {
                ArenaDestroyMembers(ar, stack, g);
                // compact the table: the destroyed members' entries go, the survivors get new indices
                int k = 0;
                for (int i = 0; i < n; i++)
                {
                    int e = ArenaEntry(ar, i);
                    if (Mem.LoadI32(e + 12) == 0) continue;
                    int obj = Mem.LoadI32(e);
                    if (k != i) Mem.Copy(ArenaEntry(ar, k), e, 16);
                    Mem.StoreI32(obj + 8, k);
                    k++;
                }
                Mem.StoreI32(ar + 4, k);
            }
            Free(stack, n * 4);
            int flags = Mem.LoadI32(ar + 20);
            Mem.StoreI32(ar + 20, 0);
            if ((flags & ArenaPendingFree) != 0 || Mem.LoadI32(ar) == 0) ArenaFree(ar);
        }

        /// <summary>Array.Copy into an array whose elements can close a cycle: element by element, as stores.</summary>
        public static void ArenaArrayCopy(int src, int si, int dst, int di, int length)
        {
            // (each element's link and unlink take the runtime lock)
            int s = src + 16 + si * 4, d = dst + 16 + di * 4;
            bool back = src == dst && si < di;
            for (int k = 0; k < length; k++)
            {
                int i = back ? length - 1 - k : k;
                int v = Mem.LoadI32(s + i * 4);
                Retain(Borrow<object>(v));
                v = ArenaLink(v, dst);
                int old = Mem.LoadI32(d + i * 4);
                Mem.StoreI32(d + i * 4, v);
                ArenaUnlink(old, dst);
            }
        }

        /// <summary>Array.Clear on an array whose elements can close a cycle.</summary>
        public static void ArenaArrayClear(int arr, int index, int length)
        {
            int d = arr + 16 + index * 4;
            for (int i = 0; i < length; i++)
            {
                int old = Mem.LoadI32(d + i * 4);
                Mem.StoreI32(d + i * 4, 0);
                ArenaUnlink(old, arr);
            }
        }

        public static int ArrayLength(object a) => Mem.LoadI32(AddressOf(a) + 12);
        public static int ArrayData(object a) => AddressOf(a) + 16;
        public static int StringData(string s) => AddressOf(s) + 16;

        /// <summary>An exception escaped an export: report it to the engine (section 6.1).</summary>
        public static void ReportUnhandled(object ex)
        {
            string type = TypeName(ex);
            string msg = "";
            string stack = "";
            if (ex is Exception e)
            {
                if (e._fullTrace != null) e._stackTraceString = e._fullTrace;
                msg = e.Message ?? "";
                stack = e.StackTrace ?? "";
            }
            Engine.ReportException(StringData(type), type.Length, StringData(msg), msg.Length, StringData(stack), stack.Length);
        }

        /// <summary>A C# throw: record the stack from the throwing frame outward (cost only when throwing).</summary>
        public static void OnThrow(object ex)
        {
            if (ex is not Exception e) return;
            var buf = new char[16384];
            int n = Engine.CaptureStack(ArrayData(buf), buf.Length);
            e._fullTrace = n > 0 ? new string(buf, 0, n) : null;
            e._throwDepth = Engine.StackDepth();
            e._stackTraceString = e._fullTrace;
        }

        /// <summary>A catch clause took the exception: its trace runs from the throw to the catching frame, as in .NET.</summary>
        public static void OnCatch(object ex)
        {
            if (ex is not Exception e || e._fullTrace == null) return;
            int keep = e._throwDepth - Engine.StackDepth() + 1;
            string full = e._fullTrace;
            int pos = 0;
            for (int i = 0; i < keep && pos >= 0; i++)
            {
                pos = full.IndexOf((char)10, pos);
                if (pos >= 0) pos++;
            }
            e._stackTraceString = keep <= 0 ? null : pos < 0 ? full : full.Substring(0, pos - 1);
        }

        /// <summary>An item of a string.Format the compiler lowered (FormatLowering.cs): as string.Format formats it without a provider.</summary>
        public static string FormatItem(object? arg) => arg is IFormattable f ? f.ToString(null, null) : arg?.ToString() ?? "";

        /// <summary>Exception.GetStackTrace (manifest substitution): the trace captured when it was thrown.</summary>
        public static string StackTraceOf(Exception e) => e._stackTraceString ?? e._fullTrace ?? "";

        /// <summary>StackTrace.ToString(TraceFormat) (manifest substitution): cs2wasm's StackTrace has no frames (traces are text).</summary>
        public static string EmptyTrace(System.Diagnostics.StackTrace t, int format) => "";

        public static long CheckedMulI64(long a, long b)
        {
            if (a == 0 || b == 0) return 0;
            if (a == -1) { if (b == long.MinValue) ThrowHelpers.Overflow(); return -b; }
            if (b == -1) { if (a == long.MinValue) ThrowHelpers.Overflow(); return -a; }
            long r = unchecked(a * b);
            if (r / b != a) ThrowHelpers.Overflow();
            return r;
        }

        public static ulong CheckedMulU64(ulong a, ulong b)
        {
            if (a == 0 || b == 0) return 0;
            ulong r = unchecked(a * b);
            if (r / b != a) ThrowHelpers.Overflow();
            return r;
        }

        /// <summary>C# floating remainder (fmod): exact, ported from musl.</summary>
        public static double FMod(double x, double y)
        {
            long ux = F64Bits(x), uy = F64Bits(y);
            int ex = (int)((ux >> 52) & 0x7ff);
            int ey = (int)((uy >> 52) & 0x7ff);
            long sx = ux & long.MinValue;
            long i;
            if ((uy << 1) == 0 || double.IsNaN(y) || ex == 0x7ff)
                return (x * y) / (x * y);
            if ((ulong)(ux << 1) <= (ulong)(uy << 1))
            {
                if ((ux << 1) == (uy << 1)) return 0 * x;
                return x;
            }
            if (ex == 0)
            {
                for (i = ux << 12; i >= 0; i <<= 1) ex--;
                ux <<= -ex + 1;
            }
            else { ux &= -1L >>> 12; ux |= 1L << 52; }
            if (ey == 0)
            {
                for (i = uy << 12; i >= 0; i <<= 1) ey--;
                uy <<= -ey + 1;
            }
            else { uy &= -1L >>> 12; uy |= 1L << 52; }
            for (; ex > ey; ex--)
            {
                i = ux - uy;
                if (i >= 0)
                {
                    if (i == 0) return 0 * x;
                    ux = i;
                }
                ux <<= 1;
            }
            i = ux - uy;
            if (i >= 0)
            {
                if (i == 0) return 0 * x;
                ux = i;
            }
            for (; (ux >>> 52) == 0; ux <<= 1, ex--) ;
            if (ex > 0) { ux -= 1L << 52; ux |= (long)ex << 52; }
            else ux >>>= -ex + 1;
            ux |= sx;
            return F64FromBits(ux);
        }
    }

    /// <summary>The threads proposal's atomic instructions on a location (ref = its address).</summary>
    public static class Atomic
    {
        [Intrinsic] public static extern int Load32(ref int location);
        [Intrinsic] public static extern void Store32(ref int location, int value);
        [Intrinsic] public static extern int Add32(ref int location, int value);
        [Intrinsic] public static extern int Exchange32(ref int location, int value);
        [Intrinsic] public static extern int CompareExchange32(ref int location, int expected, int replacement);
        [Intrinsic] public static extern long Load64(ref long location);
        [Intrinsic] public static extern void Store64(ref long location, long value);
        [Intrinsic] public static extern long Add64(ref long location, long value);
        [Intrinsic] public static extern long Exchange64(ref long location, long value);
        [Intrinsic] public static extern long CompareExchange64(ref long location, long expected, long replacement);
        /// <summary>0 woken, 1 value differed, 2 timed out. Traps where the thread may not block (the engine thread).</summary>
        [Intrinsic] public static extern int Wait32(ref int location, int expected, long timeoutNs);
        [Intrinsic] public static extern int Notify(ref int location, int count);
        [Intrinsic] public static extern void Fence();
    }

    /// <summary>Worker threads (section 4.5): each runs an instance of the module on the shared memory.</summary>
    public static class Threads
    {
        internal const int StackSize = 1 << 20;
        static int s_nextId = 1;
        static int s_spawned;
        /// <summary>True once a worker exists: waits use real time and wake on s_generation.</summary>
        public static bool Spawned => Atomic.Load32(ref s_spawned) != 0;
        /// <summary>Bumped whenever a lock is released or a monitor pulsed; waiters sleep on it.</summary>
        internal static int s_generation;

        internal static int NewId() => Atomic.Add32(ref s_nextId, 1) + 1;

        internal static void SpawnCore(System.Threading.Thread t)
        {
            int stack = Rt.Alloc(StackSize);
            int tls = Rt.Alloc(Rt.TlsSize() + 16);
            t._stack = stack;
            t._tls = tls;
            Rt.Share(t); // the worker reaches it, and through it the delegate and what it captured
            Rt.Retain(t); // the worker's reference, taken back in Run
            Atomic.Store32(ref s_spawned, 1);
            Atomic.Add32(ref s_live, 1);
            if (Engine.ThreadSpawn(stack + StackSize, tls, t.ManagedThreadId, Rt.AddressOf(t), t.IsBackground ? 1 : 0) != 0)
            {
                Rt.Release(t);
                throw new System.OutOfMemoryException("Thread failed to start.");
            }
        }

        /// <summary>The worker side: runs the delegate, then marks the thread finished and wakes joiners.</summary>
        public static void Run(int threadAddr)
        {
            Limbo.Active();
            var t = Rt.Take<System.Threading.Thread>(threadAddr);
            System.Threading.Thread.SetCurrent(t);
            try { t.StartCallback(); }
            finally
            {
                Atomic.Add32(ref s_live, -1);
                System.Threading.Thread.OnThreadExiting(t);
                System.Threading.Thread.SetCurrent(null);
                Limbo.Quiescent();
                WakeAll();
            }
        }

        internal static void WakeAll() { Atomic.Add32(ref s_generation, 1); Atomic.Notify(ref s_generation, -1); }

        /// <summary>Waits for s_generation to move from g (the engine thread spins: it never blocks).</summary>
        // ---------------- parking (section 7.2) ----------------
        static int s_parkRequested, s_parked, s_live;

        /// <summary>Engine: ask every worker to stop at its next safe point (a wait, the end of a work item, an await).</summary>
        public static void ParkAll() { Atomic.Store32(ref s_parkRequested, 1); WakeAll(); }
        /// <summary>Engine: let parked workers go on.</summary>
        public static void ResumeAll() { Atomic.Store32(ref s_parkRequested, 0); Atomic.Notify(ref s_parkRequested, -1); }
        /// <summary>How many workers are parked; all are when this equals <see cref="LiveCount"/>.</summary>
        public static int ParkedCount() => Atomic.Load32(ref s_parked);
        public static int LiveCount() => Atomic.Load32(ref s_live);

        internal static void SafePoint()
        {
            Limbo.Checkpoint();
            if (Atomic.Load32(ref s_parkRequested) == 0 || Rt.ThreadId() == 1) return;
            Atomic.Add32(ref s_parked, 1);
            while (Atomic.Load32(ref s_parkRequested) != 0) Atomic.Wait32(ref s_parkRequested, 1, -1);
            Atomic.Add32(ref s_parked, -1);
        }

        internal static void WaitGeneration(int g)
        {
            SafePoint();
            // bounded, so timers and deadlines are rechecked: 1 ms
            if (Rt.ThreadId() == 1)
            {
                long end = Engine.MonotonicNs() + 1000000;
                while (Atomic.Load32(ref s_generation) == g && Engine.MonotonicNs() < end) { }
                return;
            }
            Atomic.Wait32(ref s_generation, g, 1000000);
        }
    }

    /// <summary>
    /// Section 4.5: a shared object whose count reaches zero is not destroyed at once. Another thread may have read a
    /// reference to it from shared memory just before the store that dropped it, and not counted it yet, as .NET's
    /// racing reads allow. It waits in limbo (count word: shared, in limbo, count 0) until every thread that runs
    /// program code has passed a safe point since: a wait, the end of a work item, the start or end of a thread or of
    /// an export, where a thread holds only counted references. A count taken meanwhile keeps it alive (the count
    /// rises from 0 again). Epoch-based reclamation: per-thread epochs (0: quiescent), a global epoch, and entries
    /// [object, epoch when it entered].
    /// </summary>
    public static class Limbo
    {
        const int S = 0x40000000, L = 0x20000000;
        static int s_epoch = 1, s_table, s_cap, s_list, s_listCap, s_count, s_since, s_oldest = int.MaxValue;
        [System.ThreadStatic] static int t_depth;
        [Intrinsic] static extern void DestroyObject(int p);

        static void SetEpoch(int value)
        {
            int id = Rt.ThreadId();
            if (id >= Atomic.Load32(ref s_cap))
            {
                Rt.RuntimeLock();
                if (id >= s_cap)
                {
                    int cap = Math.Max(16, Math.Max(s_cap * 2, id + 1));
                    int t = Rt.Alloc(cap * 4);
                    if (s_table != 0) { Mem.Copy(t, s_table, s_cap * 4); Rt.Free(s_table, s_cap * 4); }
                    s_table = t;
                    Atomic.Store32(ref s_cap, cap);
                }
                Atomic.Store32(ref Mem.RefI32(s_table + 4 * id), value);
                Rt.RuntimeUnlock();
                return;
            }
            Atomic.Store32(ref Mem.RefI32(s_table + 4 * id), value);
        }

        /// <summary>The thread runs program code: what it reads from now on is protected.</summary>
        public static void Active() => SetEpoch(Atomic.Load32(ref s_epoch));
        /// <summary>The thread holds no references that are not counted, and runs no program code until Active.</summary>
        public static void Quiescent() => SetEpoch(0);
        /// <summary>A safe point: the thread holds only counted references.</summary>
        public static void Checkpoint() { Active(); if (Atomic.Load32(ref s_count) != 0) Collect(); }
        public static void ExportEnter() { if (t_depth++ == 0) Active(); }
        public static void ExportExit() { if (--t_depth == 0) { Quiescent(); if (Atomic.Load32(ref s_count) != 0) Collect(); } }

        /// <summary>The count of shared object p fell to zero (its count word is already S | L).</summary>
        public static void Add(int p)
        {
            Rt.RuntimeLock();
            if (s_count == s_listCap)
            {
                int cap = Math.Max(64, s_listCap * 2);
                int l = Rt.Alloc(cap * 8);
                if (s_list != 0) { Mem.Copy(l, s_list, s_count * 8); Rt.Free(s_list, s_listCap * 8); }
                s_list = l; s_listCap = cap;
            }
            int ep = Atomic.Load32(ref s_epoch);
            Mem.StoreI32(s_list + 8 * s_count, p);
            Mem.StoreI32(s_list + 8 * s_count + 4, ep);
            if (ep < s_oldest) s_oldest = ep;
            Atomic.Store32(ref s_count, s_count + 1);
            // (a collection scans the list: run one when the list has grown by half since, so the cost stays linear)
            bool go = ++s_since >= Math.Max(64, s_count >> 1);
            Rt.RuntimeUnlock();
            if (go) Collect();
        }

        /// <summary>Destroys the entries every thread has passed a safe point since; advances the epoch.</summary>
        public static void Collect()
        {
            Rt.RuntimeLock();
            int min = int.MaxValue;
            for (int i = 0; i < s_cap; i++)
            {
                int e = Atomic.Load32(ref Mem.RefI32(s_table + 4 * i));
                if (e != 0 && e < min) min = e;
            }
            // nothing can be ready while some thread has not passed a safe point since the oldest entry came in
            if (min <= s_oldest)
            {
                s_since = 0;
                Atomic.Add32(ref s_epoch, 1);
                Rt.RuntimeUnlock();
                return;
            }
            int ready = 0, keep = 0, n = s_count, oldest = int.MaxValue;
            int list = n == 0 ? 0 : Rt.Alloc(n * 4);
            for (int i = 0; i < n; i++)
            {
                int p = Mem.LoadI32(s_list + 8 * i), e = Mem.LoadI32(s_list + 8 * i + 4);
                if (e < min) { Mem.StoreI32(list + 4 * ready, p); ready++; }
                else { Mem.StoreI32(s_list + 8 * keep, p); Mem.StoreI32(s_list + 8 * keep + 4, e); keep++; if (e < oldest) oldest = e; }
            }
            s_oldest = keep == 0 ? int.MaxValue : oldest;
            Atomic.Store32(ref s_count, keep);
            s_since = 0;
            Atomic.Add32(ref s_epoch, 1);
            Rt.RuntimeUnlock();
            for (int i = 0; i < ready; i++) Finish(Mem.LoadI32(list + 4 * i));
            if (list != 0) Rt.Free(list, n * 4);
        }

        /// <summary>Destroys p unless a count was taken while it waited.</summary>
        static void Finish(int p)
        {
            ref int w = ref Mem.RefI32(p);
            while (true)
            {
                int v = Atomic.Load32(ref w);
                if ((v & ~(S | L)) == 0) { if (Atomic.CompareExchange32(ref w, v, 0) == v) { DestroyObject(p); return; } }
                else if (Atomic.CompareExchange32(ref w, v, v & ~L) == v) return;
            }
        }

        /// <summary>ThreadPool.NotifyWorkItemComplete (manifest substitution): the end of a work item is a safe point.</summary>
        internal static bool NotifyWorkItemComplete(System.Threading.ThreadInt64PersistentCounter.ThreadLocalNode node, int currentTimeMs)
        {
            Checkpoint();
            return System.Threading.PortableThreadPool.ThreadPoolInstance.NotifyWorkItemComplete(node, currentTimeMs);
        }
    }

    /// <summary>Section 4.5: marking objects shared (bit 30 of the count word), iteratively, with a per-thread worklist.</summary>
    public static class Sharing
    {
        const int S = 0x40000000;
        [System.ThreadStatic] static int t_buf, t_cap, t_len;
        [Intrinsic] static extern void Visit(int obj);

        static bool Mark(int x)
        {
            if (x == 0) return false;
            int w = Mem.LoadI32(x);
            if (w < -1) { Rt.ShareArena(-w); return false; } // an arena member: the arena is shared, its members queued
            if (w <= 0 || (w & S) != 0) return false;
            Atomic.Store32(ref Mem.RefI32(x), w | S);
            return true;
        }

        static void Push(int x)
        {
            if (t_len == t_cap)
            {
                int cap = t_cap == 0 ? 64 : t_cap * 2;
                int nb = Rt.Alloc(cap * 4);
                if (t_buf != 0) { Mem.Copy(nb, t_buf, t_len * 4); Rt.Free(t_buf, t_cap * 4); }
                t_buf = nb; t_cap = cap;
            }
            Mem.StoreI32(t_buf + t_len * 4, x);
            t_len++;
        }

        static void Drain()
        {
            while (t_len > 0) { t_len--; Visit(Mem.LoadI32(t_buf + t_len * 4)); }
        }

        public static void Share(int p) { if (Mark(p)) Push(p); Drain(); }

        internal static void PushMember(int member) => Push(member);
        public static void MarkPush(int x) { if (Mark(x)) Push(x); }
        public static void RevisitIfShared(int arr)
        {
            if (arr == 0) return;
            int w = Mem.LoadI32(arr);
            if (w > 0 && (w & S) != 0) { Visit(arr); Drain(); }
        }
    }

    public static class Slices
    {
        /// <summary>The sub-array a `..` slice pattern or range binds.</summary>
        public static T[] SubArray<T>(T[] array, int start, int length)
        {
            var r = new T[length];
            for (int i = 0; i < length; i++) r[i] = array[start + i];
            return r;
        }
    }

    public static class ThrowHelpers
    {
        /// <summary>The exception a failed static initializer leaves: thrown now and at every later access.</summary>
        public static object TypeInitFailed(string typeName, object inner) =>
            new System.TypeInitializationException(typeName, inner as System.Exception);
        public static void NullReference() => throw new NullReferenceException();
        public static void IndexOutOfRange() => throw new IndexOutOfRangeException();
        public static void Overflow() => throw new OverflowException();
        public static void DivideByZero() => throw new DivideByZeroException();
        public static void OutOfMemory() => throw new OutOfMemoryException();
        public static void SwitchExpression() => throw new System.Runtime.CompilerServices.SwitchExpressionException();
        public static void InvalidCast(object o, string target) =>
            throw new InvalidCastException("Unable to cast object of type '" + Rt.TypeName(o) + "' to type '" + target + "'.");
        public static void ArgumentOutOfRange(string name) => throw new ArgumentOutOfRangeException(name);
        public static void ArgumentNull(string name) => throw new ArgumentNullException(name);
        public static void InvalidOperation(string msg) => throw new InvalidOperationException(msg);
        public static void NotSupported() => throw new NotSupportedException();
    }
}
