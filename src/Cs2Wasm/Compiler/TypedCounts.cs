using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Section 4.5, step 4 of docs/AUDIT.md: in a program with threads, a count, and a read that counts, is atomic only
/// where an object another thread can reach can be (Sharing.cs). The compiler emits a placeholder per static type;
/// once the whole program is compiled and the types another thread can reach are known, each becomes the atomic
/// function or the plain one. Without threads the placeholders are the plain functions from the start.
/// </summary>
internal sealed partial class RuntimeFns
{
    enum CountKind { Retain, Release, LoadRetain, StoreRef }
    readonly Dictionary<string, (CountKind kind, ITypeSymbol type, ITypeSymbol? container)> _countPh = [];
    readonly Dictionary<string, WasmFunction> _countPhByKey = [];
    readonly Dictionary<WasmFunction, string> _countPhKey = [];
    WasmFunction? _retainPlain, _releasePlain, _storeRefPlain;
    public WasmFunction? ReleasePlainIfMade => _releasePlain;

    static readonly bool s_noTypedCounts = Environment.GetEnvironmentVariable("CS2WASM_NO_TYPED_COUNTS") == "1";

    /// <summary>(p) -> p: a retain of a value of this static type.</summary>
    public WasmFunction RetainOf(ITypeSymbol? t) => !D.ThreadMode || t == null || s_noTypedCounts ? Retain : Ph(CountKind.Retain, t, null, FT([I], [I]));
    /// <summary>(p): a release of a value of this static type.</summary>
    public WasmFunction ReleaseOf(ITypeSymbol? t) => !D.ThreadMode || t == null || s_noTypedCounts ? Release : Ph(CountKind.Release, t, null, FT([I], []));
    /// <summary>(address) -> p: a read that counts, of a slot of an object of static type `container`.</summary>
    public WasmFunction LoadRetainOf(ITypeSymbol container, ITypeSymbol value) => !D.ThreadMode || s_noTypedCounts ? LoadRetain : Ph(CountKind.LoadRetain, value, container, FT([I], [I]));

    /// <summary>(address, value) -> old: a store into a slot of an object of static type `container`.</summary>
    public WasmFunction StoreRefOf(ITypeSymbol? container) => !D.ThreadMode || container == null || s_noTypedCounts ? StoreRef : Ph(CountKind.StoreRef, container, container, FT([I, I], [I]));

    WasmFunction Ph(CountKind k, ITypeSymbol t, ITypeSymbol? c, FuncType ft)
    {
        t = D.Types.Canonical(t);
        if (c != null) c = D.Types.Canonical(c);
        var key = k + " " + Types.Key(t) + (c != null ? " in " + Types.Key(c) : "");
        if (_countPhByKey.TryGetValue(key, out var f)) return f;
        f = new WasmFunction("count " + key, ft);
        _countPhByKey[key] = f;
        _countPhKey[f] = key;
        _countPh[key] = (k, t, c);
        return f;
    }

    /// <summary>The plain retain and release of a program with threads: as the atomic ones (RuntimeFns2
    /// EmitThreadSupport), with plain loads and stores, for objects no other thread can reach. An arena's member still
    /// takes the runtime lock for its first and last outside reference (the arena's record is shared).</summary>
    void EmitPlainCounts(Func<WasmFunction> retainSlow, Func<WasmFunction> releaseSlow, bool arenas)
    {
        // CS2WASM_SHARE_CHECK=1: an object counted plainly is one this thread made (the allocator writes the thread's
        // number into the header, Rt.ShareCheck); one another thread made traps here, a fault of Sharing.cs
        void Check(Emitter e)
        {
            if (!ShareCheckOn) return;
            int s = e.AddLocal(I);
            e.Get(0); e.Load(Op.I32Load); e.I32(0); e.Op(Op.I32Ne);
            e.Get(0); e.Load(Op.I32Load); e.I32(-1); e.Op(Op.I32Ne); e.Op(Op.I32And);
            e.If();
            e.Get(0); e.Load(Op.I32Load, 8); e.I32(24); e.Op(Op.I32ShrU); e.I32(0x1F); e.Op(Op.I32And); e.Tee(s);
            e.Get(s); e.GGet(D.ThreadId); e.I32(0x1F); e.Op(Op.I32And); e.Op(Op.I32Ne); e.Op(Op.I32And);
            e.If(); e.Get(0); e.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ShareCheckFail", 1).Fn); e.Op(Op.Unreachable); e.End();
            e.End();
        }
        _retainPlain = M.AddFunction("rt.retain_plain", FT([I], [I]));
        {
            var e = new Emitter(_retainPlain);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            Check(e);
            e.Get(0); e.Load(Op.I32Load); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Store(Op.I32Store);
            if (arenas)
            {
                e.Else();
                e.Get(r); e.I32(-2); e.Op(Op.I32LtS);
                e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
                e.Else();
                e.Get(r); e.I32(-2); e.Op(Op.I32Eq);
                e.If(); e.Get(0); e.Call(retainSlow()); e.Op(Op.Drop); e.End();
                e.End();
            }
            e.End();
            e.End();
            e.Get(0);
        }
        _storeRefPlain = M.AddFunction("rt.store_ref_plain", FT([I, I], [I]));
        {
            var e = new Emitter(_storeRefPlain);
            e.Get(0); e.Load(Op.I32Load); e.Get(0); e.Get(1); e.Store(Op.I32Store);
        }
        _releasePlain = M.AddFunction("rt.release_plain", FT([I], []));
        {
            var e = new Emitter(_releasePlain);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            Check(e);
            e.Get(0); e.Load(Op.I32Load); e.Tee(r); e.I32(1); e.Op(Op.I32GtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
            e.Else();
            e.Get(r); e.I32(1); e.Op(Op.I32Eq);
            e.If(); e.Get(0); e.I32(0); e.Store(Op.I32Store); e.Get(0); e.Call(Destroy);
            if (arenas)
            {
                e.Else();
                e.Get(r); e.I32(-3); e.Op(Op.I32LtS);
                e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Store(Op.I32Store);
                e.Else();
                e.Get(r); e.I32(-1); e.Op(Op.I32LtS);
                e.If(); e.Get(0); e.Call(releaseSlow()); e.Op(Op.Drop); e.End();
                e.End();
            }
            e.End();
            e.End();
            e.End();
        }
    }

    /// <summary>Each placeholder becomes the atomic function or the plain one (the read that counts: a plain read
    /// and the value's retain) by whether another thread can reach such an object. Called again for functions made
    /// later; idempotent.</summary>
    public void ResolveCounts(Sharing? sh)
    {
        if (_countPh.Count == 0) return;
        bool threads = D.ThreadMode;
        foreach (var f in M.Functions)
        {
            if (f.IsImport || !f.Body.Any(x => x.Op == Op.Call && x.Ref is WasmFunction w && _countPhKey.ContainsKey(w))) continue;
            var body = new List<Instr>(f.Body.Count + 4);
            foreach (var ins in f.Body)
            {
                if (ins.Op != Op.Call || ins.Ref is not WasmFunction w || !_countPhKey.TryGetValue(w, out var key)) { body.Add(ins); continue; }
                var (k, t, c) = _countPh[key];
                bool shared = !threads || sh == null || sh.MayBeShared(t);
                switch (k)
                {
                    case CountKind.Retain: body.Add(ins with { Ref = shared || _retainPlain == null ? Retain : _retainPlain }); break;
                    case CountKind.Release:
                        if (shared || _releasePlain == null) { body.Add(ins with { Ref = Release }); break; }
                        // (an object no other thread can reach: as without threads, a leaf's release is written out in
                        // place, WasmModule.ExpandLeafReleases)
                        body.Add(ins with { Ref = _releasePlain, Imm2 = FunctionCompiler.ExactTypeHint(D, t, plain: true) });
                        break;
                    // (a store into a slot, a read of one: by whether a slot of a reachable object can be there, which an
                    // array type reached only as an array with no element has none of: Sharing.MaySlotsBeShared)
                    case CountKind.StoreRef: body.Add(ins with { Ref = !threads || sh == null || sh.MaySlotsBeShared(t) || _storeRefPlain == null ? StoreRef : _storeRefPlain }); break;
                    case CountKind.LoadRetain:
                        if (!threads || sh == null || sh.MaySlotsBeShared(c!) || _retainPlain == null) { body.Add(ins with { Ref = LoadRetain }); break; }
                        // (no other thread stores into this slot: the read, then the count)
                        body.Add(ins with { Op = Op.I32Load, Imm = 0, Imm2 = 2, Ref = null });
                        body.Add(ins with { Ref = shared ? Retain : _retainPlain });
                        break;
                }
            }
            f.Body = body;
        }
    }
}
