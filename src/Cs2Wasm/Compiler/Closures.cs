using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// A heap object holding variables captured by lambdas or local functions (Roslyn's display class):
/// one per function invocation for variables outside loops, one per loop iteration for variables declared in a loop body.
/// Layout: object header, parent display at 12, then the variables.
/// </summary>
internal sealed class Display
{
    public readonly List<ISymbol> Vars = [];
    public readonly Dictionary<ISymbol, (int offset, ITypeSymbol type)> Slots = new(SymbolEqualityComparer.Default);
    /// <summary>Compiler slots of state machines (state, current value, awaiters, spills), released with the object.</summary>
    public readonly Dictionary<string, (int offset, ITypeSymbol type)> ExtraSlots = [];
    public int Size = 16;
    public Display? Parent;
    public int TypeId;
    internal object? Owner;   // the FunctionCompiler that allocates it
    internal int Local = -1;  // wasm local in the owner holding the pointer
    public string Name = "";
}

sealed class ClosureInfo
{
    public required TypeMap Map;
    public Display? Env;
    public ITypeSymbol? ReturnType;
    public int ParamLocal = -1;
    public Dictionary<ISymbol, Display?> LocalFunctionEnvs = new(SymbolEqualityComparer.Default);
    public ISymbol? ThisKey;
    public string? OuterKey;
    public IMethodSymbol? OuterMethod;
    public bool IsCapturedHere(ISymbol s) => false;
}

sealed partial class FunctionCompiler
{
    readonly Dictionary<object, Display> _displays = [];   // key: loop operation, or s_fnKey for function level
    static readonly object s_fnKey = new();
    readonly Dictionary<ISymbol, Display> _capturedHere = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ISymbol, Display?> _lfEnvs = new(SymbolEqualityComparer.Default);
    readonly List<Display> _activeDisplays = [];
    ISymbol? _thisKey;

    public string InstanceKey => Inst?.Key ?? (E.Fn.Name + "#" + E.Fn.Order);

    bool IsCapturedInThis(ISymbol s) => _capturedHere.ContainsKey(s);

    bool IsCaptured(ISymbol s)
    {
        if (_capturedHere.ContainsKey(s)) return true;
        for (var d = _closure?.Env; d != null; d = d.Parent) if (d.Slots.ContainsKey(s)) return true;
        return false;
    }

    /// <summary>Pushes the base address of the display holding <paramref name="s"/>; returns the variable's offset.</summary>
    int CapturedAddress(ISymbol s)
    {
        if (_capturedHere.TryGetValue(s, out var d))
        {
            E.Get(d.Local);
            return d.Slots[s].offset;
        }
        E.Get(_closure!.ParamLocal);
        for (var x = _closure.Env; x != null; x = x.Parent)
        {
            if (x.Slots.TryGetValue(s, out var slot)) return slot.offset;
            E.Load(Op.I32Load, 12);
        }
        throw new CompileException("captured variable not found: " + s.Name);
    }

    ITypeSymbol CapturedType(ISymbol s)
    {
        if (_capturedHere.TryGetValue(s, out var d)) return d.Slots[s].type;
        for (var x = _closure?.Env; x != null; x = x.Parent) if (x.Slots.TryGetValue(s, out var slot)) return slot.type;
        throw new CompileException("captured variable not found: " + s.Name);
    }

    Own LoadCaptured(ISymbol s)
    {
        var t = CapturedType(s);
        int off = CapturedAddress(s);
        if (Types.IsStruct(t)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } return Own.None; }
        E.Load(Types.LoadOp(t), off);
        return Types.IsRef(t) ? Own.Unstable : Own.None;
    }

    void StoreCaptured(ISymbol s, ITypeSymbol valueType, Func<Own> produce)
    {
        var t = CapturedType(s);
        int a = E.AddLocal(ValType.I32);
        int off = CapturedAddress(s); E.Set(a);
        var o = produce();
        ConvertTop(valueType, t, ref o);
        StoreToAddressFromStack(() => E.Get(a), off, t, o);
    }

    Own LoadCapturedThis()
    {
        var key = _closure!.ThisKey ?? throw new CompileException("'this' is not captured");
        int off = CapturedAddress(key);
        E.Load(Op.I32Load, off);
        return Own.Stable;
    }

    Display? CurrentEnv() => _activeDisplays.Count > 0 ? _activeDisplays[^1] : _closure?.Env;

    void PushDisplayPtr(Display? target, bool retain)
    {
        if (target == null) { E.I32(0); return; }
        if (ReferenceEquals(target.Owner, this)) { E.Get(target.Local); }
        else
        {
            if (_closure == null) throw new CompileException("internal: display not reachable");
            E.Get(_closure.ParamLocal);
            var x = _closure.Env;
            while (x != null && !ReferenceEquals(x, target)) { E.Load(Op.I32Load, 12); x = x.Parent; }
            if (x == null) throw new CompileException("internal: display not on the environment chain");
        }
        if (retain) E.Call(Rt.Retain);
    }

    Display? LocalFunctionEnv(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        if (_lfEnvs.TryGetValue(def, out var d)) return d;
        if (_closure != null && _closure.LocalFunctionEnvs.TryGetValue(def, out d)) return d;
        return CurrentEnv();
    }

    // ---------------------------------------------------------------------
    // analysis: which variables nested functions capture, and where they live
    // ---------------------------------------------------------------------
    void AnalyzeCaptures(IOperation body)
    {
        if (_closure != null) foreach (var kv in _closure.LocalFunctionEnvs) _lfEnvs[kv.Key] = kv.Value;
        _thisKey = _closure?.ThisKey ?? M.ContainingType;
        var self = SelfSymbol();
        var nested = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).ToList();
        if (nested.Count == 0) return;
        foreach (var n in nested)
        {
            foreach (var r in n.Descendants())
            {
                ISymbol? sym = r switch
                {
                    ILocalReferenceOperation l => l.Local,
                    IParameterReferenceOperation p => p.Parameter,
                    _ => null,
                };
                if (sym is IParameterSymbol pps && pps.ContainingSymbol is IMethodSymbol pc && Driver.IsPrimaryCtor(pc) && _closure == null && !M.IsStatic)
                {
                    // a primary-constructor parameter read inside a lambda: the lambda needs `this`
                    if (!_capturedHere.ContainsKey(_thisKey!)) AddCaptured(_thisKey!, s_fnKey, T.Resolve(M.ContainingType, Map));
                    continue;
                }
                if (sym != null)
                {
                    if (!SymbolEqualityComparer.Default.Equals(sym.ContainingSymbol, self) && !SymbolEqualityComparer.Default.Equals(sym.ContainingSymbol?.OriginalDefinition, self.OriginalDefinition)) continue;
                    if (_capturedHere.ContainsKey(sym)) continue;
                    var key = DisplayKeyFor(sym, body);
                    AddCaptured(sym, key);
                }
                else if (r is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } && _closure == null && !M.IsStatic)
                {
                    if (!_capturedHere.ContainsKey(_thisKey!)) AddCaptured(_thisKey!, s_fnKey, T.Resolve(M.ContainingType, Map));
                }
            }
        }
        // parents: innermost enclosing display, else the function display, else the incoming environment
        foreach (var (key, d) in _displays)
        {
            if (key == s_fnKey) { d.Parent = _closure?.Env; continue; }
            d.Parent = EnclosingDisplay((IOperation)key, body);
        }
        // local function environments
        foreach (var lf in nested.OfType<ILocalFunctionOperation>())
            _lfEnvs[lf.Symbol.OriginalDefinition] = EnclosingDisplayForPosition(lf, body);
    }

    ISymbol SelfSymbol() => M;

    void AddCaptured(ISymbol sym, object key, ITypeSymbol? type = null)
    {
        if (!_displays.TryGetValue(key, out var d))
        {
            d = new Display { Owner = this, Name = E.Fn.Name };
            _displays[key] = d;
        }
        var t = type ?? T.Resolve(sym switch { ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, _ => throw new CompileException("capture") }, Map);
        var (size, align) = T.StorageOf(t);
        d.Size = (d.Size + align - 1) / align * align;
        d.Slots[sym] = (d.Size, t);
        d.Vars.Add(sym);
        d.Size += size;
        _capturedHere[sym] = d;
    }

    /// <summary>The loop whose iteration a variable belongs to (or the function).</summary>
    object DisplayKeyFor(ISymbol sym, IOperation body)
    {
        if (sym is IParameterSymbol) return s_fnKey;
        var decl = body.DescendantsAndSelf().FirstOrDefault(o =>
            o is IVariableDeclaratorOperation vd && SymbolEqualityComparer.Default.Equals(vd.Symbol, sym)
            || o is ILocalReferenceOperation { IsDeclaration: true } lr && SymbolEqualityComparer.Default.Equals(lr.Local, sym)
            || o is IDeclarationPatternOperation dp && SymbolEqualityComparer.Default.Equals(dp.DeclaredSymbol, sym)
            || o is IRecursivePatternOperation rp && SymbolEqualityComparer.Default.Equals(rp.DeclaredSymbol, sym));
        if (decl == null) return s_fnKey;
        return LoopIterationOf(decl, body) ?? s_fnKey;
    }

    static IOperation? LoopIterationOf(IOperation op, IOperation body)
    {
        IOperation? child = op;
        for (var p = op.Parent; p != null && !ReferenceEquals(child, body); child = p, p = p.Parent)
        {
            if (p is ILoopOperation loop && ReferenceEquals(loop.Body, child)) return loop;
            if (p is IForEachLoopOperation fe && ReferenceEquals(fe.LoopControlVariable, child)) return fe;
            if (p is IAnonymousFunctionOperation or ILocalFunctionOperation) return null;
        }
        return null;
    }

    Display? EnclosingDisplay(IOperation loopKey, IOperation body)
    {
        for (var l = LoopIterationOf(loopKey, body); l != null; l = LoopIterationOf(l, body))
            if (_displays.TryGetValue(l, out var d)) return d;
        return _displays.TryGetValue(s_fnKey, out var f) ? f : _closure?.Env;
    }

    Display? EnclosingDisplayForPosition(IOperation op, IOperation body)
    {
        for (var l = LoopIterationOf(op, body); l != null; l = LoopIterationOf(l, body))
            if (_displays.TryGetValue(l, out var d)) return d;
        return _displays.TryGetValue(s_fnKey, out var f) ? f : _closure?.Env;
    }

    /// <summary>Allocates the display for a key (function entry or the start of a loop iteration).</summary>
    void EnterDisplay(object key)
    {
        if (!_displays.TryGetValue(key, out var d)) return;
        d.TypeId = D.DisplayTypeId(d);
        if (d.Local < 0) d.Local = E.AddLocal(ValType.I32);
        // release the previous iteration's display, allocate a fresh one
        E.Get(d.Local); E.Call(Rt.Release);
        E.I32(d.Size); E.I32(d.TypeId); E.Call(Rt.AllocObject); E.Set(d.Local);
        E.Get(d.Local); PushDisplayPtr(d.Parent, retain: true); E.Store(Op.I32Store, 12);
        if (key == s_fnKey)
        {
            AddOwned(new Slot { Local = d.Local, Type = D.ObjectType });
            // copy captured parameters and `this` in
            foreach (var v in d.Vars)
            {
                var (off, t) = d.Slots[v];
                if (v is IParameterSymbol p)
                {
                    var pv = _vars[p];
                    E.Get(d.Local);
                    if (Types.IsStruct(t)) { E.I32(off); E.Op(Op.I32Add); PushVarAddress(pv); CopyStructInit(t); continue; }
                    LoadVar(pv);
                    if (Types.IsRef(t)) E.Call(Rt.Retain);
                    E.Store(Types.StoreOp(t), off);
                }
                else if (ReferenceEquals(v, _thisKey) && _thisLocal >= 0)
                {
                    E.Get(d.Local); E.Get(_thisLocal); E.Call(Rt.Retain); E.Store(Op.I32Store, off);
                }
            }
        }
        else AddOwned(new Slot { Local = d.Local, Type = D.ObjectType });
        _activeDisplays.Add(d);
    }

    void ExitDisplay(object key)
    {
        if (_displays.TryGetValue(key, out var d)) _activeDisplays.Remove(d);
    }

    internal ClosureInfo MakeClosureInfo(TypeMap map, Display? env, ITypeSymbol? ret)
    {
        var ci = new ClosureInfo { Map = map, Env = env, ReturnType = ret, ThisKey = _thisKey ?? M.ContainingType, OuterMethod = OuterMethod };
        foreach (var kv in _lfEnvs) ci.LocalFunctionEnvs[kv.Key] = kv.Value;
        return ci;
    }

    // ---------------------------------------------------------------------
    // compiling a lambda or local function
    // ---------------------------------------------------------------------
    public static void CompileNested(Driver d, MethodInst mi, IMethodSymbol sym, IOperation body, ClosureInfo ci)
    {
        var fc = new FunctionCompiler(d, mi.Fn, sym, mi, ci);
        fc.CompileNestedBody(body);
    }

    void CompileNestedBody(IOperation body)
    {
        if (IsStateMachine(M)) { CompileKickoff(body); return; }
        SetupParams();
        E.CurrentPos = D.Position(body.Syntax);
        PreScan(body);
        using (BodyScope())
        {
            EnterDisplay(s_fnKey);
            EmitStatement(body);
        }
        Finish();
    }
}
