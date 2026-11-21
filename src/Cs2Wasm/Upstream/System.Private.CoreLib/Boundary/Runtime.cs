// cs2wasm runtime boundary: memory, counting, exceptions, engine imports.
// Compiled by cs2wasm itself together with the program. Methods marked [Intrinsic] are emitted inline by the compiler.
#pragma warning disable CS0626, CS0436, CS0660, CS0661, CS0659
using System;

namespace Cs2Wasm.Runtime
{
    /// <summary>On a method: emitted inline by the compiler. On a static field: a [ThreadStatic] one in a program
    /// with threads, a plain static in one without (the arena machinery's scratch state, each thread's own once groups
    /// are locked apart, GroupLocks.cs).</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Field)]
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
        /// <summary>1 when the module's main thread may block in a wait (a Node process, a Web Worker); 0 when it is an
        /// engine's thread that must not (a browser page's main thread): it then spins.</summary>
        [Import("cs2wasm", "main_can_block")] public static extern int MainCanBlock();
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

    public static partial class Rt
    {
        /// <summary>A string constant pattern on a span of chars (`span is "abc"`, `switch (span) { case "abc": }`): the
        /// span's characters equal the string's (C#: MemoryExtensions.SequenceEqual).</summary>
        public static bool SpanEqualsString(System.ReadOnlySpan<char> span, string value) => System.MemoryExtensions.SequenceEqual(span, value);

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
        // Allocator: size classes of 4 bytes up to 2 KiB with free lists, first-fit list above. (4, not 8: an object whose
        // fields need no 8-byte alignment is a multiple of 4 bytes, as on NativeAOT's 32-bit targets; one that does has
        // a size that is a multiple of 8, Types.LayoutOf, and so a block at a multiple of 8: a page's blocks start at 24.)
        // All state is in linear memory (section 4.6); freed space is reused to keep the heap compact.
        // ------------------------------------------------------------------
        const int MaxSmall = 2048;
        const int Classes = MaxSmall / 4 + 1;
        // Where a free small block links to the next (its first word is zero), and the smallest block: without threads
        // the link is the second word, so a block of 8 bytes can be free (a cycle-capable object's side words; an
        // object of 12, with no fields, takes 12); with threads it is at 8, clear of a dead object's count word that a
        // reference read without a lock looks at (and of its type word), so 16 at least.
        /// <summary>A small block's size for a request: steps of 4 bytes up to 128, of 8 up to 256, then eight sizes per
        /// doubling (32 to 512, 64 to 1024, 128 to 2048). Every size its own class is a page partly used: 380 classes
        /// above 512 bytes held 3.4 MB of the self-hosted compiler's blocks in 7.8 MB of pages. (A size above MaxSmall:
        /// a multiple of 4, the large blocks round it themselves.) A multiple of 8 stays one.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int RoundSmall(int n)
        {
            if (!WideSizes()) return (n + 3) & ~3;
            int step = n <= 128 || n > MaxSmall ? 4 : n <= 256 ? 8 : 1 << (28 - I32Clz(n - 1));
            return (n + step - 1) & -step;
        }

        /// <summary>Whether the sizes above 128 bytes are in wider steps (RoundSmall): a constant at the end of the build,
        /// true for a program that makes objects of many types (RuntimeGen.Finish). A small one has few sizes, and its
        /// steps of 4 bytes are two instructions where they are used.</summary>
        [Intrinsic] static extern bool WideSizes();
        static int FreeLink => SingleThreaded() ? 4 : 8;
        static int MinBlock => SingleThreaded() ? 8 : 16;
        static int s_top, s_end, s_table, s_hash;

        // ------------------------------------------------------------------
        // Small blocks in pages (programs without threads). Blocks of one size are cut from 16 KB pages of that size,
        // and a page's freed blocks are used again before another page is touched: objects made one after the other
        // lie next to each other however the program has allocated and freed before, as they would after a moving
        // collector (lists of freed blocks for the whole heap hand out blocks from all over it, and a walk over a
        // list or a tree then misses the cache at every step).
        // A page: [0] its freed blocks (linked at +8), [4] next page and [8] previous one (the size's pages with
        // freed blocks, or the empty pages), [12] the first block never used (0: none), [16] blocks in use,
        // [20] size class * 2 + (1: it is the size's current page or among its pages with freed blocks).
        // A page nothing is left in goes back to the empty pages, for any size. Pages come four at a time from
        // 64 KB of memory at a multiple of 64 KB, noted in a map, so a small block that is not in a page (what is
        // left over when a large block is cut) is told from one that is, and kept on the old lists by size.
        // ------------------------------------------------------------------
        // The block of each size that was freed last is kept aside (s_hot, a word per size) and is the next one of
        // its size to be allocated: code that makes an object, uses it and lets it go, over and over, then reads and
        // writes that one word, and none of the page (each of which the next allocation would wait for: the
        // list of the page, its count of blocks in use). The page still counts the block as in use.
        const int PageSize = 16384, PageHead = 24;
        static int s_cur, s_avail, s_freePages, s_hot;

        // ------------------------------------------------------------------
        // Which block an address is in (docs/AUDIT.md step 7.A). The heap is handed out in granules of 1 KiB: a page
        // of small blocks is sixteen of them, a block above 2 KiB a run of as many as it needs. A table has a word
        // per granule (in leaves of 4 MiB of address space, made as the heap's top reaches them):
        //   0          nothing that holds blocks (the tables, a leaf, memory never handed out)
        //   page | 1   a page of small blocks (a program without threads)
        //   start | 2  a block of its own, which starts at `start`
        //   n << 3 | 3 the first and the last granule of a free run of n granules
        //   page | 4   a page of small blocks of one kind and size for good (a program with threads)
        // So a store that has only a slot's address (through a `ref`, a span, a struct's own method) can ask which
        // object the slot is in: BlockOf.
        // ------------------------------------------------------------------
        const int Granule = 1024, GShift = 10, LeafBytes = 16384, RunBins = 24;
        static int s_mapTop, s_runBins, s_runLo;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] static int MapGet(int addr)
        {
            int leaf = Mem.LoadI32(s_mapTop + ((int)((uint)addr >> 22) << 2));
            return leaf == 0 ? 0 : Mem.LoadI32(leaf + ((int)(((uint)addr >> GShift) & 4095) << 2));
        }

        static void MapSet(int addr, int v) =>
            Mem.StoreI32(Mem.LoadI32(s_mapTop + ((int)((uint)addr >> 22) << 2)) + ((int)(((uint)addr >> GShift) & 4095) << 2), v);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] static bool IsPage(int addr) => (MapGet(addr) & 7) == 1;

        /// <summary>The block the address is in (its start), or 0: the stack, the statics, memory that holds no block.
        /// A block: an object (for one of a cycle-capable type, the words before its header first), or memory Alloc
        /// gave.</summary>
        public static int BlockOf(int addr)
        {
            if (s_top == 0 || (uint)addr >= (uint)s_top || (uint)addr < (uint)s_runLo) return 0;
            int e = MapGet(addr), k = e & 7;
            if (k == 2) return e & ~7;
            if (k == 1 || k == 4)
            {
                int pg = e & ~7;
                int size = (Mem.LoadI32(pg + 20) >> 1) << 2;
                int off = addr - pg - PageHead;
                if (off < 0 || size == 0) return 0;
                return pg + PageHead + (int)((uint)off / (uint)size) * size;
            }
            return 0;
        }

        /// <summary>Makes sure the heap's top has this many bytes (the memory grows; growing allocates too, the
        /// exception kept for when memory runs out, so the top may have moved when this returns).</summary>
        static void Reserve(int bytes)
        {
            if ((uint)(s_end - s_top) < (uint)bytes)
            {
                GrowFor(s_top + bytes - s_end);
                s_end = Mem.Size() * 65536;
            }
        }

        /// <summary>Granules at the heap's top, never used before (clear), with the table's leaves for them.</summary>
        static int TopTake(int bytes)
        {
            while (true)
            {
                int a = s_top;
                int r0 = (int)((uint)a >> 22), r1 = (int)((uint)(a + bytes - 1) >> 22);
                int missing = -1;
                for (int r = r0; r <= r1; r++)
                    if (Mem.LoadI32(s_mapTop + (r << 2)) == 0) { missing = r; break; }
                if (missing >= 0)
                {
                    Reserve(LeafBytes);
                    if (s_top != a) continue;
                    s_top = a + LeafBytes;
                    Mem.StoreI32(s_mapTop + (missing << 2), a);
                    continue;
                }
                Reserve(bytes);
                if (s_top != a) continue;
                s_top = a + bytes;
                return a;
            }
        }

        static int RunBin(int n) { int b = 31 - I32Clz(n); return b < RunBins ? b : RunBins - 1; }

        // A free run: [0] zero (a count word that says dead), [4] its granules, [8] next, [12] previous of its size's list.
        static void RunInsert(int addr, int n)
        {
            int slot = s_runBins + (RunBin(n) << 2);
            int head = Mem.LoadI32(slot);
            Mem.StoreI32(addr, 0); Mem.StoreI32(addr + 4, n); Mem.StoreI32(addr + 8, head); Mem.StoreI32(addr + 12, 0);
            if (head != 0) Mem.StoreI32(head + 12, addr);
            Mem.StoreI32(slot, addr);
            MapSet(addr, (n << 3) | 3);
            MapSet(addr + ((n - 1) << GShift), (n << 3) | 3);
        }

        static void RunUnlink(int r)
        {
            int next = Mem.LoadI32(r + 8), prev = Mem.LoadI32(r + 12);
            if (prev != 0) Mem.StoreI32(prev + 8, next); else Mem.StoreI32(s_runBins + (RunBin(Mem.LoadI32(r + 4)) << 2), next);
            if (next != 0) Mem.StoreI32(next + 12, prev);
        }

        /// <summary>A run of n granules: a free one (the first of its size's list that is long enough, the rest of it
        /// free again), else from the top (clear then). Whoever takes it writes the table's words for it.</summary>
        static int AllocRun(int n, out bool clear)
        {
            clear = false;
            for (int bin = RunBin(n); bin < RunBins; bin++)
            {
                int r = Mem.LoadI32(s_runBins + (bin << 2));
                while (r != 0)
                {
                    int len = Mem.LoadI32(r + 4);
                    if (len >= n)
                    {
                        RunUnlink(r);
                        if (len > n) RunInsert(r + (n << GShift), len - n);
                        // (its ends are no free run's any more: a run freed beside it looks at these words. Words
                        // inside it may still say "free run", left by runs that were joined into this one: whoever
                        // cuts a run further clears the ends of each piece it keeps)
                        MapSet(r, 0); MapSet(r + ((n - 1) << GShift), 0);
                        return r;
                    }
                    r = Mem.LoadI32(r + 8);
                }
            }
            clear = true;
            return TopTake(n << GShift);
        }

        /// <summary>A run is free again: joined with the free runs before and after it (the heap stays compact,
        /// section 4.6).</summary>
        static void FreeRun(int addr, int n)
        {
            if ((uint)addr > (uint)s_runLo)
            {
                int e = MapGet(addr - Granule);
                if ((e & 7) == 3) { int len = (int)((uint)e >> 3); int before = addr - (len << GShift); RunUnlink(before); addr = before; n += len; }
            }
            int after = addr + (n << GShift);
            if ((uint)after < (uint)s_top)
            {
                int e = MapGet(after);
                if ((e & 7) == 3) { RunUnlink(after); n += (int)((uint)e >> 3); }
            }
            RunInsert(addr, n);
        }

        /// <summary>A run of n granules at a multiple of `align` granules (a power of two).</summary>
        static int AllocRunAligned(int n, int align)
        {
            // A free run with an aligned window of n granules in it, else the heap's top made aligned. (Taking n +
            // align - 1 granules and giving back what lies before and after the window left, between two pages cut
            // one after the other at the top, a run of exactly a page at a page's boundary, which a request for n +
            // align - 1 never takes: one more such run for each page, every later page walking all of them under the
            // runtime lock, and half the memory of the pages lost.)
            int mask = (align << GShift) - 1;
            for (int bin = RunBin(n); bin < RunBins; bin++)
                for (int r = Mem.LoadI32(s_runBins + (bin << 2)); r != 0; r = Mem.LoadI32(r + 8))
                {
                    int len = Mem.LoadI32(r + 4);
                    int start = (r + mask) & ~mask, head = (start - r) >> GShift;
                    if (head + n > len) continue;
                    // (r is a whole free run: what lies around it is not free, so the pieces left need no joining)
                    RunUnlink(r);
                    int tail = len - head - n;
                    MapSet(start, 0); MapSet(start + ((n - 1) << GShift), 0);
                    if (head > 0) RunInsert(r, head);
                    if (tail > 0) RunInsert(start + (n << GShift), tail);
                    return start;
                }
            while (true)
            {
                int a = s_top;
                int pad = (((a + mask) & ~mask) - a) >> GShift;
                int b = TopTake((pad + n) << GShift);
                // (the top moved while it was taken, for the table's leaf: whatever is around the window is given back)
                int start = (b + mask) & ~mask, head = (start - b) >> GShift, tail = pad - head;
                if (tail < 0) { FreeRun(b, pad + n); continue; }
                if (head > 0) FreeRun(b, head);
                if (tail > 0) FreeRun(start + (n << GShift), tail);
                return start;
            }
        }

        /// <summary>A block of its own (above 2 KiB): a run of granules, cleared.</summary>
        static int AllocHuge(int size)
        {
            int n = (int)(((uint)size + (Granule - 1)) >> GShift);
            int a = AllocRun(n, out bool clear);
            for (int i = 0; i < n; i++) MapSet(a + (i << GShift), a | 2);
            if (!clear) Mem.Fill(a, 0, size);
            return a;
        }

        /// <summary>Four empty pages: 64 KiB at a multiple of 64 KiB.</summary>
        static void NewChunk()
        {
            int a = AllocRunAligned(64, 64);
            for (int i = 0; i < 64; i++) MapSet(a + (i << GShift), (a + (i >> 4) * PageSize) | 1);
            // (the lowest first out: blocks made one after the other go up through memory, page after page)
            for (int p = a + 65536 - PageSize; p != a - PageSize; p -= PageSize) { Mem.StoreI32(p + 4, s_freePages); s_freePages = p; }
        }

        /// <summary>A page of small blocks of one kind and size for good, the thread's own (ThreadPage): its blocks are
        /// cut from it in order ([12]), its freed ones go back onto its list ([0]).</summary>
        static int NewOwnedPage(int size, int kind)
        {
            int pg = AllocRunAligned(16, 16);
            for (int i = 0; i < 16; i++) MapSet(pg + (i << GShift), pg | 4);
            Mem.StoreI32(pg, 0); Mem.StoreI32(pg + 4, 0); Mem.StoreI32(pg + 8, ThreadId() + 1);
            Mem.StoreI32(pg + 12, pg + PageHead);
            Mem.StoreI32(pg + 16, kind);
            // (the current page of its size: flagged, as one on the list of pages with freed blocks)
            Mem.StoreI32(pg + 20, (size >> 2) * 2 + 1);
            return pg;
        }

        /// <summary>A page of small blocks of one kind and size for good (a program with threads: a block keeps its
        /// kind, see below): all its blocks, linked at 8; no thread's own.</summary>
        static int NewThreadPage(int size, int kind)
        {
            int pg = AllocRunAligned(16, 16);
            for (int i = 0; i < 16; i++) MapSet(pg + (i << GShift), pg | 4);
            Mem.StoreI32(pg, 0); Mem.StoreI32(pg + 4, 0); Mem.StoreI32(pg + 8, 0); Mem.StoreI32(pg + 12, 0);
            Mem.StoreI32(pg + 16, kind);
            Mem.StoreI32(pg + 20, (size >> 2) * 2);
            int list = 0;
            for (int b = pg + PageHead + ((PageSize - PageHead) / size - 1) * size; b >= pg + PageHead; b -= size)
            {
                Mem.StoreI32(b, 0);
                Mem.StoreI32(b + 8, list);
                list = b;
            }
            return list;
        }

        /// <summary>A small block of the size (a multiple of 4, 16 at least), cleared.</summary>
        static int PageAlloc(int size)
        {
            int c = size >> 2;
            int curSlot = s_cur + c * 4;
            // the size's run first (AllocObject takes from it without coming here: this is for the other callers)
            int runSlot = s_hot + (Classes + c) * 4;
            int rp = Mem.LoadI32(runSlot);
            if (rp != Mem.LoadI32(runSlot + Classes * 4)) { Mem.StoreI32(runSlot, rp + size); return rp; }
            while (true)
            {
                int pg = Mem.LoadI32(curSlot);
                if (pg != 0)
                {
                    int head = Mem.LoadI32(pg);
                    if (head != 0)
                    {
                        Mem.StoreI32(pg, Mem.LoadI32(head + 4));
                        Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 1);
                        Zero(head, size);
                        return head;
                    }
                    int bump = Mem.LoadI32(pg + 12);
                    if (bump != 0)
                    {
                        // (clear: the page was cleared when it was taken, below)
                        int nb = bump + size;
                        Mem.StoreI32(pg + 12, nb + size <= pg + PageSize ? nb : 0);
                        Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 1);
                        return bump;
                    }
                    // full: neither current nor among the pages with freed blocks, until a block of it is freed
                    Mem.StoreI32(pg + 20, Mem.LoadI32(pg + 20) & ~1);
                    Mem.StoreI32(curSlot, 0);
                }
                // a block of the size that is in no page
                int oddSlot = s_table + c * 4;
                int odd = Mem.LoadI32(oddSlot);
                if (odd != 0) { Mem.StoreI32(oddSlot, Mem.LoadI32(odd + 4)); Zero(odd, size); return odd; }
                // a page of the size with freed blocks; else an empty page
                int availSlot = s_avail + c * 4;
                int q = Mem.LoadI32(availSlot);
                if (q != 0)
                {
                    int next = Mem.LoadI32(q + 4);
                    Mem.StoreI32(availSlot, next);
                    if (next != 0) Mem.StoreI32(next + 8, 0);
                    Mem.StoreI32(curSlot, q);
                    continue;
                }
                while (s_freePages == 0) NewChunk();
                int np = s_freePages;
                s_freePages = Mem.LoadI32(np + 4);
                // (cleared whole, once: a block of it that was never used needs no clearing when it is allocated)
                // An empty page becomes the size's run: its blocks are handed out one after the other by moving a
                // word (the run's next block), without touching the page. The page counts them all as in use from
                // the start (the ones not handed out yet too: it cannot become empty while the run is in it), and is
                // neither the current page nor among those with freed blocks until one of its blocks is freed.
                Mem.Fill(np, 0, PageSize);
                int cap = (PageSize - PageHead) / size;
                Mem.StoreI32(np + 16, cap);
                Mem.StoreI32(np + 20, c * 2);
                int first = np + PageHead;
                Mem.StoreI32(runSlot, first + size);
                Mem.StoreI32(runSlot + Classes * 4, first + cap * size);
                return first;
            }
        }

        /// <summary>A block of a page is free again.</summary>
        static void PageFree(int addr)
        {
            int pg = addr & ~(PageSize - 1);
            Mem.StoreI32(addr, 0);
            Mem.StoreI32(addr + 4, Mem.LoadI32(pg));
            Mem.StoreI32(pg, addr);
            int used = Mem.LoadI32(pg + 16) - 1;
            Mem.StoreI32(pg + 16, used);
            int fl = Mem.LoadI32(pg + 20);
            int c = fl >> 1;
            int availSlot = s_avail + c * 4;
            if ((fl & 1) == 0)
            {
                // it was full: among its size's pages with freed blocks from now on
                int head = Mem.LoadI32(availSlot);
                Mem.StoreI32(pg + 4, head);
                Mem.StoreI32(pg + 8, 0);
                if (head != 0) Mem.StoreI32(head + 8, pg);
                Mem.StoreI32(availSlot, pg);
                Mem.StoreI32(pg + 20, fl | 1);
            }
            else if (used == 0 && Mem.LoadI32(s_cur + c * 4) != pg)
            {
                // nothing left in it: an empty page, for any size
                int next = Mem.LoadI32(pg + 4), prev = Mem.LoadI32(pg + 8);
                if (prev != 0) Mem.StoreI32(prev + 4, next); else Mem.StoreI32(availSlot, next);
                if (next != 0) Mem.StoreI32(next + 8, prev);
                Mem.StoreI32(pg + 4, s_freePages);
                s_freePages = pg;
            }
        }

        static void InitHeap()
        {
            int b = (HeapBase() + 15) & ~15;
            s_table = b;
            // (the lists of the two kinds of blocks: Lists)
            b += 2 * (Classes * 4 + LargeBins * 4);
            // (the pages of small blocks: each size's current page, its pages with freed blocks, the block of each
            // size freed last, and which 64 KB of memory are pages)
            s_cur = b; b += Classes * 4;
            s_avail = b; b += Classes * 4;
            // (after the hot blocks, each size's run: its next block, and where the run ends)
            s_hot = b; b += 3 * Classes * 4;
            // (the free runs by size, and the table's top: a leaf for every 4 MiB of address space)
            s_runBins = b; b += RunBins * 4;
            s_mapTop = b; b += 1024 * 4;
            if (MemStatsOn()) { s_ms = b; b += MsArea; }
            s_top = (b + Granule - 1) & ~(Granule - 1);
            s_runLo = s_top;
            s_end = Mem.Size() * 65536;
        }

        [Intrinsic] public static extern int ThreadId();
        /// <summary>CS2WASM_SHARE_CHECK=1 (diagnosis of section 4.5's sharing): true, and an object's hash word carries
        /// the thread that allocated it in bits 24-30; a plain count update from another thread is reported.</summary>
        [Intrinsic] public static extern bool ShareCheck();
        /// <summary>CS2WASM_SHARE_CHECK: a plain count of an object another thread made (TypedCounts.cs) names it.</summary>
        public static void ShareCheckFail(int p)
        {
            throw new System.InvalidOperationException("share check: a plain count of " + TypeName(Borrow<object>(p)) + " made by thread " + ((Mem.LoadI32(p + 8) >> 24) & 0x1F) + ", counted on thread " + ThreadId());
        }
        /// <summary>CompileOptions.Checked (the tests' build): freed memory is filled with a pattern and never used
        /// again, so a reference used after its object was freed reads the pattern (a reference read from it traps
        /// at its use, a number differs from .NET's) where it would have read whatever took the block.</summary>
        [Intrinsic] public static extern bool Checked();

        static int s_violations;

        /// <summary>CS2WASM_SHARE_CHECK: an object another thread allocated and never shared had its count updated
        /// plainly on this one (the first few, with the stack).</summary>
        public static unsafe void ShareViolation(int a)
        {
            if (Atomic.Add32(ref s_violations, 1) > 5) return;
            if (a > 0 && (Mem.LoadI32(a) >> 16) == 0x0EAD)
            {
                string uaf = "share check: use after free: an object freed on thread " + (Mem.LoadI32(a) & 0xFFFF).ToString() + ", used on thread " + ThreadId().ToString() + " (type id " + Mem.LoadI32(a + 4).ToString() + ")\n";
                fixed (char* p = uaf) Engine.Write((int)p, uaf.Length);
                Mem.StoreI32(-4, 0);
            }
            if (a < 0)
            {
                string bad = "share check: an object of " + TypeInfo.Type(Mem.LoadI32(-a + 4)).ToString() + " has a field the sharing visitor reads as a reference, which is none\n";
                fixed (char* p = bad) Engine.Write((int)p, bad.Length);
                Mem.StoreI32(-4, 0);
            }
            string text = "share violation: " + TypeInfo.Type(Mem.LoadI32(a + 4)).ToString() + " allocated on thread " + ((Mem.LoadI32(a + 8) >> 24) & 0x1F).ToString()
                + ", count " + Mem.LoadI32(a).ToString() + ", on thread " + ThreadId().ToString() + "\n";
            fixed (char* p = text) Engine.Write((int)p, text.Length);
            // (a trap: the engine's stack trace shows where)
            Mem.StoreI32(-4, 0);
        }

        /// <summary>The module is built for an engine without threads (CS2WASM_NO_THREADS): starting one throws.</summary>
        [Intrinsic] public static extern bool NoThreads();
        /// <summary>CS2WASM_NO_DROP_CHECK=1 (a measurement, at compile time: what a let-go structure kept by its own
        /// holds leaves is not freed): the check at the drop is off.</summary>
        [Intrinsic] static extern bool NoDropCheck();
        // CS2WASM_DROP_STATS=1 (a diagnosis, at compile time): the check at the drop counts what it does and says so
        [Intrinsic] static extern bool DropStats();
        /// <summary>CS2WASM_STALE_HOLDS=1 (at compile time, an experiment): an owned member keeps a hold once made, without
        /// threads (docs/AUDIT.md, holds kept once made: 90% fewer hold changes in the self-hosted compiler, its time the
        /// same, its peak memory 12% more, which is unexplained, so not the default).</summary>
        [Intrinsic] static extern bool StaleHolds();
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool StickyHolds() => !ThreadMode() && StaleHolds();

        // A member under a top whose check at the drop failed has a mark in its note word (even; a top's own note is odd):
        // 2 if that check failed for members held from outside (the top's number counts them, TopHeld walks to it), 4 if
        // for references from outside what it owns (NoteOutside walks to it). A walk only for the kind whose number is
        // kept: in the self-hosted compiler 26 million walks to a top, most for marks of checks of the other kind.
        const int MarkHeld = 2, MarkOutside = 4;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool HeldMarked(int note) => (note & (1 | MarkHeld)) != 0;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool OutsideMarked(int note) => (note & (1 | MarkOutside)) != 0;
        static long s_dsCalls, s_dsWalked, s_dsDead, s_dsFailInside, s_dsFailHeld, s_dsMax, s_dsFromRelease, s_dsFromUnlink, s_dsTops, s_dsTopHops, s_dsTopMarked, s_dsTopFar;
        static int s_dsBuf;
        static void DsNum(ref int at, string label, long v)
        {
            for (int i = 0; i < label.Length; i++) { Mem.StoreU16(s_dsBuf + 2 * at, label[i]); at++; }
            if (v == 0) { Mem.StoreU16(s_dsBuf + 2 * at, '0'); at++; }
            int start = at;
            while (v > 0) { Mem.StoreU16(s_dsBuf + 2 * at, '0' + (int)(v % 10)); at++; v /= 10; }
            for (int i = start, j = at - 1; i < j; i++, j--) { int a = Mem.LoadU16(s_dsBuf + 2 * i); Mem.StoreU16(s_dsBuf + 2 * i, Mem.LoadU16(s_dsBuf + 2 * j)); Mem.StoreU16(s_dsBuf + 2 * j, a); }
        }
        /// <summary>Checked builds' diagnoses: a line on the console, then the trap shows the whole stack.</summary>
        static void ChkSay(string text)
        {
            int b = Alloc(2 * text.Length + 2);
            for (int i = 0; i < text.Length; i++) Mem.StoreU16(b + 2 * i, text[i]);
            Mem.StoreU16(b + 2 * text.Length, 10);
            Engine.Write(b, text.Length + 1);
        }
        // (and, with it, how often each path of the arena operations runs: OpStat's numbers, printed with DsPrint)
        static int s_opStats;
        const int OpN = 28;
        const string OpNames = "link,link-plain,link-same,link-core,join,merge,joined-visit,unlink,unlink-plain,unlink-up,unlink-inside,leave-visit,leave,reevaluate,retain-slow,release-slow,update-hold,handover,link-at,unlink-at,take-ownership,unlink-top,destroy,link-up,hold-change,hold-up,retain-fast,release-fast";
        // (a constant test where it is asked, so that without the diagnosis nothing is left of it, not even a call)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void OpStat(int k) { if (DropStats()) OpStatCount(k); }
        static void OpStatCount(int k)
        {
            if (s_opStats == 0) { s_opStats = Alloc(OpN * 8); for (int i = 0; i < OpN; i++) Mem.StoreI64(s_opStats + 8 * i, 0); }
            long n = Mem.LoadI64(s_opStats + 8 * k) + 1;
            Mem.StoreI64(s_opStats + 8 * k, n);
            if (k == 0 && (n & ((1 << 18) - 1)) == 0) DsPrint();
        }
        static void DsPrint()
        {
            if (s_dsBuf == 0) s_dsBuf = Alloc(4096);
            int at = 0;
            DsNum(ref at, "drop checks ", s_dsCalls); DsNum(ref at, " walked ", s_dsWalked); DsNum(ref at, " dead ", s_dsDead);
            DsNum(ref at, " fail-inside ", s_dsFailInside); DsNum(ref at, " fail-held ", s_dsFailHeld); DsNum(ref at, " max ", s_dsMax);
            DsNum(ref at, " from-release ", s_dsFromRelease); DsNum(ref at, " from-unlink ", s_dsFromUnlink);
            DsNum(ref at, " tops ", s_dsTops); DsNum(ref at, " top-hops ", s_dsTopHops); DsNum(ref at, " top-marked ", s_dsTopMarked); DsNum(ref at, " top-far ", s_dsTopFar);
            Mem.StoreU16(s_dsBuf + 2 * at, 10); at++;
            if (s_opStats != 0)
            {
                // (no string is made here: this runs in the middle of an arena operation)
                int k = 0;
                Mem.StoreU16(s_dsBuf + 2 * at, ' '); at++;
                for (int i = 0; i <= OpNames.Length; i++)
                    if (i == OpNames.Length || OpNames[i] == ',')
                    {
                        DsNum(ref at, " ", Mem.LoadI64(s_opStats + 8 * k));
                        k++;
                        Mem.StoreU16(s_dsBuf + 2 * at, k % 8 == 0 ? 10 : ' '); at++;
                    }
                    else { Mem.StoreU16(s_dsBuf + 2 * at, OpNames[i]); at++; }
                Mem.StoreU16(s_dsBuf + 2 * at, 10); at++;
            }
            Engine.Write(s_dsBuf, at);
        }
        /// <summary>No other thread can ever run: the program starts none (known when the whole program is compiled).</summary>
        [Intrinsic] public static extern bool SingleThreaded();
        /// <summary>Whether the program can add a handler to AppDomain.FirstChanceException (a constant at the end of
        /// the build: Compiler/RuntimeGen.cs).</summary>
        [Intrinsic] public static extern bool FirstChanceWanted();
        /// <summary>RaiseFirstChance, when the program can add a handler (its body is written at the end).</summary>
        [Intrinsic] public static extern void FirstChance(object ex);
        /// <summary>Whether the program asks frames for their methods (a constant at the end of the build).</summary>
        [Intrinsic] public static extern bool FramesWanted();
        /// <summary>The hidden method a throw noted (Compiler/Filters.cs: ThrowSite), 0 for none; clears the note.</summary>
        [Intrinsic] public static extern int TakeThrowSite();
        /// <summary>The hidden throwers' record entries, by note - 1 (Compiler/Reflection.cs).</summary>
        [Intrinsic] public static extern int ThrowSites();

        /// <summary>A throw or rethrow, before any filter or catch runs: AppDomain.FirstChanceException's handlers
        /// (compiled only for a program that reaches AppDomain.add_FirstChanceException).</summary>
        public static void RaiseFirstChance(object ex)
        {
            if (ex is Exception e) AppContext.OnFirstChanceException(e, AppDomain.CurrentDomain);
        }
        /// <summary>Threads: the lock of a place that holds a reference (the one a read that counts and a store take),
        /// for an exchange written by hand (Interlocked); 0 and nothing without threads.</summary>
        [Intrinsic] public static extern int RefLock(ref int place);
        [Intrinsic] public static extern void RefUnlock(int held);
        /// <summary>Threads: between the two no reference anywhere is stored over (every place's lock is held), so a
        /// range of them can be read and counted as it is. Nothing of the program may run in between.</summary>
        [Intrinsic] public static extern void BulkEnter();
        [Intrinsic] public static extern void BulkExit();

        /// <summary>Threads: counts the object at the address if it is still alive (a weak reference's target: its
        /// last release may be under way on another thread). Called with the runtime lock held, which the clearing
        /// of weak references takes.</summary>
        public static bool TryRetain(int a)
        {
            ref int w = ref Mem.RefI32(a);
            while (true)
            {
                int v = Atomic.Load32(ref w);
                if (v == -1) return true;
                // (an arena's member: its arena frees it under the lock held here, clearing the weak reference with it)
                if (v < -1) { Retain(Borrow<object>(a)); return true; }
                if (v == 0) return false;
                if (Atomic.CompareExchange32(ref w, v, v + 1) == v) return true;
            }
        }
        /// <summary>Threads: until ReleaseEpoch the thread passes no safe point, whatever it waits for (Limbo.Hold), so
        /// the code around keeps what it borrowed; nothing without threads.</summary>
        [Intrinsic] public static extern void HoldEpoch();
        [Intrinsic] public static extern void ReleaseEpoch();
        /// <summary>Releases the current thread's thread-static references (its end).</summary>
        [Intrinsic] public static extern void ReleaseThreadStatics();
        /// <summary>A delegate of the type (by id) that passes its arguments to the handler as an object[]; null when the
        /// program names no such delegate type (Compiler/DynamicDelegates.cs).</summary>
        [Intrinsic] public static extern System.Delegate? ObjectArrayDelegate(int delegateTypeId, System.Func<object?[], object?> handler);
        /// <summary>Invokes the delegate with the arguments unboxed from the array, the result boxed (DynamicInvoke).</summary>
        [Intrinsic] public static extern object? DynamicInvokeRaw(System.Delegate d, object?[]? args);
        /// <summary>The delegate type's parameter count; -1 when the program has no DynamicInvoke for it.</summary>
        [Intrinsic] public static extern int DelegateArity(System.Delegate d);
        /// <summary>The reflection record entry of the method a method-group delegate's function calls; 0 if none.</summary>
        [Intrinsic] public static extern int DelegateMethodEntry(int fn);
        /// <summary>The table of stack frame names and their methods' reflection record entries ([count, (name, entry)...]:
        /// a constructor's entry + 1), kept when the program asks a frame for its method (Compiler/Reflection.cs).</summary>
        [Intrinsic] public static extern int FrameMethods();
        /// <summary>Takes a count of every reference an object holds (its fields, its box's value, its elements): after
        /// its bytes were copied into a new object (MemberwiseClone).</summary>
        [Intrinsic] public static extern void RetainContents(object o);
        /// <summary>The module anonymously hosted DynamicMethods report (manifest substitution: they run on the IL
        /// interpreter, without a dynamic assembly).</summary>
        public static System.Reflection.Module DynamicMethodsModule() => System.Reflection.RuntimeAssembly.OfIndex(0).ManifestModule;
        [Intrinsic] public static extern int TlsBase();
        [Intrinsic] public static extern int TlsSize();
        [Intrinsic] public static extern void RuntimeLock();
        [Intrinsic] public static extern void RuntimeUnlock();
        /// <summary>The runtime lock is this thread's (threads; false without).</summary>
        [Intrinsic] public static extern bool RuntimeLockHeld();
        /// <summary>The program is compiled for threads (Driver.ThreadMode): known when each method is compiled, so a
        /// branch on it is taken out before what it calls is compiled (Folding.KnownBool). The group locks' code
        /// (GroupLocks.cs) is behind it: a program without threads compiles none of it.</summary>
        [Intrinsic] public static extern bool ThreadMode();
        /// <summary>CS2WASM_DEADLOCK_DUMP=1 at compile time: a long wait for a lock throws, naming its holder.</summary>
        [Intrinsic] static extern bool DeadlockDump();

        // The heap's lock (threads): the allocator's shared lists and pages, apart from the runtime lock, which code
        // that holds a group's lock may not take (GroupLocks.cs: the runtime lock comes before any group's). Taken
        // last of all locks: nothing is counted or destroyed while it is held. Reentrant (an allocation that fails
        // makes its exception under it).
        static int s_heapOwner, s_heapDepth, s_heapWaiters;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void HeapLock() { if (!ThreadMode()) RuntimeLock(); else HeapLockMt(); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void HeapUnlock() { if (!ThreadMode()) RuntimeUnlock(); else HeapUnlockMt(); }

        static void HeapLockMt()
        {
            int me = ThreadId();
            if (Atomic.Load32(ref s_heapOwner) == me) { s_heapDepth++; return; }
            int n = 0;
            while (true)
            {
                int cur = Atomic.Load32(ref s_heapOwner);
                if (cur == 0) { if (Atomic.CompareExchange32(ref s_heapOwner, 0, me) == 0) break; continue; }
                if (++n < 200) continue;
                if (Threads.MayBlock())
                {
                    Atomic.Add32(ref s_heapWaiters, 1);
                    Atomic.Wait32(ref s_heapOwner, cur, 1000000);
                    Atomic.Add32(ref s_heapWaiters, -1);
                }
            }
            s_heapDepth = 1;
        }

        static void HeapUnlockMt()
        {
            if (--s_heapDepth != 0) return;
            Atomic.Store32(ref s_heapOwner, 0);
            if (Atomic.Load32(ref s_heapWaiters) != 0) Atomic.Notify(ref s_heapOwner, 1);
        }

        /// <summary>Bytes from the heap's start to its top (freed blocks included).</summary>
        public static long HeapUsed() => s_top == 0 ? 0 : (long)(uint)s_top - HeapBase();

        // ------------------------------------------------------------------
        // With threads, a read that counts takes no lock (rt.load_retain): it reads a reference, counts the object if
        // its count word says it is alive, and looks at the place again. The reference may be out of date by then, so
        // the word it counts at must always be a count word or zero, whatever became of the memory:
        // - a block keeps its kind for good: a plain object's (count word first), or a cycle-capable object's (the
        //   arena's words first, the count word after them); each kind has its own lists, and a block is never joined
        //   to its neighbours;
        // - memory that is no object (Alloc: the runtime's records, a thread's stack, a program's native memory) has
        //   a word before it that stays zero, and so can share the plain objects' lists;
        // - a free block's first word is zero (and its count word, for the second kind), its link is at 8.
        // Without threads the kinds are not told apart (nothing reads without counting first).
        // ------------------------------------------------------------------
        const int KindPlain = 0, KindArena = 1;

        // (memory that is no object stays at a multiple of 8: a thread's stack, words read atomically; Free rounds alike)
        public static int Alloc(int size) => AllocBlock((size + 15) & ~7, KindPlain) + 8;

        /// <summary>Alloc for the first thread's own block of thread statics: before there is one, nothing may read a
        /// thread static, the thread's lists of free blocks among them.</summary>
        public static int AllocFirst(int size)
        {
            if (SingleThreaded()) return AllocBlock((size + 15) & ~7, KindPlain) + 8;
            HeapLock();
            int a = AllocCore((size + 15) & ~7, KindPlain) + 8;
            HeapUnlock();
            return a;
        }

        static int AllocBlock(int size, int kind)
        {
            // threads: small blocks come from the thread's own free lists, without the runtime lock
            if (!SingleThreaded() && size <= MaxSmall) return AllocLocal(size, kind);
            HeapLock();
            int a = AllocCore(size, kind);
            HeapUnlock();
            return a;
        }

        // ------------------------------------------------------------------
        // Per-thread caches of small blocks (programs with threads): a thread allocates from and frees into its own
        // free lists, one per kind and size class, and takes from or gives back to the shared ones in batches under
        // the runtime lock: when a list is empty, when the thread has freed more than CacheLimit bytes into them, and
        // when the thread ends. A shared list is a stack of such batches, each the list a thread gave back whole
        // (linked at 8; its first block's word 12 is the next batch), so a batch goes either way in a few stores:
        // neither side walks a list (a thread that freed a tree of a hundred thousand nodes and builds another
        // walked them twice, in the cache misses of a list that runs all over memory).
        // A thread's own pages (ThreadPage), as the pages of a program without threads: a thread cuts its small blocks
        // from pages that are its own, one per kind and size at a time, and a block it frees that is in one of its
        // pages goes back onto that page's list, so blocks made one after the other lie next to each other however
        // they were freed before (a list of freed blocks for all of a thread's memory hands them out in the order
        // they were freed: a list freed in its group's order and made again was walked at a cache miss a node). A
        // block another thread frees goes the way above, through the freeing thread's list and the shared ones.
        // A thread's page: [0] its freed blocks (linked at 8), [4] next of the thread's pages of the kind and size
        // with freed blocks, [8] the thread that owns it, plus one (0: none, the thread ended), [12] the first block
        // never cut (0: none), [16] kind + 2 * its blocks that are neither on its list nor uncut (wherever they are:
        // a block another thread freed stays counted until it comes back to the page), [20] size class * 2 + (1:
        // current or on that list). Only its owner touches [0], [4], [12], [16] and the flag. A page whose blocks
        // are all back is cut again from its start, in order, as an empty page is without threads.
        // The thread's table (t_heads): per kind and size, its list of blocks (above), its current page, its pages
        // with freed blocks.
        // ------------------------------------------------------------------
        const int CacheLimit = 256 * 1024;
        const int CurTable = 2 * Classes * 4, AvailTable = 4 * Classes * 4, ThreadTable = 6 * Classes * 4;
        [System.ThreadStatic] static int t_heads, t_cached;

        static int AllocLocal(int size, int kind)
        {
            size = RoundSmall(size);
            if (size < 16) size = 16;
            int heads = t_heads;
            if (heads == 0) heads = InitThreadCache();
            int cls = (kind * Classes + (size >> 2)) * 4;
            int b = PageTake(heads + cls, size, false);
            if (b != 0) { Zero(b, size); return b; }
            int slot = heads + cls;
            b = Mem.LoadI32(slot);
            if (b == 0)
            {
                // (the shared lists' blocks before a new page: what other threads gave back is used again)
                Refill(slot, size, kind);
                b = Mem.LoadI32(slot);
                if (b == 0)
                {
                    b = PageTake(heads + cls, size, true);
                    Zero(b, size);
                    return b;
                }
            }
            Mem.StoreI32(slot, Mem.LoadI32(b + 8));
            Zero(b, size);
            return b;
        }

        /// <summary>A block off the thread's current page of the kind and size (`row`: its entry in the table), else
        /// off the next of its pages with freed blocks; 0 if there is none (`fresh`: the current page is new).</summary>
        static int PageTake(int row, int size, bool fresh)
        {
            int heads = t_heads, cls = row - heads;
            while (true)
            {
                int pg = Mem.LoadI32(heads + CurTable + cls);
                if (pg != 0)
                {
                    int b = Mem.LoadI32(pg);
                    if (b != 0) { Mem.StoreI32(pg, Mem.LoadI32(b + 8)); Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 2); return b; }
                    int bump = Mem.LoadI32(pg + 12);
                    if (bump != 0)
                    {
                        int nb = bump + size;
                        Mem.StoreI32(pg + 12, nb + size <= pg + PageSize ? nb : 0);
                        Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 2);
                        return bump;
                    }
                    // (full: off the lists until a block of it is freed)
                    Mem.StoreI32(pg + 20, Mem.LoadI32(pg + 20) & ~1);
                    Mem.StoreI32(heads + CurTable + cls, 0);
                }
                int next = Mem.LoadI32(heads + AvailTable + cls);
                if (next == 0) return 0;
                Mem.StoreI32(heads + AvailTable + cls, Mem.LoadI32(next + 4));
                Mem.StoreI32(heads + CurTable + cls, next);
            }
        }

        static int InitThreadCache()
        {
            HeapLock();
            int h = AllocCore(ThreadTable + 8, KindPlain) + 8;
            HeapUnlock();
            Mem.Fill(h, 0, ThreadTable);
            t_heads = h;
            return h;
        }

        /// <summary>The thread's own page a small block is in, or 0 (a page another thread owns, or none).</summary>
        static int OwnPage(int addr)
        {
            int e = MapGet(addr);
            if ((e & 7) != 4) return 0;
            int pg = e & ~7;
            return Mem.LoadI32(pg + 8) == ThreadId() + 1 ? pg : 0;
        }

        /// <summary>A block back onto its page, the thread's own; the page onto the thread's pages with freed blocks
        /// when it was full.</summary>
        static void PagePut(int pg, int addr)
        {
            Mem.StoreI32(addr, 0);
            int used = Mem.LoadI32(pg + 16) - 2;
            Mem.StoreI32(pg + 16, used);
            // (all its blocks back: cut again from the start, in order)
            if (used < 2) { Mem.StoreI32(pg, 0); Mem.StoreI32(pg + 12, pg + PageHead); }
            else { Mem.StoreI32(addr + 8, Mem.LoadI32(pg)); Mem.StoreI32(pg, addr); }
            int fl = Mem.LoadI32(pg + 20);
            if ((fl & 1) == 0)
            {
                Mem.StoreI32(pg + 20, fl | 1);
                int row = t_heads + AvailTable + ((used & 1) * Classes + (fl >> 1)) * 4;
                Mem.StoreI32(pg + 4, Mem.LoadI32(row));
                Mem.StoreI32(row, pg);
            }
        }

        /// <summary>Fills an empty list of the thread's with the shared list's top batch of the kind and size, if there
        /// is one; else a new page of the thread's own, its current one (AllocLocal takes from it).</summary>
        static void Refill(int slot, int size, int kind)
        {
            HeapLock();
            if (s_top == 0) InitHeap();
            int shared = Lists(kind) + (size >> 2) * 4;
            int list = Mem.LoadI32(shared);
            if (list != 0) { Mem.StoreI32(shared, Mem.LoadI32(list + 12)); Mem.StoreI32(slot, list); }
            else Mem.StoreI32(slot + CurTable, NewOwnedPage(size, kind));
            HeapUnlock();
        }

        static void FreeLocal(int addr, int size, int kind)
        {
            size = RoundSmall(size);
            if (size < 16) size = 16;
            int heads = t_heads;
            if (heads == 0) heads = InitThreadCache();
            int own = OwnPage(addr);
            if (own != 0) { PagePut(own, addr); return; }
            int slot = heads + (kind * Classes + (size >> 2)) * 4;
            // the link is at 8: word 0 (a plain object's count) stays 0, dead, for a reference read just before the free
            Mem.StoreI32(addr, 0);
            Mem.StoreI32(addr + 8, Mem.LoadI32(slot));
            Mem.StoreI32(slot, addr);
            // (counted by what is freed only: an allocation writes nothing but its list)
            int cached = t_cached + size;
            t_cached = cached;
            if (cached > CacheLimit) FlushThreadCache();
        }

        /// <summary>The thread ends: its pages become no thread's, their freed blocks and those never cut go to the
        /// shared lists, with the blocks of its list.</summary>
        internal static void EndThreadCache()
        {
            int heads = t_heads;
            if (heads == 0) return;
            for (int kind = 0; kind < 2; kind++)
                for (int c = 4; c < Classes; c++)
                {
                    int cls = (kind * Classes + c) * 4, size = c << 2;
                    int pg = Mem.LoadI32(heads + CurTable + cls);
                    Mem.StoreI32(heads + CurTable + cls, 0);
                    while (pg != 0)
                    {
                        int list = Mem.LoadI32(heads + cls);
                        for (int b = Mem.LoadI32(pg); b != 0;) { int next = Mem.LoadI32(b + 8); Mem.StoreI32(b + 8, list); list = b; b = next; }
                        int bump = Mem.LoadI32(pg + 12);
                        if (bump != 0) for (int b = bump; b + size <= pg + PageSize; b += size) { Mem.StoreI32(b, 0); Mem.StoreI32(b + 8, list); list = b; }
                        Mem.StoreI32(heads + cls, list);
                        Mem.StoreI32(pg, 0); Mem.StoreI32(pg + 12, 0);
                        Mem.StoreI32(pg + 20, Mem.LoadI32(pg + 20) & ~1);
                        Atomic.Store32(ref Mem.RefI32(pg + 8), 0);
                        pg = Mem.LoadI32(heads + AvailTable + cls);
                        if (pg != 0) Mem.StoreI32(heads + AvailTable + cls, Mem.LoadI32(pg + 4));
                    }
                }
            FlushThreadCache();
        }

        /// <summary>Gives the thread's cached blocks back to the shared lists (it has freed much, or it ends).</summary>
        internal static void FlushThreadCache()
        {
            int heads = t_heads;
            if (heads == 0) return;
            HeapLock();
            for (int kind = 0; kind < 2; kind++)
                for (int c = 4; c < Classes; c++)
                {
                    int slot = heads + (kind * Classes + c) * 4, b = Mem.LoadI32(slot);
                    if (b == 0) continue;
                    // (a batch of its own on the shared stack)
                    int shared = Lists(kind) + c * 4;
                    Mem.StoreI32(b + 12, Mem.LoadI32(shared));
                    Mem.StoreI32(shared, b);
                    Mem.StoreI32(slot, 0);
                }
            HeapUnlock();
            t_cached = 0;
        }

        /// <summary>Grows the memory by at least need bytes: by an eighth of its size, 16 MiB at least, when the maximum
        /// allows (growing a shared memory is slow; growing it by a page at a time, for every refill, slower still).</summary>
        static void GrowFor(int need)
        {
            int pages = (need + 65535) / 65536;
            int more = Math.Max(pages, Math.Max(Mem.Size() / 8, 256));
            if (Mem.Grow(more) < 0 && Mem.Grow(pages) < 0) ThrowHelpers.OutOfMemory();
            // (with room again: the exception a later failure throws is made now, while it can be)
            s_end = Mem.Size() * 65536;
            MemStatsGrow();
            ThrowHelpers.ReserveOutOfMemory();
        }

        // ------------------------------------------------------------------
        // CS2WASM_MEMSTATS=1 (a diagnosis, at compile time): where the heap's memory is. At every growth of the memory
        // the heap is gone through (the table's words, each page's blocks) and a line says what it holds; the export
        // cs2wasm_memstats (the host calls it at the end when it is there) prints the last growth's state in full,
        // by size class and by type, and then the state at the end. Its records are made in the heap's start, before
        // the first block (InitHeap), so the report allocates nothing while it looks.
        //   per class (MsCls words): pages, blocks in use, of them objects, of them cycle-capable objects, of those with
        //   member words, empty pages of the class (none in use);
        //   totals (MsTot): below; types: an open table of (type id + 1, bytes, blocks).
        // ------------------------------------------------------------------
        [Intrinsic] static extern bool MemStatsOn();
        /// <summary>CS2WASM_MEMSTATS=1: a call of MsGrow; else nothing (not even the call's code: a branch on a
        /// constant still made the module bigger).</summary>
        [Intrinsic] static extern void MemStatsGrow();
        /// <summary>CS2WASM_MEMSTATS=1: a call of MsRecord with the argument; else nothing.</summary>
        [Intrinsic] static extern void MemStatsRecord(int delta);
        public static void MsGrow() { MsWalk(); MsLine("grow"); }
        public static void MsRecord(int delta) => s_msRec += delta;
        const int MsCls = 6, MsTypes = 16384;
        const int TPages = 0, TEmptyPages = 1, THuge = 2, THugeGran = 3, TFreeRuns = 4, TFreeGran = 5, TFreeMax = 6,
            TOther = 7, TOddBlocks = 8, TOddBytes = 9, TMemberWords = 10, TArenaRecords = 11, TFreePageList = 12, TRunLeft = 13,
            THugeObj = 14, THugeArena = 15, TLeafGran = 16, TMwLive = 17, TBadTags = 18, TWords = 24, TCount = 40;
        static int s_ms, s_msRec, s_msGrows;
        static int MsClsAt(int c) => s_ms + c * MsCls * 4;
        static int MsTotAt(int k) => s_ms + Classes * MsCls * 4 + k * 4;
        static int MsTypeAt(int i) => s_ms + Classes * MsCls * 4 + TCount * 4 + i * 12;
        static int MsBuf => MsTypeAt(MsTypes);
        const int MsArea = Classes * MsCls * 4 + TCount * 4 + MsTypes * 12 + 2048;
        static int s_msAt;

        static void MsAdd(int addr, int v) => Mem.StoreI32(addr, Mem.LoadI32(addr) + v);

        static void MsType(int typeId, int bytes)
        {
            int i = (int)((uint)(typeId * -1640531535) >> 18) & (MsTypes - 1);
            for (int n = 0; n < MsTypes; n++, i = (i + 1) & (MsTypes - 1))
            {
                int e = MsTypeAt(i), k = Mem.LoadI32(e);
                if (k == 0) { Mem.StoreI32(e, typeId + 1); k = typeId + 1; }
                if (k == typeId + 1) { MsAdd(e + 4, bytes); MsAdd(e + 8, 1); return; }
            }
        }

        /// <summary>An object at a block's start (its first word: a count, or the mark before a cycle-capable one's
        /// words), tallied by type; whether it is one.</summary>
        static int MsObject(int b, int bytes)
        {
            int w = Mem.LoadI32(b);
            if (w == 0) return 0;
            int o = w <= PrefixMark ? b + ArenaPrefix : b;
            MsType(Mem.LoadI32(o + 4), bytes);
            if (w > PrefixMark) return 1;
            // (a first word below -1 that is no tag: its side words would be outside the heap; counted, not read)
            int side = PrefixMark - w;
            if (side != 0 && ((side & 3) != 0 || (uint)side < (uint)s_runLo || (uint)side >= (uint)s_top)) { MsAdd(MsTotAt(TBadTags), 1); return 1; }
            if (Mem.LoadI32(o - 16) == 0) return 3;
            MsAdd(MsTotAt(TMemberWords), 1);
            // (which of the nine words are in use)
            for (int k = 4; k <= 28; k += 4) if (Mem.LoadI32(o - k) != 0) MsAdd(MsTotAt(TWords + k / 4), 1);
            if (PLW(o) != 0) MsAdd(MsTotAt(TWords + 8), 1);
            if (PLM(o) != 0) MsAdd(MsTotAt(TWords + 9), 1);
            return 7;
        }

        static void MsWalk()
        {
            if (s_ms == 0) return;
            Mem.Fill(s_ms, 0, MsArea - 2048);
            int top = s_top;
            // (the blocks of each size's run that were never handed out: counted in use by their page)
            for (int c = 4; c < Classes; c++)
            {
                int next = Mem.LoadI32(s_hot + (Classes + c) * 4), end = Mem.LoadI32(s_hot + (2 * Classes + c) * 4);
                if (next != end) { MsAdd(MsClsAt(c) + 4, -(end - next) / (c << 2)); MsAdd(MsTotAt(TRunLeft), end - next); }
            }
            // (a page on the empty list keeps what its memory held before, header too, until it is taken: PageAlloc
            // clears it then; its count of blocks in use is set to -1 here, which no page in use has, to tell it)
            for (int p = s_freePages; p != 0; p = Mem.LoadI32(p + 4)) { MsAdd(MsTotAt(TFreePageList), 1); if (SingleThreaded()) Mem.StoreI32(p + 16, -1); }
            for (int a = s_runLo; (uint)a < (uint)top;)
            {
                int e = MapGet(a), k = e & 7;
                if (k == 1 || k == 4)
                {
                    int pg = e & ~7;
                    if (pg != a) { a += Granule; continue; }
                    int fl = Mem.LoadI32(pg + 20), c = fl >> 1, size = c << 2;
                    int used = k == 1 ? Mem.LoadI32(pg + 16) : Mem.LoadI32(pg + 16) >> 1;
                    int row = MsClsAt(c);
                    MsAdd(MsTotAt(TPages), 1);
                    if (size == 0 || (k == 1 && used == -1) || (uint)c >= Classes) { MsAdd(MsTotAt(TEmptyPages), 1); a += PageSize; continue; }
                    MsAdd(row, 1);
                    MsAdd(row + 4, used);
                    if (used == 0) { MsAdd(row + 20, 1); MsAdd(MsTotAt(TEmptyPages), 1); }
                    int bump = Mem.LoadI32(pg + 12), last = bump != 0 ? bump : pg + PageSize;
                    for (int b = pg + PageHead; b + size <= last; b += size)
                    {
                        int r = MsObject(b, size);
                        if (r != 0) MsAdd(row + 8, 1);
                        if ((r & 2) != 0) MsAdd(row + 12, 1);
                        if ((r & 4) != 0) MsAdd(row + 16, 1);
                    }
                    a += PageSize;
                }
                else if (k == 2)
                {
                    int n = 0;
                    while ((uint)(a + (n << GShift)) < (uint)top && MapGet(a + (n << GShift)) == e) n++;
                    MsAdd(MsTotAt(THuge), 1); MsAdd(MsTotAt(THugeGran), n);
                    int r = MsObject(a, n << GShift);
                    if (r != 0) MsAdd(MsTotAt(THugeObj), n);
                    if ((r & 2) != 0) MsAdd(MsTotAt(THugeArena), n);
                    a += n << GShift;
                }
                else if (k == 3)
                {
                    int n = (int)((uint)e >> 3);
                    MsAdd(MsTotAt(TFreeRuns), 1); MsAdd(MsTotAt(TFreeGran), n);
                    if (n > Mem.LoadI32(MsTotAt(TFreeMax))) Mem.StoreI32(MsTotAt(TFreeMax), n);
                    a += n << GShift;
                }
                else { MsAdd(MsTotAt(TOther), 1); a += Granule; }
            }
            // (small blocks in no page, on the old lists)
            for (int kind = 0; kind < 2; kind++)
                for (int c = 4; c < Classes; c++)
                    for (int b = Mem.LoadI32(Lists(kind) + c * 4); b != 0; b = Mem.LoadI32(b + FreeLink))
                        if (SingleThreaded()) { MsAdd(MsTotAt(TOddBlocks), 1); MsAdd(MsTotAt(TOddBytes), c << 3); }
                        else break;
            Mem.StoreI32(MsTotAt(TArenaRecords), s_msRec);
        }

        static void MsText(string s)
        {
            for (int i = 0; i < s.Length && s_msAt < 1000; i++) { Mem.StoreU16(MsBuf + 2 * s_msAt, s[i]); s_msAt++; }
        }

        static void MsNum(long v)
        {
            if (v < 0) { Mem.StoreU16(MsBuf + 2 * s_msAt, '-'); s_msAt++; v = -v; }
            int start = s_msAt;
            do { Mem.StoreU16(MsBuf + 2 * s_msAt, '0' + (int)(v % 10)); s_msAt++; v /= 10; } while (v > 0);
            for (int i = start, j = s_msAt - 1; i < j; i++, j--) { int t = Mem.LoadU16(MsBuf + 2 * i); Mem.StoreU16(MsBuf + 2 * i, Mem.LoadU16(MsBuf + 2 * j)); Mem.StoreU16(MsBuf + 2 * j, t); }
        }

        static void MsKb(string label, long bytes) { MsText(label); MsNum((bytes + 512) >> 10); MsText(" KB"); }

        static void MsEnd() { Mem.StoreU16(MsBuf + 2 * s_msAt, 10); Engine.Write(MsBuf, s_msAt + 1); s_msAt = 0; }

        static long MsTot(int k) => Mem.LoadI32(MsTotAt(k));

        /// <summary>The totals of the last walk, one line.</summary>
        static void MsLine(string what)
        {
            long live = 0, held = 0, objects = 0;
            for (int c = 4; c < Classes; c++)
            {
                int row = MsClsAt(c), pages = Mem.LoadI32(row);
                if (pages == 0) continue;
                int size = c << 2, cap = (PageSize - PageHead) / size;
                live += (long)Mem.LoadI32(row + 4) * size;
                objects += (long)Mem.LoadI32(row + 8) * size;
                held += (long)pages * PageSize - (long)Mem.LoadI32(row + 4) * size;
            }
            s_msGrows++;
            MsText("memstats "); MsText(what); MsKb(" memory ", (long)Mem.Size() * 65536); MsKb(" top ", (long)(uint)s_top);
            MsKb(" base ", HeapBase()); MsKb(" small-live ", live); MsKb(" (objects ", objects); MsText(")");
            MsKb(" small-held-free ", held); MsKb(" empty-pages ", MsTot(TEmptyPages) * PageSize);
            MsKb(" huge ", MsTot(THugeGran) << GShift); MsKb(" free-runs ", MsTot(TFreeGran) << GShift);
            MsText(" members "); MsNum(MsTot(TMemberWords)); MsText(" bad-tags "); MsNum(MsTot(TBadTags));
            MsEnd();
        }

        static void MsFull(string what)
        {
            MsLine(what);
            MsText("memstats "); MsText(what); MsText(": pages "); MsNum(MsTot(TPages)); MsText(" (empty "); MsNum(MsTot(TEmptyPages));
            MsText(", on the empty list "); MsNum(MsTot(TFreePageList)); MsText(")"); MsKb(" run-left ", MsTot(TRunLeft));
            MsText(" huge blocks "); MsNum(MsTot(THuge)); MsKb(" ", MsTot(THugeGran) << GShift); MsKb(" (objects ", MsTot(THugeObj) << GShift);
            MsKb(", cycle-capable ", MsTot(THugeArena) << GShift); MsText(")");
            MsText(" free runs "); MsNum(MsTot(TFreeRuns)); MsKb(" largest ", MsTot(TFreeMax) << GShift);
            MsText(" other granules "); MsNum(MsTot(TOther)); MsText(" odd blocks "); MsNum(MsTot(TOddBlocks)); MsKb(" ", MsTot(TOddBytes));
            MsText(" bad tags "); MsNum(MsTot(TBadTags)); MsText(" member words "); MsNum(MsTot(TMemberWords)); MsText(" arena records "); MsNum(MsTot(TArenaRecords));
            MsEnd();
            MsText("memstats "); MsText(what); MsText(" members with word k (and side words) not zero:");
            for (int k = 4; k <= 28; k += 4) { MsText(" "); MsNum(k); MsText(":"); MsNum(MsTot(TWords + k / 4)); }
            MsText(" waiting:"); MsNum(MsTot(TWords + 8)); MsText(" note:"); MsNum(MsTot(TWords + 9));
            MsEnd();
            MsText("memstats "); MsText(what); MsText(" class: size pages in-use objects cycle-capable with-words empty | live KB, held free KB"); MsEnd();
            for (int c = 4; c < Classes; c++)
            {
                int row = MsClsAt(c), pages = Mem.LoadI32(row);
                if (pages == 0) continue;
                int size = c << 2;
                MsText("memstats "); MsText(what); MsText(" class "); MsNum(size);
                for (int i = 0; i < MsCls; i++) { MsText(" "); MsNum(Mem.LoadI32(row + 4 * i)); }
                MsKb(" | ", (long)Mem.LoadI32(row + 4) * size); MsKb(" ", (long)pages * PageSize - (long)Mem.LoadI32(row + 4) * size);
                MsEnd();
            }
            // the types with the most bytes
            for (int n = 0; n < 60; n++)
            {
                int best = -1; long bb = 0;
                for (int i = 0; i < MsTypes; i++)
                {
                    int e = MsTypeAt(i);
                    if (Mem.LoadI32(e) > 0 && (uint)Mem.LoadI32(e + 4) > bb) { bb = (uint)Mem.LoadI32(e + 4); best = e; }
                }
                if (best < 0) break;
                int id = Mem.LoadI32(best) - 1;
                MsText("memstats "); MsText(what); MsText(" type "); MsKb("", bb); MsText(" "); MsNum(Mem.LoadI32(best + 8)); MsText(" ");
                MsText(TypeInfo.FullName(id) ?? "?");
                MsEnd();
                Mem.StoreI32(best, -Mem.LoadI32(best));
            }
            for (int i = 0; i < MsTypes; i++) { int e = MsTypeAt(i); if (Mem.LoadI32(e) < 0) Mem.StoreI32(e, -Mem.LoadI32(e)); }
        }

        /// <summary>The export cs2wasm_memstats: the last growth's state (still in the records), then the state now.</summary>
        public static void MemStatsReport()
        {
            if (!MemStatsOn() || s_ms == 0) return;
            if (s_msGrows > 0) MsFull("at-last-growth");
            MsWalk();
            MsFull("now");
        }

        // Blocks above MaxSmall: their sizes are rounded up to one of eight steps per power of two (an eighth more at
        // most), and each such size has its own list of freed blocks, so allocating and freeing one is a push or a pop
        // whatever is free. A free one: [0] zero, [8] next.
        const int LargeBins = 20 * 8;

        /// <summary>The lists of a kind: its small blocks by size (a word each, Classes of them), then its large ones.</summary>
        static int Lists(int kind) => kind == 0 ? s_table : s_table + (Classes + LargeBins) * 4;

        /// <summary>The list of a large size that is one of the steps (or the last list a smaller size fits: the floor).</summary>
        static int LargeBin(int size)
        {
            int k = 31 - I32Clz(size);
            return (k - 11) * 8 + ((size >> (k - 3)) & 7);
        }

        /// <summary>A large size rounded up to a step.</summary>
        static int RoundLarge(int size)
        {
            int step = 1 << (28 - I32Clz(size));
            return (size + step - 1) & ~(step - 1);
        }

        static int AllocCore(int size, int kind)
        {
            if (s_top == 0) InitHeap();
            size = RoundSmall(size);
            if (size < MinBlock) size = MinBlock;
            // (small blocks: from pages without threads; with them, from the lists by size the threads' caches refill from)
            if (size <= MaxSmall) return SingleThreaded() ? PageAlloc(size) : AllocSmallShared(size, kind);
            // A program with threads keeps a freed large block for a block of its kind and size (a block keeps its
            // kind for good, below), so its sizes are steps; without threads a freed run is any run again.
            if (!SingleThreaded() && size < 0x40000000)
            {
                size = RoundLarge(size);
                int slot = Lists(kind) + Classes * 4 + LargeBin(size) * 4;
                int head = Mem.LoadI32(slot);
                if (head != 0)
                {
                    Mem.StoreI32(slot, Mem.LoadI32(head + FreeLink));
                    Mem.Fill(head, 0, size);
                    return head;
                }
            }
            return AllocHuge(size);
        }

        /// <summary>A small block in a program with threads: off its kind's list of the size, else off a new page of them.</summary>
        static int AllocSmallShared(int size, int kind)
        {
            int slot = Lists(kind) + (size >> 2) * 4;
            int head = Mem.LoadI32(slot), below = 0;
            if (head == 0) head = NewThreadPage(size, kind);
            else below = Mem.LoadI32(head + 12);
            // (the rest of its batch is the top batch now, over the batches below it)
            int next = Mem.LoadI32(head + 8);
            if (next != 0) { Mem.StoreI32(next + 12, below); Mem.StoreI32(slot, next); }
            else Mem.StoreI32(slot, below);
            Zero(head, size);
            return head;
        }

        // The objects whose destruction waits for the outermost one to finish (rt.destroy, past a depth of nesting),
        // in a program with threads: a list of the thread's own (without threads they are linked through their
        // count words, which a program with threads keeps zero in a dead object).
        [System.ThreadStatic] static int t_pendQ, t_pendN, t_pendCap;

        public static void PendDestroy(int obj)
        {
            if (t_pendN == t_pendCap)
            {
                int cap = t_pendCap == 0 ? 64 : t_pendCap * 2;
                int nq = Alloc(cap * 4);
                if (t_pendQ != 0) { Mem.Copy(nq, t_pendQ, t_pendN * 4); Free(t_pendQ, t_pendCap * 4); }
                t_pendQ = nq; t_pendCap = cap;
            }
            Mem.StoreI32(t_pendQ + 4 * t_pendN, obj);
            t_pendN++;
        }

        public static int NextPending()
        {
            if (t_pendN == 0) return 0;
            t_pendN--;
            return Mem.LoadI32(t_pendQ + 4 * t_pendN);
        }

        /// <summary>Frees memory Alloc gave (not an object: FreeObject).</summary>
        public static void Free(int addr, int size)
        {
            FreeBlock(addr - 8, (size + 15) & ~7, KindPlain);
        }

        /// <summary>Frees an object's memory (its destruction: the compiler's destroy functions).</summary>
        public static void FreeObject(int addr, int size)
        {
            // The usual case in one small piece (an engine puts a small function into its callers, a large one it
            // calls): the block is kept aside for the next allocation of its size; the one that was there goes on.
            if (SingleThreaded() && !ShareCheck() && !Checked())
            {
                int s = (size + 3) & ~3;
                if (WideSizes() && s > 128) s = RoundSmall(size);
                if ((uint)s <= MaxSmall)
                {
                    int hotSlot = s_hot + (s >> 2) * 4;
                    int old = Mem.LoadI32(hotSlot);
                    Mem.StoreI32(hotSlot, addr);
                    Mem.StoreI32(addr, 0);
                    if (old != 0) FreeSmallOld(old, s);
                    return;
                }
            }
            else if (!ShareCheck() && !Checked())
            {
                // (threads: back onto its page, the thread's own, as FreeLocal)
                int s = RoundSmall(size), heads = t_heads;
                if ((uint)(s - 16) <= MaxSmall - 16 && heads != 0)
                {
                    int own = OwnPage(addr);
                    if (own != 0) { PagePut(own, addr); return; }
                    int slot = heads + (s >> 2) * 4;
                    Mem.StoreI32(addr, 0);
                    Mem.StoreI32(addr + 8, Mem.LoadI32(slot));
                    Mem.StoreI32(slot, addr);
                    int cached = t_cached + s;
                    t_cached = cached;
                    if (cached > CacheLimit) FlushThreadCache();
                    return;
                }
            }
            FreeBlock(addr, size, KindPlain);
        }

        /// <summary>The block that was kept aside for its size (FreeObject, FreeCore) when another takes its place.</summary>
        static void FreeSmallOld(int addr, int size)
        {
            if (IsPage(addr))
            {
                // (the usual case here, without another call: back onto its page's list; the page stays as it is
                // unless this was its first freed block or its last one in use)
                int pg = addr & ~(PageSize - 1);
                int used = Mem.LoadI32(pg + 16) - 1;
                if (used != 0 && (Mem.LoadI32(pg + 20) & 1) != 0)
                {
                    Mem.StoreI32(addr + 4, Mem.LoadI32(pg));
                    Mem.StoreI32(pg, addr);
                    Mem.StoreI32(pg + 16, used);
                    return;
                }
                PageFree(addr);
                return;
            }
            int slot = Lists(KindPlain) + (size >> 2) * 4;
            Mem.StoreI32(addr, 0);
            Mem.StoreI32(addr + 4, Mem.LoadI32(slot));
            Mem.StoreI32(slot, addr);
        }

        static void FreeBlock(int addr, int size, int kind)
        {
            // CS2WASM_SHARE_CHECK: freed memory is poisoned (0x0EAD, the freeing thread) and never reused
            // (its type word too: a borrowed reference used after the free traps at its next dispatch or type test)
            if (Checked() && !ShareCheck() && size >= 8)
            {
                // (and the table must name this block for its first and its last byte: BlockOf)
                if (BlockOf(addr) != addr || BlockOf(addr + RoundSmall(size) - 1) != addr) Mem.StoreI32(-4, 0);
                // (with threads a dead object's count word stays zero: a read that counts relies on it)
                int whole = RoundSmall(size);
                Mem.StoreI32(addr, SingleThreaded() ? 0x0EAD0000 : 0);
                Mem.StoreI32(addr + 4, 0x7EAD0000);
                if (whole > 8) Mem.Fill(addr + 8, 0xDD, whole - 8);
                return;
            }
            if (ShareCheck() && size >= 8) { Mem.StoreI32(addr, 0x0EAD0000 | (ThreadId() & 0xFFFF)); Mem.StoreI32(addr + 4, 0x7EAD0000); return; }
            if (!SingleThreaded() && size <= MaxSmall) { FreeLocal(addr, size, kind); return; }
            HeapLock();
            FreeCore(addr, size, kind);
            HeapUnlock();
        }

        static void FreeCore(int addr, int size, int kind)
        {
            size = RoundSmall(size);
            if (size < MinBlock) size = MinBlock;
            if (size <= MaxSmall)
            {
                if (SingleThreaded())
                {
                    // (kept aside for the next allocation of the size; the one that was there goes to its page)
                    int hotSlot = s_hot + (size >> 2) * 4;
                    int old = Mem.LoadI32(hotSlot);
                    Mem.StoreI32(hotSlot, addr);
                    Mem.StoreI32(addr, 0);
                    if (old == 0) return;
                    addr = old;
                    if (IsPage(addr)) { PageFree(addr); return; }
                }
                // the link is at 8: word 0 (an object's count) stays 0, dead, for a reference read just before the free
                int slot = Lists(kind) + (size >> 2) * 4;
                Mem.StoreI32(addr, 0);
                Mem.StoreI32(addr + FreeLink, Mem.LoadI32(slot));
                Mem.StoreI32(slot, addr);
                return;
            }
            if (SingleThreaded()) { FreeRun(addr, (int)(((uint)size + (Granule - 1)) >> GShift)); return; }
            // (threads: onto its kind's list of its size's step, as it was allocated; a block of a gigabyte or more
            // is not used again)
            if (size >= 0x40000000) { Mem.StoreI32(addr, 0); return; }
            int bin = Lists(kind) + Classes * 4 + LargeBin(RoundLarge(size)) * 4;
            Mem.StoreI32(addr, 0);
            Mem.StoreI32(addr + 8, Mem.LoadI32(bin));
            Mem.StoreI32(bin, addr);
        }

        /// <summary>New object: count 1, type id, hash code from a per-instance counter in allocation order (section 7.3).</summary>
        [System.ThreadStatic] static int t_hash;

        /// <summary>An object's identity hash: a counter in allocation order (section 7.3). With threads, a counter of
        /// each thread under the thread's id: the order on a thread does not depend on the others, and no thread
        /// waits for a shared counter.</summary>
        static int NextHash()
        {
            if (SingleThreaded()) return ++s_hash;
            return ((ThreadId() & 0xFF) << 22) ^ ++t_hash;
        }

        /// <summary>Clears a block (a multiple of 4 bytes; of one that is not a multiple of 8, all but its first word, which
        /// is a header's or a tag's that its maker writes). A small one word by word: memory.fill is a call into the
        /// engine, slower than the few stores.</summary>
        static void Zero(int addr, int size)
        {
            if (size > 256) { Mem.Fill(addr, 0, size); return; }
            for (int p = addr + size - 8; (uint)p >= (uint)addr; p -= 8) Mem.StoreI64(p, 0);
        }

        public static int AllocObject(int size, int typeId)
        {
            // The usual case in one small piece, without a call (an engine puts a small function into its callers, a
            // large one it calls): the block last freed of the size is taken, cleared and given its header. (An
            // object's identity hash is made when it is first asked for: HashOf.)
            if (SingleThreaded())
            {
                int s = (size + 3) & ~3, hot = s_hot;
                if (WideSizes() && s > 128) s = RoundSmall(size);
                if ((uint)s <= MaxSmall && hot != 0)
                {
                    int hotSlot = hot + (s >> 2) * 4;
                    int head = Mem.LoadI32(hotSlot);
                    if (head != 0)
                    {
                        Mem.StoreI32(hotSlot, 0);
                        for (int p = head + s - 8; (uint)p > (uint)head; p -= 8) Mem.StoreI64(p, 0);
                        Mem.StoreI32(head, 1);
                        Mem.StoreI32(head + 4, typeId);
                        return head;
                    }
                    // (else the next block of the size's run: memory never used, clear already)
                    int run = Mem.LoadI32(hotSlot + Classes * 4);
                    if (run != Mem.LoadI32(hotSlot + 2 * Classes * 4))
                    {
                        Mem.StoreI32(hotSlot + Classes * 4, run + s);
                        Mem.StoreI32(run, 1);
                        Mem.StoreI32(run + 4, typeId);
                        return run;
                    }
                }
            }
            else if (!ShareCheck())
            {
                // (threads: the same piece on the thread's current page of the size, AllocLocal)
                int s = RoundSmall(size), heads = t_heads;
                if ((uint)(s - 16) <= MaxSmall - 16 && heads != 0)
                {
                    int pg = Mem.LoadI32(heads + CurTable + (s >> 2) * 4);
                    int head = pg != 0 ? Mem.LoadI32(pg) : 0;
                    if (head != 0)
                    {
                        Mem.StoreI32(pg, Mem.LoadI32(head + 8));
                        Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 2);
                        for (int p = head + s - 8; (uint)p > (uint)head; p -= 8) Mem.StoreI64(p, 0);
                        Mem.StoreI32(head, 1);
                        Mem.StoreI32(head + 4, typeId);
                        return head;
                    }
                    // (else the next block it never cut, or cut again since: cleared too)
                    int bump = pg != 0 ? Mem.LoadI32(pg + 12) : 0;
                    if (bump != 0)
                    {
                        int nb = bump + s;
                        Mem.StoreI32(pg + 12, nb + s <= pg + PageSize ? nb : 0);
                        Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 2);
                        for (int p = bump + s - 8; (uint)p > (uint)bump; p -= 8) Mem.StoreI64(p, 0);
                        Mem.StoreI32(bump, 1);
                        Mem.StoreI32(bump + 4, typeId);
                        return bump;
                    }
                }
            }
            return AllocObjectSlow(size, typeId);
        }

        static int AllocObjectSlow(int size, int typeId)
        {
            // The usual case in one piece, without a call: a freed block of the size is taken off its list, cleared
            // and given its header. (An object's identity hash is made when it is first asked for: HashOf.)
            if (SingleThreaded())
            {
                int s = RoundSmall(size);
                if (s <= MaxSmall && s_table != 0)
                {
                    // (AllocObject, its caller, looked in the size's hot slot and run)
                    int head = 0;
                    {
                        int pg = Mem.LoadI32(s_cur + (s >> 2) * 4);
                        if (pg != 0)
                        {
                            head = Mem.LoadI32(pg);
                            if (head != 0)
                            {
                                Mem.StoreI32(pg, Mem.LoadI32(head + 4));
                                Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 1);
                            }
                            else
                            {
                                // (a block of the page never used: clear already, PageAlloc clears a page it takes)
                                int bump = Mem.LoadI32(pg + 12);
                                if (bump != 0)
                                {
                                    int nb = bump + s;
                                    Mem.StoreI32(pg + 12, nb + s <= pg + PageSize ? nb : 0);
                                    Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 1);
                                    Mem.StoreI32(bump, 1);
                                    Mem.StoreI32(bump + 4, typeId);
                                    return bump;
                                }
                            }
                        }
                    }
                    if (head != 0)
                    {
                        for (int p = head + s - 8; (uint)p > (uint)head; p -= 8) Mem.StoreI64(p, 0);
                        Mem.StoreI32(head, 1);
                        Mem.StoreI32(head + 4, typeId);
                        return head;
                    }
                    if (s >= 8)
                    {
                        head = PageAlloc(s);
                        Mem.StoreI32(head, 1);
                        Mem.StoreI32(head + 4, typeId);
                        return head;
                    }
                }
            }
            int a = AllocBlock(size, KindPlain);
            Mem.StoreI32(a, 1);
            Mem.StoreI32(a + 4, typeId);
            if (ShareCheck()) Mem.StoreI32(a + 8, (ThreadId() & 0x1F) << 24);
            return a;
        }

        /// <summary>An object in a frame (one that never escapes its local, Compiler/EscapeAnalysis.cs): zeroed, its
        /// count immortal (retains and releases leave it), its hash code the next one as for any object.</summary>
        public static int InitFrameObject(int a, int size, int typeId)
        {
            Mem.Fill(a, 0, size);
            Mem.StoreI32(a, -1);
            Mem.StoreI32(a + 4, typeId);
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

        /// <summary>A store into an array that is not of the type the storing code names (a string[] used as an
        /// object[]): the value must be of the array's own element type.</summary>
        public static void StoreCheck(int arr, int val)
        {
            if (val == 0) return;
            if (!ElementFits(TypeInfo.ElementId(Mem.LoadI32(arr + 4)), Mem.LoadI32(val + 4))) throw new ArrayTypeMismatchException();
        }

        /// <summary>What string concatenation takes of an object: its ToString, nothing for null.</summary>
        public static string TextOf(object? o) => o?.ToString() ?? "";

        internal static bool ElementFits(int to, int from)
        {
            if (TypeInfo.IsAssignable(to, from)) return true;
            // (an array of a derived element type is an array of the base's)
            int te = TypeInfo.ElementId(to), fe = TypeInfo.ElementId(from);
            return te >= 0 && fe >= 0 && TypeInfo.Rank(to) == TypeInfo.Rank(from) && (TypeInfo.Flags(te) & 1) == 0 && (TypeInfo.Flags(fe) & 1) == 0 && ElementFits(te, fe);
        }

        public static int AllocArrayWithHeader(int length, int elemSize, int typeId, int dataOffset)
        {
            long total = dataOffset + (long)length * elemSize;
            if (total > 0x7FFF0000L) ThrowHelpers.OutOfMemory();
            int a = AllocObject((int)total, typeId);
            Mem.StoreI32(a + 12, length);
            return a;
        }

        /// <summary>Thread.GetCurrentProcessorId (manifest substitution): no processor numbers in WASM; the thread's.</summary>
        public static int CurrentProcessorId() => ThreadId() - 1;

        /// <summary>A string of the first n chars of buf (the runtime's own, without the public constructor's checks).</summary>
        public static string StringOf(char[] buf, int n)
        {
            int a = AllocString(n);
            Mem.Copy(a + 16, ArrayData(buf), n * 2);
            return Take<string>(a);
        }

        public static int AllocString(int length)
        {
            int a = AllocObject(16 + length * 2 + 2, 1);
            Mem.StoreI32(a + 12, length);
            return a;
        }

        /// <summary>An object's identity hash: made the first time it is asked for (most objects are never asked), and
        /// kept in the header from then on.</summary>
        public static int HashOf(object o)
        {
            int a = AddressOf(o);
            // (bits 31 to 29 of the word are flags: pinned by a static, marked: ObjectMarks, finalized; with the share
            // check, bits 24 to 28 are the thread that made the object)
            int mask = ShareCheck() ? 0xFFFFFF : 0x1FFFFFFF;
            int v = Mem.LoadI32(a + 8);
            if ((v & mask) == 0)
            {
                int h = NextHash() & mask;
                if (h == 0) h = 1;
                ref int w = ref Mem.RefI32(a + 8);
                while (true)
                {
                    v = Atomic.Load32(ref w);
                    if ((v & mask) != 0) break;
                    if (Atomic.CompareExchange32(ref w, v, v | h) == v) { v |= h; break; }
                }
            }
            return v & mask;
        }

        /// <summary>Bit 29 of an object's hash word: its finalizer has run (or is running). An object its finalizer
        /// made reachable again lives on and is not finalized a second time, unless GC.ReRegisterForFinalize asks.</summary>
        public const int FinalizedBit = 0x20000000;

        public static void ClearFinalized(object o)
        {
            ref int w = ref Mem.RefI32(AddressOf(o) + 8);
            while (true) { int v = Atomic.Load32(ref w); if ((v & FinalizedBit) == 0 || Atomic.CompareExchange32(ref w, v, v & ~FinalizedBit) == v) break; }
        }

        // ------------------------------------------------------------------
        // Arenas (section 4.3). Compiled into a module only when the compiler found a store that can close a cycle.
        //
        // An object of a cycle-capable type is allocated with 32 bytes before its header (AllocObjectArena: a tag, seven words):
        //   [-32] its tag (its side words: a waiting container and a note, below), [-28] its slots' bits, [-24] flags and u, [-20] its owner,
        //   [-16] its arena's record (0: none yet), [-12] next member, [-8] previous member (a ring),
        //   [-4] inside count: the references to it from slots of members of its arena.
        // It starts as a plain counted object. A store that can close a cycle puts the container and the value in one
        // arena (ArenaLink); the stored reference is then an inside one: counted in the value's inside count, not
        // in its count. A member's count word is -(outside count + 2): below -1, so retain and release take the
        // arena's path, where the outside references (locals, statics, other objects and arenas) count on the
        // member and on the arena's total.
        // Arena record (24 bytes): [0] its members that have outside references, [4] a member (the ring), [8] members, [12] flags,
        //   [16] the arena this one was merged into (0: none), [20] the members and merged records that point here.
        // Two arenas merge in a few steps whatever their sizes: the smaller's record points to the larger's, and its
        // ring is joined to the other's. A member's record word may so be a merged record: ArenaOf follows it to the
        // arena and makes the member point there; a merged record goes when nothing points to it any more.
        //
        // Nothing is scanned and nothing waits. A member no reference of either kind refers to is destroyed at once,
        // and the members only it referred to with it (a subtree taken out of a tree). An arena whose total reaches
        // zero is destroyed whole: what is left then is what its members' references to each other kept, the cycles.
        // A reference from a member to one that holds it (a child to its parent) is not counted at all (Owners, below),
        // so a part taken out of its tree has no reference left to it and goes at that store. A part that is let go while
        // its members still refer to each other in some other way (two siblings that refer to each other) stays until
        // the arena goes, as section 4.3 allows: nothing looks for it.
        // A pinned object (stored in a static) has bit 31 of its hash word set and never joins an arena.
        // ------------------------------------------------------------------
        // ArenaShared: another thread can reach the arena's members (Sharing): it takes no more members, and the
        // objects its members reach are shared. Shared objects never join an arena (their counts are atomic, an
        // arena's are under the runtime lock); cycles among them are not collected.
        const int ArenaBusy = 1, ArenaShared = 4;
        const int SharedCount = 0x40000000;
        // (record word 24: not zero when a member has a finalizer)
        const int ArenaRecord = 32;
        // The words of an object of a cycle-capable type (below) are before its header, in the block with it: the block
        // is [0] the tag, [4] to [28] seven words (word k at k bytes before the header), [32] the object. The tag is
        // PrefixMark less the address of the member's side words, a block of their own for two words few members ever
        // write (0: none): [0] the container waiting to own it (Owners), [4] the note of a check at the drop that failed
        // (Dropped). So a block's first word is below -1 for such an object and for no other.
        // (The words were once a block of their own, made when the first was written, with a pointer to it before the
        // header: 56 bytes for a member, where nearly every object of such a type meets a store that closes a cycle:
        // Roslyn's 3.7 million members had 205 MB of the self-hosted compiler's 555, and every word was two loads. Of
        // those members 6% had a waiting container and 5% a note: 32 bytes and the few side words now.)
        // (the note is apart from the word of a container waiting to own the member, which it once shared: a failed check
        // wiped the waiting owner, so the member stayed without one and was checked again at its next loss, every time,
        // all it owned gone through each time)
        // (With threads the object's count word must not be where a free block has its link, at 8: it is at 40, after
        // the tag and the lock word, LockWord, which another thread reads without the group's lock: KeyStripe.)
        // (threads: one more word, at 36, the member's lock word: GroupLocks)
        static int ArenaPrefix => ThreadMode() ? 40 : 32;
        const int SideWords = 8;
        /// <summary>The tag of a cycle-capable object without side words (the first word of its block, while it is
        /// allocated): no count word of an object that starts its block is below -1 (a member's is, and a member never
        /// starts its block), and memory that is no object has a zero there (Alloc). So a block's first word says what
        /// the block is.</summary>
        const int PrefixMark = -2;

        /// <summary>The object the address is in (a field's, an element's, a struct's inside either), or 0: the stack,
        /// the statics, memory that is no object.</summary>
        public static int ObjectOf(int addr)
        {
            int b = BlockOf(addr);
            if (b == 0) return 0;
            int w = Mem.LoadI32(b);
            if (w == 0) return 0;
            if (w <= PrefixMark) { int o = b + ArenaPrefix; return Mem.LoadI32(o) == 0 ? 0 : o; }
            return b;
        }

        /// <summary>One of the object's seven words (k: 4 to 28, the word that many bytes before its header); 0 until
        /// it is written (the block is cleared when the object is made).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] static int PL(int obj, int k) => Mem.LoadI32(obj - k);

        /// <summary>Sets one of the object's seven words.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] static void PS(int obj, int k, int value) => Mem.StoreI32(obj - k, value);

        /// <summary>The member's side words, or 0.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] static int Side(int obj) => PrefixMark - Mem.LoadI32(obj - ArenaPrefix);

        /// <summary>The container waiting to own the member (Owners), 0 for none.</summary>
        static int PLW(int obj) { int w = Side(obj); return w == 0 ? 0 : Mem.LoadI32(w); }
        /// <summary>The note of a check at the drop that failed (Dropped), 0 for none.</summary>
        /// (two loads, no call: asked at most counts of a member that a check failed for, and at every link and unlink)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] static int PLM(int obj) { int t = Mem.LoadI32(obj - ArenaPrefix); return t == PrefixMark ? 0 : Mem.LoadI32(PrefixMark - t + 4); }
        static void PSW(int obj, int value) { int w = value == 0 ? Side(obj) : SideOf(obj); if (w != 0) Mem.StoreI32(w, value); }
        static void PSM(int obj, int value) { int w = value == 0 ? Side(obj) : SideOf(obj); if (w != 0) Mem.StoreI32(w + 4, value); }

        /// <summary>The member's side words, made (cleared) if it has none.</summary>
        static int SideOf(int obj)
        {
            int w = Side(obj);
            if (w != 0) return w;
            // (without threads a block of their own, of 8 bytes: nothing asks which object an address in it is in)
            w = SingleThreaded() ? AllocBlock(SideWords, KindPlain) : Alloc(SideWords);
            Mem.StoreI64(w, 0);
            Mem.StoreI32(obj - ArenaPrefix, PrefixMark - w);
            return w;
        }

        /// <summary>The object's words are all zero again (it left its arena), its side words gone.</summary>
        static void MemberWordsFree(int obj)
        {
            SideFree(obj);
            Mem.StoreI32(obj - 28, 0);
            Mem.StoreI64(obj - 24, 0); Mem.StoreI64(obj - 16, 0); Mem.StoreI64(obj - 8, 0);
        }

        static void SideFree(int obj)
        {
            int w = Side(obj);
            if (w == 0) return;
            Mem.StoreI32(obj - ArenaPrefix, PrefixMark);
            if (SingleThreaded()) FreeBlock(w, SideWords, KindPlain); else Free(w, SideWords);
        }

        [Intrinsic] static extern int SccOf(int typeId);
        [Intrinsic] static extern void ArenaClearObject(int addr);
        [Intrinsic] static extern void ArenaFinalizeObject(int addr);
        [Intrinsic] static extern int ArenaFinType(int typeId);
        [Intrinsic] static extern void ArenaFreeObject(int addr);
        [Intrinsic] static extern void ArenaVisitObject(int addr);

        /// <summary>An object of a cycle-capable type: room for its arena's words before the header (the compiler
        /// sends AllocObject here for such a type).</summary>
        public static int AllocObjectArena(int size, int typeId)
        {
            // (the usual case in one piece, as AllocObject: a freed block of the size, cleared, its header set)
            if (SingleThreaded())
            {
                int s = RoundSmall(size + ArenaPrefix);
                if (s <= MaxSmall && s_table != 0)
                {
                    int hotSlot = s_hot + (s >> 2) * 4;
                    int head = Mem.LoadI32(hotSlot);
                    if (head != 0) Mem.StoreI32(hotSlot, 0);
                    else
                    {
                        // (the next block of the size's run: memory never used, clear already)
                        int run = Mem.LoadI32(hotSlot + Classes * 4);
                        if (run != Mem.LoadI32(hotSlot + 2 * Classes * 4))
                        {
                            Mem.StoreI32(hotSlot + Classes * 4, run + s);
                            Mem.StoreI32(run, PrefixMark);
                            Mem.StoreI32(run + ArenaPrefix, 1);
                            Mem.StoreI32(run + ArenaPrefix + 4, typeId);
                            return run + ArenaPrefix;
                        }
                        int pg = Mem.LoadI32(s_cur + (s >> 2) * 4);
                        if (pg != 0)
                        {
                            head = Mem.LoadI32(pg);
                            if (head != 0)
                            {
                                Mem.StoreI32(pg, Mem.LoadI32(head + 4));
                                Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 1);
                            }
                            else
                            {
                                int bump = Mem.LoadI32(pg + 12);
                                if (bump != 0)
                                {
                                    int nb = bump + s;
                                    Mem.StoreI32(pg + 12, nb + s <= pg + PageSize ? nb : 0);
                                    Mem.StoreI32(pg + 16, Mem.LoadI32(pg + 16) + 1);
                                    Mem.StoreI32(bump, PrefixMark);
                                    Mem.StoreI32(bump + ArenaPrefix, 1);
                                    Mem.StoreI32(bump + ArenaPrefix + 4, typeId);
                                    return bump + ArenaPrefix;
                                }
                            }
                        }
                    }
                    if (head != 0)
                    {
                        for (int p = head + s - 8; (uint)p >= (uint)head; p -= 8) Mem.StoreI64(p, 0);
                        Mem.StoreI32(head, PrefixMark);
                        Mem.StoreI32(head + ArenaPrefix, 1);
                        Mem.StoreI32(head + ArenaPrefix + 4, typeId);
                        return head + ArenaPrefix;
                    }
                    head = PageAlloc(s);
                    Mem.StoreI32(head, PrefixMark);
                    Mem.StoreI32(head + ArenaPrefix, 1);
                    Mem.StoreI32(head + ArenaPrefix + 4, typeId);
                    return head + ArenaPrefix;
                }
            }
            int a = AllocBlock(size + ArenaPrefix, KindArena) + ArenaPrefix;
            Mem.StoreI32(a - ArenaPrefix, PrefixMark);
            Mem.StoreI32(a, 1);
            Mem.StoreI32(a + 4, typeId);
            if (ShareCheck()) Mem.StoreI32(a + 8, (ThreadId() & 0x1F) << 24);
            return a;
        }

        /// <summary>Frees such an object (the destroy functions of cycle-capable types).</summary>
        public static void FreeObjectArena(int addr, int size)
        {
            // (its count word reads as dead from here on, wherever the block goes)
            Mem.StoreI32(addr, 0);
            // (its words need no clearing, a block is cleared when it is allocated; its side words go)
            SideFree(addr);
            // (the usual case in one small piece, as FreeObject)
            if (SingleThreaded() && !ShareCheck() && !Checked())
            {
                int s = RoundSmall(size + ArenaPrefix);
                if ((uint)s <= MaxSmall)
                {
                    int blk = addr - ArenaPrefix;
                    int hotSlot = s_hot + (s >> 2) * 4;
                    int old = Mem.LoadI32(hotSlot);
                    Mem.StoreI32(hotSlot, blk);
                    Mem.StoreI32(blk, 0);
                    if (old != 0) FreeSmallOld(old, s);
                    return;
                }
            }
            FreeBlock(addr - ArenaPrefix, size + ArenaPrefix, KindArena);
        }

        /// <summary>The arena of a member (its count word says it is one). (Its record, unless that was merged into another
        /// since: two loads where it is asked, the rest a call.)</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static int ArenaOf(int obj)
        {
            int ar = Mem.LoadI32(obj - 16);
            return Mem.LoadI32(ar + 16) == 0 ? ar : ArenaOfMerged(obj, ar);
        }

        static int ArenaOfMerged(int obj, int ar)
        {
            int f = Mem.LoadI32(ar + 16);
            // (merged since: to the arena it is part of now, and straight there from here on)
            int root = f;
            while (true) { int g = Mem.LoadI32(root + 16); if (g == 0) break; root = g; }
            PS(obj, 16, root);
            Mem.StoreI32(root + 20, Mem.LoadI32(root + 20) + 1);
            ArenaDeref(ar);
            return root;
        }

        /// <summary>One pointer to a record is gone: a merged record nothing points to goes (and its pointer on).</summary>
        static void ArenaDeref(int ar)
        {
            while (true)
            {
                int n = Mem.LoadI32(ar + 20) - 1;
                Mem.StoreI32(ar + 20, n);
                int f = Mem.LoadI32(ar + 16);
                if (n != 0 || f == 0) return;
                { MemStatsRecord(-1); Free(ar, ArenaRecord); }
                ar = f;
            }
        }

        static int NewArena()
        {
            int ar = Alloc(ArenaRecord); MemStatsRecord(1);
            Mem.StoreI64(ar, 0); Mem.StoreI64(ar + 8, 0); Mem.StoreI64(ar + 16, 0); Mem.StoreI64(ar + 24, 0);
            return ar;
        }

        /// <summary>Threads: a new arena for a plain object that joins it, of the stripe that object is locked by.</summary>
        static int NewArenaFor(int obj)
        {
            int ar = NewArena();
            if (ThreadMode()) Mem.StoreI32(ar + 28, HashStripe(obj));
            return ar;
        }

        /// <summary>A plain object (count c > 0) becomes a member: its count is its outside count.</summary>
        static void ArenaJoin(int ar, int obj)
        {
            // (exchanged, not stored: another thread that holds a reference may be counting it, without the lock, as
            // the plain object it is until here; its exchange then fails and it counts the member)
            int c;
            // (threads: the group's stripe first, GroupLocks.cs, while the object is still plain and so locked by its
            // own: a thread that sees it a member then finds the stripe this thread holds, and waits)
            OpStat(4);
            if (ThreadMode()) PS(obj, LockWord, Mem.LoadI32(ar + 28) + 1);
            do { c = Atomic.Load32(ref Mem.RefI32(obj)); } while (Atomic.CompareExchange32(ref Mem.RefI32(obj), c, -(c + 2)) != c);
            PS(obj, 16, ar);
            PS(obj, 4, 0);
            if (ArenaFinType(Mem.LoadI32(obj + 4)) != 0) Mem.StoreI32(ar + 24, 1);
            int head = Mem.LoadI32(ar + 4);
            if (head == 0)
            {
                PS(obj, 12, obj);
                PS(obj, 8, obj);
                Mem.StoreI32(ar + 4, obj);
            }
            else
            {
                int tail = PL(head, 8);
                PS(obj, 12, head);
                PS(obj, 8, tail);
                PS(tail, 12, obj);
                PS(head, 8, obj);
            }
            Mem.StoreI32(ar + 8, Mem.LoadI32(ar + 8) + 1);
            // (the record's first word: the members that have outside references. A count that leaves a member with
            // some before and after then changes the member's own count word and nothing else: no lock, rt.retain)
            if (c > 0) Mem.StoreI32(ar, Mem.LoadI32(ar) + 1);
            Mem.StoreI32(ar + 20, Mem.LoadI32(ar + 20) + 1);
            // what its slots hold already (stored while only the storing code knew it: ArenaLinkFresh) is linked next
            if (s_linkN == s_linkCap)
            {
                int cap = s_linkCap == 0 ? 64 : s_linkCap * 2;
                int nq = Alloc(cap * 4);
                if (s_linkQ != 0) { Mem.Copy(nq, s_linkQ, s_linkN * 4); Free(s_linkQ, s_linkCap * 4); }
                s_linkQ = nq; s_linkCap = cap;
            }
            Mem.StoreI32(s_linkQ + 4 * s_linkN, obj);
            s_linkN++;
        }

        // (the objects that joined an arena and whose slots are still to be linked)
        [Intrinsic] static int s_linkQ, s_linkN, s_linkCap;

        /// <summary>Links what the objects that just joined an arena hold in their slots, and what those hold, in turn
        /// (a list, not a nest: a long chain joins without a deep stack).</summary>
        static void ArenaLinkJoined()
        {
            while (s_linkN > 0)
            {
                s_linkN--;
                int o = Mem.LoadI32(s_linkQ + 4 * s_linkN);
                if (o == 0) continue;
                // (still a member: nothing ran in between)
                OpStat(6);
                int sa = s_visitArena, sm = s_visitMode, so = s_visitObj;
                s_visitArena = o; s_visitMode = 4; s_visitObj = o;
                ArenaVisitObject(o);
                s_visitArena = sa; s_visitMode = sm; s_visitObj = so;
            }
        }

        /// <summary>The smaller arena becomes part of the larger; returns the survivor.</summary>
        static int ArenaMerge(int a, int b)
        {
            OpStat(5);
            if (Mem.LoadI32(a + 8) < Mem.LoadI32(b + 8)) { int t = a; a = b; b = t; }
            int ha = Mem.LoadI32(a + 4), hb = Mem.LoadI32(b + 4);
            // (threads: b's members take a's stripe; this thread holds both)
            if (ThreadMode() && hb != 0 && Mem.LoadI32(a + 28) != Mem.LoadI32(b + 28))
            {
                int sw = Mem.LoadI32(a + 28) + 1, m = hb;
                do { PS(m, LockWord, sw); m = PL(m, 12); } while (m != hb);
            }
            if (hb != 0)
            {
                if (ha == 0) Mem.StoreI32(a + 4, hb);
                else
                {
                    // the two rings become one
                    int ta = PL(ha, 8), tb = PL(hb, 8);
                    PS(ta, 12, hb); PS(hb, 8, ta);
                    PS(tb, 12, ha); PS(ha, 8, tb);
                }
            }
            Mem.StoreI32(a, Mem.LoadI32(a) + Mem.LoadI32(b));
            Mem.StoreI32(a + 8, Mem.LoadI32(a + 8) + Mem.LoadI32(b + 8));
            Mem.StoreI32(a + 12, Mem.LoadI32(a + 12) | Mem.LoadI32(b + 12));
            Mem.StoreI32(a + 24, Mem.LoadI32(a + 24) | Mem.LoadI32(b + 24));
            // (b's members still point to b: b points to a, for as long as one of them does)
            Mem.StoreI32(b, 0); Mem.StoreI32(b + 4, 0); Mem.StoreI32(b + 8, 0);
            if (Mem.LoadI32(b + 20) == 0) { MemStatsRecord(-1); Free(b, ArenaRecord); }
            else { Mem.StoreI32(b + 16, a); Mem.StoreI32(a + 20, Mem.LoadI32(a + 20) + 1); }
            return a;
        }

        // ---- Owners (section 4.3: cycles shaped like ownership are freed by counting alone) ----
        // A member records its owner: the member whose slot first took a counted reference to it. A reference stored
        // into one of a member's own slots (not an array's elements) to a member on its owner chain (itself, its
        // owner, its owner's owner, .. ChainMax steps) is an up-reference: it is not counted at all, since whatever
        // keeps the storing object alive through the chain keeps the target alive. So a child's reference to its
        // parent, through the list and array that hold the child, costs nothing, and a subtree taken out of its tree
        // has no reference left to it and is destroyed at that store.
        // What makes that exact: u counts, for each member, the up-references from it and from what it owns to members
        // above it (kept by walking the chain at each such store and removal, never anything else). A member with
        // u > 0 that something other than its owner refers to holds its owner with a counted reference (Held), and
        // so on up: a child held from outside keeps its parent, as it does in .NET. A member whose owner lets it go
        // keeps the owner as long as it holds it; then it has none, and the next container that holds it takes over.
        // Slot bits (two per slot, the first 16 words after the header): 1 a counted inside reference, 2 an
        // up-reference, 0 anything else (an outside-counted reference, or nothing).
        // An array has no slot bits: its word counts the inside references its elements hold (Elems says so).
        const int Owned = 1, Held = 2, Dying = 4, MultOne = 8, MultMask = 0x78, Queued = 0x80, Elems = 0x100, UOne = 0x200, UShift = 9;
        const int ChainMax = 8;
        [System.ThreadStatic] static int t_prevSlot, t_prevBits;
        [Intrinsic] static int s_visitObj, s_visitElem, s_destroying, s_killQ, s_killN, s_killCap, s_storeSlot;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int SlotIndex(int container, int slot)
        {
            if (slot == 0) return -1;
            int off = slot - container;
            return off >= 12 && off < 76 ? (off - 12) >> 2 : -1;
        }
        static int SlotBits(int container, int i) => (PL(container, 28) >> (2 * i)) & 3;
        static void SetSlotBits(int container, int i, int b)
        {
            int w = PL(container, 28);
            PS(container, 28, (w & ~(3 << (2 * i))) | (b << (2 * i)));
        }

        /// <summary>The target is the member itself or one of its owners (ChainMax steps up).</summary>
        static bool OnChain(int from, int target)
        {
            int v = from;
            for (int k = 0; k <= ChainMax; k++)
            {
                if (v == target) return true;
                v = PL(v, 20);
                if (v == 0) return false;
            }
            return false;
        }

        /// <summary>An up-reference from `from` to `target` came (d = 1) or went (d = -1): the u of each member from
        /// `from` up to below the target, then what each of them holds.</summary>
        static void UpPath(int from, int target, int d)
        {
            int v = from;
            for (int k = 0; k <= ChainMax && v != target && v != 0; k++)
            {
                PS(v, 24, PL(v, 24) + d * UOne);
                v = PL(v, 20);
            }
            v = from;
            for (int k = 0; k <= ChainMax && v != target && v != 0; k++)
            {
                int next = PL(v, 20);
                UpdateHold(v);
                v = next;
            }
        }

        /// <summary>A member with up-references below it that something other than its owner refers to holds its
        /// owner; one that no longer does lets it go. Follows the change up the chain.
        /// (Its usual case where it is asked: a member with no up-reference below it that does not hold its owner has
        /// nothing to change, want and have both false. Two bits of one word, against a call: 6.3 million calls in a
        /// parse of 400 classes, nearly all of them that.)</summary>
        static void UpdateHold(int v)
        {
            OpStat(16);
            bool first = true;
            while (true)
            {
                int c = Mem.LoadI32(v);
                if (c >= -1) return;
                int fl = PL(v, 24);
                if ((fl & Dying) != 0) return;
                int w = PL(v, 20);
                if (w == 0) return;
                int others = PL(v, 4) - ((fl & MultMask) >> 3) + (-c - 2);
                bool want = (uint)fl >= UOne && others > 0;
                if (want == ((fl & Held) != 0)) return;
                // (a stale hold: an owned member keeps the hold it made, unless its owner is dying. Wanted still means
                // held; what a stale hold keeps is found by the check at the drop when its structure is let go: its top's
                // own loss of support, a note's number at zero, or a destruction's survivor, below)
                if (!want && (fl & Owned) != 0 && StickyHolds() && (PL(w, 24) & Dying) == 0) return;
                if (DropStats()) { OpStat(first ? 24 : 25); first = false; }
                if (want)
                {
                    PS(v, 24, fl | Held);
                    PS(w, 4, PL(w, 4) + 1);
                    v = w;
                    continue;
                }
                fl &= ~Held;
                PS(v, 24, fl);
                if ((fl & Owned) == 0) { PS(v, 20, 0); Adopt(v); }
                int n = PL(w, 4) - 1;
                PS(w, 4, n);
                if (n == 0 && Mem.LoadI32(w) == -2) { Kill(w); return; }
                v = w;
            }
        }

        /// <summary>UpdateHold has nothing to do for the member: its first look (not a member, dying, no owner, or holding
        /// its owner exactly when it should) ends it. Written where UpdateHold is asked after a first or last outside
        /// reference, a link and an unlink: in a parse of 400 classes 6.3 million calls, 5.5 million of them ending
        /// there.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool HoldSettled(int v)
        {
            int c = Mem.LoadI32(v);
            if (c >= -1) return true;
            int fl = Mem.LoadI32(v - 24);
            if ((fl & Dying) != 0 || Mem.LoadI32(v - 20) == 0) return true;
            bool want = (uint)fl >= UOne && Mem.LoadI32(v - 4) - ((fl & MultMask) >> 3) + (-c - 2) > 0;
            if (want == ((fl & Held) != 0)) return true;
            // (a stale hold an owned member keeps, UpdateHold)
            return !want && (fl & Owned) != 0 && StickyHolds() && (Mem.LoadI32(Mem.LoadI32(v - 20) - 24) & Dying) == 0;
        }

        /// <summary>A member no reference of either kind refers to, found while another change is under way: destroyed
        /// when that change is done (DrainKills, before the operation returns), or with the others of a destruction.</summary>
        static void Kill(int w)
        {
            int fl = PL(w, 24);
            if ((fl & (Dying | Queued)) != 0) return;
            if (s_destroying != 0)
            {
                PS(w, 24, fl | Dying);
                ArenaTakeOut(ArenaOf(w), w);
                PS(w, 12, s_visitDead);
                s_visitDead = w;
                return;
            }
            KillPush(w);
            PS(w, 24, fl | Queued);
        }

        static void KillPush(int w)
        {
            if (s_killN == s_killCap)
            {
                int cap = s_killCap == 0 ? 16 : s_killCap * 2;
                int nq = Alloc(cap * 4);
                if (s_killQ != 0) { Mem.Copy(nq, s_killQ, s_killN * 4); Free(s_killQ, s_killCap * 4); }
                s_killQ = nq; s_killCap = cap;
            }
            Mem.StoreI32(s_killQ + 4 * s_killN, w);
            s_killN++;
        }

        /// <summary>A member that is destroyed while it waits in the list: taken off it.</summary>
        static void Unqueue(int m)
        {
            int fl = PL(m, 24);
            if ((fl & Queued) == 0) return;
            PS(m, 24, fl & ~Queued);
            for (int i = 0; i < s_killN; i++) if (Mem.LoadI32(s_killQ + 4 * i) == m) Mem.StoreI32(s_killQ + 4 * i, 0);
        }

        static void DrainKills()
        {
            // (a destruction under way finishes first: its caller drains the list after it, and a check at the drop
            // does not run during one)
            if (s_destroying != 0) return;
            while (s_killN > 0)
            {
                s_killN--;
                int w = Mem.LoadI32(s_killQ + 4 * s_killN);
                // (0: destroyed since, with another; and it may be referred to again)
                if (w == 0) continue;
                PS(w, 24, PL(w, 24) & ~Queued);
                if (Mem.LoadI32(w) == -2 && PL(w, 4) == 0 && (PL(w, 24) & Dying) == 0
                    && (Mem.LoadI32(ArenaOf(w) + 12) & ArenaBusy) == 0) ArenaDestroyFrom(w, 0);
                // (a destruction's survivor, QueueCheck: kept by what it owns alone? A top whose check failed is looked at
                // again by its note's number)
                else if (StickyHolds() && (PLM(w) & 1) == 0 && DropCandidate(w)) Dropped(w);
            }
        }

        /// <summary>A member to check at the drop once the operation under way is done (a destruction's survivor).</summary>
        static void QueueCheck(int w)
        {
            int fl = PL(w, 24);
            if ((fl & (Dying | Queued)) != 0) return;
            KillPush(w);
            PS(w, 24, fl | Queued);
        }

        /// <summary>A member without an owner whose reference a container still holds counted: that container owns it
        /// from now on (unless the member is above it).</summary>
        static void Adopt(int v)
        {
            int p = PLW(v);
            // (odd: a top's note, Dropped; and nothing adopts a member that is dying)
            if (p == 0 || (p & 1) != 0 || (PL(v, 24) & Dying) != 0) return;
            PSW(v, 0);
            if (Mem.LoadI32(p) >= -1) return;
            // (as the first container to hold it would have: the ownership turns round where the member owns
            // what is above the container, a parent it referred to before it was in the parent's list)
            TakeOwnership(v, p);
        }

        /// <summary>TryLeave's first tests, where it is asked after an unlink: a member with outside references that no
        /// member refers to and whose slots hold no linked reference.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool LeaveCandidate(int m) => Mem.LoadI32(m) < -2 && Mem.LoadI32(m - 4) == 0 && Mem.LoadI32(m - 28) == 0;

        /// <summary>A member that no member refers to and that refers to none (a list's array after Clear, held by a
        /// static): a plain object again, so what it once held is not kept in one unit with it. With it gone, an arena
        /// nothing outside refers to is freed.</summary>
        static void TryLeave(int m)
        {
            int c = Mem.LoadI32(m);
            if (c >= -2 || PL(m, 4) != 0 || PL(m, 28) != 0 || s_destroying != 0) return;
            if ((PL(m, 24) & ~Elems) != 0 || PL(m, 20) != 0) return;
            int ar = ArenaOf(m);
            if ((Mem.LoadI32(ar + 12) & ArenaBusy) != 0) return;
            // (its own slots, read once: none holds a member of the arena, whatever the counts say)
            OpStat(11);
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem, ss = s_handSum;
            s_visitArena = ar; s_visitMode = 8; s_visitObj = m; s_visitElem = 0; s_handSum = 0;
            ArenaVisitObject(m);
            int found = s_handSum;
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se; s_handSum = ss;
            if (found != 0) return;
            OpStat(12);
            ClearMark(m);
            ArenaTakeOut(ar, m);
            // (threads: its words cleared but kept, before it is a plain object, which a thread that holds no lock of
            // this group may then lock and join to another: KeyStripe reads the words of objects it does not hold.
            // Not its lock word: until the exchange below it is a member of this group, which a thread must wait for)
            if (ThreadMode()) { int w = Side(m); if (w != 0) Mem.StoreI64(w, 0); Mem.StoreI32(m - 28, 0); Mem.StoreI64(m - 24, 0); Mem.StoreI64(m - 16, 0); Mem.StoreI64(m - 8, 0); }
            // (exchanged: other threads count it meanwhile; it has outside references, so one member fewer that does)
            do { c = Atomic.Load32(ref Mem.RefI32(m)); } while (Atomic.CompareExchange32(ref Mem.RefI32(m), c, -c - 2) != c);
            if (!ThreadMode()) MemberWordsFree(m);
            int total = Mem.LoadI32(ar) - 1;
            Mem.StoreI32(ar, total);
            if (total != 0) return;
            if (Mem.LoadI32(ar + 8) == 0) ArenaEmpty(ar); else ArenaFree(ar);
        }

        /// <summary>The member has an owner now: a counted reference in one of its own slots to a member that is on its
        /// chain becomes an up-reference (a parent set before the child was put in the parent's list).</summary>
        static void Reevaluate(int v)
        {
            OpStat(13);
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem;
            s_visitArena = ArenaOf(v); s_visitMode = 5; s_visitObj = v; s_visitElem = 0;
            ArenaVisitObject(v);
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se;
        }

        /// <summary>The container took a counted reference to a member without an owner: it owns it, unless that
        /// would make the member its own owner further up. Then the member right below it on the chain stops being
        /// owned by it (when nothing below refers above it), and the ownership turns round: a parent first referred to
        /// by a child, the child then put into the parent's list.</summary>
        static void TakeOwnership(int val, int container)
        {
            OpStat(20);
            if (OnChain(container, val))
            {
                int x = container;
                for (int k = 0; k <= ChainMax && PL(x, 20) != val; k++) x = PL(x, 20);
                int xf = PL(x, 24);
                if (PL(x, 20) != val || (uint)xf >= UOne || (xf & Held) != 0) return;
                PS(x, 24, xf & ~(Owned | MultMask));
                PS(x, 20, 0);
            }
            PS(val, 20, container);
            PS(val, 24, PL(val, 24) | Owned | MultOne);
            // (only a counted reference in one of its own slots can become an up-reference)
            if ((PL(val, 28) & 0x55555555) != 0 && (PL(val, 24) & Elems) == 0) Reevaluate(val);
        }

        [Intrinsic] static int s_handFrom, s_handSum;

        // The references that members with finalizers among them gave up as they died: (slot, value) pairs. .NET
        // runs a finalizer while everything its object refers to is still there, so the references are put back
        // for the finalizers of all that die together, and taken out again after.
        [Intrinsic] static int s_cutQ, s_cutN, s_cutCap, s_cutOn;

        static void NoteCut(int slot, int value)
        {
            if (s_cutN == s_cutCap)
            {
                int cap = s_cutCap == 0 ? 64 : s_cutCap * 2;
                int nq = Alloc(cap * 8);
                if (s_cutQ != 0) { Mem.Copy(nq, s_cutQ, s_cutN * 8); Free(s_cutQ, s_cutCap * 8); }
                s_cutQ = nq; s_cutCap = cap;
            }
            Mem.StoreI32(s_cutQ + 8 * s_cutN, slot);
            Mem.StoreI32(s_cutQ + 8 * s_cutN + 4, value);
            s_cutN++;
        }

        /// <summary>The references noted from `from` on are back in their slots (one to an object that lives on is
        /// counted meanwhile: a finalizer that clears its field releases it, as it does a plain object's).</summary>
        static void CutsBack(int from, int to) => CutsBack(s_cutQ, from, to);
        static void CutsBack(int q, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                int slot = Mem.LoadI32(q + 8 * i), v = Mem.LoadI32(q + 8 * i + 4);
                Mem.StoreI32(slot, v);
                Retain(Borrow<object>(v));
            }
        }

        /// <summary>And out again, where the finalizers left them.</summary>
        static void CutsOut(int from, int to) => CutsOut(s_cutQ, from, to);
        static void CutsOut(int q, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                int slot = Mem.LoadI32(q + 8 * i), v = Mem.LoadI32(q + 8 * i + 4);
                if (Mem.LoadI32(slot) != v) continue;
                Mem.StoreI32(slot, 0);
                Release(Borrow<object>(v));
            }
        }

        /// <summary>The owner of `from` put `to` in the slot where `from` was (a list's array grown: the new array has
        /// taken the old one's items). An item the old container owned that the new one holds too is the new one's
        /// from now on, with the references it and what it owns have to objects above: the two containers have the
        /// same owner, so those go the same way up. Otherwise the items, referred to from two containers, would hold
        /// the old one (as anything referred to from elsewhere holds its owner), and it them, for good.
        /// Not when something below refers to the old container itself (then it must stay): told by the counts, the
        /// references that pass it being all that its items have above them.</summary>
        static void Handover(int from, int to, int owner)
        {
            // (threads: read only in a group this thread holds)
            if (!ThreadMode()) { if (to == 0 || to == from || Mem.LoadI32(to) >= -1 || PL(to, 20) != owner || PL(to, 16) != PL(from, 16)) return; }
            else if (to == 0 || to == from || Mem.LoadI32(to) >= -1 || !GroupHeld(to) || PL(to, 20) != owner || PL(to, 16) != PL(from, 16)) return;
            OpStat(17);
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem, sf = s_handFrom, ss = s_handSum;
            s_visitArena = PL(from, 16); s_handFrom = from; s_handSum = 0; s_visitElem = 0;
            s_visitMode = 6; s_visitObj = from;
            ArenaVisitObject(from);
            if (s_handSum == (int)((uint)PL(from, 24) >> UShift))
            {
                s_visitMode = 7; s_visitObj = to;
                ArenaVisitObject(to);
            }
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se; s_handFrom = sf; s_handSum = ss;
            UpdateHold(to);
        }

        /// <summary>The container gave up one counted reference to the member: if it owned it, one of the owner's
        /// references fewer; with the last, the member has no owner (unless it holds it: then until it lets go).</summary>
        static void OwnerLetGo(int old, int container)
        {
            if (PLW(old) == container) PSW(old, 0);
            if (PL(old, 20) != container) return;
            int fl = PL(old, 24);
            if ((fl & Owned) == 0) return;
            fl -= MultOne;
            if ((fl & MultMask) != 0) { PS(old, 24, fl); return; }
            fl &= ~Owned;
            PS(old, 24, fl);
            if ((fl & Held) == 0) PS(old, 20, 0);
        }

        // ---- The check at the drop (the owner's decision of 2026-11-18) ----
        // A member that something besides its owner refers to holds its owner (above), since that other may be
        // outside the tree. When it is inside (a child in its parent's list and in its parent's dictionary), the tree
        // holds itself: let go, its top has no reference from outside and no owner, and is kept by holds alone.
        // Counting cannot tell the two apart; one look can. At the store or release that leaves a member so, what it
        // owns is gone through once: every counted reference among them is taken off, and if none of them is left
        // with a reference of any kind, nothing outside refers to any of them and they are destroyed together, as
        // members that die together are. Otherwise the counts are put back, and the top remembers how many of them
        // outside code holds (the word for another holder, odd): it is looked at again when that number is zero, or
        // when one of them loses a reference from a member.
        [Intrinsic] static int s_trQ, s_trN, s_trCap, s_trial, s_watch, s_watchHit;
        // (every group's: threads count it atomically)
        static int s_failedTops;

        static void TrPush(int m)
        {
            if (s_trN == s_trCap)
            {
                int cap = s_trCap == 0 ? 256 : s_trCap * 2;
                int nq = Alloc(cap * 4);
                if (s_trQ != 0) { Mem.Copy(nq, s_trQ, s_trN * 4); Free(s_trQ, s_trCap * 4); }
                s_trQ = nq; s_trCap = cap;
            }
            Mem.StoreI32(s_trQ + 4 * s_trN, m);
            s_trN++;
        }

        /// <summary>The top's note of a check that failed is gone. (It stays noted as a member that may be under such a
        /// top, 2: it can be, a top of its own under another's, and counted in that one's numbers.)</summary>
        static void ClearMark(int m)
        {
            if ((PLM(m) & 1) == 0) return;
            PSM(m, MarkHeld | MarkOutside);
            if (!ThreadMode()) s_failedTops--; else Atomic.Add32(ref s_failedTops, -1);
        }

        /// <summary>The member above all its owners (itself when it has none); 0 when that is far.</summary>
        static int Top(int m)
        {
            int v = m;
            if (DropStats()) s_dsTops++;
            for (int k = 0; k < 64; k++)
            {
                if (DropStats()) s_dsTopHops++;
                if ((PL(v, 24) & Owned) == 0) { if (DropStats() && (PLM(v) & 1) != 0) s_dsTopMarked++; return v; }
                int w = PL(v, 20);
                if (w == 0 || Mem.LoadI32(w) >= -1) { if (DropStats() && (PLM(v) & 1) != 0) s_dsTopMarked++; return v; }
                v = w;
            }
            if (DropStats()) s_dsTopFar++;
            return 0;
        }

        /// <summary>The member is the top or one of what it owns, by the owners above it (within a reach of 64).</summary>
        static bool Under(int m, int top)
        {
            int v = m;
            for (int k = 0; k < 64 && v != 0; k++)
            {
                if (v == top) return true;
                if ((PL(v, 24) & Owned) == 0) return false;
                int w = PL(v, 20);
                if (w == 0 || Mem.LoadI32(w) >= -1) return false;
                v = w;
            }
            return false;
        }

        static void TrVisit(int mode)
        {
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem;
            for (int i = 0; i < s_trN; i++)
            {
                int m = Mem.LoadI32(s_trQ + 4 * i);
                s_visitMode = mode; s_visitObj = m; s_visitElem = 0;
                ArenaVisitObject(m);
                // (what it holds of its owner is a counted reference too)
                if (mode != 13 && (PL(m, 24) & Held) != 0)
                {
                    int w = PL(m, 20);
                    if (w != 0 && Mem.LoadI32(w) < -1 && (PL(w, 24) & Dying) != 0) PS(w, 4, PL(w, 4) + (mode == 14 ? -1 : 1));
                }
            }
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se;
        }

        /// <summary>Dropped's first tests, where it is asked: a member with no outside reference and some inside one, neither
        /// owned nor dying. (Most members at their last outside reference are owned: the call ended there.)</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool DropCandidate(int x) => Mem.LoadI32(x) == -2 && Mem.LoadI32(x - 4) != 0 && (Mem.LoadI32(x - 24) & (Owned | Dying)) == 0;

        /// <summary>A member left without a reference from outside: when it has no owner and no other container
        /// holds it, the check. True when it was destroyed, with all it owns.</summary>
        static bool Dropped(int x)
        {
            if (NoDropCheck() || s_destroying != 0 || s_trial != 0) return false;
            if (Mem.LoadI32(x) != -2 || PL(x, 4) == 0) return false;
            if ((PL(x, 24) & (Owned | Dying)) != 0) return false;
            // (a container waiting to own it refers to it: not kept by holds alone)
            if (PLW(x) != 0) return false;
            int ar = ArenaOf(x);
            if (Mem.LoadI64(ar + 12) != 0) return false;
            ClearMark(x);
            s_trial = 1;
            // what it owns, and what they own: marked as they are found
            s_trN = 0;
            PS(x, 24, PL(x, 24) | Dying);
            TrPush(x);
            TrVisit(13);
            // the counted references among them, off
            TrVisit(14);
            int held = 0, outside = 0; bool inside = true;
            for (int i = 0; i < s_trN; i++)
            {
                int m = Mem.LoadI32(s_trQ + 4 * i);
                if (Mem.LoadI32(m) != -2) held++;
                int left = PL(m, 4);
                if (left != 0) { inside = false; outside += left; }
            }
            TrVisit(15);
            bool dead = inside && held == 0;
            if (DropStats())
            {
                s_dsCalls++; s_dsWalked += s_trN; if (s_trN > s_dsMax) s_dsMax = s_trN;
                if (dead) s_dsDead++; else if (!inside) s_dsFailInside++; else s_dsFailHeld++;
                if ((s_dsCalls & 4095) == 0) DsPrint();
            }
            s_watchHit = 0;
            if (!dead)
            {
                // (and each member under it noted as one: a note of 2, which no check reads as a top's; TopHeld and
                // NoteOutside look for the top of a noted member only. A member that comes under the top later is in
                // none of its numbers, so what it does changes none of them: the walk up for every count of every
                // member, most of them under no such top, was a seventh of the self-hosted compiler's time)
                for (int i = 0; i < s_trN; i++) { int m = Mem.LoadI32(s_trQ + 4 * i); PS(m, 24, PL(m, 24) & ~Dying); if (m != x && (PLM(m) & 1) == 0) PSM(m, PLM(m) | (inside ? MarkHeld : MarkOutside)); }
                // (the note: odd; bit 1 clear, how many of them outside code holds, TopHeld; set, how many references
                // from members outside what it owns are left into it, NoteOutside)
                PSM(x, inside ? (held << 2) | 1 : (outside << 2) | 3);
                if (!ThreadMode()) s_failedTops++; else Atomic.Add32(ref s_failedTops, 1);
                s_trial = 0;
                return false;
            }
            if (s_watch != 0 && Mem.LoadI32(s_watch) < -1 && (PL(s_watch, 24) & Dying) != 0) s_watchHit = 1;
            // they die together: off the ring, in the list of the dead behind the top
            int rest = 0;
            for (int i = 1; i < s_trN; i++)
            {
                int m = Mem.LoadI32(s_trQ + 4 * i);
                ArenaTakeOut(ar, m);
                PS(m, 12, rest);
                rest = m;
            }
            s_trial = 0;
            PS(x, 24, PL(x, 24) & ~Dying);
            ArenaDestroyFrom(x, rest);
            return true;
        }

        /// <summary>A member under a top whose check failed got its first reference from outside, or lost its
        /// last (d): the top's number; at zero the top is looked at again. Returns the top to look at, or 0.</summary>
        static int TopHeld(int p, int d)
        {
            if (!HeldMarked(PLM(p))) return 0;
            int t = Top(p);
            if (t == 0) return 0;
            int o = PLM(t);
            if ((o & 3) != 1) return 0;
            int n = (o >> 2) + d;
            if (n > 0) { PSM(t, (n << 2) | 1); return 0; }
            return t;
        }

        /// <summary>A reference from a member (the container) into what a top owns came (d = 1) or went (-1). When the
        /// top's check failed because such references from outside what it owns were left, their number; at zero the top
        /// is looked at again. Returns the top to look at, or 0. (A reference from inside changes nothing.)</summary>
        static int NoteOutside(int member, int container, int d)
        {
            if (!OutsideMarked(PLM(member))) return 0;
            int t = Top(member);
            if (t == 0) return 0;
            int o = PLM(t);
            if ((o & 3) != 3 || Under(container, t)) return 0;
            int n = (o >> 2) + d;
            if (n > 0) { PSM(t, (n << 2) | 3); return 0; }
            return t;
        }

        /// <summary>Retain of an arena member, under the runtime lock: one more outside reference (its first: the
        /// others are counted without the lock, rt.retain; so the count is added to atomically here too).</summary>
        public static void ArenaRetain(int p)
        {
            OpStat(14);
            // (without threads, its usual case first, with no call: the member's first outside reference (count -2), its
            // record not merged into another (ArenaOf would follow it), under no top whose number of held members is
            // kept (TopHeld has nothing to do), and its hold settled: with no up-reference below it, or holding its owner
            // already, as one more outside reference wants (UpdateHold has nothing to do))
            if (!ThreadMode())
            {
                int ar0 = PL(p, 16), fl0 = PL(p, 24);
                if (Mem.LoadI32(p) == -2 && Mem.LoadI32(ar0 + 16) == 0 && (s_failedTops == 0 || !HeldMarked(PLM(p)))
                    && ((uint)fl0 < UOne ? (fl0 & Held) == 0 : (fl0 & Held) != 0 && PL(p, 4) - ((fl0 & MultMask) >> 3) + 1 > 0))
                {
                    Mem.StoreI32(p, -3);
                    Mem.StoreI32(ar0, Mem.LoadI32(ar0) + 1);
                    OpStat(26);
                    return;
                }
            }
            int old = Atomic.Add32(ref Mem.RefI32(p), -1);
            if (old == -2) { int ar = ArenaOf(p); Mem.StoreI32(ar, Mem.LoadI32(ar) + 1); if (s_failedTops != 0 && HeldMarked(PLM(p))) TopHeld(p, 1); }
            if ((uint)PL(p, 24) >= UOne && !HoldSettled(p)) UpdateHold(p);
        }

        /// <summary>Release of an arena member, under the runtime lock: one outside reference fewer (its last).</summary>
        public static void ArenaRelease(int p)
        {
            OpStat(15);
            // (without threads, its usual case first, with no call: the member's last outside reference goes (count -3),
            // its record not merged, other members of the arena still referred to from outside (the arena lives), the
            // arena not busy, the member still referred to from inside (it lives), owned and not dying (the check at the
            // drop is not for it), under no top whose number of held members is kept (TopHeld), and its hold settled:
            // with no up-reference below it and not holding, or holding while others than its owner still refer to it
            // from inside (UpdateHold))
            if (!ThreadMode() && !Checked())
            {
                int ar0 = PL(p, 16), fl0 = PL(p, 24), total0 = Mem.LoadI32(ar0) - 1, in0 = PL(p, 4);
                if (Mem.LoadI32(p) == -3 && Mem.LoadI32(ar0 + 16) == 0 && total0 != 0 && (Mem.LoadI32(ar0 + 12) & ArenaBusy) == 0 && in0 != 0
                    && (fl0 & (Owned | Dying)) == Owned && (s_failedTops == 0 || !HeldMarked(PLM(p)))
                    && ((uint)fl0 < UOne ? (fl0 & Held) == 0 : ((fl0 & Held) != 0) == (in0 - ((fl0 & MultMask) >> 3) > 0)))
                {
                    Mem.StoreI32(p, -2);
                    Mem.StoreI32(ar0, total0);
                    if (DropStats()) s_dsFromRelease++;
                    OpStat(27);
                    return;
                }
            }
            int w = Atomic.Add32(ref Mem.RefI32(p), 1) + 1;
            // (checked: a member released with no outside reference counted on it, a release too many or of an inside
            // reference; it would read as immortal, -1, from here on)
            if (Checked() && SingleThreaded() && w == -1) { ChkSay("checked: a " + TypeName(Borrow<object>(p)) + ", a member with no outside reference, released"); Mem.StoreI32(-4, 0); }
            int ar = ArenaOf(p);
            if (w == -2)
            {
                int total = Mem.LoadI32(ar) - 1;
                Mem.StoreI32(ar, total);
                if (total == 0) { ArenaFree(ar); return; }
            }
            if ((Mem.LoadI32(ar + 12) & ArenaBusy) != 0) return;
            // (no reference of either kind: it goes now, with what only it referred to)
            if (w == -2 && PL(p, 4) == 0) { ArenaDestroyFrom(p, 0); DrainKills(); return; }
            // (kept by what it owns alone, or under a top that was: the check at the drop)
            if (w == -2)
            {
                int t = s_failedTops != 0 && HeldMarked(PLM(p)) ? TopHeld(p, -1) : 0;
                if (DropStats()) s_dsFromRelease++;
                if (t != 0 && Dropped(t)) { DrainKills(); return; }
                // (a top a check failed for: again only when the number of its members outside code holds is zero, TopHeld)
                if (t != p && (PLM(p) & 1) == 0 && DropCandidate(p) && Dropped(p)) { DrainKills(); return; }
            }
            if ((uint)PL(p, 24) >= UOne && !HoldSettled(p)) { UpdateHold(p); DrainKills(); }
        }

        /// <summary>
        /// A store of value (owned) into a slot of container that can close a cycle: if both are of one cycle-capable
        /// group, they end up in one arena and the stored reference becomes an inside one, or an up-reference.
        /// Returns the value to store. `slot`: the slot's address, 0 for an array's element.
        /// </summary>
        public static int ArenaLink(int val, int container, int slot)
        {
            OpStat(0);
            if (container == 0) return val;
            // (threads: a store's region is locked at its link, which knows the value: StoreLockIf, GroupLocks.cs)
            if (ThreadMode() && t_regionC == container) RegionLock(container, val, true);
            // Nothing to link, and nothing of the container's to change: no lock. The value is null, or its own class
            // cannot be on a cycle with the container's (the site can store others that can: a list of nodes takes a
            // leaf), so the reference is a plain counted one whatever becomes of the two; and the slot is one whose
            // bits say nothing now (an array's element, a slot past those an object keeps bits for, a plain object's,
            // or a member's that holds no linked reference: `field = null` over null). (Under the lock, each such
            // store of twenty-four parsing threads stopped the rest: of a million stores that took it, 991,000
            // were a null into a member's field.)
            // (threads: also a pinned container's, which never joins: GroupLocks.cs, PinnedPlain)
            if (val == 0 || !CanLink(val, container) || ThreadMode() && PinnedPlain(container))
            {
                int i0 = SlotIndex(container, slot), cw = Mem.LoadI32(container);
                if (i0 < 0 || cw > 0) { OpStat(1); t_prevSlot = slot; t_prevBits = slot != 0 && i0 >= 0 ? 0 : -1; return val; }
                if (cw < -1 && SlotBits(container, i0) == 0) { OpStat(1); t_prevSlot = slot; t_prevBits = 0; return val; }
            }
            if (t_backN != 0) AfterStore();
            int mk = -1;
            if (!ThreadMode()) RuntimeLock();
            else if (t_regionC != container || t_regionMk < 0) mk = LinkLock(val, container);
            // (what the slot's bits said of the value it held: for the ArenaUnlink that follows the store)
            int i = Mem.LoadI32(container) < -1 ? SlotIndex(container, slot) : -1;
            t_prevSlot = slot;
            t_prevBits = slot != 0 && SlotIndex(container, slot) >= 0 ? 0 : -1;
            if (i >= 0) { t_prevBits = SlotBits(container, i); SetSlotBits(container, i, 0); }
            // (a container that joins an arena here has its slots linked: not this one, whose value is on its way out)
            int outer = s_storeSlot;
            s_storeSlot = slot;
            int r = val;
            if (val != 0)
            {
                // (the usual case: both are members of one arena already, which is not being destroyed or merged)
                int ar = Mem.LoadI32(container) < -1 && Mem.LoadI32(val) < -1 ? PL(container, 16) : 0;
                if (ar != 0 && ar == PL(val, 16) && Mem.LoadI64(ar + 12) == 0) { OpStat(2); LinkMembers(val, container, slot, ar, false); }
                else { OpStat(3); ArenaLinkCore(val, container, slot); }
            }
            if (s_linkN != 0) ArenaLinkJoined();
            s_storeSlot = outer;
            if (s_killN != 0) DrainKills();
            if (!ThreadMode()) RuntimeUnlock(); else if (mk >= 0) GroupUnlock(mk);
            return r;
        }

        /// <summary>ArenaLink where the compiler saw that only the storing code knows the container (a new object, not
        /// let out yet: FreshStores.cs): nothing can reach it, so the store closes no cycle and is a plain counted one.
        /// Unless the container is an arena's member already (it was linked while it was being made): then as any store.</summary>
        public static int ArenaLinkFresh(int val, int container, int slot)
        {
            // (an object stored into its own slot closes a cycle, whoever knows the object)
            if (container == 0 || Mem.LoadI32(container) > 0 && val != container) { t_prevSlot = 0; return val; }
            return ArenaLink(val, container, slot);
        }

        /// <summary>ArenaLink where the storing code holds the container in a local variable that owns its reference
        /// (the compiler saw that every value the variable gets is a new object or a call's result). When that
        /// reference is the only one (count 1), no object anywhere refers to the container, so nothing the stored
        /// value leads to can lead back to it: the store closes no cycle and is a plain counted one, and the objects
        /// below it need not be gone through (a tree whose root gets a new branch stays a tree of plain objects).
        /// The slot's old value goes as for a fresh object's (ArenaUnlinkFresh).</summary>
        public static int ArenaLinkOwned(int val, int container, int slot)
        {
            if (container == 0 || Mem.LoadI32(container) == 1 && val != container) { t_prevSlot = 0; return val; }
            return ArenaLink(val, container, slot);
        }

        /// <summary>The old value of a slot stored by ArenaLinkFresh: a plain object's slots hold counted references.</summary>
        public static void ArenaUnlinkFresh(int old, int container, int slot)
        {
            if (container == 0 || Mem.LoadI32(container) > 0) { Release(Borrow<object>(old)); return; }
            ArenaUnlink(old, container, slot);
        }

        static int ArenaLinkCore(int val, int container, int slot)
        {
            int cv = Mem.LoadI32(val), ca = Mem.LoadI32(container);
            if (cv == -1 || cv == 0 || ca == -1 || ca == 0) return val; // immortal, or being destroyed
            // (threads: a member stored into a plain object stays a counted reference, and the container plain. No
            // member refers to a plain object of a cycle-capable class, which joins as it is stored, so no cycle can
            // close through it until a member comes to refer to it, and it joins then, its slots linked as it does.
            // Joined here, every thread's own lexer and parser became members of the one group of Roslyn's shared
            // tokens, each store into them under that group's lock: GroupLocks.cs, LinkLock)
            if (ThreadMode() && ca > 0 && cv < -1) return val;
            int g = SccOf(Mem.LoadI32(val + 4));
            if (g == 0 || g != SccOf(Mem.LoadI32(container + 4))) return val;
            // (one whose finalizer is running, or that waits for the others': a counted reference, FinPhase)
            if (((PL(val, 24) | PL(container, 24)) & Dying) != 0) return val;
            if (ca > 0 && Mem.LoadI32(container + 8) < 0) return val; // pinned
            if (cv > 0 && Mem.LoadI32(val + 8) < 0) return val;
            int ra = ca < -1 ? ArenaOf(container) : 0, rv = cv < -1 ? ArenaOf(val) : 0;
            if (ra != 0 && (Mem.LoadI32(ra + 12) & ArenaBusy) != 0) return val;
            if (rv != 0 && (Mem.LoadI32(rv + 12) & ArenaBusy) != 0) return val;
            int ar = ra;
            if (ar == 0) { ar = rv != 0 ? rv : ThreadMode() ? NewArenaFor(container) : NewArena(); ArenaJoin(ar, container); }
            cv = Mem.LoadI32(val); // val may be the container itself
            if (cv > 0) ArenaJoin(ar, val);
            else
            {
                int now = ArenaOf(val);
                if (now != ar) ar = ArenaMerge(ar, now);
            }
            return LinkMembers(val, container, slot, ar, cv > 0);
        }

        // The count a store took is given back when the reference becomes an inside one. When that count is the
        // value's last outside one, the value, its container, or the whole arena may have nothing referring to it
        // from then on, and the storing code has yet to write the slot (and to read the slot's old value): the count
        // is kept until the store is written (ArenaUnlink follows every store), and given back then as any release.
        [System.ThreadStatic] static int t_backQ, t_backN, t_backCap;

        static void GiveBackLater(int val)
        {
            if (t_backN == t_backCap)
            {
                int cap = t_backCap == 0 ? 8 : t_backCap * 2;
                int nq = Alloc(cap * 4);
                if (t_backQ != 0) { Mem.Copy(nq, t_backQ, t_backN * 4); Free(t_backQ, t_backCap * 4); }
                t_backQ = nq; t_backCap = cap;
            }
            Mem.StoreI32(t_backQ + 4 * t_backN, val);
            t_backN++;
        }

        static void AfterStore()
        {
            // (threads: each as any release, which locks its group: it may no longer be a member by now)
            if (ThreadMode())
            {
                while (t_backN > 0) { t_backN--; Release(Borrow<object>(Mem.LoadI32(t_backQ + 4 * t_backN))); }
                return;
            }
            RuntimeLock();
            while (t_backN > 0)
            {
                t_backN--;
                ArenaRelease(Mem.LoadI32(t_backQ + 4 * t_backN));
            }
            RuntimeUnlock();
        }

        /// <summary>The value and the container are members of the arena: the stored reference becomes an inside one or
        /// an up-reference.</summary>
        static int LinkMembers(int val, int container, int slot, int ar, bool joined)
        {
            // the stored reference is inside the arena now: the count the store took is given back (its last outside
            // one after the store, GiveBackLater; at once for an object that joined for this store: nothing was
            // counted on its being referred to from outside)
            // (checked: a value stored with no outside count on it: the store's count was never taken)
            if (Checked() && SingleThreaded() && Mem.LoadI32(val) == -2) { ChkSay("checked: a " + TypeName(Borrow<object>(val)) + " stored into a " + TypeName(Borrow<object>(container)) + " with no outside count" + (joined ? " (it joined for this store)" : "")); Mem.StoreI32(-4, 0); }
            if (Atomic.Add32(ref Mem.RefI32(val), 1) == -3)
            {
                if (joined) Mem.StoreI32(ar, Mem.LoadI32(ar) - 1);
                else { Atomic.Add32(ref Mem.RefI32(val), -1); GiveBackLater(val); }
            }
            int i = s_visitElem != 0 ? -1 : SlotIndex(container, slot);
            if (i >= 0 && OnChain(container, val))
            {
                // an up-reference: not counted (the chain from the container to the value keeps the value)
                OpStat(23);
                SetSlotBits(container, i, 2);
                UpPath(container, val, 1);
                // (a value nothing else refers to kept its count: it goes when the store is written, AfterStore)
                if (Mem.LoadI32(val) == -2 && PL(val, 4) == 0) Kill(val);
                else UpdateHold(val);
                return val;
            }
            PS(val, 4, PL(val, 4) + 1);
            if (s_failedTops != 0 && OutsideMarked(PLM(val))) NoteOutside(val, container, 1);
            if (i >= 0) SetSlotBits(container, i, 1);
            else if (slot == 0 || s_visitElem != 0)
            {
                PS(container, 28, PL(container, 28) + 1);
                PS(container, 24, PL(container, 24) | Elems);
            }
            if (val != container)
            {
                int ow = PL(val, 20);
                int fl = PL(val, 24);
                if (ow == container && (fl & Owned) != 0)
                {
                    // (one more of its owner's references; more than can be told apart: no owner, as if let go)
                    if ((fl & MultMask) == MultMask) { fl &= ~(Owned | MultMask); PS(val, 24, fl); if ((fl & Held) == 0) PS(val, 20, 0); }
                    else PS(val, 24, fl + MultOne);
                }
                else if (ow == 0) TakeOwnership(val, container);
                // (the first other container that holds it is the one to own it next: a later one, a task's
                // result, a temporary array, is as often gone by then)
                else if (PLW(val) == 0) { ClearMark(val); PSW(val, container); }
            }
            if (!HoldSettled(val)) UpdateHold(val);
            return val;
        }

        /// <summary>The old value of a slot that can close a cycle was overwritten.</summary>
        /// <summary>Whether a reference from the container to the value can be part of a cycle: both of one group of
        /// classes that can refer to each other. Told by their classes, which do not change: no lock.</summary>
        static bool CanLink(int val, int container)
        {
            int g = SccOf(Mem.LoadI32(val + 4));
            return g != 0 && g == SccOf(Mem.LoadI32(container + 4));
        }

        public static void ArenaUnlink(int old, int container, int slot)
        {
            ArenaUnlinkOld(old, container, slot);
            if (t_backN != 0) AfterStore();
        }

        static void ArenaUnlinkOld(int old, int container, int slot)
        {
            // what the slot held, by its bits before the store (ArenaLink noted them); -1: not known, by the counts
            int bits = -1;
            if (slot != 0 && t_prevSlot == slot) { bits = t_prevBits; t_prevSlot = 0; }
            if (old == 0) return;
            OpStat(7);
            if (bits == 0) { OpStat(8); Release(Borrow<object>(old)); return; }
            // (never linked: a plain counted reference, see ArenaLink)
            if (!CanLink(old, container) || ThreadMode() && PinnedPlain(container)) { OpStat(8); Release(Borrow<object>(old)); return; }
            int mk = -1;
            if (!ThreadMode()) RuntimeLock();
            else
            {
                // (the container's group: an old value of another is no inside reference, and is read nothing of)
                if (t_regionC == container) RegionLock(container, old, false);
                if (t_regionC != container || t_regionMk < 0) { mk = t_lkN; LockGroupOf(container, false); }
            }
            int co = Mem.LoadI32(old), cc = Mem.LoadI32(container);
            if (bits == 2)
            {
                // (an up-reference: never counted)
                OpStat(9);
                if (co < -1 && cc < -1) { UpPath(container, old, -1); DrainKills(); }
                if (!ThreadMode()) RuntimeUnlock(); else if (mk >= 0) GroupUnlock(mk);
                return;
            }
            // an inside reference: both are members of one arena (they were linked when it was stored), the arena is
            // not being destroyed (its members' inside references are cut first), and it was counted as one
            bool inside;
            // (threads: an old value of a group this thread does not hold is of another arena; nothing of it is read)
            if (ThreadMode()) inside = co < -1 && cc < -1 && GroupHeld(old) && PL(old, 4) != 0;
            else inside = co < -1 && cc < -1 && PL(old, 4) != 0;
            if (inside)
            {
                int ar = PL(old, 16);
                if (ar != PL(container, 16) || Mem.LoadI32(ar + 16) != 0) { ar = ArenaOf(old); inside = ar == ArenaOf(container); }
                inside = inside && (Mem.LoadI32(ar + 12) & ArenaBusy) == 0;
            }
            if (inside)
            {
                OpStat(10);
                int n = PL(old, 4) - 1;
                PS(old, 4, n);
                if (slot == 0 && PL(container, 28) > 0) PS(container, 28, PL(container, 28) - 1);
                bool owned = PL(old, 20) == container && (PL(old, 24) & (Owned | MultMask)) == (Owned | MultOne);
                bool ownerEdge = PL(old, 20) == container;
                OwnerLetGo(old, container);
                // (a container replaced by another in the same slot, a list's array by a larger one: what it owned is
                // the new one's from here on)
                if (owned && n > 0 && slot != 0) { Handover(old, Mem.LoadI32(slot), container); n = PL(old, 4); }
                bool gone = false;
                if (n == 0 && co == -2) ArenaDestroyFrom(old, 0);
                else
                {
                    if (PL(old, 20) == 0) Adopt(old);
                    if (!HoldSettled(old)) UpdateHold(old);
                    // (left with what it owns alone, or under a top that a check failed for: the check at the drop)
                    s_watch = container;
                    if (s_failedTops != 0) OpStat(21);
                    if (DropStats()) s_dsFromUnlink++;
                    // (a top a check failed for is looked at again only where the look can succeed: when the number of
                    // references from outside what it owns that were left reaches zero, NoteOutside; when it lost its
                    // owner. Each look goes through all it owns: a sort over references to a large structure went
                    // through it at each move)
                    // (NoteOutside looks at the member's note before it walks to the top: most members have none. The
                    // walk to the top here first, to see it was not far, was a million walks of six hops a parse)
                    int back = s_failedTops != 0 && OutsideMarked(PLM(old)) ? NoteOutside(old, container, -1) : 0;
                    bool died = back != 0 && Dropped(back);
                    if (!died && back != old && (ownerEdge || (PLM(old) & 1) == 0) && DropCandidate(old)) died = Dropped(old);
                    gone = died && s_watchHit != 0;
                    s_watch = 0;
                    if (!died && old != container && LeaveCandidate(old)) TryLeave(old);
                }
                if (s_killN != 0) DrainKills();
                if (!gone && LeaveCandidate(container)) TryLeave(container);
            }
            if (!ThreadMode()) RuntimeUnlock(); else if (mk >= 0) GroupUnlock(mk);
            if (!inside) Release(Borrow<object>(old));
        }

        /// <summary>A store into a static readonly field: the object stays out of arenas. The field never changes, so the
        /// object lasts as long as the program and no cycle through it is ever garbage; and what many structures share
        /// from such fields (the empty array every list starts with) does not make one arena of them all. An object in
        /// any other static field is a member like any other: the static is one more reference to it.</summary>
        public static int ArenaPin(int val)
        {
            if (val != 0 && Mem.LoadI32(val) > 0) Mem.StoreI32(val + 8, Mem.LoadI32(val + 8) | int.MinValue);
            return val;
        }

        // ------------------------------------------------------------------
        // A store that has only the slot's address: through a `ref`, a span, a pointer, a struct's own method, an
        // exchange. Which object the slot is in is the allocator's to say (ObjectOf), and whether the slot is one a
        // cycle can close through is the object's type's (ArenaIsSlot, generated: the offsets of its sites). Such a
        // store is then the store the code would have made through the object: nothing about a slot depends on how
        // it was reached.
        // ------------------------------------------------------------------
        [Intrinsic] static extern int ArenaIsSlot(int obj, int offset);
        [Intrinsic] public static extern int LinkAt(int val, int slot);
        [Intrinsic] public static extern void UnlinkAt(int old, int slot);
        [Intrinsic] public static extern bool MoveRefs(int dst, int src, int count);
        [System.ThreadStatic] static int t_atSlot, t_atOwner;

        /// <summary>Threads: a store into a slot of the container that can close a cycle is one step (link, exchange,
        /// unlink) with the container's group locked, and the slot's place (GroupLocks.cs, the store's region): 1 for
        /// such a region. Also while the container is a plain object: another
        /// thread can make it a group's member in the middle of the store, and link the slot's old value, which a plain
        /// unlink then leaves counted (test 139, as shipped: two nodes of 320,000 never freed). A protocol that lets a
        /// plain container's store go without the lock is still to do (docs/AUDIT.md).</summary>
        public static int StoreLockIf(int container, int slot)
        {
            if (container == 0) return 0;
            return RegionEnter(container, slot);
        }

        /// <summary>The same for a slot known by its address (a slot of an object of a cycle-capable type).</summary>
        public static int StoreLockAtIf(int slot)
        {
            int c = SlotOwner(slot);
            if (c == 0) return 0;
            return RegionEnter(c, slot);
        }

        public static void StoreUnlockIf(int took) => RegionExit(took);

        /// <summary>The address is a slot of an object of a cycle-capable type (an arena's member, or one that can be).</summary>
        public static bool InArenaSlot(int slot) => SlotOwner(slot) != 0;

        /// <summary>The object of a cycle-capable type the address is a slot of (t_slotArray: an array's element), or 0.
        /// (ObjectOf, BlockOf and MapGet written out: each store by address asks, and each was a call)</summary>
        static int SlotOwner(int slot)
        {
            t_slotArray = false;
            if (s_top == 0 || (uint)slot >= (uint)s_top || (uint)slot < (uint)s_runLo) return 0;
            int leaf = Mem.LoadI32(s_mapTop + ((int)((uint)slot >> 22) << 2));
            if (leaf == 0) return 0;
            int e = Mem.LoadI32(leaf + ((int)(((uint)slot >> GShift) & 4095) << 2)), k = e & 7, b;
            if (k == 2) b = e & ~7;
            else if (k == 1 || k == 4)
            {
                int pg = e & ~7;
                int units = Mem.LoadI32(pg + 20) >> 1, size = units << 2;
                int off = slot - pg - PageHead;
                if (off < 0 || size == 0) return 0;
                // (without threads, the block's index by a multiplication: off is below a page, 2^14, and size at most
                // MaxSmall, so off * ceil(2^32 / size) / 2^32 is within 2^-18 above off / size, whose fraction is at
                // least 2^-11 below the next integer: the floor is the same. A division was most of this function)
                if (!ThreadMode() && units <= MaxSmall / 4)
                {
                    if (s_recip == 0) RecipInit();
                    uint m = (uint)Mem.LoadI32(s_recip + 4 * units);
                    b = pg + PageHead + (int)(((ulong)(uint)off * m) >> 32) * size;
                }
                else b = pg + PageHead + (int)((uint)off / (uint)size) * size;
            }
            else return 0;
            if (b == 0) return 0;
            int w = Mem.LoadI32(b);
            if (w == 0) return 0;
            int c = b;
            if (w <= PrefixMark) { c = b + ArenaPrefix; if (Mem.LoadI32(c) == 0) return 0; }
            if (SccOf(Mem.LoadI32(c + 4)) == 0) return 0;
            int kind = ArenaIsSlot(c, slot - c);
            t_slotArray = kind == 2;
            return kind != 0 ? c : 0;
        }
        [System.ThreadStatic] static bool t_slotArray, t_atArray;
        // (ceil(2^32 / (4 k)) for each block size 4 k of a page, SlotOwner; made the first time, without threads)
        static int s_recip;
        static void RecipInit()
        {
            int t = Alloc(4 * (MaxSmall / 4 + 1));
            Mem.StoreI32(t, 0);
            for (int k = 1; k <= MaxSmall / 4; k++) Mem.StoreI32(t + 4 * k, (int)(0xFFFFFFFFu / (uint)(4 * k) + 1));
            s_recip = t;
        }

        /// <summary>ArenaLink for a slot known by its address: the value to store (owned).</summary>
        public static int ArenaLinkAt(int val, int slot)
        {
            OpStat(18);
            int c = SlotOwner(slot);
            t_atSlot = slot; t_atOwner = c; t_atArray = t_slotArray;
            if (c == 0) return val;
            return ArenaLink(val, c, t_slotArray ? 0 : slot);
        }

        /// <summary>ArenaUnlink for the slot ArenaLinkAt was just called for (or any other): its old value.</summary>
        public static void ArenaUnlinkAt(int old, int slot)
        {
            OpStat(19);
            int c; bool array;
            if (t_atSlot == slot) { c = t_atOwner; array = t_atArray; } else { c = SlotOwner(slot); array = t_slotArray; }
            t_atSlot = 0;
            if (c == 0) { if (old != 0) Release(Borrow<object>(old)); return; }
            // (ArenaUnlink's two steps, one call fewer)
            ArenaUnlinkOld(old, c, array ? 0 : slot);
            if (t_backN != 0) AfterStore();
        }

        /// <summary>A block of references copied over slots a cycle can close through (a span's CopyTo into a list's
        /// array): element by element, as stores. False when the destination is no such slots: the plain way then.</summary>
        public static bool ArenaMove(int dst, int src, int count)
        {
            if (count <= 0 || SlotOwner(dst) == 0) return false;
            if ((uint)(dst - src) >= (uint)(count << 2)) for (int i = 0; i < count; i++) MoveOne(dst + (i << 2), src + (i << 2));
            else for (int i = count - 1; i >= 0; i--) MoveOne(dst + (i << 2), src + (i << 2));
            return true;
        }

        static void MoveOne(int d, int s)
        {
            int v = Mem.LoadI32(s);
            if (v != 0) Retain(Borrow<object>(v));
            v = ArenaLinkAt(v, d);
            int old = Mem.LoadI32(d);
            Mem.StoreI32(d, v);
            ArenaUnlinkAt(old, d);
        }

        // The generated visitor calls ArenaVisit for each slot of a member that can hold an inside reference
        // (ArenaVisitElement for an array's elements); what that does is the mode: 0 a dying member's references
        // (taken out; a member that loses its last dies too), 1 the same when all of them die anyway (taken out
        // only), 4 a copied or joining object's slots (linked as the stores they stand for), 5 a member that got
        // an owner (Reevaluate).
        [Intrinsic] static int s_visitArena, s_visitMode, s_visitDead;

        public static void ArenaVisitElement(int slot)
        {
            int e = s_visitElem;
            s_visitElem = 1;
            ArenaVisit(slot);
            s_visitElem = e;
        }

        public static void ArenaVisit(int slot)
        {
            int child = Mem.LoadI32(slot);
            // (4: each reference, counted by the copy, as a store of it)
            if (s_visitMode == 4) { if (child != 0 && slot != s_storeSlot) ArenaLinkCore(child, s_visitArena, slot); return; }
            // (16: LinkWalk, threads)
            if (ThreadMode() && s_visitMode == 16) { WalkVisit(child); return; }
            // (13 to 15, Dropped: what the member owns; a counted reference to one of them off, and back on)
            if (s_visitMode >= 13)
            {
                if (child == 0 || Mem.LoadI32(child) >= -1) return;
                if (ThreadMode() && !GroupHeld(child)) return;
                int cf = PL(child, 24);
                if (s_visitMode == 13)
                {
                    if (PL(child, 20) == s_visitObj && (cf & (Owned | Dying)) == Owned) { PS(child, 24, cf | Dying); TrPush(child); }
                    return;
                }
                if ((cf & Dying) == 0) return;
                int ti = s_visitElem != 0 ? -1 : SlotIndex(s_visitObj, slot);
                int tb = ti >= 0 ? SlotBits(s_visitObj, ti) : -1;
                if (tb == 0 || tb == 2) return;
                PS(child, 4, PL(child, 4) + (s_visitMode == 14 ? -1 : 1));
                return;
            }
            // (10 to 12, FinPhase: a reference to a member that dies with this one comes off its count, goes back
            // on, is taken out of the slot)
            if (s_visitMode >= 10)
            {
                if (child == 0 || SccOf(Mem.LoadI32(child + 4)) == 0 || (PL(child, 24) & Dying) == 0 || Mem.LoadI32(child) <= 0) return;
                if (s_visitMode == 10) Mem.StoreI32(child, Mem.LoadI32(child) - 1);
                else if (s_visitMode == 11) Mem.StoreI32(child, Mem.LoadI32(child) + 1);
                else Mem.StoreI32(slot, 0);
                return;
            }
            if (child == 0 || Mem.LoadI32(child) >= -1) return;
            // (threads: one of a group this thread does not hold is of another arena; nothing of it is read)
            if (ThreadMode() && !GroupHeld(child)) return;
            if (s_visitMode == 1) { if (ArenaOf(child) == s_visitArena) { if (s_cutOn != 0) NoteCut(slot, child); Mem.StoreI32(slot, 0); } return; }
            if (s_visitMode == 8) { if (ArenaOf(child) == s_visitArena) s_handSum++; return; }
            int obj = s_visitObj;
            if (s_visitMode >= 6)
            {
                // Handover: 6 what the old container's own items have above them, in all; 7 an item of the new
                // container that the old one owns changes owner
                int cf = PL(child, 24);
                if (PL(child, 20) != s_handFrom || (cf & Owned) == 0) return;
                int uc = (int)((uint)cf >> UShift);
                if (s_visitMode == 6) { s_handSum += uc; return; }
                // (what it held of the old container, it holds of the new)
                if ((cf & Held) != 0) { PS(s_handFrom, 4, PL(s_handFrom, 4) - 1); PS(obj, 4, PL(obj, 4) + 1); }
                PS(child, 24, (cf & ~MultMask) | MultOne);
                PS(child, 20, obj);
                if (PLW(child) == obj) PSW(child, 0);
                PS(obj, 24, PL(obj, 24) + uc * UOne);
                PS(s_handFrom, 24, PL(s_handFrom, 24) - uc * UOne);
                return;
            }
            int i = s_visitElem != 0 ? -1 : SlotIndex(obj, slot);
            int bits = i >= 0 ? SlotBits(obj, i) : -1;
            if (s_visitMode == 5)
            {
                // a counted reference of the member's own to a member on its chain: an up-reference from now on
                if (bits != 1 || PL(child, 20) == obj || !OnChain(obj, child)) return;
                if (PL(child, 4) + (-Mem.LoadI32(child) - 2) <= 1) return;
                PS(child, 4, PL(child, 4) - 1);
                SetSlotBits(obj, i, 2);
                if (PLW(child) == obj) PSW(child, 0);
                UpPath(obj, child, 1);
                UpdateHold(child);
                return;
            }
            // 0: the member (obj) is being destroyed
            if (bits == 2) { if (s_cutOn != 0) NoteCut(slot, child); Mem.StoreI32(slot, 0); SetSlotBits(obj, i, 0); UpPath(obj, child, -1); return; }
            // (an outside-counted reference stays in the slot for the release every other reference of the dying
            // object gets; so does one whose kind is not known when the member has no inside count)
            if (bits == 0 || ArenaOf(child) != s_visitArena || PL(child, 4) == 0) return;
            if (s_cutOn != 0) NoteCut(slot, child);
            Mem.StoreI32(slot, 0);
            if (i >= 0) SetSlotBits(obj, i, 0);
            int left = PL(child, 4) - 1;
            PS(child, 4, left);
            OwnerLetGo(child, obj);
            if (left == 0 && Mem.LoadI32(child) == -2) { Kill(child); return; }
            if (PL(child, 20) == 0) Adopt(child);
            UpdateHold(child);
            // (a survivor of the cut that nothing outside refers to may be kept by stale holds of what it owns alone:
            // checked at the drop when the destruction is done, DrainKills)
            if (StickyHolds() && Mem.LoadI32(child) == -2 && (PL(child, 24) & Owned) == 0) QueueCheck(child);
        }

        /// <summary>A copy of an object (MemberwiseClone) has counted each reference it holds: those in slots that can
        /// close a cycle are linked, as the stores they stand for would have been.</summary>
        public static void ArenaLinkContents(int obj)
        {
            int mk = -1;
            if (!ThreadMode()) RuntimeLock(); else mk = LinkLock(0, obj, true);
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj;
            s_visitArena = obj; s_visitMode = 4; s_visitObj = obj;
            int queued = s_linkN;
            ArenaVisitObject(obj);
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so;
            // (the copy joined an arena at the first of its slots that links: its slots are all linked by this visit,
            // so not again as a member that joined with slots already holding references, which gave each store's count
            // back twice and freed what the copy and the original still held: test 180)
            for (int q = queued; q < s_linkN; q++) if (Mem.LoadI32(s_linkQ + 4 * q) == obj) Mem.StoreI32(s_linkQ + 4 * q, 0);
            ArenaLinkJoined();
            DrainKills();
            if (!ThreadMode()) RuntimeUnlock(); else GroupUnlock(mk);
            if (t_backN != 0) AfterStore();
        }

        /// <summary>Takes a member off its arena's ring (ar: its arena, which its record word points to: ArenaOf).</summary>
        static void ArenaTakeOut(int ar, int m)
        {
            int next = PL(m, 12);
            if (next == m) Mem.StoreI32(ar + 4, 0);
            else
            {
                int prev = PL(m, 8);
                PS(prev, 12, next);
                PS(next, 8, prev);
                if (Mem.LoadI32(ar + 4) == m) Mem.StoreI32(ar + 4, next);
            }
            Mem.StoreI32(ar + 8, Mem.LoadI32(ar + 8) - 1);
            Mem.StoreI32(ar + 20, Mem.LoadI32(ar + 20) - 1);
        }

        /// <summary>The arena's record goes with its last member.</summary>
        static void ArenaEmpty(int ar)
        {
            if (Mem.LoadI32(ar + 8) == 0 && Mem.LoadI32(ar) == 0 && (Mem.LoadI32(ar + 12) & ArenaBusy) == 0) { MemStatsRecord(-1); Free(ar, ArenaRecord); }
        }

        // While the finalizers of members that die together run, every reference to one of them is counted, from
        // wherever: the ones their slots held are put back counted, and a finalizer's own stores count as any store
        // (a member that is dying joins no group: ArenaLinkCore). Each has FinBias on its count meanwhile, so no
        // release destroys it a second time. Afterwards the references they hold to each other are taken off the
        // counts (their slots, visited as when they were let go): a count that is left is a reference from where the
        // program can reach it, and then they all live on.
        const int FinBias = 0x10000000;

        static int FinNext(int m, int first, int link) { int n = PL(m, link); return n == first ? 0 : n; }

        static void FinVisit(int first, int link, int mode)
        {
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem;
            for (int m = first; m != 0; m = FinNext(m, link == 12 ? first : 0, link))
            {
                s_visitMode = mode; s_visitObj = m; s_visitElem = 0;
                ArenaVisitObject(m);
            }
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se;
        }

        /// <summary>The finalizers of the members (a list through the prefix word `link`: 8 ending in 0, 12 a ring).
        /// True when one of them was stored where the program can reach it: they are never freed then.</summary>
        static bool FinPhase(int first, int link, int cutBase)
        {
            // (its own code, not FinPhaseCuts: a program without threads carries none of the deferred destruction)
            int end = link == 12 ? first : 0;
            for (int m = first; m != 0; m = FinNext(m, end, link)) { Mem.StoreI32(m, FinBias); PS(m, 24, PL(m, 24) | Dying); }
            int cutEnd = s_cutN;
            CutsBack(cutBase, cutEnd);
            for (int m = first; m != 0; m = FinNext(m, end, link)) ArenaFinalizeObject(m);
            FinVisit(first, link, 10);
            bool back = false;
            for (int m = first; m != 0; m = FinNext(m, end, link)) if (Mem.LoadI32(m) != FinBias) { back = true; break; }
            if (back)
            {
                FinVisit(first, link, 11);
                for (int m = first; m != 0;) { int next = FinNext(m, end, link); Mem.StoreI32(m, -1); m = next; }
                s_cutN = cutBase;
                return true;
            }
            FinVisit(first, link, 12);
            CutsOut(cutBase, cutEnd);
            s_cutN = cutBase;
            for (int m = first; m != 0; m = FinNext(m, end, link)) Mem.StoreI32(m, 0);
            return false;
        }

        /// <summary>FinPhase with the cut references in a block (q, from..to). `unlocked`: run after the runtime lock
        /// was given back (RunDeferredDestroys): the visits take it, the finalizers run without it.</summary>
        static bool FinPhaseCuts(int first, int link, int q, int from, int to, bool unlocked = true, bool prepared = false)
        {
            int end = link == 12 ? first : 0;
            // (prepared: done already, under the lock, FinPrepare)
            if (!prepared) FinPrepare(first, link, q, from, to);
            for (int m = first; m != 0; m = FinNext(m, end, link)) ArenaFinalizeObject(m);
            if (unlocked) RuntimeLock();
            FinVisit(first, link, 10);
            bool back = false;
            for (int m = first; m != 0; m = FinNext(m, end, link)) if (Mem.LoadI32(m) != FinBias) { back = true; break; }
            if (back)
            {
                FinVisit(first, link, 11);
                for (int m = first; m != 0;) { int next = FinNext(m, end, link); Mem.StoreI32(m, -1); m = next; }
                if (unlocked) RuntimeUnlock();
                return true;
            }
            FinVisit(first, link, 12);
            if (unlocked) RuntimeUnlock();
            CutsOut(q, from, to);
            for (int m = first; m != 0; m = FinNext(m, end, link)) Mem.StoreI32(m, 0);
            return false;
        }

        /// <summary>The finalizer phase's first steps: the members marked dying, under the bias, and the cut references
        /// put back counted. With threads, at the deferral, under the runtime lock: a reference put back later, outside
        /// it, may be to an object another thread has freed meanwhile (seen: test 139).</summary>
        static void FinPrepare(int first, int link, int q, int from, int to)
        {
            int end = link == 12 ? first : 0;
            for (int m = first; m != 0; m = FinNext(m, end, link)) { Mem.StoreI32(m, FinBias); PS(m, 24, PL(m, 24) | Dying); }
            CutsBack(q, from, to);
        }

        // ---- With threads, a group's destruction ends after the runtime lock is given back ----
        // Its finalizers run program code, and its releases destroy other objects, whose finalizers do too (a
        // SafeHandle's takes the wait subsystem's lock, which a thread that holds it may be waiting for the runtime
        // lock with: a deadlock, seen). .NET runs no finalizer under a lock of its runtime. So what is left of the
        // destruction once the members are taken out (the finalizers, the clears, the frees) waits in a list of the
        // thread's own and runs when its outermost RuntimeUnlock gives the lock back; the cut references go with it.
        [System.ThreadStatic] static int t_defQ, t_defN, t_defCap, t_defRunning;
        const int DefEntry = 24;

        static void Defer(int first, int link, int ar, bool fin, int cutBase)
        {
            int n = s_cutN - cutBase, cuts = 0;
            if (n > 0) { cuts = Alloc(n * 8); Mem.Copy(cuts, s_cutQ + 8 * cutBase, n * 8); }
            s_cutN = cutBase;
            if (t_defN == t_defCap)
            {
                int cap = t_defCap == 0 ? 8 : t_defCap * 2;
                int nq = Alloc(cap * DefEntry);
                if (t_defQ != 0) { Mem.Copy(nq, t_defQ, t_defN * DefEntry); Free(t_defQ, t_defCap * DefEntry); }
                t_defQ = nq; t_defCap = cap;
            }
            int e = t_defQ + DefEntry * t_defN;
            // (the references the finalizers see, put back now, while the lock is held)
            if (fin) FinPrepare(first, link, cuts, 0, n);
            Mem.StoreI32(e, first); Mem.StoreI32(e + 4, link); Mem.StoreI32(e + 8, ar); Mem.StoreI32(e + 12, fin ? 1 : 0);
            Mem.StoreI32(e + 16, cuts); Mem.StoreI32(e + 20, n);
            t_defN++;
        }

        /// <summary>Called by the outermost RuntimeUnlock (threads): the destructions this thread left for then.</summary>
        public static void RunDeferredDestroys()
        {
            if (t_defN == 0 || t_defRunning != 0) return;
            t_defRunning = 1;
            while (t_defN > 0)
            {
                t_defN--;
                int e = t_defQ + DefEntry * t_defN;
                int first = Mem.LoadI32(e), link = Mem.LoadI32(e + 4), ar = Mem.LoadI32(e + 8), cuts = Mem.LoadI32(e + 16), n = Mem.LoadI32(e + 20);
                bool fin = Mem.LoadI32(e + 12) != 0;
                int end = link == 12 ? first : 0;
                bool back = fin && FinPhaseCuts(first, link, cuts, 0, n, true, prepared: true);
                if (cuts != 0) Free(cuts, n * 8);
                if (!back)
                {
                    for (int m = first; m != 0; m = FinNext(m, end, link)) ArenaClearObject(m);
                    for (int m = first; m != 0;) { int next = FinNext(m, end, link); ArenaFreeObject(m); m = next; }
                }
                // (a whole arena whose member came back keeps its record, busy, for the words that name it; a part's
                // record was dealt with when it was taken out)
                if (link == 12 && !back) { RuntimeLock(); { MemStatsRecord(-1); Free(ar, ArenaRecord); } RuntimeUnlock(); }
            }
            t_defRunning = 0;
        }

        /// <summary>A member without references: it is destroyed, and so is each member that loses its last reference
        /// with it, one after the other (no recursion: a long chain is a long list). First every one of them gives
        /// up its references to other members (their owners are still there to walk through), then each is destroyed
        /// as any object.</summary>
        static void ArenaDestroyFrom(int p, int rest)
        {
            OpStat(22);
            int ar = ArenaOf(p);
            ArenaTakeOut(ar, p);
            // (rest: members that die with it whatever their counts, Dropped; marked and off the ring already)
            PS(p, 12, rest);
            PS(p, 24, PL(p, 24) | Dying);
            int dead = p, done = 0;
            bool fin = Mem.LoadI32(ar + 24) != 0;
            int cutOn = s_cutOn, cutBase = s_cutN;
            s_cutOn = fin ? 1 : 0;
            s_destroying++;
            while (dead != 0)
            {
                int m = dead;
                dead = PL(m, 12);
                Unqueue(m);
                int sa = s_visitArena, sm = s_visitMode, sd = s_visitDead, so = s_visitObj, se = s_visitElem;
                s_visitArena = ar; s_visitMode = 0; s_visitDead = dead; s_visitObj = m; s_visitElem = 0;
                // (what it held of its owner goes back)
                int fl = PL(m, 24);
                if ((fl & Held) != 0)
                {
                    int w = PL(m, 20);
                    PS(m, 24, fl & ~Held);
                    int n = PL(w, 4) - 1;
                    PS(w, 4, n);
                    if (n == 0 && Mem.LoadI32(w) == -2) Kill(w); else UpdateHold(w);
                }
                ArenaVisitObject(m);
                dead = s_visitDead;
                s_visitArena = sa; s_visitMode = sm; s_visitDead = sd; s_visitObj = so; s_visitElem = se;
                PS(m, 8, done);
                done = m;
            }
            s_destroying--;
            s_cutOn = cutOn;
            // then as any object: no longer a member (count zero: being destroyed), its finalizer, its other references
            for (int m = done; m != 0; m = PL(m, 8)) { Mem.StoreI32(m, 0); PS(m, 16, 0); ClearMark(m); }
            // (threads: the rest once the runtime lock is given back, RunDeferredDestroys)
            // (the members are off the arena's ring already: whether its record goes is known now, and what is left
            // needs it no more; it may be merged or freed by others before then)
            if (!SingleThreaded()) { Defer(done, 8, 0, fin, cutBase); ArenaEmpty(ar); return; }
            // A finalizer stored one of them where the program can reach it: they all live on, with what they
            // refer to, and are never freed (what one of several objects that die together can still reach is
            // not followed: section 4.3's group stays whole).
            if (fin && FinPhase(done, 8, cutBase)) { ArenaEmpty(ar); return; }
            for (int m = done; m != 0; m = PL(m, 8)) ArenaClearObject(m);
            for (int m = done; m != 0;)
            {
                int next = PL(m, 8);
                ArenaFreeObject(m);
                m = next;
            }
            ArenaEmpty(ar);
        }

        /// <summary>No outside reference remains: the whole arena goes (its members' inside references are taken out,
        /// then each member's finalizer runs and its other references are released, then the memory is freed).</summary>
        static void ArenaFree(int ar)
        {
            int flags = Mem.LoadI32(ar + 12);
            if ((flags & ArenaBusy) != 0) return;
            Mem.StoreI32(ar + 12, flags | ArenaBusy);
            int first = Mem.LoadI32(ar + 4);
            if (first != 0)
            {
                // (every member's record word straight at the arena: the merged records they pointed to go now)
                int m = first;
                do { ArenaOf(m); Unqueue(m); ClearMark(m); m = PL(m, 12); } while (m != first);
                int sa = s_visitArena, sm = s_visitMode;
                bool fin = Mem.LoadI32(ar + 24) != 0;
                int cutOn = s_cutOn, cutBase = s_cutN;
                s_cutOn = fin ? 1 : 0;
                s_visitArena = ar; s_visitMode = 1;
                m = first;
                do { ArenaVisitObject(m); m = PL(m, 12); } while (m != first);
                s_visitArena = sa; s_visitMode = sm;
                s_cutOn = cutOn;
                // (threads: the rest once the runtime lock is given back, RunDeferredDestroys)
                if (!SingleThreaded()) { Defer(first, 12, ar, fin, cutBase); return; }
                // (a finalizer stored one of them where the program can reach it: as in ArenaDestroyFrom; the
                // arena's record stays, busy, for the words of its members that name it)
                if (fin && FinPhase(first, 12, cutBase)) return;
                m = first;
                do { ArenaClearObject(m); m = PL(m, 12); } while (m != first);
                m = first;
                do
                {
                    int next = PL(m, 12);
                    ArenaFreeObject(m);
                    m = next;
                } while (m != first);
            }
            { MemStatsRecord(-1); Free(ar, ArenaRecord); }
        }

        /// <summary>Array.Copy into an array whose elements can close a cycle: element by element, as stores.</summary>
        public static void ArenaArrayCopy(int src, int si, int dst, int di, int length)
        {
            // (each element's link and unlink take the runtime lock)
            int s = src + 16 + si * 4, d = dst + 16 + di * 4;
            int by = di - si;
            if (src == dst && by != 0 && length > 0 && (by < 0 ? -by : by) < length)
            {
                // A move inside one array (a list's RemoveAt, Insert): the elements that only change place keep their
                // references. The `by` elements written over without being moved lose one, and the `by` elements the
                // move leaves in two places gain one (the caller clears or overwrites one of the two).
                int n = by < 0 ? -by : by;
                int lost = by < 0 ? d : s + length * 4;       // first slot written over and not read
                int twice = by < 0 ? s + (length - n) * 4 : s; // first slot read and not written over
                int olds = Alloc(n * 4);
                for (int k = 0; k < n; k++)
                {
                    Mem.StoreI32(olds + k * 4, Mem.LoadI32(lost + k * 4));
                    int v = Mem.LoadI32(twice + k * 4);
                    Retain(Borrow<object>(v));
                    ArenaLink(v, dst, 0);
                }
                Mem.Copy(d, s, length * 4);
                for (int k = 0; k < n; k++) ArenaUnlink(Mem.LoadI32(olds + k * 4), dst, 0);
                Free(olds, n * 4);
                return;
            }
            bool back = src == dst && si < di;
            for (int k = 0; k < length; k++)
            {
                int i = back ? length - 1 - k : k;
                int v = Mem.LoadI32(s + i * 4);
                Retain(Borrow<object>(v));
                v = ArenaLink(v, dst, 0);
                int old = Mem.LoadI32(d + i * 4);
                Mem.StoreI32(d + i * 4, v);
                ArenaUnlink(old, dst, 0);
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
                ArenaUnlink(old, arr, 0);
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
            if (ex is OutOfMemoryException)
            {
                // The trace needs memory too. There usually is some (one large allocation failed), and then the
                // exception has its trace as any other; with no room for the trace, it has none.
                e._fullTrace = null; e._stackTraceString = null;
                e._throwDepth = Engine.StackDepth();
                if (HasRoom(TraceRoom)) CaptureTrace(e);
            }
            else
            {
                CaptureTrace(e);
                e._throwDepth = Engine.StackDepth();
            }
            // (Exception.TargetSite: the method that threw, if traces hide it)
            e._asyncTop = false;
            if (FramesWanted()) e._throwSite = TakeThrowSite();
            if (FirstChanceWanted()) FirstChance(e);
        }

        /// <summary>What taking a trace allocates at most (its buffer, its string, a thread's refill), with a margin.</summary>
        const int TraceRoom = 131072;

        /// <summary>Whether the heap's top has room for this many bytes, growing the memory if it can.</summary>
        static bool HasRoom(int bytes)
        {
            RuntimeLock();
            bool ok = true;
            if (s_top == 0) InitHeap();
            if ((uint)(s_end - s_top) < (uint)bytes)
            {
                int pages = (s_top + bytes - s_end + 65535) / 65536;
                ok = Mem.Grow(pages) >= 0;
                if (ok) s_end = Mem.Size() * 65536;
            }
            RuntimeUnlock();
            return ok;
        }

        static void CaptureTrace(Exception e)
        {
            var buf = new char[16384];
            int n = Engine.CaptureStack(ArrayData(buf), buf.Length);
            e._fullTrace = n > 0 ? StringOf(buf, n) : null;
            e._stackTraceString = e._fullTrace;
        }

        /// <summary>The stack of the calling code as an exception's trace has it, innermost frame first (for
        /// System.Diagnostics.StackTrace and StackFrame).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static string CurrentTrace()
        {
            var buf = new char[16384];
            int n = Engine.CaptureStack(ArrayData(buf), buf.Length);
            return n > 0 ? StringOf(buf, n) : "";
        }

        /// <summary>A catch clause took the exception: its trace runs from the throw to the catching frame, as in .NET.</summary>
        public static void OnCatch(object ex) => CutTrace(ex, 1);

        /// <summary>A type's static initializer failed: the runtime took the exception below the frame that touched
        /// the type, so its trace ends at the initializer's frame (.cctor), as in .NET: it is the trace of the
        /// TypeInitializationException thrown for it (taken first) that its own ends with, cut off.</summary>
        public static void OnInitializerFailed(object inner, object wrapper)
        {
            // (the frames below the initializer are the wrapper's: the same stack, taken just after; by address, as
            // a program that only throws has no string searching of its own)
            var e = inner as Exception; var w = wrapper as Exception;
            string? full = e?._fullTrace, outer = w?._fullTrace;
            if (full == null || outer == null || outer.Length == 0) return;
            int end = full.Length - outer.Length - 1;
            if (end > 0 && Mem.LoadU16(StringData(full) + 2 * end) == 10) e!._stackTraceString = Prefix(full, end);
        }

        static string Prefix(string s, int n)
        {
            int a = AllocString(n);
            Mem.Copy(a + 16, StringData(s), n * 2);
            return Take<string>(a);
        }

        /// <summary>An async method's MoveNext took the exception (to fault its task): the trace ends at its frame.</summary>
        public static void OnAsyncCatch(object ex)
        {
            CutTrace(ex, 1);
            if (ex is Exception e) e._asyncTop = true;
        }

        static void CutTrace(object ex, int catching)
        {
            if (ex is not Exception e || e._fullTrace == null) return;
            int keep = e._throwDepth - Engine.StackDepth() + catching;
            string full = e._fullTrace;
            if (keep <= 0) { e._stackTraceString = null; return; }
            // (the first `keep` lines; by hand: a program that only throws has no string searching of its own)
            int end = 0, lines = 0;
            for (int d = StringData(full); end < full.Length; end++)
                if (Mem.LoadU16(d + 2 * end) == 10 && ++lines == keep) break;
            e._stackTraceString = end >= full.Length ? full : Prefix(full, end);
        }

        /// <summary>An item of a string.Format the compiler lowered (FormatLowering.cs): as string.Format formats it without a provider.</summary>
        public static string FormatItem(object? arg) => arg is IFormattable f ? f.ToString(null, null) : arg?.ToString() ?? "";

        /// <summary>Exception.GetStackTrace (manifest substitution): the trace captured when it was thrown.</summary>
        public static string StackTraceOf(Exception e) => e._stackTraceString ?? e._fullTrace ?? "";

        /// <summary>StackTrace.ToString(TraceFormat) (manifest substitution): the frames' text (StackTrace.Mono.cs).</summary>
        public static string EmptyTrace(System.Diagnostics.StackTrace t, int format) => t.Cs2WasmText(format);

        /// <summary>StackTrace.ToString(TraceFormat, StringBuilder) (manifest substitution; Exception.SetCurrentStackTrace):
        /// the same text, appended. (.NET's own reads each frame's MethodBase: asking frames for their methods gives every
        /// method of the program a reflection record and an invoke thunk, and a frame without one wrote nothing.)</summary>
        public static void TraceTextInto(System.Diagnostics.StackTrace t, int format, System.Text.StringBuilder sb) => sb.Append(t.Cs2WasmText(format));

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
        // CS2WASM_DEADLOCK_DUMP=1 (a diagnosis, at compile time): a thread that waits seconds for the runtime lock says
        // which thread holds it and throws, with its stack (RuntimeFns.EmitThreadSupport calls this only then).
        public static void LockTimeout(int owner) =>
            throw new System.InvalidOperationException("deadlock dump: thread " + Rt.ThreadId() + " waited for the runtime lock, held by thread " + owner);
        internal const int StackSize = 1 << 20;
        static int s_nextId = 1;
        static int s_spawned;
        /// <summary>True once a worker exists: waits use real time and wake on s_generation.</summary>
        public static bool Spawned => Atomic.Load32(ref s_spawned) != 0;
        /// <summary>Bumped whenever a lock is released or a monitor pulsed; waiters sleep on it.</summary>
        internal static int s_generation;

        internal static int NewId() => Atomic.Add32(ref s_nextId, 1) + 1;

        // finished threads' stacks and thread-local blocks, for reuse: a list through the blocks' trailer
        // ([0] finished, set by __thread_entry when it is done with both; [4] next; [8] the stack)
        static int s_finished;

        /// <summary>A stack and a zeroed thread-local block: a finished thread's when one is free.</summary>
        static unsafe void TakeBlocks(out int stack, out int tls)
        {
            int size = Rt.TlsSize();
            Rt.RuntimeLock();
            int prev = 0, cur = s_finished;
            while (cur != 0 && Atomic.Load32(ref *(int*)(cur + size)) == 0) { prev = cur; cur = Mem.LoadI32(cur + size + 4); }
            if (cur != 0)
            {
                int next = Mem.LoadI32(cur + size + 4);
                if (prev == 0) s_finished = next; else Mem.StoreI32(prev + size + 4, next);
            }
            Rt.RuntimeUnlock();
            if (cur != 0)
            {
                stack = Mem.LoadI32(cur + size + 8);
                tls = cur;
                Mem.Fill(tls, 0, size + 16);
                return;
            }
            stack = Rt.Alloc(StackSize);
            tls = Rt.Alloc(size + 16);
        }

        /// <summary>A thread ending (on its own stack): its blocks go to the list, reused once __thread_entry marks them.</summary>
        static void GiveBlocks(int stack, int tls)
        {
            int size = Rt.TlsSize();
            Mem.StoreI32(tls + size + 8, stack);
            Rt.RuntimeLock();
            Mem.StoreI32(tls + size + 4, s_finished);
            s_finished = tls;
            Rt.RuntimeUnlock();
        }

        internal static void SpawnCore(System.Threading.Thread t)
        {
            // (a module built for an engine without threads: as .NET says there)
            if (Rt.NoThreads()) throw new System.PlatformNotSupportedException("Threads are not supported on this platform.");
            TakeBlocks(out int stack, out int tls);
            // (the groups' locks: before the first thread that may take one starts)
            if (Rt.ThreadMode()) Rt.InitGroupLocks();
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
            var t = Rt.Take<System.Threading.Thread>(threadAddr);
            System.Threading.Thread.SetCurrent(t);
            try { t.StartCallback(); }
            finally
            {
                Atomic.Add32(ref s_live, -1);
                System.Threading.Thread.OnThreadExiting(t);
                System.Threading.Thread.SetCurrent(null);
                // the thread's statics die with it (as .NET's become unreachable)
                Rt.ReleaseThreadStatics();
                if (Rt.ThreadMode()) Rt.EndGroupLocks();
                Rt.EndThreadCache();
                GiveBlocks(t._stack, t._tls);
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
            if (Atomic.Load32(ref s_parkRequested) == 0 || Rt.ThreadId() == 1) return;
            Atomic.Add32(ref s_parked, 1);
            while (Atomic.Load32(ref s_parkRequested) != 0) Atomic.Wait32(ref s_parkRequested, 1, -1);
            Atomic.Add32(ref s_parked, -1);
        }

        internal static void WaitGeneration(int g)
        {
            SafePoint();
            // bounded, so timers and deadlines are rechecked: 1 ms
            Block(ref s_generation, g, 1000000);
        }

        /// <summary>Waiting for a lock is no safe point: code takes locks while it holds references it borrowed (the
        /// compiler lets a borrow last until the thread's next safe point, FieldBorrows.cs), so the thread keeps its
        /// epoch while it waits. It still parks when the engine asks (a waiting thread touches no memory).</summary>
        internal static void WaitGenerationHolding(int g)
        {
            ParkPoint();
            BlockHolding(ref s_generation, g, 1000000);
        }

        internal static void ParkPoint()
        {
            if (Atomic.Load32(ref s_parkRequested) == 0 || Rt.ThreadId() == 1) return;
            Atomic.Add32(ref s_parked, 1);
            while (Atomic.Load32(ref s_parkRequested) != 0) Atomic.Wait32(ref s_parkRequested, 1, -1);
            Atomic.Add32(ref s_parked, -1);
        }

        static int s_spin;
        /// <summary>A spin lock's pause (SpinWait: its Yield, Sleep(0) and Sleep(1)): no safe point, as a lock's wait.</summary>
        internal static void SpinPause(int ms)
        {
            ParkPoint();
            if (ms > 0) BlockHolding(ref s_spin, 0, ms * 1000000L);
        }

        /// <summary>Block without becoming quiescent (a lock's wait).</summary>
        internal static void BlockHolding(ref int word, int expected, long ns)
        {
            if (MayBlock()) Atomic.Wait32(ref word, expected, ns);
            else
            {
                long end = ns < 0 ? long.MaxValue : Engine.MonotonicNs() + ns;
                while (Atomic.Load32(ref word) == expected && Engine.MonotonicNs() < end) { }
            }
        }

        static int s_mainBlocks = -1;

        /// <summary>Whether this thread may block in a wait: a worker always; the main thread when its host says so.</summary>
        internal static bool MayBlock()
        {
            if (Rt.ThreadId() != 1) return true;
            if (s_mainBlocks < 0) s_mainBlocks = Engine.MainCanBlock();
            return s_mainBlocks == 1;
        }

        /// <summary>Waits while the word holds expected, at most ns nanoseconds (-1: no limit); quiescent meanwhile (a
        /// waiting thread holds only counted references, so deferred destruction does not wait for it). A thread that
        /// may not block spins.</summary>
        internal static void Block(ref int word, int expected, long ns)
        {
            if (MayBlock()) Atomic.Wait32(ref word, expected, ns);
            else
            {
                long end = ns < 0 ? long.MaxValue : Engine.MonotonicNs() + ns;
                while (Atomic.Load32(ref word) == expected && Engine.MonotonicNs() < end) { }
            }
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

    /// <summary>Collection expressions' spread elements (`[.. xs]`): each element of the spread added in order, as C#
    /// defines it.</summary>
    public static class CollectionExpressions
    {
        public static void AddRange<T>(System.Collections.Generic.ICollection<T> c, System.Collections.Generic.IEnumerable<T> items) { foreach (var x in items) c.Add(x); }
        public static void AddSpan<T>(System.Collections.Generic.ICollection<T> c, System.ReadOnlySpan<T> items) { foreach (var x in items) c.Add(x); }
        public static void AddSpanMutable<T>(System.Collections.Generic.ICollection<T> c, System.Span<T> items) { foreach (var x in items) c.Add(x); }
    }

    public static class ThrowHelpers
    {
        /// <summary>Activator.CreateInstance&lt;T&gt;() of a T without a parameterless constructor.</summary>
        public static void NoDefaultConstructor(string typeName)
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new System.MissingMethodException(SR.Format(SR.Activator_CannotCreateInstance, typeName, SR.Arg_NoDefCTorWithoutTypeName)); Rt.ReleaseEpoch();
            throw e;
        }
        /// <summary>The exception a failed static initializer leaves: thrown now and at every later access.</summary>
        public static object TypeInitFailed(string typeName, object inner) =>
            new System.TypeInitializationException(typeName, inner as System.Exception);
        public static void NullReference()
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new NullReferenceException(); Rt.ReleaseEpoch();
            throw e;
        }
        public static void IndexOutOfRange()
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new IndexOutOfRangeException(); Rt.ReleaseEpoch();
            throw e;
        }
        public static void Overflow()
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new OverflowException(); Rt.ReleaseEpoch();
            throw e;
        }
        public static void DivideByZero()
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new DivideByZeroException(); Rt.ReleaseEpoch();
            throw e;
        }
        // The exception for a failed allocation cannot always be allocated then: one is made when the memory first
        // grows (as .NET keeps one ready), and thrown when making another fails. A program whose memory never grew
        // and has no room for it at all ends, as the process does under .NET.
        static OutOfMemoryException? s_oom;
        static int s_oomBusy;

        internal static void ReserveOutOfMemory()
        {
            if (s_oom != null || s_oomBusy != 0) return;
            s_oomBusy = 1;
            s_oom = new OutOfMemoryException();
            s_oomBusy = 0;
        }

        public static void OutOfMemory()
        {
            if (s_oomBusy != 0)
            {
                // (no room for the exception itself)
                if (s_oom == null) Rt.Unreachable();
                // (this is the call inside the one below, which made no exception)
                s_oomBusy = 0;
                Rt.ReleaseEpoch();
                throw s_oom!;
            }
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch();
            s_oomBusy = 1;
            var e = new OutOfMemoryException();
            s_oomBusy = 0;
            Rt.ReleaseEpoch();
            throw e;
        }
        public static void SwitchExpression()
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new System.Runtime.CompilerServices.SwitchExpressionException(); Rt.ReleaseEpoch();
            throw e;
        }
        public static void InvalidCast(object o, string target)
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new InvalidCastException("Unable to cast object of type '" + Rt.TypeName(o) + "' to type '" + target + "'."); Rt.ReleaseEpoch();
            throw e;
        }
        public static void ArgumentOutOfRange(string name)
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new ArgumentOutOfRangeException(name); Rt.ReleaseEpoch();
            throw e;
        }
        /// <summary>An [UnsafeAccessor] over a type the program does not have (as when .NET cannot load it).</summary>
        public static void TypeLoad(string typeName)
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new TypeLoadException(SR.Format(SR.TypeLoad_ResolveType, typeName)); Rt.ReleaseEpoch();
            throw e;
        }
        public static void ArgumentNull(string name)
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new ArgumentNullException(name); Rt.ReleaseEpoch();
            throw e;
        }
        public static void InvalidOperation(string msg)
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new InvalidOperationException(msg); Rt.ReleaseEpoch();
            throw e;
        }
        /// <summary>What the sandbox does not have (processes, raw sockets, the registry: section 8), where a program
        /// reaches it: .NET's own exception for a platform that lacks something, as its browser target throws.</summary>
        public static void PlatformNotSupported()
        {
            Rt.HoldEpoch(); var e = new PlatformNotSupportedException(); Rt.ReleaseEpoch();
            throw e;
        }

        public static void NotSupported()
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new NotSupportedException(); Rt.ReleaseEpoch();
            throw e;
        }
    }
}
