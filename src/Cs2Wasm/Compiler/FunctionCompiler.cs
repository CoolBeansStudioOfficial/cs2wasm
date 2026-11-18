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
    /// <summary>A field borrow's temp (FieldBorrows.cs): released through this placeholder (a release, or a drop).</summary>
    public WasmFunction? ReleaseMarker;
    /// <summary>A struct temporary moved to its owner (StructMoves.cs): cleared, no release at the statement's end.</summary>
    public bool Moved;
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
    /// <summary>goto: the block each segment begins after, and the segment being compiled (a goto to a later one is a
    /// branch out of that segment's block, still open: no dispatch).</summary>
    public Label[]? GotoSegs; public int GotoSeg;
    public int ExnLocal = -1, ExPtrLocal = -1; // inside a catch: the caught exception
}

sealed partial class FunctionCompiler
{
    static readonly bool s_expNoThrow = Environment.GetEnvironmentVariable("CS2WASM_EXP_NOTHROW") == "1";
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
        // a body the trimmer's substitutions replace (with cs2wasm's features): a stub returning the value
        if (Inst?.Impl == MethodImpl.Body && Library.Substitution(M) is { } sub) { EmitSubstitutedBody(sub); return; }
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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var (body, syntax) = GetBody(def);
        Driver.BindTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        E.CurrentPos = D.Position(syntax);
        PreScan(body);
        SetupOwnedParams();
        FindFrameObjects(body);
        using (BodyScope())
        {
            EnterDisplay(s_fnKey);
            GuardStaticInitOnEntry();
            if (body is IConstructorBodyOperation ctor) EmitConstructorBody(ctor);
            else if (body is IMethodBodyOperation mb) EmitStatement((IOperation?)mb.BlockBody ?? mb.ExpressionBody!);
            else EmitStatement(body);
        }
        // (a consumed struct parameter must have moved: its caller does not release the temporary it passed)
        if (_structParamLoads != _structParamMoves)
            throw new CompileException($"internal: consumed struct parameter not moved in {M.ToDisplayString()}");
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

        // (a frame alone needs no handler: the stack pointer is put back where the exception is caught,
        // RuntimeGen.RestoreStackAtCatches)
        bool needHandler = _allSlots.Count > 0;
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
            // (from here on an exception is on its way out: nothing reads a temporary again, FieldBorrows.cs)
            H(Op.Nop, -1, Driver.HandlerStart);
            var save = E.Fn.Body; E.Fn.Body = handler;
            foreach (var s in _allSlots) ReleaseSlot(s);
            E.Fn.Body = save;
            H(Op.LocalGet, exn); H(Op.ThrowRef);
            body.InsertRange(0, prologue);
            body.AddRange(handler);
        }
        else body.InsertRange(0, prologue);
        for (int i = 0; i < body.Count; i++) if (ReferenceEquals(body[i].Ref, FrameMarker)) body[i] = body[i] with { Ref = null };
        E.Fn.Body.RemoveAll(i => i.Op == Op.Nop && i.Ref is not Driver.WriteMark);
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
        else if (!M.IsStatic)
        {
            _thisLocal = idx++;
            if (Inst?.ThisByValue == true)
            {
                // (the number came itself: it is kept in the frame, and `this` is where: Driver.ThisValueVariant)
                var nt = T.Canonical(M.ContainingType);
                int value = _thisLocal, off = AllocFrame(nt);
                _thisLocal = E.AddLocal(ValType.I32);
                var save = E.Fn.Body; E.Fn.Body = _entryCode;
                E.Get(_fb); E.Get(value); E.Store(Types.StoreOp(nt), off);
                E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Set(_thisLocal);
                E.Fn.Body = save;
            }
        }
        // an extension member (C# 14 `extension(T x)` block): the receiver comes as `this` (its address when it is a
        // value or passed by reference), and the block's parameter names it
        if (_closure == null && !M.IsStatic && M.ContainingType is { IsExtension: true, ExtensionParameter: { } xp })
        {
            var xt = T.Resolve(xp.Type, Map);
            var xv = new Var { Type = xt };
            if (xp.RefKind != RefKind.None || xt.IsValueType) { xv.AddrLocal = _thisLocal; xv.Offset = 0; }
            else xv.Local = _thisLocal;
            _vars[xp] = xv;
            _vars[xp.OriginalDefinition] = xv;
        }
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
    /// <summary>Locals whose address is taken in an operation tree (for one a control-flow graph synthesizes).</summary>
    void MarkAddressTaken(IOperation tree)
    {
        foreach (var op in tree.DescendantsAndSelf())
            switch (op)
            {
                case IArgumentOperation { Parameter.RefKind: not RefKind.None } a: MarkAddr(a.Value); break;
                case IAddressOfOperation ao: MarkAddr(ao.Reference); break;
                case IDynamicInvocationOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation: MarkDynamicRefArgs(op); break;
                case IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None, Initializer: { } init }: MarkAddr(init.Value); break;
                case ISimpleAssignmentOperation { IsRef: true } sa: MarkAddr(sa.Value); break;
            }
    }

    /// <summary>The ref and out arguments of a dynamic operation (passed by reference to its call site).</summary>
    void MarkDynamicRefArgs(IOperation op)
    {
        var (args, kind) = op switch
        {
            IDynamicInvocationOperation d => (d.Arguments, (Func<int, RefKind?>)d.GetArgumentRefKind),
            IDynamicIndexerAccessOperation d => (d.Arguments, d.GetArgumentRefKind),
            IDynamicObjectCreationOperation d => (d.Arguments, d.GetArgumentRefKind),
            _ => throw new InvalidOperationException(),
        };
        for (int i = 0; i < args.Length; i++)
            if (kind(i) is RefKind.Ref or RefKind.Out) MarkAddr(args[i]);
    }

    void PreScan(IOperation body)
    {
        foreach (var op in body.DescendantsAndSelf())
        {
            switch (op)
            {
                case IArgumentOperation { Parameter.RefKind: not RefKind.None } a: MarkAddr(a.Value); break;
                case IAddressOfOperation ao: MarkAddr(ao.Reference); break;
                case IDynamicInvocationOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation: MarkDynamicRefArgs(op); break;
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
        ComputeLocalMoves(body);
        foreach (var p in M.Parameters)
        {
            var v = _vars[p];
            var pt = v.Type;
            // (the body refers to the definition's parameters: a generic method's instantiation has others)
            if (p.RefKind == RefKind.None && !v.InMemory && (_addrTaken.Contains(p) || _addrTaken.Contains(p.OriginalDefinition)))
            {
                // move the parameter into the frame
                int off = AllocFrame(pt);
                var save = E.Fn.Body; E.Fn.Body = _entryCode;
                E.Get(_fb); E.Get(v.Local); E.Store(Types.StoreOp(pt), off);
                E.Fn.Body = save;
                v.Local = -1; v.AddrLocal = _fb; v.Offset = off;
            }
            // a parameter the body assigns, or whose address it takes (a `ref` argument can be written through:
            // `MakeCount(ref args)`), owns its value: the caller's stays the caller's
            if (p.RefKind == RefKind.None && Types.IsRef(pt) && !Types.IsImmortalType(pt) && (_assignedParams.Contains(p) || _assignedParams.Contains(p.OriginalDefinition)
                || _addrTaken.Contains(p) || _addrTaken.Contains(p.OriginalDefinition)))
            {
                _assignedParams.Add(p);
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
            // `ref c ? ref a : ref b`: either may be the reference
            case IConditionalOperation { IsRef: true } rc: MarkAddr(rc.WhenTrue); if (rc.WhenFalse != null) MarkAddr(rc.WhenFalse); break;
        }
    }

    int AllocFrame(ITypeSymbol t)
    {
        var (size, align) = T.StorageOf(t);
        align = Math.Max(align, 1);
        // (no variable at the frame base: the base itself, when it goes somewhere, is the whole frame: FrameSlots.cs)
        if (_frameSize == 0) _frameSize = 8;
        _frameSize = (_frameSize + align - 1) / align * align;
        int off = _frameSize;
        _frameSize += size;
        _usesFrame = true;
        (E.Fn.FrameSlots ??= []).Add((off, size));
        return off;
    }

    RuntimeFns Rt => D.Rt;
    public string FnName => E.Fn.Name;
    public bool FnProgramCode => E.Fn.ProgramCode;
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
        if (l.RefKind == RefKind.None && T.NeedsRc(t) && !Types.IsImmortalType(t))
        {
            var slot = v.InMemory ? new Slot { FrameOffset = v.Offset, Type = t } : new Slot { Local = v.Local, Type = t };
            // a local that owns a frame object (EscapeAnalysis.cs): its scope's end drops the object
            if (!v.InMemory && _frameLocals != null && _frameLocals.TryGetValue(l, out var ft)) slot.ReleaseMarker = D.FrameObjectFns(ft).drop;
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
        // (where each reference local's scope ends: FieldBorrows.cs looks for stores up to here)
        foreach (var o in s.Owned)
            if (o.Local >= 0 && o.ReleaseMarker == null) { E.Emit(Op.Nop, o.Local, 0, Driver.ScopeEnd); E.Body[^1] = E.Body[^1] with { Pos = -1 }; }
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    /// <summary>A release of a value whose type says exactly what class of object it is (a string, an array of
    /// numbers, a sealed class): the call carries the type (its second number, the type's id + 1), and where
    /// destroying such an object only frees it, the final pass writes the count's test and the call of that class's
    /// own destruction in its place (WasmModule.ExpandLeafReleases) instead of the dispatch on the object's type.</summary>
    void ReleaseOf(ITypeSymbol? t)
    {
        E.Call(Rt.Release);
        int hint = ExactTypeHint(t);
        if (hint != 0) E.Body[^1] = E.Body[^1] with { Imm2 = hint };
    }

    int ExactTypeHint(ITypeSymbol? t) => t == null ? 0 : ExactTypeHint(D, T.Resolve(t, Map));

    /// <summary>The same for a type that is already resolved (the runtime's own loops over an array's elements).</summary>
    internal static int ExactTypeHint(Driver D, ITypeSymbol t)
    {
        if (D.ThreadMode) return 0;
        if (Types.IsString(t)) return 2;
        if (t is IArrayTypeSymbol { Rank: 1 } at)
            return at.ElementType is INamedTypeSymbol { IsValueType: true } et && Types.PrimOf(et) != Prim.None ? D.TypeId(D.Types.Canonical(t)) + 1 : 0;
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true, IsGenericType: false } nt && !Types.IsImmortalType(nt))
        {
            for (var c = nt.ContainingType; c != null; c = c.ContainingType) if (c.IsGenericType) return 0;
            return D.TypeId(D.Types.Canonical(nt)) + 1;
        }
        return 0;
    }

    void ReleaseSlot(Slot s)
    {
        if (s.Local >= 0)
        {
            E.Get(s.Local);
            if (s.ReleaseMarker != null) E.Call(s.ReleaseMarker); else ReleaseOf(s.Type);
            E.I32(0); E.Set(s.Local);
        }
        else
        {
            if (Types.IsRef(s.Type))
            {
                E.Get(_fb); E.Load(Op.I32Load, s.FrameOffset); ReleaseOf(s.Type);
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
            if (!s.Moved) ReleaseSlot(s);
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
        // (a slot given back is one that held references: released and cleared at its statement's end, and found
        // clear by the code that uses it next. Only another such temporary may take it: one that holds no reference
        // is never cleared, and in a loop the slot's first user would find its bytes there as a reference)
        int off = T.NeedsRc(t) && _structPool.TryGetValue(key, out var st) && st.Count > 0 ? st.Pop() : AllocFrame(t);
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
        if (D.ThreadMode && CountedLoad(o) == Own.Owned) return;
        if (o is Own.Stable or Own.Unstable) E.Call(Rt.Retain);
    }

    /// <summary>Value on stack stays valid for the rest of the statement.</summary>
    Own ToTransient(Own o, ITypeSymbol t)
    {
        if (D.ThreadMode) o = CountedLoad(o);
        if (o == Own.Owned) { E.Tee(TempRef(t)); return Own.Unstable; }
        return o;
    }

    /// <summary>Value on stack stays valid across a call that may overwrite the memory it was read from.</summary>
    Own ToCallSafe(Own o, ITypeSymbol t)
    {
        if (D.ThreadMode) o = CountedLoad(o);
        if (o == Own.Owned) { E.Tee(TempRef(t)); return Own.Stable; }
        if (o == Own.Unstable && _lastFieldLoad is var (at, key) && at == E.Fn.Body.Count)
        {
            // a field of a stable object: the count may not be needed (FieldBorrows.cs decides at the end)
            var (retain, release) = D.NewFieldBorrow(E.Fn, key);
            E.Call(retain);
            int l = _refPool.Count > 0 ? _refPool.Pop() : E.AddLocal(ValType.I32);
            RegisterTemp(new Slot { Local = l, Type = t, ReleaseMarker = release });
            E.Tee(l);
            return Own.Stable;
        }
        if (o == Own.Unstable) { E.Call(Rt.Retain); E.Tee(TempRef(t)); return Own.Stable; }
        return o;
    }

    /// <summary>Where the last load of a reference field of a stable object ended (body length), and its key.</summary>
    (int at, string key)? _lastFieldLoad;

    void Drop(Own o, ITypeSymbol? t)
    {
        if (t != null && Types.IsVoid(t)) return;
        if (o == Own.Owned) ReleaseOf(t);
        else E.Op(Op.Drop);
    }

    // =====================================================================
    // statements
    // =====================================================================
    void EmitStatement(IOperation op)
    {
        int save = E.CurrentPos;
        if (op.Syntax is not BlockSyntax) E.CurrentPos = D.Position(op.Syntax) is var p && p >= 0 ? p : save;
        _stmts.Add((op, _temps.Count));
        try { EmitStatementHooks(op); EmitStatementCore(op); }
        catch (CompileException ex) when (ex.Location == null) { throw new CompileException(ex.Message, op.Syntax.GetLocation()); }
        _stmts.RemoveAt(_stmts.Count - 1);
        E.CurrentPos = save;
    }

    /// <summary>The statements being compiled, outermost first, each with how many lists of temporaries were open when
    /// it began: a temporary made now is let go before the innermost statement that began with fewer lists open ends.</summary>
    readonly List<(IOperation op, int temps)> _stmts = [];

    /// <summary>A local that is assigned again, but not while a temporary made now is held: its object stays the
    /// same, and alive, that long (the local owns it, or borrows it for its own scope). A field of it read into such a
    /// temporary can borrow as from a reference that stays put (FieldBorrows.cs looks for stores of the field).</summary>
    bool QuietLocalBase(IOperation op)
    {
        while (op is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } c) op = c.Operand;
        if (_closure != null || _machine != null || _temps.Count == 0) return false;
        if (op is not ILocalReferenceOperation { Local: { IsRef: false } l } || IsCaptured(l) || _addrTaken.Contains(l) || TypeOf(op).IsValueType) return false;
        IOperation? x = null;
        for (int i = _stmts.Count - 1; i >= 0; i--) if (_stmts[i].temps < _temps.Count) { x = _stmts[i].op; break; }
        if (x == null) return false;
        // (declared inside: a loop there gives it a new value each time round)
        var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (decl == null || x.Syntax.SyntaxTree != decl.SyntaxTree || x.Syntax.Span.Contains(decl.Span)) return false;
        foreach (var o in x.DescendantsAndSelf())
            if (o is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, l) && IsLocalWrite(lr)) return false;
        return true;
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
            case IConditionalOperation c when Fold(c.Condition) is null && FoldAfterEffect(c.Condition) is (IOperation effect, bool fixedValue):
                // `x && false` (`TryTake() && log.IsEnabled()`): x for its effects, then only the side taken
                BeginTemps(); EmitDiscard(effect); EndTemps();
                if (fixedValue) { PushScope(); EmitStatement(c.WhenTrue); PopScope(); }
                else if (c.WhenFalse != null) { PushScope(); EmitStatement(c.WhenFalse); PopScope(); }
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
            case ILockOperation lk when TypeOf(lk.LockedValue) is INamedTypeSymbol { Name: "Lock" } lockT && Types.FullName(lockT) == "System.Threading.Lock":
                {
                    // C# 13: lock on a System.Threading.Lock is `using (l.EnterScope()) { body }`
                    PushScope();
                    var enterScope = lockT.GetMembers("EnterScope").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
                    var scopeT = T.Canonical(enterScope.ReturnType);
                    int sc = TempStruct(scopeT);
                    BeginTemps();
                    var lo = EmitValue(lk.LockedValue);
                    int ll = E.AddLocal(ValType.I32);
                    ToOwned(lo); E.Set(ll); AddOwned(new Slot { Local = ll, Type = lockT });
                    EndTemps();
                    CallWith(D.CanonicalMethod(enterScope), false, () => E.Get(ll), () => { }, () => { E.Get(_fb); E.I32(sc); E.Op(Op.I32Add); }, lk.Syntax);
                    var dispose = ((INamedTypeSymbol)scopeT).GetMembers("Dispose").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
                    EmitTryFinallyCore(() => { PushScope(); EmitStatement(lk.Body); PopScope(); },
                        () => { CallWith(D.CanonicalMethod(dispose), false, () => { E.Get(_fb); E.I32(sc); E.Op(Op.I32Add); }, () => { }, null, lk.Syntax); });
                    PopScope();
                    break;
                }
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
        if (!s_noForwardGoto && GotosAllForward(ops, start, labeled))
        {
            // Every goto to these labels is before its label (`goto ReturnFound;` out of a loop, the labels at the
            // method's end): no dispatch is needed. Each label is the end of a block that began before the first
            // statement, and a goto to it is a branch out of that block; segments fall through in order.
            var fexit = E.Block();
            var fc = new Ctl { Kind = CtlKind.Block, Break = fexit, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
            _ctl.Add(fc);
            var ends = new Label[n];
            for (int k = n - 1; k >= 1; k--)
            {
                ends[k] = E.Block();
                for (IOperation? lo = ops[labeled[k - 1]]; lo is ILabeledOperation lop; lo = lop.Operation)
                {
                    _labels[lop.Label] = (fc, true);
                    _gotoForward[lop.Label] = ends[k];
                }
            }
            for (int k = 0; k < n; k++)
            {
                if (k > 0) E.End();
                EmitStatementsPlain(ops, k == 0 ? start : labeled[k - 1], k < labeled.Count ? labeled[k] : ops.Count);
            }
            _ctl.Remove(fc);
            E.End();
            return;
        }
        int st = E.AddLocal(ValType.I32);
        E.I32(0); E.Set(st);
        var exit = E.Block();
        var disp = E.Loop();
        var c = new Ctl { Kind = CtlKind.Block, Break = exit, Continue = disp, GotoState = st, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
        _ctl.Add(c);
        for (int k = 0; k < labeled.Count; k++)
            // (`A: B: stmt`: each label of a chain names the same segment)
            for (IOperation? lo = ops[labeled[k]]; lo is ILabeledOperation lop; lo = lop.Operation)
            {
                _labels[lop.Label] = (c, true);
                _gotoIndex[lop.Label] = k + 1;
            }
        var segs = new Label[n];
        for (int k = n - 1; k >= 0; k--) segs[k] = E.Block();
        E.Get(st); E.BrTable(segs, segs[0]);
        if (!s_noForwardGoto) c.GotoSegs = segs;
        for (int k = 0; k < n; k++)
        {
            E.End();
            c.GotoSeg = k;
            int from = k == 0 ? start : labeled[k - 1];
            int to = k < labeled.Count ? labeled[k] : ops.Count;
            if (k > 0)
                for (IOperation? lo = ops[from]; lo is ILabeledOperation lop; lo = lop.Operation) _gotoOwned[lop.Label] = _scopes[^1].Owned.Count;
            EmitStatementsPlain(ops, from, to);
        }
        _ctl.Remove(c);
        E.End(); E.End();
    }

    static readonly bool s_noForwardGoto = Environment.GetEnvironmentVariable("CS2WASM_NO_FORWARD_GOTO") == "1";
    /// <summary>The labels every goto reaches from before them (EmitLabeledStatements): the block each one ends.</summary>
    readonly Dictionary<ILabelSymbol, Label> _gotoForward = new(SymbolEqualityComparer.Default);

    /// <summary>Whether every goto to one of the block's labels is before the label in the text (and so, being in
    /// the label's scope, in a statement before it: a branch out of a block that ends at the label reaches it).</summary>
    static bool GotosAllForward(IReadOnlyList<IOperation> ops, int start, List<int> labeled)
    {
        var at = new Dictionary<ILabelSymbol, int>(SymbolEqualityComparer.Default);
        foreach (int i in labeled)
            for (IOperation? lo = ops[i]; lo is ILabeledOperation lop; lo = lop.Operation) at[lop.Label] = ops[i].Syntax.SpanStart;
        for (int i = start; i < ops.Count; i++)
            foreach (var d in ops[i].DescendantsAndSelf())
                if (d is IBranchOperation { BranchKind: BranchKind.GoTo } br && at.TryGetValue(br.Target, out int pos) && br.Syntax.SpanStart >= pos) return false;
        return true;
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
            // what follows a statement that never completes is unreachable: not compiled, so what only it calls
            // is not either (`if (GlobalizationMode.Invariant) return ...;` guards the rest of a method)
            if (NeverCompletes(ops[i])) return;
        }
    }

    bool NeverCompletes(IOperation op) => op switch
    {
        IReturnOperation or IThrowOperation => true,
        IExpressionStatementOperation { Operation: IThrowOperation } => true,
        IBranchOperation { BranchKind: BranchKind.Break or BranchKind.Continue or BranchKind.GoTo } => true,
        IBlockOperation { Operations: [.., var last] } bl => !bl.Operations.Any(o => o is ILabeledOperation) && NeverCompletes(last),
        IConditionalOperation c when Fold(c.Condition) is bool known => known ? NeverCompletes(c.WhenTrue) : c.WhenFalse != null && NeverCompletes(c.WhenFalse),
        IConditionalOperation { WhenFalse: { } wf } c => NeverCompletes(c.WhenTrue) && NeverCompletes(wf),
        _ => false,
    };

    /// <summary>fixed: nothing moves without a collector, so pinning is holding the object for the statement and
    /// taking the address of its data (arrays and strings: null or empty gives a null pointer, as in C#).</summary>
    /// <summary>The value is a local variable or a parameter of this function that nothing writes or moves out of
    /// inside the statement: whatever it holds when the statement starts, it holds, alive, until the statement ends
    /// (`string result = ...; fixed (char* p = result) { ... }`).</summary>
    bool KeptThrough(IOperation value, SyntaxNode statement)
    {
        if (_machine != null || _closure != null) return false;
        ISymbol? sym = value switch
        {
            ILocalReferenceOperation { Local: { IsRef: false } l } when !IsCaptured(l) && !_addrTaken.Contains(l) => l,
            IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } q } when !IsCaptured(q) && !_addrTaken.Contains(q) && !_addrTaken.Contains(q.OriginalDefinition)
                && !_assignedParams.Contains(q) && !_assignedParams.Contains(q.OriginalDefinition) && !IsPrimaryParamHere(q) => q,
            _ => null,
        };
        if (sym == null) return false;
        // (a parameter this copy of the method consumes is moved into a field: OwnedParams.cs)
        if (sym is IParameterSymbol ps && Inst is { OwnedParams: var om } && (om & (1 << ps.Ordinal)) != 0) return false;
        var span = statement.Span;
        if (sym is ILocalSymbol local)
        {
            if (LocalWrites().TryGetValue(local, out _) && _localWritePositions.TryGetValue(local, out var ws) && ws.Any(p => span.Contains(p))) return false;
            foreach (var mv in _localMoves) if (mv is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, local) && span.Contains(lr.Syntax.SpanStart)) return false;
        }
        return true;
    }

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
                // (a variable that keeps its value for the whole statement keeps the object: no count of the pointer's own)
                if (o != Own.Owned && KeptThrough(init, fs)) E.Set(h);
                else { ToOwned(o); E.Set(h); AddOwned(new Slot { Local = h, Type = it }); }
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
            else if (!v.InMemory && Types.IsRef(v.Type) && WrittenBorrow(d.Symbol) is { } wb)
            {
                // a local assigned again whose every value can be borrowed (LocalBorrows.cs)
                var bo = EmitValueAs(init, v.Type);
                EmitBorrowMark(v, wb, bo);
                E.Set(v.Local);
            }
            else if (!v.InMemory && Types.IsRef(v.Type) && FieldBorrowCandidate(d.Symbol, init) is { } fkey) EmitFieldBorrow(v, init, fkey, d.Symbol);
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
                    bool move = TakeFresh(rt);
                    int src = Scratch(ValType.I32); E.Set(src);
                    E.Get(_structRetLocal); E.Get(src);
                    if (move) MoveStruct(rt, assign: false); else CopyStructInit(rt);
                    FreeScratch(ValType.I32, src);
                    hasValue = false;
                }
                else
                {
                    var o = EmitValueAs(value, rt);
                    // (a body that returns `this` borrowed: Driver.ReturnsThis)
                    if (Types.IsRef(rt) && !(Inst?.ReturnsBorrowed == true && _closure == null)) ToOwned(o);
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
        if (_gotoForward.TryGetValue(br.Target, out var fwd))
        {
            int fi = _ctl.IndexOf(t.ctl);
            for (int i = _ctl.Count - 1; i > fi; i--)
                if (_ctl[i].Kind == CtlKind.TryFinally) EmitFinallyInline(i);
            for (int i = _temps.Count - 1; i >= t.ctl.TempDepth; i--) foreach (var s in _temps[i]) ReleaseSlot(s);
            for (int i = _scopes.Count - 1; i >= t.ctl.ScopeDepth; i--)
                for (int j = _scopes[i].Owned.Count - 1; j >= 0; j--) ReleaseSlot(_scopes[i].Owned[j]);
            E.Br(fwd);
            return;
        }
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
            // (forward: the segment's block is still open, and ends where the segment begins)
            if (t.ctl.GotoSegs is { } gs && seg > t.ctl.GotoSeg && seg < gs.Length) { E.Br(gs[seg]); return; }
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
            // (a variable the body may reassign does not keep the array alive: the loop holds it)
            else { if (o == Own.Unstable || o == Own.Stable && !IsStableBase(coll)) { E.Call(Rt.Retain); E.Set(arr); AddOwned(new Slot { Local = arr, Type = collType }); } else E.Set(arr); }
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
            AssignLoopVariable(fe, elemType, borrowElement: true, produce: () =>
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
        if (s_listForEach && _machine == null && ListParts(collType) is var (lItems, lSize, lVersion, lElem, lEnum, lMoveNext)
            && fe.LoopControlVariable is not IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None })
        {
            // A List<T>: what its enumerator does, without the enumerator (the same checks in the same order):
            //   version = list._version; for (i = 0; ; i++) { if (version != list._version) throw; if (i >= list._size) break; x = list._items[i]; body }
            // The element comes straight from the list's array: one copy where the enumerator made two (into its
            // Current, then into the variable), and a reference borrows from the array as an array's element does
            // (section 4.1), unless something the loop runs stores elements or the list's array.
            BeginTemps();
            var o = EmitValue(coll);
            int list = E.AddLocal(ValType.I32);
            if (o == Own.Owned) { E.Set(list); AddOwned(new Slot { Local = list, Type = collType }); }
            else { if (o == Own.Unstable || o == Own.Stable && !IsStableBase(coll)) { E.Call(Rt.Retain); E.Set(list); AddOwned(new Slot { Local = list, Type = collType }); } else E.Set(list); }
            EndTemps();
            E.Get(list); NullCheck();
            int version = E.AddLocal(ValType.I32), i = E.AddLocal(ValType.I32), items = E.AddLocal(ValType.I32);
            E.Get(list); E.Load(Op.I32Load, lVersion.Offset); E.Set(version);
            E.I32(0); E.Set(i);
            var brk = E.Block();
            var top = E.Loop();
            E.Get(list); E.Load(Op.I32Load, lVersion.Offset); E.Get(version); E.Op(Op.I32Ne);
            E.If();
            {
                // the list was changed: the enumerator's own MoveNext throws (its frames are the trace's), on an
                // enumerator in the state this loop is in
                BeginTemps();
                var el = T.LayoutOf(lEnum);
                int tmp = TempStruct(lEnum);
                E.Get(_fb); E.I32(tmp); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(lEnum).size); E.Op(Op.MemoryFill);
                foreach (var f in el.Fields)
                {
                    int src = f.Symbol.Name switch { "_list" => list, "_index" => i, "_version" => version, _ => -1 };
                    if (src < 0) continue;
                    E.Get(_fb); E.Get(src); E.Store(Op.I32Store, tmp + f.Offset);
                    // (the enumerator's count of the list, which its release at the statement's end gives back)
                    if (f.Symbol.Name == "_list") { E.Get(list); E.Call(Rt.Retain); E.Op(Op.Drop); }
                }
                CallWith(D.CanonicalMethod(lMoveNext), false, () => { E.Get(_fb); E.I32(tmp); E.Op(Op.I32Add); }, () => { }, null, fe.Syntax);
                E.Op(Op.Drop);
                EndTemps();
            }
            E.End();
            E.Get(i); E.Get(list); E.Load(Op.I32Load, lSize.Offset); E.Op(Op.I32GeU); E.BrIf(brk);
            // (the array as it is now, and the index within it: a list another thread changes may have a shorter one)
            E.Get(list); E.Load(Op.I32Load, lItems.Offset); E.Set(items);
            E.Get(items); NullCheck();
            E.Get(i); E.Get(items); E.Load(Op.I32Load, 12); E.Op(Op.I32GeU);
            E.If(); E.Call(D.LibMethod("Cs2Wasm.Runtime.ThrowHelpers", "IndexOutOfRange", 0).Fn); E.Op(Op.Unreachable); E.End();
            var cont = E.Block();
            var c = PushLoop(fe, brk, cont);
            PushScope();
            EnterDisplay(fe);
            var listType = (INamedTypeSymbol)collType;
            string itemsKey = Driver.FieldKey(listType, lItems.Symbol);
            D.NoteBorrowField(itemsKey, lItems.Symbol, lItems.Type);
            AssignLoopVariable(fe, lElem, borrowElement: !D.ThreadMode, borrowKeys: Driver.ElementsKey + "\n" + itemsKey, produce: () =>
            {
                E.Get(items); E.Get(i); var sz = T.StorageOf(lElem).size;
                if (sz != 1) { E.I32(sz); E.Op(Op.I32Mul); }
                E.Op(Op.I32Add);
                if (Types.IsStruct(lElem)) { E.I32(Types.ArrayData); E.Op(Op.I32Add); return Own.None; }
                E.Load(Types.LoadOp(lElem), Types.ArrayData);
                return Types.IsRef(lElem) ? Own.Unstable : Own.None;
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
        if (Types.InlineArrayLength(collType) is int inlineLength && fe.LoopControlVariable is not IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None })
        {
            // an [InlineArray(n)] struct: its n elements in place, one after another (C# goes over it as a span)
            var elemType = T.LayoutOf(collType).Fields[0].Type;
            BeginTemps();
            EmitValue(coll);
            int baseAddr = E.AddLocal(ValType.I32); E.Set(baseAddr);
            int i = E.AddLocal(ValType.I32);
            E.I32(0); E.Set(i);
            var brk = E.Block();
            var top = E.Loop();
            E.Get(i); E.I32(inlineLength); E.Op(Op.I32GeU); E.BrIf(brk);
            var cont = E.Block();
            var c = PushLoop(fe, brk, cont);
            PushScope();
            EnterDisplay(fe);
            AssignLoopVariable(fe, elemType, borrowElement: false, produce: () =>
            {
                E.Get(baseAddr); E.Get(i); var sz = T.StorageOf(elemType).size;
                if (sz != 1) { E.I32(sz); E.Op(Op.I32Mul); }
                E.Op(Op.I32Add);
                if (Types.IsStruct(elemType)) return Own.None;
                E.Load(Types.LoadOp(elemType));
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
            EndTemps();
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
            // (a struct enumerator from an abstract, virtual or interface GetEnumerator of a class or interface: the
            // call dispatches, as for a class enumerator; Roslyn's Rope, AbstractLookupSymbolsInfo.IArityEnumerable)
            bool dispatch = !collType.IsValueType && (getEnum.IsVirtual || getEnum.IsAbstract || getEnum.IsOverride || getEnum.ContainingType.TypeKind == TypeKind.Interface);
            // (an extension GetEnumerator takes the collection as its argument)
            if (getEnum.IsStatic) CallWith(SubstMethod(getEnum), false, null, () => ReceiverFromLocal(collLocal, collType, coll), () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }, fe.Syntax);
            else EmitCallResolved(SubstMethod(getEnum), () => ReceiverFromLocal(collLocal, collType, coll), [], dispatch, intoAddr: () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); });
            enumLocal = -1;
        }
        else
        {
            enumLocal = E.AddLocal(ValType.I32);
            var r = getEnum.IsStatic ? CallWith(SubstMethod(getEnum), false, null, () => ReceiverFromLocal(collLocal, collType, coll), null, fe.Syntax)
                : EmitCallResolved(SubstMethod(getEnum), () => ReceiverFromLocal(collLocal, collType, coll), [], getEnum.IsVirtual || getEnum.IsAbstract || getEnum.ContainingType.TypeKind == TypeKind.Interface);
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
            if (fe.LoopControlVariable is IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } refDecl && (getter.ReturnsByRef || getter.ReturnsByRefReadonly))
            {
                // foreach (ref T x in span): x refers to the element Current returns a reference to
                BeginTemps();
                var save = _wantRefReturnAddress; _wantRefReturnAddress = true;
                try { EmitCallResolved(SubstMethod(getter), Recv, [], false); }
                finally { _wantRefReturnAddress = save; }
                E.Set(LocalVar(refDecl.Symbol).AddrLocal);
                EndTemps();
            }
            else
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
                    // an enumerator not known to be IDisposable (IEnumerator's) is disposed only if it is one at run
                    // time (C#: `if (e is IDisposable d) d.Dispose()`; ArrayEnumerator is not)
                    bool known = Types.FullName(enumType) == "System.IDisposable" || enumType.AllInterfaces.Any(i => Types.FullName(i) == "System.IDisposable")
                        || dispose.ContainingType.TypeKind != TypeKind.Interface;
                    Recv();
                    if (!known) E.Call(Rt.IsInst(T.Canonical(dispose.ContainingType)));
                    E.If();
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
    static readonly bool s_listForEach = Environment.GetEnvironmentVariable("CS2WASM_NO_LIST_FOREACH") != "1";

    /// <summary>The parts of the library's List&lt;T&gt; a `foreach` over one reads (EmitForEach): its array, size and
    /// version fields, T, and its enumerator's type and MoveNext. Null for any other type.</summary>
    (FieldSlot items, FieldSlot size, FieldSlot version, ITypeSymbol elem, INamedTypeSymbol enumerator, IMethodSymbol moveNext)? ListParts(ITypeSymbol t)
    {
        if (t is not INamedTypeSymbol { Name: "List", Arity: 1, TypeKind: TypeKind.Class } nt || nt.ContainingType != null
            || nt.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic") return null;
        var elem = T.Canonical(nt.TypeArguments[0]);
        if (elem is ITypeParameterSymbol || elem.IsRefLikeType) return null;
        var fields = T.LayoutOf(nt).Fields;
        FieldSlot? Field(string name) => fields.FirstOrDefault(f => f.Symbol.Name == name);
        if (Field("_items") is not { } items || Field("_size") is not { } size || Field("_version") is not { } version) return null;
        if (items.Type is not IArrayTypeSymbol { Rank: 1 }) return null;
        if (nt.GetTypeMembers("Enumerator").FirstOrDefault() is not { } en) return null;
        en = (INamedTypeSymbol)T.Canonical(en);
        if (en.GetMembers("MoveNext").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0) is not { } moveNext) return null;
        var names = T.LayoutOf(en).Fields.Select(f => f.Symbol.Name).ToHashSet();
        if (!names.Contains("_list") || !names.Contains("_index") || !names.Contains("_version")) return null;
        return (items, size, version, elem, en, moveNext);
    }

    void AssignLoopVariable(IForEachLoopOperation fe, ITypeSymbol valueType, Func<Own> produce, IOperation? conversion = null, bool borrowElement = false, string? borrowKeys = null)
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
            // an array's element borrows from the array unless something that can run in the loop stores elements
            // (section 4.1: a foreach element borrows from its source; FieldBorrows.cs decides at the end)
            if (borrowElement && o is Own.Unstable or Own.Stable && Types.IsRef(v.Type) && !v.InMemory && !_addrTaken.Contains(sym) && _closure == null && _machine == null)
            {
                var marker = D.Module.AddFunction("borrow? []", new FuncType([ValType.I32], [ValType.I32]));
                E.Call(marker);
                E.Set(v.Local);
                D.FieldBorrows.Add((E.Fn, v.Local, borrowKeys ?? Driver.ElementsKey, marker));
            }
            else StoreVar(v, o);
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
        else if (Types.IsStruct(vt))
        {
            // a struct value is copied into a slot of the switch's own: its temporary goes back to the pool at once,
            // and the clauses' tests use temporaries too (`switch (this, other)`)
            int off = AllocFrame(vt);
            bool move = TakeFresh(vt);
            int src = E.AddLocal(ValType.I32); E.Set(src);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Get(src);
            if (move) MoveStruct(vt, assign: false); else CopyStructInit(vt);
            if (T.NeedsRc(vt)) AddOwned(new Slot { FrameOffset = off, Type = vt });
            val = E.AddLocal(ValType.I32); E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Set(val);
        }
        else { val = E.AddLocal(Types.WasmType(vt) ?? ValType.I32); E.Set(val); }
        EndTemps();
        var end = E.Block();
        var c = new Ctl { Kind = CtlKind.Switch, Break = end, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
        _ctl.Add(c);
        _labels[sw.ExitLabel] = (c, false);
        // goto case / goto default: sections inside a dispatch loop; a state of -1 means "test the clauses"
        var clauseLabels = new Dictionary<ILabelSymbol, int>(SymbolEqualityComparer.Default);
        for (int i = 0; i < sw.Cases.Length; i++) foreach (var cl in sw.Cases[i].Clauses) if (cl.Label != null) clauseLabels[cl.Label] = i;
        // a section's statements split at its labels into segments (goto into the middle of a section, from
        // another: `goto convOperatorName;`); segments are numbered across sections, a section's first is its own
        var segStart = new List<(int sec, int from)>();
        var secFirst = new int[sw.Cases.Length];
        var innerLabels = new List<(ILabelSymbol lbl, int seg)>();
        for (int i = 0; i < sw.Cases.Length; i++)
        {
            secFirst[i] = segStart.Count;
            segStart.Add((i, 0));
            var body = sw.Cases[i].Body;
            for (int j = 1; j < body.Length; j++)
                if (body[j] is ILabeledOperation lo) { innerLabels.Add((lo.Label, segStart.Count)); segStart.Add((i, j)); }
            if (body.Length > 0 && body[0] is ILabeledOperation lo0) innerLabels.Add((lo0.Label, secFirst[i]));
        }
        bool gotoCase = innerLabels.Count > 0 || sw.Cases.Any(cs => cs.Body.Any(b => b.DescendantsAndSelf().OfType<IBranchOperation>().Any(br => clauseLabels.ContainsKey(br.Target))));
        int sect = -1;
        if (gotoCase)
        {
            sect = E.AddLocal(ValType.I32);
            E.I32(-1); E.Set(sect);
            c.Continue = E.Loop();
            c.GotoState = sect;
            foreach (var (lbl, i) in clauseLabels) { _labels[lbl] = (c, true); _gotoIndex[lbl] = secFirst[i]; }
            foreach (var (lbl, k) in innerLabels) { _labels[lbl] = (c, true); _gotoIndex[lbl] = k; }
        }
        var labels = new Label[segStart.Count];
        for (int i = segStart.Count - 1; i >= 0; i--) labels[i] = E.Block();
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
                        BeginTemps(); EmitEqualsConst(val, vt, sv.Value); EndTemps(ValType.I32); E.BrIf(labels[secFirst[i]]); break;
                    case IPatternCaseClauseOperation pc:
                        BeginTemps();
                        EmitPattern(pc.Pattern, val, vt);
                        if (pc.Guard != null) { E.If(ValType.I32); EmitCondition(pc.Guard); E.Else(); E.I32(0); E.End(); }
                        EndTemps(ValType.I32);
                        E.BrIf(labels[secFirst[i]]); break;
                    case IRelationalCaseClauseOperation rc:
                        BeginTemps(); E.Get(val); EmitValue(rc.Value); EmitCompare(rc.Relation, vt); EndTemps(ValType.I32); E.BrIf(labels[secFirst[i]]); break;
                    default: throw new CompileException("unsupported case clause", clause.Syntax.GetLocation());
                }
            }
        }
        if (defaultIdx >= 0) E.Br(labels[secFirst[defaultIdx]]); else E.Br(end);
        for (int k = 0; k < segStart.Count; k++)
        {
            E.End(); // close block labels[k]
            var (sec, from) = segStart[k];
            var body = sw.Cases[sec].Body;
            bool last = k + 1 == segStart.Count || segStart[k + 1].sec != sec;
            int to = last ? body.Length : segStart[k + 1].from;
            if (from == 0) PushScope();
            if (body.Length > 0 && body[from] is ILabeledOperation inner) _gotoOwned[inner.Label] = _scopes[^1].Owned.Count;
            EmitStatementsPlain(body, from, to);
            if (!last) continue; // falls into the section's next segment
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
            E.Call(D.StringEquals.Fn);
            return;
        }
        if (IsNullable(vt))
        {
            // `case null:` is !HasValue; `case c:` is HasValue && Value == c (the value is the T?'s address)
            var c = constant;
            while (c is IConversionOperation cv && cv.Type is { } cvt && IsNullable(cvt)) c = cv.Operand;
            if (c.ConstantValue is { HasValue: true, Value: null } || c.Type == null) { E.Get(val); NullableHas(vt); E.Op(Op.I32Eqz); return; }
            var u = Underlying(vt);
            E.Get(val); NullableHas(vt);
            E.If(ValType.I32);
            int uv = E.AddLocal(Types.WasmType(u) ?? ValType.I32);
            E.Get(val); NullableVal(vt); E.Set(uv);
            EmitEqualsConst(uv, u, c);
            E.Else(); E.I32(0); E.End();
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
                // one pass (a program none of whose own code has a filter): the filter runs here, when the exception
                // has reached its catch. What the filter declares is the handler's too
                // (`when (Find(e, out var handler))`), so both are in one scope; `keep` takes the count the
                // exception came with when the filter holds, and is released with the scope.
                E.If();
                PushScope();
                int keepF = E.AddLocal(ValType.I32);
                E.I32(0); E.Set(keepF); AddOwned(new Slot { Local = keepF, Type = D.ObjectType });
                BindCatchVariable(cc, ex, retain: true);
                BeginTemps(); EmitCondition(cc.Filter); EndTemps(ValType.I32);
                E.If();
                E.Get(ex); E.Set(keepF);
                var cf = new Ctl { Kind = CtlKind.Catch, ExnLocal = exn, ExPtrLocal = ex, ScopeDepth = _scopes.Count, TempDepth = _temps.Count };
                _ctl.Add(cf);
                EmitStatement(cc.Handler);
                _ctl.Remove(cf);
                var fs = _scopes[^1];
                for (int i = fs.Owned.Count - 1; i >= 0; i--) ReleaseSlot(fs.Owned[i]);
                E.Br(done);
                E.End();
                PopScope();
                E.End();
                continue;
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
        if (s_expNoThrow && D.IsLibrary(M)) { E.Op(Op.Unreachable); return; } // EXPERIMENT: measurement only
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
            else if (Types.IsStruct(rt))
            {
                // a struct resource (Lock.Scope): its own frame slot, so the body's temporaries cannot reuse it
                int src = Scratch(ValType.I32); E.Set(src);
                int fo = AllocFrame(rt);
                _usesFrame = true;
                E.Get(_fb); E.I32(fo); E.Op(Op.I32Add); E.Tee(l); E.Get(src); CopyStructInit(rt);
                FreeScratch(ValType.I32, src);
                if (T.NeedsRc(rt)) AddOwned(new Slot { FrameOffset = fo, Type = rt });
            }
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
                    foreach (var f in fi.InitializedFields)
                    {
                        // a ref field (`ref int _first = ref first;`): the slot takes the address
                        if (f.RefKind != RefKind.None) { E.Get(_thisLocal); EmitAddress(fi.Value); E.Store(Op.I32Store, T.Field(f, Map).Offset); }
                        else EmitStoreField(f, () => E.Get(_thisLocal), fi.Value, initializer: true);
                    }
                    break;
                case IPropertyInitializerOperation pi:
                    foreach (var p in pi.InitializedProperties)
                    {
                        var bf = Driver.AutoPropertyField(p) ?? throw new CompileException("property initializer without backing field");
                        EmitStoreField(bf, () => E.Get(_thisLocal), pi.Value, initializer: true);
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

    /// <summary>A static initializer whose fields no compiled code reads, and whose value has no effect to keep:
    /// constants, null, delegates (lambdas, method groups), arrays of constants.</summary>
    bool UnreadAndPure(INamedTypeSymbol t, IOperation op)
    {
        IEnumerable<ISymbol> fields = op switch
        {
            IFieldInitializerOperation fi => fi.InitializedFields,
            IPropertyInitializerOperation pi => pi.InitializedProperties.Select(p => (ISymbol?)Driver.AutoPropertyField(p)).Where(f => f != null)!,
            _ => [],
        };
        var value = (op as ISymbolInitializerOperation)?.Value;
        if (value == null || !fields.Any()) return false;
        var owner = T.Canonical(t);
        if (fields.Any(f => D.StaticReads.Contains(Driver.StaticKey(owner, f)))) return false;
        s_pureDriver = D;
        return PureInit(value, t);
    }

    /// <summary>Whether an initializer's value has no effect but making the value (so an unread field's goes): constants,
    /// defaults, lambdas, constant-size arrays of such; calls and conversions a library's manifest declares
    /// (`pure-init`: lookups whose result is their only effect, as GodotSharp's method binds and names) on such
    /// arguments; a static readonly field of a type whose static initialization is itself such.</summary>
    static bool PureInit(IOperation v, INamedTypeSymbol? self = null) => PureInit(v, self == null ? [] : [self.OriginalDefinition]);

    static bool PureInit(IOperation v, HashSet<INamedTypeSymbol> seen)
    {
        while (v is IConversionOperation { OperatorMethod: null } c) v = c.Operand;
        return v switch
        {
            { ConstantValue.HasValue: true } => true,
            ITypeOfOperation => true,
            IConversionOperation { OperatorMethod: { } om } cv => Library.PureInitMethods.Contains(Library.PureInitKey(om)) && PureInit(cv.Operand, seen),
            IInvocationOperation { Instance: null } inv => Library.PureInitMethods.Contains(Library.PureInitKey(inv.TargetMethod)) && inv.Arguments.All(a => PureInit(a.Value, seen)),
            IFieldReferenceOperation { Instance: null, Field: { IsReadOnly: true } f } => PureType(f.ContainingType, seen),
            IDefaultValueOperation => true,
            IDelegateCreationOperation { Target: IAnonymousFunctionOperation or IMethodReferenceOperation { Instance: null } } => true,
            IAnonymousFunctionOperation => true,
            IArrayCreationOperation { DimensionSizes: var ds, Initializer: var init } =>
                ds.All(d => d.ConstantValue.HasValue) && (init == null || init.ElementValues.All(e => PureInit(e, seen))),
            _ => false,
        };
    }

    /// <summary>The left-out lookups so far, in order: a run of the same lookup on (a field of the class, a name, a
    /// constant) is one call of its batch form over tables (`pure-init-batch`), the others one call each.</summary>
    void FlushLookups(INamedTypeSymbol t, List<(IInvocationOperation inv, SyntaxNode at)> pending)
    {
        int i = 0;
        while (i < pending.Count)
        {
            int j = i;
            var first = pending[i].inv;
            if (Library.BatchInitMethods.TryGetValue(Library.PureInitKey(first.TargetMethod), out var batchName) && BatchShape(t, first) is { } f0)
                while (j + 1 < pending.Count && SymbolEqualityComparer.Default.Equals(pending[j + 1].inv.TargetMethod, first.TargetMethod)
                    && BatchShape(t, pending[j + 1].inv) is { } fj && SymbolEqualityComparer.Default.Equals(fj, f0)) j++;
            else batchName = null;
            E.CurrentPos = D.Position(pending[i].at);
            BeginTemps();
            if (batchName != null && j > i) EmitLookupBatch(first, pending.Skip(i).Take(j - i + 1).Select(p => p.inv).ToList(), batchName);
            else { j = i; EmitCheckedLookup(first); }
            EndTemps();
            i = j + 1;
        }
        pending.Clear();
    }

    /// <summary>For a lookup (type field of the class, name, constant): the type field.</summary>
    IFieldSymbol? BatchShape(INamedTypeSymbol t, IInvocationOperation inv)
    {
        if (inv.Arguments.Length != 3) return null;
        var a0 = Strip(inv.Arguments[0].Value);
        if (a0 is not IFieldReferenceOperation { Instance: null, Field: var f } || !SymbolEqualityComparer.Default.Equals(f.ContainingType.OriginalDefinition, t.OriginalDefinition)) return null;
        if (NameOf(inv.Arguments[1].Value) == null || !inv.Arguments[2].Value.ConstantValue.HasValue) return null;
        return f;
    }

    static IOperation Strip(IOperation v) { while (v is IConversionOperation { OperatorMethod: null } c) v = c.Operand; return v; }

    /// <summary>A name argument as text: a string made a StringName, directly or through another type's pure field.</summary>
    string? NameOf(IOperation v)
    {
        v = Strip(v);
        if (v is IFieldReferenceOperation { Instance: null, Field: { IsReadOnly: true } f } && Driver.FieldInitializer(f) is { } fi
            && D.ModelFor(fi.SyntaxTree).GetOperation(fi) is IFieldInitializerOperation { Value: var fv }) v = Strip(fv);
        return v is IConversionOperation { OperatorMethod: { } om, Operand.ConstantValue: { HasValue: true, Value: string s } }
            && Library.PureInitMethods.Contains(Library.PureInitKey(om)) ? s : null;
    }

    /// <summary>A run of lookups as one call of the batch form: (type, names as UTF-16, their lengths, constants, count).</summary>
    void EmitLookupBatch(IInvocationOperation first, List<IInvocationOperation> run, string batchName)
    {
        var owner = first.TargetMethod.ContainingType;
        var helper = owner.GetMembers(batchName).OfType<IMethodSymbol>().First();
        var names = new List<byte>(); var lengths = new List<byte>(); var consts = new List<byte>();
        foreach (var inv in run)
        {
            var n = NameOf(inv.Arguments[1].Value)!;
            names.AddRange(System.Text.Encoding.Unicode.GetBytes(n));
            lengths.AddRange(BitConverter.GetBytes(n.Length));
            consts.AddRange(BitConverter.GetBytes(Convert.ToUInt64(inv.Arguments[2].Value.ConstantValue.Value)));
        }
        var nameBlob = D.DataBlob(names.Count == 0 ? new byte[2] : [.. names], 2);
        var lenBlob = D.DataBlob([.. lengths], 4);
        var constBlob = D.DataBlob([.. consts], 8);
        var h = SubstMethod(helper);
        CallWith(h, false, null, () =>
        {
            PushArg(h.Parameters[0], first.Arguments[0].Value);
            E.I32Addr(nameBlob); E.I32Addr(lenBlob); E.I32Addr(constBlob); E.I32(run.Count);
        }, null, first.Syntax);
    }

    /// <summary>The call of an initializer's value, if it is a lookup that can fail (`pure-init-checked`).</summary>
    static IInvocationOperation? CheckedLookup(IOperation v)
    {
        while (v is IConversionOperation { OperatorMethod: null } c) v = c.Operand;
        return v is IInvocationOperation { Instance: null } inv && Library.CheckedInitMethods.Contains(Library.PureInitKey(inv.TargetMethod)) ? inv : null;
    }

    /// <summary>A failing lookup of a left-out initializer: called, its result dropped; an argument read from another
    /// pure static field is that field's initializer instead (so the field can go too: GodotSharp's MethodName.X).</summary>
    void EmitCheckedLookup(IInvocationOperation inv)
    {
        var m = SubstMethod(inv.TargetMethod);
        var own = CallWith(m, false, null, () =>
        {
            foreach (var a in inv.Arguments)
            {
                var v = a.Value;
                var inner = v;
                while (inner is IConversionOperation { OperatorMethod: null } c) inner = c.Operand;
                if (inner is IFieldReferenceOperation { Instance: null, Field: { IsReadOnly: true } f } && !SymbolEqualityComparer.Default.Equals(f.ContainingType.OriginalDefinition, _staticInitType?.OriginalDefinition)
                    && Driver.FieldInitializer(f) is { } fi
                    && D.ModelFor(fi.SyntaxTree).GetOperation(fi) is IFieldInitializerOperation { Value: var fv } && PureInit(fv, f.ContainingType))
                    v = fv;
                PushArg(a.Parameter!, v);
            }
        }, null, inv.Syntax);
        if (!m.ReturnsVoid)
        {
            if (Types.IsRef(T.Resolve(m.ReturnType, Map)) && own == Own.Owned) E.Call(Rt.Release);
            else E.Op(Op.Drop);
        }
    }

    /// <summary>A type whose static initialization makes values only: no static constructor, pure initializers.</summary>
    static bool PureType(INamedTypeSymbol t, HashSet<INamedTypeSymbol> seen)
    {
        if (!seen.Add(t.OriginalDefinition)) return true;
        if (t.StaticConstructors.Any(c => !c.IsImplicitlyDeclared)) return false;
        foreach (var m in t.GetMembers())
        {
            if (!m.IsStatic) continue;
            var init = m switch
            {
                IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared => Driver.FieldInitializer(f),
                IPropertySymbol p => Driver.PropertyInitializer(p),
                _ => null,
            };
            if (init == null) continue;
            if (s_pureDriver?.ModelFor(init.SyntaxTree).GetOperation(init) is not ISymbolInitializerOperation op || !PureInit(op.Value, seen)) return false;
        }
        return true;
    }
    [ThreadStatic] static Driver? s_pureDriver;

    /// <summary>How many of the type's static initializers the next compilation leaves out (unread, pure).</summary>
    internal static int UnreadPureInits(Driver d, INamedTypeSymbol t)
    {
        s_pureDriver = d;
        // (which of a type's initializers are pure never changes: found once, then each round only asks what is read.
        // A program with a large library has thousands of types with static fields, asked about round after round)
        if (!d.PureInitKeys.TryGetValue(t, out var groups))
        {
            var list = new List<string[]>();
            PureInitGroups(d, t, list);
            d.PureInitKeys[t] = groups = list.ToArray();
        }
        int count = 0;
        foreach (var keys in groups)
        {
            bool read = false;
            foreach (var k in keys) if (d.StaticReads.Contains(k)) { read = true; break; }
            if (!read) count++;
        }
        return count;
    }

    static void PureInitGroups(Driver d, INamedTypeSymbol t, List<string[]> groups)
    {
        var owner = d.Types.Canonical(t);
        foreach (var m in t.GetMembers())
        {
            if (!m.IsStatic) continue;
            var init = m switch
            {
                IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared => Driver.FieldInitializer(f),
                IPropertySymbol p => Driver.PropertyInitializer(p),
                _ => null,
            };
            if (init == null || d.ModelFor(init.SyntaxTree).GetOperation(init) is not ISymbolInitializerOperation op) continue;
            IEnumerable<ISymbol> fields = op switch
            {
                IFieldInitializerOperation fi => fi.InitializedFields,
                IPropertyInitializerOperation pi => pi.InitializedProperties.Select(p => (ISymbol?)Driver.AutoPropertyField(p)).Where(f => f != null)!,
                _ => [],
            };
            if (fields.Any() && PureInit(op.Value, t)) groups.Add(fields.Select(f => Driver.StaticKey(owner, f)).ToArray());
        }
    }

    /// <summary>Whether the second compilation would leave out any of the type's static initializers (else its
    /// first compilation stands).</summary>
    internal static bool AnyUnreadPureInit(Driver d, INamedTypeSymbol t)
    {
        var owner = d.Types.Canonical(t);
        foreach (var m in t.GetMembers())
        {
            if (!m.IsStatic) continue;
            var init = m switch
            {
                IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared => Driver.FieldInitializer(f),
                IPropertySymbol p => Driver.PropertyInitializer(p),
                _ => null,
            };
            if (init == null || d.ModelFor(init.SyntaxTree).GetOperation(init) is not ISymbolInitializerOperation op) continue;
            IEnumerable<ISymbol> fields = op switch
            {
                IFieldInitializerOperation fi => fi.InitializedFields,
                IPropertyInitializerOperation pi => pi.InitializedProperties.Select(p => (ISymbol?)Driver.AutoPropertyField(p)).Where(f => f != null)!,
                _ => [],
            };
            if (fields.Any() && !fields.Any(f => d.StaticReads.Contains(Driver.StaticKey(owner, f))) && PureInit(op.Value)) return true;
        }
        return false;
    }

    /// <summary>The type whose static initializer is being compiled.</summary>
    INamedTypeSymbol? _staticInitType;

    void CompileStaticInitBody(INamedTypeSymbol t, IAddress flag, IMethodSymbol? cctor)
    {
        var pendingLookups = new List<(IInvocationOperation inv, SyntaxNode at)>();
        _staticInitType = t;
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
            if (D.SkipUnreadInits && op != null && UnreadAndPure(t, op))
            {
                // a lookup that can fail still runs where it was, its result dropped (the same failure, at the same
                // point of the initializer)
                if (op is ISymbolInitializerOperation { Value: var lv } && CheckedLookup(lv) is { } inv) pendingLookups.Add((inv, init));
                continue;
            }
            FlushLookups(t, pendingLookups);
            E.CurrentPos = D.Position(init);
            if (op != null) PreScan(op); // `out var` in an initializer declares a variable whose address is taken
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
        FlushLookups(t, pendingLookups);
        if (cctor != null && Driver.HasBody(cctor))
        {
            var (body, syntax) = GetBody(cctor);
            PreScan(body);
            if (body is IConstructorBodyOperation cb) { if (cb.BlockBody != null) EmitStatement(cb.BlockBody); else if (cb.ExpressionBody != null) EmitStatement(cb.ExpressionBody); }
            else if (body is IMethodBodyOperation mb) EmitStatement((IOperation?)mb.BlockBody ?? mb.ExpressionBody!);
        }
        Finish();
    }

    void EmitSubstitutedBody(string value)
    {
        // cs2wasm's own substitution: `=>Type.Method`, the body is a call to a boundary method taking the same
        // parameters (the instance first)
        if (value.StartsWith("=>"))
        {
            int dot = value.LastIndexOf('.');
            int n = M.Parameters.Length + (M.IsStatic ? 0 : 1);
            var target = D.LibMethod(value[2..dot], value[(dot + 1)..], n).Sym;
            var own = CallWith(target, false, null, () => { for (int i = 0; i < n; i++) E.Get(i); }, null, null);
            if (!M.ReturnsVoid && Types.IsRef(T.Resolve(M.ReturnType, Map)) && own is Own.Stable or Own.Unstable) E.Call(Rt.Retain);
            E.Op(Op.Return);
            return;
        }
        if (M.ReturnsVoid) return;
        var rt = T.Resolve(M.ReturnType, Map);
        if (Types.PrimOf(rt) == Prim.Bool) E.I32(value == "true" ? 1 : 0);
        else if (Types.WasmType(rt) is ValType.I64) E.I64(long.TryParse(value, out var l) ? l : 0);
        else if (Types.WasmType(rt) is ValType.F32) E.F32(0);
        else if (Types.WasmType(rt) is ValType.F64) E.F64(0);
        else E.I32(int.TryParse(value, out var iv) ? iv : 0);
        E.Op(Op.Return);
    }

    void Synthesize()
    {
        var def = M.OriginalDefinition;
        if (_valueEqualsType != null) { EmitValueEquals(); return; }
        if (_objectArrayThunkHandler != null) { EmitObjectArrayThunk(); return; }
        if (def.IsExtern && def.GetAttributes().Any(a => a.AttributeClass?.Name == "UnsafeAccessorAttribute")) { EmitUnsafeAccessorBody(); return; }
        // (a lambda with default or params parameters has a delegate type of its own, anonymous too: a delegate like any)
        if (_anonType != null || def.ContainingType.IsAnonymousType && def.ContainingType.TypeKind != TypeKind.Delegate) { EmitAnonymousMember(); return; }
        if (def.ContainingType.IsRecord && def.IsImplicitlyDeclared && !(def.AssociatedSymbol is IPropertySymbol ap && Driver.AutoPropertyField(ap) != null) || def.ContainingType.IsRecord && def.MethodKind == MethodKind.Constructor && !Driver.HasBody(def)) { EmitRecordMember(); return; }
        if (def.MethodKind == MethodKind.Constructor)
        {
            if (Driver.IsPrimaryCtor(def))
            {
                // capture the parameters, run the initializers, then the base constructor from the base list
                foreach (var pp in M.Parameters.Where(Driver.IsCapturedPrimaryParam))
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
                if (bf.IsStatic) { D.StaticReads.Add(Driver.StaticKey(Owner(bf), bf)); EmitStaticInitGuardForField(Owner(bf)); E.I32Addr(D.StaticField(Owner(bf), bf, ft)); }
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
        if (ev.IsStatic) { D.StaticReads.Add(Driver.StaticKey(owner, ev)); EmitStaticInitGuardForField(owner); E.I32Addr(D.StaticField(owner, ev, T.Resolve(ev.Type, Map))); return 0; }
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

    static readonly HashSet<string> s_fileEntries = ["Open", "FStat", "Stat", "LStat", "Read", "Write", "PRead", "PWrite", "LSeek", "FSync", "FTruncate",
        "Unlink", "MkDir", "RmDir", "OpenDir", "ReadDir", "ReadDirR", "CloseDir", "Access", "RealPath", "Rename", "CopyFile", "FLock", "PosixFAdvise",
        "FAllocate", "GetCwd", "ChDir", "ChMod", "FChMod", "UTimensat", "FUTimens", "Link", "SymLink", "ReadLink", "GetFileSystemType", "Dup",
        "MksTemps", "MMap", "ShmOpen", "ShmUnlink", "LChflags", "FChflags", "GetDefaultTimeZone", "INotifyInit"];

    /// <summary>"platform invoke (lib!SystemNative_X)": the sandbox capability X stands for, or null.</summary>
    static string? NativeCapability(string pinvoke)
    {
        int bang = pinvoke.IndexOf('!');
        if (bang < 0) return null;
        var entry = pinvoke[(bang + 1)..].TrimEnd(')');
        if (entry.StartsWith("SystemNative_", StringComparison.Ordinal)) entry = entry["SystemNative_".Length..];
        if (s_fileEntries.Contains(entry)) return "the file system";
        if (entry.Contains("Socket") || entry.StartsWith("Get") && entry.Contains("Host") || entry is "Connect" or "Bind" or "Listen" or "Accept" or "Send" or "Receive" or "SendMessage" or "ReceiveMessage") return "network sockets";
        if (entry is "ForkAndExecProcess" or "Kill" or "WaitPidExitedNoHang" or "WaitIdAnyExitedNoHangNoWait") return "processes";
        return null;
    }

    /// <summary>A capability the sandbox lacks, reported where the program's own code first reaches the library
    /// member that needs it (once per capability and place), or where it is used when no program code leads to it.</summary>
    void ReportCapability(Location? loc, string name, string capability)
    {
        MethodInst? prev = null;
        var c = D.Compiling;
        while (c != null && D.IsLibrary(c.Sym)) { prev = c; c = c.ReachedFrom; }
        if (c != null && prev?.FirstUse is { } userLoc)
        {
            if (!D.ReportedCapabilities.Add(capability + "@" + userLoc.GetLineSpan())) return;
            D.ReportedCapabilities.Add(capability);
            D.Report(Diags.SandboxCapability, userLoc, Driver.Display(prev.Sym) + " (through " + name + ")", capability);
            return;
        }
        // reached only inside the library: once per capability
        if (!D.ReportedCapabilities.Add(capability)) return;
        D.Report(Diags.SandboxCapability, loc, name, capability);
    }

    void ReportMissing(IMethodSymbol m, Location? loc)
    {
        loc ??= m.Locations.FirstOrDefault(l => l.IsInSource);
        var name = m.ToDisplayString() + D.WhyReached(D.Compiling);
        if (Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null)
            Console.Error.WriteLine($"missing {name} asm={m.ContainingAssembly?.Name} type-asm={m.ContainingType?.ContainingAssembly?.Name} key={Types.MethodKey(m)} {Environment.StackTrace}");
        var def = m.OriginalDefinition;
        if (Driver.MissingCapability(def) is { } needed && !needed.StartsWith("platform invoke")) { ReportCapability(loc, name, needed); return; }
        // an upstream library's native call (System.Native's): the capability the program's call into the library
        // stands for (File.ReadAllText: the file system), or the native function's
        if (D.IsLibrary(m) && Driver.MissingCapability(def) is { } pinv && pinv.StartsWith("platform invoke"))
        {
            if ((EntryCapability() ?? NativeCapability(pinv)) is { } nc) { ReportCapability(loc, name, nc); return; }
            // a native call reached only inside the library, once the program is already told what it lacks
            if (D.ReportedCapabilities.Count > 0) return;
        }
        if (def.GetDllImportData() != null || def.GetAttributes().Any(a => a.AttributeClass?.Name is "DllImportAttribute" or "LibraryImportAttribute"))
        { D.Report(Diags.PlatformInvoke, loc, name); return; }
        if (!D.IsLibrary(m) && def.Locations.Any(l => l.IsInSource))
        { D.Report(Diags.MissingBoundary, loc, Types.CommentId(m)); return; }
        if (CapabilityOf(m) is { } cap) D.Report(Diags.SandboxCapability, loc, name, cap);
        else D.Report(Diags.LibraryMissing, loc, name);
    }

    /// <summary>The sandbox capability a library member stands for, by its type.</summary>
    static string? CapabilityOf(IMethodSymbol m)
    {
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
        return cap;
    }

    /// <summary>The capability of the library member the program's own code called to get here, if any.</summary>
    string? EntryCapability()
    {
        MethodInst? prev = null;
        var c = D.Compiling;
        while (c != null && D.IsLibrary(c.Sym)) { prev = c; c = c.ReachedFrom; }
        return c != null && prev != null ? CapabilityOf(prev.Sym) : null;
    }
}
