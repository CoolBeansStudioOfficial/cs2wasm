using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>Escape analysis (section 5: "an object that never escapes needs no heap allocation").
///
/// A summary per method parameter (and `this`) says whether a value passed there can outlive the call: stored in a
/// field, an element or a static, captured, thrown, handed to code the compiler cannot see, or returned (a separate
/// answer: the caller then checks what it does with the result, so `sb.Append(x)` keeps `sb` local when its result is
/// dropped). Summaries are computed over the methods' bodies (the program's and the library's), memoized, and assume
/// a recursive use escapes. With the exact type of a new object known, its virtual calls resolve.
///
/// A local initialized by `new C(...)` whose every use is a call, field or property access, comparison or argument
/// that does not escape, never assigned again, captured or passed by reference, gets its object in the frame instead
/// of the heap: no allocation, no free, its counts left alone (the header says immortal), its fields released when
/// the local's scope ends. Whether C can be part of a reference cycle is known only once the whole program is
/// compiled; a type that can keeps the heap (the allocation and the drop are per-type functions written then).</summary>
internal sealed partial class Driver
{
    internal enum Esc { No = 0, Returned = 1, Yes = 2 }

    static Esc Max(Esc a, Esc b) => a > b ? a : b;

    readonly Dictionary<string, Esc> _escapes = [];

    /// <summary>Whether a value passed as parameter `index` of m (-1: the receiver) can outlive the call. `exact`: the
    /// receiver's exact type when it is known (it resolves virtual calls on `this`).</summary>
    internal Esc ParamEscape(IMethodSymbol m, int index, INamedTypeSymbol? exact)
    {
        var def = CanonicalMethod(m).OriginalDefinition;
        // (a plugin's hook in the method may be given what it was passed: PluginContext)
        if (HooksSee(def)) return Esc.Yes;
        UsedAnalysis();
        var key = Types.MethodKey(def) + "#" + index + "#" + (index < 0 && exact != null ? Types.Key(exact) : "");
        if (_escapes.TryGetValue(key, out var r)) return r;
        _escapes[key] = Esc.Yes; // (a recursive use, while deciding: assumed to escape)
        r = ComputeParamEscape(def, index, index < 0 ? exact : null);
        _escapes[key] = r;
        return r;
    }

    static IOperation? BodyOperation(Driver d, IMethodSymbol def)
    {
        foreach (var sr in def.DeclaringSyntaxReferences)
        {
            SyntaxNode? target = sr.GetSyntax() switch
            {
                BaseMethodDeclarationSyntax bm when bm.Body != null || bm.ExpressionBody != null => bm,
                AccessorDeclarationSyntax a when a.Body != null || a.ExpressionBody != null => a,
                ArrowExpressionClauseSyntax ar => ar,
                _ => null,
            };
            if (target != null && d.ModelFor(target.SyntaxTree).GetOperation(target) is { } b) return b;
        }
        return null;
    }

    Esc ComputeParamEscape(IMethodSymbol def, int index, INamedTypeSymbol? exact)
    {
        // runtime intrinsics that read an object without keeping it
        if (def.IsExtern && def.ContainingType.ContainingNamespace?.ToDisplayString() == "Cs2Wasm.Runtime"
            && (def.ContainingType.Name == "Mem" || def.ContainingType.Name == "Rt" && def.Name is "HashOf" or "TypeId" or "AddressOf"))
            return Esc.No;
        if (def.IsAbstract || def.IsExtern || def.IsAsync || def.IsIterator) { if (Environment.GetEnvironmentVariable("CS2WASM_ESCAPE") == "1") Console.Error.WriteLine($"escape:   {def.ToDisplayString()}: no body"); return Esc.Yes; }
        if (index >= 0 && (index >= def.Parameters.Length || def.Parameters[index].RefKind != RefKind.None)) return Esc.Yes;
        if (Library.Substitution(def) != null) return index < 0 ? Esc.No : Esc.Yes;
        var body = BodyOperation(this, def);
        if (body == null)
        {
            // an auto-property's accessors use `this` only for its field; an implicit constructor runs the base one
            if (def.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet && def.AssociatedSymbol is IPropertySymbol p
                && p.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is PropertyDeclarationSyntax { AccessorList: { } al } && al.Accessors.All(a => a.Body == null && a.ExpressionBody == null)))
                return index < 0 ? Esc.No : Esc.Yes;
            if (def.MethodKind == MethodKind.Constructor && def.IsImplicitlyDeclared && index < 0 && !def.ContainingType.IsValueType
                && !def.ContainingType.GetMembers().Any(m => m is IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: false }))
                return BaseConstructorEscape(def.ContainingType, exact);
            if (Environment.GetEnvironmentVariable("CS2WASM_ESCAPE") == "1") Console.Error.WriteLine($"escape:   {def.ToDisplayString()}: no body found");
            return Esc.Yes;
        }
        if (body.DescendantsAndSelf().Any(o => o.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak)) return Esc.Yes;
        var acc = Esc.No;
        // a constructor without `: this(...)`/`: base(...)` runs its base's parameterless one
        if (def.MethodKind == MethodKind.Constructor && index < 0 && body is IConstructorBodyOperation { Initializer: null } && !def.ContainingType.IsValueType)
            acc = BaseConstructorEscape(def.ContainingType, exact);
        if (acc == Esc.Yes) return acc;
        var nested = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).SelectMany(o => o.DescendantsAndSelf()).ToHashSet();
        foreach (var op in body.Descendants())
        {
            bool isRef = index < 0
                ? op is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
                : op is IParameterReferenceOperation pr && SymbolEqualityComparer.Default.Equals(pr.Parameter.OriginalDefinition, def.Parameters[index]);
            if (!isRef) continue;
            if (InNameOf(op)) continue;
            if (nested.Contains(op)) return Esc.Yes;
            if (op.Parent is ISimpleAssignmentOperation sa && sa.Target == op) continue; // (the parameter given another value)
            acc = Max(acc, UseEscape(op, exact));
            if (acc == Esc.Yes)
            {
                if (Environment.GetEnvironmentVariable("CS2WASM_ESCAPE") == "1") Console.Error.WriteLine($"escape:   {def.ToDisplayString()} #{index}: {op.Parent?.Kind} {op.Parent?.Syntax}");
                return acc;
            }
        }
        return acc;
    }

    static bool InNameOf(IOperation op)
    {
        for (var p = op.Parent; p != null; p = p.Parent) if (p is INameOfOperation) return true;
        return false;
    }

    Esc BaseConstructorEscape(INamedTypeSymbol type, INamedTypeSymbol? exact)
    {
        var b = type.BaseType;
        if (b == null || b.SpecialType == SpecialType.System_Object) return Esc.No;
        var ctor = b.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        return ctor == null ? Esc.Yes : ParamEscape(ctor, -1, exact);
    }

    /// <summary>What a use of a tracked value (v) lets it do: nothing, be returned, or escape. `alias`: a local may take
    /// the value (inside a callee, whose locals end before the object does; not where the object is a frame's).</summary>
    internal Esc UseEscape(IOperation v, INamedTypeSymbol? exact, bool alias = true)
    {
        var p = v.Parent;
        // a call the compiler omits ([Conditional] without its symbol, a partial method without a body): no use
        if (p is IInvocationOperation om && om.Instance == v && FunctionCompiler.IsOmittedConditionalCall(om)
            || p is IArgumentOperation { Parent: IInvocationOperation oa } && FunctionCompiler.IsOmittedConditionalCall(oa))
            return Esc.No;
        switch (p)
        {
            case IFieldReferenceOperation fr when fr.Instance == v:
                // reading or writing one of its fields; a reference to the field must not outlive the object
                return fr.Parent switch
                {
                    IAddressOfOperation => Esc.Yes,
                    IReturnOperation when fr.Syntax.Parent is RefExpressionSyntax => Esc.Yes,
                    ISimpleAssignmentOperation { IsRef: true } => Esc.Yes,
                    IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => Esc.Yes,
                    IArgumentOperation { Parameter.RefKind: not RefKind.None } a when a.Parent is IInvocationOperation ai
                        && (ai.TargetMethod.ReturnsByRef || ai.TargetMethod.ReturnsByRefReadonly || ai.TargetMethod.ReturnType.IsRefLikeType) => Esc.Yes,
                    _ => Esc.No,
                };
            case IPropertyReferenceOperation pr when pr.Instance == v:
                {
                    var prop = pr.Property;
                    if (prop.ReturnsByRef || prop.ReturnsByRefReadonly) return Esc.Yes;
                    bool write = pr.Parent is IAssignmentOperation asg && asg.Target == pr || pr.Parent is IIncrementOrDecrementOperation;
                    bool read = !(pr.Parent is ISimpleAssignmentOperation sa && sa.Target == pr);
                    var r = Esc.No;
                    foreach (var acc in new[] { read ? prop.GetMethod : null, write ? prop.SetMethod : null })
                    {
                        if (acc == null) continue;
                        var t = Resolve(acc, true, exact);
                        if (t == null) return Esc.Yes;
                        var e = ParamEscape(t, -1, exact);
                        if (e == Esc.Returned) e = UseEscape(pr, exact, alias);
                        r = Max(r, e);
                        if (r == Esc.Yes) return r;
                    }
                    return r;
                }
            case IInvocationOperation inv when inv.Instance == v:
                {
                    var t = Resolve(inv.TargetMethod, inv.IsVirtual, exact);
                    if (t == null) return Esc.Yes;
                    var e = ParamEscape(t, -1, exact);
                    if (e == Esc.Returned) e = UseEscape(inv, exact, alias);
                    return e;
                }
            case IArgumentOperation arg when arg.Value == v:
                {
                    if (arg.Parameter == null || arg.Parameter.RefKind != RefKind.None || arg.ArgumentKind != ArgumentKind.Explicit && arg.ArgumentKind != ArgumentKind.DefaultValue) return Esc.Yes;
                    IMethodSymbol? callee = arg.Parent switch
                    {
                        IInvocationOperation i => Resolve(i.TargetMethod, i.IsVirtual, null),
                        IObjectCreationOperation oc => oc.Constructor,
                        _ => null,
                    };
                    if (callee == null) return Esc.Yes;
                    var e = ParamEscape(callee, arg.Parameter.OriginalDefinition.Ordinal, null);
                    if (e == Esc.Returned) e = arg.Parent is IInvocationOperation call ? UseEscape(call, null, alias) : Esc.Yes;
                    return e;
                }
            case IConversionOperation conv when conv.Operand == v && conv.OperatorMethod == null && (conv.Conversion.IsIdentity || conv.Conversion.IsReference && conv.Conversion.IsImplicit):
                return UseEscape(conv, exact, alias);
            case IBinaryOperation { OperatorMethod: null }:
            case IIsTypeOperation:
            case IExpressionStatementOperation:
                return Esc.No;
            case IIsPatternOperation ip when ip.Value == v:
                if (ip.Pattern is IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol pl }) return alias ? AliasEscape(pl, v, exact) : Esc.Yes;
                return ip.Pattern is ITypePatternOperation or IConstantPatternOperation or INegatedPatternOperation { Pattern: ITypePatternOperation or IConstantPatternOperation }
                    or IDeclarationPatternOperation { DeclaredSymbol: null } ? Esc.No : Esc.Yes;
            // a local that takes the value: its uses are the value's
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: { RefKind: RefKind.None } il } }:
                return alias ? AliasEscape(il, v, exact) : Esc.Yes;
            case ISimpleAssignmentOperation { IsRef: false, Target: ILocalReferenceOperation { Local: { RefKind: RefKind.None } al } } asg when asg.Value == v && alias:
                return Max(AliasEscape(al, v, exact), asg.Parent is IExpressionStatementOperation ? Esc.No : UseEscape(asg, exact, alias));
            case IReturnOperation:
                return Esc.Returned;
            default:
                return Esc.Yes;
        }
    }

    readonly HashSet<ILocalSymbol> _aliasing = new(SymbolEqualityComparer.Default);

    /// <summary>The uses of a local that holds the tracked value (in the body the value is in).</summary>
    Esc AliasEscape(ILocalSymbol local, IOperation at, INamedTypeSymbol? exact)
    {
        if (!_aliasing.Add(local)) return Esc.No; // (its uses are being checked)
        try
        {
            var root = at;
            while (root.Parent != null) root = root.Parent;
            var r = Esc.No;
            foreach (var lr in root.DescendantsAndSelf().OfType<ILocalReferenceOperation>())
            {
                if (!SymbolEqualityComparer.Default.Equals(lr.Local, local)) continue;
                if (lr.Parent is ISimpleAssignmentOperation sa && sa.Target == lr) continue;
                if (lr.Parent is IAnonymousFunctionOperation || InLambda(lr)) return Esc.Yes;
                r = Max(r, UseEscape(lr, exact));
                if (r == Esc.Yes) return r;
            }
            return r;
        }
        finally { _aliasing.Remove(local); }
    }

    static bool InLambda(IOperation op)
    {
        for (var p = op.Parent; p != null; p = p.Parent) if (p is IAnonymousFunctionOperation or ILocalFunctionOperation) return true;
        return false;
    }

    /// <summary>The method a call runs: the target itself unless it is virtual, else the implementation on the exact
    /// receiver type (null when that type is not known).</summary>
    IMethodSymbol? Resolve(IMethodSymbol m, bool isVirtual, INamedTypeSymbol? exact)
    {
        var original = m;
        m = CanonicalMethod(m);
        var md = m.OriginalDefinition;
        bool iface = md.ContainingType.TypeKind == TypeKind.Interface;
        bool overridable = iface ? !md.IsStatic && (md.IsAbstract || md.IsVirtual) : (md.IsVirtual || md.IsAbstract || md.IsOverride) && !md.IsSealed && !md.ContainingType.IsSealed;
        if (!isVirtual && !iface || !overridable) return md;
        if (exact == null) return null;
        if (iface)
            return exact.FindImplementationForInterfaceMember(original) as IMethodSymbol ?? exact.FindImplementationForInterfaceMember(m) as IMethodSymbol;
        // (the program's types see the library through metadata: members compare by their library definitions)
        for (var t = exact; t != null; t = t.BaseType)
            foreach (var c in t.GetMembers(md.Name).OfType<IMethodSymbol>())
                for (var o = c; o != null; o = o.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(CanonicalMethod(o).OriginalDefinition, md)) return c;
        return null;
    }

    // ---- the frame objects' per-type functions ----

    readonly Dictionary<string, (INamedTypeSymbol type, WasmFunction alloc, WasmFunction drop)> _frameObjects = [];

    /// <summary>(frame address) → the object; (object) → () at the end of its local's scope.</summary>
    internal (WasmFunction alloc, WasmFunction drop) FrameObjectFns(INamedTypeSymbol t)
    {
        var key = Types.Key(t);
        if (_frameObjects.TryGetValue(key, out var x)) return (x.alloc, x.drop);
        var alloc = Module.AddFunction("frameobj.new " + key, new FuncType([ValType.I32], [ValType.I32]));
        var drop = Module.AddFunction("frameobj.drop " + key, new FuncType([ValType.I32], []));
        _frameObjects[key] = (t, alloc, drop);
        LibMethod("Cs2Wasm.Runtime.Rt", "InitFrameObject", 3);
        return (alloc, drop);
    }

    internal IEnumerable<(INamedTypeSymbol type, WasmFunction alloc, WasmFunction drop)> FrameObjects => _frameObjects.Values;

    readonly Dictionary<int, (int size, WasmFunction alloc, WasmFunction drop)> _frameDisplays = [];

    /// <summary>The same for a display (a closure's variables) that lives in its function's frame: (frame address) →
    /// the object; (object or 0) → (), its references released, nothing freed.</summary>
    internal (WasmFunction alloc, WasmFunction drop) FrameDisplayFns(int typeId, int size)
    {
        if (_frameDisplays.TryGetValue(typeId, out var x)) return (x.alloc, x.drop);
        var alloc = Module.AddFunction("framedisplay.new " + typeId, new FuncType([ValType.I32], [ValType.I32]));
        var drop = Module.AddFunction("framedisplay.drop " + typeId, new FuncType([ValType.I32], []));
        _frameDisplays[typeId] = (size, alloc, drop);
        LibMethod("Cs2Wasm.Runtime.Rt", "InitFrameObject", 3);
        return (alloc, drop);
    }

    internal IEnumerable<(int id, int size, WasmFunction alloc, WasmFunction drop)> FrameDisplays => _frameDisplays.Select(kv => (kv.Key, kv.Value.size, kv.Value.alloc, kv.Value.drop));
}

sealed partial class FunctionCompiler
{
    /// <summary>The object creations whose objects live in the frame, and the locals that own them.</summary>
    HashSet<IOperation>? _frameCreations;
    Dictionary<ISymbol, INamedTypeSymbol>? _frameLocals;

    const int MaxFrameObjectSize = 256;
    static readonly bool s_escapeDebug = Environment.GetEnvironmentVariable("CS2WASM_ESCAPE") == "1";

    void FindFrameObjects(IOperation body)
    {
        if (M.IsAsync || M.IsIterator || M.OriginalDefinition.IsIterator || _machine != null || D.CompilingInstrumented) return;
        var nested = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).SelectMany(o => o.DescendantsAndSelf()).ToHashSet();
        List<(ILocalSymbol local, IObjectCreationOperation oc)>? candidates = null;
        foreach (var op in body.Descendants())
        {
            if (op is not IVariableDeclaratorOperation { Symbol: { RefKind: RefKind.None } local, Initializer.Value: var init } || nested.Contains(op)) continue;
            while (init is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) init = c.Operand;
            if (init is not IObjectCreationOperation oc) continue;
            // (a using declaration or statement disposes it: not followed here)
            if (op.Parent?.Parent is IUsingDeclarationOperation || op.Parent?.Parent is IUsingOperation || op.Parent?.Parent?.Parent is IUsingOperation) continue;
            (candidates ??= []).Add((local, oc));
        }
        if (candidates == null) return;
        var refs = body.Descendants().OfType<ILocalReferenceOperation>().ToLookup(r => (ISymbol)r.Local, SymbolEqualityComparer.Default);
        foreach (var (local, oc) in candidates)
        {
            void Why(string reason) { if (s_escapeDebug) Console.Error.WriteLine($"escape: {M.ToDisplayString()} {local.Name} = new {oc.Type}: {reason}"); }
            if (_addrTaken.Contains(local) || IsCaptured(local)) { Why("captured or address taken"); continue; }
            if (T.Resolve(oc.Type!, Map) is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } ct || Types.IsString(ct) || ct.IsAnonymousType) { Why("not a class"); continue; }
            var t = (INamedTypeSymbol)T.Canonical(ct);
            if (Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader) > MaxFrameObjectSize || HasFinalizer(t)) { Why("size or finalizer"); continue; }
            if (oc.Constructor != null && D.ParamEscape(oc.Constructor, -1, t) != Driver.Esc.No) { Why("constructor lets this out"); continue; }
            if (oc.Initializer != null && !InitializerKeepsLocal(oc.Initializer, t)) { Why("initializer"); continue; }
            bool ok = true;
            foreach (var r in refs[local])
            {
                if (nested.Contains(r) || r.Parent is IAssignmentOperation a && a.Target == r || r.Parent is IDeconstructionAssignmentOperation
                    || r.Parent is IIncrementOrDecrementOperation || D.UseEscape(r, t, alias: false) != Driver.Esc.No) { ok = false; Why("use at line " + (r.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1) + ": " + r.Parent?.Syntax); break; }
            }
            if (!ok) continue;
            Why("in the frame");
            (_frameCreations ??= []).Add(oc);
            (_frameLocals ??= new(SymbolEqualityComparer.Default))[local] = t;
        }
    }

    /// <summary>`new C { F = ..., P = ... }`: member assignments whose setters keep `this` in.</summary>
    bool InitializerKeepsLocal(IObjectOrCollectionInitializerOperation init, INamedTypeSymbol t)
    {
        foreach (var i in init.Initializers)
        {
            if (i is not ISimpleAssignmentOperation { Target: var target }) return false;
            switch (target)
            {
                case IFieldReferenceOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver } }: break;
                case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }, Property.SetMethod: { } set }:
                    if (D.ParamEscape(set, -1, t) != Driver.Esc.No) return false;
                    break;
                default: return false;
            }
        }
        return true;
    }

    static bool HasFinalizer(INamedTypeSymbol t)
    {
        for (var b = t; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            if (b.GetMembers("Finalize").Any(m => m is IMethodSymbol { MethodKind: MethodKind.Destructor })) return true;
        return false;
    }

}

sealed partial class RuntimeGen
{
    /// <summary>The frame objects' functions, once cycles are known: a cycle-capable type keeps the heap.</summary>
    void EmitFrameObjects()
    {
        var rt = D.Rt;
        foreach (var (t, alloc, drop) in D.FrameObjects)
        {
            int id = D.TypeId(t);
            int size = Math.Max(D.Types.LayoutOf(t).Size, Types.ObjectHeader);
            bool heap = D.Arenas.Enabled && D.Arenas.CycleTypes.Contains(id);
            var a = new Emitter(alloc);
            if (heap) { a.I32(size); a.Tid(id); a.Call(rt.AllocObject); }
            else { a.Get(0); a.I32(size); a.Tid(id); a.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "InitFrameObject", 3).Fn); }
            var d = new Emitter(drop);
            if (heap) { d.Get(0); d.Call(rt.Release); continue; }
            d.Get(0); d.If();
            EmitDestroy(d, id, clear: true, free: false);
            d.End();
        }
        // (a display nothing but its own function's local functions knows: no delegate or lambda refers to it, so it
        // is on no cycle; should the graph say otherwise, it keeps the heap)
        foreach (var (id, size, alloc, drop) in D.FrameDisplays)
        {
            bool heap = D.Arenas.Enabled && D.Arenas.CycleTypes.Contains(id);
            var a = new Emitter(alloc);
            if (heap) { a.I32(size); a.Tid(id); a.Call(rt.AllocObject); }
            else { a.Get(0); a.I32(size); a.Tid(id); a.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "InitFrameObject", 3).Fn); }
            var d = new Emitter(drop);
            if (heap) { d.Get(0); d.Call(rt.Release); continue; }
            d.Get(0); d.If();
            EmitDestroy(d, id, clear: true, free: false);
            d.End();
        }
    }
}
