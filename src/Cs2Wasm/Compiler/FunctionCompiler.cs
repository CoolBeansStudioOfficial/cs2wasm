using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>How a reference value on the operand stack is held (section 4.1).</summary>
internal enum Own
{
    /// <summary>Not counted: numbers, pointers, struct addresses, void.</summary>
    None,
    /// <summary>The stack holds one count that the consumer must take or release.</summary>
    Owned,
    /// <summary>Borrowed from something that cannot change during the current call (a local, a parameter, this, a literal).</summary>
    Stable,
    /// <summary>Borrowed from memory that code run later in the expression may overwrite (a field, an element).</summary>
    Unstable,
}

/// <summary>A variable: either a WASM local holding its value, or memory at (address local + offset).</summary>
sealed class Var
{
    public required ITypeSymbol Type;
    public int Local = -1;      // value lives in this wasm local
    public int AddrLocal = -1;  // value lives in memory at local[AddrLocal] + Offset
    public int Offset;
    public bool InMemory => AddrLocal >= 0;
}

sealed class Slot
{
    public int Local = -1;      // owned reference in a wasm local
    public int FrameOffset = -1; // owned struct contents in the frame
    public ITypeSymbol Type = null!;
}

sealed class Scope { public List<Slot> Owned = []; }

enum CtlKind { Loop, Switch, TryFinally, Catch, Block }

sealed class Ctl
{
    public CtlKind Kind;
    public Label? Break, Continue;
    public int ScopeDepth, TempDepth;
    public Action? FinallyAction;
    public int GotoState = -1; // goto: the dispatch loop's state local (Continue is the loop)
    public int ExnLocal = -1, ExPtrLocal = -1; // inside a catch: the caught exception
}

sealed partial class FunctionCompiler
{
    readonly Driver D;
    readonly Types T;
    readonly Emitter E;
    readonly MethodInst? Inst;
    readonly IMethodSymbol M;
    readonly TypeMap Map;
    readonly Dictionary<ISymbol, Var> _vars = new(SymbolEqualityComparer.Default);
    readonly HashSet<ISymbol> _addrTaken = new(SymbolEqualityComparer.Default);
    readonly HashSet<ISymbol> _assignedParams = new(SymbolEqualityComparer.Default);
    readonly List<Scope> _scopes = [];
    readonly List<List<Slot>> _temps = [];
    readonly List<Slot> _allSlots = [];
    readonly List<HashSet<Slot>> _regions = [];
    readonly List<Ctl> _ctl = [];
    readonly Dictionary<ILabelSymbol, (Ctl ctl, bool isContinue)> _labels = new(SymbolEqualityComparer.Default);
    /// <summary>goto targets: segment index in their dispatch loop, and (once emitted) the owned-slot count of the
    /// label's block scope at the label, so a backward goto releases what was declared after it.</summary>
    readonly Dictionary<ILabelSymbol, int> _gotoIndex = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ILabelSymbol, int> _gotoOwned = new(SymbolEqualityComparer.Default);
    readonly Stack<int> _refPool = new();
    readonly Dictionary<(int, int), Stack<int>> _structPool = [];
    readonly Dictionary<ValType, Stack<int>> _scratch = [];
    readonly List<(int local, ITypeSymbol type)> _implicitReceivers = [];
    int _frameSize;
    readonly int _spEntry, _fb;
    int _thisLocal = -1;
    int _structRetLocal = -1;
    int _retLocal = -1;
    bool _usesFrame;
    static readonly object FrameMarker = new();
    readonly ClosureInfo? _closure;

    FunctionCompiler(Driver d, WasmFunction fn, IMethodSymbol m, MethodInst? inst, ClosureInfo? closure = null)
    {
        D = d; T = d.Types; E = new Emitter(fn); M = m; Inst = inst; Map = TypeMap.For(m);
        _closure = closure;
        if (closure != null) Map = closure.Map;
        _clone = inst?.Clone ?? closure?.Clone;
        _spEntry = E.AddLocal(ValType.I32);
        _fb = E.AddLocal(ValType.I32);
    }

    // =====================================================================
    // entry points
    // =====================================================================
    public static void Compile(Driver d, MethodInst mi)
    {
        var fc = new FunctionCompiler(d, mi.Fn, mi.Sym, mi);
        fc.CompileMethod();
    }

    void CompileMethod()
    {
        var def = M.OriginalDefinition;
        switch (Inst!.Impl)
        {
            case MethodImpl.Missing:
                ReportMissing(M, Inst.FirstUse);
                E.Op(Op.Unreachable);
                return;
            case MethodImpl.Synthesized:
                SetupParams();
                using (BodyScope()) Synthesize();
                Finish();
                return;
        }
        if (IsStateMachine(M)) { CompileKickoff(); return; }
        SetupParams();
        var (body, syntax) = GetBody(def);
        E.CurrentPos = D.Position(syntax);
        PreScan(body);
        using (BodyScope())
        {
            EnterDisplay(s_fnKey);
            GuardStaticInitOnEntry();
            if (body is IConstructorBodyOperation ctor) EmitConstructorBody(ctor);
            else if (body is IMethodBodyOperation mb) EmitStatement((IOperation?)mb.BlockBody ?? mb.ExpressionBody!);
            else EmitStatement(body);
        }
        Finish();
    }

    IDisposable BodyScope()
    {
        PushScope();
        return new Disposer(() => { });
    }

    sealed class Disposer(Action a) : IDisposable { public void Dispose() => a(); }

    (IOperation body, SyntaxNode syntax) GetBody(IMethodSymbol def)
    {
        foreach (var r in def.DeclaringSyntaxReferences)
        {
            var node = r.GetSyntax();
            var model = D.ModelFor(node.SyntaxTree);
            SyntaxNode? target = node switch
            {
                BaseMethodDeclarationSyntax b when b.Body != null || b.ExpressionBody != null => b,
                AccessorDeclarationSyntax a when a.Body != null || a.ExpressionBody != null => a,
                ArrowExpressionClauseSyntax ar => ar,
                LocalFunctionStatementSyntax lf => lf,
                AnonymousFunctionExpressionSyntax af => af,
                _ => null,
            };
            if (target == null) continue;
            var op = model.GetOperation(target);
            switch (op)
            {
                case ILocalFunctionOperation lfo: return (lfo.Body ?? lfo.IgnoredBody!, target);
                case IAnonymousFunctionOperation afo: return (afo.Body, target);
                case null: break;
                default: return (op, target);
            }
        }
        throw new CompileException("no body for " + def.ToDisplayString(), def.Locations.FirstOrDefault());
    }

    void Finish()
    {
        // fall off the end: void methods return
        if (E.Fn.Type.Results.Length == 0) EmitReturnSequence(false);
        else E.Op(Op.Unreachable);

        bool needHandler = _allSlots.Count > 0 || _usesFrame;
        var body = E.Fn.Body;
        var prologue = new List<Instr>();
        void P(Op op, long imm = 0, object? r = null) => prologue.Add(new Instr { Op = op, Imm = imm, Ref = r, Pos = -1 });
        if (_usesFrame)
        {
            _frameSize = (_frameSize + 15) / 16 * 16;
            P(Op.GlobalGet, 0, D.StackPointer); P(Op.LocalTee, _spEntry);
            P(Op.I32Const, _frameSize); P(Op.I32Sub); P(Op.LocalTee, _fb); P(Op.GlobalSet, 0, D.StackPointer);
            P(Op.LocalGet, _fb); P(Op.I32Const, 0); P(Op.I32Const, _frameSize); P(Op.MemoryFill);
        }
        else
        {
            // no frame: drop the epilogue instructions that restore the stack pointer
            for (int i = 0; i + 1 < body.Count; i++)
                if (body[i].Op == Op.LocalGet && ReferenceEquals(body[i].Ref, FrameMarker))
                {
                    body[i] = body[i] with { Op = Op.Nop, Ref = null };
                    body[i + 1] = body[i + 1] with { Op = Op.Nop, Ref = null };
                }
        }
        // params retained on entry (assigned reference parameters own their value)
        prologue.AddRange(_entryCode);
        if (needHandler)
        {
            var handler = new List<Instr>();
            void H(Op op, long imm = 0, object? r = null) => handler.Add(new Instr { Op = op, Imm = imm, Ref = r, Pos = -1 });
            int exn = E.AddLocal(ValType.ExnRef);
            // block (result exnref) ; try_table (catch_all_ref 0) ; body ; end ; unreachable ; end
            prologue.Add(new Instr { Op = Op.Block, Ref = ValType.ExnRef, Pos = -1 });
            var tti = new TryTableInfo { BlockType = null }; tti.Catches.Add(new CatchClause { Kind = 3, Label = 0 });
            prologue.Add(new Instr { Op = Op.TryTable, Ref = tti, Pos = -1 });
            H(Op.End); H(Op.Unreachable); H(Op.End);
            H(Op.LocalSet, exn);
            var save = E.Fn.Body; E.Fn.Body = handler;
            foreach (var s in _allSlots) ReleaseSlot(s);
            if (_usesFrame) { E.Get(_spEntry); E.GSet(D.StackPointer); }
            E.Fn.Body = save;
            H(Op.LocalGet, exn); H(Op.ThrowRef);
            body.InsertRange(0, prologue);
            body.AddRange(handler);
        }
        else body.InsertRange(0, prologue);
        for (int i = 0; i < body.Count; i++) if (ReferenceEquals(body[i].Ref, FrameMarker)) body[i] = body[i] with { Ref = null };
        E.Fn.Body.RemoveAll(i => i.Op == Op.Nop);
    }

    readonly List<Instr> _entryCode = [];

    // =====================================================================
    // parameters, locals, frame
    // =====================================================================
    bool StructRet => !M.ReturnsByRef && !M.ReturnsByRefReadonly && Types.IsStruct(T.Resolve(M.ReturnType, Map));

    void SetupParams()
    {
        int idx = 0;
        if (StructRet) _structRetLocal = idx++;
        if (_closure != null) { _closure.ParamLocal = idx++; }
        else if (!M.IsStatic) _thisLocal = idx++;
        foreach (var p in M.Parameters)
        {
            var pt = T.Resolve(p.Type, Map);
            var v = new Var { Type = pt };
            if (p.RefKind != RefKind.None || Types.IsStruct(pt)) { v.AddrLocal = idx; v.Offset = 0; }
            else v.Local = idx;
            _vars[p] = v;
            _vars[p.OriginalDefinition] = v; // the body's operations refer to the definition's parameters
            idx++;
        }
    }

    /// <summary>Find variables whose address is taken and parameters that are assigned.</summary>
    void PreScan(IOperation body)
    {
        foreach (var op in body.DescendantsAndSelf())
        {
            switch (op)
            {
                case IArgumentOperation { Parameter.RefKind: not RefKind.None } a: MarkAddr(a.Value); break;
                case IAddressOfOperation ao: MarkAddr(ao.Reference); break;
                case IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None, Initializer: { } init }: MarkAddr(init.Value); break;
                case ISimpleAssignmentOperation { IsRef: true } sa: MarkAddr(sa.Value); break;
                case IReturnOperation { ReturnedValue: { } rv } when M.ReturnsByRef || M.ReturnsByRefReadonly: MarkAddr(rv); break;
            }
            if (op is IAssignmentOperation asg && asg.Target is IParameterReferenceOperation pr) _assignedParams.Add(pr.Parameter);
            if (op is IIncrementOrDecrementOperation inc && inc.Target is IParameterReferenceOperation pr2) _assignedParams.Add(pr2.Parameter);
            if (op is IDeconstructionAssignmentOperation da)
                foreach (var t in da.Target.DescendantsAndSelf().OfType<IParameterReferenceOperation>()) _assignedParams.Add(t.Parameter);
        }
        AnalyzeCaptures(body);
        foreach (var p in M.Parameters)
        {
            var v = _vars[p];
            var pt = v.Type;
            if (p.RefKind == RefKind.None && !v.InMemory && _addrTaken.Contains(p))
            {
                // move the parameter into the frame
                int off = AllocFrame(pt);
                var save = E.Fn.Body; E.Fn.Body = _entryCode;
                E.Get(_fb); E.Get(v.Local); E.Store(Types.StoreOp(pt), off);
                E.Fn.Body = save;
                v.Local = -1; v.AddrLocal = _fb; v.Offset = off;
            }
            if (p.RefKind == RefKind.None && Types.IsRef(pt) && _assignedParams.Contains(p))
            {
                var save = E.Fn.Body; E.Fn.Body = _entryCode;
                if (v.InMemory) { E.Get(v.AddrLocal); E.Load(Op.I32Load, v.Offset); E.Call(Rt.Retain); E.Op(Op.Drop); }
                else { E.Get(v.Local); E.Call(Rt.Retain); E.Op(Op.Drop); }
                E.Fn.Body = save;
                var slot = v.InMemory ? new Slot { FrameOffset = v.Offset, Type = pt } : new Slot { Local = v.Local, Type = pt };
                _paramSlots.Add(slot);
                _allSlots.Add(slot);
            }
        }
    }

    readonly List<Slot> _paramSlots = [];

    void MarkAddr(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        if (op is IDeclarationExpressionOperation de) op = de.Expression;
        switch (op)
        {
            case ILocalReferenceOperation l: _addrTaken.Add(l.Local); break;
            case IParameterReferenceOperation p: _addrTaken.Add(p.Parameter); break;
        }
    }

    int AllocFrame(ITypeSymbol t)
    {
        var (size, align) = T.StorageOf(t);
        align = Math.Max(align, 1);
        _frameSize = (_frameSize + align - 1) / align * align;
        int off = _frameSize;
        _frameSize += size;
        _usesFrame = true;
        return off;
    }

    RuntimeFns Rt => D.Rt;
    public string FnName => E.Fn.Name;
    public string? OuterKey => _closure?.OuterKey;
    /// <summary>The method a lambda or local function is lexically inside (for frame names).</summary>
    public IMethodSymbol OuterMethod => _closure?.OuterMethod ?? M;

    Var LocalVar(ILocalSymbol l)
    {
        if (_vars.TryGetValue(l, out var v)) return v;
        var t = T.Resolve(l.Type, Map);
        if (l.RefKind != RefKind.None) { v = new Var { Type = t, Local = -1, AddrLocal = E.AddLocal(ValType.I32), Offset = 0 }; v.Local = -2; }
        else if (Types.IsStruct(t) || _addrTaken.Contains(l)) v = new Var { Type = t, AddrLocal = _fb, Offset = AllocFrame(t) };
        else v = new Var { Type = t, Local = E.AddLocal(Types.WasmType(t) ?? ValType.I32) };
        _vars[l] = v;
        if (l.RefKind == RefKind.None && T.NeedsRc(t))
        {
            var slot = v.InMemory ? new Slot { FrameOffset = v.Offset, Type = t } : new Slot { Local = v.Local, Type = t };
            AddOwned(slot);
        }
        return v;
    }

    bool IsRefLocal(Var v) => v.Local == -2;

    void AddOwned(Slot s)
    {
        _scopes[^1].Owned.Add(s);
        _allSlots.Add(s);
        foreach (var r in _regions) r.Add(s);
    }

    void PushScope() => _scopes.Add(new Scope());

    void PopScope()
    {
        var s = _scopes[^1];
        for (int i = s.Owned.Count - 1; i >= 0; i--) ReleaseSlot(s.Owned[i]);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    void ReleaseSlot(Slot s)
    {
        if (s.Local >= 0)
        {
            E.Get(s.Local); E.Call(Rt.Release);
            E.I32(0); E.Set(s.Local);
        }
        else
        {
            if (Types.IsRef(s.Type))
            {
                E.Get(_fb); E.Load(Op.I32Load, s.FrameOffset); E.Call(Rt.Release);
                E.Get(_fb); E.I32(0); E.Store(Op.I32Store, s.FrameOffset);
            }
            else
            {
                E.Get(_fb); if (s.FrameOffset != 0) { E.I32(s.FrameOffset); E.Op(Op.I32Add); }
                E.Call(D.Rt.ReleaseStruct(s.Type));
                var size = T.StorageOf(s.Type).size;
                E.Get(_fb); if (s.FrameOffset != 0) { E.I32(s.FrameOffset); E.Op(Op.I32Add); }
                E.I32(0); E.I32(size); E.Op(Op.MemoryFill);
            }
        }
    }

    // ---- temporaries ----
    void BeginTemps() => _temps.Add([]);

    void EndTemps(ValType? keep = null)
    {
        var list = _temps[^1];
        _temps.RemoveAt(_temps.Count - 1);
        if (list.Count == 0) return;
        int k = -1;
        if (keep != null) { k = Scratch(keep.Value); E.Set(k); }
        foreach (var s in list)
        {
            ReleaseSlot(s);
            if (s.Local >= 0) _refPool.Push(s.Local);
            else
            {
                var key = T.StorageOf(s.Type);
                if (!_structPool.TryGetValue(key, out var st)) _structPool[key] = st = new();
                st.Push(s.FrameOffset);
            }
        }
        if (keep != null) { E.Get(k); FreeScratch(keep.Value, k); }
    }

    /// <summary>A reference local owned until the end of the current statement.</summary>
    int TempRef(ITypeSymbol t)
    {
        int l = _refPool.Count > 0 ? _refPool.Pop() : E.AddLocal(ValType.I32);
        var s = new Slot { Local = l, Type = t };
        RegisterTemp(s);
        return l;
    }

    /// <summary>A frame slot for a struct temporary, released at the end of the current statement.</summary>
    int TempStruct(ITypeSymbol t)
    {
        var key = T.StorageOf(t);
        int off = _structPool.TryGetValue(key, out var st) && st.Count > 0 ? st.Pop() : AllocFrame(t);
        _usesFrame = true;
        if (T.NeedsRc(t)) RegisterTemp(new Slot { FrameOffset = off, Type = t });
        return off;
    }

    void RegisterTemp(Slot s)
    {
        if (_temps.Count == 0) BeginTemps(); // defensive: function-level temps
        _temps[^1].Add(s);
        _allSlots.Add(s);
        foreach (var r in _regions) r.Add(s);
    }

    int Scratch(ValType t)
    {
        if (_scratch.TryGetValue(t, out var st) && st.Count > 0) return st.Pop();
        return E.AddLocal(t);
    }

    void FreeScratch(ValType t, int l)
    {
        if (!_scratch.TryGetValue(t, out var st)) _scratch[t] = st = new();
        st.Push(l);
    }

    // =====================================================================
    // ownership conversions
    // =====================================================================
    /// <summary>Value on stack becomes owned (+1).</summary>
    void ToOwned(Own o)
    {
        if (o is Own.Stable or Own.Unstable) E.Call(Rt.Retain);
    }

    /// <summary>Value on stack stays valid for the rest of the statement.</summary>
    Own ToTransient(Own o, ITypeSymbol t)
    {
        if (o == Own.Owned) { E.Tee(TempRef(t)); return Own.Unstable; }
        return o;
    }

    /// <summary>Value on stack stays valid across a call that may overwrite the memory it was read from.</summary>
    Own ToCallSafe(Own o, ITypeSymbol t)
    {
        if (o == Own.Owned) { E.Tee(TempRef(t)); return Own.Stable; }
        if (o == Own.Unstable) { E.Call(Rt.Retain); E.Tee(TempRef(t)); return Own.Stable; }
        return o;
    }

    void Drop(Own o, ITypeSymbol? t)
    {
        if (t != null && Types.IsVoid(t)) return;
        if (o == Own.Owned) E.Call(Rt.Release);
        else E.Op(Op.Drop);
    }

    // =====================================================================
    // statements
    // =====================================================================
    void EmitStatement(IOperation op)
    {
        int save = E.CurrentPos;
        if (op.Syntax is not BlockSyntax) E.CurrentPos = D.Position(op.Syntax) is var p && p >= 0 ? p : save;
        try { EmitStatementHooks(op); EmitStatementCore(op); }
        catch (CompileException ex) when (ex.Location == null) { throw new CompileException(ex.Message, op.Syntax.GetLocation()); }
        E.CurrentPos = save;
    }

    void EmitStatementCore(IOperation op)
    {
        switch (op)
        {
            case IBlockOperation b:
                PushScope();
                EmitStatements(b.Operations, 0);
                PopScope();
                break;
            case IExpressionStatementOperation es:
                BeginTemps();
                EmitDiscard(es.Operation);
                EndTemps();
                break;
            case IVariableDeclarationGroupOperation g:
                foreach (var decl in g.Declarations) EmitDeclaration(decl);
                break;
            case IReturnOperation r:
                if (r.Kind != OperationKind.Return) throw new CompileException("iterators (yield) are not supported yet", op.Syntax.GetLocation());
                EmitReturn(r.ReturnedValue);
                break;
            case IConditionalOperation c when Fold(c.Condition) is bool known:
                // only the side taken is compiled (Folding)
                if (known) { PushScope(); EmitStatement(c.WhenTrue); PopScope(); }
                else if (c.WhenFalse != null) { PushScope(); EmitStatement(c.WhenFalse); PopScope(); }
                break;
            case IConditionalOperation c:
                BeginTemps(); EmitCondition(c.Condition); EndTemps(ValType.I32);
                E.If();
                PushScope(); EmitStatement(c.WhenTrue); PopScope();
                if (c.WhenFalse != null) { E.Else(); PushScope(); EmitStatement(c.WhenFalse); PopScope(); }
                E.End();
                break;
            case IWhileLoopOperation w: EmitWhile(w); break;
            case IForLoopOperation f: EmitFor(f); break;
            case IForEachLoopOperation fe: EmitForEach(fe); break;
            case IBranchOperation br: EmitBranch(br); break;
            case ISwitchOperation sw: EmitSwitch(sw); break;
            case ITryOperation tr: EmitTry(tr); break;
            case IThrowOperation th: BeginTemps(); EmitThrow(th.Exception); EndTemps(); break;
            case IUsingOperation u: EmitUsing(u.Resources, u.Body, u.Locals); break;
            case ILockOperation lk:
                {
                    // Monitor.Enter(o); try { body } finally { Monitor.Exit(o); } with o held for the statement
                    PushScope();
                    BeginTemps();
                    var o = EmitValue(lk.LockedValue);
                    int l = E.AddLocal(ValType.I32);
                    ToOwned(o); E.Set(l); AddOwned(new Slot { Local = l, Type = D.ObjectType });
                    EndTemps();
                    E.Get(l); E.Call(D.LibMethod("System.Threading.Monitor", "Enter", 1).Fn);
                    EmitTryFinallyCore(() => { PushScope(); EmitStatement(lk.Body); PopScope(); },
                        () => { E.Get(l); E.Call(D.LibMethod("System.Threading.Monitor", "Exit", 1).Fn); });
                    PopScope();
                    break;
                }
            case ILabeledOperation lb:
                if (lb.Operation != null) EmitStatement(lb.Operation);
                break;
            case IEmptyOperation: break;
            case ILocalFunctionOperation: break; // compiled when referenced
            case IUsingDeclarationOperation: throw new CompileException("using declaration outside a block");
            case { Kind: OperationKind.None, Syntax: FixedStatementSyntax fs }:
                EmitFixed(fs);
                break;
            default:
                if (op.Kind == OperationKind.None || op is IInvalidOperation) throw new CompileException($"unsupported statement '{op.Syntax}'", op.Syntax.GetLocation());
                BeginTemps(); EmitDiscard(op); EndTemps();
                break;
        }
    }

    void EmitStatements(IReadOnlyList<IOperation> ops, int start)
    {
        var labeled = new List<int>();
        for (int i = start; i < ops.Count; i++)
        {
            if (ops[i] is IUsingDeclarationOperation) break;
            if (ops[i] is ILabeledOperation) labeled.Add(i);
        }
        if (labeled.Count > 0) { EmitLabeledStatements(ops, start, labeled); return; }
        EmitStatementsPlain(ops, start, ops.Count);
    }

    /// <summary>A block with labels: segments between labels inside a dispatch loop; goto L sets the state to L's
    /// segment and branches to the loop, whose br_table enters that segment. Segments fall through in order.</summary>
    void EmitLabeledStatements(IReadOnlyList<IOperation> ops, int start, List<int> labeled)
    {
        int n = labeled.Count + 1;
        int st = E.AddLocal(ValType.I32);
        E.I32(0); E.Set(st);
        var exit = E.Block();
        var disp = E.Loop();
        var c = new Ctl { Kind = CtlKind.Block, Break = exit, Continue = disp, GotoState = st, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
        _ctl.Add(c);
        for (int k = 0; k < labeled.Count; k++)
        {
            var lbl = ((ILabeledOperation)ops[labeled[k]]).Label;
            _labels[lbl] = (c, true);
            _gotoIndex[lbl] = k + 1;
        }
        var segs = new Label[n];
        for (int k = n - 1; k >= 0; k--) segs[k] = E.Block();
        E.Get(st); E.BrTable(segs, segs[0]);
        for (int k = 0; k < n; k++)
        {
            E.End();
            int from = k == 0 ? start : labeled[k - 1];
            int to = k < labeled.Count ? labeled[k] : ops.Count;
            if (k > 0) _gotoOwned[((ILabeledOperation)ops[from]).Label] = _scopes[^1].Owned.Count;
            EmitStatementsPlain(ops, from, to);
        }
        _ctl.Remove(c);
        E.End(); E.End();
    }

    void EmitStatementsPlain(IReadOnlyList<IOperation> ops, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (ops[i] is IUsingDeclarationOperation ud)
            {
                // the rest of the block is the body of the using
                var rest = ops.Skip(i + 1).Take(end - i - 1).ToList();
                EmitUsingCore(ud.DeclarationGroup, () => EmitStatements(rest, 0));
                return;
            }
            if (i == start && ops[i] is ILabeledOperation lo) { if (lo.Operation != null) EmitStatement(lo.Operation); continue; }
            EmitStatement(ops[i]);
        }
    }

    /// <summary>fixed: nothing moves without a collector, so pinning is holding the object for the statement and
    /// taking the address of its data (arrays and strings: null or empty gives a null pointer, as in C#).</summary>
    void EmitFixed(FixedStatementSyntax fs)
    {
        var model = D.ModelFor(fs.SyntaxTree);
        PushScope();
        foreach (var v in fs.Declaration.Variables)
        {
            var sym = (ILocalSymbol)model.GetDeclaredSymbol(v)!;
            var initExpr = v.Initializer!.Value;
            var init = model.GetOperation(initExpr)!;
            while (init is IConversionOperation { IsImplicit: true } cv) init = cv.Operand;
            var it = TypeOf(init);
            int ptr = E.AddLocal(ValType.I32);
            if (it is IArrayTypeSymbol at || Types.IsString(it))
            {
                BeginTemps();
                var o = EmitValue(init);
                int h = E.AddLocal(ValType.I32);
                ToOwned(o); E.Set(h); AddOwned(new Slot { Local = h, Type = it });
                EndTemps();
                int data = it is IArrayTypeSymbol a2 ? Types.ArrayDataOffset(a2.Rank) : 16;
                E.Get(h); E.Op(Op.I32Eqz);
                E.If(ValType.I32); E.I32(0);
                E.Else();
                E.Get(h); E.Load(Op.I32Load, 12); E.Op(Op.I32Eqz);
                E.If(ValType.I32); E.I32(0); E.Else(); E.Get(h); E.I32(data); E.Op(Op.I32Add); E.End();
                E.End();
                E.Set(ptr);
            }
            else if (it is IPointerTypeSymbol)
            {
                BeginTemps(); EmitValue(init); EndTemps(ValType.I32); E.Set(ptr);
            }
            else
            {
                // the GetPinnableReference pattern (spans and user types): the reference it returns
                var gpr = it.GetMembers("GetPinnableReference").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0)
                    ?? throw new CompileException("fixed on " + it + " is not supported", initExpr.GetLocation());
                BeginTemps();
                var m = SubstMethod(gpr);
                if (Types.IsStruct(T.Canonical(it)))
                {
                    int tmp = TempStruct(T.Canonical(it));
                    EmitValue(init);
                    // copy the struct value into the temp, then call on its address
                    int src = E.AddLocal(ValType.I32);
                    E.Set(src);
                    E.Get(_fb); E.I32(tmp); E.Op(Op.I32Add); E.Get(src); CopyStructInit(T.Canonical(it));
                    _wantRefReturnAddress = true;
                    CallWith(m, false, () => { E.Get(_fb); E.I32(tmp); E.Op(Op.I32Add); }, () => { }, null, initExpr);
                    _wantRefReturnAddress = false;
                }
                else
                {
                    var oo = EmitValue(init);
                    int h = E.AddLocal(ValType.I32);
                    ToOwned(oo); E.Set(h); AddOwned(new Slot { Local = h, Type = it });
                    _wantRefReturnAddress = true;
                    CallWith(m, IsVirtualDispatch(m), () => E.Get(h), () => { }, null, initExpr);
                    _wantRefReturnAddress = false;
                }
                EndTemps(ValType.I32);
                E.Set(ptr);
            }
            var lv = LocalVar(sym);
            E.Get(ptr); E.Set(lv.Local);
        }
        PushScope();
        EmitStatement(model.GetOperation(fs.Statement)!);
        PopScope();
        PopScope();
    }

    void EmitDeclaration(IVariableDeclarationOperation decl)
    {
        foreach (var d in decl.Declarators)
        {
            var init = d.Initializer?.Value ?? decl.Initializer?.Value;
            if (IsCaptured(d.Symbol))
            {
                if (init != null) { BeginTemps(); EmitStoreToVar(d.Symbol, init); EndTemps(); }
                continue;
            }
            var v = LocalVar(d.Symbol);
            if (init == null) continue;
            BeginTemps();
            if (d.Symbol.RefKind != RefKind.None)
            {
                EmitAddress(init); E.Set(v.AddrLocal);
            }
            else if (Types.IsStruct(v.Type) && IsDirectStructProducer(init))
            {
                EmitStructInto(init, () => { E.Get(v.AddrLocal); if (v.Offset != 0) { E.I32(v.Offset); E.Op(Op.I32Add); } });
            }
            else EmitStoreToVar(d.Symbol, init);
            EndTemps();
        }
    }

    void EmitReturn(IOperation? value)
    {
        bool hasValue = value != null;
        BeginTemps();
        if (value != null)
        {
            if (M.ReturnsByRef || M.ReturnsByRefReadonly)
            {
                EmitAddress(value);
                _retLocal = _retLocal >= 0 ? _retLocal : E.AddLocal(ValType.I32);
                E.Set(_retLocal);
            }
            else
            {
                var rt = T.Resolve(M.ReturnType, Map);
                if (_closure?.ReturnType is { } crt) rt = crt;
                if (Types.IsStruct(rt))
                {
                    // init-copy into the caller's destination
                    EmitValueAs(value, rt);
                    int src = Scratch(ValType.I32); E.Set(src);
                    E.Get(_structRetLocal); E.Get(src); CopyStructInit(rt);
                    FreeScratch(ValType.I32, src);
                    hasValue = false;
                }
                else
                {
                    var o = EmitValueAs(value, rt);
                    if (Types.IsRef(rt)) ToOwned(o);
                    var vt = Types.WasmType(rt)!.Value;
                    _retLocal = _retLocal >= 0 ? _retLocal : E.AddLocal(vt);
                    E.Set(_retLocal);
                }
            }
        }
        EndTemps();
        // run finally blocks and release everything live
        for (int i = _ctl.Count - 1; i >= 0; i--)
            if (_ctl[i].Kind == CtlKind.TryFinally) EmitFinallyInline(i);
        EmitReturnSequence(hasValue);
    }

    void EmitReturnSequence(bool hasValue)
    {
        foreach (var tl in _temps) foreach (var s in tl) ReleaseSlot(s);
        for (int i = _scopes.Count - 1; i >= 0; i--)
            for (int j = _scopes[i].Owned.Count - 1; j >= 0; j--) ReleaseSlot(_scopes[i].Owned[j]);
        foreach (var s in _paramSlots) ReleaseSlot(s);
        E.Emit(Op.LocalGet, _spEntry, 0, FrameMarker); E.Emit(Op.GlobalSet, 0, 0, D.StackPointer);
        if (hasValue) E.Get(_retLocal);
        E.Op(Op.Return);
    }

    void EmitFinallyInline(int ctlIndex)
    {
        var c = _ctl[ctlIndex];
        var saved = _ctl.GetRange(ctlIndex, _ctl.Count - ctlIndex);
        _ctl.RemoveRange(ctlIndex, _ctl.Count - ctlIndex);
        c.FinallyAction!();
        _ctl.AddRange(saved);
    }

    void EmitBranch(IBranchOperation br)
    {
        if (!_labels.TryGetValue(br.Target, out var t))
            throw new CompileException("goto to this label is not supported", br.Syntax.GetLocation());
        if (_gotoIndex.TryGetValue(br.Target, out var seg) && t.ctl.GotoState >= 0)
        {
            int gi = _ctl.IndexOf(t.ctl);
            for (int i = _ctl.Count - 1; i > gi; i--)
                if (_ctl[i].Kind == CtlKind.TryFinally) EmitFinallyInline(i);
            for (int i = _temps.Count - 1; i >= t.ctl.TempDepth; i--) foreach (var s in _temps[i]) ReleaseSlot(s);
            for (int i = _scopes.Count - 1; i >= t.ctl.ScopeDepth; i--)
                for (int j = _scopes[i].Owned.Count - 1; j >= 0; j--) ReleaseSlot(_scopes[i].Owned[j]);
            // backward: what the label's block declared after the label is declared again
            if (_gotoOwned.TryGetValue(br.Target, out var keep) && t.ctl.ScopeDepth > 0)
            {
                var owned = _scopes[t.ctl.ScopeDepth - 1].Owned;
                for (int j = owned.Count - 1; j >= keep; j--) ReleaseSlot(owned[j]);
            }
            E.I32(seg); E.Set(t.ctl.GotoState);
            E.Br(t.ctl.Continue!);
            return;
        }
        int idx = _ctl.IndexOf(t.ctl);
        for (int i = _ctl.Count - 1; i > idx; i--)
            if (_ctl[i].Kind == CtlKind.TryFinally) EmitFinallyInline(i);
        for (int i = _temps.Count - 1; i >= t.ctl.TempDepth; i--) foreach (var s in _temps[i]) ReleaseSlot(s);
        for (int i = _scopes.Count - 1; i >= t.ctl.ScopeDepth; i--)
            for (int j = _scopes[i].Owned.Count - 1; j >= 0; j--) ReleaseSlot(_scopes[i].Owned[j]);
        E.Br(t.isContinue ? t.ctl.Continue! : t.ctl.Break!);
    }

    Ctl PushLoop(ILoopOperation loop, Label brk, Label cont)
    {
        var c = new Ctl { Kind = CtlKind.Loop, Break = brk, Continue = cont, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
        _ctl.Add(c);
        _labels[loop.ExitLabel] = (c, false);
        _labels[loop.ContinueLabel] = (c, true);
        return c;
    }

    void EmitWhile(IWhileLoopOperation w)
    {
        PushScope();
        var brk = E.Block();
        var top = E.Loop();
        if (w.ConditionIsTop && w.Condition != null)
        {
            BeginTemps(); EmitCondition(w.Condition); EndTemps(ValType.I32);
            if (!w.ConditionIsUntil) E.Op(Op.I32Eqz);
            E.BrIf(brk);
        }
        var cont = E.Block();
        var c = PushLoop(w, brk, cont);
        PushScope(); EnterDisplay(w); EmitStatement(w.Body); ExitDisplay(w); PopScope();
        _ctl.Remove(c);
        E.End();
        if (!w.ConditionIsTop && w.Condition != null)
        {
            BeginTemps(); EmitCondition(w.Condition); EndTemps(ValType.I32);
            if (w.ConditionIsUntil) E.Op(Op.I32Eqz);
            E.BrIf(top);
        }
        else E.Br(top);
        E.End(); E.End();
        PopScope();
    }

    void EmitFor(IForLoopOperation f)
    {
        PushScope();
        foreach (var b in f.Before) EmitStatement(b);
        var brk = E.Block();
        var top = E.Loop();
        if (f.Condition != null)
        {
            BeginTemps(); EmitCondition(f.Condition); EndTemps(ValType.I32);
            E.Op(Op.I32Eqz); E.BrIf(brk);
        }
        var cont = E.Block();
        var c = PushLoop(f, brk, cont);
        PushScope(); EnterDisplay(f); EmitStatement(f.Body); ExitDisplay(f); PopScope();
        _ctl.Remove(c);
        E.End();
        foreach (var a in f.AtLoopBottom) EmitStatement(a);
        E.Br(top);
        E.End(); E.End();
        PopScope();
    }

    void EmitForEach(IForEachLoopOperation fe)
    {
        if (fe.IsAsynchronous) throw new CompileException("await foreach is not supported yet", fe.Syntax.GetLocation());
        var coll = fe.Collection;
        while (coll is IConversionOperation { IsImplicit: true } cv) coll = cv.Operand;
        var collType = T.Resolve(coll.Type!, Map);
        PushScope();
        if (collType is IArrayTypeSymbol at)
        {
            // for (int i = 0; i < a.Length; i++) { var x = a[i]; body }
            BeginTemps();
            var o = EmitValue(coll);
            int arr = E.AddLocal(ValType.I32);
            if (o == Own.Owned) { E.Set(arr); AddOwned(new Slot { Local = arr, Type = collType }); }
            else { if (o == Own.Unstable) { E.Call(Rt.Retain); E.Set(arr); AddOwned(new Slot { Local = arr, Type = collType }); } else E.Set(arr); }
            EndTemps();
            E.Get(arr); NullCheck();
            int i = E.AddLocal(ValType.I32);
            E.I32(0); E.Set(i);
            var brk = E.Block();
            var top = E.Loop();
            E.Get(i); E.Get(arr); E.Load(Op.I32Load, 12); E.Op(Op.I32GeU); E.BrIf(brk);
            var cont = E.Block();
            var c = PushLoop(fe, brk, cont);
            PushScope();
            EnterDisplay(fe);
            var elemType = T.Canonical(at.ElementType);
            AssignLoopVariable(fe, elemType, () =>
            {
                E.Get(arr); E.Get(i); var sz = T.StorageOf(elemType).size;
                if (sz != 1) { E.I32(sz); E.Op(Op.I32Mul); }
                E.Op(Op.I32Add);
                int data = Types.ArrayDataOffset(at.Rank);
                if (Types.IsStruct(elemType)) { E.I32(data); E.Op(Op.I32Add); return Own.None; }
                E.Load(Types.LoadOp(elemType), data);
                return Types.IsRef(elemType) ? Own.Unstable : Own.None;
            });
            EmitStatement(fe.Body);
            ExitDisplay(fe);
            PopScope();
            _ctl.Remove(c);
            E.End();
            E.Get(i); E.I32(1); E.Op(Op.I32Add); E.Set(i);
            E.Br(top);
            E.End(); E.End();
            PopScope();
            return;
        }
        var syntax = fe.Syntax as CommonForEachStatementSyntax ?? throw new CompileException("foreach syntax");
        var info = D.ModelFor(syntax.SyntaxTree).GetForEachStatementInfo(syntax);
        if (info.GetEnumeratorMethod == null || info.MoveNextMethod == null || info.CurrentProperty == null)
            throw new CompileException("foreach over this collection is not supported", fe.Syntax.GetLocation());
        // e = coll.GetEnumerator()
        var getEnum = info.GetEnumeratorMethod;
        var enumType = T.Resolve(getEnum.ReturnType, Map);
        Var ev;
        int enumLocal;
        BeginTemps();
        var collOwn = EmitValue(coll);
        collOwn = ToCallSafe(collOwn, collType);
        int collLocal = Scratch(ValType.I32); E.Set(collLocal);
        if (Types.IsStruct(enumType))
        {
            int off = AllocFrame(enumType);
            if (T.NeedsRc(enumType)) AddOwned(new Slot { FrameOffset = off, Type = enumType });
            ev = new Var { Type = enumType, AddrLocal = _fb, Offset = off };
            EmitCallResolved(SubstMethod(getEnum), () => ReceiverFromLocal(collLocal, collType, coll), [], false, intoAddr: () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); });
            enumLocal = -1;
        }
        else
        {
            enumLocal = E.AddLocal(ValType.I32);
            var r = EmitCallResolved(SubstMethod(getEnum), () => ReceiverFromLocal(collLocal, collType, coll), [], getEnum.IsVirtual || getEnum.IsAbstract || getEnum.ContainingType.TypeKind == TypeKind.Interface);
            if (Types.IsRef(enumType)) { ToOwned(r); E.Set(enumLocal); AddOwned(new Slot { Local = enumLocal, Type = enumType }); }
            else E.Set(enumLocal);
            ev = new Var { Type = enumType, Local = enumLocal };
        }
        FreeScratch(ValType.I32, collLocal);
        EndTemps();

        void Recv()
        {
            if (ev.InMemory) { E.Get(_fb); E.I32(ev.Offset); E.Op(Op.I32Add); }
            else E.Get(ev.Local);
        }
        bool enumIsStruct = Types.IsStruct(enumType);
        var dispose = info.DisposeMethod == null ? null : enumIsStruct ? FindDispose(enumType) : info.DisposeMethod;
        void Body()
        {
            var brk = E.Block();
            var top = E.Loop();
            BeginTemps();
            var mn = EmitCallResolved(SubstMethod(info.MoveNextMethod!), Recv, [], !enumIsStruct && IsVirtualDispatch(info.MoveNextMethod!));
            EndTemps(ValType.I32);
            E.Op(Op.I32Eqz); E.BrIf(brk);
            var cont = E.Block();
            var c = PushLoop(fe, brk, cont);
            PushScope();
            EnterDisplay(fe);
            var getter = info.CurrentProperty!.GetMethod!;
            var elemType = T.Resolve(getter.ReturnType, Map);
            AssignLoopVariable(fe, elemType, () => EmitCallResolved(SubstMethod(getter), Recv, [], !enumIsStruct && IsVirtualDispatch(getter)), conversion: fe.LoopControlVariable);
            EmitStatement(fe.Body);
            ExitDisplay(fe);
            PopScope();
            _ctl.Remove(c);
            E.End();
            E.Br(top);
            E.End(); E.End();
        }
        if (dispose != null)
        {
            EmitTryFinallyCore(Body, () =>
            {
                BeginTemps();
                if (!enumIsStruct)
                {
                    Recv(); E.If();
                    EmitCallResolved(SubstMethod(dispose), Recv, [], IsVirtualDispatch(dispose) || dispose.ContainingType.TypeKind == TypeKind.Interface);
                    E.End();
                }
                else EmitCallResolved(SubstMethod(dispose), Recv, [], false);
                EndTemps();
            });
        }
        else Body();
        PopScope();
    }

    static bool IsVirtualDispatch(IMethodSymbol m) => (m.IsVirtual || m.IsAbstract || m.IsOverride || m.ContainingType.TypeKind == TypeKind.Interface) && !m.IsSealed && !m.ContainingType.IsSealed && !m.ContainingType.IsValueType;

    void ReceiverFromLocal(int local, ITypeSymbol type, IOperation orig)
    {
        E.Get(local);
        if (!Types.IsStruct(type) && !type.IsValueType) { /* reference: null check */ E.Tee(local); NullCheck(); E.Get(local); }
    }

    /// <summary>Declares and assigns the foreach iteration variable from the value produced by <paramref name="produce"/>.</summary>
    void AssignLoopVariable(IForEachLoopOperation fe, ITypeSymbol valueType, Func<Own> produce, IOperation? conversion = null)
    {
        if (fe.LoopControlVariable is not IVariableDeclaratorOperation decl)
        {
            var tupleTarget = fe.LoopControlVariable is IDeclarationExpressionOperation { Expression: ITupleOperation dt } ? dt : fe.LoopControlVariable as ITupleOperation;
            if (tupleTarget != null && fe.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.ForEachVariableStatementSyntax fvs)
            {
                BeginTemps();
                var o = produce();
                int src;
                if (Types.IsStruct(valueType)) { src = E.AddLocal(ValType.I32); E.Set(src); }
                else { if (Types.IsRef(valueType)) ToCallSafe(o, valueType); src = E.AddLocal(ValType.I32); E.Set(src); }
                var dinfo = D.ModelFor(fvs.SyntaxTree).GetDeconstructionInfo(fvs);
                DeconstructInto(tupleTarget, valueType, src, dinfo);
                EndTemps();
                return;
            }
            throw new CompileException("unsupported foreach variable", fe.Syntax.GetLocation());
        }
        var sym = decl.Symbol;
        BeginTemps();
        if (IsCapturedInThis(sym))
        {
            EmitStoreToVarFrom(sym, valueType, produce);
        }
        else
        {
            var v = LocalVar(sym);
            var o = produce();
            ConvertTop(valueType, v.Type, ref o);
            StoreVar(v, o);
        }
        EndTemps();
    }

    // ---------------- switch ----------------
    void EmitSwitch(ISwitchOperation sw)
    {
        PushScope();
        BeginTemps();
        var vt = T.Resolve(sw.Value.Type!, Map);
        var o = EmitValue(sw.Value);
        int val;
        if (Types.IsRef(vt))
        {
            val = E.AddLocal(ValType.I32);
            ToOwned(o); E.Set(val); AddOwned(new Slot { Local = val, Type = vt });
        }
        else { val = E.AddLocal(Types.WasmType(vt) ?? ValType.I32); E.Set(val); if (Types.IsStruct(vt)) { /* address */ } }
        EndTemps();
        var end = E.Block();
        var c = new Ctl { Kind = CtlKind.Switch, Break = end, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
        _ctl.Add(c);
        _labels[sw.ExitLabel] = (c, false);
        // goto case / goto default: sections inside a dispatch loop; a state of -1 means "test the clauses"
        var clauseLabels = new Dictionary<ILabelSymbol, int>(SymbolEqualityComparer.Default);
        for (int i = 0; i < sw.Cases.Length; i++) foreach (var cl in sw.Cases[i].Clauses) if (cl.Label != null) clauseLabels[cl.Label] = i;
        bool gotoCase = sw.Cases.Any(cs => cs.Body.Any(b => b.DescendantsAndSelf().OfType<IBranchOperation>().Any(br => clauseLabels.ContainsKey(br.Target))));
        int sect = -1;
        if (gotoCase)
        {
            sect = E.AddLocal(ValType.I32);
            E.I32(-1); E.Set(sect);
            c.Continue = E.Loop();
            c.GotoState = sect;
            foreach (var (lbl, i) in clauseLabels) { _labels[lbl] = (c, true); _gotoIndex[lbl] = i; }
        }
        var labels = new Label[sw.Cases.Length];
        for (int i = sw.Cases.Length - 1; i >= 0; i--) labels[i] = E.Block();
        if (gotoCase)
        {
            E.Get(sect); E.I32(0); E.Op(Op.I32GeS);
            E.If(); E.Get(sect); E.BrTable(labels, labels[0]); E.End();
        }
        int defaultIdx = -1;
        for (int i = 0; i < sw.Cases.Length; i++)
        {
            foreach (var clause in sw.Cases[i].Clauses)
            {
                switch (clause)
                {
                    case IDefaultCaseClauseOperation: defaultIdx = i; break;
                    case ISingleValueCaseClauseOperation sv:
                        BeginTemps(); EmitEqualsConst(val, vt, sv.Value); EndTemps(ValType.I32); E.BrIf(labels[i]); break;
                    case IPatternCaseClauseOperation pc:
                        BeginTemps();
                        EmitPattern(pc.Pattern, val, vt);
                        if (pc.Guard != null) { E.If(ValType.I32); EmitCondition(pc.Guard); E.Else(); E.I32(0); E.End(); }
                        EndTemps(ValType.I32);
                        E.BrIf(labels[i]); break;
                    case IRelationalCaseClauseOperation rc:
                        BeginTemps(); E.Get(val); EmitValue(rc.Value); EmitCompare(rc.Relation, vt); EndTemps(ValType.I32); E.BrIf(labels[i]); break;
                    default: throw new CompileException("unsupported case clause", clause.Syntax.GetLocation());
                }
            }
        }
        if (defaultIdx >= 0) E.Br(labels[defaultIdx]); else E.Br(end);
        for (int i = 0; i < sw.Cases.Length; i++)
        {
            E.End(); // close block labels[i]
            PushScope();
            foreach (var s in sw.Cases[i].Body) EmitStatement(s);
            PopScope();
            // C# sections cannot fall through; fall-off means unreachable (or break emitted already)
            E.Br(end);
        }
        _ctl.Remove(c);
        if (gotoCase) E.End();
        E.End();
        PopScope();
    }

    void EmitEqualsConst(int val, ITypeSymbol vt, IOperation constant)
    {
        if (Types.IsString(vt))
        {
            E.Get(val); EmitValue(constant);
            E.Call(D.LibMethod("System.String", "Equals", 2).Fn);
            return;
        }
        E.Get(val); var ct = constant.Type != null ? T.Resolve(constant.Type, Map) : vt;
        var o = EmitValue(constant); ConvertTop(ct, vt, ref o);
        EmitCompare(BinaryOperatorKind.Equals, vt);
    }

    // ---------------- try/catch/finally ----------------
    void EmitTry(ITryOperation tr)
    {
        if (tr.Finally != null)
        {
            EmitTryFinallyCore(() =>
            {
                if (tr.Catches.Length > 0) EmitTryCatch(tr);
                else { PushScope(); EmitStatement(tr.Body); PopScope(); }
            }, () => { PushScope(); EmitStatement(tr.Finally); PopScope(); });
        }
        else EmitTryCatch(tr);
    }

    void EmitTryFinallyCore(Action body, Action fin)
    {
        // block $done
        //   block $h (result exnref)
        //     try_table (catch_all_ref $h)  body  end
        //     finally ; br $done
        //   end
        //   restore sp ; release region ; finally ; throw_ref
        // end
        var done = E.Block();
        var h = E.Block(ValType.ExnRef);
        var region = new HashSet<Slot>();
        _regions.Add(region);
        E.TryTable(null, (3, null, h));
        _usesFrame = true;
        var c = new Ctl { Kind = CtlKind.TryFinally, FinallyAction = fin, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
        _ctl.Add(c);
        body();
        _ctl.Remove(c);
        E.End();
        _regions.Remove(region);
        fin();
        E.Br(done);
        E.End();
        int exn = E.AddLocal(ValType.ExnRef);
        E.Set(exn);
        E.Get(_fb); E.GSet(D.StackPointer);
        foreach (var s in region) ReleaseSlot(s);
        fin();
        E.Get(exn); E.Op(Op.ThrowRef);
        E.End();
    }


    void EmitTryCatch(ITryOperation tr)
    {
        // block $done
        //   block $c (result i32 exnref)
        //     try_table (catch_ref $tag $c) body end
        //     br $done
        //   end
        //   ... dispatch on type ...
        // end
        _usesFrame = true;
        bool twoPass = D.UsesFilters;
        var (rec, savedChain) = twoPass ? PushHandlerRecord(tr) : (0, -1);
        var done = E.Block();
        var cblk = E.Block(new FuncType([], [ValType.I32, ValType.ExnRef]));
        var region = new HashSet<Slot>();
        _regions.Add(region);
        E.TryTable(null, (1, D.ExceptionTag, cblk));
        Ctl? pop = null;
        if (twoPass) { pop = new Ctl { Kind = CtlKind.TryFinally, FinallyAction = () => { E.Get(savedChain); E.GSet(D.EhChain); }, ScopeDepth = _scopes.Count, TempDepth = _temps.Count }; _ctl.Add(pop); }
        PushScope(); EmitStatement(tr.Body); PopScope();
        if (pop != null) { _ctl.Remove(pop); E.Get(savedChain); E.GSet(D.EhChain); }
        E.End();
        _regions.Remove(region);
        E.Br(done);
        E.End();
        int exn = E.AddLocal(ValType.ExnRef), ex = E.AddLocal(ValType.I32);
        E.Set(exn); E.Set(ex);
        E.Get(_fb); E.GSet(D.StackPointer);
        foreach (var s in region) ReleaseSlot(s);
        int clause = -1;
        if (twoPass)
        {
            clause = E.AddLocal(ValType.I32);
            EmitHandlerDecision(rec, savedChain, ex, clause);
            E.Get(clause); E.Op(Op.I32Eqz); E.If(); E.Get(exn); E.Op(Op.ThrowRef); E.End();
        }
        E.Get(ex); E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "OnCatch", 1).Fn);
        for (int ci = 0; ci < tr.Catches.Length; ci++)
        {
            var cc = tr.Catches[ci];
            var et = cc.ExceptionType is { } t0 ? T.Resolve(t0, Map) : null;
            bool catchAll = et == null || Types.FullName(et) == "System.Exception" || Types.IsObject(et);
            if (twoPass) { E.Get(clause); E.I32(ci + 1); E.Op(Op.I32Eq); }
            else if (!catchAll) { E.Get(ex); E.Call(Rt.IsInst(et!)); }
            else E.I32(1);
            if (cc.Filter != null && !twoPass)
            {
                E.If(ValType.I32);
                PushScope();
                BindCatchVariable(cc, ex, retain: true);
                BeginTemps(); EmitCondition(cc.Filter); EndTemps(ValType.I32);
                int fr = Scratch(ValType.I32); E.Set(fr);
                PopScope();
                E.Get(fr); FreeScratch(ValType.I32, fr);
                E.Else(); E.I32(0); E.End();
            }
            E.If();
            PushScope();
            var c = new Ctl { Kind = CtlKind.Catch, ExnLocal = exn, ExPtrLocal = ex, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
            _ctl.Add(c);
            if (!BindCatchVariable(cc, ex, retain: false))
            {
                // no variable: the in-flight count is released now
                int keep = E.AddLocal(ValType.I32);
                E.Get(ex); E.Set(keep); AddOwned(new Slot { Local = keep, Type = D.ObjectType });
            }
            EmitStatement(cc.Handler);
            _ctl.Remove(c);
            PopScope();
            E.Br(done);
            E.End();
        }
        E.Get(exn); E.Op(Op.ThrowRef);
        E.End();
    }

    /// <summary>Binds the catch variable; it takes over the thrown object's count (or retains it, for filters).</summary>
    bool BindCatchVariable(ICatchClauseOperation cc, int ex, bool retain)
    {
        if (cc.ExceptionDeclarationOrExpression is IVariableDeclaratorOperation vd)
        {
            if (IsCapturedInThis(vd.Symbol))
            {
                EmitStoreToVarFrom(vd.Symbol, D.ObjectType, () => { E.Get(ex); return retain ? Own.Stable : Own.Owned; });
                return true;
            }
            _vars.Remove(vd.Symbol);
            var v = LocalVar(vd.Symbol);
            E.Get(ex); if (retain) E.Call(Rt.Retain);
            E.Set(v.Local);
            return true;
        }
        return false;
    }

    void EmitThrow(IOperation? exception)
    {
        if (exception == null)
        {
            // rethrow: the innermost catch's exception
            var c = _ctl.LastOrDefault(x => x.Kind == CtlKind.Catch) ?? throw new CompileException("rethrow outside catch");
            E.Get(c.ExPtrLocal); E.Call(Rt.Retain); E.Op(Op.Drop);
            if (D.UsesFilters) { E.Get(c.ExPtrLocal); E.Call(Rt.FirstPass); }
            E.Get(c.ExnLocal); E.Op(Op.ThrowRef);
            return;
        }
        var o = EmitValue(exception);
        ToOwned(o);
        int t = Scratch(ValType.I32);
        E.Tee(t); NullCheck();
        E.Get(t);
        E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "OnThrow", 1).Fn);
        if (D.UsesFilters) { E.Get(t); E.Call(Rt.FirstPass); }
        E.Get(t);
        E.Emit(Op.Throw, 0, 0, D.ExceptionTag);
        FreeScratch(ValType.I32, t);
    }

    // ---------------- using ----------------
    void EmitUsing(IOperation resources, IOperation body, System.Collections.Immutable.ImmutableArray<ILocalSymbol> locals)
    {
        PushScope();
        EmitUsingCore(resources, () => { PushScope(); EmitStatement(body); PopScope(); });
        PopScope();
    }

    void EmitUsingCore(IOperation resources, Action body)
    {
        // evaluate resources into locals, then try { body } finally { dispose in reverse }
        var held = new List<(int local, ITypeSymbol type, Var? var)>();
        if (resources is IVariableDeclarationGroupOperation g)
        {
            foreach (var decl in g.Declarations)
            {
                EmitDeclaration(decl);
                foreach (var d in decl.Declarators)
                {
                    var v = LocalVar(d.Symbol);
                    held.Add((-1, v.Type, v));
                }
            }
        }
        else
        {
            BeginTemps();
            var rt = T.Resolve(resources.Type!, Map);
            var o = EmitValue(resources);
            int l = E.AddLocal(ValType.I32);
            if (Types.IsRef(rt)) { ToOwned(o); E.Set(l); AddOwned(new Slot { Local = l, Type = rt }); }
            else E.Set(l);
            EndTemps();
            held.Add((l, rt, null));
        }
        EmitTryFinallyCore(body, () =>
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                var (l, type, v) = held[i];
                var dispose = FindDispose(type);
                if (dispose == null) continue;
                BeginTemps();
                void Recv()
                {
                    if (v != null)
                    {
                        if (v.InMemory) { E.Get(v.AddrLocal); if (v.Offset != 0) { E.I32(v.Offset); E.Op(Op.I32Add); } if (!Types.IsStruct(type)) E.Load(Op.I32Load); }
                        else E.Get(v.Local);
                    }
                    else E.Get(l);
                }
                if (Types.IsStruct(type)) EmitCallResolved(dispose, Recv, [], false);
                else
                {
                    Recv(); E.If();
                    EmitCallResolved(dispose, Recv, [], IsVirtualDispatch(dispose) || dispose.ContainingType.TypeKind == TypeKind.Interface);
                    E.End();
                }
                EndTemps();
            }
        });
    }

    IMethodSymbol? FindDispose(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n)
        {
            var direct = n.GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && !m.IsStatic);
            if (direct != null && (n.IsValueType || n.IsRefLikeType)) return direct;
        }
        var idisp = D.Main.GetSpecialType(SpecialType.System_IDisposable);
        var m2 = idisp.GetMembers("Dispose").OfType<IMethodSymbol>().First();
        if (t.TypeKind == TypeKind.Interface && Types.FullName(t) == "System.IDisposable") return m2;
        if (t.IsValueType) return D.Implementation(t, m2);
        return m2;
    }

    // =====================================================================
    // constructors & synthesized members
    // =====================================================================
    void EmitConstructorBody(IConstructorBodyOperation ctor)
    {
        bool chainsToThis = ctor.Initializer is IExpressionStatementOperation { Operation: IInvocationOperation inv } &&
                            SymbolEqualityComparer.Default.Equals(inv.TargetMethod.ContainingType.OriginalDefinition, M.ContainingType.OriginalDefinition);
        if (!chainsToThis) EmitFieldInitializers();
        if (ctor.Initializer != null) EmitStatement(ctor.Initializer);
        else EmitImplicitBaseCtor();
        if (ctor.BlockBody != null) EmitStatement(ctor.BlockBody);
        else if (ctor.ExpressionBody != null) EmitStatement(ctor.ExpressionBody);
    }

    void EmitImplicitBaseCtor()
    {
        if (M.ContainingType.TypeKind != TypeKind.Class || M.IsStatic) return;
        var bt = M.ContainingType.BaseType;
        if (bt == null || Types.IsObject(bt)) return;
        var cbt = (INamedTypeSymbol)T.Resolve(bt, Map);
        var bctor = cbt.InstanceConstructors.FirstOrDefault(c => c.Parameters.All(p => p.IsOptional || p.IsParams));
        if (bctor == null) return;
        BeginTemps();
        EmitCallResolved(bctor, () => E.Get(_thisLocal), bctor.Parameters.Select(p => (IOperation?)null).ToList(), false);
        EndTemps();
    }

    void EmitFieldInitializers()
    {
        var t = M.ContainingType;
        foreach (var m in t.GetMembers())
        {
            if (m.IsStatic) continue;
            EqualsValueClauseSyntax? init = m switch
            {
                IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared => Driver.FieldInitializer(f),
                IPropertySymbol p => Driver.PropertyInitializer(p),
                _ => null,
            };
            if (init == null) continue;
            var model = D.ModelFor(init.SyntaxTree);
            var op = model.GetOperation(init);
            int save = E.CurrentPos; E.CurrentPos = D.Position(init);
            BeginTemps();
            switch (op)
            {
                case IFieldInitializerOperation fi:
                    foreach (var f in fi.InitializedFields) EmitStoreField(f, () => E.Get(_thisLocal), fi.Value);
                    break;
                case IPropertyInitializerOperation pi:
                    foreach (var p in pi.InitializedProperties)
                    {
                        var bf = Driver.AutoPropertyField(p) ?? throw new CompileException("property initializer without backing field");
                        EmitStoreField(bf, () => E.Get(_thisLocal), pi.Value);
                    }
                    break;
            }
            EndTemps();
            E.CurrentPos = save;
        }
    }

    void GuardStaticInitOnEntry()
    {
        if (M.MethodKind == MethodKind.StaticConstructor) return;
        if (M.ContainingType is not { } ct) return;
        if (!(M.IsStatic || M.MethodKind == MethodKind.Constructor)) return;
        if (D.BeforeFieldInit(ct)) return; // only precise (explicit static constructor) types run on method entry
        EmitStaticInitGuard(ct);
    }

    void EmitStaticInitGuard(INamedTypeSymbol ct)
    {
        var si = D.StaticInit((INamedTypeSymbol)T.Resolve(ct, Map));
        if (si == null) return;
        // inside the type's own static constructor no guard is needed
        if (M.MethodKind == MethodKind.StaticConstructor && Types.Key(T.Resolve(M.ContainingType, Map)) == Types.Key(T.Resolve(ct, Map))) return;
        E.I32Addr(si.Value.flag); E.Load(Op.I32Load);
        E.I32(-1); E.Op(Op.I32Ne); E.If(); E.Call(si.Value.fn); E.End();
    }

    public static void CompileStaticInit(Driver d, INamedTypeSymbol t, WasmFunction fn, IAddress flag, IMethodSymbol? cctor)
    {
        var sym = cctor ?? t.StaticConstructors.FirstOrDefault() ?? throw new CompileException("no static constructor symbol for " + t);
        var fc = new FunctionCompiler(d, fn, sym, null);
        fc.CompileStaticInitBody(t, flag, cctor);
    }

    void CompileStaticInitBody(INamedTypeSymbol t, IAddress flag, IMethodSymbol? cctor)
    {
        PushScope();
        // base type's initializer first is not required by C#; static field initializers in textual order
        foreach (var m in t.GetMembers())
        {
            if (!m.IsStatic) continue;
            EqualsValueClauseSyntax? init = m switch
            {
                IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared => Driver.FieldInitializer(f),
                IPropertySymbol p => Driver.PropertyInitializer(p),
                _ => null,
            };
            if (init == null) continue;
            var op = D.ModelFor(init.SyntaxTree).GetOperation(init);
            E.CurrentPos = D.Position(init);
            BeginTemps();
            switch (op)
            {
                case IFieldInitializerOperation fi:
                    foreach (var f in fi.InitializedFields) EmitStoreField(f, null, fi.Value);
                    break;
                case IPropertyInitializerOperation pi:
                    foreach (var p in pi.InitializedProperties)
                        EmitStoreField(Driver.AutoPropertyField(p)!, null, pi.Value);
                    break;
            }
            EndTemps();
        }
        if (cctor != null && Driver.HasBody(cctor))
        {
            var (body, syntax) = GetBody(cctor);
            PreScan(body);
            if (body is IConstructorBodyOperation cb) { if (cb.BlockBody != null) EmitStatement(cb.BlockBody); else if (cb.ExpressionBody != null) EmitStatement(cb.ExpressionBody); }
            else if (body is IMethodBodyOperation mb) EmitStatement((IOperation?)mb.BlockBody ?? mb.ExpressionBody!);
        }
        Finish();
    }

    void Synthesize()
    {
        var def = M.OriginalDefinition;
        if (_anonType != null || def.ContainingType.IsAnonymousType) { EmitAnonymousMember(); return; }
        if (def.ContainingType.IsRecord && def.IsImplicitlyDeclared && !(def.AssociatedSymbol is IPropertySymbol ap && Driver.AutoPropertyField(ap) != null) || def.ContainingType.IsRecord && def.MethodKind == MethodKind.Constructor && !Driver.HasBody(def)) { EmitRecordMember(); return; }
        if (def.MethodKind == MethodKind.Constructor)
        {
            if (Driver.IsPrimaryCtor(def))
            {
                // capture the parameters, run the initializers, then the base constructor from the base list
                foreach (var pp in M.Parameters)
                {
                    var slot = PrimarySlot(pp);
                    var v = _vars[pp];
                    BeginTemps();
                    StoreToAddressFromStack(() => E.Get(_thisLocal), slot.Offset, slot.Type, LoadVar(v), pub: Publish.Container, pubContainer: () => E.Get(_thisLocal));
                    EndTemps();
                }
                EmitFieldInitializers();
                var decl = def.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().First();
                var baseSyntax = decl.BaseList?.Types.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PrimaryConstructorBaseTypeSyntax>().FirstOrDefault();
                if (baseSyntax != null && D.ModelFor(baseSyntax.SyntaxTree).GetOperation(baseSyntax) is IOperation bop)
                {
                    BeginTemps(); EmitDiscard(bop is IExpressionStatementOperation es ? es.Operation : bop); EndTemps();
                }
                else EmitImplicitBaseCtor();
                return;
            }
            EmitFieldInitializers();
            EmitImplicitBaseCtor();
            return;
        }
        if (def.MethodKind == MethodKind.StaticConstructor) return;
        if (def.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet && def.AssociatedSymbol is IPropertySymbol p)
        {
            var bf = Driver.AutoPropertyField(p)!;
            if (def.MethodKind == MethodKind.PropertyGet)
            {
                var ft = T.Resolve(bf.Type, Map);
                if (bf.IsStatic) { EmitStaticInitGuardForField(Owner(bf)); E.I32Addr(D.StaticField(Owner(bf), bf, ft)); }
                else { E.Get(_thisLocal); }
                int off = bf.IsStatic ? 0 : T.Field(bf, Map).Offset;
                if (Types.IsStruct(ft))
                {
                    if (off != 0) { E.I32(off); E.Op(Op.I32Add); }
                    int src = Scratch(ValType.I32); E.Set(src);
                    E.Get(_structRetLocal); E.Get(src); CopyStructInit(ft);
                    EmitReturnSequence(false);
                }
                else
                {
                    E.Load(Types.LoadOp(ft), off);
                    if (Types.IsRef(ft)) E.Call(Rt.Retain);
                    _retLocal = E.AddLocal(Types.WasmType(ft)!.Value); E.Set(_retLocal);
                    EmitReturnSequence(true);
                }
            }
            else
            {
                var vp = M.Parameters[^1];
                EmitStoreFieldFrom(bf, bf.IsStatic ? null : () => E.Get(_thisLocal), T.Resolve(vp.Type, Map), () => LoadVar(_vars[vp]));
            }
            return;
        }
        if (def.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove && def.AssociatedSymbol is IEventSymbol ev)
        {
            var dt = T.Resolve(ev.Type, Map);
            var combine = D.LibMethod("System.Delegate", def.MethodKind == MethodKind.EventAdd ? "Combine" : "Remove", 2);
            int baseL = E.AddLocal(ValType.I32);
            int off = EventStorage(ev, () => E.Get(_thisLocal));
            E.Set(baseL);
            E.Get(baseL); E.Load(Op.I32Load, off);
            LoadVar(_vars[M.Parameters[0]]);
            E.Call(combine.Fn);
            StoreToAddressFromStack(() => E.Get(baseL), off, dt, Own.Owned);
            return;
        }
        if (def.ContainingType.TypeKind == TypeKind.Delegate && def.Name == "Invoke")
        {
            EmitDelegateInvokeBody();
            return;
        }
        if (def.ContainingType.IsRecord) { EmitRecordMember(); return; }
        throw new CompileException("cannot synthesize " + def.ToDisplayString());
    }

    INamedTypeSymbol Owner(ISymbol member) => (INamedTypeSymbol)T.Resolve(member.ContainingType, Map);

    /// <summary>Pushes the base address of a field-like event's storage; returns its offset.</summary>
    int EventStorage(IEventSymbol ev, Action pushInstance)
    {
        var owner = Owner(ev);
        if (ev.IsStatic) { EmitStaticInitGuardForField(owner); E.I32Addr(D.StaticField(owner, ev, T.Resolve(ev.Type, Map))); return 0; }
        pushInstance();
        var slot = T.LayoutOf(owner).Fields.First(f => f.Symbol is IEventSymbol e && e.Name == ev.Name);
        return slot.Offset;
    }

    void EmitStaticInitGuardForField(INamedTypeSymbol owner)
    {
        var ct = (INamedTypeSymbol)T.Canonical(owner);
        if (M.ContainingType != null && Types.Key(T.Resolve(M.ContainingType, Map)) == Types.Key(ct) && M.MethodKind == MethodKind.StaticConstructor) return;
        EmitStaticInitGuard(ct);
    }

    // =====================================================================
    // diagnostics for missing members
    // =====================================================================
    static readonly HashSet<string> s_sandboxNamespaces = ["System.IO", "System.Net", "System.Net.Sockets", "System.Net.Http", "Microsoft.Win32", "System.Runtime.InteropServices.ComTypes"];

    void ReportMissing(IMethodSymbol m, Location? loc)
    {
        loc ??= m.Locations.FirstOrDefault(l => l.IsInSource);
        var name = m.ToDisplayString();
        if (Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null)
            Console.Error.WriteLine($"missing {name} asm={m.ContainingAssembly?.Name} type-asm={m.ContainingType?.ContainingAssembly?.Name} key={Types.MethodKey(m)} {Environment.StackTrace}");
        var def = m.OriginalDefinition;
        if (def.GetDllImportData() != null || def.GetAttributes().Any(a => a.AttributeClass?.Name is "DllImportAttribute" or "LibraryImportAttribute"))
        { D.Report(Diags.PlatformInvoke, loc, name); return; }
        if (!D.IsLibrary(m) && def.Locations.Any(l => l.IsInSource))
        { D.Report(Diags.MissingBoundary, loc, Types.CommentId(m)); return; }
        var ns = m.ContainingNamespace?.ToDisplayString() ?? "";
        var tn = Types.FullName(m.ContainingType);
        string? cap = ns switch
        {
            "System.IO" when tn.Contains("File") || tn.Contains("Directory") || tn.Contains("Path") || tn.Contains("Drive") => "the file system",
            "System.Net" or "System.Net.Sockets" or "System.Net.Http" => "network sockets",
            "Microsoft.Win32" => "the registry",
            _ => null,
        };
        if (tn == "System.Diagnostics.Process") cap = "processes";
        if (tn == "System.Environment" && (m.Name.Contains("Environment") || m.Name is "SetEnvironmentVariable" or "GetEnvironmentVariables" or "Exit" or "GetCommandLineArgs" or "get_CurrentDirectory")) cap = m.Name == "Exit" ? "processes" : "the environment";
        if (cap != null) D.Report(Diags.SandboxCapability, loc, name, cap);
        else D.Report(Diags.LibraryMissing, loc, name);
    }
}
