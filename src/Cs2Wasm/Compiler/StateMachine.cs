using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Iterators and async methods (section 3), lowered as Roslyn does into a state machine object holding every local,
/// with a MoveNext function. MoveNext is a dispatch loop over the method's control-flow graph: each basic block, and
/// each point after a `yield return` or an `await`, is a resumable segment. try/catch/finally regions are handled by
/// one exception handler around the loop that finds the region of the segment that was running.
/// </summary>
sealed class MachineInfo
{
    public bool IsAsync, IsIterator, ReturnsEnumerable, ReturnsVoid;
    /// <summary>A plain iterator: MoveNext returns whether there is a current element.</summary>
    public bool SyncIterator => IsIterator && !IsAsync;
    /// <summary>An async iterator (IAsyncEnumerable/IAsyncEnumerator): MoveNext completes the pending MoveNextAsync promise.</summary>
    public bool AsyncIterator => IsIterator && IsAsync;
    public required Display Machine;
    public ITypeSymbol? ElementType;   // iterator: T
    public ITypeSymbol? ResultType;    // async: T of Task<T>, or null
    public ITypeSymbol? TaskType;      // async: Task or Task<T> (canonical)
    public ITypeSymbol? DeclaredReturn; // the method's declared return type (Task, ValueTask, IEnumerable<T>...)
    public required WasmFunction MoveNext;
    public required IMethodSymbol Method;
    public required TypeMap Map;
    public IOperation Body = null!;
    /// <summary>The control-flow graph of the body (for lambdas and local functions, taken from the enclosing method's).</summary>
    public ControlFlowGraph? Cfg;
    /// <summary>Iterator Dispose / DisposeAsync: filled after MoveNext, when the suspension points are known.</summary>
    public WasmFunction? DisposeFn;
}

internal sealed partial class Driver
{
    public readonly Dictionary<int, SyntheticType> SyntheticById = [];
}

/// <summary>A compiler-made class (iterator objects): its interfaces and the functions implementing their methods.</summary>
internal sealed class SyntheticType
{
    public required string Name;
    public List<ITypeSymbol> Interfaces = [];
    public Dictionary<string, WasmFunction> Impls = [];
}

internal static class DisplayExtra
{
    public static int Extra(this Display d, string key, ITypeSymbol t, Types types)
    {
        if (d.ExtraSlots.TryGetValue(key, out var e)) return e.offset;
        var (size, align) = types.StorageOf(t);
        d.Size = (d.Size + align - 1) / align * align;
        int off = d.Size;
        d.ExtraSlots[key] = (off, t);
        d.Size += size;
        return off;
    }
}

sealed partial class FunctionCompiler
{
    MachineInfo? _machine;
    ControlFlowGraph? _cfg;
    BasicBlock? _curBlock;
    readonly Dictionary<IOperation, (int off, ITypeSymbol type)> _precomputed = [];
    readonly List<(int id, int block, int op, int action)> _segments = [];
    readonly Queue<int> _segmentQueue = new();
    int _curLocal = -1, _dispatchStateOff;
    Label? _dispatch;
    int _spillCounter;
    readonly Dictionary<IOperation, List<(int kind, IOperation op)>> _plans = [];

    static bool IsStateMachine(IMethodSymbol m) => m.IsAsync || m.IsIterator;

    ITypeSymbol IntType => T.Canonical(D.Main.GetSpecialType(SpecialType.System_Int32));

    // =====================================================================
    // kickoff: the method itself creates the machine
    // =====================================================================
    void CompileKickoff(IOperation? nestedBody = null)
    {
        var def = M.OriginalDefinition;
        bool nested = nestedBody != null;
        SetupParams();
        IOperation body; SyntaxNode syntax;
        if (nested) { body = nestedBody!; syntax = body.Syntax; }
        else (body, syntax) = GetBody(def);
        E.CurrentPos = D.Position(syntax);
        var machine = new Display { Owner = null, Name = "<" + def.Name + ">d" };
        var info = new MachineInfo
        {
            Machine = machine, Method = M, Map = Map, IsAsync = M.IsAsync, IsIterator = M.IsIterator,
            MoveNext = D.Module.AddFunction("MoveNext of " + E.Fn.Name, M.IsIterator && !M.IsAsync ? new FuncType([ValType.I32], [ValType.I32]) : new FuncType([ValType.I32], [])),
            Body = body,
            Cfg = nested ? NestedCfg(def, syntax) : null,
        };
        info.MoveNext.FrameName = Driver.FrameName(M);
        var ret = T.Resolve(M.ReturnType, Map);
        info.DeclaredReturn = ret;
        if (info.IsIterator)
        {
            var n = (INamedTypeSymbol)ret;
            info.ReturnsEnumerable = n.Name is "IEnumerable" or "IAsyncEnumerable";
            info.ElementType = n.Arity == 1 ? T.Canonical(n.TypeArguments[0]) : D.ObjectType;
        }
        else
        {
            info.ReturnsVoid = Types.IsVoid(ret);
            if (!info.ReturnsVoid)
            {
                var n = (INamedTypeSymbol)ret;
                var full = Types.FullName(n);
                if (full is not ("System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1"))
                    throw new CompileException("async methods returning " + n + " are not supported yet (custom builders)", def.Locations.FirstOrDefault());
                info.ResultType = n.Arity == 1 ? T.Canonical(n.TypeArguments[0]) : null;
                var task = info.ResultType == null ? D.LibType("System.Threading.Tasks.Task") : D.LibType("System.Threading.Tasks.Task`1").Construct(info.ResultType);
                info.TaskType = task;
            }
        }
        // the machine holds parameters, `this`, and every local of the body
        foreach (var p in M.Parameters) AddMachineVar(machine, p.OriginalDefinition, T.Resolve(p.Type, Map));
        var thisKey = nested ? (_closure!.ThisKey ?? M.ContainingType) : (ISymbol)M.ContainingType;
        if (!nested && !M.IsStatic) AddMachineVar(machine, thisKey, T.Resolve(M.ContainingType, Map));
        if (nested) machine.Parent = _closure!.Env;
        foreach (var l in body.DescendantsAndSelf().OfType<IVariableDeclaratorOperation>()) AddMachineVar(machine, l.Symbol, T.Resolve(l.Symbol.Type, Map));
        foreach (var l in body.DescendantsAndSelf().OfType<ILocalReferenceOperation>().Where(x => x.IsDeclaration)) AddMachineVar(machine, l.Local, T.Resolve(l.Local.Type, Map));
        foreach (var l in body.DescendantsAndSelf().OfType<IDeclarationPatternOperation>()) if (l.DeclaredSymbol is ILocalSymbol ls) AddMachineVar(machine, ls, T.Resolve(ls.Type, Map));
        foreach (var l in body.DescendantsAndSelf().OfType<IForEachLoopOperation>()) foreach (var ls in l.Locals) AddMachineVar(machine, ls, T.Resolve(ls.Type, Map));
        foreach (var l in body.DescendantsAndSelf().OfType<ICatchClauseOperation>()) foreach (var ls in l.Locals) AddMachineVar(machine, ls, T.Resolve(ls.Type, Map));
        foreach (var l in body.DescendantsAndSelf().OfType<IUsingOperation>()) foreach (var ls in l.Locals) AddMachineVar(machine, ls, T.Resolve(ls.Type, Map));
        foreach (var l in body.DescendantsAndSelf().OfType<IBlockOperation>()) foreach (var ls in l.Locals) AddMachineVar(machine, ls, T.Resolve(ls.Type, Map));
        int stateOff = machine.Extra("state", IntType, T);
        if (info.IsIterator)
        {
            machine.Extra("current", info.ElementType!, T);
            if (info.AsyncIterator) machine.Extra("promise", D.LibType("System.Threading.Tasks.Task`1").Construct(T.Canonical(D.Main.GetSpecialType(SpecialType.System_Boolean))), T);
            if (info.ReturnsEnumerable)
                foreach (var p in M.Parameters) machine.Extra("init:" + p.Name, T.Resolve(p.Type, Map), T);
        }
        if (info.TaskType != null) machine.Extra("task", info.TaskType, T);
        int tid = D.DisplayTypeId(machine);
        var sizeAddr = new LateAddr(() => machine.Size);

        // compile MoveNext later (it may add slots), with the machine as its environment
        var ci = new ClosureInfo { Map = Map, Env = machine, ThisKey = thisKey, OuterMethod = nested ? OuterMethod : M };
        foreach (var kv in _lfEnvs) ci.LocalFunctionEnvs[kv.Key] = kv.Value;
        if (_closure != null) foreach (var kv in _closure.LocalFunctionEnvs) ci.LocalFunctionEnvs[kv.Key] = kv.Value;
        var mnInst = new MethodInst { Sym = M, Fn = info.MoveNext, Key = InstanceKey + " MoveNext", Impl = MethodImpl.Body };
        mnInst.Compile = () => { var fc = new FunctionCompiler(D, info.MoveNext, M, mnInst, ci) { _machine = info }; fc.CompileMoveNext(); };
        D.EnqueueExtra(mnInst);

        using (BodyScope())
        {
            int obj = E.AddLocal(ValType.I32);
            E.I32Addr(sizeAddr); E.I32(tid); E.Call(Rt.AllocObject); E.Set(obj);
            AddOwned(new Slot { Local = obj, Type = D.ObjectType });
            if (nested) { E.Get(obj); E.Get(_closure!.ParamLocal); E.Call(Rt.Retain); E.Store(Op.I32Store, 12); }
            foreach (var p in M.Parameters)
            {
                var (off, pt) = machine.Slots[p.OriginalDefinition];
                StoreToAddressFromStackCopy(obj, off, pt, () => LoadVar(_vars[p]));
                if (info.IsIterator && info.ReturnsEnumerable)
                    StoreToAddressFromStackCopy(obj, machine.ExtraSlots["init:" + p.Name].offset, pt, () => LoadVar(_vars[p]));
            }
            if (!nested && !M.IsStatic) StoreToAddressFromStackCopy(obj, machine.Slots[thisKey].offset, T.Resolve(M.ContainingType, Map), () => { E.Get(_thisLocal); return Own.Stable; });
            if (info.IsIterator)
            {
                E.Get(obj); E.I32(info.ReturnsEnumerable ? -2 : 0); E.Store(Op.I32Store, stateOff);
                if (info.AsyncIterator) RegisterAsyncIterator(info, tid); else RegisterIterator(info, tid);
                E.Get(obj); E.Call(Rt.Retain);
                _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                EmitReturnSequence(true);
            }
            else
            {
                if (info.TaskType != null)
                {
                    var create = D.LibMethod("System.Threading.Tasks.Task", "CreateForAsync", 0);
                    var tcreate = info.ResultType == null ? create.Sym : ((INamedTypeSymbol)info.TaskType).GetMembers("CreateForAsyncT").OfType<IMethodSymbol>().First();
                    int toff = machine.ExtraSlots["task"].offset;
                    E.Get(obj); E.Call(D.Method(tcreate).Fn); E.Store(Op.I32Store, toff);
                }
                E.Get(obj); E.Call(info.MoveNext);
                if (info.TaskType != null)
                {
                    E.Get(obj); E.Load(Op.I32Load, machine.ExtraSlots["task"].offset); E.Call(Rt.Retain);
                    var full = Types.FullName(ret);
                    if (full.StartsWith("System.Threading.Tasks.ValueTask"))
                    {
                        // ValueTask(task): construct the struct into the return slot
                        int t = Scratch(ValType.I32); E.Set(t);
                        var vctor = ((INamedTypeSymbol)ret).InstanceConstructors.First(c => c.Parameters.Length == 1 && Types.FullName(c.Parameters[0].Type).StartsWith("System.Threading.Tasks.Task"));
                        CallWith(D.CanonicalMethod(vctor), false, () => E.Get(_structRetLocal), () => E.Get(t), null, null);
                        E.Get(t); E.Call(Rt.Release);
                        FreeScratch(ValType.I32, t);
                        EmitReturnSequence(false);
                    }
                    else
                    {
                        _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                        EmitReturnSequence(true);
                    }
                }
                else EmitReturnSequence(false);
            }
        }
        Finish();
    }

    /// <summary>The CFG of a lambda or local function, found inside its enclosing member's CFG.</summary>
    ControlFlowGraph NestedCfg(IMethodSymbol def, SyntaxNode bodySyntax)
    {
        var member = bodySyntax.AncestorsAndSelf().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax || (n is ArrowExpressionClauseSyntax && n.Parent is PropertyDeclarationSyntax or IndexerDeclarationSyntax))
            ?? throw new CompileException("cannot find the member enclosing an async lambda", def.Locations.FirstOrDefault());
        var model = D.ModelFor(member.SyntaxTree);
        var root = model.GetOperation(member);
        ControlFlowGraph cfg = root switch
        {
            IMethodBodyOperation mb => ControlFlowGraph.Create(mb),
            IConstructorBodyOperation cb => ControlFlowGraph.Create(cb),
            IBlockOperation { Parent: null } bl => ControlFlowGraph.Create(bl),
            _ => throw new CompileException("cannot build the control-flow graph around an async lambda", def.Locations.FirstOrDefault()),
        };
        return FindNested(cfg, def) ?? throw new CompileException("async lambda not found in its member's control-flow graph", def.Locations.FirstOrDefault());
    }

    static ControlFlowGraph? FindNested(ControlFlowGraph cfg, IMethodSymbol target)
    {
        foreach (var lf in cfg.LocalFunctions)
        {
            var g = cfg.GetLocalFunctionControlFlowGraph(lf);
            if (SymbolEqualityComparer.Default.Equals(lf, target)) return g;
            if (FindNested(g, target) is { } r) return r;
        }
        foreach (var b in cfg.Blocks)
            foreach (var op in b.Operations.Concat(b.BranchValue != null ? [b.BranchValue] : []))
                foreach (var fa in op.DescendantsAndSelf().OfType<IFlowAnonymousFunctionOperation>())
                {
                    var g = cfg.GetAnonymousFunctionControlFlowGraph(fa);
                    if (SymbolEqualityComparer.Default.Equals(fa.Symbol, target)) return g;
                    if (FindNested(g, target) is { } r) return r;
                }
        return null;
    }

    void AddMachineVar(Display d, ISymbol s, ITypeSymbol t)
    {
        if (d.Slots.ContainsKey(s)) return;
        var (size, align) = T.StorageOf(t);
        d.Size = (d.Size + align - 1) / align * align;
        d.Slots[s] = (d.Size, t);
        d.Vars.Add(s);
        d.Size += size;
    }

    /// <summary>obj.field = value (taking a count / copying a struct), field initially empty.</summary>
    void StoreToAddressFromStackCopy(int obj, int off, ITypeSymbol t, Func<Own> produce)
    {
        var o = produce();
        StoreToAddressFromStack(() => E.Get(obj), off, t, o);
    }

    // =====================================================================
    // iterator objects: IEnumerable<T> / IEnumerator<T> implementations
    // =====================================================================
    void RegisterIterator(MachineInfo info, int tid)
    {
        if (D.SyntheticById.ContainsKey(tid)) return;
        var et = info.ElementType!;
        var machine = info.Machine;
        int stateOff = machine.ExtraSlots["state"].offset, curOff = machine.ExtraSlots["current"].offset;
        var ienT = (INamedTypeSymbol)T.Canonical(D.Main.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")!.Construct(et));
        var ietorT = (INamedTypeSymbol)T.Canonical(D.Main.GetTypeByMetadataName("System.Collections.Generic.IEnumerator`1")!.Construct(et));
        var ien = (INamedTypeSymbol)T.Canonical(D.Main.GetTypeByMetadataName("System.Collections.IEnumerable")!);
        var ietor = (INamedTypeSymbol)T.Canonical(D.Main.GetTypeByMetadataName("System.Collections.IEnumerator")!);
        var idisp = (INamedTypeSymbol)T.Canonical(D.Main.GetSpecialType(SpecialType.System_IDisposable));
        var st = new SyntheticType { Name = machine.Name, Interfaces = [ietorT, ietor, idisp] };
        if (info.ReturnsEnumerable) { st.Interfaces.Add(ienT); st.Interfaces.Add(ien); }
        string K(INamedTypeSymbol t, string name) => Types.MethodKey(D.CanonicalMethod(t.GetMembers(name).OfType<IMethodSymbol>().First()));

        // GetEnumerator: the object itself the first time, else a fresh copy started from the initial arguments
        var ge = D.Module.AddFunction("GetEnumerator of " + machine.Name, new FuncType([ValType.I32], [ValType.I32]));
        {
            var e = new Emitter(ge);
            e.Get(0); e.Load(Op.I32Load, stateOff); e.I32(-2); e.Op(Op.I32Eq);
            e.If(); e.Get(0); e.I32(0); e.Store(Op.I32Store, stateOff); e.Get(0); e.Call(Rt.Retain); e.Op(Op.Return); e.End();
            int n = e.AddLocal(ValType.I32);
            e.I32Addr(new LateAddr(() => machine.Size)); e.I32(tid); e.Call(Rt.AllocObject); e.Set(n);
            foreach (var p in M.Parameters)
            {
                var (off, pt) = machine.Slots[p.OriginalDefinition];
                if (!machine.ExtraSlots.TryGetValue("init:" + p.Name, out var init)) continue;
                CopyField(e, n, off, 0, init.offset, pt);
                CopyField(e, n, init.offset, 0, init.offset, pt);
            }
            if (machine.Slots.TryGetValue(M.ContainingType, out var thisSlot)) CopyField(e, n, thisSlot.offset, 0, thisSlot.offset, thisSlot.type);
            if (machine.Parent != null) CopyField(e, n, 12, 0, 12, D.ObjectType);
            e.Get(n);
        }
        st.Impls[K(ienT, "GetEnumerator")] = ge;
        st.Impls[K(ien, "GetEnumerator")] = ge;
        st.Impls[K(ietor, "MoveNext")] = info.MoveNext;
        // Current
        var getCur = ietorT.GetMembers("get_Current").OfType<IMethodSymbol>().First();
        var curSig = Driver.Signature(getCur, T);
        var gc = D.Module.AddFunction("Current of " + machine.Name, curSig);
        {
            var e = new Emitter(gc);
            if (Types.IsStruct(et)) { e.Get(0); e.Get(1); e.I32(curOff); e.Op(Op.I32Add); e.Call(Rt.CopyInit(et)); }
            else { e.Get(0); e.Load(Types.LoadOp(et), curOff); if (Types.IsRef(et)) e.Call(Rt.Retain); }
        }
        st.Impls[K(ietorT, "get_Current")] = gc;
        var gco = D.Module.AddFunction("IEnumerator.Current of " + machine.Name, new FuncType([ValType.I32], [ValType.I32]));
        {
            var save = E; // box through a small compiler instance-free path
            var e = new Emitter(gco);
            if (Types.IsRef(et)) { e.Get(0); e.Load(Op.I32Load, curOff); e.Call(Rt.Retain); }
            else
            {
                int tidB = D.MarkConstructed(et);
                int dataOff = BoxDataOffset(T, et);
                int b = e.AddLocal(ValType.I32);
                e.I32(dataOff + T.StorageOf(et).size); e.I32(tidB); e.Call(Rt.AllocObject); e.Set(b);
                if (Types.IsStruct(et)) { e.Get(b); e.I32(dataOff); e.Op(Op.I32Add); e.Get(0); e.I32(curOff); e.Op(Op.I32Add); e.Call(Rt.CopyInit(et)); }
                else { e.Get(b); e.Get(0); e.Load(Types.LoadOp(et), curOff); e.Store(Types.StoreOp(et), dataOff); }
                e.Get(b);
            }
        }
        st.Impls[K(ietor, "get_Current")] = gco;
        var disp = D.Module.AddFunction("Dispose of " + machine.Name, new FuncType([ValType.I32], []));
        info.DisposeFn = disp;
        st.Impls[K(idisp, "Dispose")] = disp;
        var reset = D.Module.AddFunction("Reset of " + machine.Name, new FuncType([ValType.I32], []));
        { var e = new Emitter(reset); e.Call(D.LibMethod("Cs2Wasm.Runtime.ThrowHelpers", "NotSupported", 0).Fn); }
        st.Impls[K(ietor, "Reset")] = reset;
        D.SyntheticById[tid] = st;
    }

    /// <summary>Async iterator: the pending MoveNextAsync completes with `value`.</summary>
    void CompletePromise(bool value)
    {
        var info = _machine!;
        var promise = info.Machine.ExtraSlots["promise"];
        var set = ((INamedTypeSymbol)promise.type).GetMembers("TrySetResultT").OfType<IMethodSymbol>().First();
        CallWith(set, false, () => { E.Get(0); E.Load(Op.I32Load, promise.offset); }, () => E.I32(value ? 1 : 0), null, null);
        E.Op(Op.Drop);
    }

    /// <summary>IAsyncEnumerable/IAsyncEnumerator over the machine: MoveNextAsync makes a fresh promise and runs
    /// MoveNext, which completes it at the next `yield return` (true), the end (false), or an exception.</summary>
    void RegisterAsyncIterator(MachineInfo info, int tid)
    {
        if (D.SyntheticById.ContainsKey(tid)) return;
        var et = info.ElementType!;
        var machine = info.Machine;
        int stateOff = machine.ExtraSlots["state"].offset, curOff = machine.ExtraSlots["current"].offset;
        var (promiseOff, promiseT) = machine.ExtraSlots["promise"];
        var ienT = (INamedTypeSymbol)T.Canonical(D.Main.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1")!.Construct(et));
        var ietorT = (INamedTypeSymbol)T.Canonical(D.Main.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerator`1")!.Construct(et));
        var idisp = (INamedTypeSymbol)T.Canonical(D.Main.GetTypeByMetadataName("System.IAsyncDisposable")!);
        var st = new SyntheticType { Name = machine.Name, Interfaces = [ietorT, idisp] };
        if (info.ReturnsEnumerable) st.Interfaces.Add(ienT);
        IMethodSymbol Member(INamedTypeSymbol t, string name) => D.CanonicalMethod(t.GetMembers(name).OfType<IMethodSymbol>().First());
        string K(INamedTypeSymbol t, string name) => Types.MethodKey(Member(t, name));

        if (info.ReturnsEnumerable)
        {
            var gm = Member(ienT, "GetAsyncEnumerator");
            var ge = D.Module.AddFunction("GetAsyncEnumerator of " + machine.Name, Driver.Signature(gm, T));
            var e = new Emitter(ge);
            e.Get(0); e.Load(Op.I32Load, stateOff); e.I32(-2); e.Op(Op.I32Eq);
            e.If(); e.Get(0); e.I32(0); e.Store(Op.I32Store, stateOff); e.Get(0); e.Call(Rt.Retain); e.Op(Op.Return); e.End();
            int n = e.AddLocal(ValType.I32);
            e.I32Addr(new LateAddr(() => machine.Size)); e.I32(tid); e.Call(Rt.AllocObject); e.Set(n);
            foreach (var p in M.Parameters)
            {
                var (off, pt) = machine.Slots[p.OriginalDefinition];
                if (!machine.ExtraSlots.TryGetValue("init:" + p.Name, out var init)) continue;
                CopyField(e, n, off, 0, init.offset, pt);
                CopyField(e, n, init.offset, 0, init.offset, pt);
            }
            if (machine.Slots.TryGetValue(M.ContainingType, out var thisSlot)) CopyField(e, n, thisSlot.offset, 0, thisSlot.offset, thisSlot.type);
            if (machine.Parent != null) CopyField(e, n, 12, 0, 12, D.ObjectType);
            e.Get(n);
            st.Impls[K(ienT, "GetAsyncEnumerator")] = ge;
        }
        // MoveNextAsync(): ValueTask<bool> written to the return slot (param 0), `this` is param 1
        {
            var mm = Member(ietorT, "MoveNextAsync");
            var f = D.Module.AddFunction("MoveNextAsync of " + machine.Name, Driver.Signature(mm, T));
            var e = new Emitter(f);
            var create = ((INamedTypeSymbol)promiseT).GetMembers("CreateForAsyncT").OfType<IMethodSymbol>().First();
            int p = e.AddLocal(ValType.I32);
            e.Call(D.Method(D.CanonicalMethod(create)).Fn); e.Set(p);
            e.Get(1); e.Load(Op.I32Load, promiseOff); e.Call(Rt.Release);
            e.Get(1); e.Get(p); e.Store(Op.I32Store, promiseOff);
            // a finished machine answers false at once
            e.Get(1); e.Load(Op.I32Load, stateOff); e.I32(-1); e.Op(Op.I32Eq);
            e.If();
            var set = ((INamedTypeSymbol)promiseT).GetMembers("TrySetResultT").OfType<IMethodSymbol>().First();
            e.Get(p); e.I32(0); e.Call(D.Method(D.CanonicalMethod(set)).Fn); e.Op(Op.Drop);
            e.Else();
            e.Get(1); e.Call(info.MoveNext);
            e.End();
            e.Get(0); e.Get(p); e.Call(D.LibMethod("Cs2Wasm.Runtime.AsyncIter", "Wrap", 1).Fn);
            st.Impls[K(ietorT, "MoveNextAsync")] = f;
        }
        {
            var getCur = Member(ietorT, "get_Current");
            var gc = D.Module.AddFunction("Current of " + machine.Name, Driver.Signature(getCur, T));
            var e = new Emitter(gc);
            if (Types.IsStruct(et)) { e.Get(0); e.Get(1); e.I32(curOff); e.Op(Op.I32Add); e.Call(Rt.CopyInit(et)); }
            else { e.Get(0); e.Load(Types.LoadOp(et), curOff); if (Types.IsRef(et)) e.Call(Rt.Retain); }
            st.Impls[K(ietorT, "get_Current")] = gc;
        }
        {
            var dm = Member(idisp, "DisposeAsync");
            var f = D.Module.AddFunction("DisposeAsync of " + machine.Name, Driver.Signature(dm, T));
            info.DisposeFn = f;
            st.Impls[K(idisp, "DisposeAsync")] = f;
        }
        D.SyntheticById[tid] = st;
    }

    void CopyField(Emitter e, int dstObj, int dstOff, int srcLocal, int srcOff, ITypeSymbol t)
    {
        if (Types.IsStruct(t))
        {
            e.Get(dstObj); e.I32(dstOff); e.Op(Op.I32Add); e.Get(srcLocal); e.I32(srcOff); e.Op(Op.I32Add);
            if (T.LayoutOf(t).HasRefs) e.Call(Rt.CopyInit(t)); else { e.I32(T.StorageOf(t).size); e.Op(Op.MemoryCopy); }
            return;
        }
        e.Get(dstObj); e.Get(srcLocal); e.Load(Types.LoadOp(t), srcOff);
        if (Types.IsRef(t)) e.Call(Rt.Retain);
        e.Store(Types.StoreOp(t), dstOff);
    }

    // =====================================================================
    // MoveNext: dispatch over the control-flow graph
    // =====================================================================
    void CompileMoveNext()
    {
        var info = _machine!;
        if (info.Cfg != null) _cfg = info.Cfg;
        else if (info.Body is IMethodBodyOperation mb) _cfg = ControlFlowGraph.Create(mb);
        else throw new CompileException("state machine body");
        foreach (var r in AllRegions(_cfg.Root))
            if (r.Kind is ControlFlowRegionKind.Filter or ControlFlowRegionKind.FilterAndHandler)
                throw new CompileException("exception filters in async methods and iterators are not supported yet", M.Locations.FirstOrDefault());
        // params live in the machine: no wasm params besides the machine pointer
        _closure!.ParamLocal = 0;
        foreach (var p in M.Parameters) _vars.Remove(p);
        PushScope();
        _dispatchStateOff = info.Machine.ExtraSlots["state"].offset;
        _curLocal = E.AddLocal(ValType.I32);
        for (int i = 0; i < _cfg.Blocks.Length; i++) _segments.Add((i, i, 0, 0));
        // entry: the block after Entry
        var dispatch = E.Loop();
        _dispatch = dispatch;
        var handler = E.Block(new FuncType([], [ValType.I32, ValType.ExnRef]));
        E.TryTable(null, (1, D.ExceptionTag, handler));
        foreach (var b in _cfg.Blocks) _segmentQueue.Enqueue(b.Ordinal);
        var emitted = new HashSet<int>();
        while (_segmentQueue.Count > 0)
        {
            int id = _segmentQueue.Dequeue();
            if (!emitted.Add(id)) continue;
            var seg = _segments[id];
            var block = _cfg.Blocks[seg.block];
            E.Get(0); E.Load(Op.I32Load, _dispatchStateOff);
            if (id == 0) { /* state 0 starts at Entry */ }
            E.I32(id); E.Op(Op.I32Eq);
            E.If();
            E.I32(id); E.Set(_curLocal);
            EmitSegment(block, seg.op, seg.action);
            E.End();
        }
        // finished or disposed: nothing to do
        if (info.SyncIterator) { E.I32(0); E.Op(Op.Return); } else E.Op(Op.Return);
        E.End(); // try_table
        E.Op(Op.Unreachable);
        E.End(); // handler block
        int exn = E.AddLocal(ValType.ExnRef), ex = E.AddLocal(ValType.I32);
        E.Set(exn); E.Set(ex);
        E.Get(_fb); E.GSet(D.StackPointer);
        EmitMachineExceptionDispatch(ex, exn);
        E.End(); // loop
        if (info.SyncIterator) { E.I32(0); }
        _usesFrame = true;
        PopScopeNoRelease();
        EmitIteratorDispose();
        FinishMachine();
    }

    void PopScopeNoRelease() => _scopes.RemoveAt(_scopes.Count - 1);

    void FinishMachine()
    {
        // MoveNext's own frame: prologue as usual; no function-level cleanup handler (locals are in the machine)
        var body = E.Fn.Body;
        var prologue = new List<Instr>();
        void P(Op op, long imm = 0, object? r = null) => prologue.Add(new Instr { Op = op, Imm = imm, Ref = r, Pos = -1 });
        _frameSize = (_frameSize + 15) / 16 * 16;
        P(Op.GlobalGet, 0, D.StackPointer); P(Op.LocalTee, _spEntry);
        P(Op.I32Const, _frameSize); P(Op.I32Sub); P(Op.LocalTee, _fb); P(Op.GlobalSet, 0, D.StackPointer);
        P(Op.LocalGet, _fb); P(Op.I32Const, 0); P(Op.I32Const, _frameSize); P(Op.MemoryFill);
        body.InsertRange(0, prologue);
        // every return restores the stack pointer
        for (int i = body.Count - 1; i >= 0; i--)
            if (body[i].Op == Op.Return && !(i > 0 && body[i - 1].Op == Op.GlobalSet && ReferenceEquals(body[i - 1].Ref, D.StackPointer)))
            {
                body.Insert(i, new Instr { Op = Op.GlobalSet, Ref = D.StackPointer, Pos = -1 });
                body.Insert(i, new Instr { Op = Op.LocalGet, Imm = _spEntry, Pos = -1 });
            }
        E.Fn.Body.RemoveAll(i => i.Op == Op.Nop);
    }

    static IEnumerable<ControlFlowRegion> AllRegions(ControlFlowRegion r)
    {
        yield return r;
        foreach (var n in r.NestedRegions) foreach (var x in AllRegions(n)) yield return x;
    }

    readonly List<int> _yieldResumes = [];

    /// <summary>Dispose of a suspended iterator runs the finally blocks around its `yield return`, innermost first, as
    /// .NET does; then the iterator is finished.</summary>
    void EmitIteratorDispose()
    {
        var info = _machine!;
        if (info.DisposeFn == null) return;
        var save = E;
        var e = new Emitter(info.DisposeFn);
        int self = info.AsyncIterator ? 1 : 0;
        int exit = _cfg!.Blocks.Length - 1;
        foreach (var id in _yieldResumes)
        {
            var b = _cfg.Blocks[_segments[id].block];
            var fins = new List<ControlFlowRegion>();
            ControlFlowRegion? child = null;
            for (var x = b.EnclosingRegion; x != null; child = x, x = x.EnclosingRegion)
                if (x.Kind == ControlFlowRegionKind.TryAndFinally && child?.Kind == ControlFlowRegionKind.Try)
                    fins.Add(x.NestedRegions.First(n => n.Kind == ControlFlowRegionKind.Finally));
            if (fins.Count == 0) continue;
            e.Get(self); e.Load(Op.I32Load, _dispatchStateOff); e.I32(id); e.Op(Op.I32Eq);
            e.If();
            for (int i = 0; i < fins.Count; i++)
            {
                var (retOff, _) = FinallySlots(fins[i]);
                int next = i + 1 < fins.Count ? fins[i + 1].FirstBlockOrdinal : exit;
                e.Get(self); e.I32(next); e.Store(Op.I32Store, retOff);
            }
            e.Get(self); e.I32(fins[0].FirstBlockOrdinal); e.Store(Op.I32Store, _dispatchStateOff);
            if (info.AsyncIterator)
            {
                var (promiseOff, promiseT) = info.Machine.ExtraSlots["promise"];
                var create = ((INamedTypeSymbol)promiseT).GetMembers("CreateForAsyncT").OfType<IMethodSymbol>().First();
                int p = e.AddLocal(ValType.I32);
                e.Call(D.Method(D.CanonicalMethod(create)).Fn); e.Set(p);
                e.Get(self); e.Load(Op.I32Load, promiseOff); e.Call(Rt.Release);
                e.Get(self); e.Get(p); e.Store(Op.I32Store, promiseOff);
                e.Get(self); e.Call(info.MoveNext);
                e.Get(0); e.Get(p); e.Call(D.LibMethod("Cs2Wasm.Runtime.AsyncIter", "WrapDispose", 1).Fn);
            }
            else { e.Get(self); e.Call(info.MoveNext); e.Op(Op.Drop); }
            e.Op(Op.Return);
            e.End();
        }
        e.Get(self); e.I32(-1); e.Store(Op.I32Store, _dispatchStateOff);
        if (info.AsyncIterator) { e.Get(0); e.I32(0); e.I32(T.StorageOf(T.Canonical(D.LibType("System.Threading.Tasks.ValueTask"))).size); e.Op(Op.MemoryFill); }
    }

    int NewSegment(int block, int op, int action)
    {
        int id = _segments.Count;
        _segments.Add((id, block, op, action));
        _segmentQueue.Enqueue(id);
        return id;
    }

    void SetState(int id) { E.Get(0); E.I32(id); E.Store(Op.I32Store, _dispatchStateOff); }

    void Goto(int segment) { SetState(segment); E.Br(_dispatch!); }

    void EmitSegment(BasicBlock b, int opStart, int actionStart)
    {
        _curBlock = b;
        var info = _machine!;
        if (b.Kind == BasicBlockKind.Entry) { EmitCfgBranch(b.FallThroughSuccessor!, null); return; }
        if (b.Kind == BasicBlockKind.Exit) { EmitMachineCompletion(null); return; }
        for (int i = opStart; i < b.Operations.Length; i++)
        {
            var op = b.Operations[i];
            int startAction = i == opStart ? actionStart : 0;
            if (ContainsAwait(op))
            {
                if (!EmitAwaitPlan(op, b, i, startAction)) return; // suspended
                if (op is IAwaitOperation) { _precomputed.Remove(op); continue; } // value discarded
            }
            if (op is IReturnOperation { Kind: OperationKind.YieldReturn } yr)
            {
                BeginTemps();
                var cur = info.Machine.ExtraSlots["current"];
                var o = EmitValueAs(yr.ReturnedValue!, info.ElementType!);
                StoreToAddressFromStack(() => E.Get(0), cur.offset, info.ElementType!, o);
                EndTemps();
                int resume = NewSegment(b.Ordinal, i + 1, 0);
                _yieldResumes.Add(resume);
                SetState(resume);
                if (info.AsyncIterator) { CompletePromise(true); E.Op(Op.Return); return; }
                E.I32(1); E.Op(Op.Return);
                return;
            }
            EmitStatement(op);
        }
        if (b.BranchValue != null && ContainsAwait(b.BranchValue))
        {
            if (!EmitAwaitPlan(b.BranchValue, b, b.Operations.Length, actionStart)) return;
        }
        if (b.ConditionKind != ControlFlowConditionKind.None)
        {
            BeginTemps(); EmitCondition(b.BranchValue!); EndTemps(ValType.I32);
            if (b.ConditionKind == ControlFlowConditionKind.WhenFalse) E.Op(Op.I32Eqz);
            E.If();
            EmitCfgBranch(b.ConditionalSuccessor!, null);
            E.End();
            EmitCfgBranch(b.FallThroughSuccessor!, null);
            return;
        }
        EmitCfgBranch(b.FallThroughSuccessor!, b.BranchValue);
    }

    void EmitCfgBranch(ControlFlowBranch br, IOperation? value)
    {
        var info = _machine!;
        switch (br.Semantics)
        {
            case ControlFlowBranchSemantics.Regular:
                {
                    int dest = br.Destination!.Ordinal;
                    if (br.FinallyRegions.Length > 0) { ChainFinally(br.FinallyRegions, dest); return; }
                    Goto(dest);
                    return;
                }
            case ControlFlowBranchSemantics.Return:
                {
                    if (value != null && info.IsAsync && info.ResultType != null)
                    {
                        var rv = info.Machine.Extra("retval", info.ResultType, T);
                        BeginTemps();
                        var o = EmitValueAs(value, info.ResultType);
                        StoreToAddressFromStack(() => E.Get(0), rv, info.ResultType, o);
                        EndTemps();
                    }
                    int exit = _cfg!.Blocks.Length - 1;
                    if (br.FinallyRegions.Length > 0) { ChainFinally(br.FinallyRegions, exit); return; }
                    Goto(exit);
                    return;
                }
            case ControlFlowBranchSemantics.Throw:
                BeginTemps(); EmitThrow(value); EndTemps(); return;
            case ControlFlowBranchSemantics.Rethrow:
                {
                    var catchRegion = EnclosingOf(_curBlock!.EnclosingRegion, ControlFlowRegionKind.Catch) ?? throw new CompileException("rethrow outside catch");
                    int cs = CaughtSlot(catchRegion);
                    E.Get(0); E.Load(Op.I32Load, cs); E.Call(Rt.Retain);
                    E.Emit(Op.Throw, 0, 0, D.ExceptionTag);
                    return;
                }
            case ControlFlowBranchSemantics.StructuredExceptionHandling:
                {
                    // end of a finally: rethrow a pending exception, else continue where the finally was entered from
                    var fin = EnclosingOf(_curBlock!.EnclosingRegion, ControlFlowRegionKind.Finally) ?? throw new CompileException("end of finally");
                    var (retOff, exnOff) = FinallySlots(fin);
                    E.Get(0); E.Load(Op.I32Load, exnOff);
                    E.If();
                    int p = Scratch(ValType.I32);
                    E.Get(0); E.Load(Op.I32Load, exnOff); E.Set(p);
                    E.Get(0); E.I32(0); E.Store(Op.I32Store, exnOff);
                    E.Get(p); E.Emit(Op.Throw, 0, 0, D.ExceptionTag);
                    FreeScratch(ValType.I32, p);
                    E.End();
                    E.Get(0); E.Get(0); E.Load(Op.I32Load, retOff); E.Store(Op.I32Store, _dispatchStateOff);
                    E.Br(_dispatch!);
                    return;
                }
            default:
                E.Op(Op.Unreachable);
                return;
        }
    }

    void ChainFinally(System.Collections.Immutable.ImmutableArray<ControlFlowRegion> finallies, int dest)
    {
        for (int i = 0; i < finallies.Length; i++)
        {
            var (retOff, exnOff) = FinallySlots(finallies[i]);
            int next = i + 1 < finallies.Length ? finallies[i + 1].FirstBlockOrdinal : dest;
            E.Get(0); E.I32(next); E.Store(Op.I32Store, retOff);
        }
        Goto(finallies[0].FirstBlockOrdinal);
    }

    (int ret, int exn) FinallySlots(ControlFlowRegion fin)
    {
        var key = "fin" + fin.FirstBlockOrdinal;
        return (_machine!.Machine.Extra(key + ".ret", IntType, T), _machine.Machine.Extra(key + ".exn", D.ObjectType, T));
    }

    int CaughtSlot(ControlFlowRegion catchRegion) => _machine!.Machine.Extra("caught" + catchRegion.FirstBlockOrdinal, D.ObjectType, T);

    static ControlFlowRegion? EnclosingOf(ControlFlowRegion r, ControlFlowRegionKind kind)
    {
        for (var x = r; x != null; x = x.EnclosingRegion) if (x.Kind == kind) return x;
        return null;
    }

    /// <summary>The method finished (Exit block): iterator done, or the task completes.</summary>
    void EmitMachineCompletion(IOperation? unused)
    {
        var info = _machine!;
        SetState(-1);
        if (info.AsyncIterator) { CompletePromise(false); E.Op(Op.Return); return; }
        if (info.IsIterator) { E.I32(0); E.Op(Op.Return); return; }
        if (info.TaskType != null)
        {
            var task = info.Machine.ExtraSlots["task"].offset;
            if (info.ResultType == null)
            {
                E.Get(0); E.Load(Op.I32Load, task); E.Call(D.LibMethod("System.Threading.Tasks.Task", "TrySetResultCore", 0).Fn); E.Op(Op.Drop);
            }
            else
            {
                var rv = info.Machine.Extra("retval", info.ResultType, T);
                var set = ((INamedTypeSymbol)info.TaskType).GetMembers("TrySetResultT").OfType<IMethodSymbol>().First();
                CallWith(set, false, () => { E.Get(0); E.Load(Op.I32Load, task); }, () =>
                {
                    E.Get(0);
                    if (Types.IsStruct(info.ResultType)) { E.I32(rv); E.Op(Op.I32Add); }
                    else E.Load(Types.LoadOp(info.ResultType), rv);
                }, null, null);
                E.Op(Op.Drop);
            }
        }
        E.Op(Op.Return);
    }

    /// <summary>After an exception escaped a segment: find the innermost catch or finally of that segment's region.</summary>
    void EmitMachineExceptionDispatch(int ex, int exn)
    {
        var info = _machine!;
        // group segments by block region
        foreach (var seg in _segments)
        {
            var b = _cfg!.Blocks[seg.block];
            var chain = HandlerChain(b.EnclosingRegion);
            if (chain.Count == 0) continue;
            E.Get(_curLocal); E.I32(seg.id); E.Op(Op.I32Eq);
            E.If();
            foreach (var (kind, region) in chain)
            {
                if (kind == 0)
                {
                    // catch region: type test
                    var et = region.ExceptionType is { } t0 ? T.Resolve(t0, Map) : null;
                    bool all = et == null || Types.FullName(et) is "System.Exception" or "System.Object";
                    if (!all) { E.Get(ex); E.Call(Rt.IsInst(et!)); } else E.I32(1);
                    E.If();
                    int cs = CaughtSlot(region);
                    E.Get(0); E.Load(Op.I32Load, cs); E.Call(Rt.Release);
                    E.Get(0); E.Get(ex); E.Store(Op.I32Store, cs);
                    E.Get(ex); E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "OnCatch", 1).Fn);
                    Goto(region.FirstBlockOrdinal);
                    E.End();
                }
                else
                {
                    var (_, exnOff) = FinallySlots(region);
                    E.Get(0); E.Get(ex); E.Store(Op.I32Store, exnOff);
                    Goto(region.FirstBlockOrdinal);
                }
            }
            E.End();
        }
        // nothing handles it
        if (info.AsyncIterator)
        {
            SetState(-1);
            var promise = info.Machine.ExtraSlots["promise"].offset;
            E.Get(0); E.Load(Op.I32Load, promise); E.Get(ex);
            E.Call(D.LibMethod("System.Threading.Tasks.Task", "TrySetExceptionCore", 1).Fn); E.Op(Op.Drop);
            E.Get(ex); E.Call(Rt.Release);
            E.Op(Op.Return);
            return;
        }
        if (info.IsAsync && info.TaskType != null)
        {
            SetState(-1);
            var task = info.Machine.ExtraSlots["task"].offset;
            E.Get(0); E.Load(Op.I32Load, task); E.Get(ex);
            E.Call(D.LibMethod("System.Threading.Tasks.Task", "TrySetExceptionCore", 1).Fn); E.Op(Op.Drop);
            E.Get(ex); E.Call(Rt.Release);
            E.Op(Op.Return);
            return;
        }
        if (info.IsAsync)
        {
            // async void: the exception goes to the engine
            SetState(-1);
            E.Get(ex); E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Fn);
            E.Get(ex); E.Call(Rt.Release);
            E.Op(Op.Return);
            return;
        }
        SetState(-1);
        E.Get(exn); E.Op(Op.ThrowRef);
    }

    /// <summary>Handlers an exception raised in `r` reaches, innermost first: (0, catch) or (1, finally), up to the first finally.</summary>
    static List<(int kind, ControlFlowRegion region)> HandlerChain(ControlFlowRegion r)
    {
        var result = new List<(int, ControlFlowRegion)>();
        ControlFlowRegion? child = null;
        for (var x = r; x != null; child = x, x = x.EnclosingRegion)
        {
            if (child?.Kind != ControlFlowRegionKind.Try) continue;
            if (x.Kind == ControlFlowRegionKind.TryAndCatch)
                foreach (var n in x.NestedRegions) if (n.Kind == ControlFlowRegionKind.Catch) result.Add((0, n));
            if (x.Kind == ControlFlowRegionKind.TryAndFinally)
            {
                var fin = x.NestedRegions.First(n => n.Kind == ControlFlowRegionKind.Finally);
                result.Add((1, fin));
                break; // the finally rethrows; its own segment's chain continues outward
            }
        }
        return result;
    }

    // =====================================================================
    // await
    // =====================================================================
    static bool ContainsAwait(IOperation op) => op.DescendantsAndSelf().Any(o => o is IAwaitOperation);

    /// <summary>Evaluation actions for a statement containing awaits: values to its left are spilled into the machine first.</summary>
    List<(int kind, IOperation op)> PlanFor(IOperation stmt)
    {
        if (_plans.TryGetValue(stmt, out var plan)) return plan;
        plan = [];
        void Visit(IOperation o)
        {
            if (!ContainsAwait(o)) return;
            if (o is IAwaitOperation a)
            {
                Visit(a.Operation);
                plan.Add((1, a)); // start: get awaiter, maybe suspend
                plan.Add((2, a)); // end: get result
                return;
            }
            if (o is IConditionalOperation or ICoalesceOperation or IConditionalAccessOperation or IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr })
                throw new CompileException("await inside a conditional expression is not supported yet", o.Syntax.GetLocation());
            var children = EvalChildren(o).ToList();
            int last = children.FindLastIndex(ContainsAwait);
            for (int j = 0; j < last; j++)
            {
                var c = children[j];
                if (ContainsAwait(c)) Visit(c);
                else if (NeedsSpill(c)) plan.Add((0, c));
            }
            Visit(children[last]);
        }
        Visit(stmt);
        _plans[stmt] = plan;
        return plan;
    }

    static IEnumerable<IOperation> EvalChildren(IOperation o)
    {
        switch (o)
        {
            case IAssignmentOperation asg:
                foreach (var c in TargetParts(asg.Target)) yield return c;
                yield return asg.Value;
                yield break;
            case IArgumentOperation arg: yield return arg.Value; yield break;
        }
        foreach (var c in o.ChildOperations)
        {
            if (c is IArgumentOperation a) yield return a.Value;
            else yield return c;
        }
    }

    static IEnumerable<IOperation> TargetParts(IOperation target) => target switch
    {
        IFieldReferenceOperation { Instance: { } i } => [i],
        IPropertyReferenceOperation p => (p.Instance != null ? new[] { p.Instance } : []).Concat(p.Arguments.Select(a => a.Value)),
        IArrayElementReferenceOperation ae => new[] { ae.ArrayReference }.Concat(ae.Indices),
        _ => [],
    };

    static bool NeedsSpill(IOperation c) => !(c.ConstantValue.HasValue || c is IInstanceReferenceOperation or ILocalReferenceOperation { IsDeclaration: true });

    /// <summary>Runs the statement's await actions from `start`; returns false if the method suspended.</summary>
    bool EmitAwaitPlan(IOperation stmt, BasicBlock b, int opIndex, int start)
    {
        var plan = PlanFor(stmt);
        var info = _machine!;
        for (int k = start; k < plan.Count; k++)
        {
            var (kind, op) = plan[k];
            switch (kind)
            {
                case 0:
                    {
                        var t = TypeOf(op);
                        int off = info.Machine.Extra("spill" + (_spillCounter++), t, T);
                        BeginTemps();
                        var o = EmitValue(op);
                        StoreToAddressFromStack(() => E.Get(0), off, t, o);
                        EndTemps();
                        _precomputed[op] = (off, t);
                        break;
                    }
                case 1:
                    {
                        var a = (IAwaitOperation)op;
                        var ai = AwaitInfo(a);
                        var getAwaiter = SubstMethod(ai.getAwaiter);
                        var awT = T.Canonical(getAwaiter.ReturnType);
                        int awOff = info.Machine.Extra("awaiter" + AwaitKey(a), awT, T);
                        BeginTemps();
                        Own ow;
                        if (getAwaiter.IsStatic) ow = EmitCallOps(getAwaiter, null, [a.Operation], false, a.Syntax, alreadyResolved: true);
                        else ow = EmitCallOps(getAwaiter, a.Operation, [], IsVirtualCall(a.Operation, getAwaiter), a.Syntax, alreadyResolved: true);
                        StoreToAddressFromStack(() => E.Get(0), awOff, awT, ow);
                        EndTemps();
                        int resume = NewSegment(b.Ordinal, opIndex, k + 1);
                        // if (!awaiter.IsCompleted) { state = resume; awaiter.OnCompleted(MoveNext); return; }
                        BeginTemps();
                        var isc = SubstMethod(ai.isCompleted.GetMethod!);
                        CallWith(isc, false, () => PushAwaiter(awOff, awT), () => { }, null, null);
                        EndTemps(ValType.I32);
                        E.Op(Op.I32Eqz);
                        E.If();
                        SetState(resume);
                        BeginTemps();
                        var onCompleted = FindOnCompleted(awT) ?? throw new CompileException("awaiter " + awT + " has no OnCompleted(Action)", a.Syntax.GetLocation());
                        var actionT = T.Canonical(D.Main.GetTypeByMetadataName("System.Action")!);
                        CallWith(onCompleted, false, () => PushAwaiter(awOff, awT), () =>
                        {
                            E.Get(0); E.Call(Rt.Retain);
                            NewDelegate(actionT, info.MoveNext.Type.Results.Length == 0 ? info.MoveNext : MoveNextVoidThunk(info));
                            int dl = TempRef(actionT); E.Tee(dl);
                        }, null, null);
                        EndTemps();
                        if (info.SyncIterator) E.I32(0);
                        E.Op(Op.Return);
                        E.End();
                        Goto(resume);
                        return false;
                    }
                case 2:
                    {
                        var a = (IAwaitOperation)op;
                        var ai = AwaitInfo(a);
                        var getAwaiter = SubstMethod(ai.getAwaiter);
                        var awT = T.Canonical(getAwaiter.ReturnType);
                        int awOff = info.Machine.Extra("awaiter" + AwaitKey(a), awT, T);
                        var gr = SubstMethod(ai.getResult);
                        var rt = T.Canonical(gr.ReturnType);
                        BeginTemps();
                        if (Types.IsVoid(rt))
                        {
                            CallWith(gr, false, () => PushAwaiter(awOff, awT), () => { }, null, null);
                            _precomputed[a] = (-1, rt);
                        }
                        else
                        {
                            int roff = info.Machine.Extra("result" + AwaitKey(a), rt, T);
                            var ro = CallWith(gr, false, () => PushAwaiter(awOff, awT), () => { }, null, null);
                            StoreToAddressFromStack(() => E.Get(0), roff, rt, ro);
                            _precomputed[a] = (roff, rt);
                        }
                        EndTemps();
                        // the awaiter is done: drop what it holds
                        if (T.NeedsRc(awT))
                        {
                            if (Types.IsRef(awT)) { E.Get(0); E.Load(Op.I32Load, awOff); E.Call(Rt.Release); E.Get(0); E.I32(0); E.Store(Op.I32Store, awOff); }
                            else { E.Get(0); E.I32(awOff); E.Op(Op.I32Add); E.Call(Rt.ReleaseStruct(awT)); E.Get(0); E.I32(awOff); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(awT).size); E.Op(Op.MemoryFill); }
                        }
                        break;
                    }
            }
        }
        return true;
    }

    readonly Dictionary<IOperation, int> _awaitKeys = [];
    int AwaitKey(IAwaitOperation a) { if (!_awaitKeys.TryGetValue(a, out var k)) _awaitKeys[a] = k = _awaitKeys.Count; return k; }

    /// <summary>The awaiter pattern of an await: from the semantic model for `await e`, or by lookup for the awaits
    /// the control-flow graph makes itself (`await foreach`, `await using`).</summary>
    (IMethodSymbol getAwaiter, IPropertySymbol isCompleted, IMethodSymbol getResult) AwaitInfo(IAwaitOperation a)
    {
        if (a.Syntax is AwaitExpressionSyntax aes)
        {
            var ai = D.ModelFor(a.Syntax.SyntaxTree).GetAwaitExpressionInfo(aes);
            if (ai.GetAwaiterMethod == null || ai.GetResultMethod == null || ai.IsCompletedProperty == null) throw new CompileException("await on this type is not supported", a.Syntax.GetLocation());
            return (ai.GetAwaiterMethod, ai.IsCompletedProperty, ai.GetResultMethod);
        }
        var ot = a.Operation.Type ?? throw new CompileException("await without a type", a.Syntax.GetLocation());
        IMethodSymbol? ga = null;
        for (var t = ot as INamedTypeSymbol; t != null && ga == null; t = t.BaseType)
            ga = t.GetMembers("GetAwaiter").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && !m.IsStatic);
        if (ga == null) throw new CompileException("await on " + ot + " is not supported", a.Syntax.GetLocation());
        var awT = ga.ReturnType;
        var isc = awT.GetMembers("IsCompleted").OfType<IPropertySymbol>().First();
        var gr = awT.GetMembers("GetResult").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
        return (ga, isc, gr);
    }

    WasmFunction MoveNextVoidThunk(MachineInfo info)
    {
        var f = D.Module.AddFunction("MoveNext thunk", new FuncType([ValType.I32], []));
        var e = new Emitter(f); e.Get(0); e.Call(info.MoveNext); e.Op(Op.Drop);
        return f;
    }

    void PushAwaiter(int awOff, ITypeSymbol awT)
    {
        E.Get(0);
        if (Types.IsStruct(awT) || Types.PrimOf(awT) != Prim.None) { E.I32(awOff); E.Op(Op.I32Add); }
        else E.Load(Op.I32Load, awOff);
    }

    IMethodSymbol? FindOnCompleted(ITypeSymbol awT)
    {
        foreach (var name in new[] { "UnsafeOnCompleted", "OnCompleted" })
            for (var t = awT as INamedTypeSymbol; t != null; t = t.BaseType is { } bt ? (INamedTypeSymbol)T.Canonical(bt) : null)
            {
                var m = t.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.Length == 1 && !x.IsStatic && x.DeclaredAccessibility == Accessibility.Public);
                if (m != null) return D.CanonicalMethod(m);
                if (t.IsValueType) break;
            }
        // explicit interface implementation
        var inc = D.Main.GetTypeByMetadataName("System.Runtime.CompilerServices.INotifyCompletion")!;
        var slot = inc.GetMembers("OnCompleted").OfType<IMethodSymbol>().First();
        return D.Implementation(awT, slot);
    }

    /// <summary>A value computed before an await (or an await's result), read back from the machine.</summary>
    bool TryPrecomputed(IOperation op, out Own own)
    {
        own = Own.None;
        if (!_precomputed.TryGetValue(op, out var p)) return false;
        if (p.off < 0) return true; // a void await: nothing to push
        E.Get(0);
        if (Types.IsStruct(p.type)) { E.I32(p.off); E.Op(Op.I32Add); return true; }
        E.Load(Types.LoadOp(p.type), p.off);
        own = Types.IsRef(p.type) ? Own.Unstable : Own.None;
        return true;
    }

    // CFG-only operations
    Own EmitFlowCapture(IFlowCaptureOperation fc)
    {
        var t = fc.Value.Type != null ? TypeOf(fc.Value) : D.ObjectType;
        int off = _machine!.Machine.Extra("cap" + fc.Id.GetHashCode() + "_" + fc.Syntax.SpanStart, t, T);
        _captureSlots[fc.Id] = (off, t);
        BeginTemps();
        var o = EmitValue(fc.Value);
        StoreToAddressFromStack(() => E.Get(0), off, t, o);
        EndTemps();
        return Own.None;
    }

    readonly Dictionary<CaptureId, (int off, ITypeSymbol type)> _captureSlots = [];

    Own EmitFlowCaptureReference(IFlowCaptureReferenceOperation r)
    {
        if (!_captureSlots.TryGetValue(r.Id, out var s)) throw new CompileException("flow capture read before write");
        E.Get(0);
        if (Types.IsStruct(s.type)) { E.I32(s.off); E.Op(Op.I32Add); return Own.None; }
        E.Load(Types.LoadOp(s.type), s.off);
        var want = r.Type != null ? TypeOf(r) : s.type;
        var o = Types.IsRef(s.type) ? Own.Unstable : Own.None;
        if (Types.Key(want) != Types.Key(s.type)) ConvertTop(s.type, want, ref o);
        return o;
    }

    Own EmitCaughtException()
    {
        var region = EnclosingOf(_curBlock!.EnclosingRegion, ControlFlowRegionKind.Catch) ?? throw new CompileException("caught exception outside catch");
        E.Get(0); E.Load(Op.I32Load, CaughtSlot(region));
        return Own.Unstable;
    }

    Own EmitIsNull(IIsNullOperation n)
    {
        var t = TypeOf(n.Operand);
        var o = EmitValue(n.Operand);
        if (IsNullable(t)) { NullableHas(t); E.Op(Op.I32Eqz); return Own.None; }
        if (t.IsValueType) { Drop(o, t); E.I32(0); return Own.None; }
        ToTransient(o, t);
        E.Op(Op.I32Eqz);
        return Own.None;
    }
}
