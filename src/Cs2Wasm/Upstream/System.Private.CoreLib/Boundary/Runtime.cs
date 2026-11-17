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

    public static class Rt
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
        // Allocator: size classes of 8 bytes up to 2 KiB with free lists, first-fit list above.
        // All state is in linear memory (section 4.6); freed space is reused to keep the heap compact.
        // ------------------------------------------------------------------
        const int MaxSmall = 2048;
        const int Classes = MaxSmall / 8 + 1;
        static int s_top, s_end, s_table, s_hash;

        static void InitHeap()
        {
            int b = (HeapBase() + 15) & ~15;
            s_table = b;
            s_top = b + Classes * 4 + LargeBins * 4;
            s_top = (s_top + 15) & ~15;
            s_end = Mem.Size() * 65536;
        }

        [Intrinsic] public static extern int ThreadId();
        /// <summary>CS2WASM_SHARE_CHECK=1 (diagnosis of section 4.5's sharing): true, and an object's hash word carries
        /// the thread that allocated it in bits 24-30; a plain count update from another thread is reported.</summary>
        [Intrinsic] public static extern bool ShareCheck();

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
            string text = "share violation: " + TypeInfo.Type(Mem.LoadI32(a + 4)).ToString() + " allocated on thread " + ((Mem.LoadI32(a + 8) >> 24) & 0x3F).ToString()
                + ", count " + Mem.LoadI32(a).ToString() + ", on thread " + ThreadId().ToString() + "\n";
            fixed (char* p = text) Engine.Write((int)p, text.Length);
            // (a trap: the engine's stack trace shows where)
            Mem.StoreI32(-4, 0);
        }

        /// <summary>No other thread can ever run: the program starts none (known when the whole program is compiled).</summary>
        [Intrinsic] public static extern bool SingleThreaded();
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

        /// <summary>Bytes from the heap's start to its top (freed blocks included).</summary>
        public static long HeapUsed() => s_top == 0 ? 0 : (long)(uint)s_top - HeapBase();

        public static int Alloc(int size)
        {
            // threads: small blocks come from the thread's own free lists, without the runtime lock
            if (!SingleThreaded() && size <= MaxSmall) return AllocLocal(size);
            RuntimeLock();
            int a = AllocCore(size);
            RuntimeUnlock();
            return a;
        }

        // ------------------------------------------------------------------
        // Per-thread caches of small blocks (programs with threads): a thread allocates from and frees into its own
        // free lists, one per size class, and takes from or gives back to the shared ones in batches under the runtime
        // lock: when a list is empty, when it holds more than CacheLimit bytes, and when the thread ends.
        // ------------------------------------------------------------------
        const int CacheLimit = 256 * 1024, RefillBytes = 16 * 1024;
        [System.ThreadStatic] static int t_heads, t_cached;

        static int AllocLocal(int size)
        {
            size = (size + 7) & ~7;
            if (size < 16) size = 16;
            int heads = t_heads;
            if (heads == 0) heads = InitThreadCache();
            int slot = heads + (size >> 3) * 4;
            int b = Mem.LoadI32(slot);
            if (b == 0) { Refill(slot, size); b = Mem.LoadI32(slot); }
            Mem.StoreI32(slot, Mem.LoadI32(b + 8));
            t_cached -= size;
            Zero(b, size);
            return b;
        }

        static int InitThreadCache()
        {
            RuntimeLock();
            int h = AllocCore(Classes * 4);
            RuntimeUnlock();
            Mem.Fill(h, 0, Classes * 4);
            t_heads = h;
            return h;
        }

        /// <summary>Fills an empty list of the thread's: blocks of the shared list of the size, else new ones carved from
        /// the heap's top, RefillBytes' worth.</summary>
        static void Refill(int slot, int size)
        {
            int want = Math.Max(1, RefillBytes / size), got = 0, list = 0;
            RuntimeLock();
            if (s_top == 0) InitHeap();
            int shared = s_table + (size >> 3) * 4;
            while (got < want)
            {
                int b = Mem.LoadI32(shared);
                if (b == 0) break;
                Mem.StoreI32(shared, Mem.LoadI32(b + 8));
                Mem.StoreI32(b + 8, list); list = b; got++;
            }
            if (got == 0)
            {
                int bytes = want * size;
                int start = 0;
                if ((uint)(s_end - s_top) < (uint)bytes)
                {
                    start = TakeLarge(bytes);
                    if (start == 0)
                    {
                        int need = s_top + bytes - s_end;
                        GrowFor(need);
                        s_end = Mem.Size() * 65536;
                    }
                }
                if (start == 0) { start = s_top; s_top += bytes; }
                for (int i = want - 1; i >= 0; i--) { int b = start + i * size; Mem.StoreI32(b + 8, list); list = b; }
                got = want;
            }
            RuntimeUnlock();
            Mem.StoreI32(slot, list);
            t_cached += got * size;
        }

        static void FreeLocal(int addr, int size)
        {
            size = (size + 7) & ~7;
            if (size < 16) size = 16;
            int heads = t_heads;
            if (heads == 0) heads = InitThreadCache();
            int slot = heads + (size >> 3) * 4;
            // the link is at 8: word 0 (an object's count) stays 0, dead, for a reference read just before the free
            Mem.StoreI32(addr, 0);
            Mem.StoreI32(addr + 8, Mem.LoadI32(slot));
            Mem.StoreI32(slot, addr);
            t_cached += size;
            if (t_cached > CacheLimit) FlushThreadCache();
        }

        /// <summary>Gives the thread's cached blocks back to the shared lists (too many cached, or the thread's end).</summary>
        internal static void FlushThreadCache()
        {
            int heads = t_heads;
            if (heads == 0) return;
            RuntimeLock();
            for (int c = 2; c < Classes; c++)
            {
                int slot = heads + c * 4, b = Mem.LoadI32(slot);
                if (b == 0) continue;
                int tail = b;
                while (Mem.LoadI32(tail + 8) != 0) tail = Mem.LoadI32(tail + 8);
                int shared = s_table + c * 4;
                Mem.StoreI32(tail + 8, Mem.LoadI32(shared));
                Mem.StoreI32(shared, b);
                Mem.StoreI32(slot, 0);
            }
            RuntimeUnlock();
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
            ThrowHelpers.ReserveOutOfMemory();
        }

        // Blocks above MaxSmall: their sizes are rounded up to one of eight steps per power of two (an eighth more at
        // most), and each such size has its own list of freed blocks, so allocating and freeing one is a push or a pop
        // whatever is free. [0] next.
        const int LargeBins = 20 * 8;

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
                    Zero(head, size);
                    return head;
                }
                // (the region cut from a freed large block, below)
                // (addresses compare as unsigned numbers: memory goes on past 2 GB)
                if ((uint)(s_carveEnd - s_carve) >= (uint)size) { int c = s_carve; s_carve = c + size; return c; }
            }
            else if (size < 0x40000000)
            {
                size = RoundLarge(size);
                int bins = s_table + Classes * 4;
                int bin = LargeBin(size);
                int head = Mem.LoadI32(bins + bin * 4);
                if (head != 0)
                {
                    Mem.StoreI32(bins + bin * 4, Mem.LoadI32(head));
                    Mem.Fill(head, 0, size);
                    return head;
                }
                // a freed block of a larger size nearby (up to four times): split, the rest freed again
                int last = bin + 16 < LargeBins ? bin + 16 : LargeBins - 1;
                for (int i = bin + 1; i <= last; i++)
                {
                    int blk = Mem.LoadI32(bins + i * 4);
                    if (blk == 0) continue;
                    Mem.StoreI32(bins + i * 4, Mem.LoadI32(blk));
                    int csize = LargeSize(i);
                    Mem.Fill(blk, 0, size);
                    FreeChunk(blk + size, csize - size);
                    return blk;
                }
            }
            if ((uint)(s_end - s_top) < (uint)size)
            {
                // Freed space first (section 4.6), before the memory grows. For a small block, a whole freed large
                // block becomes the region small blocks are cut from, one after the other (cutting one small block
                // off a large one at a time would look for a block at every allocation).
                if (size <= MaxSmall)
                {
                    int bins = s_table + Classes * 4;
                    for (int i = 0; i < LargeBins; i++)
                    {
                        int blk = Mem.LoadI32(bins + i * 4);
                        if (blk == 0) continue;
                        Mem.StoreI32(bins + i * 4, Mem.LoadI32(blk));
                        int csize = LargeSize(i);
                        if (s_carveEnd - s_carve >= 16) FreeCore(s_carve, (s_carveEnd - s_carve) & ~7);
                        Mem.Fill(blk, 0, csize);
                        s_carve = blk + size; s_carveEnd = blk + csize;
                        return blk;
                    }
                }
                else
                {
                    int blk = TakeLarge(size);
                    if (blk != 0) return blk;
                }
                int need = s_top + size - s_end;
                GrowFor(need);
                s_end = Mem.Size() * 65536;
            }
            int a = s_top;
            s_top += size;
            return a;
        }

        // (a freed large block that small blocks are being cut from: where the next one starts, and its end)
        static int s_carve, s_carveEnd;

        /// <summary>The size of the blocks on a large list.</summary>
        static int LargeSize(int bin) => (8 + (bin & 7)) << ((bin >> 3) + 8);

        /// <summary>A piece of free memory of any size (a multiple of 8): onto the lists, in blocks of their sizes.</summary>
        static void FreeChunk(int addr, int size)
        {
            int bins = s_table + Classes * 4;
            while (size > MaxSmall)
            {
                if (addr + size == s_top) { Mem.Fill(addr, 0, size); s_top = addr; return; }
                int bin = LargeBin(size);
                int csize = LargeSize(bin);
                Mem.StoreI32(addr, Mem.LoadI32(bins + bin * 4));
                Mem.StoreI32(bins + bin * 4, addr);
                addr += csize; size -= csize;
            }
            if (size >= 16) FreeCore(addr, size);
        }

        /// <summary>A freed large block that holds size bytes (a multiple of 8), zeroed; what is left of it is freed
        /// again. 0 if there is none.</summary>
        static int TakeLarge(int size)
        {
            int bins = s_table + Classes * 4;
            int first = size <= MaxSmall ? 0 : LargeBin(RoundLarge(size));
            for (int i = first; i < LargeBins; i++)
            {
                int blk = Mem.LoadI32(bins + i * 4);
                if (blk == 0) continue;
                int csize = LargeSize(i);
                if (csize < size) continue;
                Mem.StoreI32(bins + i * 4, Mem.LoadI32(blk));
                Mem.Fill(blk, 0, size);
                if (csize - size >= 16) FreeChunk(blk + size, csize - size);
                return blk;
            }
            return 0;
        }

        public static void Free(int addr, int size)
        {
            // CS2WASM_SHARE_CHECK: freed memory is poisoned (0x0EAD, the freeing thread) and never reused
            // (its type word too: a borrowed reference used after the free traps at its next dispatch or type test)
            if (ShareCheck() && size >= 8) { Mem.StoreI32(addr, 0x0EAD0000 | (ThreadId() & 0xFFFF)); Mem.StoreI32(addr + 4, 0x7EAD0000); return; }
            if (!SingleThreaded() && size <= MaxSmall) { FreeLocal(addr, size); return; }
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
            // (a large block has its size's step: as it was allocated)
            FreeChunk(addr, size < 0x40000000 ? RoundLarge(size) : size);
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

        /// <summary>Clears a block (a multiple of 8 bytes). A small one word by word: memory.fill is a call into the
        /// engine, slower than the few stores.</summary>
        static void Zero(int addr, int size)
        {
            if (size > 256) { Mem.Fill(addr, 0, size); return; }
            for (int e = addr + size; addr != e; addr += 8) Mem.StoreI64(addr, 0);
        }

        public static int AllocObject(int size, int typeId)
        {
            // The usual case in one piece, without a call: a freed block of the size is taken off its list, cleared
            // and given its header. (An object's identity hash is made when it is first asked for: HashOf.)
            if (SingleThreaded())
            {
                int s = (size + 7) & ~7;
                if (s <= MaxSmall && s_table != 0)
                {
                    int slot = s_table + (s >> 3) * 4;
                    int head = Mem.LoadI32(slot);
                    if (head != 0)
                    {
                        Mem.StoreI32(slot, Mem.LoadI32(head + 8));
                        Mem.StoreI32(head, 1);
                        Mem.StoreI32(head + 4, typeId);
                        for (int p = head + 8, e = head + s; p != e; p += 8) Mem.StoreI64(p, 0);
                        return head;
                    }
                }
            }
            int a = Alloc(size);
            Mem.StoreI32(a, 1);
            Mem.StoreI32(a + 4, typeId);
            if (ShareCheck()) Mem.StoreI32(a + 8, (ThreadId() & 0x3F) << 24);
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
            // (bits 31 and 30 of the word are flags: pinned by a static, marked: ObjectMarks; with the share check,
            // bits 24 to 29 are the thread that made the object)
            int mask = ShareCheck() ? 0xFFFFFF : 0x3FFFFFFF;
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
            return v & 0x3FFFFFFF;
        }

        // ------------------------------------------------------------------
        // Arenas (section 4.3). Compiled into a module only when the compiler found a store that can close a cycle.
        //
        // An object of a cycle-capable type is allocated with 16 bytes before its header (AllocObjectArena):
        //   [-16] its arena's record (0: none yet), [-12] next member, [-8] previous member (a ring),
        //   [-4] inside count: the references to it from slots of members of its arena.
        // It starts as a plain counted object. A store that can close a cycle puts the container and the value in one
        // arena (ArenaLink); the stored reference is then an inside one: counted in the value's inside count, not
        // in its count. A member's count word is -(outside count + 2): below -1, so retain and release take the
        // arena's path, where the outside references (locals, statics, other objects and arenas) count on the
        // member and on the arena's total.
        // Arena record (24 bytes): [0] total outside count, [4] a member (the ring), [8] members, [12] flags,
        //   [16] the arena this one was merged into (0: none), [20] the members and merged records that point here.
        // Two arenas merge in a few steps whatever their sizes: the smaller's record points to the larger's, and its
        // ring is joined to the other's. A member's record word may so be a merged record: ArenaOf follows it to the
        // arena and makes the member point there; a merged record goes when nothing points to it any more.
        //
        // Nothing is scanned and nothing waits. A member no reference of either kind refers to is destroyed at once,
        // and the members only it referred to with it (a subtree taken out of a tree). An arena whose total reaches
        // zero is destroyed whole: what is left then is what its members' references to each other kept, the cycles.
        // When a store takes away an inside reference to a member that has others left and no outside one, the member
        // may head a part of the arena nothing else refers to any more (a subtree with links back to its parents,
        // taken out of its tree): ArenaSplit looks at that part only, and frees it if so. A part it does not find
        // (larger than its limit) stays until the arena goes, as section 4.3 allows.
        // A pinned object (stored in a static) has bit 31 of its hash word set and never joins an arena.
        // ------------------------------------------------------------------
        // ArenaShared: another thread can reach the arena's members (Sharing): it takes no more members, and the
        // objects its members reach are shared. Shared objects never join an arena (their counts are atomic, an
        // arena's are under the runtime lock); cycles among them are not collected.
        const int ArenaBusy = 1, ArenaShared = 4;
        const int SharedCount = 0x40000000;
        const int ArenaPrefix = 16, ArenaRecord = 24;

        [Intrinsic] static extern int SccOf(int typeId);
        [Intrinsic] static extern void ArenaClearObject(int addr);
        [Intrinsic] static extern void ArenaFreeObject(int addr);
        [Intrinsic] static extern void ArenaVisitObject(int addr);

        /// <summary>An object of a cycle-capable type: room for its arena's words before the header (the compiler
        /// sends AllocObject here for such a type).</summary>
        public static int AllocObjectArena(int size, int typeId)
        {
            int a = Alloc(size + ArenaPrefix) + ArenaPrefix;
            Mem.StoreI32(a, 1);
            Mem.StoreI32(a + 4, typeId);
            if (ShareCheck()) Mem.StoreI32(a + 8, (ThreadId() & 0x3F) << 24);
            return a;
        }

        /// <summary>Frees such an object (the destroy functions of cycle-capable types).</summary>
        public static void FreeObjectArena(int addr, int size) => Free(addr - ArenaPrefix, size + ArenaPrefix);

        /// <summary>The arena of a member (its count word says it is one).</summary>
        internal static int ArenaOf(int obj)
        {
            int ar = Mem.LoadI32(obj - 16);
            int f = Mem.LoadI32(ar + 16);
            if (f == 0) return ar;
            // (merged since: to the arena it is part of now, and straight there from here on)
            int root = f;
            while (true) { int g = Mem.LoadI32(root + 16); if (g == 0) break; root = g; }
            Mem.StoreI32(obj - 16, root);
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
                Free(ar, ArenaRecord);
                ar = f;
            }
        }

        static int NewArena()
        {
            int ar = Alloc(ArenaRecord);
            Mem.StoreI64(ar, 0); Mem.StoreI64(ar + 8, 0); Mem.StoreI64(ar + 16, 0);
            return ar;
        }

        /// <summary>A plain object (count c > 0) becomes a member: its count is its outside count.</summary>
        static void ArenaJoin(int ar, int obj)
        {
            int c = Mem.LoadI32(obj);
            Mem.StoreI32(obj, -(c + 2));
            Mem.StoreI32(obj - 16, ar);
            Mem.StoreI32(obj - 4, 0);
            int head = Mem.LoadI32(ar + 4);
            if (head == 0)
            {
                Mem.StoreI32(obj - 12, obj);
                Mem.StoreI32(obj - 8, obj);
                Mem.StoreI32(ar + 4, obj);
            }
            else
            {
                int tail = Mem.LoadI32(head - 8);
                Mem.StoreI32(obj - 12, head);
                Mem.StoreI32(obj - 8, tail);
                Mem.StoreI32(tail - 12, obj);
                Mem.StoreI32(head - 8, obj);
            }
            Mem.StoreI32(ar + 8, Mem.LoadI32(ar + 8) + 1);
            Mem.StoreI32(ar, Mem.LoadI32(ar) + c);
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
        static int s_linkQ, s_linkN, s_linkCap;

        /// <summary>Links what the objects that just joined an arena hold in their slots, and what those hold, in turn
        /// (a list, not a nest: a long chain joins without a deep stack).</summary>
        static void ArenaLinkJoined()
        {
            while (s_linkN > 0)
            {
                s_linkN--;
                int o = Mem.LoadI32(s_linkQ + 4 * s_linkN);
                // (still a member: nothing ran in between)
                int sa = s_visitArena, sm = s_visitMode;
                s_visitArena = o; s_visitMode = 4;
                ArenaVisitObject(o);
                s_visitArena = sa; s_visitMode = sm;
            }
        }

        /// <summary>The smaller arena becomes part of the larger; returns the survivor.</summary>
        static int ArenaMerge(int a, int b)
        {
            if (Mem.LoadI32(a + 8) < Mem.LoadI32(b + 8)) { int t = a; a = b; b = t; }
            int ha = Mem.LoadI32(a + 4), hb = Mem.LoadI32(b + 4);
            if (hb != 0)
            {
                if (ha == 0) Mem.StoreI32(a + 4, hb);
                else
                {
                    // the two rings become one
                    int ta = Mem.LoadI32(ha - 8), tb = Mem.LoadI32(hb - 8);
                    Mem.StoreI32(ta - 12, hb); Mem.StoreI32(hb - 8, ta);
                    Mem.StoreI32(tb - 12, ha); Mem.StoreI32(ha - 8, tb);
                }
            }
            Mem.StoreI32(a, Mem.LoadI32(a) + Mem.LoadI32(b));
            Mem.StoreI32(a + 8, Mem.LoadI32(a + 8) + Mem.LoadI32(b + 8));
            Mem.StoreI32(a + 12, Mem.LoadI32(a + 12) | Mem.LoadI32(b + 12));
            // (b's members still point to b: b points to a, for as long as one of them does)
            Mem.StoreI32(b, 0); Mem.StoreI32(b + 4, 0); Mem.StoreI32(b + 8, 0);
            if (Mem.LoadI32(b + 20) == 0) Free(b, ArenaRecord);
            else { Mem.StoreI32(b + 16, a); Mem.StoreI32(a + 20, Mem.LoadI32(a + 20) + 1); }
            return a;
        }

        /// <summary>Retain of an arena member: one more outside reference.</summary>
        public static void ArenaRetain(int p)
        {
            Mem.StoreI32(p, Mem.LoadI32(p) - 1);
            int ar = ArenaOf(p);
            Mem.StoreI32(ar, Mem.LoadI32(ar) + 1);
        }

        /// <summary>Release of an arena member: one outside reference fewer.</summary>
        public static void ArenaRelease(int p)
        {
            int w = Mem.LoadI32(p) + 1;
            Mem.StoreI32(p, w);
            int ar = ArenaOf(p);
            int total = Mem.LoadI32(ar) - 1;
            Mem.StoreI32(ar, total);
            if (total == 0) { ArenaFree(ar); return; }
            if (w != -2 || (Mem.LoadI32(ar + 12) & ArenaBusy) != 0) return;
            // (no reference of either kind: it goes now, with what only it referred to)
            if (Mem.LoadI32(p - 4) == 0) { ArenaDestroyFrom(p); ArenaPending(); return; }
        }

        /// <summary>
        /// A store of value (owned) into a slot of container that can close a cycle: if both are of one cycle-capable
        /// group, they end up in one arena and the stored reference becomes an inside one. Returns the value to store.
        /// </summary>
        public static int ArenaLink(int val, int container)
        {
            if (val == 0 || container == 0) return val;
            RuntimeLock();
            int r = ArenaLinkCore(val, container);
            ArenaLinkJoined();
            RuntimeUnlock();
            return r;
        }

        /// <summary>ArenaLink where the compiler saw that only the storing code knows the container (a new object, not
        /// let out yet: FreshStores.cs): nothing can reach it, so the store closes no cycle and is a plain counted one.
        /// Unless the container is an arena's member already (it was linked while it was being made): then as any store.</summary>
        public static int ArenaLinkFresh(int val, int container)
        {
            if (container == 0 || Mem.LoadI32(container) > 0) return val;
            return ArenaLink(val, container);
        }

        /// <summary>The old value of a slot stored by ArenaLinkFresh: a plain object's slots hold counted references.</summary>
        public static void ArenaUnlinkFresh(int old, int container)
        {
            if (container == 0 || Mem.LoadI32(container) > 0) { Release(Borrow<object>(old)); return; }
            ArenaUnlink(old, container);
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
            int ra = ca < -1 ? ArenaOf(container) : 0, rv = cv < -1 ? ArenaOf(val) : 0;
            if (ra != 0 && (Mem.LoadI32(ra + 12) & ArenaBusy) != 0) return val;
            if (rv != 0 && (Mem.LoadI32(rv + 12) & ArenaBusy) != 0) return val;
            // (a shared arena takes no more members; two of its members still link)
            if (ra == 0 || ra != rv)
            {
                if (ra != 0 && (Mem.LoadI32(ra + 12) & ArenaShared) != 0) return val;
                if (rv != 0 && (Mem.LoadI32(rv + 12) & ArenaShared) != 0) return val;
            }
            int ar = ra;
            if (ar == 0) { ar = rv != 0 ? rv : NewArena(); ArenaJoin(ar, container); }
            cv = Mem.LoadI32(val); // val may be the container itself
            if (cv > 0) ArenaJoin(ar, val);
            else
            {
                int now = ArenaOf(val);
                if (now != ar) ar = ArenaMerge(ar, now);
            }
            // the stored reference is inside the arena now: the count the store took becomes an inside count
            Mem.StoreI32(val, Mem.LoadI32(val) + 1);
            Mem.StoreI32(ar, Mem.LoadI32(ar) - 1);
            Mem.StoreI32(val - 4, Mem.LoadI32(val - 4) + 1);
            return val;
        }

        /// <summary>The old value of a slot that can close a cycle was overwritten.</summary>
        public static void ArenaUnlink(int old, int container)
        {
            if (old == 0) return;
            RuntimeLock();
            int co = Mem.LoadI32(old), cc = Mem.LoadI32(container);
            // an inside reference: both are members of one arena (they were linked when it was stored), the arena is
            // not being destroyed (its members' inside references are cut first), and it was counted as one
            // (ArenaVisit says when a slot's reference to a member is not)
            bool inside = co < -1 && cc < -1 && Mem.LoadI32(old - 4) != 0;
            if (inside)
            {
                int ar = ArenaOf(old);
                inside = ar == ArenaOf(container) && (Mem.LoadI32(ar + 12) & ArenaBusy) == 0;
            }
            if (inside)
            {
                int n = Mem.LoadI32(old - 4) - 1;
                Mem.StoreI32(old - 4, n);
                if (n == 0) { if (co == -2) { ArenaDestroyFrom(old); ArenaPending(); } }
            }
            RuntimeUnlock();
            if (!inside) Release(Borrow<object>(old));
        }

        /// <summary>A store into a static field: the object stays out of arenas (statics never end).</summary>
        public static int ArenaPin(int val)
        {
            if (val != 0 && Mem.LoadI32(val) > 0) Mem.StoreI32(val + 8, Mem.LoadI32(val + 8) | int.MinValue);
            return val;
        }

        // The generated visitor calls ArenaVisit for each slot of a member that can hold an inside reference; what that
        // does is the mode: 0 a dying member's references (taken out; a member that loses its last dies too),
        // 1 the same when all of them die anyway (taken out only), 2 a split's count (ArenaSplit), 3 its undoing,
        // 4 a copied object's slots (ArenaLinkContents).
        static int s_visitArena, s_visitMode, s_visitDead;
        const int SplitMax = 4096, PendingMax = 32;
        static int s_split, s_splitN, s_splitRoot, s_splitOver, s_pending, s_pendingN;

        public static void ArenaVisit(int slot)
        {
            int child = Mem.LoadI32(slot);
            // (4: each reference, counted by the copy, as a store of it)
            if (s_visitMode == 4) { if (child != 0) ArenaLinkCore(child, s_visitArena); return; }
            if (child == 0 || Mem.LoadI32(child) >= -1 || ArenaOf(child) != s_visitArena) return;
            switch (s_visitMode)
            {
                case 1: Mem.StoreI32(slot, 0); return;
                case 3: Mem.StoreI32(child - 4, Mem.LoadI32(child - 4) + 1); return;
                case 2:
                    {
                        int n = Mem.LoadI32(child - 4) - 1;
                        Mem.StoreI32(child - 4, n);
                        // (below zero: this slot's reference was counted as an outside one, see below; nothing is decided then)
                        if (n < 0) s_splitOver = 1;
                        if (n != 0 || Mem.LoadI32(child) != -2 || child == s_splitRoot) return;
                        if (s_splitN == SplitMax) { s_splitOver = 1; return; }
                        Mem.StoreI32(s_split + 4 * s_splitN, child);
                        s_splitN++;
                        return;
                    }
            }
            // A slot filled by something other than a store the compiler links holds a counted, outside reference even
            // to a member of the same arena. The member's inside count is then lower than its inside referrers; a
            // reference found with that count at zero is such an outside one, and stays in the slot for the release
            // every other reference of the dying object gets.
            if (Mem.LoadI32(child - 4) == 0) return;
            Mem.StoreI32(slot, 0);
            int left = Mem.LoadI32(child - 4) - 1;
            Mem.StoreI32(child - 4, left);
            if (Mem.LoadI32(child) != -2) return;
            if (left == 0)
            {
                ArenaTakeOut(s_visitArena, child);
                Mem.StoreI32(child - 12, s_visitDead);
                s_visitDead = child;
            }
            else if (s_pendingN < PendingMax)
            {
                // it lost a reference with its referrer and has others: it may head a part cut off now. Looked at
                // when the referrer is done (ArenaPending); held by an outside count until then.
                if (s_pending == 0) s_pending = Alloc(PendingMax * 4);
                Mem.StoreI32(s_pending + 4 * s_pendingN, child);
                s_pendingN++;
                Mem.StoreI32(child, -3);
                Mem.StoreI32(child + 8, Mem.LoadI32(child + 8) | int.MinValue);
                Mem.StoreI32(s_visitArena, Mem.LoadI32(s_visitArena) + 1);
            }
        }

        /// <summary>A copy of an object (MemberwiseClone) has counted each reference it holds: those in slots that can
        /// close a cycle are linked, as the stores they stand for would have been.</summary>
        public static void ArenaLinkContents(int obj)
        {
            RuntimeLock();
            int sa = s_visitArena, sm = s_visitMode;
            s_visitArena = obj; s_visitMode = 4;
            ArenaVisitObject(obj);
            s_visitArena = sa; s_visitMode = sm;
            ArenaLinkJoined();
            RuntimeUnlock();
        }

        /// <summary>Takes a member off its arena's ring (ar: its arena, which its record word points to: ArenaOf).</summary>
        static void ArenaTakeOut(int ar, int m)
        {
            int next = Mem.LoadI32(m - 12);
            if (next == m) Mem.StoreI32(ar + 4, 0);
            else
            {
                int prev = Mem.LoadI32(m - 8);
                Mem.StoreI32(prev - 12, next);
                Mem.StoreI32(next - 8, prev);
                if (Mem.LoadI32(ar + 4) == m) Mem.StoreI32(ar + 4, next);
            }
            Mem.StoreI32(ar + 8, Mem.LoadI32(ar + 8) - 1);
            Mem.StoreI32(ar + 20, Mem.LoadI32(ar + 20) - 1);
        }

        /// <summary>The arena's record goes with its last member.</summary>
        static void ArenaEmpty(int ar)
        {
            if (Mem.LoadI32(ar + 8) == 0 && Mem.LoadI32(ar) == 0 && (Mem.LoadI32(ar + 12) & ArenaBusy) == 0) Free(ar, ArenaRecord);
        }

        /// <summary>A member without references: it is destroyed, and so is each member that loses its last reference
        /// with it, one after the other (no recursion: a long chain is a long list).</summary>
        static void ArenaDestroyFrom(int p)
        {
            int ar = ArenaOf(p);
            ArenaTakeOut(ar, p);
            Mem.StoreI32(p - 12, 0);
            int dead = p;
            while (dead != 0)
            {
                int m = dead;
                dead = Mem.LoadI32(m - 12);
                // its inside references go first (they are not counted as the others: the visitor takes them out)
                int sa = s_visitArena, sm = s_visitMode, sd = s_visitDead;
                s_visitArena = ar; s_visitMode = 0; s_visitDead = dead;
                ArenaVisitObject(m);
                dead = s_visitDead;
                s_visitArena = sa; s_visitMode = sm; s_visitDead = sd;
                // then as any object: no longer a member (count zero: being destroyed), its finalizer, its other references
                Mem.StoreI32(m, 0);
                Mem.StoreI32(m - 16, 0);
                ArenaClearObject(m);
                ArenaFreeObject(m);
            }
            ArenaEmpty(ar);
        }

        /// <summary>The members that lost a reference with a dying referrer (ArenaVisit): each gives its hold back and
        /// is looked at as after a store.</summary>
        static void ArenaPending()
        {
            while (s_pendingN > 0)
            {
                s_pendingN--;
                ArenaRelease(Mem.LoadI32(s_pending + 4 * s_pendingN));
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
            if (first != 0)
            {
                // (every member's record word straight at the arena: the merged records they pointed to go now)
                int m = first;
                do { ArenaOf(m); m = Mem.LoadI32(m - 12); } while (m != first);
                int sa = s_visitArena, sm = s_visitMode;
                s_visitArena = ar; s_visitMode = 1;
                m = first;
                do { ArenaVisitObject(m); m = Mem.LoadI32(m - 12); } while (m != first);
                s_visitArena = sa; s_visitMode = sm;
                m = first;
                do { ArenaClearObject(m); m = Mem.LoadI32(m - 12); } while (m != first);
                m = first;
                do
                {
                    int next = Mem.LoadI32(m - 12);
                    ArenaFreeObject(m);
                    m = next;
                } while (m != first);
            }
            Free(ar, ArenaRecord);
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
                    ArenaLink(v, dst);
                }
                Mem.Copy(d, s, length * 4);
                for (int k = 0; k < n; k++) ArenaUnlink(Mem.LoadI32(olds + k * 4), dst);
                Free(olds, n * 4);
                return;
            }
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
            if (ex is OutOfMemoryException)
            {
                // The trace needs memory too. There usually is some (one large allocation failed), and then the
                // exception has its trace as any other; with no room for the trace, it has none.
                e._fullTrace = null; e._stackTraceString = null;
                e._throwDepth = Engine.StackDepth();
                if (HasRoom(TraceRoom)) CaptureTrace(e);
                return;
            }
            CaptureTrace(e);
            e._throwDepth = Engine.StackDepth();
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
            TakeBlocks(out int stack, out int tls);
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
                Rt.FlushThreadCache();
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
        public static void NotSupported()
        {
            // (the exception is made without passing a safe point: the code that failed keeps what it borrowed)
            Rt.HoldEpoch(); var e = new NotSupportedException(); Rt.ReleaseEpoch();
            throw e;
        }
    }
}
