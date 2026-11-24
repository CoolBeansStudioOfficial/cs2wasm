#nullable enable
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// Exception filters, two-pass (SPEC 3): a filter runs before the finally blocks of the frames it would unwind, as the
// operation-tree front end did it (Compiler/Filters.cs), on IL:
//   - every try with catch clauses pushes a record [site, frame base] on a stack of its own (rt.eh.records) while its
//     body runs, and takes it off when the body is left either way (rt.eh_push / rt.eh_pop);
//   - a throw (and a rethrow) first walks the records from the top (rt.first_pass): each site's test (rt.eh_test)
//     says, without unwinding, which clause would take the exception: the type tests in order, and a filter's own
//     function, which runs on the method's frame (what a filter uses is a frame variable: Ir/FilterSlots.cs); the
//     deciding record and clause are noted (__eh_record, __eh_clause);
//   - then WASM's unwinding runs the finally blocks, and a try with a filter takes the exception only if it is the
//     decided record (rt.eh_decide), the clause decided; a decision for another record, stale or none (an exception
//     that came another way: the first pass of a later throw inside a finally) is taken again from that record.
//   - a filter's own function is a boundary (what escapes a filter is swallowed, the filter is false), and so is a
//     static constructor's wrapper (an exception out of a .cctor is the runtime's to wrap).
// A program without filters keeps no record: the functions are empty and the back end inlines them away.
// Threads: the IL front end compiles one thread today; the records then need a stack per thread.
public sealed partial class IlCompiler
{
    sealed record EhClauseInfo(TypeDesc? Type, WasmFunction? Filter, bool OnePassFilter);
    readonly List<List<EhClauseInfo>> _ehSites = [[]]; // (site 0: a boundary, it takes everything)
    bool _anyFilter;
    WasmGlobal? _ehTop, _ehRecord, _ehClause;
    WasmFunction? _ehPush, _ehPop, _firstPass, _firstPassFrom, _ehDecide, _ehTest, _throwHook;
    const int EhRecordsSize = 1 << 17;

    WasmGlobal EhGlobal(ref WasmGlobal? g, string name) { if (g == null) { g = new WasmGlobal(name, ValType.I32, true, 0); Module.Globals.Add(g); } return g; }
    static readonly ValType I = ValType.I32;

    internal WasmFunction EhPush => _ehPush ??= Module.AddFunction("rt.eh_push", new FuncType([I, I], [I]));
    internal WasmFunction EhPop => _ehPop ??= Module.AddFunction("rt.eh_pop", new FuncType([I], []));
    internal WasmFunction FirstPass => _firstPass ??= Module.AddFunction("rt.first_pass", new FuncType([I], []));
    internal WasmFunction EhDecide => _ehDecide ??= Module.AddFunction("rt.eh_decide", new FuncType([I, I], [I]));
    /// <summary>(ex): before a throw, the exception's trace and the first pass.</summary>
    internal WasmFunction ThrowHook
    {
        get
        {
            if (_throwHook != null) return _throwHook;
            _throwHook = Module.AddFunction("rt.throw_hook", new FuncType([I], []));
            // (the trace: DebugInfo.cs)
            if (TryCoreMethod("Il2Wasm.Runtime", "Rt", "OnThrow") is { } ot) _onThrow = FunctionFor(ot);
            return _throwHook;
        }
    }
    WasmFunction? _onThrow;

    internal int NewEhSite(List<(TypeDesc? type, WasmFunction? filter, bool onePass)> clauses)
    {
        _ehSites.Add(clauses.Select(c => new EhClauseInfo(c.type, c.filter, c.onePass)).ToList());
        if (clauses.Any(c => c.filter != null || c.onePass)) _anyFilter = true;
        return _ehSites.Count - 1;
    }

    /// <summary>The bodies, once the program is known (before the type tests are written: eh_test makes some).</summary>
    void EmitEhSupport()
    {
        if (!_anyFilter)
        {
            if (_ehPush != null) new Emitter(_ehPush).I32(0);
            if (_ehDecide != null) new Emitter(_ehDecide).I32(0);
            if (_throwHook != null) EmitThrowHookBody();
            return;
        }
        var top = EhGlobal(ref _ehTop, "__eh_top");
        var record = EhGlobal(ref _ehRecord, "__eh_record");
        var clauseG = EhGlobal(ref _ehClause, "__eh_clause");
        var records = NamedStatic("rt.eh.records", EhRecordsSize, 8);
        _ = EhPush; _ = EhPop; _ = FirstPass; _ = EhDecide;
        _ehTest = Module.AddFunction("rt.eh_test", new FuncType([I, I, I], [I]));
        _firstPassFrom = Module.AddFunction("rt.first_pass_from", new FuncType([I, I], []));
        {
            // eh_push(site, frame base) -> the record
            var e = new Emitter(_ehPush!);
            int r = e.AddLocal(I);
            e.GGet(top); e.Op(WOp.I32Eqz); e.If(); e.I32Addr(records); e.GSet(top); e.End();
            e.GGet(top); e.Set(r);
            // (more tries under way than the records hold: the stack overflows, as .NET's would long before)
            e.Get(r); e.I32Addr(records); e.Op(WOp.I32Sub); e.I32(EhRecordsSize - 8); e.Op(WOp.I32GeU); e.If(); e.Op(WOp.Unreachable); e.End();
            e.Get(r); e.Get(0); e.Store(WOp.I32Store);
            e.Get(r); e.Get(1); e.Store(WOp.I32Store, 4);
            e.Get(r); e.I32(8); e.Op(WOp.I32Add); e.GSet(top);
            e.Get(r);
        }
        {
            // eh_pop(record): the records from it on are gone (twice is harmless)
            var e = new Emitter(_ehPop!);
            e.Get(0); e.GSet(top);
        }
        {
            // first_pass_from(ex, record): from that record down, the first whose test takes the exception
            var e = new Emitter(_firstPassFrom);
            int c = e.AddLocal(I);
            var brk = e.Block(); var loop = e.Loop();
            e.Get(1); e.I32Addr(records); e.Op(WOp.I32LtS); e.BrIf(brk);
            e.Get(1); e.Load(WOp.I32Load); e.Get(1); e.Load(WOp.I32Load, 4); e.Get(0); e.Call(_ehTest); e.Tee(c);
            e.If(); e.Get(1); e.GSet(record); e.Get(c); e.GSet(clauseG); e.Op(WOp.Return); e.End();
            e.Get(1); e.I32(8); e.Op(WOp.I32Sub); e.Set(1);
            e.Br(loop);
            e.End(); e.End();
            e.I32(-1); e.GSet(record); e.I32(0); e.GSet(clauseG);
        }
        {
            var e = new Emitter(_firstPass!);
            e.GGet(top); e.Op(WOp.I32Eqz); e.If(); e.I32(-1); e.GSet(record); e.Op(WOp.Return); e.End();
            e.Get(0); e.GGet(top); e.I32(8); e.Op(WOp.I32Sub); e.Call(_firstPassFrom);
        }
        {
            // eh_decide(ex, record) -> the clause this record's try takes (0: none)
            var e = new Emitter(_ehDecide!);
            int r = e.AddLocal(I), c = e.AddLocal(I);
            e.GGet(record); e.Set(r);
            e.Get(r); e.Get(1); e.Op(WOp.I32Ne);
            e.If();
            // (none, nothing found, or a stale decision for a record above this one: decided again from here)
            e.Get(r); e.Op(WOp.I32Eqz); e.Get(r); e.I32(-1); e.Op(WOp.I32Eq); e.Op(WOp.I32Or); e.Get(r); e.Get(1); e.Op(WOp.I32GtU); e.Op(WOp.I32Or);
            e.If(); e.Get(0); e.Get(1); e.Call(_firstPassFrom); e.End();
            e.End();
            e.I32(0); e.Set(c);
            e.GGet(record); e.Get(1); e.Op(WOp.I32Eq);
            e.If(); e.GGet(clauseG); e.Set(c); e.I32(0); e.GSet(record); e.End();
            e.Get(c);
        }
        {
            // eh_test(site, frame base, ex) -> the clause (1-based) the site's try would take, or 0
            var e = new Emitter(_ehTest);
            e.Get(0); e.Op(WOp.I32Eqz); e.If(); e.I32(1); e.Op(WOp.Return); e.End();
            for (int s = 1; s < _ehSites.Count; s++)
            {
                e.Get(0); e.I32(s); e.Op(WOp.I32Eq); e.If();
                var cl = _ehSites[s];
                for (int k = 0; k < cl.Count; k++)
                {
                    var c = cl[k];
                    // (a filter this method keeps in one pass: the site says it may take it, and decides when reached)
                    if (c.OnePassFilter || c.Filter == null && (c.Type == null || c.Type.IsObject)) { e.I32(k + 1); e.Op(WOp.Return); break; }
                    if (c.Filter != null) { e.Get(1); e.Get(2); e.Call(c.Filter); }
                    else { e.Get(2); e.Call(IsInstFn(c.Type!)); }
                    e.If(); e.I32(k + 1); e.Op(WOp.Return); e.End();
                }
                e.I32(0); e.Op(WOp.Return);
                e.End();
            }
            e.I32(0);
        }
        if (_throwHook != null) EmitThrowHookBody();
    }

    void EmitThrowHookBody()
    {
        var e = new Emitter(_throwHook!);
        if (_onThrow != null) { e.Get(0); e.Call(_onThrow); }
        if (_anyFilter) { e.Get(0); e.Call(FirstPass); }
    }
}

sealed partial class FunctionLowering
{
    readonly Dictionary<Region, bool> _twoPass = new();

    /// <summary>A try with catch clauses: its site, and its record pushed; the record's local.</summary>
    int EhEnter(Region tr)
    {
        bool twoPass = tr.Handlers.Any(h => h.Filter != null) && !_body.FiltersOnePass;
        var clauses = new List<(TypeDesc?, WasmFunction?, bool)>();
        foreach (var h in tr.Handlers)
        {
            if (h.Filter != null) clauses.Add(twoPass ? (null, MakeFilterFn(h.Filter), false) : (null, null, true));
            else clauses.Add((h.CatchType is { IsObject: false } ct ? ct : null, null, false));
        }
        int site = C.NewEhSite(clauses);
        int rec = E.AddLocal(ValType.I32);
        E.I32(site); if (_fb >= 0) E.Get(_fb); else E.I32(0); E.Call(C.EhPush); E.Set(rec);
        _twoPass[tr] = twoPass;
        return rec;
    }

    /// <summary>At the catch: the clause the first pass decided (a try with a filter; else -1), the record gone.</summary>
    int EhCaught(Region tr, int rec, int obj)
    {
        int clause = -1;
        if (_twoPass[tr]) { clause = E.AddLocal(ValType.I32); E.Get(obj); E.Get(rec); E.Call(C.EhDecide); E.Set(clause); }
        E.Get(rec); E.Call(C.EhPop);
        return clause;
    }

    void EhLeave(int rec) { E.Get(rec); E.Call(C.EhPop); }

    /// <summary>A filter as a function of its own: (the method's frame base, the exception) -> whether it takes it.</summary>
    WasmFunction MakeFilterFn(Region f)
    {
        var fn = C.Module.AddFunction($"filter {f.Start:x} in {_body.Method}", new FuncType([ValType.I32, ValType.I32], [ValType.I32]));
        new FunctionLowering(C, _body, _own, fn).RunFilter(f);
        return fn;
    }

    void RunFilter(Region f)
    {
        // (the method's frame: what the filter uses is there; its own struct values have their slots there too)
        _fb = 0;
        _handlerLocals[f] = (1, -1);
        int fr = E.AddLocal(ValType.I32);
        _filterResult[f] = fr;
        // (a boundary: a throw inside the filter is decided here)
        int brec = E.AddLocal(ValType.I32);
        E.I32(0); E.I32(0); E.Call(C.EhPush); E.Set(brec);
        var done = E.Block();
        var caught = E.Block(ValType.I32);
        E.TryTable(null, (0, C.ExceptionTag, caught));
        var fe = E.Block();
        _filterEnd[f] = fe;
        EmitLevel(new Level(f, f.Entry), null);
        E.End();
        E.Br(done);
        E.End();
        E.Op(WOp.Unreachable);
        E.End();
        // an exception out of the filter: swallowed, the filter is false; what the filter held let go
        E.Call(C.Rt.Release);
        foreach (var (node, l) in _local) if (node.Op != Opc.Arg && node.Kind == VK.Ref) { E.Get(l); E.Call(C.Rt.Release); }
        E.I32(0); E.Set(fr);
        E.End();
        E.Get(brec); E.Call(C.EhPop);
        E.Get(fr);
    }
}
