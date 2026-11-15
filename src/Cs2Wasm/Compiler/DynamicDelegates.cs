using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

// Delegates made at run time for a delegate type and a handler over object[] (System.Linq.Expressions' interpreter,
// through Internal.Runtime.Augments.DynamicDelegateAugments, as on NativeAOT): the program is whole, so every delegate
// type it names gets a thunk with that type's Invoke signature whose target is the handler; it boxes the arguments
// into an object[], calls the handler, and converts the result back. Rt.ObjectArrayDelegate(type id, handler) picks
// the thunk by type id (written at the end, over the thunks made); a type without one gives null.
//
// [UnsafeAccessor] externs: a body that calls the member they name (constructor, method or static method).
sealed partial class FunctionCompiler
{
    ITypeSymbol? _objectArrayThunkHandler;

    void EmitObjectArrayThunk()
    {
        var handlerT = (INamedTypeSymbol)_objectArrayThunkHandler!;
        var objT = D.ObjectType;
        var arrT = T.Canonical(D.LibCompilation.CreateArrayTypeSymbol(objT));
        int n = M.Parameters.Length;
        int arrTid = D.MarkConstructed(arrT);
        int arr = E.AddLocal(ValType.I32);
        E.I32(n); E.I32(4); E.Tid(arrTid); E.Call(Rt.AllocArray); E.Set(arr);
        AddOwned(new Slot { Local = arr, Type = arrT });
        for (int i = 0; i < n; i++)
        {
            var p = M.Parameters[i];
            var v = _vars[p];
            BeginTemps();
            var o = LoadVar(v);
            ConvertTop(v.Type, objT, ref o);
            StoreToAddressFromStack(() => E.Get(arr), Types.ArrayDataOffset(1) + 4 * i, objT, o, pub: Publish.None);
            EndTemps();
        }
        var invoke = D.CanonicalMethod(handlerT.DelegateInvokeMethod!);
        var r = CallWith(invoke, false, () => E.Get(_thisLocal), () => E.Get(arr), null, null);
        var rt = T.Resolve(M.ReturnType, Map);
        if (M.ReturnsVoid) { Drop(r, objT); EmitReturnSequence(false); return; }
        ConvertValue(objT, rt, false, ref r, true, null);
        if (Types.IsRef(rt)) ToOwned(r);
        _retLocal = E.AddLocal(Types.WasmType(rt) ?? ValType.I32);
        E.Set(_retLocal);
        EmitReturnSequence(true);
    }

    void EmitUnsafeAccessorBody()
    {
        var def = M.OriginalDefinition;
        var attr = def.GetAttributes().First(a => a.AttributeClass?.Name == "UnsafeAccessorAttribute");
        int kind = attr.ConstructorArguments[0].Value is int k ? k : -1;
        string name = attr.NamedArguments.FirstOrDefault(a => a.Key == "Name").Value.Value as string ?? def.Name;
        ITypeSymbol TypeOfParam(IParameterSymbol p)
        {
            if (p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "UnsafeAccessorTypeAttribute") is { ConstructorArguments: [{ Value: string tn }] })
            {
                var meta = tn.Split(',')[0].Trim();
                return D.Lib.TypeByMetadataName(meta) ?? throw new CompileException("[UnsafeAccessorType] names a type the library lacks: " + meta, def.Locations.FirstOrDefault());
            }
            return T.Resolve(p.Type, Map) is IPointerTypeSymbol or { IsReferenceType: true } or { IsValueType: true } ? T.Resolve(p.Type, Map) : p.Type;
        }
        // 0 constructor, 1 method, 2 static method (field kinds give a reference to the field: not yet)
        if (kind is not (0 or 1 or 2)) throw new CompileException("[UnsafeAccessor] of kind " + kind + " is not supported yet", def.Locations.FirstOrDefault());
        var target = kind == 0 ? (INamedTypeSymbol)T.Resolve(def.ReturnType, Map) : (INamedTypeSymbol)TypeOfParam(M.Parameters[0]);
        var rest = kind == 0 ? M.Parameters.ToList() : M.Parameters.Skip(1).ToList();
        IEnumerable<IMethodSymbol> candidates = kind == 0 ? target.InstanceConstructors : target.GetMembers(name).OfType<IMethodSymbol>().Where(m => m.IsStatic == (kind == 2));
        var tm = candidates.FirstOrDefault(m => m.Parameters.Length == rest.Count
                     && m.Parameters.Zip(rest).All(x => Types.Key(T.Canonical(x.First.Type)) == Types.Key(T.Canonical(T.Resolve(x.Second.Type, Map))) || x.Second.GetAttributes().Any(a => a.AttributeClass?.Name == "UnsafeAccessorTypeAttribute")))
                 ?? throw new CompileException($"[UnsafeAccessor] target {target}.{name} not found", def.Locations.FirstOrDefault());
        tm = D.CanonicalMethod(tm);
        Own r;
        if (kind == 0)
        {
            int tid = D.MarkConstructed(target);
            int obj = E.AddLocal(ValType.I32);
            E.I32(Math.Max(T.LayoutOf(target).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
            CallWith(tm, false, () => E.Get(obj), () => { foreach (var p in rest) PushParam(p); }, null, null);
            E.Get(obj);
            r = Own.Owned;
        }
        else r = CallWith(tm, false, kind == 1 ? () => PushParam(M.Parameters[0]) : null, () => { foreach (var p in rest) PushParam(p); }, null, null);
        if (M.ReturnsVoid) { EmitReturnSequence(false); return; }
        var rt = T.Resolve(M.ReturnType, Map);
        if (Types.IsRef(rt)) ToOwned(r);
        _retLocal = E.AddLocal(Types.WasmType(rt) ?? ValType.I32);
        E.Set(_retLocal);
        EmitReturnSequence(true);

        void PushParam(IParameterSymbol p)
        {
            var v = _vars[p];
            var o = LoadVar(v);
            if (Types.IsRef(v.Type)) ToCallSafe(o, v.Type);
        }
    }

    internal static MethodInst ObjectArrayThunk(Driver d, INamedTypeSymbol delegateType, INamedTypeSymbol handlerType)
    {
        var invoke = d.CanonicalMethod(delegateType.DelegateInvokeMethod!);
        var key = "object[] thunk " + Types.Key(delegateType);
        var fn = d.Module.AddFunction(key, Driver.Signature(invoke, d.Types));
        var mi = new MethodInst { Sym = invoke, Fn = fn, Key = key, Impl = MethodImpl.Synthesized };
        var m = mi;
        mi.Compile = () => { var fc = new FunctionCompiler(d, fn, invoke, m) { _objectArrayThunkHandler = handlerType }; fc.CompileMethod(); };
        d.EnqueueExtra(mi);
        return mi;
    }
}

internal sealed partial class Driver
{
    WasmFunction? _objectArrayDelegate;
    /// <summary>The thunks made so far, by delegate type id.</summary>
    readonly Dictionary<int, MethodInst> _objectArrayThunks = [];

    /// <summary>(type id, handler) → delegate or null: Rt.ObjectArrayDelegate (body at the end).</summary>
    public WasmFunction ObjectArrayDelegateFn => _objectArrayDelegate ??= Module.AddFunction("rt.object_array_delegate", new FuncType([ValType.I32, ValType.I32], [ValType.I32]));

    readonly HashSet<int> _attrArraysDone = [];

    /// <summary>GetCustomAttributes(type) returns an array of the attribute type asked for (Array.CreateInstance):
    /// once the program can ask, arrays of the attribute types it names are made.</summary>
    public bool ExpandAttributeArrays()
    {
        if (!AllMethods.Any(m => m.Compiled && m.Sym.Name == "GetCustomAttributes" && m.Sym.ContainingType?.Name == "CustomAttribute")) return false;
        bool any = false;
        var attr = LibType("System.Attribute");
        for (int id = 0; id < TypeById.Count; id++)
        {
            if (!_attrArraysDone.Add(id) || SyntheticById.ContainsKey(id) || DisplayById.ContainsKey(id)) continue;
            if (TypeById[id] is not INamedTypeSymbol { TypeKind: TypeKind.Class } at || !Types.IsSubtype(at, attr)) continue;
            int before = Constructed.Count;
            MarkConstructed(Types.LibCompilationArray(at));
            any |= Constructed.Count != before;
        }
        return any;
    }

    // Delegate.DynamicInvoke: per delegate type the program constructs, the reflection thunk of its Invoke (target:
    // the delegate; the arguments unboxed from the object[]; the result boxed), and its parameter count
    WasmFunction? _dynamicInvoke, _delegateArity;
    readonly Dictionary<int, WasmFunction> _dynamicThunks = [];
    public WasmFunction DynamicInvokeFn => _dynamicInvoke ??= Module.AddFunction("rt.dynamic_invoke", new FuncType([ValType.I32, ValType.I32], [ValType.I32]));
    public WasmFunction DelegateArityFn => _delegateArity ??= Module.AddFunction("rt.delegate_arity", new FuncType([ValType.I32], [ValType.I32]));

    public bool ExpandDynamicInvokeThunks()
    {
        if (_dynamicInvoke == null && _delegateArity == null) return false;
        bool any = false;
        foreach (var id in Constructed.ToList())
        {
            if (_dynamicThunks.ContainsKey(id) || SyntheticById.ContainsKey(id) || DisplayById.ContainsKey(id)) continue;
            if (TypeById[id] is not INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } inv }) continue;
            if (inv.Parameters.Any(p => p.RefKind != RefKind.None || p.Type.IsRefLikeType || p.Type is IPointerTypeSymbol) || inv.ReturnType.IsRefLikeType || inv.ReturnsByRef) continue;
            _dynamicThunks[id] = ThunkFor(CanonicalMethod(inv));
            any = true;
        }
        return any;
    }

    public void EmitDynamicInvoke()
    {
        if (_dynamicInvoke != null)
        {
            var e = new Emitter(_dynamicInvoke);
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            foreach (var (id, th) in _dynamicThunks)
            {
                e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
                e.If(); e.Get(0); e.Get(1); e.Call(th); e.Op(Op.Return); e.End();
            }
            e.Op(Op.Unreachable);
        }
        if (_delegateArity != null)
        {
            var e = new Emitter(_delegateArity);
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            foreach (var (id, _) in _dynamicThunks)
            {
                e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
                e.If(); e.I32(((INamedTypeSymbol)TypeById[id]).DelegateInvokeMethod!.Parameters.Length); e.Op(Op.Return); e.End();
            }
            e.I32(-1);
        }
    }

    /// <summary>A thunk for each closed delegate type the program names (while compiling: new types keep coming).</summary>
    public bool ExpandObjectArrayThunks()
    {
        if (_objectArrayDelegate == null) return false;
        var handler = (INamedTypeSymbol)Types.Canonical(LibType("System.Func`2").Construct(Types.Canonical(Main.CreateArrayTypeSymbol(ObjectType)), ObjectType));
        bool any = false;
        for (int id = 0; id < TypeById.Count; id++)
        {
            if (_objectArrayThunks.ContainsKey(id) || SyntheticById.ContainsKey(id) || DisplayById.ContainsKey(id)) continue;
            if (TypeById[id] is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } dt || dt.DelegateInvokeMethod == null) continue;
            if (dt.IsUnboundGenericType || dt.TypeArguments.Any(a => a is ITypeParameterSymbol) || dt.DelegateInvokeMethod.Parameters.Any(p => p.RefKind != RefKind.None) || dt.DelegateInvokeMethod.ReturnsByRef) continue;
            if (dt.DelegateInvokeMethod.Parameters.Any(p => p.Type.IsRefLikeType || p.Type is IPointerTypeSymbol) || dt.DelegateInvokeMethod.ReturnType.IsRefLikeType) continue;
            _objectArrayThunks[id] = FunctionCompiler.ObjectArrayThunk(this, dt, handler);
            // (made by the dispatch at run time: its destructor and dispatch cases are needed)
            MarkConstructed(dt);
            any = true;
        }
        return any;
    }

    /// <summary>The dispatch over the thunks: a new delegate of the type, the handler its target.</summary>
    public void EmitObjectArrayDelegate()
    {
        if (_objectArrayDelegate == null) return;
        var e = new Emitter(_objectArrayDelegate);
        foreach (var (id, th) in _objectArrayThunks)
        {
            var dt = TypeById[id];
            e.Get(0); e.Tid(id); e.Op(Op.I32Eq);
            e.If();
            e.I32(Math.Max(Types.LayoutOf(dt).Size, 24)); e.Tid(id); e.Call(Rt.AllocObject);
            int obj = e.AddLocal(ValType.I32); e.Tee(obj);
            e.Get(1); e.Call(Rt.Retain); e.Store(Op.I32Store, FunctionCompiler.DelegateTargetOffset);
            e.Get(obj); e.I32Fn(Module, th.Fn); e.Store(Op.I32Store, FunctionCompiler.DelegateFnOffset);
            e.Get(obj); e.Op(Op.Return);
            e.End();
        }
        e.I32(0);
    }
}
