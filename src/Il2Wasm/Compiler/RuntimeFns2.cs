using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Il2Wasm.Compiler;

/// <summary>Type-specialized helpers: generic equality, hashing and comparison; array element operations; enum names.</summary>
internal sealed partial class RuntimeFns
{
    WasmFunction? _lockEnter, _lockExit;
    /// <summary>The runtime lock (allocator, arenas, task continuations): reentrant, owned by a thread id. Empty
    /// without threads.</summary>
    public WasmFunction LockEnter => _lockEnter ??= M.AddFunction("rt.lock_enter", FT([], []));
    public WasmFunction LockExit => _lockExit ??= M.AddFunction("rt.lock_exit", FT([], []));

    /// <summary>Fills in the lock and, when threads exist, makes retain/release atomic.</summary>
    public void EmitThreadSupport(bool threads, Func<WasmFunction> retainSlow, Func<WasmFunction> releaseSlow, bool arenas)
    {
        if (threads) { _ = LockEnter; _ = LockExit; }
        var owner = D.StaticAddress("rt.lock.owner", 4, 4);
        var depth = D.StaticAddress("rt.lock.depth", 4, 4);
        var waiters = D.StaticAddress("rt.lock.waiters", 4, 4);
        if (_lockEnter != null && threads)
        {
            var e = new Emitter(_lockEnter);
            int cur = e.AddLocal(I), n = e.AddLocal(I);
            // reentrant: if we own it, depth++; else CAS 0 -> id: a short spin, then a wait for the owner's leaving
            // (a thread that may not wait, the engine's, spins on)
            e.I32Addr(owner); e.Emit(Op.I32AtomicLoad, 0, 2); e.GGet(D.ThreadId); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(depth); e.I32Addr(depth); e.Load(Op.I32Load); e.I32(1); e.Op(Op.I32Add); e.Store(Op.I32Store); e.Op(Op.Return); e.End();
            int tries = e.AddLocal(I);
            var brk = e.Block(); var top = e.Loop();
            e.I32Addr(owner); e.I32(0); e.GGet(D.ThreadId); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Op(Op.I32Eqz); e.BrIf(brk);
            if (DeadlockDumpOn)
            {
                e.Get(tries); e.I32(1); e.Op(Op.I32Add); e.Tee(tries); e.I32(20000); e.Op(Op.I32GtU);
                e.If(); e.I32Addr(owner); e.Emit(Op.I32AtomicLoad, 0, 2); e.Call(D.LibMethod("Il2Wasm.Runtime.Threads", "LockTimeout", 1).Fn); e.End();
            }
            e.I32(0); e.Set(n);
            { var spin = e.Loop();
              e.I32Addr(owner); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(cur); e.Op(Op.I32Eqz); e.BrIf(top);
              e.Get(n); e.I32(1); e.Op(Op.I32Add); e.Tee(n); e.I32(200); e.Op(Op.I32LtS); e.BrIf(spin);
              e.End(); }
            e.GGet(D.ThreadId); e.I32(1); e.Op(Op.I32Ne);
            e.If();
            e.I32Addr(waiters); e.I32(1); e.Emit(Op.I32AtomicRmwAdd, 0, 2); e.Op(Op.Drop);
            e.I32Addr(owner); e.Get(cur); e.I64(1000000); e.Emit(Op.MemoryAtomicWait32, 0, 2); e.Op(Op.Drop);
            e.I32Addr(waiters); e.I32(1); e.Emit(Op.I32AtomicRmwSub, 0, 2); e.Op(Op.Drop);
            e.End();
            e.Br(top); e.End(); e.End();
            e.I32Addr(depth); e.I32(1); e.Store(Op.I32Store);
        }
        if (_lockExit != null && threads)
        {
            var e = new Emitter(_lockExit);
            e.I32Addr(depth); e.I32Addr(depth); e.Load(Op.I32Load); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
            e.I32Addr(depth); e.Load(Op.I32Load); e.Op(Op.I32Eqz);
            e.If();
            e.I32Addr(owner); e.I32(0); e.Emit(Op.I32AtomicStore, 0, 2);
            e.I32Addr(waiters); e.Emit(Op.I32AtomicLoad, 0, 2);
            e.If(); e.I32Addr(owner); e.I32(1); e.Emit(Op.MemoryAtomicNotify, 0, 2); e.Op(Op.Drop); e.End();
            // (the group destructions this thread left for when it gives the lock back: Rt.RunDeferredDestroys)
            // (not before the thread has its block of thread statics: AllocFirst makes it under the lock, and the list
            // is one of them)
            if (arenas && D.ThreadMode) { e.GGet(D.TlsBase); e.If(); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "RunDeferredDestroys", 0).Fn); e.End(); }
            // (and the objects whose destruction began while it was held, rt.destroy: their finalizers run program code,
            // which must not run under the runtime lock; not inside a destruction, whose own loop takes them)
            if (D.ThreadMode)
            {
                int q = e.AddLocal(I);
                e.GGet(D.TlsBase); e.GGet(D.DestroyDepth); e.Op(Op.I32Eqz); e.Op(Op.I32And);
                e.If();
                var exit = e.Block(); var top = e.Loop();
                e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "NextPending", 0).Fn); e.Tee(q); e.Op(Op.I32Eqz); e.BrIf(exit);
                e.I32(1); e.GSet(D.DestroyDepth);
                e.Get(q); e.Call(D.Rt.Destroy);
                e.I32(0); e.GSet(D.DestroyDepth);
                e.Br(top);
                e.End(); e.End();
                e.End();
            }
            e.End();
        }
        if (s_plainCounts) { EmitLoadRetain(false, arenas); return; }
        EmitLoadRetain(threads, arenas);
        if (!threads) return;
        // (for objects no other thread can reach: TypedCounts.cs)
        EmitPlainCounts(retainSlow, releaseSlow, arenas);
        // retain(p), release(p) with threads: the count is exchanged for the new one atomically (whoever counts holds
        // a reference, so the count cannot reach zero meanwhile; an exchange, not an addition: the object may become
        // an arena's member under the counting thread, Rt.ArenaJoin, or a plain object again, Rt.TryLeave, and its
        // count word change its meaning); the release that takes the last one destroys the object, at once. Immortal
        // (-1) and dead (0) stay.
        // An arena's member (its count word below -1: -2 less its outside references): a count that leaves it with
        // outside references before and after is the same exchange, with no lock: the arena's record counts the
        // members that have outside references, not the references, so nothing else changes. Its first outside
        // reference and its last go under the runtime lock (Rt.ArenaRetain, Rt.ArenaRelease).
        {
            var f = Retain; f.Body.Clear(); f.Locals.Clear();
            var e = new Emitter(f);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            var done = e.Block(); var top = e.Loop();
            e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
            e.If();
            e.Get(0); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(done);
            e.Br(top);
            e.End();
            if (arenas)
            {
                e.Get(r); e.I32(-1); e.Op(Op.I32GeS); e.BrIf(done);
                e.Get(r); e.I32(-2); e.Op(Op.I32LtS);
                e.If();
                e.Get(0); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(done);
                e.Br(top);
                e.End();
                // (under the group's lock, Rt.ArenaRetainSlow: 0 when it was no member by then)
                e.Get(0); e.Call(retainSlow()); e.BrIf(done);
                e.Br(top);
            }
            e.End(); e.End();
            e.End();
            e.Get(0);
        }
        {
            var f = Release; f.Body.Clear(); f.Locals.Clear();
            var e = new Emitter(f);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            var done = e.Block(); var top = e.Loop();
            e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
            e.If();
            e.Get(0); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Ne); e.BrIf(top);
            e.Get(r); e.I32(1); e.Op(Op.I32Eq);
            e.If(); e.Get(0); e.Call(Destroy); e.End();
            e.Br(done);
            e.End();
            if (arenas)
            {
                e.Get(r); e.I32(-1); e.Op(Op.I32GeS); e.BrIf(done);
                e.Get(r); e.I32(-3); e.Op(Op.I32LtS);
                e.If();
                e.Get(0); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(done);
                e.Br(top);
                e.End();
                // (under the group's lock, Rt.ArenaReleaseSlow: 0 when it was no member by then)
                e.Get(0); e.Call(releaseSlow()); e.BrIf(done);
                e.Br(top);
            }
            e.End(); e.End();
            e.End();
        }
    }

    WasmFunction? _loadRetain, _refLock, _refUnlock, _bulkEnter, _bulkExit;
    /// <summary>Threads: every place's lock, with the runtime lock (taken first, as everywhere): while they are
    /// held no reference anywhere is stored over, so a range of them can be read and counted, or taken out, as it is.
    /// Nothing of the program runs inside. Empty without threads.</summary>
    public WasmFunction BulkEnter => _bulkEnter ??= M.AddFunction("rt.bulk_enter", FT([], []));
    public WasmFunction BulkExit => _bulkExit ??= M.AddFunction("rt.bulk_exit", FT([], []));
    WasmFunction? _stripeMask, _maskEnter, _maskExit;
    /// <summary>(address, bytes) -> the locks of the places in the range, a bit each (all of them from 64 places on).</summary>
    public WasmFunction StripeMask => _stripeMask ??= MakeStripeMask();
    /// <summary>(locks): the runtime lock, then those locks, lowest first (two threads never wait for each other);
    /// (locks): gives them back. For an array's range of references copied or cleared: every place's lock
    /// (rt.bulk_enter) stopped every thread that read or stored a reference anywhere.</summary>
    public WasmFunction MaskEnter => _maskEnter ??= MakeMaskLock(true, true);
    public WasmFunction MaskExit => _maskExit ??= MakeMaskLock(false, true);
    WasmFunction? _stripesEnter, _stripesExit;
    /// <summary>As rt.mask_enter and rt.mask_exit without the runtime lock: for what counts nothing while it holds
    /// the places' locks (a range cleared: what it held is released afterwards).</summary>
    public WasmFunction StripesEnter => _stripesEnter ??= MakeMaskLock(true, false);
    public WasmFunction StripesExit => _stripesExit ??= MakeMaskLock(false, false);

    WasmFunction MakeStripeMask()
    {
        var f = M.AddFunction("rt.stripe_mask", FT([I, I], [ValType.I64]));
        var e = new Emitter(f);
        int i = e.AddLocal(I), n = e.AddLocal(I); int m = e.AddLocal(ValType.I64);
        e.Get(1); e.I32(3); e.Op(Op.I32Add); e.I32(2); e.Op(Op.I32ShrU); e.Tee(n); e.I32(64); e.Op(Op.I32GeU);
        e.If(); e.I64(-1); e.Op(Op.Return); e.End();
        e.Get(0); e.I32(2); e.Op(Op.I32ShrU); e.Set(i);
        var done = e.Block(); var next = e.Loop();
        e.Get(n); e.Op(Op.I32Eqz); e.BrIf(done);
        e.Get(m); e.I64(1); e.Get(i); e.I32(63); e.Op(Op.I32And); e.Op(Op.I64ExtendI32U); e.Op(Op.I64Shl); e.Op(Op.I64Or); e.Set(m);
        e.Get(i); e.I32(1); e.Op(Op.I32Add); e.Set(i);
        e.Get(n); e.I32(1); e.Op(Op.I32Sub); e.Set(n);
        e.Br(next); e.End(); e.End();
        e.Get(m);
        return f;
    }

    WasmFunction MakeMaskLock(bool enter, bool runtimeLock)
    {
        var f = M.AddFunction((runtimeLock ? "rt.mask_" : "rt.stripes_") + (enter ? "enter" : "exit"), FT([ValType.I64], []));
        var e = new Emitter(f);
        if (!D.ThreadMode) return f;
        int p = e.AddLocal(I), k = e.AddLocal(I);
        var table = D.StaticAddress("rt.ref.locks", RefLockBytes, 64);
        if (enter && runtimeLock) e.Call(LockEnter);
        e.I32Addr(table); e.Set(p);
        var done = e.Block(); var next = e.Loop();
        e.Get(k); e.I32(64); e.Op(Op.I32GeU); e.BrIf(done);
        e.Get(0); e.Get(k); e.Op(Op.I64ExtendI32U); e.Op(Op.I64ShrU); e.I64(1); e.Op(Op.I64And); e.Op(Op.I64Eqz); e.Op(Op.I32Eqz);
        e.If();
        if (enter) { var got = e.Block(); var spin = e.Loop(); e.Get(p); e.I32(0); e.I32(1); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Op(Op.I32Eqz); e.BrIf(got); e.Br(spin); e.End(); e.End(); }
        else { e.Get(p); e.I32(0); e.Emit(Op.I32AtomicStore, 0, 2); }
        e.End();
        e.Get(p); e.I32(64); e.Op(Op.I32Add); e.Set(p);
        e.Get(k); e.I32(1); e.Op(Op.I32Add); e.Set(k);
        e.Br(next); e.End(); e.End();
        if (!enter && runtimeLock) e.Call(LockExit);
        return f;
    }

    /// <summary>(address) -> the reference there, counted: read and counted in one step with respect to rt.store_ref
    /// (Driver.ThreadMode). Without threads: the read, then the count.</summary>
    public WasmFunction LoadRetain => _loadRetain ??= M.AddFunction("rt.load_retain", FT([I], [I]));
    /// <summary>(address) -> lock: takes the lock of a place that holds a reference; (lock): gives it back. For the
    /// runtime's own exchanges of references (Interlocked).</summary>
    public WasmFunction RefLock => _refLock ??= M.AddFunction("rt.ref_lock", FT([I], [I]));
    public WasmFunction RefUnlock => _refUnlock ??= M.AddFunction("rt.ref_unlock", FT([I], []));

    /// <summary>The locks of the places that hold references: one of 64 words by the place's address, held for the
    /// few instructions of a read that counts or of a store. Never held across a call into the program. Each word is
    /// 64 bytes from the next: packed together they were four cache lines that every thread reading or storing a
    /// reference wrote to.</summary>
    const int RefLockBytes = 64 * 64;
    internal void EmitRefLock(Emitter e, int addrLocal, int lockLocal)
    {
        var table = D.StaticAddress("rt.ref.locks", RefLockBytes, 64);
        e.I32Addr(table); e.Get(addrLocal); e.I32(2); e.Op(Op.I32ShrU); e.I32(63); e.Op(Op.I32And); e.I32(6); e.Op(Op.I32Shl); e.Op(Op.I32Add); e.Set(lockLocal);
        var brk = e.Block(); var top = e.Loop();
        e.Get(lockLocal); e.I32(0); e.I32(1); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Op(Op.I32Eqz); e.BrIf(brk);
        e.Br(top); e.End(); e.End();
    }
    internal void EmitRefUnlock(Emitter e, int lockLocal) { e.Get(lockLocal); e.I32(0); e.Emit(Op.I32AtomicStore, 0, 2); }

    void EmitLoadRetain(bool threads, bool arenas)
    {
        if (_refLock != null)
        {
            var e = new Emitter(_refLock);
            if (threads) { int s = e.AddLocal(I); EmitRefLock(e, 0, s); e.Get(s); } else e.I32(0);
        }
        if (_refUnlock != null && threads) { var e = new Emitter(_refUnlock); EmitRefUnlock(e, 0); }
        if (_bulkEnter != null && threads)
        {
            var e = new Emitter(_bulkEnter);
            int p = e.AddLocal(I), end = e.AddLocal(I);
            var table = D.StaticAddress("rt.ref.locks", RefLockBytes, 64);
            e.Call(LockEnter);
            e.I32Addr(table); e.Tee(p); e.I32(RefLockBytes); e.Op(Op.I32Add); e.Set(end);
            var done = e.Block(); var next = e.Loop();
            e.Get(p); e.Get(end); e.Op(Op.I32GeU); e.BrIf(done);
            { var got = e.Block(); var spin = e.Loop(); e.Get(p); e.I32(0); e.I32(1); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Op(Op.I32Eqz); e.BrIf(got); e.Br(spin); e.End(); e.End(); }
            e.Get(p); e.I32(64); e.Op(Op.I32Add); e.Set(p);
            e.Br(next); e.End(); e.End();
        }
        if (_bulkExit != null && threads)
        {
            var e = new Emitter(_bulkExit);
            int p = e.AddLocal(I), end = e.AddLocal(I);
            var table = D.StaticAddress("rt.ref.locks", RefLockBytes, 64);
            e.I32Addr(table); e.Tee(p); e.I32(RefLockBytes); e.Op(Op.I32Add); e.Set(end);
            var done = e.Block(); var next = e.Loop();
            e.Get(p); e.Get(end); e.Op(Op.I32GeU); e.BrIf(done);
            e.Get(p); e.I32(0); e.Emit(Op.I32AtomicStore, 0, 2);
            e.Get(p); e.I32(64); e.Op(Op.I32Add); e.Set(p);
            e.Br(next); e.End(); e.End();
            e.Call(LockExit);
        }
        if (_loadRetain == null) return;
        {
            var e = new Emitter(_loadRetain);
            if (!threads) { e.Get(0); e.Load(Op.I32Load); e.Call(Retain); return; }
            if (!s_lockedLoads) { EmitLoadRetainLockFree(e, arenas); return; }
            int s = e.AddLocal(I), p = e.AddLocal(I), r = e.AddLocal(I);
            EmitRefLock(e, 0, s);
            e.Get(0); e.Load(Op.I32Load); e.Tee(p);
            e.If();
            // (an exchange: the object may change between a plain one and an arena's member meanwhile, see rt.retain;
            // a member that has outside references takes one more with no other lock)
            var counted = e.Block(); var again = e.Loop();
            e.Get(p); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
            e.If();
            e.Get(p); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(counted);
            e.Br(again);
            e.End();
            if (arenas)
            {
                e.Get(r); e.I32(-2); e.Op(Op.I32LtS);
                e.If();
                e.Get(p); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(counted);
                e.Br(again);
                e.End();
                // a member's first outside reference: under the runtime lock, which is taken before a place's lock,
                // never after: let go, take both in order, read again
                e.Get(r); e.I32(-2); e.Op(Op.I32Eq);
                e.If();
                EmitRefUnlock(e, s);
                e.Get(0); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "LoadRetainSlow", 1).Fn); e.Op(Op.Return);
                e.End();
            }
            e.End(); e.End();
            e.End();
            EmitRefUnlock(e, s);
            e.Get(p);
        }
    }

    /// <summary>IL2WASM_LOCKED_LOADS=1 (a measurement): a read that counts takes its place's lock, as a store does.</summary>
    static readonly bool s_lockedLoads = Environment.GetEnvironmentVariable("IL2WASM_LOCKED_LOADS") == "1";

    WasmFunction? _copyWords, _zeroWords;
    /// <summary>(to, from, bytes): a copy word by word, the right way round when the two overlap. For references
    /// another thread may read meanwhile (rt.load_retain reads without a lock): each is there whole, the old one or
    /// the new one, never bytes of both, which a copy of bytes does not promise.</summary>
    public WasmFunction CopyWords => _copyWords ??= MakeCopyWords();
    /// <summary>(at, bytes): zero, word by word (as rt.copy_words).</summary>
    public WasmFunction ZeroWords => _zeroWords ??= MakeZeroWords();

    WasmFunction MakeCopyWords()
    {
        var f = M.AddFunction("rt.copy_words", FT([I, I, I], []));
        var e = new Emitter(f);
        int i = e.AddLocal(I);
        // (what is left over a multiple of four: bytes, first or last as the direction asks; there is none for
        // elements that hold references)
        e.Get(0); e.Get(1); e.Op(Op.I32Sub); e.Get(2); e.Op(Op.I32GeU);
        e.If();
        { var done = e.Block(); var next = e.Loop();
          e.Get(i); e.Get(2); e.I32(-4); e.Op(Op.I32And); e.Op(Op.I32GeU); e.BrIf(done);
          e.Get(0); e.Get(i); e.Op(Op.I32Add); e.Get(1); e.Get(i); e.Op(Op.I32Add); e.Load(Op.I32Load); e.Store(Op.I32Store);
          e.Get(i); e.I32(4); e.Op(Op.I32Add); e.Set(i);
          e.Br(next); e.End(); e.End(); }
        e.Get(0); e.Get(i); e.Op(Op.I32Add); e.Get(1); e.Get(i); e.Op(Op.I32Add); e.Get(2); e.Get(i); e.Op(Op.I32Sub); e.Op(Op.MemoryCopy);
        e.Else();
        e.Get(2); e.I32(-4); e.Op(Op.I32And); e.Set(i);
        e.Get(0); e.Get(i); e.Op(Op.I32Add); e.Get(1); e.Get(i); e.Op(Op.I32Add); e.Get(2); e.Get(i); e.Op(Op.I32Sub); e.Op(Op.MemoryCopy);
        { var done = e.Block(); var next = e.Loop();
          e.Get(i); e.Op(Op.I32Eqz); e.BrIf(done);
          e.Get(i); e.I32(4); e.Op(Op.I32Sub); e.Set(i);
          e.Get(0); e.Get(i); e.Op(Op.I32Add); e.Get(1); e.Get(i); e.Op(Op.I32Add); e.Load(Op.I32Load); e.Store(Op.I32Store);
          e.Br(next); e.End(); e.End(); }
        e.End();
        return f;
    }

    WasmFunction MakeZeroWords()
    {
        var f = M.AddFunction("rt.zero_words", FT([I, I], []));
        var e = new Emitter(f);
        int i = e.AddLocal(I);
        { var done = e.Block(); var next = e.Loop();
          e.Get(i); e.Get(1); e.I32(-4); e.Op(Op.I32And); e.Op(Op.I32GeU); e.BrIf(done);
          e.Get(0); e.Get(i); e.Op(Op.I32Add); e.I32(0); e.Store(Op.I32Store);
          e.Get(i); e.I32(4); e.Op(Op.I32Add); e.Set(i);
          e.Br(next); e.End(); e.End(); }
        e.Get(0); e.Get(i); e.Op(Op.I32Add); e.I32(0); e.Get(1); e.Get(i); e.Op(Op.I32Sub); e.Op(Op.MemoryFill);
        return f;
    }

    /// <summary>A read that counts, with no lock: read the reference, count the object if its count says it is alive
    /// (an exchange, as rt.retain), then read the place again. The same reference there: the object counted is the
    /// one the place holds (it could not be freed and its memory given to another object since the count, which this
    /// thread holds; and had it been freed before, the store that let it go came before that count, so this second
    /// read sees it). Another: the count is given back and the read starts again. A store is the value's write, then
    /// the old value's release (rt.store_ref), in that order.</summary>
    /// <summary>Rt.FinBias: the count of a group's member while the finalizers of the group run.</summary>
    const int FinBias = 0x10000000;

    void EmitLoadRetainLockFree(Emitter e, bool arenas)
    {
        int p = e.AddLocal(I), r = e.AddLocal(I);
        var again = e.Loop(ValType.I32);
        e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(p); e.Op(Op.I32Eqz);
        e.If(); e.I32(0); e.Op(Op.Return); e.End();
        var counted = e.Block(); var count = e.Loop();
        e.Get(p); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
        e.If();
        if (arenas)
        {
            // (a group's member whose finalizer phase is under way, Rt.FinPrepare: its count is FinBias, and the check
            // after the finalizers, under the runtime lock, reads any count above it as a reference the program can
            // reach, and keeps the group for good with all it refers to. A count taken here on an out-of-date reference,
            // given back once the place says so, was such a count: test 139 leaked a node and the one it referred to,
            // about one run in 45 under load. So under the runtime lock, and only if the place still holds the object.)
            e.Get(r); e.I32(FinBias); e.Op(Op.I32And);
            e.If();
            e.Call(LockEnter);
            e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(p);
            e.If();
            e.Get(p); e.Emit(Op.I32AtomicLoad, 0, 2); e.I32(FinBias); e.Op(Op.I32And);
            e.If(); e.Get(p); e.Call(Retain); e.Op(Op.Drop); e.Call(LockExit); e.Get(p); e.Op(Op.Return); e.End();
            e.End();
            e.Call(LockExit);
            e.Br(again);
            e.End();
        }
        e.Get(p); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(counted);
        e.Br(count);
        e.End();
        // (dead: the place has another value by now)
        e.Get(r); e.Op(Op.I32Eqz); e.BrIf(again);
        // (never freed: no count, but the place must still say so: the memory may have been another object's)
        e.Get(r); e.I32(-1); e.Op(Op.I32Eq);
        e.If();
        e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Get(p); e.Op(Op.I32Eq); e.If(); e.Get(p); e.Op(Op.Return); e.End();
        e.Br(again);
        e.End();
        if (arenas)
        {
            e.Get(r); e.I32(-2); e.Op(Op.I32LtS);
            e.If();
            e.Get(p); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(counted);
            e.Br(count);
            e.End();
            // a member's first outside reference: under its group's lock, where its members are freed (Rt.LoadRetainSlow)
            e.Get(0); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "LoadRetainSlow", 1).Fn); e.Op(Op.Return);
        }
        else e.Br(again);
        e.End(); e.End();
        // counted: the place again
        e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Get(p); e.Op(Op.I32Eq);
        e.If(); e.Get(p); e.Op(Op.Return); e.End();
        e.Get(p); e.Call(Release);
        e.Br(again);
        e.End();
    }

    /// <summary>IL2WASM_PLAIN_COUNTS=1 (a measurement, unsafe with more than one thread running): counts and counted
    /// reads as in a program without threads, to see what the atomic ones cost.</summary>
    static readonly bool s_plainCounts = Environment.GetEnvironmentVariable("IL2WASM_PLAIN_COUNTS") == "1";
    /// <summary>IL2WASM_ALL_SHARED=1 (diagnosis): every count is updated atomically, as a shared object's.</summary>
    static readonly bool s_allShared = Environment.GetEnvironmentVariable("IL2WASM_ALL_SHARED") == "1";
    /// <summary>IL2WASM_SHARE_CHECK=1 (diagnosis): plain count updates check the object's allocating thread (Rt.ShareCheck).</summary>
    public static readonly bool ShareCheckOn = Environment.GetEnvironmentVariable("IL2WASM_SHARE_CHECK") == "1";
    /// <summary>IL2WASM_DEADLOCK_DUMP=1 (diagnosis): a long wait for the runtime lock throws, naming its holder (Threads.LockTimeout).</summary>
    public static readonly bool DeadlockDumpOn = Environment.GetEnvironmentVariable("IL2WASM_DEADLOCK_DUMP") == "1";

    WasmFunction? _ecSave, _ecRestore;
    /// <summary>Around an async method's first MoveNext: the caller's AsyncLocal context is put back afterwards.</summary>
    public WasmFunction EcSave => _ecSave ??= M.AddFunction("rt.ec_save", FT([], [I]));
    public WasmFunction? EcSaveIfMade => _ecSave;
    public WasmFunction EcRestore => _ecRestore ??= M.AddFunction("rt.ec_restore", FT([I], []));
    public void EmitEcSupport(MethodInst? save, MethodInst? restore)
    {
        if (_ecSave != null) { var e = new Emitter(_ecSave); if (save != null) e.Call(save.Fn); else e.I32(0); }
        if (_ecRestore != null && restore != null) { var e = new Emitter(_ecRestore); e.Get(0); e.Call(restore.Fn); e.Get(0); e.Call(Release); }
    }

    WasmFunction? _cctorEnter, _cctorExit, _cctorFail;
    public WasmFunction CctorFail => _cctorFail ??= M.AddFunction("rt.cctor_fail", FT([I], []));
    /// <summary>cctor_enter(word) -> run?: claims a type's initialization. One thread: mark done and run. With threads:
    /// done or our own (recursion) → no; free → claim; another thread's → wait until done.</summary>
    public WasmFunction CctorEnter => _cctorEnter ??= M.AddFunction("rt.cctor_enter", FT([I], [I]));
    public WasmFunction CctorExit => _cctorExit ??= M.AddFunction("rt.cctor_exit", FT([I], []));

    /// <summary>Thread ids rt.cctor_enter notes waits for (beyond them a wait is not followed: a deadlock among
    /// initializers of threads made later than the 4096th is not seen).</summary>
    const int CctorWaitThreads = 4096;

    public void EmitCctorSupport(bool threads)
    {
        if (_cctorEnter != null)
        {
            var e = new Emitter(_cctorEnter);
            if (!threads)
            {
                e.Get(0); e.Load(Op.I32Load); e.If(); e.I32(0); e.Op(Op.Return); e.End();
                e.Get(0); e.I32(-1); e.Store(Op.I32Store); e.I32(1);
            }
            else
            {
                // A thread that waits for another's initializer notes which (rt.cctor.waits, a word per thread id),
                // and before it waits follows the owners: the owner of the one it waits for, what that owner waits
                // for, and so on. Back at itself, waiting would never end (two types whose initializers use each
                // other, started by two threads at once): it goes on without waiting, the other type's statics as
                // they are so far, as .NET does (and as one thread does for its own initializers).
                int s = e.AddLocal(I), o = e.AddLocal(I), k = e.AddLocal(I), w = e.AddLocal(I), noted = e.AddLocal(I);
                var waits = D.StaticAddress("rt.cctor.waits", CctorWaitThreads * 4, 4);
                void Slot(int tid) { e.I32Addr(waits); e.Get(tid); e.I32(2); e.Op(Op.I32Shl); e.Op(Op.I32Add); }
                void Me() { e.I32Addr(waits); e.GGet(D.ThreadId); e.I32(2); e.Op(Op.I32Shl); e.Op(Op.I32Add); }
                void Unnote() { e.Get(noted); e.If(); Me(); e.I32(0); e.Emit(Op.I32AtomicStore, 0, 2); e.End(); }
                var top = e.Loop();
                e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(s); e.I32(0); e.Op(Op.I32LtS);
                e.Get(s); e.GGet(D.ThreadId); e.Op(Op.I32Eq); e.Op(Op.I32Or);
                e.If(); Unnote(); e.I32(0); e.Op(Op.Return); e.End();
                e.Get(s); e.Op(Op.I32Eqz);
                e.If();
                e.Get(0); e.I32(0); e.GGet(D.ThreadId); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Op(Op.I32Eqz);
                e.If(); Unnote(); e.I32(1); e.Op(Op.Return); e.End();
                e.Else();
                // another thread (s) runs it: note the wait, then follow the owners
                e.GGet(D.ThreadId); e.I32(CctorWaitThreads); e.Op(Op.I32LtU);
                e.If(); Me(); e.Get(0); e.Emit(Op.I32AtomicStore, 0, 2); e.I32(1); e.Set(noted); e.End();
                e.Get(s); e.Set(o); e.I32(0); e.Set(k);
                { var chainDone = e.Block(); var chain = e.Loop();
                  e.Get(o); e.GGet(D.ThreadId); e.Op(Op.I32Eq);
                  e.If(); Unnote(); e.I32(0); e.Op(Op.Return); e.End();
                  e.Get(o); e.I32(1); e.Op(Op.I32LtS); e.Get(o); e.I32(CctorWaitThreads); e.Op(Op.I32GeU); e.Op(Op.I32Or);
                  e.Get(k); e.I32(64); e.Op(Op.I32GeU); e.Op(Op.I32Or); e.BrIf(chainDone);
                  Slot(o); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(w); e.Op(Op.I32Eqz); e.BrIf(chainDone);
                  e.Get(w); e.Emit(Op.I32AtomicLoad, 0, 2); e.Set(o);
                  e.Get(k); e.I32(1); e.Op(Op.I32Add); e.Set(k);
                  e.Br(chain); e.End(); e.End(); }
                // workers sleep on the word, the engine thread spins
                e.GGet(D.ThreadId); e.I32(1); e.Op(Op.I32Ne);
                e.If(); e.Get(0); e.Get(s); e.I64(1000000); e.Emit(Op.MemoryAtomicWait32, 0, 2); e.Op(Op.Drop); e.End();
                e.End();
                e.Br(top);
                e.End();
                e.I32(0);
            }
        }
        if (_cctorFail != null)
        {
            var e = new Emitter(_cctorFail);
            e.Get(0); e.I32(-2); if (threads) e.Emit(Op.I32AtomicStore, 0, 2); else e.Store(Op.I32Store);
            if (threads) { e.Get(0); e.I32(-1); e.Emit(Op.MemoryAtomicNotify, 0, 2); e.Op(Op.Drop); }
        }
        if (_cctorExit != null && threads)
        {
            var e = new Emitter(_cctorExit);
            e.Get(0); e.I32(-1); e.Emit(Op.I32AtomicStore, 0, 2);
            e.Get(0); e.I32(-1); e.Emit(Op.MemoryAtomicNotify, 0, 2); e.Op(Op.Drop);
        }
    }

    WasmFunction? _retainContents;
    /// <summary>(obj): retains the references the object holds, by its type id (MemberwiseClone; made when used).</summary>
    public WasmFunction RetainContents => _retainContents ??= M.AddFunction("rt.retain_contents", FT([I], []));
    public WasmFunction? RetainContentsIfMade => _retainContents;
    WasmFunction? _onDestroy;
    public WasmFunction OnDestroy => _onDestroy ??= M.AddFunction("rt.on_destroy", FT([I], [I]));
    public WasmFunction? OnDestroyIfMade => _onDestroy;
    WasmFunction? _onFinalize;
    /// <summary>rt.on_finalize(p): before p's finalizer: the short weak handles to p cleared, the others left for
    /// rt.on_destroy; 1 if the finalizer runs (ObjectMarks.OnFinalize; `1` in a program that sets no mark).</summary>
    public WasmFunction OnFinalize => _onFinalize ??= M.AddFunction("rt.on_finalize", FT([I], [I]));
    public WasmFunction? OnFinalizeIfMade => _onFinalize;
    WasmFunction? _weakAny, _weakDying;
    /// <summary>rt.weak_any(): whether any object is a weak handle's target now (0 in a program that sets no mark); rt.weak_dying(obj):
    /// the weak references to a member of a group that is going are cleared (ObjectMarks.WeakDying; nothing in a
    /// program that sets no mark). Written at the end (RuntimeGen.EmitOnDestroy).</summary>
    public WasmFunction WeakAnyFn => _weakAny ??= M.AddFunction("rt.weak_any", FT([], [I]));
    public WasmFunction WeakDyingFn => _weakDying ??= M.AddFunction("rt.weak_dying", FT([I], []));
    public WasmFunction? WeakAnyIfMade => _weakAny;
    public WasmFunction? WeakDyingIfMade => _weakDying;

    WasmFunction? _clamp;
    /// <summary>(v, lo, hi) → v clamped to [lo, hi].</summary>
    public WasmFunction ClampI32 => _clamp ??= MakeClamp();
    WasmFunction MakeClamp()
    {
        var f = M.AddFunction("rt.clamp_i32", FT([I, I, I], [I]));
        var e = new Emitter(f);
        e.Get(0); e.Get(1); e.Op(Op.I32LtS); e.If(); e.Get(1); e.Op(Op.Return); e.End();
        e.Get(0); e.Get(2); e.Op(Op.I32GtS); e.If(); e.Get(2); e.Op(Op.Return); e.End();
        e.Get(0);
        return f;
    }

    WasmFunction? _arrayElemSize, _arrayCounted, _arrayCopy, _arrayClear, _arrayRetain, _arrayRelease, _enumInfo;

    public WasmFunction ArrayElemSize => _arrayElemSize ??= M.AddFunction("rt.array_elem_size", FT([I], [I]));
    public WasmFunction ArrayCounted => _arrayCounted ??= M.AddFunction("rt.array_counted", FT([I], [I]));
    WasmFunction? _arrayRank;
    public WasmFunction ArrayRank => _arrayRank ??= M.AddFunction("rt.array_rank", FT([I], [I]));
    WasmFunction ArrayRetainRange => _arrayRetain ??= M.AddFunction("rt.array_retain_range", FT([I, I, I], []));
    WasmFunction ArrayReleaseRange => _arrayRelease ??= M.AddFunction("rt.array_release_range", FT([I, I, I], []));
    public WasmFunction EnumInfo => _enumInfo ??= M.AddFunction("rt.enum_info", FT([I], [I]));
    WasmFunction? _enumInfoId;
    /// <summary>The enum table by type id (Enum's static API).</summary>
    public WasmFunction EnumInfoId => _enumInfoId ??= M.AddFunction("rt.enum_info_id", FT([I], [I]));

    /// <summary>(src, si, dst, di, length, size): copy elements with counts; overlapping ranges are fine.</summary>
    public WasmFunction ArrayCopy => _arrayCopy ??= MakeArrayCopy();
    public WasmFunction? ArrayCopyIfMade => _arrayCopy;
    public WasmFunction? ArrayClearIfMade => _arrayClear;
    WasmFunction? _arrayGrow;
    /// <summary>rt.array_grow(container, old, to, n): Rt.ArenaGrowArray where arrays can be on a cycle, else 0 (RuntimeGen.cs).</summary>
    public WasmFunction ArrayGrowFn => _arrayGrow ??= M.AddFunction("rt.array_grow", FT([I, I, I, I], [I]));
    public WasmFunction? ArrayGrowIfMade => _arrayGrow;
    WasmFunction? _partWeak;
    /// <summary>rt.part_weak(obj): Rt.PartMarked in a program with groups, nothing in any other (RuntimeGen.cs).</summary>
    public WasmFunction PartWeakFn => _partWeak ??= M.AddFunction("rt.part_weak", FT([I], []));
    public WasmFunction? PartWeakIfMade => _partWeak;

    // arenas (section 4.3): dispatch on the type id of a member; bodies are emitted with the type graph
    WasmFunction? _arenaClear, _arenaFree, _arenaVisit, _sccOf;
    public WasmFunction ArenaClearFn => _arenaClear ??= M.AddFunction("rt.arena_clear", FT([I], []));
    public WasmFunction ArenaFreeFn => _arenaFree ??= M.AddFunction("rt.arena_free", FT([I], []));
    public WasmFunction ArenaVisitFn => _arenaVisit ??= M.AddFunction("rt.arena_visit", FT([I], []));
    // (a table read: written where it is asked, in CanLink and the stores by address)
    public WasmFunction SccOfFn => _sccOf ??= MarkHinted(M.AddFunction("rt.scc_of", FT([I], [I])));
    static WasmFunction MarkHinted(WasmFunction f) { f.InlineHint = true; return f; }
    WasmFunction? _arenaFin, _arenaFinType;
    /// <summary>rt.arena_finalize(obj): the finalizer of a member that has one (and is not suppressed).</summary>
    public WasmFunction ArenaFinFn => _arenaFin ??= M.AddFunction("rt.arena_finalize", FT([I], []));
    /// <summary>rt.arena_fin_type(type id): 1 for a cycle-capable type with a finalizer.</summary>
    public WasmFunction ArenaFinTypeFn => _arenaFinType ??= M.AddFunction("rt.arena_fin_type", FT([I], [I]));
    public WasmFunction? ArenaFinFnIfMade => _arenaFin;
    public WasmFunction? ArenaFinTypeIfMade => _arenaFinType;
    WasmFunction? _singleThreaded;
    /// <summary>() → 1 when no other thread can ever run (the program starts none); written at the end (RuntimeGen.Finish).</summary>
    public WasmFunction SingleThreadedFn => _singleThreaded ??= M.AddFunction("rt.single_threaded", FT([], [I]));
    public WasmFunction? SingleThreadedIfMade => _singleThreaded;
    WasmFunction? _wideSizes;
    /// <summary>() -> 1 when the allocator rounds sizes above 128 bytes in wider steps (Rt.RoundSmall); written at the end.</summary>
    public WasmFunction WideSizesFn => _wideSizes ??= M.AddFunction("rt.wide_sizes", FT([], [I]));
    public WasmFunction? WideSizesIfMade => _wideSizes;
    WasmFunction? _firstChance;
    /// <summary>() → 1 when the program can add a handler to AppDomain.FirstChanceException; written at the end
    /// (RuntimeGen.Finish), so a program without one has no code for it.</summary>
    public WasmFunction FirstChanceFn => _firstChance ??= M.AddFunction("rt.first_chance", FT([], [I]));
    public WasmFunction? FirstChanceIfMade => _firstChance;
    WasmFunction? _raiseFirstChance;
    /// <summary>(exception) → (): Rt.RaiseFirstChance when the program can add a handler, else nothing (and never called).</summary>
    public WasmFunction RaiseFirstChanceFn => _raiseFirstChance ??= M.AddFunction("rt.raise_first_chance", FT([I], []));
    public WasmFunction? RaiseFirstChanceIfMade => _raiseFirstChance;
    WasmFunction? _cctorTraces;
    /// <summary>(inner exception or 0, TypeInitializationException) → (): the traces of a failed static initializer's
    /// exceptions (Rt.OnInitializerFailed, Rt.OnThrow) in a program that throws; written at the end.</summary>
    public WasmFunction CctorTracesFn => _cctorTraces ??= M.AddFunction("rt.cctor_traces", FT([I, I], []));
    public WasmFunction? CctorTracesIfMade => _cctorTraces;
    WasmFunction? _framesWanted;
    /// <summary>() → 1 when the program asks a stack frame for its method (Exception.TargetSite...); written at the end.</summary>
    public WasmFunction FramesWantedFn => _framesWanted ??= M.AddFunction("rt.frames_wanted", FT([], [I]));
    public WasmFunction? FramesWantedIfMade => _framesWanted;
    WasmFunction? _genericInstances;
    /// <summary>() → [count, (definition id, instance id, n, argument ids...)...] (MakeGenericType); body at the end.</summary>
    public WasmFunction GenericInstancesFn => _genericInstances ??= M.AddFunction("rt.generic_instances", FT([], [I]));
    public WasmFunction? GenericInstancesIfMade => _genericInstances;
    WasmFunction? _programTypes;
    /// <summary>() → [count, ids...]: the program's own types (Assembly.GetTypes).</summary>
    public WasmFunction ProgramTypesFn => _programTypes ??= M.AddFunction("rt.program_types", FT([], [I]));
    public WasmFunction? ProgramTypesIfMade => _programTypes;
    WasmFunction? _typeNameTable;
    /// <summary>() → [count, (full name, type id)...] of the program's and CoreLib's types (Type.GetType); body at the end.</summary>
    public WasmFunction TypeNameTableFn => _typeNameTable ??= M.AddFunction("rt.type_name_table", FT([], [I]));
    public WasmFunction? TypeNameTableIfMade => _typeNameTable;
    WasmFunction? _releaseThreadStatics;
    /// <summary>() → releases the current thread's thread-static references (a thread ends); body at the end.</summary>
    public WasmFunction ReleaseThreadStaticsFn => _releaseThreadStatics ??= M.AddFunction("rt.release_thread_statics", FT([], []));
    public WasmFunction? ReleaseThreadStaticsIfMade => _releaseThreadStatics;
    public WasmFunction? ArenaClearFnIfMade => _arenaClear;
    public WasmFunction? ArenaFreeFnIfMade => _arenaFree;
    public WasmFunction? ArenaVisitFnIfMade => _arenaVisit;
    WasmFunction? _arenaIsSlot;
    /// <summary>(object, offset) -> whether the word at that offset of the object is a slot a cycle can close through.</summary>
    public WasmFunction ArenaIsSlotFn => _arenaIsSlot ??= M.AddFunction("rt.arena_is_slot", FT([I, I], [I]));
    public WasmFunction? ArenaIsSlotFnIfMade => _arenaIsSlot;
    public WasmFunction? SccOfIfMade => _sccOf;
    WasmFunction? _arenaSlots;
    /// <summary>rt.arena_slots(type id): the cycle-capable slots of an object of the type, a nibble each (the slot's
    /// index among the words after the header, plus one), or 0 for a type the word cannot say them for (RuntimeGen).</summary>
    public WasmFunction ArenaSlotsFn => _arenaSlots ??= MarkHinted(M.AddFunction("rt.arena_slots", FT([I], [I])));
    public WasmFunction? ArenaSlotsIfMade => _arenaSlots;
    /// <summary>(array, index, length, size): release and zero elements.</summary>
    public WasmFunction ArrayClear => _arrayClear ??= MakeArrayClear();

    /// <summary>Pushes how far after a vector's first element the array's own first element is, in bytes: an array of
    /// more dimensions has their lengths in between (Types.ArrayDataOffset).</summary>
    public void EmitArrayLead(Emitter e, int arr, int tmp)
    {
        e.Get(arr); e.Call(ArrayRank); e.Tee(tmp); e.I32(4); e.Op(Op.I32Mul); e.I32(7); e.Op(Op.I32Add); e.I32(-8); e.Op(Op.I32And);
        e.Get(tmp); e.I32(1); e.Op(Op.I32Ne); e.Op(Op.I32Mul);
    }

    WasmFunction MakeArrayCopy()
    {
        var f = M.AddFunction("rt.array_copy", FT([I, I, I, I, I, I], []));
        D.NoteFieldWrite(f, Driver.ElementsKey);
        var e = new Emitter(f);
        int s = e.AddLocal(I), d = e.AddLocal(I);
        e.Get(0); e.I32(Types.ArrayData); e.Op(Op.I32Add); e.Get(1); e.Get(5); e.Op(Op.I32Mul); e.Op(Op.I32Add); EmitArrayLead(e, 0, s); e.Op(Op.I32Add); e.Set(s);
        e.Get(2); e.I32(Types.ArrayData); e.Op(Op.I32Add); e.Get(3); e.Get(5); e.Op(Op.I32Mul); e.Op(Op.I32Add); EmitArrayLead(e, 2, d); e.Op(Op.I32Add); e.Set(d);
        if (D.ThreadMode)
        {
            // Threads: the source's elements are counted and the destination's old ones taken out with no store able
            // to come in between (rt.bulk_enter: every place's lock); the old ones, set aside, are released after.
            int bytes = e.AddLocal(I), tmp = e.AddLocal(I); int locks = e.AddLocal(ValType.I64);
            e.Get(4); e.Get(5); e.Op(Op.I32Mul); e.Tee(bytes); e.Op(Op.I32Eqz); e.If(); e.Op(Op.Return); e.End();
            // (elements that hold no reference: their bytes, with no lock)
            e.Get(0); e.Call(ArrayCounted); e.Op(Op.I32Eqz);
            e.If(); e.Get(d); e.Get(s); e.Get(bytes); e.Op(Op.MemoryCopy); e.Op(Op.Return); e.End();
            e.Get(bytes); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "Alloc", 1).Fn); e.Set(tmp);
            // (the locks of the two ranges' places, not every place's)
            e.Get(s); e.Get(bytes); e.Call(StripeMask); e.Get(d); e.Get(bytes); e.Call(StripeMask); e.Op(Op.I64Or); e.Set(locks);
            // Elements that are one reference each: counted under the places' locks alone, each by the exchange
            // rt.retain makes without a lock (what was counted is kept in tmp). An element that needs the runtime
            // lock (a member's first outside reference) ends the attempt: the counts are given back and the copy is
            // made the other way, with the runtime lock taken first.
            e.Get(5); e.I32(4); e.Op(Op.I32Eq);
            e.If();
            {
                int i = e.AddLocal(I), q = e.AddLocal(I), r = e.AddLocal(I);
                e.Get(locks); e.Call(StripesEnter);
                e.I32(0); e.Set(i);
                var fail = e.Block();
                var ok = e.Block(); var next = e.Loop();
                e.Get(i); e.Get(bytes); e.Op(Op.I32GeU); e.BrIf(ok);
                e.Get(tmp); e.Get(i); e.Op(Op.I32Add);
                e.Get(s); e.Get(i); e.Op(Op.I32Add); e.Load(Op.I32Load); e.Tee(q);
                e.Store(Op.I32Store);
                e.Get(q);
                e.If();
                var counted = e.Block(); var again = e.Loop();
                e.Get(q); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
                e.If();
                e.Get(q); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(counted);
                e.Br(again);
                e.End();
                e.Get(r); e.I32(-2); e.Op(Op.I32LtS);
                e.If();
                e.Get(q); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(counted);
                e.Br(again);
                e.End();
                e.Get(r); e.I32(-2); e.Op(Op.I32Eq); e.BrIf(fail);
                e.End(); e.End();
                e.End();
                e.Get(i); e.I32(4); e.Op(Op.I32Add); e.Set(i);
                e.Br(next);
                e.End(); e.End();
                e.Get(tmp); e.Get(d); e.Get(bytes); e.Op(Op.MemoryCopy);
                e.Get(d); e.Get(s); e.Get(bytes); e.Call(CopyWords);
                e.Get(locks); e.Call(StripesExit);
                e.Get(2); e.Get(tmp); e.Get(4); e.Call(ArrayReleaseRange);
                e.Get(tmp); e.Get(bytes); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "Free", 2).Fn);
                e.Op(Op.Return);
                e.End();
                e.Get(locks); e.Call(StripesExit);
                // (the element that stopped it was not counted: those before it, kept in tmp, are released)
                e.I32(0); e.Set(q);
                { var undone = e.Block(); var undo = e.Loop();
                  e.Get(q); e.Get(i); e.Op(Op.I32GeU); e.BrIf(undone);
                  e.Get(tmp); e.Get(q); e.Op(Op.I32Add); e.Load(Op.I32Load); e.Call(Release);
                  e.Get(q); e.I32(4); e.Op(Op.I32Add); e.Set(q);
                  e.Br(undo); e.End(); e.End(); }
            }
            e.End();
            e.Get(locks); e.Call(MaskEnter);
            e.Get(0); e.Get(s); e.Get(4); e.Call(ArrayRetainRange);
            e.Get(tmp); e.Get(d); e.Get(bytes); e.Op(Op.MemoryCopy);
            e.Get(d); e.Get(s); e.Get(bytes); e.Call(CopyWords);
            e.Get(locks); e.Call(MaskExit);
            e.Get(2); e.Get(tmp); e.Get(4); e.Call(ArrayReleaseRange);
            e.Get(tmp); e.Get(bytes); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "Free", 2).Fn);
            return f;
        }
        e.Get(0); e.Get(s); e.Get(4); e.Call(ArrayRetainRange);
        e.Get(2); e.Get(d); e.Get(4); e.Call(ArrayReleaseRange);
        e.Get(d); e.Get(s); e.Get(4); e.Get(5); e.Op(Op.I32Mul); e.Op(Op.MemoryCopy);
        return f;
    }

    WasmFunction MakeArrayClear()
    {
        var f = M.AddFunction("rt.array_clear", FT([I, I, I, I], []));
        D.NoteFieldWrite(f, Driver.ElementsKey);
        var e = new Emitter(f);
        int a = e.AddLocal(I);
        e.Get(0); e.I32(Types.ArrayData); e.Op(Op.I32Add); e.Get(1); e.Get(3); e.Op(Op.I32Mul); e.Op(Op.I32Add); EmitArrayLead(e, 0, a); e.Op(Op.I32Add); e.Set(a);
        if (D.ThreadMode)
        {
            int bytes = e.AddLocal(I), tmp = e.AddLocal(I), p = e.AddLocal(I), end = e.AddLocal(I), l = e.AddLocal(I);
            e.Get(2); e.Get(3); e.Op(Op.I32Mul); e.Tee(bytes); e.Op(Op.I32Eqz); e.If(); e.Op(Op.Return); e.End();
            e.Get(a); e.Get(bytes); e.Op(Op.I32Add); e.Set(end);
            // nothing there to clear (a list emptied twice, a pool's array given back empty): nothing is locked
            e.Get(bytes); e.I32(3); e.Op(Op.I32And); e.Op(Op.I32Eqz);
            e.If();
            e.Get(a); e.Set(p);
            { var some = e.Block(); var next = e.Loop();
              e.Get(p); e.Get(end); e.Op(Op.I32GeU); e.If(); e.Op(Op.Return); e.End();
              e.Get(p); e.Emit(Op.I32AtomicLoad, 0, 2); e.BrIf(some);
              e.Get(p); e.I32(4); e.Op(Op.I32Add); e.Set(p); e.Br(next);
              e.End(); e.End(); }
            e.End();
            e.Get(bytes); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "Alloc", 1).Fn); e.Set(tmp);
            // a few references: each is taken out under its own place's lock, as a store takes one out (rt.store_ref).
            // Every place's lock at once, with the runtime lock, stops every thread that counts anything.
            e.Get(3); e.I32(4); e.Op(Op.I32Eq); e.Get(2); e.I32(32); e.Op(Op.I32LeU); e.Op(Op.I32And);
            e.If();
            e.Get(a); e.Set(p);
            { var done = e.Block(); var next = e.Loop();
              e.Get(p); e.Get(end); e.Op(Op.I32GeU); e.BrIf(done);
              e.Get(p); e.Call(RefLock); e.Set(l);
              e.Get(tmp); e.Get(p); e.Get(a); e.Op(Op.I32Sub); e.Op(Op.I32Add);
              e.Get(p); e.I32(0); e.Emit(Op.I32AtomicRmwXchg, 0, 2);
              e.Store(Op.I32Store);
              e.Get(l); e.Call(RefUnlock);
              e.Get(p); e.I32(4); e.Op(Op.I32Add); e.Set(p); e.Br(next);
              e.End(); e.End(); }
            e.Get(0); e.Get(tmp); e.Get(2); e.Call(ArrayReleaseRange);
            e.Get(tmp); e.Get(bytes); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "Free", 2).Fn);
            e.Op(Op.Return);
            e.End();
            e.Get(a); e.Get(bytes); e.Call(StripeMask); e.Call(StripesEnter);
            e.Get(tmp); e.Get(a); e.Get(bytes); e.Op(Op.MemoryCopy);
            e.Get(a); e.Get(bytes); e.Call(ZeroWords);
            e.Get(a); e.Get(bytes); e.Call(StripeMask); e.Call(StripesExit);
            e.Get(0); e.Get(tmp); e.Get(2); e.Call(ArrayReleaseRange);
            e.Get(tmp); e.Get(bytes); e.Call(D.LibMethod("Il2Wasm.Runtime.Rt", "Free", 2).Fn);
            return f;
        }
        e.Get(0); e.Get(a); e.Get(2); e.Call(ArrayReleaseRange);
        // (a few words one by one: memory.fill is a call into the engine, slower than the stores; a list builder
        // clears its whole array every time it is reused, most often a handful of words)
        int nb = e.AddLocal(I), stop = e.AddLocal(I);
        e.Get(2); e.Get(3); e.Op(Op.I32Mul); e.Tee(nb);
        e.I32(256); e.Op(Op.I32LeU); e.Get(nb); e.I32(3); e.Op(Op.I32And); e.Op(Op.I32Eqz); e.Op(Op.I32And);
        e.If();
        {
            e.Get(a); e.Get(nb); e.Op(Op.I32Add); e.Set(stop);
            var done = e.Block(); var next = e.Loop();
            e.Get(a); e.Get(stop); e.Op(Op.I32GeU); e.BrIf(done);
            e.Get(a); e.I32(0); e.Store(Op.I32Store);
            e.Get(a); e.I32(4); e.Op(Op.I32Add); e.Set(a); e.Br(next);
            e.End(); e.End();
        }
        e.Else();
        e.Get(a); e.I32(0); e.Get(nb); e.Op(Op.MemoryFill);
        e.End();
        return f;
    }

    IEnumerable<(int id, IArrayTypeSymbol at)> ConstructedArrays() =>
        D.Constructed.Where(id => !D.DisplayById.ContainsKey(id) && D.TypeById[id] is IArrayTypeSymbol).Select(id => (id, (IArrayTypeSymbol)D.TypeById[id]));

    /// <summary>A dispatch on the type id in `tid`: a br_table to each case's body (which returns); the other ids fall
    /// out after it. (A chain of comparisons would cost every call a walk over the types.) Type ids must be numbered.</summary>
    /// <summary>A branch on a type id to the code of its case. Cases with the same key (not null) are one piece of
    /// code: in a large program most types share a few (the classes whose arrays hold one reference an element).</summary>
    public void IdSwitch(Emitter e, int tid, List<(int id, object? key, Action body)> cases)
    {
        var cs = cases.Select(c => (ext: D.ExtId(c.id), c.key, c.body)).Where(c => c.ext >= 0).GroupBy(c => c.ext).Select(g => g.First()).OrderBy(c => c.ext).ToList();
        if (cs.Count == 0) return;
        var pieces = new List<Action>();
        var pieceOf = new Dictionary<object, int>();
        var caseOf = new int[cs.Count];
        for (int i = 0; i < cs.Count; i++)
        {
            if (cs[i].key != null && pieceOf.TryGetValue(cs[i].key!, out int have)) { caseOf[i] = have; continue; }
            caseOf[i] = pieces.Count;
            if (cs[i].key != null) pieceOf[cs[i].key!] = pieces.Count;
            pieces.Add(cs[i].body);
        }
        var dflt = e.Block();
        var labels = new Label[pieces.Count];
        for (int i = pieces.Count - 1; i >= 0; i--) labels[i] = e.Block();
        var targets = new Label[D.TypeCount];
        for (int i = 0; i < targets.Length; i++) targets[i] = dflt;
        for (int i = 0; i < cs.Count; i++) if (cs[i].ext < targets.Length) targets[cs[i].ext] = labels[caseOf[i]];
        e.Get(tid); e.BrTable(targets, dflt);
        foreach (var body in pieces) { e.End(); body(); }
        e.End();
    }

    /// <summary>A function of an object that is a small number by the object's type: read from a table in the data
    /// (a byte or two a type), not branched to. False when a value does not fit (the caller branches).</summary>
    bool IdNumbers(WasmFunction fn, int dflt, List<(int id, int value)> cases)
    {
        int max = Math.Max(dflt, cases.Count == 0 ? 0 : cases.Max(c => c.value));
        if (dflt < 0 || cases.Any(c => c.value < 0) || max > 65535) return false;
        int width = max <= 255 ? 1 : 2;
        var blob = new byte[D.TypeCount * width];
        void Put(int ext, int v) { blob[ext * width] = (byte)v; if (width == 2) blob[ext * width + 1] = (byte)(v >> 8); }
        for (int i = 0; i < D.TypeCount; i++) Put(i, dflt);
        foreach (var (id, v) in cases) { int ext = D.ExtId(id); if (ext >= 0 && ext < D.TypeCount) Put(ext, v); }
        var e = new Emitter(fn);
        e.Get(0); e.Load(Op.I32Load, 4);
        if (width == 2) { e.I32(1); e.Op(Op.I32Shl); }
        e.I32Addr(D.DataBlob(blob, width)); e.Op(Op.I32Add);
        e.Load(width == 1 ? Op.I32Load8U : Op.I32Load16U);
        return true;
    }

    /// <summary>The array helpers' dispatch on the array's type (after the type ids are numbered).</summary>
    public void EmitArrayDispatch()
    {
        if (_arrayElemSize != null && !IdNumbers(_arrayElemSize, 1, ConstructedArrays().Select(x => (x.id, D.Types.StorageOf(D.Types.Canonical(x.at.ElementType)).size)).ToList()))
        {
            var e = new Emitter(_arrayElemSize);
            int tid = e.AddLocal(I);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            IdSwitch(e, tid, ConstructedArrays().Select(x => { int size = D.Types.StorageOf(D.Types.Canonical(x.at.ElementType)).size; return (x.id, (object?)size, (Action)(() => { e.I32(size); e.Op(Op.Return); })); }).ToList());
            e.I32(1);
        }
        if (_arrayRank != null) IdNumbers(_arrayRank, 1, ConstructedArrays().Where(x => x.at.Rank != 1).Select(x => (x.id, x.at.Rank)).ToList());
        if (_arrayCounted != null) IdNumbers(_arrayCounted, 0, ConstructedArrays().Where(x => D.Types.NeedsRc(D.Types.Canonical(x.at.ElementType))).Select(x => (x.id, 1)).ToList());
        foreach (var (fn, release) in new[] { (_arrayRetain, false), (_arrayRelease, true) })
        {
            if (fn == null) continue;
            var e = new Emitter(fn);
            int tid = e.AddLocal(I);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            IdSwitch(e, tid, ConstructedArrays().Where(x => D.Types.NeedsRc(D.Types.Canonical(x.at.ElementType))).Select(x =>
            {
                var et = D.Types.Canonical(x.at.ElementType);
                var target = release ? ReleaseRange(et) : RetainRange(et);
                return (x.id, (object?)target, (Action)(() => { e.Get(1); e.Get(2); e.Call(target); e.Op(Op.Return); }));
            }).ToList());
        }
    }

    /// <summary>Per boxed enum type: [count][flags][size][unsigned] then 16-byte entries (value i64, name).</summary>
    public void EmitEnumInfo()
    {
        if (_enumInfo == null && _enumInfoId == null) return;
        if (_enumInfo != null) { var e0 = new Emitter(_enumInfo); e0.Get(0); e0.Load(Op.I32Load, 4); e0.Call(EnumInfoId); }
        var e = new Emitter(EnumInfoId);
        int tid = 0;
        foreach (var id in D.Constructed)
        {
            if (D.DisplayById.ContainsKey(id) || D.TypeById[id] is not INamedTypeSymbol { TypeKind: TypeKind.Enum } et) continue;
            var p = Types.PrimOf(et);
            bool unsigned = !Types.IsSigned(p);
            // .NET orders by the unsigned value; duplicates: first declared wins in GetName
            var dedup = new List<(long value, string name)>();
            foreach (var g in et.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
            {
                long v = System.Convert.ToInt64(g.ConstantValue is ulong u ? unchecked((long)u) : g.ConstantValue);
                if (!dedup.Any(x => x.value == v)) dedup.Add((v, g.Name));
            }
            dedup.Sort((a, b) => ((ulong)a.value).CompareTo((ulong)b.value));
            bool flags = et.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute");
            var blob = new List<byte>();
            var relocs = new List<(int, IAddress)>();
            void I32b(int v) => blob.AddRange(BitConverter.GetBytes(v));
            I32b(dedup.Count); I32b(flags ? 1 : 0); I32b(Types.PrimSize(p)); I32b(unsigned ? 1 : 0);
            foreach (var (value, name) in dedup)
            {
                blob.AddRange(BitConverter.GetBytes(value));
                relocs.Add((blob.Count, D.StringLiteral(name))); I32b(0); I32b(0);
            }
            var addr = D.DataBlob([.. blob], 8, relocs);
            e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(addr); e.Op(Op.Return); e.End();
        }
        e.Op(Op.Unreachable);
    }

    // ---------------- generic equality / hash / compare ----------------
    readonly Dictionary<string, WasmFunction> _generic = [];

    WasmFunction GenericFn(string kind, ITypeSymbol t, FuncType sig, Action<Emitter> body)
    {
        var key = kind + " " + Types.Key(t);
        if (_generic.TryGetValue(key, out var f)) return f;
        f = M.AddFunction("rt." + key, sig);
        _generic[key] = f;
        body(new Emitter(f));
        return f;
    }

    static ValType W(ITypeSymbol t) => Types.WasmType(t) ?? ValType.I32;

    IMethodSymbol ObjMethod(string name, int arity) =>
        ((INamedTypeSymbol)D.ObjectType).GetMembers(name).OfType<IMethodSymbol>().First(m => m.Parameters.Length == arity && !m.IsStatic);

    IMethodSymbol? FindMethod(ITypeSymbol t, string name, ITypeSymbol? paramType)
    {
        for (var b = t as INamedTypeSymbol; b != null; b = b.BaseType is { } bt ? (INamedTypeSymbol)D.Types.Canonical(bt) : null)
        {
            var m = b.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(x => !x.IsStatic && x.Parameters.Length == (paramType == null ? 0 : 1)
                && (paramType == null || Types.Key(D.Types.Canonical(x.Parameters[0].Type)) == Types.Key(paramType)));
            if (m != null) return m;
            if (t.IsValueType) break;
        }
        return null;
    }

    public WasmFunction GenericEquals(ITypeSymbol t)
    {
        var w = W(t);
        return GenericFn("equals", t, FT([w, w], [I]), e =>
        {
            var p = Types.PrimOf(t);
            if (p != Prim.None)
            {
                if (Types.IsFloat(p))
                {
                    var eq = p == Prim.F32 ? Op.F32Eq : Op.F64Eq; var ne = p == Prim.F32 ? Op.F32Ne : Op.F64Ne;
                    e.Get(0); e.Get(1); e.Op(eq);
                    e.Get(0); e.Get(0); e.Op(ne); e.Get(1); e.Get(1); e.Op(ne); e.Op(Op.I32And);
                    e.Op(Op.I32Or);
                    return;
                }
                e.Get(0); e.Get(1); e.Op(w == L ? Op.I64Eq : Op.I32Eq);
                return;
            }
            if (Types.IsRef(t))
            {
                e.Get(0); e.Get(1); e.Op(Op.I32Eq); e.If(); e.I32(1); e.Op(Op.Return); e.End();
                e.Get(0); e.Op(Op.I32Eqz); e.Get(1); e.Op(Op.I32Eqz); e.Op(Op.I32Or); e.If(); e.I32(0); e.Op(Op.Return); e.End();
                e.Get(0); e.Get(1); e.Call(D.Stub(ObjMethod("Equals", 1)));
                return;
            }
            // struct: Equals(T) if declared, else Equals(object) through a box
            var eqT = FindMethod(t, "Equals", t);
            if (eqT != null) { e.Get(0); e.Get(1); e.Call(D.Method(eqT).Fn); return; }
            var slow = D.LibMethod("Il2Wasm.Runtime.Generics", "SlowEquals").Sym;
            e.Get(0); e.Get(1); e.Call(D.Method(slow.Construct(t)).Fn);
        });
    }

    public WasmFunction GenericHash(ITypeSymbol t)
    {
        var w = W(t);
        return GenericFn("hash", t, FT([w], [I]), e =>
        {
            var p = Types.PrimOf(t);
            switch (p)
            {
                case Prim.None: break;
                case Prim.I64: case Prim.U64:
                    e.Get(0); e.Op(Op.I32WrapI64); e.Get(0); e.I64(32); e.Op(Op.I64ShrS); e.Op(Op.I32WrapI64); e.Op(Op.I32Xor); return;
                case Prim.Char:
                    e.Get(0); e.Get(0); e.I32(16); e.Op(Op.I32Shl); e.Op(Op.I32Or); return;
                case Prim.F64:
                    {
                        int b = e.AddLocal(L);
                        e.Get(0); e.Op(Op.I64ReinterpretF64); e.Set(b);
                        // NaN and ±0 hash alike
                        e.Get(b); e.I64(1); e.Op(Op.I64Sub); e.I64(0x7FFFFFFFFFFFFFFF); e.Op(Op.I64And); e.I64(0x7FF0000000000000); e.Op(Op.I64GeU);
                        e.If(); e.Get(b); e.I64(0x7FF0000000000000); e.Op(Op.I64And); e.Set(b); e.End();
                        e.Get(b); e.Op(Op.I32WrapI64); e.Get(b); e.I64(32); e.Op(Op.I64ShrS); e.Op(Op.I32WrapI64); e.Op(Op.I32Xor);
                        return;
                    }
                case Prim.F32:
                    {
                        int b = e.AddLocal(I);
                        e.Get(0); e.Op(Op.I32ReinterpretF32); e.Set(b);
                        e.Get(b); e.I32(1); e.Op(Op.I32Sub); e.I32(0x7FFFFFFF); e.Op(Op.I32And); e.I32(0x7F800000); e.Op(Op.I32GeU);
                        e.If(); e.Get(b); e.I32(0x7F800000); e.Op(Op.I32And); e.Set(b); e.End();
                        e.Get(b);
                        return;
                    }
                default: e.Get(0); return;
            }
            if (Types.IsRef(t))
            {
                e.Get(0); e.Op(Op.I32Eqz); e.If(); e.I32(0); e.Op(Op.Return); e.End();
                e.Get(0); e.Call(D.Stub(ObjMethod("GetHashCode", 0)));
                return;
            }
            var h = FindMethod(t, "GetHashCode", null);
            if (h != null && Types.Key(D.Types.Canonical(h.ContainingType)) == Types.Key(t)) { e.Get(0); e.Call(D.Method(h).Fn); return; }
            e.I32(0);
        });
    }
}

