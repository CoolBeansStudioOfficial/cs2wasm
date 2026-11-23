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
        // freed blocks, a ring; or the empty pages, [4] alone), [12] the first block never used (0: none; in a page
        // among those with freed blocks: its blocks in use when PageAlloc last passed it over, plus one),
        // [16] blocks in use, [20] size class * 2 + (1: it is the size's current page or among its pages with freed
        // blocks).
        // Which page with freed blocks is used again (PageAlloc, RipePages): a page a few blocks of which are free
        // gives a few blocks, so a size whose objects die one by one all over its pages (the particles of a list,
        // 400 of 19,400 a frame) had every frame's new objects spread over all of them, nine to a page, and a walk
        // of the list in its order went all over memory (the churn scene's loop 88 us a frame, 62 with the same
        // objects in order). So: one of the pages whose first block was freed last, when a quarter of it is free
        // (freed in one go, a subtree let go: still in the cache, and room for what is made next: as before); else,
        // of the pages whose first block was freed longest ago, one that is nearly empty, or one that is no longer
        // being emptied and has a quarter free; else an empty page, cut in order, while memory need not grow for
        // it. A page still being emptied is left until it is empty or nearly so.
        // A page nothing is left in goes back to the empty pages, for any size. Pages come four at a time from
        // 64 KB of memory at a multiple of 64 KB, noted in a map, so a small block that is not in a page (what is
        // left over when a large block is cut) is told from one that is, and kept on the old lists by size.
        // ------------------------------------------------------------------
        // The block of each size that was freed last is kept aside (s_hot, a word per size) and is the next one of
        // its size to be allocated: code that makes an object, uses it and lets it go, over and over, then reads and
        // writes that one word, and none of the page (each of which the next allocation would wait for: the
        // list of the page, its count of blocks in use). The page still counts the block as in use.
        const int PageSize = 16384, PageHead = 24;
        static int s_cur, s_avail, s_freePages, s_hot, s_takes, s_ringPages;

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
                    if (RipePages())
                    {
                        // Of the first few pages (the ring's front: those whose first block was freed longest ago),
                        // one that is nearly empty; else one a quarter of which is free and that has lost no block
                        // since it was last looked at here ([12]: its blocks in use then, plus one). A page that is
                        // still being emptied is left to empty (the blocks of a list whose objects die one by one:
                        // an empty page is cut in order again, a page used again while they die gets the new objects
                        // a few at a time); a page looked at and left goes to the back. When there is none, an
                        // empty page, unless memory would have to grow for it: then the front page as it is, as
                        // before.
                        int cap = (PageSize - PageHead) / size, take = 0;
                        // (first the pages at the back, whose first block was freed last: one a quarter of which is
                        // free already was freed in one go, a subtree let go, and is still in the cache; the tree
                        // scene's frames, with other programs at work on the machine, took a tenth longer without)
                        int back = Mem.LoadI32(q + 8);
                        // (HoldPages: the size's count of the times it needed a page, this one included; and whether
                        // the pages with freed blocks are past their bound)
                        int now = 0;
                        bool over = false;
                        if (HoldPages())
                        {
                            int takesSlot = s_takes + c * 4;
                            now = Mem.LoadI32(takesSlot) + 1;
                            Mem.StoreI32(takesSlot, now);
                            over = (uint)(s_ringPages * PageSize) > ((uint)(s_top - s_runLo) >> 2) * 3;
                        }
                        for (int k = 0; k < BackLook; k++)
                        {
                            // (HoldPages: a page that was passed over below is at the back too, and is not one whose
                            // first block was freed last: [12] says so)
                            if (HoldPages() && Mem.LoadI32(back + 12) != 0) break;
                            if (Mem.LoadI32(back + 16) <= cap - (cap >> StillShift)) { take = back; break; }
                            if (back == q) break;
                            back = Mem.LoadI32(back + 8);
                        }
                        for (int k = 0; k < RipeLook && take == 0; k++)
                        {
                            int inUse = Mem.LoadI32(q + 16);
                            if ((!HoldPages() || over) && inUse <= (cap >> RipeShift)) { take = q; break; }
                            int seen = Mem.LoadI32(q + 12);
                            if (HoldPages())
                            {
                                // [12]: the blocks in use when the page was last seen to have lost one, plus one, in
                                // 12 bits, and above them the size's count of takes then
                                bool same = seen != 0 && inUse == (seen & 0xFFF) - 1;
                                if (same && inUse <= cap - (cap >> StillShift) && (over || ((now - (int)((uint)seen >> 12)) & 0xFFFFF) >= StillTakes)) { take = q; break; }
                                if (!same) Mem.StoreI32(q + 12, (now << 12) | (inUse + 1));
                            }
                            else
                            {
                                if (seen != 0 && inUse == seen - 1 && inUse <= cap - (cap >> StillShift)) { take = q; break; }
                                Mem.StoreI32(q + 12, inUse + 1);
                            }
                            q = Mem.LoadI32(q + 4);
                            Mem.StoreI32(availSlot, q);
                        }
                        if (take == 0 && s_freePages == 0 && (uint)(s_end - s_top) < 3 * 65536) take = Mem.LoadI32(availSlot);
                        q = take;
                        if (q != 0) Mem.StoreI32(q + 12, 0);
                    }
                    if (q != 0)
                    {
                        PageUnlink(availSlot, q);
                        Mem.StoreI32(curSlot, q);
                        continue;
                    }
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

        /// <summary>Whether a size's pages with freed blocks are used again only once they are nearly empty, or no
        /// longer being emptied with a quarter free, as long as an empty page can be had instead without growing
        /// memory (PageAlloc); CS2WASM_NO_RIPE_PAGES=1: the page whose first block was freed last, whatever is free
        /// in it, as before.</summary>
        [Intrinsic] static extern bool RipePages();
        /// <summary>Whether a page is told to be "no longer being emptied" by the size's own clock, and the pages at the
        /// back apart from the ones passed over (CS2WASM_NO_HOLD_PAGES=1: as before, see below). Before: a page
        /// counted as no longer emptied when it had lost no block between two looks at it, and a look is an
        /// allocation that needs a page. A program that allocates a frame's objects and then frees (the churn scene:
        /// 400 particles made, then the loop in which 400 die) looks at all of a size's pages with no free in
        /// between, so every page a quarter free was taken, and each frame's new particles lay between old ones:
        /// tools/churn-layout.cs, of the 19,000 steps of the list's walk 16,000 went to a line that was not the next
        /// one. And a page passed over goes to the ring's back, where the look for a page "freed in one go" then
        /// found it. Now: no longer emptied means no block lost while the size needed StillTakes pages (its count of
        /// takes is kept with the mark, in [12]); the look at the back stops at a page that was passed over. What is
        /// held back is bounded: the pages with freed blocks, of all sizes (s_ringPages), by three quarters of what
        /// the heap has handed out, past which a page with a quarter free counts as before; and no memory is grown
        /// for an empty page (as before).</summary>
        [Intrinsic] static extern bool HoldPages();
        const int StillTakes = 4;
        /// <summary>How many of a size's pages with freed blocks an allocation looks at from the front; nearly empty: no
        /// more than 1/2^RipeShift of its blocks in use; free enough once it is no longer being emptied, or at the back:
        /// 1/2^StillShift of them; how many it looks at from the back, first.</summary>
        const int RipeLook = 8, RipeShift = 3, StillShift = 2, BackLook = 3;

        /// <summary>Takes a page out of its size's pages with freed blocks (a ring: [4] next, [8] previous).</summary>
        static void PageUnlink(int slot, int pg)
        {
            if (HoldPages()) s_ringPages--;
            int next = Mem.LoadI32(pg + 4);
            if (next == pg) { Mem.StoreI32(slot, 0); return; }
            int prev = Mem.LoadI32(pg + 8);
            Mem.StoreI32(prev + 4, next);
            Mem.StoreI32(next + 8, prev);
            if (Mem.LoadI32(slot) == pg) Mem.StoreI32(slot, next);
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
                // it was full: among its size's pages with freed blocks from now on (a ring), at the back: the
                // pages before it had their first block freed earlier
                int head = Mem.LoadI32(availSlot);
                if (HoldPages()) s_ringPages++;
                if (head == 0) { Mem.StoreI32(pg + 4, pg); Mem.StoreI32(pg + 8, pg); Mem.StoreI32(availSlot, pg); }
                else
                {
                    int tail = Mem.LoadI32(head + 8);
                    Mem.StoreI32(tail + 4, pg); Mem.StoreI32(pg + 8, tail);
                    Mem.StoreI32(pg + 4, head); Mem.StoreI32(head + 8, pg);
                    // (CS2WASM_NO_RIPE_PAGES=1: at the front, the next page to be used again)
                    if (!RipePages()) Mem.StoreI32(availSlot, pg);
                }
                Mem.StoreI32(pg + 20, fl | 1);
            }
            else if (used == 0 && Mem.LoadI32(s_cur + c * 4) != pg)
            {
                // nothing left in it: an empty page, for any size
                PageUnlink(availSlot, pg);
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
            // (each size's count of the times it needed a page: HoldPages)
            if (HoldPages()) { s_takes = b; b += Classes * 4; }
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
        /// <summary>CS2WASM_NO_TAKE_IN=1 (a diagnosis, at compile time): the check at the drop tries what the top owns
        /// alone, as before 2026-11-21 (Dropped).</summary>
        [Intrinsic] static extern bool NoTakeIn();
        /// <summary>CS2WASM_NO_FRESH_JOIN=1 (at compile time): an object that joins an arena for the store that links it is
        /// asked whether it is above its container, as any member is (LinkMembers, TakeOwnership).</summary>
        [Intrinsic] static extern bool NoFreshJoin();
        /// <summary>CS2WASM_NO_ARRAY_REPLACE=1 (at compile time): a new array stored where the array it was copied from
        /// was is linked element by element, and the old one's items handed over at its unlink (ReplaceArray).</summary>
        [Intrinsic] static extern bool NoArrayReplace();
        /// <summary>CS2WASM_NO_PINNED_SKIP=1 (at compile time): a pinned object stored into a plain object's slot takes
        /// the lock and ArenaLinkCore's questions before it is left a counted reference (ArenaLink).</summary>
        [Intrinsic] static extern bool NoPinnedSkip();
        /// <summary>The cycle-capable slots of an object of the type, a nibble each from the lowest: the slot's index
        /// among the words after the header, plus one (the slot is at 8 + 4 * nibble); 0 for a type whose slots the
        /// word cannot say (an array, more than eight slots, a slot past the fifteenth word).</summary>
        [Intrinsic] static extern int ArenaSlots(int typeId);
        /// <summary>CS2WASM_NO_STORE_WAITS=1 (at compile time): a plain object that joins for a store into a member's
        /// own slot is that member's at once, whatever its own slots hold (LinkMembers).</summary>
        [Intrinsic] static extern bool NoStoreWaits();
        /// <summary>CS2WASM_NO_EARLY_GIVE_BACK=1 (at compile time): the count a store took on a member is always given
        /// back after the store is written (EarlyGiveBack).</summary>
        [Intrinsic] static extern bool NoEarlyGiveBack();
        /// <summary>An array's rank and the size of its elements, by its address (Cs2WasmArrays.RankOf and ElementSize
        /// take an Array; no value of that type is made from an address here, which the compiler would read as an
        /// array of any type come from nowhere).</summary>
        [Intrinsic] static extern int ArrayRankAt(int array);
        [Intrinsic] static extern int ArrayElementSizeAt(int array);
        /// <summary>CS2WASM_NO_SLOT_WALK=1 (at compile time): Reevaluate visits the object by its type, not by its slot
        /// bits. (The walk was off from round 2 of the rebuild to round 10, for one long frame of the strings
        /// scene that turned out to be the host's own collection, and because it gained nothing then; with the
        /// node's add written out, Rt.AddNode, it is what is left of that step: docs/AUDIT.md A3.5, round 10.)</summary>
        [Intrinsic] static extern bool NoSlotWalk();
        /// <summary>CS2WASM_SLOT_WALK_BREAK=1 (at compile time, tools/slot-walk-breaks.sh): the walk leaves the
        /// member's first counted slot out.</summary>
        [Intrinsic] static extern bool SlotWalkBreak();
        // CS2WASM_DROP_STATS=1 (a diagnosis, at compile time): the check at the drop counts what it does and says so
        [Intrinsic] static extern bool DropStats();
        /// <summary>CS2WASM_STALE_HOLDS=1 (at compile time, an experiment): an owned member keeps a hold once made, without
        /// threads (docs/AUDIT.md, holds kept once made: 90% fewer hold changes in the self-hosted compiler, its time the
        /// same, its peak memory 12% more, which is unexplained, so not the default).</summary>
        [Intrinsic] static extern bool StaleHolds();
        /// <summary>CS2WASM_ARENA_REPORT=1 (a diagnosis, at compile time): GC.Collect prints every arena still there, its
        /// members, and those of them referred to from outside (what keeps it).</summary>
        [Intrinsic] static extern bool ArenaReport();
        public static bool ArenaReportOn() => ArenaReport();
        static int s_repEv, s_repEvN;
        static void RepEv(int kind, int a, int b, int c)
        {
            if (s_repEv == 0) s_repEv = Alloc(16 * 8192);
            int i = s_repEvN & 8191; s_repEvN++;
            Mem.StoreI32(s_repEv + 16 * i, kind); Mem.StoreI32(s_repEv + 16 * i + 4, a); Mem.StoreI32(s_repEv + 16 * i + 8, b); Mem.StoreI32(s_repEv + 16 * i + 12, c);
        }
        static int s_repQ, s_repN, s_repCap, s_repTarget, s_repHits, s_repHitN, s_repRefused, s_repRefusedFlags;
        static void ReportWho(int head, int target)
        {
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem;
            s_repTarget = target;
            if (s_repHits == 0) s_repHits = Alloc(16 * 12);
            s_repHitN = 0;
            int m = head, k = 0;
            do
            {
                s_visitMode = 20; s_visitObj = m; s_visitElem = 0;
                ArenaVisitObject(m);
                m = PL(m, 12); k++;
            } while (m != head && k < 100000);
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se;
            for (int h = 0; h < s_repHitN; h++)
            {
                int o = Mem.LoadI32(s_repHits + 12 * h);
                ChkSay("      from " + TypeName(Borrow<object>(o)) + " @" + o + " slot+" + Mem.LoadI32(s_repHits + 12 * h + 4) + " bits " + Mem.LoadI32(s_repHits + 12 * h + 8) + " its owner " + PL(o, 20) + " flags " + PL(o, 24) + " note " + PLM(o) + " count " + Mem.LoadI32(o));
            }
        }
        static void ReportNote(int ar)
        {
            if (s_repN == s_repCap)
            {
                int cap = s_repCap == 0 ? 256 : s_repCap * 2;
                int nq = Alloc(cap * 4);
                if (s_repQ != 0) { Mem.Copy(nq, s_repQ, s_repN * 4); Free(s_repQ, s_repCap * 4); }
                s_repQ = nq; s_repCap = cap;
            }
            Mem.StoreI32(s_repQ + 4 * s_repN, ar);
            s_repN++;
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void ReportForget(int ar) { if (ArenaReport()) ReportForgetNow(ar); }
        static void ReportForgetNow(int ar) { for (int i = 0; i < s_repN; i++) if (Mem.LoadI32(s_repQ + 4 * i) == ar) Mem.StoreI32(s_repQ + 4 * i, 0); }
        public static void ArenaReportNow()
        {
            if (!ArenaReport()) return;
            ChkSay("checks at the drop not made (arena busy or shared): " + s_repRefused + ", flags " + s_repRefusedFlags);
            int from = s_repEvN > 8192 ? s_repEvN - 8192 : 0;
            for (int e = from; e < s_repEvN; e++)
            {
                int i = e & 8191, k = Mem.LoadI32(s_repEv + 16 * i);
                ChkSay("ev " + e + " " + (k == 1 ? "check" : k == 2 ? "ownerless" : k == 3 ? "adopted" : "queued") + " " + Mem.LoadI32(s_repEv + 16 * i + 4) + " " + Mem.LoadI32(s_repEv + 16 * i + 8) + " " + Mem.LoadI32(s_repEv + 16 * i + 12));
            }
            for (int i = 0; i < s_repN; i++)
            {
                int ar = Mem.LoadI32(s_repQ + 4 * i);
                if (ar == 0 || Mem.LoadI32(ar + 16) != 0) continue;
                int n = Mem.LoadI32(ar + 8);
                if (n == 0) continue;
                ChkSay("arena " + ar + ": " + n + " members, " + Mem.LoadI32(ar) + " referred to from outside, flags " + Mem.LoadI32(ar + 12));
                int head = Mem.LoadI32(ar + 4), m = head, k = 0;
                if (head == 0) continue;
                do
                {
                    int c = Mem.LoadI32(m);
                    if (c < -2 || n <= 200 || (PL(m, 20) == 0 && (PLM(m) & 1) != 0))
                    {
                        ChkSay("  " + TypeName(Borrow<object>(m)) + " @" + m + " outside " + (-c - 2) + " inside " + PL(m, 4) + " owner " + PL(m, 20) + " flags " + PL(m, 24) + " note " + PLM(m));
                        if (PL(m, 20) == 0 && PL(m, 4) != 0 && (PLM(m) & 1) != 0) ReportWho(head, m);
                    }
                    m = PL(m, 12);
                    k++;
                } while (m != head && k < 100000);
            }
        }
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
        // (ArenaDestroyPlain by its passes, nanoseconds, and the members it destroyed)
        static long s_dsKill, s_dsZero, s_dsClear, s_dsFree, s_dsKilled;
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
        const int OpN = 44;
        const string OpNames = "link,link-plain,link-same,link-core,join,merge,joined-visit,unlink,unlink-plain,unlink-up,unlink-inside,leave-visit,leave,reevaluate,retain-slow,release-slow,update-hold,handover,link-at,unlink-at,take-ownership,unlink-top,destroy,link-up,hold-change,hold-up,retain-fast,release-fast,array-replace,join-owned,fresh-join,array-grow,give-back-early,link-pinned,give-back-last,join-fused,join-node,join-pair,node-up,node-add,plain-parents,part-left,part-link";
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
        static void DsDrop(int members, long walk, long clear, long free)
        {
            if (s_dsBuf == 0) s_dsBuf = Alloc(4096);
            int at = 0;
            DsNum(ref at, "group drop: members ", members); DsNum(ref at, " walk ", walk); DsNum(ref at, " clear ", clear); DsNum(ref at, " free ", free);
            Mem.StoreU16(s_dsBuf + 2 * at, 10); at++;
            Engine.Write(s_dsBuf, at);
            // (and the paths' counts so far: between two drops of a program that builds and drops, one build's)
            DsPrint();
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
            if (s_dsKilled != 0)
            {
                DsNum(ref at, "destroyed at a store or release: members ", s_dsKilled); DsNum(ref at, " us: visits ", s_dsKill / 1000); DsNum(ref at, " counts ", s_dsZero / 1000);
                DsNum(ref at, " clears ", s_dsClear / 1000); DsNum(ref at, " frees ", s_dsFree / 1000);
                Mem.StoreU16(s_dsBuf + 2 * at, 10); at++;
            }
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
        /// allows (growing a shared memory is slow; growing it by a page at a time, for every refill, slower still).
        /// (Threads: the other threads' instances learn the new size later, Wasm/MemSync.cs.)</summary>
        static void GrowFor(int need)
        {
            int pages = (need + 65535) / 65536;
            int more = Math.Max(pages, Math.Max(Mem.Size() / 8, 256));
            if (Mem.Grow(more) < 0 && Mem.Grow(pages) < 0) ThrowHelpers.OutOfMemory();
            // (with room again: the exception a later failure throws is made now, while it can be)
            s_end = Mem.Size() * 65536;
            MemStatsGrow();
            // (with other threads, not here: OomLater)
            if (SingleThreaded()) ThrowHelpers.ReserveOutOfMemory(); else Atomic.Store32(ref s_oomWanted, 1);
        }

        // The exception a later failure throws is made when the memory has grown (ThrowHelpers.ReserveOutOfMemory).
        // GrowFor runs under the heap's lock, the innermost of the runtime's (GroupLocks.cs), and making an exception
        // runs the program's code: constructors, a static's initialization, the resources' lookup, which take locks
        // of their own. With other threads running, the thread that grew the memory waited for such a lock under the
        // heap's while its holder waited for the heap's: every thread stopped, for good, at the program's first
        // growth of the memory when it came while threads were starting. Test 217, 3 to 5 runs of 480 (no output, a
        // timeout); test 133 checked, 5 loaded runs of 400 on the homelab once other changes made its first growth
        // come at another time. So with threads the growth only notes that the exception is wanted, and the next
        // object allocated the slow way by a thread that holds no lock of the runtime's makes it (AllocObjectSlow).
        static int s_oomWanted;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void OomLater()
        {
            if (t_lkN != 0 || RuntimeLockHeld() || Atomic.Load32(ref s_heapOwner) == ThreadId()) return;
            if (Atomic.CompareExchange32(ref s_oomWanted, 1, 0) != 1) return;
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
        /// <summary>CS2WASM_MEMSTATS=1: a call of MsHash with the argument (1: the object's first); else nothing.</summary>
        [Intrinsic] static extern void MemStatsHash(int first);
        static long s_msHashCalls, s_msHashFirst;
        public static void MsHash(int first) { s_msHashCalls++; s_msHashFirst += first; }
        const int MsCls = 6, MsTypes = 16384;
        const int TPages = 0, TEmptyPages = 1, THuge = 2, THugeGran = 3, TFreeRuns = 4, TFreeGran = 5, TFreeMax = 6,
            TOther = 7, TOddBlocks = 8, TOddBytes = 9, TMemberWords = 10, TArenaRecords = 11, TFreePageList = 12, TRunLeft = 13,
            THugeObj = 14, THugeArena = 15, TLeafGran = 16, TMwLive = 17, TBadTags = 18, TObjects = 19, TWords = 24, THashed = 34, THugeBins = 40, TCount = 56;
        static int s_ms, s_msRec, s_msGrows;
        static bool s_msFinal;
        static int MsClsAt(int c) => s_ms + c * MsCls * 4;
        static int MsTotAt(int k) => s_ms + Classes * MsCls * 4 + k * 4;
        static int MsTypeAt(int i) => s_ms + Classes * MsCls * 4 + TCount * 4 + i * 20;
        static int MsBuf => MsTypeAt(MsTypes);
        const int MsArea = Classes * MsCls * 4 + TCount * 4 + MsTypes * 20 + 2048;
        static int s_msAt;

        static void MsAdd(int addr, int v) => Mem.StoreI32(addr, Mem.LoadI32(addr) + v);

        static void MsType(int typeId, int bytes, int cyc, int hashed)
        {
            int i = (int)((uint)(typeId * -1640531535) >> 18) & (MsTypes - 1);
            for (int n = 0; n < MsTypes; n++, i = (i + 1) & (MsTypes - 1))
            {
                int e = MsTypeAt(i), k = Mem.LoadI32(e);
                if (k == 0) { Mem.StoreI32(e, typeId + 1); k = typeId + 1; }
                if (k == typeId + 1) { MsAdd(e + 4, bytes); MsAdd(e + 8, 1); MsAdd(e + 12, cyc); MsAdd(e + 16, hashed); return; }
            }
        }

        /// <summary>An object at a block's start (its first word: a count, or the mark before a cycle-capable one's
        /// words), tallied by type; whether it is one.</summary>
        static int MsObject(int b, int bytes)
        {
            int w = Mem.LoadI32(b);
            if (w == 0) return 0;
            int o = w <= PrefixMark ? b + ArenaPrefix : b;
            MsType(Mem.LoadI32(o + 4), bytes, w <= PrefixMark ? 1 : 0, (Mem.LoadI32(o + 8) & 0x1FFFFFFF) != 0 ? 1 : 0);
            MsAdd(MsTotAt(TObjects), 1);
            {
                // (the header's third word: an identity hash made, pinned, marked, finalized)
                int hw = Mem.LoadI32(o + 8);
                if ((hw & 0x1FFFFFFF) != 0) MsAdd(MsTotAt(THashed), 1);
                if (hw < 0) MsAdd(MsTotAt(THashed + 1), 1);
                if ((hw & 0x40000000) != 0) MsAdd(MsTotAt(THashed + 2), 1);
                if ((hw & 0x20000000) != 0) MsAdd(MsTotAt(THashed + 3), 1);
            }
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
                    // (without threads [12] is no block: what PageAlloc saw in use when it passed the page over)
                    int bump = k == 1 ? 0 : Mem.LoadI32(pg + 12), last = bump != 0 ? bump : pg + PageSize;
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
                    // (memory that is no object, by its size: 16, 64, 256, 1024 KB and above)
                    if (r == 0) { int bin = n < 16 ? 0 : n < 64 ? 1 : n < 256 ? 2 : n < 1024 ? 3 : 4; MsAdd(MsTotAt(THugeBins + bin), 1); MsAdd(MsTotAt(THugeBins + 5 + bin), n); }
                    if (r == 0 && n >= 16 && s_msFinal)
                    {
                        MsText("memstats block "); MsNum(n); MsText(" KB:");
                        for (int i = 0; i < 12; i++) { MsText(" "); MsNum(Mem.LoadI32(a + 4 * i)); }
                        MsEnd();
                    }
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
            MsText("memstats "); MsText(what); MsText(" objects "); MsNum(MsTot(TObjects)); MsText(" hashed "); MsNum(MsTot(THashed));
            MsText(" pinned "); MsNum(MsTot(THashed + 1)); MsText(" marked "); MsNum(MsTot(THashed + 2)); MsText(" finalized "); MsNum(MsTot(THashed + 3));
            MsText(" | identity hashes asked "); MsNum(s_msHashCalls); MsText(", made "); MsNum(s_msHashFirst);
            MsEnd();
            MsText("memstats "); MsText(what); MsText(" huge blocks that are no object, by size (count/KB):");
            for (int i = 0; i < 5; i++) { MsText(" "); MsNum(MsTot(THugeBins + i)); MsText("/"); MsNum(MsTot(THugeBins + 5 + i)); }
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
            for (int n = 0; n < (what == "now" ? MsTypes : 60); n++)
            {
                int best = -1; long bb = 0;
                for (int i = 0; i < MsTypes; i++)
                {
                    int e = MsTypeAt(i);
                    if (Mem.LoadI32(e) > 0 && (uint)Mem.LoadI32(e + 4) > bb) { bb = (uint)Mem.LoadI32(e + 4); best = e; }
                }
                if (best < 0) break;
                int id = Mem.LoadI32(best) - 1;
                MsText("memstats "); MsText(what); MsText(" type "); MsKb("", bb); MsText(" "); MsNum(Mem.LoadI32(best + 8)); MsText(" "); MsNum(Mem.LoadI32(best + 12)); MsText(" "); MsNum(Mem.LoadI32(best + 16)); MsText(" ");
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
            s_msFinal = true;
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

        /// <summary>FreeObject's usual case for an object whose size the compiler knows, written where the object dies
        /// (WasmModule.ExpandLeafReleases puts it there only in a program without threads, not in a checked build or
        /// with the share check, for a size s that is a multiple of 4 and no more than 128; the release has already
        /// set the object's count word to zero): the block is kept aside for the next allocation of its size, the one
        /// that was there goes back to its page.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static void FreeFixed(int addr, int s)
        {
            int hotSlot = s_hot + s;
            int old = Mem.LoadI32(hotSlot);
            Mem.StoreI32(hotSlot, addr);
            if (old != 0) FreeSmallOld(old, s);
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
            if (ThreadMode() && s_oomWanted != 0) OomLater();
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
            if (ThreadMode() && length == 0) PinEmpty(a);
            return a;
        }

        /// <summary>Threads: an array with no element, of a type whose elements can close a cycle, is pinned as it is made
        /// (ArenaPin: it never joins a group). Nothing can be on a cycle through an object with no slot, and such an
        /// array is one several threads may count (`List&lt;T&gt;.s_emptyArray`, an `Array.Empty`): so a group that one
        /// thread's objects make never takes in an object another thread can reach (Sharing.MemberIdShared, Arenas.LocalScc).</summary>
        static void PinEmpty(int a)
        {
            if (!NoEmptyPin() && SccOf(Mem.LoadI32(a + 4)) != 0) Mem.StoreI32(a + 8, Mem.LoadI32(a + 8) | int.MinValue);
        }

        /// <summary>CS2WASM_NO_LOCAL_GROUPS=1, or CS2WASM_EXP_NO_EMPTY_PIN=1 (a test of the tests, unsound with the
        /// groups that are one thread's): an array with no element is not pinned as it is made (PinEmpty).</summary>
        [Intrinsic] static extern bool NoEmptyPin();

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
            if (ThreadMode() && length == 0) PinEmpty(a);
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
            MemStatsHash((v & mask) == 0 ? 1 : 0);
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

        // (the objects the references a function just returned point into, each counted until the caller has
        // counted it: ReturnKeep)
        [System.ThreadStatic] static int t_ret0, t_ret1, t_ret2, t_ret3, t_retN;

        /// <summary>A function of the program returns a span or a `ref` (Compiler/StorageKeeps.cs): the object it
        /// points into is counted here before the function lets go of its locals and temporaries, which may
        /// be all that held it (`Span<T> F() { var a = new T[3]; return a; }`); the caller counts it for itself
        /// at the call (KeepStorage) and gives this count back (ReturnDone). Four at a time (a ref struct with
        /// several spans); one more lets the oldest go.</summary>
        public static void ReturnKeep(int addr)
        {
            int o = ObjectOf(addr);
            if (o == 0) return;
            Retain(Borrow<object>(o));
            int old;
            switch (t_retN++ & 3)
            {
                case 0: old = t_ret0; t_ret0 = o; break;
                case 1: old = t_ret1; t_ret1 = o; break;
                case 2: old = t_ret2; t_ret2 = o; break;
                default: old = t_ret3; t_ret3 = o; break;
            }
            if (old != 0) Release(Borrow<object>(old));
        }

        /// <summary>The caller has counted what came out of the call: ReturnKeep's counts go.</summary>
        public static void ReturnDone()
        {
            if (t_retN == 0) return;
            t_retN = 0;
            int a = t_ret0, b = t_ret1, c = t_ret2, d = t_ret3;
            t_ret0 = 0; t_ret1 = 0; t_ret2 = 0; t_ret3 = 0;
            if (a != 0) Release(Borrow<object>(a));
            if (b != 0) Release(Borrow<object>(b));
            if (c != 0) Release(Borrow<object>(c));
            if (d != 0) Release(Borrow<object>(d));
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
        /// <summary>Threads: the type's group of classes is one whose groups are each one thread's (Arenas.LocalScc).</summary>
        [Intrinsic] static extern bool SccLocal(int typeId);
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
                { MemStatsRecord(-1); ReportForget(ar); Free(ar, ArenaRecord); }
                ar = f;
            }
        }

        static int NewArena()
        {
            int ar = Alloc(ArenaRecord); MemStatsRecord(1);
            if (ArenaReport()) ReportNote(ar);
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
            ArenaJoinOnly(ar, obj);
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

        /// <summary>ArenaJoin without the note that the object's slots are still to be linked (the caller links them).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void ArenaJoinOnly(int ar, int obj)
        {
            // (exchanged, not stored: another thread that holds a reference may be counting it, without the lock, as
            // the plain object it is until here; its exchange then fails and it counts the member)
            int c;
            // (threads: the group's stripe first, GroupLocks.cs, while the object is still plain and so locked by its
            // own: a thread that sees it a member then finds the stripe this thread holds, and waits)
            OpStat(4);
            if (ThreadMode()) PS(obj, LockWord, Mem.LoadI32(ar + 28) + 1);
            do { c = Atomic.Load32(ref Mem.RefI32(obj)); } while (Atomic.CountCompareExchange32(ref Mem.RefI32(obj), c, -(c + 2)) != c);
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
        }

        // (the objects that joined an arena and whose slots are still to be linked)
        [Intrinsic] static int s_linkQ, s_linkN, s_linkCap;

        /// <summary>Links what the objects that just joined an arena hold in their slots, and what those hold, in turn
        /// (a list, not a nest: a long chain joins without a deep stack).</summary>
        static void ArenaLinkJoined()
        {
            int jv = s_joinVisit;
            s_joinVisit = 1;
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
            s_joinVisit = jv;
            if (jv == 0 && s_reevN != 0) LinkWaiting();
        }

        /// <summary>The slots left to a waiting owner, LinkMembers, in the order they were linked: a value now above
        /// the container that refers to it, the container's slot an up-reference; else the container owns it, as it
        /// would have at the link. (Not written into ArenaLinkJoined, which every link that joins an object runs.)</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void LinkWaiting()
        {
            for (int q = 0; q < s_reevN; q++)
            {
                int o = Mem.LoadI32(s_reevQ + 8 * q), v = Mem.LoadI32(s_reevQ + 8 * q + 4);
                if (o == 0) continue;
                if (Mem.LoadI32(o) >= -1 || Mem.LoadI32(v) >= -1 || ((PL(o, 24) | PL(v, 24)) & Dying) != 0) continue;
                if (PL(v, 20) != 0) { if (PL(o, 20) != 0) ReevaluateWaiting(o, v); continue; }
                if (PL(o, 20) != 0 && OnChain(o, v)) { ReevaluateWaiting(o, v); continue; }
                if (PL(o, 20) != 0 && !NoWaitingTop() && WaitingTop(q, o, v)) { ReevaluateWaiting(o, v); continue; }
                if (PLW(v) == o) PSW(v, 0);
                TakeOwnership(v, o);
            }
            s_reevN = 0;
            s_waitSlot = 0;
        }

        /// <summary>CS2WASM_NO_WAITING_TOP=1 (at compile time): LinkWaiting takes its pairs strictly in their order.</summary>
        [Intrinsic] static extern bool NoWaitingTop();

        /// <summary>LinkWaiting, a pair (o, v) that would make o the owner of v: when v itself waits, further down the
        /// list, to own the member at the top of o's chain, that pair is settled first; true when v is then above
        /// o (o's reference to it is an up-reference, the caller's Reevaluate). A node's list that code still
        /// refers to while the node's first child is put into it (`e.Kids.Add(Make(e))` where the list is counted
        /// across the call: any program with a second place that stores the field, which ends the borrow) waits
        /// for its owner as the node does, and the pairs are (child, node), (node, list) in the order of the
        /// visits. Taken in that order the child, whose chain ends at the list, nothing's yet, became the owner of
        /// the node, and the node's own slot an up-reference to its list: every node of the tree hung below its
        /// first child, the list on top, kept by holds alone; a subtree stored over was then never destroyed
        /// before its tree went (tools/tree-assign.cs: 121, 161, 201 nodes for .NET's 121, 121, 121).</summary>
        static bool WaitingTop(int q, int o, int v)
        {
            int t = o;
            for (int k = 0; (PL(t, 24) & Owned) != 0; t = PL(t, 20)) if (++k > ChainMax) return false;
            if (t == v || PL(t, 20) != 0 || Mem.LoadI32(t) >= -1 || (PL(t, 24) & Dying) != 0) return false;
            for (int r = q + 1; r < s_reevN; r++)
            {
                if (Mem.LoadI32(s_reevQ + 8 * r) != v || Mem.LoadI32(s_reevQ + 8 * r + 4) != t) continue;
                // (that pair, as its turn would take it: v has no owner and t none, so v owns t)
                Mem.StoreI32(s_reevQ + 8 * r, 0);
                if (PLW(t) == v) PSW(t, 0);
                TakeOwnership(t, v);
                return PL(t, 20) == v && OnChain(o, v);
            }
            return false;
        }

        // (the slot of the store under way whose value was left to a waiting owner, LinkMembers, with its object and
        // the value: the slot holds its old value until ArenaLink returns. Each thread's own, as the list they go
        // with, s_reevQ: plain statics, they were written by two threads' links of two groups at once, and a link
        // then read another thread's slot, or its own gone)
        [Intrinsic] static int s_waitSlot, s_waitObj, s_waitVal;

        /// <summary>LinkWaiting: the container's reference to the value may be an up-reference now. Reevaluate reads
        /// the container's slots; the slot of the store under way does not hold the value yet.</summary>
        static void ReevaluateWaiting(int o, int v)
        {
            if (s_waitSlot != 0 && o == s_waitObj && v == s_waitVal)
            {
                int i = SlotIndex(o, s_waitSlot);
                if (i >= 0) ReevaluateSlot(o, i, v);
                return;
            }
            Reevaluate(o);
        }

        // (ArenaLinkJoined's visits, and the members to Reevaluate after them)
        [Intrinsic] static int s_joinVisit, s_reevQ, s_reevN, s_reevCap;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Waiting(int val, int container)
        {
            if (PLW(val) == 0) { ClearMark(val); PSW(val, container); }
            ReevaluateLater(container, val);
        }

        static void ReevaluateLater(int container, int val)
        {
            if (s_reevN == s_reevCap)
            {
                int cap = s_reevCap == 0 ? 16 : s_reevCap * 2;
                int nq = Alloc(cap * 8);
                if (s_reevQ != 0) { Mem.Copy(nq, s_reevQ, s_reevN * 8); Free(s_reevQ, s_reevCap * 8); }
                s_reevQ = nq; s_reevCap = cap;
            }
            Mem.StoreI32(s_reevQ + 8 * s_reevN, container);
            Mem.StoreI32(s_reevQ + 8 * s_reevN + 4, val);
            s_reevN++;
        }

        /// <summary>CS2WASM_NO_MERGE_REPOINT=1 (at compile time): the members of a small arena merged into another keep
        /// their record words until ArenaOf is asked of each (ArenaMerge).</summary>
        [Intrinsic] static extern bool NoMergeRepoint();
        const int MergeRepoint = 8;

        /// <summary>The smaller arena becomes part of the larger; returns the survivor.</summary>
        static int ArenaMerge(int a, int b)
        {
            OpStat(5);
            if (Mem.LoadI32(a + 8) < Mem.LoadI32(b + 8)) { int t = a; a = b; b = t; }
            int ha = Mem.LoadI32(a + 4), hb = Mem.LoadI32(b + 4);
            // (a few members: each one's record word straight at the survivor now, as ArenaOf would set it the first
            // time it is asked; b's record then goes here, if no older record points to it, and no link or count of
            // these members takes the long way for a record merged since. A tree built from the leaves up makes a
            // group of five for each node with children, the node, its list, the array and the first child with its
            // list, merged into the tree's as the node's parent field is linked: 3,124 a build in the scene tree of
            // tools/frames.mjs.)
            // (threads: in a group that is one thread's alone, LocalStripe: nothing else reads the records)
            if ((!ThreadMode() || hb != 0 && SccLocal(Mem.LoadI32(hb + 4))) && !NoMergeRepoint() && hb != 0 && Mem.LoadI32(b + 8) <= MergeRepoint)
            {
                int m = hb, moved = 0;
                do { if (PL(m, 16) == b) { PS(m, 16, a); moved++; } m = PL(m, 12); } while (m != hb);
                Mem.StoreI32(b + 20, Mem.LoadI32(b + 20) - moved);
                Mem.StoreI32(a + 20, Mem.LoadI32(a + 20) + moved);
            }
            // (threads: b's members take a's stripe; this thread holds both)
            if (ThreadMode() && hb != 0 && !SccLocal(Mem.LoadI32(hb + 4)) && Mem.LoadI32(a + 28) != Mem.LoadI32(b + 28))
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
            if (Mem.LoadI32(b + 20) == 0) { MemStatsRecord(-1); ReportForget(b); Free(b, ArenaRecord); }
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
        // Far: a slot past the sixteenth word of the member (not an array) was linked: an inside reference no bits
        // tell of (it stays set). A member with neither Elems nor Far has all its linked references in the slots
        // its bits say (ArenaDestroyPlain).
        const int Owned = 1, Held = 2, Dying = 4, MultOne = 8, MultMask = 0x78, Queued = 0x80, Elems = 0x100, Far = 0x200, UOne = 0x400, UShift = 10;
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

        /// <summary>CS2WASM_NO_OWNED_CHAIN=1 (at compile time): a reference to a member on the storing object's chain is
        /// an up-reference whatever the links of the chain are (OnChainOwned).</summary>
        [Intrinsic] static extern bool NoOwnedChain();

        /// <summary>OnChain by links that are owned ones, each member up to the target owned by the next: what a new
        /// up-reference asks. A member whose owner let it go keeps the owner while it holds it (it has an
        /// up-reference below it and something else refers to it: a child taken out of its parent's list that a
        /// local still refers to). An up-reference made through that link then kept the hold for good: the child,
        /// its parent field set to a member above its old place (`moved.Parent = to`, to the parent of the node it
        /// was taken from) and then put into that one's list, never got the list's array for its owner (it had one,
        /// the old array, which it held), and held its old array, the old array its list, the list its node, from
        /// inside the new place: when the old node was replaced, no count of it reached zero, the check at the drop
        /// found the child's hold a reference from outside what the node owns, and the node with all it owns stayed
        /// until the group went. In the scene tree of tools/frames.mjs one replaced subtree in twenty-five (the
        /// move's target is the old node's parent one time in twenty-five): 30,660 entities for 19,531 reachable
        /// after 500 frames. Refused here, the reference is a counted one: the old up-reference goes at the unlink
        /// that follows the store, the hold with it, the member has no owner, and the container that takes it next
        /// owns it and makes the reference an up-reference then (TakeOwnership, Reevaluate), as for a move to any
        /// other place.</summary>
        static bool OnChainOwned(int from, int target)
        {
            if (NoOwnedChain()) return OnChain(from, target);
            int v = from;
            for (int k = 0; k <= ChainMax; k++)
            {
                if (v == target) return true;
                if ((PL(v, 24) & Owned) == 0) return false;
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
                if (!HoldSettled(v)) UpdateHold(v);
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
                // (or a top whose number of references from outside came to zero as their holders died, DyingNote)
                else if ((StickyHolds() ? (PLM(w) & 1) == 0 : (PLM(w) & 3) == 3) && DropCandidate(w)) Dropped(w);
            }
        }

        /// <summary>A member to check at the drop once the operation under way is done (a destruction's survivor).</summary>
        static void QueueCheck(int w)
        {
            int fl = PL(w, 24);
            if ((fl & (Dying | Queued)) != 0) return;
            KillPush(w);
            PS(w, 24, fl | Queued);
            if (ArenaReport()) RepEv(4, w, 0, 0);
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
            if (ArenaReport()) RepEv(3, v, p, PL(v, 20));
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
            if ((PL(m, 24) & ~(Elems | Far)) != 0 || PL(m, 20) != 0) return;
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
            do { c = Atomic.Load32(ref Mem.RefI32(m)); } while (Atomic.CountCompareExchange32(ref Mem.RefI32(m), c, -c - 2) != c);
            if (!ThreadMode()) MemberWordsFree(m);
            int total = Mem.LoadI32(ar) - 1;
            Mem.StoreI32(ar, total);
            if (total != 0) return;
            if (Mem.LoadI32(ar + 8) == 0) ArenaEmpty(ar); else ArenaFree(ar);
        }

        /// <summary>The member has an owner now: a counted reference in one of its own slots to a member that is on its
        /// chain becomes an up-reference (a parent set before the child was put in the parent's list).</summary>
        static void ReevaluateSlot(int obj, int i, int child)
        {
            // a counted reference of the member's own to a member on its chain: an up-reference from now on
            if (child == 0 || Mem.LoadI32(child) >= -1) return;
            if (SlotBits(obj, i) != 1 || PL(child, 20) == obj || !OnChain(obj, child)) return;
            if (PL(child, 4) + (-Mem.LoadI32(child) - 2) <= 1) return;
            PS(child, 4, PL(child, 4) - 1);
            SetSlotBits(obj, i, 2);
            if (PLW(child) == obj) PSW(child, 0);
            // (a count of the child's that waits for the store to be written may be its last from outside)
            if (obj == s_lastC) LastGiveBack(obj, child, t_backN != 0);
            UpPath(obj, child, 1);
            UpdateHold(child);
        }

        static void Reevaluate(int v)
        {
            OpStat(13);
            // (its counted slots are those its bits say, the first sixteen words: read here, with no visit of the
            // object by its type. An array has none, its elements are never up-references. In the scene tree of
            // tools/frames.mjs a node that takes its place is looked at four times: 14,000 visits a build)
            if ((!ThreadMode() || SccLocal(Mem.LoadI32(v + 4))) && !NoSlotWalk())
            {
                if ((PL(v, 24) & Elems) != 0) return;
                int w = PL(v, 28) & 0x55555555;
                if (SlotWalkBreak()) w &= w - 1;
                for (int k = 0; w != 0; k++, w = (int)((uint)w >> 2))
                    if ((w & 1) != 0) ReevaluateSlot(v, k, Mem.LoadI32(v + 12 + 4 * k));
                return;
            }
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

        /// <summary>A new array (`to`: a plain object, so each of its slots holds a counted reference) is stored into
        /// the member's slot that holds the array it was copied from (`old`): a list that grows. When the old array
        /// then has nothing left that refers to it (no code, no other member: only the slot, which this store writes
        /// over, and the holds of its own items), it is destroyed at the unlink that follows the store, and nothing
        /// can read it again. So the items change hands in one pass here: the new array joins the arena and is the
        /// container's, each item is the new array's (its owner, the hold it had, the up-references that pass it), the
        /// old array's reference to it is the new one's, and the count the new array's slot held is given back. The
        /// old array is left with no element, for the unlink to destroy.
        /// The same end as the long way: the new array's elements linked one by one as it joins (each a second
        /// container's reference to the item, with a waiting owner in side words of its own), the counts given back
        /// after the store (a release each), the items handed over at the unlink (Handover: two visits), and the old
        /// array's references taken out as it is destroyed (a visit). In the scene tree of tools/frames.mjs that was
        /// 1.6 ms of a 4.2 ms build: 3,906 lists that grow from four to eight.
        /// False (nothing done) unless all of it holds: the old array's elements are exactly the new one's (the rest
        /// of the new one empty), each a member the old array owns, once; what Handover asks (the up-references that
        /// pass the old array are all its items'); no check at the drop has a note on any of them.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool ReplaceArray(int to, int container, int slot)
        {
            if (NoArrayReplace() || ThreadMode() && !SccLocal(Mem.LoadI32(container + 4))) return false;
            int old = Mem.LoadI32(slot);
            if (old == 0 || Mem.LoadI32(old) != -2) return false;
            int of = PL(old, 24);
            if ((of & (Elems | Owned | MultMask | Dying | Queued)) != (Elems | Owned | MultOne) || PL(old, 20) != container) return false;
            // (the same array type, of one reference an element; the new one not pinned)
            if (Mem.LoadI32(old + 4) != Mem.LoadI32(to + 4) || Mem.LoadI32(to + 8) < 0) return false;
            // (the container's arena is the old array's and its items': a linked reference is between members of one)
            int ar = ArenaOf(container);
            // (neither with a finalizer running: such an object joins nothing, ArenaLinkCore)
            if ((Mem.LoadI32(ar + 12) & ArenaBusy) != 0 || ((PL(container, 24) | PL(to, 24)) & Dying) != 0) return false;
            if ((s_destroying | s_trial | s_joinVisit | s_visitElem) != 0 || ArenaReport()) return false;
            if (ArrayRankAt(to) != 1 || ArrayElementSizeAt(to) != 4) return false;
            int n = Mem.LoadI32(old + 12), tn = Mem.LoadI32(to + 12);
            if (tn < n) return false;
            int s = old + 16, d = to + 16;
            int items = 0, sumU = 0, held = 0, last = 0;
            bool notes = (ThreadMode() ? t_failedLocal : s_failedTops) != 0;
            if (notes && PLM(old) != 0) return false;
            for (int k = 0; k < n; k++)
            {
                int v = Mem.LoadI32(s + 4 * k);
                if (v != Mem.LoadI32(d + 4 * k)) return false;
                if (v == 0) continue;
                // (a member: the new array's slot counts it from outside)
                int c = Mem.LoadI32(v);
                if (c > -3) return false;
                int vf = PL(v, 24);
                if ((vf & (Owned | MultMask | Dying)) != (Owned | MultOne) || PL(v, 20) != old) return false;
                if (notes && PLM(v) != 0) return false;
                items++;
                sumU += (int)((uint)vf >> UShift);
                if ((vf & Held) != 0) held++;
                if (c == -3) last++;
            }
            for (int k = n; k < tn; k++) if (Mem.LoadI32(d + 4 * k) != 0) return false;
            // (every linked element of the old array is one of these; Handover's condition; and the old array is
            // referred to by the container's slot and its items' holds, nothing else: it dies at the unlink)
            if (items == 0 || PL(old, 28) != items || (int)((uint)of >> UShift) != sumU || PL(old, 4) != 1 + held) return false;
            // (the arena keeps a member that code refers to: none of this is its last)
            int tc = Mem.LoadI32(to);
            if (Mem.LoadI32(ar) - last + (tc > 1 ? 1 : 0) <= 0) return false;
            OpStat(28);
            ArenaJoinOnly(ar, to);
            LinkMembers(to, container, slot, ar, true);
            for (int k = 0; k < n; k++)
            {
                int v = Mem.LoadI32(s + 4 * k);
                if (v == 0) continue;
                Mem.StoreI32(s + 4 * k, 0);
                Mem.StoreI32(v, Mem.LoadI32(v) + 1);
                PS(v, 20, to);
            }
            if (last != 0) Mem.StoreI32(ar, Mem.LoadI32(ar) - last);
            PS(old, 28, 0);
            PS(old, 24, of & (UOne - 1));
            PS(old, 4, 1);
            PS(to, 28, items);
            PS(to, 24, PL(to, 24) | Elems | (sumU << UShift));
            PS(to, 4, PL(to, 4) + held);
            // (the new array holds the container as the old one did, before the old one's hold goes back at its
            // destruction; then each item, one outside reference fewer)
            if (!HoldSettled(to)) UpdateHold(to);
            for (int k = 0; k < n; k++)
            {
                int v = Mem.LoadI32(d + 4 * k);
                if (v != 0 && !HoldSettled(v)) UpdateHold(v);
            }
            return true;
        }

        /// <summary>rt.array_grow: ArenaGrowArray in a program with arrays that can be on a cycle, 0 in any other (and
        /// with CS2WASM_NO_ARRAY_GROW=1 at compile time): RuntimeGen.cs.</summary>
        [Intrinsic] static extern int ArrayGrow(int container, int old, int to, int n);

        /// <summary>List&lt;T&gt;.Capacity's setter, before its Array.Copy (an edit of List.cs, the manifest): the list's
        /// new array takes the first n items of the old one and the old one's place in the list, in one step
        /// (ArenaGrowArray). True when done: the setter returns (its local still counts the new array, as it would
        /// after the store). False: nothing done, the setter copies and stores as upstream.</summary>
        public static bool GrowInto<T>(object list, T[] old, T[] fresh, int n)
        {
            if (typeof(T).IsValueType) return false;
            return ArrayGrow(AddressOf(list), AddressOf(old), AddressOf(fresh), n) != 0;
        }

        /// <summary>A list grows: `to` is its new array (a plain object, new and empty, its one count the caller's), `old`
        /// the array in the container's slot, whose first n elements the caller is about to copy into `to` before it
        /// stores `to` over `old`. When the old array would then die at that store's unlink (as ReplaceArray asks: no
        /// code and no other member refers to it, only the slot and the holds of its own items), the three steps are
        /// one here: the new array joins the arena as the container's, in the slot; each item moves (its reference out of the old array into the new, its owner the new
        /// array; its counts, its hold and the up-references that pass it are what they were, on the new array now);
        /// the old array, with no element and no reference, is destroyed.
        /// The same end as the copy (a count on each item from outside, which with an up-reference below it holds its
        /// owners up the chain), the store (ReplaceArray: the counts given back, the holds let go) and the unlink: in
        /// between, no code runs that could see the items counted twice. In the scene tree of tools/frames.mjs a
        /// growth from four to eight was 13 hold changes, 223 ns; 3,906 of them a build.
        /// 0 (nothing done) unless all of it holds: what ReplaceArray asks of the old array and its items (each of the
        /// first n a member the old array owns, once; no linked element past them; the up-references that pass the
        /// old array all its items'; no note of a check at the drop on any), and the container's slot found by its
        /// bits. Returns 1 when done. The caller's count on `to` stays, an outside reference until the setter returns
        /// (as after the store), but `to` does not hold the container for it meanwhile, as a member with
        /// up-references below it does for a reference from elsewhere: the reference is the local of a method of the
        /// container's own, which its caller keeps for the call, and nothing runs before the local is let go (the
        /// hold would be made here and undone there, up the owners as far as they go).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static int ArenaGrowArray(int container, int old, int to, int n)
        {
            if (ThreadMode() && (container == 0 || !SccLocal(Mem.LoadI32(container + 4)))) return 0;
            if (old == 0 || to == 0 || n <= 0 || Mem.LoadI32(old) != -2 || Mem.LoadI32(to) != 1 || Mem.LoadI32(container) >= -1) return 0;
            int of = PL(old, 24);
            if ((of & (Elems | Owned | MultMask | Dying | Queued)) != (Elems | Owned | MultOne) || PL(old, 20) != container) return 0;
            // (the same array type, of one reference an element; the new one not pinned)
            if (Mem.LoadI32(old + 4) != Mem.LoadI32(to + 4) || Mem.LoadI32(to + 8) < 0) return 0;
            int cf = PL(container, 24);
            if ((cf & (Dying | Elems)) != 0) return 0;
            int ar = ArenaOf(container);
            if ((Mem.LoadI32(ar + 12) & ArenaBusy) != 0) return 0;
            if ((s_destroying | s_trial | s_joinVisit | s_visitElem | s_linkN | s_killN | t_backN) != 0 || ArenaReport()) return 0;
            if (ArrayRankAt(to) != 1 || ArrayElementSizeAt(to) != 4) return 0;
            if (n > Mem.LoadI32(old + 12) || n > Mem.LoadI32(to + 12)) return 0;
            // (the container's slot that holds the old array as a linked reference)
            int slot = 0, sw = PL(container, 28) & 0x55555555;
            for (int k = 0; sw != 0; k++, sw = (int)((uint)sw >> 2))
                if ((sw & 1) != 0 && Mem.LoadI32(container + 12 + 4 * k) == old) { slot = container + 12 + 4 * k; break; }
            if (slot == 0) return 0;
            int s = old + 16, d = to + 16;
            int items = 0, sumU = 0, held = 0;
            bool notes = (ThreadMode() ? t_failedLocal : s_failedTops) != 0;
            if (notes && PLM(old) != 0) return 0;
            for (int k = 0; k < n; k++)
            {
                if (Mem.LoadI32(d + 4 * k) != 0) return 0;
                int v = Mem.LoadI32(s + 4 * k);
                if (v == 0) continue;
                if (Mem.LoadI32(v) >= -1) return 0;
                int vf = PL(v, 24);
                if ((vf & (Owned | MultMask | Dying)) != (Owned | MultOne) || PL(v, 20) != old) return 0;
                if (notes && PLM(v) != 0) return 0;
                items++;
                sumU += (int)((uint)vf >> UShift);
                if ((vf & Held) != 0) held++;
            }
            // (every linked element of the old array is one of these; Handover's condition; and the old array is
            // referred to by the container's slot and its items' holds, nothing else)
            if (items == 0 || PL(old, 28) != items || (int)((uint)of >> UShift) != sumU || PL(old, 4) != 1 + held) return 0;
            OpStat(31);
            // (threads: a group that is one thread's, LocalStripe: counted as held, as a link's; given back at the end,
            // what is taken in between is given back by what took it)
            if (ThreadMode()) LkPush(LocalStripe); else RuntimeLock();
            // (the new array: a member the caller's local refers to from outside; what the old array held of the
            // container, it holds)
            ArenaJoinOnly(ar, to);
            PS(to, 20, container);
            PS(to, 24, PL(to, 24) | Owned | MultOne | Elems | (sumU << UShift) | (of & Held));
            PS(to, 4, 1 + held);
            PS(to, 28, items);
            for (int k = 0; k < n; k++)
            {
                int v = Mem.LoadI32(s + 4 * k);
                if (v == 0) continue;
                Mem.StoreI32(d + 4 * k, v);
                Mem.StoreI32(s + 4 * k, 0);
                PS(v, 20, to);
            }
            Mem.StoreI32(slot, to);
            // (the old array: no element, no up-reference below it, no hold, no reference: destroyed as at the unlink
            // of the store, its owner kept while it is)
            PS(old, 28, 0);
            PS(old, 24, of & (UOne - 1) & ~Held);
            PS(old, 4, 0);
            // (ArenaDestroyFrom for a member that refers to no member, holds nothing and is in no list, where no
            // member has a finalizer: off the ring, no longer a member, what else it holds released, freed)
            if (ThreadMode() ? SccLocal(Mem.LoadI32(old + 4)) && Mem.LoadI32(ar + 24) == 0 : SingleThreaded() && Mem.LoadI32(ar + 24) == 0)
            {
                ArenaTakeOut(ArenaOf(old), old);
                Mem.StoreI32(old, 0); PS(old, 16, 0); ClearMark(old);
                ArenaClearObject(old);
                ArenaFreeObject(old);
            }
            else ArenaDestroyFrom(old, 0);
            if (s_killN != 0) DrainKills();
            if (ThreadMode()) GroupUnlock(t_lkN - 1); else RuntimeUnlock();
            return 1;
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
            if (ArenaReport()) RepEv(2, old, container, (fl & Held) != 0 ? 1 : 0);
        }

        /// <summary>CS2WASM_NO_SHED_HOLDS=1 (at compile time): a container kept by the holds of its own items stays
        /// with all its items (ShedHolds).</summary>
        [Intrinsic] static extern bool NoShedHolds();

        /// <summary>A container (an array, a node) that its owner and the code have let go, kept only by the holds
        /// of items of its own that something else still refers to (a member with an up-reference below it that
        /// another than its owner refers to holds its owner): `g.A = null` where one cell of the array is also in
        /// `g.One`, each cell referring back to g. Until 2026-11-23 the array stayed, and with it every other
        /// cell, until the held one was let go (three cells of four for .NET's none: test 274). Each held item
        /// has a container waiting to own it (the first other member that took a reference to it, Owners), and
        /// when that one is the container's own owner or below it, every member the item and what it owns refer
        /// up to is above the waiting container too: the item changes owner now, as it would at the container's
        /// destruction (Adopt), its hold given back, the up-references that pass it counted on the members from
        /// its new owner up to the old one's owner in place of the old one. With no hold left the container has
        /// no reference and the caller destroys it, with the items nothing else refers to.
        /// Nothing done unless all of it holds: the holds are all its references; every item is owned once and
        /// none refers up to the container itself (told by the counts, as Handover: the up-references that pass
        /// the container are all its items'); no check at the drop has a note on any of them; without threads.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void ShedHolds(int a)
        {
            if (NoShedHolds() || ThreadMode() && !SccLocal(Mem.LoadI32(a + 4)) || (s_destroying | s_trial | s_joinVisit | s_visitElem | s_linkN) != 0) return;
            int af = PL(a, 24);
            if ((af & (Owned | Dying | Queued | Far)) != 0 || Mem.LoadI32(a) != -2) return;
            // (the owner it still holds: the place its items are under)
            int up = PL(a, 20);
            if (up == 0 || Mem.LoadI32(up) >= -1 || (PL(up, 24) & Dying) != 0) return;
            if ((ThreadMode() ? t_failedLocal : s_failedTops) != 0 && PLM(a) != 0) return;
            int ar = ArenaOf(a);
            if (Mem.LoadI64(ar + 12) != 0) return;
            bool array = (af & Elems) != 0;
            if (array && ArrayElementSizeAt(a) != 4) return;
            // (its items: an array's elements, of any rank, or the slots its bits say hold a counted reference)
            int rank = array ? ArrayRankAt(a) : 1;
            int first = array ? a + (rank == 1 ? 16 : (16 + 4 * rank + 7) / 8 * 8) : a + 12, count = array ? Mem.LoadI32(a + 12) : 16, bits = array ? 0 : PL(a, 28);
            int sumU = 0, held = 0;
            for (int k = 0; k < count; k++)
            {
                if (!array && ((bits >> (2 * k)) & 3) != 1) continue;
                int c = Mem.LoadI32(first + 4 * k);
                if (c == 0 || Mem.LoadI32(c) >= -1) continue;
                int cf = PL(c, 24);
                if (PL(c, 20) != a || (cf & Owned) == 0) continue;
                if ((cf & (Dying | Queued)) != 0 || (cf & MultMask) != MultOne) return;
                sumU += (int)((uint)cf >> UShift);
                if ((cf & Held) == 0) continue;
                if ((ThreadMode() ? t_failedLocal : s_failedTops) != 0 && PLM(c) != 0) return;
                if (ShedWay(c, cf, a, up) == 0) return;
                held++;
            }
            if (held == 0 || held != PL(a, 4) || (int)((uint)af >> UShift) != sumU) return;
            for (int k = 0; k < count; k++)
            {
                if (!array && ((bits >> (2 * k)) & 3) != 1) continue;
                int c = Mem.LoadI32(first + 4 * k);
                if (c == 0 || Mem.LoadI32(c) >= -1) continue;
                int cf = PL(c, 24);
                if (PL(c, 20) != a || (cf & (Owned | Held)) != (Owned | Held)) continue;
                int uc = (int)((uint)cf >> UShift);
                if (ShedWay(c, cf, a, up) == 2)
                {
                    // (its references up, all in its own slots, are counted ones from here on: with none below it
                    // it holds nothing, UpPath lets the hold go; whoever owns it next finds the references on its
                    // chain again, Reevaluate)
                    for (int ub = PL(c, 28), i = 0; ub != 0; ub = (int)((uint)ub >> 2), i++)
                    {
                        if ((ub & 3) != 2) continue;
                        int t = Mem.LoadI32(c + 12 + 4 * i);
                        if (t == 0 || Mem.LoadI32(t) >= -1) continue;
                        SetSlotBits(c, i, 1);
                        PS(t, 4, PL(t, 4) + 1);
                        if ((ThreadMode() ? t_failedLocal : s_failedTops) != 0 && OutsideMarked(PLM(t))) NoteOutside(t, c, 1);
                        UpPath(c, t, -1);
                        if (!HoldSettled(t)) UpdateHold(t);
                    }
                    continue;
                }
                int w = PLW(c);
                PSW(c, 0);
                PS(c, 24, cf & ~Held);
                PS(c, 20, w);
                PS(a, 4, PL(a, 4) - 1);
                PS(a, 24, PL(a, 24) - uc * UOne);
                for (int v = w, h = 0; v != up && v != 0 && h <= ChainMax; v = PL(v, 20), h++) PS(v, 24, PL(v, 24) + uc * UOne);
                // (the container's own reference to it is another's than its owner's now, until the container goes)
                if (!HoldSettled(c)) UpdateHold(c);
                for (int v = w, h = 0; v != up && v != 0 && h <= ChainMax; h++) { int next = PL(v, 20); if (!HoldSettled(v)) UpdateHold(v); v = next; }
            }
        }

        /// <summary>ShedHolds, how a held item (c, its flags cf) of the container a lets its hold go: 1 the container
        /// waiting to own it is the container's owner (up) or below it, and takes it; 2 it owns nothing that
        /// refers up (its references up are all in its own slots), and they become counted ones; 0 neither.</summary>
        static int ShedWay(int c, int cf, int a, int up)
        {
            int w = PLW(c);
            if (w != 0 && (w & 1) == 0 && w != a && Mem.LoadI32(w) < -1 && (PL(w, 24) & Dying) == 0 && OnChainOwned(w, up) && !Under(w, a)) return 1;
            if ((cf & (Elems | Far)) != 0) return 0;
            int n = 0;
            for (int ub = PL(c, 28); ub != 0; ub = (int)((uint)ub >> 2)) if ((ub & 3) == 2) n++;
            return n != 0 && n == (int)((uint)cf >> UShift) ? 2 : 0;
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
        /// <summary>Threads: s_failedTops for the groups that are each one thread's (Arenas.LocalScc), this thread's:
        /// a top of another thread's or of a shared group with a failed check left every other group's single-thread
        /// paths that look for notes off (AddNode, NodeUp, PlainParents: one idle worker's, tools/frames-threads).</summary>
        [System.ThreadStatic] static int t_failedLocal;
        // (Dropped: 0, the trial takes in nothing but what the top owns; else one more than how many other tops it took
        // in, with what they own. Zero by default: an initial value would give Rt a static constructor. Each thread's
        // own, as the trial's lists: a plain static, the check of one thread's group set it to zero while another's
        // was taking tops in, which then stayed in s_tkQ, marked Dying, for the thread's next check)
        [Intrinsic] static int s_trExt;
        // (the tops taken in, waiting until what the top owns has been gone through; and where in the trial's list
        // what they own starts, 0 before that)
        [Intrinsic] static int s_tkQ, s_tkN, s_tkCap, s_trOwn;

        static void TkPush(int m)
        {
            if (s_tkN == s_tkCap)
            {
                int cap = s_tkCap == 0 ? 16 : s_tkCap * 2;
                int nq = Alloc(cap * 4);
                if (s_tkQ != 0) { Mem.Copy(nq, s_tkQ, s_tkN * 4); Free(s_tkQ, s_tkCap * 4); }
                s_tkQ = nq; s_tkCap = cap;
            }
            Mem.StoreI32(s_tkQ + 4 * s_tkN, m);
            s_tkN++;
        }

        /// <summary>The trial's list back to its first n (what the top owns), and the tops taken in let go.</summary>
        static void TrTruncate(int n)
        {
            for (int i = n; i < s_trN; i++) { int m = Mem.LoadI32(s_trQ + 4 * i); PS(m, 24, PL(m, 24) & ~Dying); }
            for (int k = 0; k < s_tkN; k++) { int m = Mem.LoadI32(s_tkQ + 4 * k); PS(m, 24, PL(m, 24) & ~Dying); }
            s_trN = n;
            s_tkN = 0;
        }

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
            if (!ThreadMode()) s_failedTops--; else if (SccLocal(Mem.LoadI32(m + 4))) t_failedLocal--; else Atomic.Add32(ref s_failedTops, -1);
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

        static void TrVisit(int mode) => TrVisit(mode, 0);

        static void TrVisit(int mode, int from)
        {
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem;
            for (int i = from; i < s_trN; i++)
            {
                int m = Mem.LoadI32(s_trQ + 4 * i);
                s_visitMode = mode; s_visitObj = m; s_visitElem = 0;
                // (a member under a top taken in that code refers to: the set cannot die, and what other tops own is
                // not gone through further, Dropped. A live structure that small ones refer into would be at each of
                // their checks)
                if (mode == 13 && s_trOwn != 0 && i >= s_trOwn && Mem.LoadI32(m) != -2) { s_trExt = -1; break; }
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
            // (unless that container is one the member itself owns: `n.Raw[6] = n`, the array waits to own the member
            // that owns it, and its reference is one the trial takes off; a member that holds its old owner is not
            // adopted, so nothing else looks: test 247 with a root that is a member)
            if (PLW(x) != 0 && !Under(PLW(x), x)) return false;
            int ar = ArenaOf(x);
            if (Mem.LoadI64(ar + 12) != 0) { if (ArenaReport()) { s_repRefused++; s_repRefusedFlags |= Mem.LoadI32(ar + 12); } return false; }
            ClearMark(x);
            s_trial = 1;
            int held, outside; bool inside, dead;
            // (first with the other tops that what it owns refers into, those that no code refers to and nothing owns,
            // taken in; if that fails by a reference from outside, again by what the top owns alone, whose numbers the
            // note keeps)
            s_trExt = NoTakeIn() ? 0 : 1;
            // what it owns, and what they own: marked as they are found
            s_trN = 0;
            PS(x, 24, PL(x, 24) | Dying);
            TrPush(x);
            TrVisit(13);
            int own = s_trN;
            // (then the tops taken in, with what they own, and the tops those take in)
            while (s_tkN > 0 && s_trExt > 0)
            {
                int from = s_trN;
                if (s_trOwn == 0) s_trOwn = from;
                for (int k = 0; k < s_tkN; k++) TrPush(Mem.LoadI32(s_tkQ + 4 * k));
                s_tkN = 0;
                TrVisit(13, from);
            }
            s_trOwn = 0;
            // (stopped at one that code refers to: what the top owns alone, the first part of the list)
            if (s_trExt < 0) { TrTruncate(own); s_trExt = 0; }
            while (true)
            {
                // the counted references among them, off
                TrVisit(14);
                held = 0; outside = 0; inside = true;
                for (int i = 0; i < s_trN; i++)
                {
                    int m = Mem.LoadI32(s_trQ + 4 * i);
                    if (Mem.LoadI32(m) != -2) held++;
                    int left = PL(m, 4);
                    if (left != 0) { inside = false; outside += left; }
                }
                TrVisit(15);
                dead = inside && held == 0;
                if (ArenaReport()) RepEv(1, x, dead ? 1 : inside ? 2 : 3, (s_trExt << 16) | (inside ? held : outside));
                if (dead || s_trExt <= 1) break;
                // (failed by members that code holds: all of them are what the top owns, since one under a top taken in
                // stops the trial. That is the note, so that the last of them let go looks again, TopHeld, with what
                // is taken in then)
                if (inside) break;
                // (failed by a reference from outside: again by what the top owns alone, whose numbers the note keeps)
                TrTruncate(own);
                s_trExt = 0;
            }
            s_trExt = 0;
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
                if (!ThreadMode()) s_failedTops++; else if (SccLocal(Mem.LoadI32(x + 4))) t_failedLocal++; else Atomic.Add32(ref s_failedTops, 1);
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
            if (!ThreadMode() || SccLocal(Mem.LoadI32(p + 4)))
            {
                int ar0 = PL(p, 16), fl0 = PL(p, 24);
                if (Mem.LoadI32(p) == -2 && Mem.LoadI32(ar0 + 16) == 0 && ((ThreadMode() ? t_failedLocal : s_failedTops) == 0 || !HeldMarked(PLM(p)))
                    && ((uint)fl0 < UOne ? (fl0 & Held) == 0 : (fl0 & Held) != 0 && PL(p, 4) - ((fl0 & MultMask) >> 3) + 1 > 0))
                {
                    Mem.StoreI32(p, -3);
                    Mem.StoreI32(ar0, Mem.LoadI32(ar0) + 1);
                    OpStat(26);
                    return;
                }
            }
            int old = Atomic.CountAdd32(ref Mem.RefI32(p), -1);
            if (old == -2) { int ar = ArenaOf(p); Mem.StoreI32(ar, Mem.LoadI32(ar) + 1); if ((ThreadMode() && SccLocal(Mem.LoadI32(p + 4)) ? t_failedLocal : s_failedTops) != 0 && HeldMarked(PLM(p))) TopHeld(p, 1); }
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
            if ((!ThreadMode() || SccLocal(Mem.LoadI32(p + 4))) && !Checked())
            {
                int ar0 = PL(p, 16), fl0 = PL(p, 24), total0 = Mem.LoadI32(ar0) - 1, in0 = PL(p, 4);
                if (Mem.LoadI32(p) == -3 && Mem.LoadI32(ar0 + 16) == 0 && total0 != 0 && (Mem.LoadI32(ar0 + 12) & ArenaBusy) == 0 && in0 != 0
                    && (fl0 & (Owned | Dying)) == Owned && ((ThreadMode() ? t_failedLocal : s_failedTops) == 0 || !HeldMarked(PLM(p)))
                    && ((uint)fl0 < UOne ? (fl0 & Held) == 0 : ((fl0 & Held) != 0) == (in0 - ((fl0 & MultMask) >> 3) > 0)))
                {
                    Mem.StoreI32(p, -2);
                    Mem.StoreI32(ar0, total0);
                    if (DropStats()) s_dsFromRelease++;
                    OpStat(27);
                    return;
                }
            }
            int w = Atomic.CountAdd32(ref Mem.RefI32(p), 1) + 1;
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
            // (let go by its owner before, by the code now, kept by the holds of its own items alone: ShedHolds)
            if (w == -2 && PL(p, 4) != 0 && (PL(p, 24) & (Owned | Dying)) == 0) ShedHolds(p);
            // (no reference of either kind: it goes now, with what only it referred to)
            if (w == -2 && PL(p, 4) == 0) { ArenaDestroyFrom(p, 0); DrainKills(); return; }
            // (kept by what it owns alone, or under a top that was: the check at the drop)
            if (w == -2)
            {
                int t = (ThreadMode() && SccLocal(Mem.LoadI32(p + 4)) ? t_failedLocal : s_failedTops) != 0 && HeldMarked(PLM(p)) ? TopHeld(p, -1) : 0;
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
            // (threads: nor into a pinned container, which never joins: GroupLocks.cs, PinnedPlain)
            if (ThreadMode() && val != 0 && PinnedPlain(container))
            {
                int ip = SlotIndex(container, slot);
                t_prevSlot = slot; t_prevBits = slot != 0 && ip >= 0 ? 0 : -1;
                return val;
            }
            bool can = val != 0 && CanLink(val, container);
            if (!can)
            {
                int i0 = SlotIndex(container, slot), cw = Mem.LoadI32(container);
                if (i0 < 0 || cw > 0) { OpStat(1); t_prevSlot = slot; t_prevBits = slot != 0 && i0 >= 0 ? 0 : -1; return val; }
                if (cw < -1 && SlotBits(container, i0) == 0) { OpStat(1); t_prevSlot = slot; t_prevBits = 0; return val; }
            }
            // (a pinned object, which joins nothing, into a plain object, which has no bits to change: a counted
            // reference, ArenaLinkCore's answer after the lock and its questions about the two, with what ArenaLink
            // notes for the unlink that follows. The empty array every new list starts with, stored by List<T>'s
            // constructor: once for each entity of the scene tree of tools/frames.mjs, 19,531 a build.)
            if (can && (!ThreadMode() || SccLocal(Mem.LoadI32(container + 4))) && !NoPinnedSkip() && t_backN == 0 && Mem.LoadI32(container) > 0 && Mem.LoadI32(val) > 0 && Mem.LoadI32(val + 8) < 0)
            {
                OpStat(33);
                t_prevSlot = slot; t_prevBits = slot != 0 ? 0 : -1;
                return val;
            }
            if (t_backN != 0) AfterStore();
            int mk = -1;
            if (!ThreadMode()) RuntimeLock();
            else if (t_regionC != container || t_regionMk < 0) mk = LinkLock(val, container);
            // (what the slot's bits said of the value it held: for the ArenaUnlink that follows the store)
            int i = Mem.LoadI32(container) < -1 ? SlotIndex(container, slot) : -1;
            t_prevSlot = slot;
            // (a plain container's field held a plain counted reference, a field past those a member keeps bits for too:
            // told by the counts at the unlink, a container that joined an arena at this very store (its other slots
            // linked as it joined, not this one) made the old value read as an inside one, and its outside count was
            // never given back: test 132's wide objects, a field past the sixteenth set while the object was new. An
            // array's elements are all linked as it joins, the old one too: by the counts is right for them)
            t_prevBits = slot != 0 && (SlotIndex(container, slot) >= 0 || Mem.LoadI32(container) > 0) ? 0 : -1;
            if (i >= 0) { t_prevBits = SlotBits(container, i); SetSlotBits(container, i, 0); }
            // (a container that joins an arena here has its slots linked: not this one, whose value is on its way out)
            int outer = s_storeSlot;
            s_storeSlot = slot;
            int r = val;
            // (threads: a value LinkLock left out is linked to nothing, GroupLocks.cs)
            if (ThreadMode() && t_linkOver != 0) { t_linkOver = 0; val = 0; }
            if (val != 0)
            {
                // (the usual case: both are members of one arena already, which is not being destroyed or merged)
                int ar = Mem.LoadI32(container) < -1 && Mem.LoadI32(val) < -1 ? PL(container, 16) : 0;
                if (ar != 0 && ar == PL(val, 16) && Mem.LoadI64(ar + 12) == 0)
                {
                    OpStat(2);
                    // (a node put into its parent's list: AddNode)
                    if (slot != 0 || (Mem.LoadI32(val) != -3 && NodePathBreak() != 6) || !AddNode(val, container, ar)) LinkMembers(val, container, slot, ar, false);
                }
                // (a plain object over a linked reference of a member's own slot: a list's larger array over its array)
                else if (t_prevBits == 1 && Mem.LoadI32(val) > 0 && ReplaceArray(val, container, slot)) { }
                // (a plain object only the store's count refers to: a new object put into a member's list)
                else if (can && Mem.LoadI32(val) == 1 && (JoinFresh(val, container, slot) || JoinPair(val, container, slot) || JoinOwned(val, container, slot, true))) { }
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

        /// <summary>A plain object whose one count is the reference being linked (a new object stored into a member's
        /// slot, or held by a slot of an object that joins), of the container's group of classes: it joins the
        /// container's arena and is the container's, with no outside reference. What ArenaLinkCore and LinkMembers do
        /// for it, with what they ask and cannot be so left out: it is a member of no arena (nothing to merge), its
        /// count goes from the store's to none in one step (the arena's number of members that code refers to
        /// unchanged), it is above nothing (LinkMembers, fresh) and nothing holds or waits for it (its words are
        /// zero), so it has no hold to settle. False (nothing done) when the container is no member or either cannot
        /// be linked now: ArenaLinkCore's answer then. `can`: the two are known to be of one group of classes.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool JoinOwned(int val, int container, int slot, bool can)
        {
            if (NoFreshJoin() || ThreadMode() && !SccLocal(Mem.LoadI32(container + 4))) return false;
            if (Mem.LoadI32(container) >= -1) return false;
            if (!can) { int g = SccOf(Mem.LoadI32(val + 4)); if (g == 0 || g != SccOf(Mem.LoadI32(container + 4))) return false; }
            if (((PL(val, 24) | PL(container, 24)) & Dying) != 0 || Mem.LoadI32(val + 8) < 0) return false;
            int ar = ArenaOf(container);
            if ((Mem.LoadI32(ar + 12) & ArenaBusy) != 0) return false;
            OpStat(29);
            ArenaJoin(ar, val);
            Mem.StoreI32(val, -2);
            Mem.StoreI32(ar, Mem.LoadI32(ar) - 1);
            PS(val, 4, 1);
            int i = s_visitElem != 0 ? -1 : SlotIndex(container, slot);
            if (i >= 0) SetSlotBits(container, i, 1);
            else if (slot == 0 || s_visitElem != 0)
            {
                PS(container, 28, PL(container, 28) + 1);
                PS(container, 24, PL(container, 24) | Elems);
            }
            else PS(container, 24, PL(container, 24) | Far);
            PS(val, 20, container);
            PS(val, 24, PL(val, 24) | Owned | MultOne);
            return true;
        }

        /// <summary>CS2WASM_NO_FUSED_JOIN=1 (at compile time): a new object stored into a member's slot joins by
        /// JoinOwned and the visit of its slots (ArenaLinkJoined), each slot linked as the store it stands for.</summary>
        [Intrinsic] static extern bool NoFusedJoin();

        /// <summary>JoinOwned and the links of the new object's own slots in one step, where what the visit of the
        /// joined object would come to is fixed by what its slots hold, and can be written as it is found:
        /// nothing for an empty slot, a pinned object (the empty array of a new list) and an object of another
        /// group of classes; a new object of its own that only that slot refers to and whose own slots hold
        /// nothing to link (its list) is its own, after it on the ring in the order of the slots, as the visit
        /// joins them; and one reference up, of two kinds.
        /// A member above the container on its chain of owned links that code still refers to from elsewhere
        /// (the parent of a leaf, set as the leaf was made, whose list has items already): an up-reference, its
        /// count given back (not the member's last from outside: nothing to decide), the u of the members
        /// between them and their holds as LinkMembers does them (UpPath).
        /// Or a plain object that code still refers to, which holds, in one slot, the member at the top of the
        /// container's chain, a member nothing owns and nothing else refers to (the node whose list takes its
        /// first item, in the scene's order: the node was made with its parent, its list got its first array,
        /// and neither the node nor anything above the list is the array's yet). The general way: the new
        /// object joins; the visit of its slots joins the node through the parent field, as a counted reference
        /// that waits for an owner (Waiting: side words for the node, a place in LinkWaiting's list); the visit
        /// of the node's slots links its list to it (the store's count given back early, TakeOwnership,
        /// Reevaluate) and whatever its other slots hold; LinkWaiting then finds the node above the new object
        /// and makes the field an up-reference (Reevaluate, by a visit of the object). Written here: the node a
        /// member nothing owns, counted from outside by what counted it less the field; the top of the chain
        /// the node's, with no reference from outside; the field an up-reference; then the node's other slots
        /// that hold members, each by the general link of a joining object's slot (LinkJoinedSlot: the node's
        /// own parent, with the merge of the two arenas), and LinkWaiting for what those left waiting. The
        /// general way does the node's slots in their order and the up-reference last; here the list and the
        /// up-reference come first. They change the new object, the members of its chain up to the node and
        /// the node's count of inside references; the link of another slot of the node reads the node's owner
        /// (none, either way) and what that slot holds, and writes the node's bits for that slot and, for a
        /// hold, adds to the node's inside count: the same end either way.
        /// Nothing waits in ArenaJoin's list after either, so no visit follows. False, nothing written, for
        /// anything else: a slot that holds another kind of reference, a type whose slots its word does not say
        /// (ArenaSlots), a container whose arena is busy or merged, threads: JoinOwned's way then.
        /// (By a program of the parts, on the desk machine: a leaf with its list and its parent field, put into a
        /// list that has room, 49 ns by the general way, 10 of them its two allocations, and 38 here: 12,500 of
        /// the 19,531 entities of a build of the scene tree of tools/frames.mjs; a node with its first leaf and
        /// their lists 256 ns and 205: 2,500 a build, the 625 whose own parent is a plain object still go the
        /// general way.)</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool JoinFresh(int val, int container, int slot)
        {
            if (NoFusedJoin() || NoFreshJoin() || NoOwnedChain() || ThreadMode() && !SccLocal(Mem.LoadI32(container + 4))) return false;
            if (Mem.LoadI32(container) >= -1 || (s_linkN | s_joinVisit | s_visitElem | s_reevN) != 0) return false;
            int ar = PL(container, 16);
            if (Mem.LoadI64(ar + 12) != 0 || (PL(container, 24) & Dying) != 0) return false;
            if (PL(val, 24) != 0 || Mem.LoadI32(val + 8) < 0) return false;
            int d = ArenaSlots(Mem.LoadI32(val + 4));
            if (d == 0) return false;
            int g = SccOf(Mem.LoadI32(val + 4));
            // what the slots hold, before anything is written
            int e = 0, up = 0;
            for (int w = d; w != 0; w = (int)((uint)w >> 4))
            {
                int child = Mem.LoadI32(val + 8 + ((w & 15) << 2));
                if (child == 0) continue;
                int cc = Mem.LoadI32(child);
                // (threads: immortal, ArenaPinFixed: linked to nothing)
                if (ThreadMode() && cc == -1) continue;
                if (cc > 0)
                {
                    // (pinned, or of another group of classes: linked to nothing)
                    if (Mem.LoadI32(child + 8) < 0) continue;
                    int ct = Mem.LoadI32(child + 4);
                    if (SccOf(ct) != g) continue;
                    if (PL(child, 24) != 0) return false;
                    if (cc == 1)
                    {
                        // (a new object of its own: its slots hold nothing to link)
                        int d2 = ArenaSlots(ct);
                        if (d2 == 0) return false;
                        for (; d2 != 0; d2 = (int)((uint)d2 >> 4))
                        {
                            int c2 = Mem.LoadI32(child + 8 + ((d2 & 15) << 2));
                            if (c2 != 0 && (ThreadMode() ? Mem.LoadI32(c2) != -1 && (Mem.LoadI32(c2) <= 0 || Mem.LoadI32(c2 + 8) >= 0) : Mem.LoadI32(c2) <= 0 || Mem.LoadI32(c2 + 8) >= 0)) return false;
                        }
                        continue;
                    }
                    // (a plain object that code still refers to: the one reference up; not a member's plain part,
                    // PlainPart: its holder's slot is linked as it joins, the general way)
                    if ((e | up) != 0) return false;
                    if (PL(child, 12) != 0 && PlainPartsBreak() != 4 && PartHolder(child) != 0) return false;
                    e = child;
                    continue;
                }
                // (a member of the container's arena, on its chain by owned links, with more references from
                // outside than this slot's: the one reference up)
                if ((e | up) != 0 || cc >= -3 || PL(child, 16) != ar || (PL(child, 24) & Dying) != 0) return false;
                int v = container, k = 0;
                while (v != child)
                {
                    if (++k == ChainMax || (PL(v, 24) & Owned) == 0) return false;
                    v = PL(v, 20);
                    if (v == 0) return false;
                }
                up = 1;
            }
            int t = 0, ed = 0, ts = 0, others = 0, pe = 0, pes = 0;
            if (e != 0)
            {
                if (NoEarlyGiveBack()) return false;
                // (the top of the container's chain: nothing owns it, and its one reference is the plain object's)
                t = container;
                for (int h = 0; (PL(t, 24) & Owned) != 0; t = PL(t, 20)) if (++h > ChainMax - 2) return false;
                if (Mem.LoadI32(t) != -3 || PL(t, 4) != 0 || PL(t, 20) != 0 || (PL(t, 24) & ~(Elems | Far)) != 0 || Side(t) != 0 || PL(t, 16) != ar) return false;
                ed = ArenaSlots(Mem.LoadI32(e + 4));
                if (ed == 0) return false;
                for (int w = ed; w != 0; w = (int)((uint)w >> 4))
                {
                    int c = Mem.LoadI32(e + 8 + ((w & 15) << 2));
                    if (c == 0) continue;
                    if (c == t) { ts = w & 15; continue; }
                    // (a plain object of the group of classes in another slot: the node's own parent, a plain
                    // object still, PlainParents; anything else the general way, its slots are not known)
                    if (Mem.LoadI32(c) > 0 && Mem.LoadI32(c + 8) >= 0 && SccOf(Mem.LoadI32(c + 4)) == g)
                    {
                        if (pe != 0 || Mem.LoadI32(c) < 2) return false;
                        // (not a member's plain part: PartHolder)
                        if (PL(c, 12) != 0 && PlainPartsBreak() != 4 && PartHolder(c) != 0) return false;
                        pe = c; pes = w & 15;
                        continue;
                    }
                    // (the node's other slots that hold members: one alone can take NodeUp)
                    if (Mem.LoadI32(c) <= 0) others++;
                }
                if (ts == 0) return false;
                if (pe != 0 && (others != 0 || !PlainParents(pe, e, val, ar, g, false))) return false;
            }
            OpStat(29); OpStat(35);
            JoinOne(ar, val, container);
            int i = SlotIndex(container, slot);
            if (i >= 0) SetSlotBits(container, i, 1);
            else if (slot == 0)
            {
                PS(container, 28, PL(container, 28) + 1);
                PS(container, 24, PL(container, 24) | Elems);
            }
            else PS(container, 24, PL(container, 24) | Far);
            int bits = 0;
            for (int w = d; w != 0; w = (int)((uint)w >> 4))
            {
                int n = (w & 15) - 1, child = Mem.LoadI32(val + 12 + (n << 2));
                if (child == 0) continue;
                int cc = Mem.LoadI32(child);
                if (cc > 0)
                {
                    if (Mem.LoadI32(child + 8) < 0 || SccOf(Mem.LoadI32(child + 4)) != g) continue;
                    if (cc == 1)
                    {
                        // (its own new object, with nothing to link in it: left a plain object, PlainPart)
                        // (not one of a class with a finalizer, which runs with the group's; not one a weak handle
                        // refers to, or whose finalizer was suppressed: ObjectMarks, PartMarked)
                        if (!NoPlainParts() && (ArenaFinType(Mem.LoadI32(child + 4)) == 0 || PlainPartsBreak() == 6)
                            && ((Mem.LoadI32(child + 8) & ObjectMarks.MarkedBit) == 0 || PlainPartsBreak() == 7))
                        {
                            OpStat(41);
                            PS(child, 12, val);
                            PS(child, 8, val + 12 + (n << 2));
                            continue;
                        }
                        OpStat(29);
                        JoinOne(ar, child, val);
                        bits |= 1 << (2 * n);
                        continue;
                    }
                    // (the plain object joins, nothing's: ArenaLinkCore's join, and LinkMembers' count given back
                    // for an object that joined for the link; the field an up-reference, below)
                    OpStat(4); OpStat(36);
                    Mem.StoreI32(child, -(cc + 1));
                    PS(child, 16, ar);
                    PS(child, 4, 0);
                    RingAdd(ar, child);
                    bits |= 2 << (2 * n);
                    continue;
                }
                OpStat(23);
                Mem.StoreI32(child, cc + 1);
                bits |= 2 << (2 * n);
                PS(val, 28, bits);
                UpPath(val, child, 1);
                if (!HoldSettled(child)) UpdateHold(child);
            }
            PS(val, 28, bits);
            if (e != 0)
            {
                // (the top of the chain is the node's: its count from outside, the node's slot's, given back; the
                // arena's number of members that code refers to one more by the node and one less by this)
                OpStat(32); OpStat(23);
                Mem.StoreI32(t, -2);
                PS(t, 4, 1);
                PS(t, 20, e);
                PS(t, 24, PL(t, 24) | Owned | MultOne);
                PS(e, 28, 1 << (2 * (ts - 1)));
                UpPath(val, e, 1);
                // (the node's other slots that hold members)
                int sa = s_visitArena, sm = s_visitMode, so = s_visitObj;
                s_visitArena = e; s_visitMode = 4; s_visitObj = e; s_joinVisit = 1;
                for (int w = ed; w != 0; w = (int)((uint)w >> 4))
                {
                    int sl = e + 8 + ((w & 15) << 2), c = Mem.LoadI32(sl);
                    if (c != 0 && c != t && Mem.LoadI32(c) <= 0 && !((others == 1 || NodePathBreak() == 4) && NodeUp(c, e, sl))) LinkJoinedSlot(c, sl);
                }
                s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_joinVisit = 0;
                if (s_linkN == 0 && s_reevN != 0) LinkWaiting();
                if (pe != 0) { SetSlotBits(e, pes - 1, 1); PlainParents(pe, e, val, ar, g, true); }
            }
            return true;
        }

        /// <summary>CS2WASM_NO_PLAIN_PARENTS=1 (at compile time): JoinFresh refuses a node whose own parent is a
        /// plain object still (the general way joins the chain of plain parents).</summary>
        [Intrinsic] static extern bool NoPlainParents();

        /// <summary>JoinFresh's node e (a plain object whose list takes its first item) holds, in its one other
        /// slot that can be linked, a plain object that code still refers to: its own parent, made before it and
        /// not yet a member, because no child of it has a child (in a tree built parent first, each node's first
        /// child: 625 a build of the scene tree of tools/frames.mjs, and with them every plain parent above).
        /// The general way: e's visit joins the parent through e's field as a counted reference that waits for
        /// an owner (side words, a pair for LinkWaiting); the parent's visit joins its new objects with nothing
        /// to link (its list), and its own parent the same way, level after level, up to a parent that is a
        /// member (linked as LinkJoinedSlot links it: the merge, NodeUp's outcome), or none; then LinkWaiting's
        /// pairs from the bottom: each parent, without an owner, is its child's (TakeOwnership). Each level's
        /// slots hold, asked before a word is written (`write` false): nothing to link, new objects of the
        /// level's own whose slots hold nothing to link, and at most one reference up, to a plain object that
        /// code still refers to (the next level; none twice) or to a member that code refers to (the end: its
        /// group not busy or merged, with more members than the node's so that the merge ends in it, no note of
        /// a failed check alive). Written (`write` true), level by level in the order of the general way's
        /// joins: the level a member with the counts it had less the field's, nothing's, referred to once from
        /// inside; its new objects its own; the next level joined as its slot comes; its bits; the member's
        /// link by NodeUp; and the level its child's. Not made: the levels' side words (they would hold
        /// nothing after LinkWaiting). `below`: the member whose slot holds the first level (e), already
        /// joined; `item`: the new item, for the walk's end.</summary>
        static bool PlainParents(int first, int below, int item, int ar, int g, bool write)
        {
            if (!write && (NoPlainParents() || NoNodePath() || (ThreadMode() ? t_failedLocal : s_failedTops) != 0)) return false;
            int b = below, joined = 0;
            if (write)
            {
                // (the first level joins as e's slot is visited)
                int c0 = Mem.LoadI32(first);
                OpStat(4);
                Mem.StoreI32(first, -(c0 + 1)); PS(first, 16, ar); PS(first, 4, 1); RingAdd(ar, first);
                Mem.StoreI32(ar, Mem.LoadI32(ar) + 1);
            }
            for (int a = first, level = 0; a != 0; level++)
            {
                if (!write)
                {
                    if (level == ChainMax || a == item || PL(a, 24) != 0) return false;
                    // (no level twice)
                    for (int x = first, k = 0; k < level; k++)
                    {
                        if (x == a) return false;
                        x = PlainUp(x, g);
                    }
                    if (a == below) return false;
                }
                int da = ArenaSlots(Mem.LoadI32(a + 4));
                if (da == 0) return false;
                int up = 0, m = 0, ms = 0, bits = 0;
                for (int w = da; w != 0; w = (int)((uint)w >> 4))
                {
                    int k = (w & 15) - 1, c = Mem.LoadI32(a + 12 + (k << 2));
                    if (c == 0) continue;
                    int cc = Mem.LoadI32(c);
                    if (ThreadMode() && cc == -1) continue;
                    if (SccOf(Mem.LoadI32(c + 4)) != g) continue;
                    // (the next level, joined in the write pass as this slot came: a member by now)
                    if (write && c == up) continue;
                    if (cc > 0)
                    {
                        if (Mem.LoadI32(c + 8) < 0) continue;
                        if (cc == 1)
                        {
                            if (write) { OpStat(29); JoinOne(ar, c, a); bits |= 1 << (2 * k); continue; }
                            // (a new object of the level's own: its slots hold nothing to link)
                            int d2 = ArenaSlots(Mem.LoadI32(c + 4));
                            if (d2 == 0 || PL(c, 24) != 0) return false;
                            if (NodePathBreak() != 8)
                                for (; d2 != 0; d2 = (int)((uint)d2 >> 4))
                                {
                                    int c2 = Mem.LoadI32(c + 8 + ((d2 & 15) << 2));
                                    if (c2 != 0 && (ThreadMode() ? Mem.LoadI32(c2) != -1 && (Mem.LoadI32(c2) <= 0 || Mem.LoadI32(c2 + 8) >= 0) : Mem.LoadI32(c2) <= 0 || Mem.LoadI32(c2 + 8) >= 0)) return false;
                                }
                            joined++;
                            continue;
                        }
                        if (up != 0 || m != 0) return false;
                        // (not a member's plain part: PartHolder)
                        if (!write && PL(c, 12) != 0 && PlainPartsBreak() != 4 && PartHolder(c) != 0) return false;
                        up = c;
                        if (write)
                        {
                            OpStat(4);
                            Mem.StoreI32(c, -(cc + 1)); PS(c, 16, ar); PS(c, 4, 1); RingAdd(ar, c);
                            Mem.StoreI32(ar, Mem.LoadI32(ar) + 1);
                            bits |= 1 << (2 * k);
                        }
                        continue;
                    }
                    // (a member: the end of the plain parents)
                    if (up != 0 || m != 0 || cc > -3) return false;
                    m = c; ms = k;
                    if (write)
                    {
                        // (linked as its slot comes: the merge is the general way's, with the members it has then,
                        // and what joins after it joins the group that is left)
                        PS(a, 28, bits);
                        if (!NodeUp(m, a, a + 12 + (ms << 2))) LinkJoinedSlot(m, a + 12 + (ms << 2));
                        bits = PL(a, 28);
                        ar = PL(m, 16);
                    }
                }
                joined++;
                if (m != 0 && !write)
                {
                    if ((PL(m, 24) & (Dying | Queued)) != 0 || PL(m, 20) == a) return false;
                    int am = PL(m, 16);
                    if (Mem.LoadI64(am + 12) != 0) return false;
                    if (am != ar && Mem.LoadI32(am + 8) <= Mem.LoadI32(ar + 8) + joined + 10) return false;
                }
                if (write)
                {
                    PS(a, 28, bits);
                    TakeOwnership(a, b);
                }
                b = a;
                a = up;
            }
            if (write) OpStat(40);
            return true;
        }

        /// <summary>PlainParents, before anything is written: the plain object of the group that code still refers to
        /// in a slot of the level (its reference up), or 0.</summary>
        static int PlainUp(int a, int g)
        {
            for (int w = ArenaSlots(Mem.LoadI32(a + 4)); w != 0; w = (int)((uint)w >> 4))
            {
                int c = Mem.LoadI32(a + 8 + ((w & 15) << 2));
                if (c != 0 && Mem.LoadI32(c) > 1 && Mem.LoadI32(c + 8) >= 0 && SccOf(Mem.LoadI32(c + 4)) == g) return c;
            }
            return 0;
        }

        /// <summary>True, the default since 2026-11-23 (CS2WASM_PLAIN_PARTS=1 at compile time, or a
        /// CS2WASM_PLAIN_PARTS_BREAK, for false; CS2WASM_NO_PLAIN_PARTS=1 true): JoinFresh joins a new object's own
        /// new object with it, a member (PartHolder). Off because with the parts on, objects a finalizer brings back
        /// (Rt.FinRevive) and objects behind weak handles that track resurrection stay alive where .NET frees them
        /// (tests 248, 265, 266; cause not found).</summary>
        [Intrinsic] static extern bool NoPlainParts();
        /// <summary>CS2WASM_PLAIN_PARTS_BREAK=n (at compile time, test 202's): one rule of the plain parts is not
        /// asked. 1: ArenaLinkCore, a part that joins as a container is not linked to its holder; 2: the same
        /// for one that joins as a value; 3: JoinPair takes a part as its container; 4: JoinFresh takes a part
        /// as its node; 5: PartHolder does not ask that the holder's slot still holds the part; 6: an object of
        /// a class with a finalizer is left a part; 7: a part a weak handle refers to stays one (JoinFresh's
        /// test of the mark, and PartMarked).</summary>
        [Intrinsic] static extern int PlainPartsBreak();
        /// <summary>rt.part_weak: PartMarked in a program with groups, nothing in any other (RuntimeGen.cs).</summary>
        [Intrinsic] internal static extern void PartWeak(int addr);

        /// <summary>A weak handle was made for the object (ObjectMarks.SetAt). The weak references to the members
        /// of a group that goes are cleared before a finalizer of any runs (WeakDyingAll, test 245); a plain
        /// part is released with its holder's clear, after them. So a part that gets a weak handle becomes the
        /// member it would have been, here: the store `holder.slot = part` once more, by the general way (its
        /// words cleared first: the link finds a plain object in a member's slot, and the unlink of the slot's
        /// old value, the same object, gives the plain reference's count back).</summary>
        public static void PartMarked(int x)
        {
            if (NoPlainParts() || ThreadMode() || PlainPartsBreak() == 7) return;
            if (x == 0 || Mem.LoadI32(x) <= 0 || SccOf(Mem.LoadI32(x + 4)) == 0 || PL(x, 12) == 0) return;
            int hs = PartHolder(x);
            if (hs == 0) return;
            int h = PL(x, 12);
            PS(x, 12, 0); PS(x, 8, 0);
            Retain(Borrow<object>(x));
            int v = ArenaLink(x, h, hs);
            Mem.StoreI32(hs, v);
            ArenaUnlink(x, h, hs);
        }

        /// <summary>A plain part (docs/AUDIT.md A3.5, the build, round 10). JoinFresh joins a new object and, with
        /// it, each new object of its own that only its slot refers to and that holds nothing to link (an
        /// entity's empty list): a second member for every leaf of a tree, joined, and destroyed as a member
        /// when the leaf goes, though nothing can be linked through it while it holds nothing. Such an object
        /// is left a plain object instead: the member's slot holds it by a plain counted reference (bits 0, as
        /// a reference to a pinned object or to one of another group of classes), it is released as one when
        /// the member is cleared, and it keeps, in the two words a member has its place on the ring in (free
        /// while it is plain, written by every join), the member that holds it and the slot.
        /// "A member refers to no plain object of its group of classes" was what kept a cycle from closing
        /// unseen: a plain object that joins has its slots linked, and a member's slots are linked already.
        /// With a part that holds for every part but one that joins later (a store into it of something that can
        /// be linked: the list's first array; a store of it where a cycle can close): the holder's slot must be
        /// linked then, or two members have a counted reference between them that no link knows of, and what
        /// hangs below the part, with a reference back up, never goes. So wherever a plain object that code
        /// still refers to becomes a member (ArenaLinkCore, for the container and for the value; JoinFresh's
        /// node and JoinPair's container, which refuse it and so come to ArenaLinkCore), its two words are
        /// asked, and the holder's slot is linked first, as the store `holder.slot = part` it stands for
        /// (PartLink): from there on the part is the member it would have been.
        /// The words can be stale (the holder's slot stored over, the holder gone or a plain object again, the
        /// part taken by another object; nothing clears them then), so they are a hint, checked by what is
        /// there: the slot's address is a slot of a living object of a group (SlotOwner: the allocator's
        /// answer for the address, then the type's), that object the one named, a member, the slot one it has
        /// bits for, the bits 0, and the slot holds the part. A member that passes holds the part by a plain
        /// counted reference in a slot a cycle can close through: whatever wrote the words, linking it is
        /// right. A part whose one count is its holder's (JoinOwned's and JoinFresh's own new objects) has no
        /// other holder: its words are stale and the join writes over them.
        /// Not an object of a class with a finalizer (it would run at the holder's clear, not with the group's),
        /// not with threads (JoinFresh is not taken there). Returns the holder's slot, or 0 with the words
        /// cleared.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int PartHolder(int x)
        {
            int h = PL(x, 12), hs = PL(x, 8);
            if (h != 0 && hs != 0 && !ThreadMode())
            {
                bool sa = t_slotArray;
                int c = SlotOwner(hs);
                t_slotArray = sa;
                if (c == h && Mem.LoadI32(h) < -1)
                {
                    int i = SlotIndex(h, hs);
                    if (i >= 0 && SlotBits(h, i) == 0 && (Mem.LoadI32(hs) == x || PlainPartsBreak() == 5)) return hs;
                }
            }
            PS(x, 12, 0); PS(x, 8, 0);
            return 0;
        }

        /// <summary>A plain part is about to join an arena: its holder's slot (hs, PartHolder's answer) is linked
        /// now, as the store of the part into that slot would have been: a part only that slot refers to joins
        /// as the holder's (JoinOwned); one that code refers to as well goes ArenaLinkCore's way. Its slots are
        /// linked with those of whatever else joins in the link under way (ArenaJoin's list).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PartLink(int x, int hs)
        {
            int h = PL(x, 12);
            PS(x, 12, 0); PS(x, 8, 0);
            OpStat(42);
            // (the holder's slot is a field, whatever the visit under way is going through)
            int se = s_visitElem;
            s_visitElem = 0;
            if (Mem.LoadI32(x) != 1 || !JoinOwned(x, h, hs, false)) ArenaLinkCore(x, h, hs);
            s_visitElem = se;
        }

        /// <summary>CS2WASM_NO_NODE_PATH=1 (at compile time): the three steps of a node of a tree built parent first
        /// (JoinPair, NodeUp, AddNode) are not taken; each goes the general way.</summary>
        [Intrinsic] static extern bool NoNodePath();
        /// <summary>CS2WASM_NODE_PATH_BREAK=n (at compile time, test 201's): one rule of those steps is not asked.
        /// 1: JoinPair, what the new object holds; 2: JoinPair, the container's other slots; (3: no longer a
        /// rule: NodeUp gives the parent's last count from outside back as LinkMembers does;) 4: NodeUp, that the node has no other slot to
        /// link; 5: NodeUp, whether the parent has an owner; 6: AddNode, that the store's count is the node's one
        /// reference from outside; 7: AddNode, that the member the node owns above the array has no up-reference
        /// below it and holds nothing; 8: PlainParents, what a new object of a plain parent's own holds.
        /// (tools/node-path-breaks.sh: test 201's lines change with 1, 2 and 6 as they are, and with 4, 5, 7 and 8,
        /// which leave what the check at the drop mends later, when that check is off on both sides.)</summary>
        [Intrinsic] static extern int NodePathBreak();

        /// <summary>The node path, 1 of 3 (docs/AUDIT.md A3.5, the build, round 9). A tree built parent first
        /// (`e = new Node { Parent = p }; e.Kids.Add(child of e) ..; p.Kids.Add(e)`) asks three things of the
        /// general link for each node, each of them a fixed outcome a few loads decide:
        /// this one, a new object (count 1: the store's) stored into a slot of a plain object of its group of
        /// classes (a list's first array, `_items = new T[4]`, before the list's node is anything's): the two
        /// become a group of their own, the container its first member with the counts code has on it, the new
        /// object the container's with none. The general way: ArenaLinkCore (a record, two joins noted for a
        /// visit each, LinkMembers with the count given back as for an object that joined for the store and the
        /// fresh object's ownership without a walk), then ArenaLinkJoined's visits of both by their types, which
        /// find nothing: asked here before a word is written, the new object's slots or elements all empty (a
        /// pinned object or one of another group of classes in a slot counts as empty, as the visit passes it
        /// by) and the container's other slots likewise. False, nothing written, for anything else.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool JoinPair(int val, int container, int slot)
        {
            if (NoNodePath() || NoFreshJoin() || ThreadMode() && !SccLocal(Mem.LoadI32(container + 4))) return false;
            int ca = Mem.LoadI32(container);
            if (ca <= 0 || val == container || (s_linkN | s_joinVisit | s_visitElem | s_reevN) != 0) return false;
            if (Mem.LoadI32(container + 8) < 0 || Mem.LoadI32(val + 8) < 0 || (PL(container, 24) | PL(val, 24)) != 0) return false;
            // (a member's plain part, PlainPart: it joins as its holder's, ArenaLinkCore)
            if (PL(container, 12) != 0 && PlainPartsBreak() != 3 && PartHolder(container) != 0) return false;
            int i = SlotIndex(container, slot);
            if (i < 0) return false;
            int g = SccOf(Mem.LoadI32(container + 4));
            // (the container's other slots: what the visit of a joined object would link)
            int dc = ArenaSlots(Mem.LoadI32(container + 4));
            if (dc == 0) return false;
            if (NodePathBreak() != 2)
                for (int w = dc; w != 0; w = (int)((uint)w >> 4))
                {
                    int sl = container + 8 + ((w & 15) << 2);
                    if (sl != slot && !SlotEmpty(Mem.LoadI32(sl), g)) return false;
                }
            // (the new object: its slots, or an array's elements, one reference each)
            int dv = ArenaSlots(Mem.LoadI32(val + 4));
            if (NodePathBreak() != 1)
            {
                if (dv != 0)
                {
                    for (int w = dv; w != 0; w = (int)((uint)w >> 4))
                        if (!SlotEmpty(Mem.LoadI32(val + 8 + ((w & 15) << 2)), g)) return false;
                }
                else
                {
                    if (ArrayElementSizeAt(val) != 4 || ArrayRankAt(val) != 1) return false;
                    int n = Mem.LoadI32(val + 12);
                    if ((uint)n > 64) return false;
                    for (int k = 0; k < n; k++) if (Mem.LoadI32(val + 16 + 4 * k) != 0) return false;
                }
            }
            OpStat(37);
            int ar = NewArena();
            // (the container: ArenaJoinOnly, a member with the counts it had)
            OpStat(4);
            Mem.StoreI32(container, -(ca + 2));
            PS(container, 16, ar);
            PS(container, 4, 0);
            RingAdd(ar, container);
            Mem.StoreI32(ar, 1);
            // (the new object: its, with no reference from outside; the slot a linked one)
            JoinOne(ar, val, container);
            SetSlotBits(container, i, 1);
            return true;
        }

        /// <summary>JoinPair: a slot's value the visit of a joined object links to nothing: none, a pinned object,
        /// a plain object of another group of classes.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool SlotEmpty(int c, int g)
        {
            if (c == 0) return true;
            if (ThreadMode() && Mem.LoadI32(c) == -1) return true;
            return Mem.LoadI32(c) > 0 && (Mem.LoadI32(c + 8) < 0 || SccOf(Mem.LoadI32(c + 4)) != g);
        }

        /// <summary>The node path, 2 of 3: JoinFresh's node (a plain object whose list takes its first item) has
        /// just joined as nothing's, and one other slot of it holds a member p that code refers to by more than
        /// that slot (its parent, set as the node was made): the link of that slot, as LinkJoinedSlot comes to
        /// it. The general way: ArenaLinkCore's questions, the merge of the two groups, LinkMembers (the slot's
        /// count given back, not p's last; a counted reference, the node not being below p yet), and then, for a
        /// p nothing owns, Waiting (side words for p, a place in LinkWaiting's list) and LinkWaiting's turn of
        /// that one pair, which finds the node without an owner and makes it p's (TakeOwnership: the first
        /// child-node owns its parent until it is put into the parent's list); for a p that has an owner, the
        /// node noted as the container waiting to own it next, and p's hold. Written here in that order with no
        /// list between: the same words but p's side words, which are not made for a p nothing owns (they would
        /// hold nothing after LinkWaiting). Only when no other slot of the node is linked in the same step
        /// (LinkWaiting runs after all of them) and nothing waits already. False, nothing written, otherwise:
        /// LinkJoinedSlot then.</summary>
        static bool NodeUp(int p, int e, int sl)
        {
            if (NoNodePath() || ((ThreadMode() ? t_failedLocal : s_failedTops) | s_reevN | s_linkN) != 0) return false;
            int cp = Mem.LoadI32(p);
            if (cp > -3) return false;
            if (p == e || PL(e, 20) != 0 || PL(e, 24) != 0) return false;
            if (SccOf(Mem.LoadI32(p + 4)) != SccOf(Mem.LoadI32(e + 4))) return false;
            if ((PL(p, 24) & (Dying | Queued)) != 0) return false;
            int ae = PL(e, 16), ap = PL(p, 16);
            if (Mem.LoadI64(ae + 12) != 0 || Mem.LoadI64(ap + 12) != 0) return false;
            int i = SlotIndex(e, sl), ow = PL(p, 20);
            if (i < 0 || ow == e) return false;
            OpStat(38);
            int ar = ae != ap ? ArenaMerge(ae, ap) : ae;
            if (cp != -3) Mem.StoreI32(p, cp + 1);
            else
            {
                // (the field's count is the parent's last from outside: given back here or after the store, as
                // LinkMembers decides it; the parent then does not wait, and is the node's at once all the same)
                Mem.StoreI32(p, -2);
                if (EarlyGiveBack(p, e, i, ar)) { OpStat(32); Mem.StoreI32(ar, Mem.LoadI32(ar) - 1); }
                else { Mem.StoreI32(p, -3); GiveBackLater(p); }
            }
            PS(p, 4, PL(p, 4) + 1);
            SetSlotBits(e, i, 1);
            if (ow == 0 || NodePathBreak() == 5)
            {
                TakeOwnership(p, e);
                if (cp == -3 && !HoldSettled(p)) UpdateHold(p);
            }
            else
            {
                if (PLW(p) == 0) { ClearMark(p); PSW(p, e); }
                if (!HoldSettled(p)) UpdateHold(p);
            }
            return true;
        }

        /// <summary>The node path, 3 of 3: the node is put into its parent's list, `arr[k] = e`, a member with no
        /// owner whose one reference from outside is the store's, into an array of its own group that is not
        /// being changed. LinkMembers' way: the count given back early (EarlyGiveBack, two walks up the array's
        /// owners to find the node above it and the member right below the node), a counted element, then
        /// TakeOwnership (the same two walks: the member the node owned above the array, its parent since its
        /// first child joined, stops being the node's when nothing below it refers above it and it holds
        /// nothing; the array owns the node), Reevaluate of the node's counted slots (its parent field becomes
        /// an up-reference), and the node's hold. Here one walk, asked before a word is written: the node found
        /// above the array within the chain's reach, with the member below it free to be let go, or not found
        /// above it at all (the top node of a new subtree put into a living tree: nothing turns round). False,
        /// nothing written, for anything else: a walk that ends in neither, a node that holds, is owned, is an
        /// array, is being destroyed, a note of a failed check anywhere, threads.</summary>
        static bool AddNode(int val, int container, int ar)
        {
            if (NoNodePath() || ThreadMode() && !SccLocal(Mem.LoadI32(container + 4)) || NoEarlyGiveBack()) return false;
            if (((ThreadMode() ? t_failedLocal : s_failedTops) | s_joinVisit | s_visitElem) != 0 || val == container) return false;
            int fl = PL(val, 24);
            if (PL(val, 20) != 0 || (fl & (Owned | Held | Dying | Queued | MultMask | Elems)) != 0) return false;
            if (Mem.LoadI32(ar) <= 1) return false;
            int cf = PL(container, 24);
            if ((cf & Dying) != 0) return false;
            int x = container, turn = 0;
            for (int steps = 0; ; steps++)
            {
                int w = PL(x, 20);
                if (w == val) { turn = x; break; }
                if (w == 0) break;
                if (steps == ChainMax - 1) return false;
                x = w;
            }
            if (turn != 0)
            {
                int xf = PL(turn, 24);
                if ((xf & (Dying | Queued)) != 0) return false;
                if (((uint)xf >= UOne || (xf & Held) != 0) && NodePathBreak() != 7) return false;
            }
            OpStat(39);
            Mem.StoreI32(val, Mem.LoadI32(val) + 1);
            Mem.StoreI32(ar, Mem.LoadI32(ar) - 1);
            PS(val, 4, PL(val, 4) + 1);
            PS(container, 28, PL(container, 28) + 1);
            PS(container, 24, cf | Elems);
            if (turn != 0) { PS(turn, 24, PL(turn, 24) & ~(Owned | MultMask)); PS(turn, 20, 0); }
            PS(val, 20, container);
            PS(val, 24, fl | Owned | MultOne);
            if ((PL(val, 28) & 0x55555555) != 0) Reevaluate(val);
            if (!HoldSettled(val)) UpdateHold(val);
            return true;
        }

        /// <summary>JoinFresh: a plain object whose one count is the reference of the slot that takes it becomes a
        /// member the holder of that slot owns, with no reference from outside: ArenaJoinOnly, and what JoinOwned
        /// writes after it, in one piece (the arena's number of members that code refers to as it was).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void JoinOne(int ar, int obj, int owner)
        {
            OpStat(4);
            Mem.StoreI32(obj, -2);
            PS(obj, 16, ar);
            PS(obj, 4, 1);
            PS(obj, 20, owner);
            PS(obj, 24, Owned | MultOne);
            RingAdd(ar, obj);
        }

        /// <summary>ArenaJoinOnly's part that is the arena's: the new member last on the ring, one more member, one
        /// more word that points to the record, and whether a member has a finalizer.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void RingAdd(int ar, int obj)
        {
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
            Mem.StoreI32(ar + 20, Mem.LoadI32(ar + 20) + 1);
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
            if (((PL(val, 24) | PL(container, 24)) & Dying) != 0)
            {
                // (a member that lives on took a dying one into a slot: noted for FinRevive)
                if (s_finDepth != 0 && ca < -1 && (PL(container, 24) & Dying) == 0) FinRecord(container);
                return val;
            }
            if (ca > 0 && Mem.LoadI32(container + 8) < 0) return val; // pinned
            if (cv > 0 && Mem.LoadI32(val + 8) < 0) return val;
            // (a member's plain part, PlainPart, is about to join: its holder's slot is linked first, as the store
            // it stands for)
            if (!NoPlainParts() && !ThreadMode())
            {
                if (ca > 0 && PL(container, 12) != 0 && PlainPartsBreak() != 1) { int hs = PartHolder(container); if (hs != 0) { PartLink(container, hs); ca = Mem.LoadI32(container); } }
                if (cv > 0 && val != container && PL(val, 12) != 0 && PlainPartsBreak() != 2) { int hs = PartHolder(val); if (hs != 0) { PartLink(val, hs); cv = Mem.LoadI32(val); } }
                if (cv == -1 || cv == 0 || ca == -1 || ca == 0) return val;
            }
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
            bool later = false;
            int i = s_visitElem != 0 ? -1 : SlotIndex(container, slot);
            if (Atomic.CountAdd32(ref Mem.RefI32(val), 1) == -3)
            {
                if (joined) Mem.StoreI32(ar, Mem.LoadI32(ar) - 1);
                // (given back here, where the release after the store would take the count off and do nothing else:
                // EarlyGiveBack)
                else if (EarlyGiveBack(val, container, i, ar)) { OpStat(32);Mem.StoreI32(ar, Mem.LoadI32(ar) - 1); }
                else { Atomic.CountAdd32(ref Mem.RefI32(val), -1); GiveBackLater(val); later = true; }
            }
            // (fresh: the value was a plain object until this store made it a member. No member is owned by a plain
            // object (an owner holds what it owns by a linked slot or is held by it, and a member with either does not
            // leave its arena, TryLeave), so the value is above nothing: not on the container's chain, and its
            // ownership turns nothing round. The walks up the chain that asked, three for a tree's leaf with its list,
            // each to the top of the reach, were a fifth of the links' time in the scene tree of tools/frames.mjs.)
            bool fresh = joined && !NoFreshJoin();
            if (i >= 0 && !fresh && OnChainOwned(container, val))
            {
                // an up-reference: not counted (the chain from the container to the value keeps the value)
                OpStat(23);
                SetSlotBits(container, i, 2);
                if (container == s_lastC) LastGiveBack(container, val, later);
                UpPath(container, val, 1);
                // (a value nothing else refers to kept its count: it goes when the store is written, AfterStore)
                if (Mem.LoadI32(val) == -2 && PL(val, 4) == 0) Kill(val);
                else if (!HoldSettled(val)) UpdateHold(val);
                return val;
            }
            PS(val, 4, PL(val, 4) + 1);
            if ((ThreadMode() && SccLocal(Mem.LoadI32(val + 4)) ? t_failedLocal : s_failedTops) != 0 && OutsideMarked(PLM(val))) NoteOutside(val, container, 1);
            if (i >= 0) SetSlotBits(container, i, 1);
            else if (slot == 0 || s_visitElem != 0)
            {
                PS(container, 28, PL(container, 28) + 1);
                PS(container, 24, PL(container, 24) | Elems);
            }
            else PS(container, 24, PL(container, 24) | Far);
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
                // A reference a plain object's slot took while it was plain, linked now that the object joins (its
                // slots, ArenaLinkJoined), to a member without an owner that code still holds from outside: whether this
                // container is its owner is decided when the links are done. A tree built from the leaves up (a node's
                // children made, each with its parent in a field, before the node is in its own parent's list) links a
                // child's parent field before the node's list is the node's: the node was owned by its first child,
                // every node, and turned round when it was put in its list. Once the node's own slots are linked, the
                // node is above the child, and the child's field is an up-reference (Reevaluate); otherwise the
                // container owns it, as it would have at the link.
                else if (ow == 0 && s_joinVisit != 0 && Mem.LoadI32(val) < (later ? -3 : -2) && val != container)
                {
                    Waiting(val, container);
                }
                // The same for a store into a member's own slot of a plain object that code still holds, which joins
                // for it with its slots still to be linked (it waits in ArenaJoin's list, so ArenaLinkJoined and
                // LinkWaiting run before this store's link ends): `e.Children.Add(c); c.Parent = e;`, the parent set
                // after the child is in the list, where the node, its list and its array's owner are plain objects
                // until the first child's parent field is stored. Owned by that child at once, every node was the
                // property of its first child (and the child no longer its array's, the ownership turned round
                // as the node's list took the array), each first child a top held by its own tree: 1,600 checks at
                // the drop a build of the scene tree of tools/frames.mjs, the old arrays of the lists' growths
                // handed over the long way, 5.5 ms a build for the other order's 1.9.
                else if (ow == 0 && fresh && i >= 0 && s_joinVisit == 0 && s_linkN != 0 && !NoStoreWaits() && Mem.LoadI32(val) < -2)
                {
                    s_waitSlot = slot; s_waitObj = container; s_waitVal = val;
                    Waiting(val, container);
                }
                else if (ow == 0 && fresh)
                {
                    // (TakeOwnership without its walk; and none of its own slots is linked yet, so none to look at again)
                    OpStat(30);
                    PS(val, 20, container);
                    PS(val, 24, fl | Owned | MultOne);
                }
                // (FinRevive: a member that code still refers to is not made the property of one that came back: let
                // go, the one that came back would be kept by the check at the drop for what it owns that code holds)
                else if (ow == 0) { if (s_reviving == 0 || Mem.LoadI32(val) >= (later ? -3 : -2)) TakeOwnership(val, container); }
                // (the first other container that holds it is the one to own it next: a later one, a task's
                // result, a temporary array, is as often gone by then)
                else if (PLW(val) == 0) { ClearMark(val); PSW(val, container); }
            }
            if (!HoldSettled(val)) UpdateHold(val);
            return val;
        }

        /// <summary>The store's count on the value, its last from outside, can be given back as the reference is
        /// linked, not after the store is written (GiveBackLater): a member with no owner that holds nothing, which
        /// the container owns from this link on (not one linked as an up-reference, nor one left waiting), in an
        /// arena other members of which code still refers to. The release after the store would then take the count
        /// off and do nothing else (ArenaRelease's first case: the arena lives, the member is referred to from inside
        /// and owned, and wants no hold), but until it did, the member, referred to from elsewhere than its owner,
        /// held its new owners up the chain when a slot of its own became an up-reference (Reevaluate: a node put
        /// into its parent's list, its parent field set before), and let them go at the release: six hold changes
        /// and the release the long way for each node with children in the scene tree of tools/frames.mjs.
        /// CS2WASM_NO_EARLY_GIVE_BACK=1: after the store, always.</summary>
        static bool EarlyGiveBack(int val, int container, int i, int ar)
        {
            if (ThreadMode() && !SccLocal(Mem.LoadI32(container + 4)) || NoEarlyGiveBack() || val == container) return false;
            if (PL(val, 20) != 0 || (PL(val, 24) & (Owned | Held | Dying | Queued)) != 0) return false;
            if (Mem.LoadI32(ar) <= 1 || Mem.LoadI64(ar + 12) != 0) return false;
            if ((ThreadMode() ? t_failedLocal : s_failedTops) != 0 && (PLM(val) != 0 || PLM(container) != 0)) return false;
            if (!OnChain(container, val)) return true;
            // (above the container. In one of the container's own slots the reference is an up-reference, not counted:
            // the value then has the store's count alone. In an array's element the ownership turns round, if
            // TakeOwnership's test says so, asked here as there: a node that owned its parent since its first child's
            // parent field was linked, put into the parent's list. If not, the value stays without an owner, and
            // the release after the store checks it at the drop)
            if (i >= 0) return false;
            int x = container;
            for (int k = 0; k <= ChainMax && PL(x, 20) != val; k++) x = PL(x, 20);
            int xf = PL(x, 24);
            return PL(x, 20) == val && (uint)xf < UOne && (xf & Held) == 0;
        }

        // (the container of the store under way, when the store is the last use of the local that holds it and that
        // local's is its one reference from outside, ArenaLastBegin; and the container whose count was given back)
        [Intrinsic] static int s_lastC, s_lastGiven;

        /// <summary>Before the link of a store `c.F = v;` that is the last use of the local c, which owns its count
        /// (Compiler/LocalMoves.cs, IsLastContainer; CS2WASM_NO_LAST_CONTAINER=1 at compile time: not called): a
        /// member whose one reference from outside is that local's is noted for the link.</summary>
        public static void ArenaLastBegin(int c)
        {
            if (c != 0 && Mem.LoadI32(c) == -3 && (!ThreadMode() || SccLocal(Mem.LoadI32(c + 4)))) s_lastC = c;
        }

        /// <summary>After that link: 0 when the local's count was given back (the local is cleared: the release at
        /// the end of its scope finds nothing), else the container.</summary>
        public static int ArenaLastEnd(int c)
        {
            s_lastC = 0;
            if (s_lastGiven == 0) return c;
            s_lastGiven = 0;
            return 0;
        }

        /// <summary>The link makes a slot of the noted container an up-reference (`e.Children.Add(c); c.Parent = e;`,
        /// the parent set after the child is in its list, c a local not used again). With the local's count still
        /// on it, the container, a member with an up-reference below it that is referred to from elsewhere than its
        /// owner, would hold its owner, that one its own, up to the value, and let them all go when the local's
        /// scope ends, the release the long way: six hold changes a child, 117,000 a build of the scene tree of
        /// tools/frames.mjs in that order. The local's count is given back here instead, where the release would
        /// take it off and do nothing else had it come before the store (ArenaRelease's first case, asked as there:
        /// the arena lives by other members, the container is owned and referred to from inside, holds nothing and
        /// has no up-reference below it yet, no note of a failed check), and the value does not need the hold: it
        /// is referred to from inside, or from outside by more than the store's own count (`later`: that count is
        /// still on it). The container lives on by its owner; nothing of the program runs before the store is
        /// written, and after the statement nothing of the method refers to it.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void LastGiveBack(int c, int val, bool later)
        {
            s_lastC = 0;
            int ar = PL(c, 16), fl = PL(c, 24), total = Mem.LoadI32(ar) - 1;
            if (Mem.LoadI32(c) != -3 || total == 0 || Mem.LoadI64(ar + 12) != 0 || PL(c, 4) == 0) return;
            if ((fl & (Owned | Held | Dying | Queued)) != Owned || (uint)fl >= UOne) return;
            if ((ThreadMode() ? t_failedLocal : s_failedTops) != 0 && (PLM(c) != 0 || PLM(val) != 0)) return;
            if (PL(val, 4) == 0 && Mem.LoadI32(val) >= (later ? -3 : -2)) return;
            OpStat(34);
            Mem.StoreI32(c, -2);
            Mem.StoreI32(ar, total);
            s_lastGiven = c;
        }

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
            if (!CanLink(old, container)) { OpStat(8); Release(Borrow<object>(old)); return; }
            if (ThreadMode() && PinnedPlain(container)) { Release(Borrow<object>(old)); return; }
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
                // (one destroyed here keeps its owner while it is: the up-references from it and from what it owns are
                // taken off the u of the members above it as each goes, UpPath walking the owners. Let go first, they
                // stayed on the container and its owners for good: a list's array that kept them refused its Handover
                // when the list grew, and the items it still held stayed with it until the whole group went. In the
                // scene tree of tools/frames.mjs, a subtree replaced under a node that later took more children: a
                // third of the group.)
                if (n != 0 || co != -2) OwnerLetGo(old, container);
                // (a container replaced by another in the same slot, a list's array by a larger one: what it owned is
                // the new one's from here on)
                if (owned && n > 0 && slot != 0) { Handover(old, Mem.LoadI32(slot), container); n = PL(old, 4); }
                // (let go by its owner and by the code, kept by the holds of its own items alone: ShedHolds)
                if (n > 0 && co == -2 && (PL(old, 24) & (Owned | Dying)) == 0) { ShedHolds(old); n = PL(old, 4); }
                bool gone = false;
                if (n == 0 && co == -2) ArenaDestroyFrom(old, 0);
                else
                {
                    bool adopted = false;
                    if (PL(old, 20) == 0) { Adopt(old); adopted = PL(old, 20) != 0; }
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
                    int back = (ThreadMode() && SccLocal(Mem.LoadI32(old + 4)) ? t_failedLocal : s_failedTops) != 0 && OutsideMarked(PLM(old)) ? NoteOutside(old, container, -1) : 0;
                    bool died = back != 0 && Dropped(back);
                    if (!died && back != old && (ownerEdge || (PLM(old) & 1) == 0) && DropCandidate(old)) died = Dropped(old);
                    // (let go by its owner, it is now the property of another container that holds it, and that one
                    // may be its own: an array it owns whose element refers back to it, `n.Raw[6] = n`, test 247.
                    // The ownership has turned round, the array is the top, nothing's, with no reference from
                    // outside, kept by what it owns alone: the check is the new top's. Without it the member
                    // stayed until its group went. CS2WASM_NO_ADOPT_CHECK=1: not asked)
                    if (!died && adopted && !NoAdoptCheck())
                    {
                        int t = Top(old);
                        if (t != 0 && t != old && t != back && (PLM(t) & 1) == 0 && DropCandidate(t)) died = Dropped(t);
                    }
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

        /// <summary>Threads: the one write of a static readonly field (Compiler/Arenas.cs PinFixed): pinned, and an
        /// array with no element made immortal. It holds nothing and the field holds it to the program's end, so
        /// nothing changes but that its count is not written again by every thread that counts it (the empty array
        /// every new list starts with: eight threads that each made lists took its cache line from each other at
        /// every count, and every read of it, a leaf's list's length, waited for that). An immortal object joins no
        /// group (ArenaLinkCore, JoinFresh). CS2WASM_EXP_FIXED_PINS_ANY=1 (a test of the tests): any object.</summary>
        public static int ArenaPinFixed(int val)
        {
            ArenaPin(val);
            if (val == 0) return val;
            if (!ExpFixedPinsAny() && (TypeInfo.ElementId(Mem.LoadI32(val + 4)) < 0 || Mem.LoadI32(val + 12) != 0)) return val;
            int c;
            do { c = Atomic.Load32(ref Mem.RefI32(val)); if (c <= 0) return val; } while (Atomic.CompareExchange32(ref Mem.RefI32(val), c, -1) != c);
            return val;
        }
        [Intrinsic] static extern bool ExpFixedPinsAny();

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

        /// <summary>A slot of an object that joined an arena (ArenaLinkJoined), linked as the store it stands for: a
        /// plain object only this slot refers to joins in one piece (JoinOwned); a member of the object's arena,
        /// neither dying, goes to LinkMembers as ArenaLink sends it; anything else to ArenaLinkCore.
        /// (Out of line, as JoinOwned and ReplaceArray are: written into ArenaVisit and ArenaLink they moved the
        /// engine's change of tier into the timed frames of the strings scene of tools/frames.mjs, one frame of
        /// 0.5 ms; the same was seen for LinkWaiting.)</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void LinkJoinedSlot(int child, int slot)
        {
            int o = s_visitArena, cc = Mem.LoadI32(child);
            if (!NoFreshJoin() && !ThreadMode())
            {
                // (a pinned object, the empty array every new list has: it joins nothing, ArenaLinkCore's answer
                // after its questions about the two types)
                if (cc > 1 && Mem.LoadI32(child + 8) < 0) return;
                if (ThreadMode() && cc == -1) return;
                if (cc == 1) { if (JoinOwned(child, o, slot, false)) return; }
                else if (cc < -1 && Mem.LoadI32(o) < -1)
                {
                    int ar = PL(o, 16);
                    if (ar == PL(child, 16) && Mem.LoadI64(ar + 12) == 0 && ((PL(child, 24) | PL(o, 24)) & Dying) == 0) { LinkMembers(child, o, slot, ar, false); return; }
                }
            }
            ArenaLinkCore(child, o, slot);
        }

        public static void ArenaVisit(int slot)
        {
            int child = Mem.LoadI32(slot);
            // (0, a dying member's slot, before the other modes' tests: a subtree let go at a store is one such
            // visit for each slot of each of its members, the scene tree of tools/frames.mjs 343 members a frame)
            if (s_visitMode == 0)
            {
                if (child == 0 || Mem.LoadI32(child) >= -1) return;
                if (ThreadMode() && !GroupHeld(child)) return;
                VisitDying(slot, child);
                return;
            }
            if (ArenaReport() && s_visitMode == 20)
            {
                if (child == s_repTarget && s_repHitN < 16)
                {
                    int ri = s_visitElem != 0 ? -1 : SlotIndex(s_visitObj, slot);
                    Mem.StoreI32(s_repHits + 12 * s_repHitN, s_visitObj);
                    Mem.StoreI32(s_repHits + 12 * s_repHitN + 4, slot - s_visitObj);
                    Mem.StoreI32(s_repHits + 12 * s_repHitN + 8, ri >= 0 ? SlotBits(s_visitObj, ri) : -1);
                    s_repHitN++;
                }
                return;
            }
            // (4: each reference, counted by the copy, as a store of it)
            if (s_visitMode == 4) { if (child != 0 && slot != s_storeSlot) LinkJoinedSlot(child, slot); return; }
            // (16: LinkWalk, threads)
            if (ThreadMode() && s_visitMode == 16) { WalkVisit(child); return; }
            // (13 to 15, Dropped: what the member owns; a counted reference to one of them off, and back on)
            if (s_visitMode >= 13)
            {
                // (21, FinRevive: a slot of a member that lives on which holds one of the members coming back)
                if (s_visitMode == 21)
                {
                    if (child == 0 || SccOf(Mem.LoadI32(child + 4)) == 0) return;
                    int tc = Mem.LoadI32(child);
                    if (tc >= FinTag + FinBias && tc < FinTag + 2 * FinBias && (PL(child, 24) & Dying) != 0) FixPush(s_visitObj, slot, s_visitElem, child);
                    return;
                }
                if (child == 0 || Mem.LoadI32(child) >= -1) return;
                if (ThreadMode() && !GroupHeld(child)) return;
                int cf = PL(child, 24);
                if (s_visitMode == 13)
                {
                    if (PL(child, 20) == s_visitObj && (cf & (Owned | Dying)) == Owned) { PS(child, 24, cf | Dying); TrPush(child); }
                    // (a member the set refers to that is under another top, one with no owner that no code refers to:
                    // that top in the trial too, with what it owns. Two tops that refer into each other's trees are one
                    // garbage structure, and each tried alone fails by the other's reference. That happens when a
                    // member's owner goes and the one container waiting to own it next is gone too, while another
                    // still holds it: tasks' results, test 127)
                    else if (s_trExt > 0 && (cf & (Owned | Dying)) == 0 && Mem.LoadI32(child) == -2)
                    { PS(child, 24, cf | Dying); TkPush(child); s_trExt++; }
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
                if (bits == 1) ReevaluateSlot(obj, i, child);
                return;
            }
        }

        /// <summary>CS2WASM_NO_DYING_UP=1 (at compile time): a dying member's up-reference to a dying member is taken
        /// off the u of the members between them (VisitDying).</summary>
        [Intrinsic] static extern bool NoDyingUp();

        /// <summary>ArenaVisit's mode 0: a slot of a member that is being destroyed (s_visitObj) holds a member
        /// (child): the reference is taken out; a member that loses its last dies too.</summary>
        static void VisitDying(int slot, int child)
        {
            int obj = s_visitObj;
            int i = s_visitElem != 0 ? -1 : SlotIndex(obj, slot);
            int bits = i >= 0 ? SlotBits(obj, i) : -1;
            if (bits == 2)
            {
                if (s_cutOn != 0) NoteCut(slot, child);
                Mem.StoreI32(slot, 0); SetSlotBits(obj, i, 0);
                // (an up-reference to a member that is dying too, every member between the two dying with them: a
                // child's parent field in a subtree that goes. The u of members that are being destroyed is read by
                // nothing (UpdateHold and HoldSettled end at Dying), so the walk up that takes the reference off
                // each, and the second that settles each one's hold, are left out: 5 ns a member of 32 in the scene
                // tree of tools/frames.mjs)
                if (!NoDyingUp() && (PL(child, 24) & Dying) != 0 && DyingPath(obj, child)) return;
                UpPath(obj, child, -1);
                return;
            }
            // (an outside-counted reference stays in the slot for the release every other reference of the dying
            // object gets; so does one whose kind is not known when the member has no inside count)
            if (bits == 0 || ArenaOf(child) != s_visitArena || PL(child, 4) == 0) return;
            if (s_cutOn != 0) NoteCut(slot, child);
            Mem.StoreI32(slot, 0);
            if (i >= 0) SetSlotBits(obj, i, 0);
            int left = PL(child, 4) - 1;
            PS(child, 4, left);
            // (one that dies with it keeps its owner, as the first of them does: ArenaUnlinkOld)
            if (left == 0 && Mem.LoadI32(child) == -2) { Kill(child); return; }
            OwnerLetGo(child, obj);
            if (PL(child, 20) == 0) Adopt(child);
            UpdateHold(child);
            if ((ThreadMode() && SccLocal(Mem.LoadI32(child + 4)) ? t_failedLocal : s_failedTops) != 0 && OutsideMarked(PLM(child))) DyingNote(child, obj);
            // (a survivor of the cut that nothing outside refers to may be kept by stale holds of what it owns alone:
            // checked at the drop when the destruction is done, DrainKills)
            if (StickyHolds() && Mem.LoadI32(child) == -2 && (PL(child, 24) & Owned) == 0) QueueCheck(child);
        }

        /// <summary>CS2WASM_NO_DYING_NOTE=1 (at compile time): a reference that goes with its dying holder is not
        /// taken off the number of a top whose check failed (DyingNote).</summary>
        [Intrinsic] static extern bool NoDyingNote();

        /// <summary>A dying member (obj) referred to a member that lives on (child) under a top whose check at the
        /// drop failed by references from outside what it owns: this was one of them, and it is gone, as at an
        /// unlink (ArenaUnlinkOld asks NoteOutside the same). At zero the top is checked again once the
        /// destruction is done (DrainKills). Until 2026-11-23 only a store took such a reference off the number:
        /// a ring of cells kept by one cell of another array stayed, when that array went, until the group did
        /// (test 249).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void DyingNote(int child, int obj)
        {
            if (NoDyingNote()) return;
            int t = NoteOutside(child, obj, -1);
            if (t != 0 && (PL(t, 24) & (Dying | Queued)) == 0) { KillPush(t); PS(t, 24, PL(t, 24) | Queued); }
        }

        /// <summary>The target is above the dying member on its chain, and every member between them is dying:
        /// what UpPath would walk (it ends where the chain does, or ChainMax steps up), with nothing left alive on
        /// the way whose u a reference that goes would have to come off.</summary>
        static bool DyingPath(int from, int target)
        {
            int v = from;
            for (int k = 0; k <= ChainMax; k++)
            {
                if (v == target) return true;
                if (v == 0 || (PL(v, 24) & Dying) == 0) return false;
                v = PL(v, 20);
            }
            return false;
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
            if (Mem.LoadI32(ar + 8) == 0 && Mem.LoadI32(ar) == 0 && (Mem.LoadI32(ar + 12) & ArenaBusy) == 0) { MemStatsRecord(-1); ReportForget(ar); Free(ar, ArenaRecord); }
        }

        // While the finalizers of members that die together run, every reference to one of them is counted, from
        // wherever: the ones their slots held are put back counted, and a finalizer's own stores count as any store
        // (a member that is dying joins no group: ArenaLinkCore). Each has FinBias on its count meanwhile, so no
        // release destroys it a second time. Afterwards the references they hold to each other are taken off the
        // counts (their slots, visited as when they were let go): a count that is left is a reference from where the
        // program can reach it, and then they all live on.
        const int FinBias = 0x10000000;

        static int FinNext(int m, int first, int link) { int n = PL(m, link); return n == first ? 0 : n; }

        [Intrinsic] static extern int WeakAny();
        [Intrinsic] static extern void WeakDying(int addr);

        /// <summary>The members that die together (a list through the prefix word `link`: 8 ending in 0, 12 a ring):
        /// the weak references to each are cleared, before a finalizer of any runs and before any is cleared, as
        /// .NET clears the short weak references to all that became unreachable together first (test 245; until
        /// 2026-11-22 a member's were cleared at its own turn, and a finalizer of one member read a weak reference
        /// to the next alive). One call and a test for a program, or a moment, without a marked object.</summary>
        static void WeakDyingAll(int first, int link)
        {
            if (WeakAny() == 0) return;
            int end = link == 12 ? first : 0;
            WeakSlots.Hold();
            for (int m = first; m != 0; m = FinNext(m, end, link)) WeakDying(m);
            WeakSlots.Release();
        }

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
            WeakDyingAll(first, link);
            int recBase = s_frN;
            s_finDepth++;
            for (int m = first; m != 0; m = FinNext(m, end, link)) ArenaFinalizeObject(m);
            FinVisit(first, link, 10);
            bool back = false;
            for (int m = first; m != 0; m = FinNext(m, end, link)) if (Mem.LoadI32(m) != FinBias) { back = true; break; }
            if (back)
            {
                FinVisit(first, link, 11);
                s_cutN = cutBase;
                if (NoFinRevive()) for (int m = first; m != 0;) { int next = FinNext(m, end, link); Mem.StoreI32(m, -1); m = next; }
                else FinRevive(first, link, recBase);
                FinDone();
                return true;
            }
            FinVisit(first, link, 12);
            CutsOut(cutBase, cutEnd);
            s_cutN = cutBase;
            for (int m = first; m != 0; m = FinNext(m, end, link)) Mem.StoreI32(m, 0);
            FinDone();
            return false;
        }

        /// <summary>CS2WASM_NO_FIN_REVIVE=1 (at compile time): members a reference was left to after their finalizers
        /// are never freed, nor what they refer to (FinRevive).</summary>
        [Intrinsic] static extern bool NoFinRevive();
        [Intrinsic] static extern bool NoAdoptCheck();

        // (FinPhase under way, how deep; the members that live on which took a dying member into a slot meanwhile,
        // each counted once more while it is in the list; FinRevive's slots to link: container, slot, element, value)
        static int s_finDepth, s_frQ, s_frN, s_frCap, s_fxQ, s_fxN, s_fxCap, s_reviving;
        const int FinTag = 0x20000000;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void FinRecord(int c)
        {
            if (s_frN != 0 && Mem.LoadI32(s_frQ + 4 * (s_frN - 1)) == c) return;
            if (s_frN == s_frCap)
            {
                int cap = s_frCap == 0 ? 16 : s_frCap * 2;
                int nq = Alloc(cap * 4);
                if (s_frQ != 0) { Mem.Copy(nq, s_frQ, s_frN * 4); Free(s_frQ, s_frCap * 4); }
                s_frQ = nq; s_frCap = cap;
            }
            Mem.StoreI32(s_frQ + 4 * s_frN, c);
            s_frN++;
            Retain(Borrow<object>(c));
        }

        static void FixPush(int c, int slot, int elem, int child)
        {
            if (s_fxN == s_fxCap)
            {
                int cap = s_fxCap == 0 ? 16 : s_fxCap * 2;
                int nq = Alloc(cap * 16);
                if (s_fxQ != 0) { Mem.Copy(nq, s_fxQ, s_fxN * 16); Free(s_fxQ, s_fxCap * 16); }
                s_fxQ = nq; s_fxCap = cap;
            }
            int e = s_fxQ + 16 * s_fxN;
            Mem.StoreI32(e, c); Mem.StoreI32(e + 4, slot); Mem.StoreI32(e + 8, elem); Mem.StoreI32(e + 12, child);
            s_fxN++;
        }

        /// <summary>A FinPhase is over: with the outermost, the members noted meanwhile are let go.</summary>
        static void FinDone()
        {
            if (--s_finDepth != 0) return;
            while (s_frN > 0)
            {
                s_frN--;
                int c = Mem.LoadI32(s_frQ + 4 * s_frN);
                if (c != 0) Release(Borrow<object>(c));
            }
        }

        /// <summary>After the finalizers of members that die together, a reference to one of them is left that is
        /// not one of their own (FinPhase; their own are back on the counts): a finalizer stored one where the
        /// program can reach it, or into an object it made, which may itself be reachable only from them (a list
        /// of a dying member that grew: the new array, the new items that refer back; test 244). Which of the two
        /// is not looked for. The members become what they were before any store linked them: plain objects,
        /// each reference counted where it is held (the finalizers ran while every reference to and from a dying
        /// member was a counted one, ArenaLinkCore). Then the references are linked as the stores they stand
        /// for would have been: a slot of a member that lives on which took one of them (noted at the store,
        /// FinRecord), and each one's own slots (ArenaLinkContents; an object a finalizer made is linked as it
        /// joins). Then the count each was given for the while is let go: what the program can reach lives on as
        /// any object does (its finalizer has run: not again, unless GC.ReRegisterForFinalize asks), and the rest
        /// goes by the counts, at once or as a group of its own with the finalizers of the objects made
        /// meanwhile, as .NET frees it at its next collection. (Until 2026-11-23 the members were never freed.)</summary>
        static void FinRevive(int first, int link, int recBase)
        {
            int end = link == 12 ? first : 0, n = 0;
            // (a noted member nothing else refers to any more, a list's array that was grown out of since: it goes
            // now, before anything is linked to it. Left until the end, it kept what it owned from the member
            // that holds the same items now, and the members coming back, referred to from under it, stayed)
            if (s_finDepth == 1)
                for (int r = recBase; r < s_frN; r++)
                {
                    int c = Mem.LoadI32(s_frQ + 4 * r);
                    if (c == 0 || Mem.LoadI32(c) != -3 || PL(c, 4) != 0) continue;
                    for (int q = r; q < s_frN; q++) if (Mem.LoadI32(s_frQ + 4 * q) == c) Mem.StoreI32(s_frQ + 4 * q, 0);
                    Release(Borrow<object>(c));
                }
            for (int m = first; m != 0; m = FinNext(m, end, link)) n++;
            int list = Alloc(n * 4 + 4);
            // (the list from the top down: a part's members are listed last dead first, a whole group's as they
            // joined. Bit 0 of an entry: the member had an owner among them, and is linked from above)
            int at = 0;
            for (int m = first; m != 0; m = FinNext(m, end, link))
            {
                int ow = PL(m, 20);
                int under = (PL(m, 24) & Owned) != 0 && ow != 0 && Mem.LoadI32(ow) >= FinBias && (PL(ow, 24) & Dying) != 0 ? 1 : 0;
                Mem.StoreI32(list + 4 * (link == 12 ? at : n - 1 - at), m | under);
                at++;
            }
            for (int i = 0; i < n; i++) { int m = Mem.LoadI32(list + 4 * i) & ~1; Mem.StoreI32(m, Mem.LoadI32(m) + FinTag); }
            // the slots of members that live on which hold one of them (each such member once)
            int fx0 = s_fxN;
            int sa = s_visitArena, sm = s_visitMode, so = s_visitObj, se = s_visitElem;
            for (int r = recBase; r < s_frN; r++)
            {
                int c = Mem.LoadI32(s_frQ + 4 * r);
                if (c == 0 || Mem.LoadI32(c) >= -1 || (PL(c, 24) & Dying) != 0) continue;
                bool seen = false;
                for (int q = recBase; q < r && !seen; q++) seen = Mem.LoadI32(s_frQ + 4 * q) == c;
                if (seen) continue;
                s_visitMode = 21; s_visitObj = c; s_visitElem = 0;
                ArenaVisitObject(c);
            }
            s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se;
            // plain objects again, each with its counts and one more for the while
            for (int i = 0; i < n; i++)
            {
                int m = Mem.LoadI32(list + 4 * i) & ~1;
                int c = Mem.LoadI32(m) - FinTag - FinBias + 1;
                MemberWordsFree(m);
                Mem.StoreI32(m, c);
            }
            // (one nothing refers to goes now, as the plain object it is)
            for (int i = 0; i < n; i++)
            {
                int m = Mem.LoadI32(list + 4 * i) & ~1;
                if (Mem.LoadI32(m) != 1) continue;
                Mem.StoreI32(list + 4 * i, 0);
                Release(Borrow<object>(m));
            }
            s_reviving++;
            // the links, a container's before those of what it holds, as the program made them: the members that
            // had no owner among the others, each with all that its slots reach
            for (int i = 0; i < n; i++)
            {
                int e = Mem.LoadI32(list + 4 * i);
                if (e != 0 && (e & 1) == 0 && Mem.LoadI32(e) > 0) ArenaLinkContents(e);
            }
            // then the slots of the members that live on
            int outer = s_storeSlot;
            for (int i = fx0; i < s_fxN; i++)
            {
                int e = s_fxQ + 16 * i;
                int c = Mem.LoadI32(e), slot = Mem.LoadI32(e + 4), child = Mem.LoadI32(e + 12);
                if (Mem.LoadI32(slot) != child || Mem.LoadI32(c) >= -1) continue;
                sa = s_visitArena; sm = s_visitMode; so = s_visitObj; se = s_visitElem;
                s_visitArena = c; s_visitMode = 4; s_visitObj = c; s_visitElem = Mem.LoadI32(e + 8); s_storeSlot = slot;
                LinkJoinedSlot(child, slot);
                s_visitArena = sa; s_visitMode = sm; s_visitObj = so; s_visitElem = se;
                if (s_linkN != 0) ArenaLinkJoined();
                s_storeSlot = outer;
            }
            s_fxN = fx0;
            if (s_killN != 0) DrainKills();
            if (t_backN != 0) AfterStore();
            // then what none of that reached
            for (int i = 0; i < n; i++)
            {
                int m = Mem.LoadI32(list + 4 * i) & ~1;
                if (m != 0 && Mem.LoadI32(m) > 0) ArenaLinkContents(m);
            }
            s_reviving--;
            // (the noted members let go before the ones coming back are: as above)
            if (s_finDepth == 1)
                while (s_frN > recBase)
                {
                    s_frN--;
                    int c = Mem.LoadI32(s_frQ + 4 * s_frN);
                    if (c != 0) Release(Borrow<object>(c));
                }
            // (from the bottom up: the top's count is the last to go)
            for (int i = n - 1; i >= 0; i--) { int m = Mem.LoadI32(list + 4 * i) & ~1; if (m != 0) Release(Borrow<object>(m)); }
            Free(list, n * 4 + 4);
        }

        /// <summary>FinPhase with the cut references in a block (q, from..to). `unlocked`: run after the runtime lock
        /// was given back (RunDeferredDestroys): the visits take it, the finalizers run without it.</summary>
        static bool FinPhaseCuts(int first, int link, int q, int from, int to, bool unlocked = true, bool prepared = false)
        {
            int end = link == 12 ? first : 0;
            // (prepared: done already, under the lock, FinPrepare)
            if (!prepared) FinPrepare(first, link, q, from, to);
            WeakDyingAll(first, link);
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
                    if (!fin) WeakDyingAll(first, link);
                    for (int m = first; m != 0; m = FinNext(m, end, link)) ArenaClearObject(m);
                    for (int m = first; m != 0;) { int next = FinNext(m, end, link); ArenaFreeObject(m); m = next; }
                }
                // (a whole arena whose member came back keeps its record, busy, for the words that name it; a part's
                // record was dealt with when it was taken out)
                if (link == 12 && !back) { RuntimeLock(); { MemStatsRecord(-1); ReportForget(ar); Free(ar, ArenaRecord); } RuntimeUnlock(); }
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
            // (threads: in a group that is one thread's, as without: nothing another thread can see of it)
            if (ThreadMode() ? SccLocal(Mem.LoadI32(p + 4)) && !NoFastDestroy() && !ArenaReport() && Mem.LoadI32(ar + 24) == 0
                : SingleThreaded() && !NoFastDestroy() && !ArenaReport() && Mem.LoadI32(ar + 24) == 0) { ArenaDestroyPlain(p, rest, ar); return; }
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
            if (!fin) WeakDyingAll(done, 8);
            for (int m = done; m != 0; m = PL(m, 8)) ArenaClearObject(m);
            for (int m = done; m != 0;)
            {
                int next = PL(m, 8);
                ArenaFreeObject(m);
                m = next;
            }
            ArenaEmpty(ar);
        }

        /// <summary>CS2WASM_NO_FAST_DESTROY=1 (at compile time): a member of a group without finalizers is destroyed
        /// by ArenaDestroyFrom's own passes (ArenaDestroyPlain).</summary>
        [Intrinsic] static extern bool NoFastDestroy();

        /// <summary>ArenaDestroyFrom in a group no member of which has a finalizer, without threads (p is off the
        /// ring): the same steps (each dying member gives up its references to members, those that lose their
        /// last die too; every count zero; then each one's other references released; then the memory freed),
        /// without what only finalizers and threads need: no references noted for a finalizer to see, the visit's
        /// state set once for the pass and not around each member, a member's count zero as its turn comes, its
        /// linked references read by its slot bits or as an array's elements where that is all of them
        /// (CS2WASM_NO_DESTROY_BY_BITS=1: by its type's visit function), and the blocks back onto their pages
        /// without the dispatch on the type (ArenaFreeList's way). A subtree replaced in a tree is destroyed here, member by
        /// member through its owners: 343 members a frame in the scene tree of tools/frames.mjs, 32 ns each where the
        /// drop of a whole group takes 6 to 12.</summary>
        static void ArenaDestroyPlain(int p, int rest, int ar)
        {
            PS(p, 12, rest);
            PS(p, 24, PL(p, 24) | Dying);
            int dead = p, done = 0;
            int cutOn = s_cutOn;
            s_cutOn = 0;
            s_destroying++;
            long t0 = DropStats() ? Engine.MonotonicNs() : 0;
            int sa = s_visitArena, sm = s_visitMode, sd = s_visitDead, so = s_visitObj, se = s_visitElem;
            // (what runs inside a visit and visits in another mode puts these back: Reevaluate, TryLeave, Handover)
            s_visitArena = ar; s_visitMode = 0; s_visitElem = 0;
            while (dead != 0)
            {
                int m = dead;
                dead = PL(m, 12);
                // (its count zero as its turn comes, not in a pass after the visits: nothing of the program runs
                // before the last of them, and a reference to it that a later one holds is left in its slot, as one
                // to a member that died before always was, for the clear's release, which does nothing at zero)
                Mem.StoreI32(m, 0); PS(m, 16, 0); ClearMark(m);
                int fl = PL(m, 24);
                if ((fl & (Queued | Held)) != 0)
                {
                    Unqueue(m);
                    // (what it held of its owner goes back)
                    fl = PL(m, 24);
                    if ((fl & Held) != 0)
                    {
                        int w = PL(m, 20);
                        PS(m, 24, fl & ~Held);
                        int n = PL(w, 4) - 1;
                        PS(w, 4, n);
                        s_visitDead = dead;
                        if (n == 0 && Mem.LoadI32(w) == -2) Kill(w); else UpdateHold(w);
                        dead = s_visitDead;
                    }
                }
                if (NoDestroyByBits() || (fl & Far) != 0) { s_visitDead = dead; s_visitObj = m; ArenaVisitObject(m); dead = s_visitDead; }
                else if ((fl & Elems) == 0)
                {
                    // (its linked references are those its bits say: read here, with no visit of the object by its
                    // type, what VisitDying does for each written out)
                    int w = PL(m, 28), cleared = 0;
                    for (int k = 0; w != 0; k++, w = (int)((uint)w >> 2))
                    {
                        int b = w & 3;
                        if (b == 0) continue;
                        int slot = m + 12 + 4 * k, child = Mem.LoadI32(slot);
                        if (child == 0 || Mem.LoadI32(child) >= -1) continue;
                        if (b == 2)
                        {
                            Mem.StoreI32(slot, 0);
                            cleared |= 3 << (2 * k);
                            if (!NoDyingUp() && (PL(child, 24) & Dying) != 0 && DyingPath(m, child)) continue;
                            PS(m, 28, PL(m, 28) & ~cleared);
                            s_visitDead = dead;
                            UpPath(m, child, -1);
                            dead = s_visitDead;
                            continue;
                        }
                        if (PL(child, 4) == 0 || ArenaOf(child) != ar) continue;
                        Mem.StoreI32(slot, 0);
                        cleared |= 3 << (2 * k);
                        int left = PL(child, 4) - 1;
                        PS(child, 4, left);
                        if (left == 0 && Mem.LoadI32(child) == -2)
                        {
                            // (Kill, in a destruction)
                            int cf = PL(child, 24);
                            if ((cf & (Dying | Queued)) == 0) { PS(child, 24, cf | Dying); ArenaTakeOut(ar, child); PS(child, 12, dead); dead = child; }
                            continue;
                        }
                        PS(m, 28, PL(m, 28) & ~cleared);
                        dead = DyingSurvivor(m, child, dead);
                    }
                    if (cleared != 0) PS(m, 28, PL(m, 28) & ~cleared);
                }
                else if (ArrayRankAt(m) == 1 && ArrayElementSizeAt(m) == 4)
                {
                    // (an array of references: each element as the visit by its type takes it, ArenaVisitElement)
                    int slot = m + 16, end = slot + 4 * Mem.LoadI32(m + 12);
                    for (; slot != end; slot += 4)
                    {
                        int child = Mem.LoadI32(slot);
                        if (child == 0 || Mem.LoadI32(child) >= -1) continue;
                        if (PL(child, 4) == 0 || ArenaOf(child) != ar) continue;
                        Mem.StoreI32(slot, 0);
                        int left = PL(child, 4) - 1;
                        PS(child, 4, left);
                        if (left == 0 && Mem.LoadI32(child) == -2)
                        {
                            int cf = PL(child, 24);
                            if ((cf & (Dying | Queued)) == 0) { PS(child, 24, cf | Dying); ArenaTakeOut(ar, child); PS(child, 12, dead); dead = child; }
                            continue;
                        }
                        dead = DyingSurvivor(m, child, dead);
                    }
                }
                else { s_visitDead = dead; s_visitObj = m; ArenaVisitObject(m); dead = s_visitDead; }
                PS(m, 8, done);
                done = m;
            }
            s_visitArena = sa; s_visitMode = sm; s_visitDead = sd; s_visitObj = so; s_visitElem = se;
            s_destroying--;
            s_cutOn = cutOn;
            long t1 = DropStats() ? Engine.MonotonicNs() : 0;
            long t2 = t1;
            WeakDyingAll(done, 8);
            for (int m = done; m != 0; m = PL(m, 8)) { ArenaClearObject(m); if (DropStats()) s_dsKilled++; }
            long t3 = DropStats() ? Engine.MonotonicNs() : 0;
            // (threads: each block back the way of its kind, FreeLocal)
            if (ThreadMode() || ShareCheck() || Checked() || NoFreeList())
            {
                for (int m = done; m != 0;) { int next = PL(m, 8); ArenaFreeObject(m); m = next; }
            }
            else
            {
                // (ArenaFreeList, down the list of the dead)
                int known = 0;
                for (int m = done; m != 0;)
                {
                    int next = PL(m, 8), blk = m - ArenaPrefix;
                    int pg = blk & ~(PageSize - 1);
                    if (pg != known && !IsPage(blk)) { ArenaFreeObject(m); m = next; continue; }
                    known = pg;
                    if (Mem.LoadI32(blk) != PrefixMark) SideFree(m);
                    int used = Mem.LoadI32(pg + 16) - 1;
                    if (used != 0 && (Mem.LoadI32(pg + 20) & 1) != 0)
                    {
                        Mem.StoreI32(blk, 0);
                        Mem.StoreI32(blk + 4, Mem.LoadI32(pg));
                        Mem.StoreI32(pg, blk);
                        Mem.StoreI32(pg + 16, used);
                    }
                    else PageFree(blk);
                    m = next;
                }
            }
            if (DropStats()) { s_dsKill += t1 - t0; s_dsZero += t2 - t1; s_dsClear += t3 - t2; s_dsFree += Engine.MonotonicNs() - t3; }
            ArenaEmpty(ar);
        }

        /// <summary>CS2WASM_NO_DESTROY_BY_BITS=1 (at compile time): ArenaDestroyPlain visits each dying member by its
        /// type (the generated visit function, Rt.ArenaVisit for each slot).</summary>
        [Intrinsic] static extern bool NoDestroyByBits();

        /// <summary>ArenaDestroyPlain: a member a dying one (m) referred to that lives on, VisitDying's end for it.
        /// `dead`: the list of the dying so far; returns it, with what died of this.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int DyingSurvivor(int m, int child, int dead)
        {
            s_visitDead = dead;
            OwnerLetGo(child, m);
            if (PL(child, 20) == 0) Adopt(child);
            UpdateHold(child);
            if ((ThreadMode() ? t_failedLocal : s_failedTops) != 0 && OutsideMarked(PLM(child))) DyingNote(child, m);
            if (StickyHolds() && Mem.LoadI32(child) == -2 && (PL(child, 24) & Owned) == 0) QueueCheck(child);
            return s_visitDead;
        }

        /// <summary>Whether a group without finalizers is let go in two passes (Compiler/RuntimeGen.cs, FusedDrop).</summary>
        [Intrinsic] static extern bool FusedDrop();
        // (the innermost pass of ArenaFree that clears its members as it walks their ring, 0 for none)
        static int s_dropPass;

        /// <summary>A finalizer is about to run (the compiler calls this before each: RuntimeGen.cs, EmitDestroy), the
        /// first code of the program since a group began to be let go in two passes, ArenaFree: the members that
        /// pass has not come to yet are as the walk before the clears left them, each count zero (a dying object:
        /// nothing counts it, a weak reference to it reads as gone), off the list of members to destroy, its note
        /// gone, before the finalizer can look. Of every such pass under way: one group's clears can let another
        /// go.</summary>
        public static void ArenaBeforeFinalizer()
        {
            for (int p = s_dropPass; p != 0; p = Mem.LoadI32(p))
            {
                int m = Mem.LoadI32(p + 4), first = Mem.LoadI32(p + 8);
                if (m == 0) continue;
                do { ArenaOf(m); Unqueue(m); ClearMark(m); Mem.StoreI32(m, 0); m = PL(m, 12); } while (m != first);
                Mem.StoreI32(p + 4, 0);
            }
        }

        /// <summary>CS2WASM_NO_FREE_LIST=1 (at compile time): the members of a group that is let go are freed one by one
        /// through their types' free functions (ArenaFreeList).</summary>
        [Intrinsic] static extern bool NoFreeList();

        /// <summary>Frees the members in the list (k of them; each cleared, its count zero): what ArenaFreeObject does
        /// for each, FreeObjectArena by its type's size, without the dispatch on the type for a block that lies in a
        /// page of small blocks: the page knows its blocks' size, and the block goes straight back onto its page's
        /// list (FreeSmallOld's case, PageFree for a page that was full or is empty now), not by way of the block
        /// kept aside for the next allocation of the size, which every free here would put on its page one free
        /// later. Any other block (a large array) as before. (The scene tree of tools/frames.mjs: 75,000 members a
        /// drop, 7 ns each through the dispatch.)</summary>
        static void ArenaFreeList(int list, int k)
        {
            if (!SingleThreaded() || ShareCheck() || Checked() || NoFreeList())
            {
                for (int i = 0; i < k; i++) ArenaFreeObject(Mem.LoadI32(list + 4 * i));
                return;
            }
            // (the page of the member before: one member after the other lies in the same page as often as not, and
            // a page stays one while it has a block in use; nothing is allocated in between)
            int known = 0;
            for (int i = 0; i < k; i++)
            {
                int m = Mem.LoadI32(list + 4 * i), blk = m - ArenaPrefix;
                int pg = blk & ~(PageSize - 1);
                if (pg != known)
                {
                    if (!IsPage(blk)) { ArenaFreeObject(m); continue; }
                    known = pg;
                }
                if (Mem.LoadI32(blk) != PrefixMark) SideFree(m);
                int used = Mem.LoadI32(pg + 16) - 1;
                if (used != 0 && (Mem.LoadI32(pg + 20) & 1) != 0)
                {
                    Mem.StoreI32(blk, 0);
                    Mem.StoreI32(blk + 4, Mem.LoadI32(pg));
                    Mem.StoreI32(pg, blk);
                    Mem.StoreI32(pg + 16, used);
                }
                else PageFree(blk);
            }
        }

        /// <summary>No outside reference remains: the whole arena goes (its members' inside references are taken out,
        /// then each member's finalizer runs and its other references are released, then the memory is freed).</summary>
        static void ArenaFree(int ar)
        {
            int flags = Mem.LoadI32(ar + 12);
            if ((flags & ArenaBusy) != 0) return;
            Mem.StoreI32(ar + 12, flags | ArenaBusy);
            int first = Mem.LoadI32(ar + 4);
            bool fin0 = Mem.LoadI32(ar + 24) != 0;
            if (ThreadMode() ? first != 0 && !fin0 && SccLocal(Mem.LoadI32(first + 4)) : first != 0 && !fin0 && SingleThreaded())
            {
                // No member has a finalizer: three passes, none of which reads another member. Each member's count
                // word is zero first (being destroyed), so releasing it is nothing (rt.release does nothing at zero):
                // a member's references to the others, which the visit below takes out of their slots one by one,
                // then need no visit and are left to the clear, which releases the rest of what each holds as it would.
                // (The visit read every member a member refers to, its count and its record: a cache miss each, in a
                // group freed whole, the scene tree of tools/frames.mjs, 31,000 entities in 7 to 10 ms.)
                // The ring is walked once, into a list: the walk is a chain of loads each waiting for the one before (a
                // member's next is in the member), and the ring of a group that has lived a while is in no order in
                // memory, so each step is a miss. The clear and the free then go down the list, where the loads of one
                // member do not wait for another's.
                int n = Mem.LoadI32(ar + 8), cap = n > 0 ? n : 16;
                int list = Alloc(cap * 4), k = 0;
                int m = first;
                WeakDyingAll(first, 12);
                long t0 = DropStats() ? Engine.MonotonicNs() : 0;
                long t1;
                if (FusedDrop())
                {
                    // (the pass's note for ArenaBeforeFinalizer: [0] the pass around this one, [4] the first member whose
                    // count is not zero yet, 0 for none, [8] the ring's first)
                    int pass = Alloc(12);
                    Mem.StoreI32(pass, s_dropPass); Mem.StoreI32(pass + 4, first); Mem.StoreI32(pass + 8, first);
                    s_dropPass = pass;
                    do
                    {
                        int next = PL(m, 12);
                        if (Mem.LoadI32(pass + 4) != 0)
                        {
                            ArenaOf(m); Unqueue(m); ClearMark(m); Mem.StoreI32(m, 0);
                            Mem.StoreI32(pass + 4, next == first ? 0 : next);
                        }
                        if (k == cap) { int nl = Alloc(cap * 8); Mem.Copy(nl, list, cap * 4); Free(list, cap * 4); list = nl; cap *= 2; }
                        Mem.StoreI32(list + 4 * k, m); k++;
                        ArenaClearObject(m);
                        m = next;
                    } while (m != first);
                    s_dropPass = Mem.LoadI32(pass);
                    Free(pass, 12);
                    t1 = DropStats() ? Engine.MonotonicNs() : 0;
                }
                else
                {
                    do
                    {
                        ArenaOf(m); Unqueue(m); ClearMark(m); Mem.StoreI32(m, 0);
                        if (k == cap) { int nl = Alloc(cap * 8); Mem.Copy(nl, list, cap * 4); Free(list, cap * 4); list = nl; cap *= 2; }
                        Mem.StoreI32(list + 4 * k, m); k++;
                        m = PL(m, 12);
                    } while (m != first);
                    t1 = DropStats() ? Engine.MonotonicNs() : 0;
                    for (int i = 0; i < k; i++) ArenaClearObject(Mem.LoadI32(list + 4 * i));
                }
                long t2 = DropStats() ? Engine.MonotonicNs() : 0;
                ArenaFreeList(list, k);
                Free(list, cap * 4);
                // (CS2WASM_DROP_STATS=1: a large group's drop by its passes, in microseconds)
                if (DropStats() && k >= 10000) DsDrop(k, (t1 - t0) / 1000, (t2 - t1) / 1000, (Engine.MonotonicNs() - t2) / 1000);
            }
            else if (first != 0)
            {
                // (every member's record word straight at the arena: the merged records they pointed to go now)
                int m = first;
                do { ArenaOf(m); Unqueue(m); ClearMark(m); m = PL(m, 12); } while (m != first);
                int sa = s_visitArena, sm = s_visitMode;
                bool fin = fin0;
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
                // (with CS2WASM_NO_FIN_REVIVE=1 the record stays, busy, for the words of its members that name it)
                if (fin && FinPhase(first, 12, cutBase)) { if (NoFinRevive()) return; { MemStatsRecord(-1); ReportForget(ar); Free(ar, ArenaRecord); } return; }
                if (!fin) WeakDyingAll(first, 12);
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
            { MemStatsRecord(-1); ReportForget(ar); Free(ar, ArenaRecord); }
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
            // (into a plain array whose one reference is the copying code's, a list's new array as it grows: nothing
            // refers to it, so no element closes a cycle, and each is a plain counted store, as ArenaLinkOwned's. The
            // array's elements are linked when it is stored where a cycle can close: ArenaJoin links what a joining
            // object holds)
            if (src != dst && (!ThreadMode() || SccLocal(Mem.LoadI32(dst + 4))) && Mem.LoadI32(dst) == 1)
            {
                for (int k = 0; k < length; k++)
                {
                    int v = Mem.LoadI32(s + k * 4);
                    Retain(Borrow<object>(v));
                    int old = Mem.LoadI32(d + k * 4);
                    Mem.StoreI32(d + k * 4, v);
                    if (old != 0) Release(Borrow<object>(old));
                }
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
            if (FramesWanted())
            {
                // (ExceptionDispatchInfo's throw: the site of the first throw stays)
                int site = TakeThrowSite();
                e._throwSite = e._dispatching ? e._capturedSite : site;
                e._dispatching = false;
            }
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
        /// <summary>Add32 and CompareExchange32 on an object's count word: atomic in a program with threads; in one
        /// without, a load and a store, as its counts are everywhere else (rt.retain, rt.release). The engine runs an
        /// atomic instruction with the bus locked whatever the memory: 20 cycles each, and a tree's leaf joining its
        /// arena did eight (tools/frames.mjs, tree). CS2WASM_ATOMIC_COUNTS=1 (at compile time): atomic always.</summary>
        [Intrinsic] public static extern int CountAdd32(ref int location, int value);
        [Intrinsic] public static extern int CountCompareExchange32(ref int location, int expected, int replacement);
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
