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
    }

    /// <summary>Engine imports used by the library (section 7.4). The engine supplies these.</summary>
    public static class Engine
    {
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

        public static int Alloc(int size)
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
                    Mem.StoreI32(slot, Mem.LoadI32(head));
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
            size = (size + 7) & ~7;
            if (size < 16) size = 16;
            if (size <= MaxSmall)
            {
                int slot = s_table + (size >> 3) * 4;
                Mem.StoreI32(addr, Mem.LoadI32(slot));
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
            s_hash++;
            Mem.StoreI32(a + 8, s_hash);
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
        const int ArenaBusy = 1, ArenaPendingFree = 2;
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
            int cv = Mem.LoadI32(val), ca = Mem.LoadI32(container);
            if (cv == -1 || cv == 0 || ca == -1 || ca == 0) return val; // immortal, or being destroyed
            int g = SccOf(Mem.LoadI32(val + 4));
            if (g == 0 || g != SccOf(Mem.LoadI32(container + 4))) return val;
            if (ca > 0 && Mem.LoadI32(container + 8) < 0) return val; // pinned
            if (cv > 0 && Mem.LoadI32(val + 8) < 0) return val;
            int ra = ca < -1 ? -ca : 0, rv = cv < -1 ? -cv : 0;
            if (ra != 0 && (Mem.LoadI32(ra + 20) & ArenaBusy) != 0) return val;
            if (rv != 0 && (Mem.LoadI32(rv + 20) & ArenaBusy) != 0) return val;
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
            int co = Mem.LoadI32(old);
            if (co < -1 && co == Mem.LoadI32(container)) { ArenaDirty(-co); return; } // uncounted: it may be garbage now
            Release(Borrow<object>(old));
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
                if (e._fullTrace != null) e._stackTrace = e._fullTrace;
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
            e._stackTrace = e._fullTrace;
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
            e._stackTrace = keep <= 0 ? null : pos < 0 ? full : full.Substring(0, pos - 1);
        }

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
