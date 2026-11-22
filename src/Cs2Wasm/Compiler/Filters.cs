using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Exception filters, two-pass (section 3): a filter runs before the finally blocks of the frames it would unwind.
//
// In a program with filters, every try/catch pushes a record on a per-thread handler chain (a global) while its body
// runs: [prev, test function (table index), environment]. The test function decides, without unwinding, whether
// that try statement catches an exception: it runs the type tests and filters in order and returns the clause
// number (or 0). Filter variables live in a display, as for lambdas, so the test function can read them.
// `throw` walks the chain first (rt.first_pass) and records the deciding record and clause; then WASM unwinding runs
// the finally blocks, and each catch site takes the exception only if it is the decided one. A decision for a record
// deeper than the site (a frame that no longer exists: a state machine or a filter that swallowed it) is stale: the
// site runs the first pass again from itself.

internal sealed partial class Driver
{
    bool? _usesFilters;
    /// <summary>True when the program has an exception filter: try/catch statements then keep the handler chain.</summary>
    public bool UsesFilters => _usesFilters ??= FindFilters();

    bool FindFilters()
    {
        var found = Inputs.SelectMany(c => c.SyntaxTrees).SelectMany(t => t.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.CatchFilterClauseSyntax>()).FirstOrDefault();
        if (found != null && Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null)
            Console.Error.WriteLine($"filter found: {found.GetType().Name} kind {found.Kind()} '{found}' in {found.SyntaxTree.FilePath} (trees {Inputs.Sum(c => c.SyntaxTrees.Count())})");
        return found != null;
    }
    WasmGlobal? _ehChain, _ehRecord, _ehClause;
    public WasmGlobal EhChain => _ehChain ??= AddGlobal("__eh_chain");
    public WasmGlobal EhRecord => _ehRecord ??= AddGlobal("__eh_record");
    public WasmGlobal EhClause => _ehClause ??= AddGlobal("__eh_clause");
    // Exception.TargetSite of a throw in a method traces hide ([StackTraceHidden]: ThrowHelper's): such a throw notes
    // its method (its place in HiddenThrowers, + 1) here for Rt.OnThrow; none of it is left unless frames are asked
    // for their methods (RuntimeGen.Finish)
    WasmGlobal? _throwSite;
    public WasmGlobal ThrowSite => _throwSite ??= AddGlobal("__throw_site");
    public WasmGlobal? ThrowSiteIfMade => _throwSite;
    public readonly List<IMethodSymbol> HiddenThrowers = [];
    readonly Dictionary<string, int> _hiddenThrowerIndex = [];
    public int HiddenThrowerIndex(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        var key = Types.MethodKey(def);
        if (!_hiddenThrowerIndex.TryGetValue(key, out int k)) { HiddenThrowers.Add(def); _hiddenThrowerIndex[key] = k = HiddenThrowers.Count; }
        return k;
    }
    /// <summary>A method whose throws note it for TargetSite: one traces leave out that is the program's or the
    /// library's own (not the runtime's, whose throws stand for the code that called it).</summary>
    public static bool IsHiddenThrower(IMethodSymbol m) => IsHiddenFrame(m) && m.MethodKind != MethodKind.DelegateInvoke
        && !(m.ContainingNamespace?.ToDisplayString() ?? "").StartsWith("Cs2Wasm.Runtime", StringComparison.Ordinal);
    WasmGlobal AddGlobal(string name) { var g = new WasmGlobal(name, ValType.I32, true, 0); Module.Globals.Add(g); return g; }
    public static readonly FuncType FilterTestType = new([ValType.I32, ValType.I32], [ValType.I32]);
}

internal sealed partial class RuntimeFns
{
    WasmFunction? _firstPass, _ehBoundary;
    /// <summary>first_pass(ex): walks the handler chain; sets __eh_record (-1: none) and __eh_clause.</summary>
    public WasmFunction FirstPass => _firstPass ??= MakeFirstPass();
    /// <summary>A record whose test catches everything (a filter's own frame: what escapes a filter is swallowed).</summary>
    public WasmFunction EhBoundary => _ehBoundary ??= MakeBoundary();

    WasmFunction MakeBoundary()
    {
        var f = M.AddFunction("rt.eh_boundary", Driver.FilterTestType);
        var e = new Emitter(f);
        e.I32(1);
        return f;
    }

    WasmFunction MakeFirstPass()
    {
        var f = M.AddFunction("rt.first_pass", FT([I], []));
        var e = new Emitter(f);
        int rec = e.AddLocal(I), r = e.AddLocal(I);
        e.GGet(D.EhChain); e.Set(rec);
        var brk = e.Block(); var top = e.Loop();
        e.Get(rec); e.Op(Op.I32Eqz); e.BrIf(brk);
        e.Get(rec); e.Load(Op.I32Load, 8); e.Get(0); e.Get(rec); e.Load(Op.I32Load, 4);
        e.Emit(Op.CallIndirect, 0, D.IndirectBucket(Driver.FilterBucket), Driver.FilterTestType);
        e.Tee(r);
        e.If(); e.Get(rec); e.GSet(D.EhRecord); e.Get(r); e.GSet(D.EhClause); e.Op(Op.Return); e.End();
        e.Get(rec); e.Load(Op.I32Load); e.Set(rec);
        e.Br(top);
        e.End(); e.End();
        e.I32(-1); e.GSet(D.EhRecord);
        return f;
    }
}

sealed partial class FunctionCompiler
{
    int AllocFrameBytes(int size, int align)
    {
        if (_frameSize == 0) _frameSize = 8; // (no variable at the frame base: FrameSlots.cs)
        _frameSize = (_frameSize + align - 1) / align * align;
        int off = _frameSize;
        _frameSize += size;
        _usesFrame = true;
        (E.Fn.FrameSlots ??= []).Add((off, size));
        return off;
    }

    /// <summary>Pushes a handler record for this try/catch; returns (record offset, saved chain local).</summary>
    (int rec, int saved) PushHandlerRecord(ITryOperation tr)
    {
        int rec = AllocFrameBytes(12, 4);
        int saved = E.AddLocal(ValType.I32);
        var test = FilterTest(tr);
        E.GGet(D.EhChain); E.Set(saved);
        E.Get(_fb); E.Get(saved); E.Store(Op.I32Store, rec);
        E.Get(_fb); E.I32Fn(D.Module, test); E.Store(Op.I32Store, rec + 4);
        D.NoteIndirectTarget(Driver.FilterBucket, test);
        E.Get(_fb); PushDisplayPtr(CurrentEnv(), retain: false); E.Store(Op.I32Store, rec + 8);
        E.Get(_fb); E.I32(rec); E.Op(Op.I32Add); E.GSet(D.EhChain);
        return (rec, saved);
    }

    /// <summary>At the catch site: the clause (1-based) that takes the exception, or 0 to rethrow. Pops the record.</summary>
    void EmitHandlerDecision(int rec, int saved, int ex, int clause)
    {
        int mine = E.AddLocal(ValType.I32), r = E.AddLocal(ValType.I32);
        E.Get(_fb); E.I32(rec); E.Op(Op.I32Add); E.Set(mine);
        E.I32(0); E.Set(clause);
        E.GGet(D.EhRecord); E.Set(r);
        E.Get(r); E.Get(mine); E.Op(Op.I32Eq);
        E.If();
        E.GGet(D.EhClause); E.Set(clause);
        E.Else();
        // none recorded, nothing found, or a stale (deeper) decision: decide again from here
        E.Get(r); E.Op(Op.I32Eqz); E.Get(r); E.I32(-1); E.Op(Op.I32Eq); E.Op(Op.I32Or); E.Get(r); E.Get(mine); E.Op(Op.I32LtU); E.Op(Op.I32Or);
        E.If();
        E.Get(mine); E.GSet(D.EhChain);
        E.Get(ex); E.Call(Rt.FirstPass);
        E.GGet(D.EhRecord); E.Get(mine); E.Op(Op.I32Eq);
        E.If(); E.GGet(D.EhClause); E.Set(clause); E.End();
        E.End();
        E.End();
        E.Get(saved); E.GSet(D.EhChain);
        E.Get(clause); E.If(); E.I32(0); E.GSet(D.EhRecord); E.End();
    }

    WasmFunction FilterTest(ITryOperation tr)
    {
        var fn = D.Module.AddFunction("catch test in " + FnName, Driver.FilterTestType);
        var env = CurrentEnv();
        var ci = MakeClosureInfo(Map, env, null);
        ci.OuterKey = OuterKey ?? InstanceKey;
        var fc = new FunctionCompiler(D, fn, M, Inst, ci);
        fc.CompileFilterTest(tr);
        return fn;
    }

    void CompileFilterTest(ITryOperation tr)
    {
        _closure!.ParamLocal = 0;
        const int ex = 1;
        foreach (var p in M.Parameters) _vars.Remove(p);
        E.CurrentPos = D.Position(tr.Syntax);
        using (BodyScope())
        {
            int res = E.AddLocal(ValType.I32);
            _retLocal = res;
            // this frame swallows what escapes a filter: a boundary record, and a catch around the tests
            int brec = AllocFrameBytes(12, 4);
            int saved = E.AddLocal(ValType.I32);
            E.GGet(D.EhChain); E.Set(saved);
            E.Get(_fb); E.Get(saved); E.Store(Op.I32Store, brec);
            E.Get(_fb); E.I32Fn(D.Module, Rt.EhBoundary); E.Store(Op.I32Store, brec + 4);
            D.NoteIndirectTarget(Driver.FilterBucket, Rt.EhBoundary);
            E.Get(_fb); E.I32(0); E.Store(Op.I32Store, brec + 8);
            E.Get(_fb); E.I32(brec); E.Op(Op.I32Add); E.GSet(D.EhChain);
            var done = E.Block();
            var caught = E.Block(ValType.I32);
            E.TryTable(null, (0, D.ExceptionTag, caught));
            for (int i = 0; i < tr.Catches.Length; i++)
            {
                var cc = tr.Catches[i];
                var et = cc.ExceptionType is { } t0 ? T.Resolve(t0, Map) : null;
                bool catchAll = et == null || Types.FullName(et) == "System.Exception" || Types.IsObject(et);
                if (!catchAll) { E.Get(ex); E.Call(Rt.IsInst(et!)); } else E.I32(1);
                E.If();
                if (cc.Filter != null)
                {
                    PushScope();
                    if (cc.ExceptionDeclarationOrExpression is IVariableDeclaratorOperation vd && IsCaptured(vd.Symbol))
                        EmitStoreToVarFrom(vd.Symbol, D.ObjectType, () => { E.Get(ex); return Own.Stable; });
                    BeginTemps(); EmitCondition(cc.Filter); EndTemps(ValType.I32);
                    PopScope();
                    E.If(); E.I32(i + 1); E.Set(res); E.Br(done); E.End();
                }
                else { E.I32(i + 1); E.Set(res); E.Br(done); }
                E.End();
            }
            E.I32(0); E.Set(res);
            E.Br(done);
            E.End(); // try_table
            E.Op(Op.Unreachable);
            E.End(); // caught: the exception a filter threw is dropped
            E.Get(_fb); E.GSet(D.StackPointer);
            E.Call(Rt.Release);
            E.I32(0); E.GSet(D.EhRecord);
            E.I32(0); E.Set(res);
            E.End(); // done
            E.Get(saved); E.GSet(D.EhChain);
            EmitReturnSequence(true);
        }
        Finish();
    }
}
