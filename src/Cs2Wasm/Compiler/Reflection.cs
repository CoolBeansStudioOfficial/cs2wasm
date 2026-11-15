using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Reflection metadata (section 8), kept for what the program's reflection calls can be proven to reach: the trimming
/// analysis. `typeof(X).GetMethod("M")` keeps X.M; `GetMethods()` on `typeof(X)` keeps all of X's methods; a type
/// marked [DynamicallyAccessedMembers] keeps everything; reflection on a Type the compiler cannot see is warning
/// CW0201. Kept members get a record in the data segment and a thunk (target, object[] args) -> object that unboxes
/// the arguments, calls the member and boxes the result; the library's MemberInfo classes read the records.
/// Programs that do not reflect pay nothing.
/// </summary>
sealed class ReflectionKeep
{
    public readonly Dictionary<string, (INamedTypeSymbol type, bool all, HashSet<string> names, bool ctors)> Types = [];
    /// <summary>Generic method instances MakeGenericMethod asks for (typeof arguments the compiler sees), by type.</summary>
    public readonly Dictionary<string, List<IMethodSymbol>> GenericInstances = [];
    public void KeepInstance(INamedTypeSymbol t, IMethodSymbol m)
    {
        Keep(t, m.Name);
        var k = Compiler.Types.Key(t);
        if (!GenericInstances.TryGetValue(k, out var l)) GenericInstances[k] = l = [];
        if (!l.Any(x => SymbolEqualityComparer.Default.Equals(x, m))) l.Add(m);
    }
    public void Keep(INamedTypeSymbol t, string? name, bool ctors = false)
    {
        var k = Compiler.Types.Key(t);
        if (!Types.TryGetValue(k, out var e)) e = (t, false, [], false);
        if (name == null && !ctors) e.all = true;
        else if (name != null) e.names.Add(name);
        if (ctors) e.ctors = true;
        Types[k] = e;
    }
}

internal sealed partial class Driver
{
    ReflectionKeep? _reflect;
    readonly Dictionary<string, WasmFunction> _reflThunks = [];
    WasmFunction? _metaOf;
    public WasmFunction MetaOf => _metaOf ??= Module.AddFunction("rt.meta_of", new FuncType([ValType.I32], [ValType.I32]));
    public static readonly FuncType ThunkType = new([ValType.I32, ValType.I32], [ValType.I32]);

    static readonly HashSet<string> s_reflectNames = ["GetMethod", "GetMethods", "GetProperty", "GetProperties", "GetField", "GetFields", "GetEvent", "GetEvents",
        "GetConstructor", "GetConstructors", "GetMember", "GetMembers", "GetCustomAttributes", "GetCustomAttribute", "IsDefined", "GetInterfaces"];

    static readonly string[] s_binderExpressionMembers = ["Add", "AddChecked", "And", "AndAlso", "ArrayIndex", "Assign", "Call", "Field", "Constant",
        "Convert", "ConvertChecked", "Divide", "Equal", "ExclusiveOr", "Property", "GreaterThan", "GreaterThanOrEqual", "Invoke", "Lambda", "LeftShift",
        "LessThan", "LessThanOrEqual", "Modulo", "Multiply", "MultiplyChecked", "Negate", "NegateChecked", "New", "NewArrayInit", "Not", "NotEqual", "Or",
        "OrElse", "Parameter", "UnaryPlus", "Quote", "RightShift", "Subtract", "SubtractChecked"];

    /// <summary>Finds the reflection the program does (FinishExports, before the last compile round).</summary>
    internal void AnalyzeReflection()
    {
        var keep = new ReflectionKeep();
        bool wantsDelegateMethod = false;
        var delegateRefs = new List<IMethodSymbol>();
        // a type's field and property initializers (static: its static initializer, compiled apart; instance: its
        // constructors')
        IEnumerable<SyntaxReference> Initializers(INamedTypeSymbol t, bool statics) =>
            t.GetMembers().Where(x => x.IsStatic == statics && x is IFieldSymbol or IPropertySymbol)
                .SelectMany(x => x.DeclaringSyntaxReferences)
                .Select(x => x.GetSyntax() switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax { Initializer: { } vi } => vi.SyntaxTree.GetReference(vi),
                    Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax { Initializer: { } pi } => pi.SyntaxTree.GetReference(pi),
                    _ => null,
                }).OfType<SyntaxReference>();
        var work = new List<(IEnumerable<SyntaxReference> refs, TypeMap map)>();
        foreach (var mi in AllMethods.ToList())
        {
            if (!mi.Compiled || mi.Impl != MethodImpl.Body) continue;
            var def = mi.Sym.OriginalDefinition;
            var refs = def.DeclaringSyntaxReferences.AsEnumerable();
            if (def.MethodKind == MethodKind.Constructor) refs = refs.Concat(Initializers(def.ContainingType, false));
            work.Add((refs, TypeMap.For(mi.Sym)));
        }
        foreach (var (ct, _, _, _) in _compiledCctors.ToList())
        {
            var d = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            for (var x = ct; x != null; x = x.ContainingType)
                for (int i = 0; i < x.TypeArguments.Length; i++) d[x.OriginalDefinition.TypeParameters[i]] = x.TypeArguments[i];
            work.Add((Initializers(ct.OriginalDefinition, true), new TypeMap(d)));
        }
        foreach (var (refs, map) in work)
        {
            foreach (var r in refs)
            {
                // (the library's reflection too: System.Linq.Expressions asks delegate types for Invoke)
                bool input = IsInput(r.SyntaxTree);
                if (!input && !r.GetSyntax().DescendantTokens().Any(t => t.ValueText is "GetMethod" or "GetProperty" or "GetField" or "GetMember" or "GetConstructor" or "GetConstructors" or "GetMethodInfo" or "Method" or "CreateInstance")) continue;
                var body = ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax());
                if (body == null) continue;
                foreach (var op in body.DescendantsAndSelf().OfType<IInvocationOperation>())
                {
                    Visit(op, map, keep, input);
                    // ((Delegate)M<T>).GetMethodInfo(): that method (System.Linq.Expressions' CallSiteOpsReflectionCache)
                    if (op is { TargetMethod: { Name: "GetMethodInfo", ContainingType.Name: "RuntimeReflectionExtensions" }, Arguments: [{ Value: var dv }] })
                        KeepDelegateTarget(dv);
                }
                // new Func<...>(M<T>).Method: that method (System.Linq.Queryable's operators)
                foreach (var pr in body.DescendantsAndSelf().OfType<IPropertyReferenceOperation>())
                    if (pr is { Property: { Name: "Method", ContainingType: { Name: "Delegate", ContainingNamespace.Name: "System" } }, Instance: { } di })
                        KeepDelegateTarget(di);
                void KeepDelegateTarget(IOperation dv)
                {
                    {
                        while (dv is IConversionOperation dc) dv = dc.Operand;
                        if (dv is IDelegateCreationOperation { Target: IMethodReferenceOperation { Method: var gm } } && gm.MethodKind != MethodKind.LocalFunction
                            && !gm.ContainingType.TypeArguments.Any(a => a is ITypeParameterSymbol))
                        {
                            if (!gm.IsGenericMethod) keep.Keep(gm.ContainingType, gm.Name);
                            else if (gm.TypeArguments.Select(a => Types.Resolve(a, map)).ToArray() is var targs && !targs.Any(a => a is ITypeParameterSymbol))
                                keep.KeepInstance(gm.ContainingType, gm.OriginalDefinition.Construct([.. targs.Select(Types.Canonical)]));
                        }
                    }
                }
                if (!input) continue;
                foreach (var op in body.DescendantsAndSelf())
                {
                    if (op is IPropertyReferenceOperation { Property: { Name: "Method", ContainingType: { Name: "Delegate", ContainingNamespace.Name: "System" } } }
                        or IInvocationOperation { TargetMethod: { Name: "GetMethodInfo", ContainingType.Name: "RuntimeReflectionExtensions" } })
                        wantsDelegateMethod = true;
                    if (op is IMethodReferenceOperation { Parent: IDelegateCreationOperation } mr && mr.Method.MethodKind != MethodKind.LocalFunction)
                    {
                        var rm = mr.Method;
                        if (rm.ContainingType.TypeArguments.Any(a => a is ITypeParameterSymbol) || rm.TypeArguments.Any(a => a is ITypeParameterSymbol)) continue;
                        delegateRefs.Add(rm);
                    }
                }
            }
        }
        // the C# runtime binder (dynamic) finds its predefined members through reflection (Microsoft.CSharp's
        // PredefinedMembers table: the Expression factories it builds trees with, and the operators of string,
        // decimal and Delegate it binds to)
        if (AllMethods.Any(m => m.Compiled && m.Sym.ContainingType?.ToDisplayString() == "Microsoft.CSharp.RuntimeBinder.SymbolTable"))
        {
            foreach (var n in s_binderExpressionMembers) keep.Keep(LibType("System.Linq.Expressions.Expression"), n);
            foreach (var n in new[] { "Concat", "op_Equality", "op_Inequality" }) keep.Keep(LibType("System.String"), n);
            foreach (var n in new[] { "Combine", "Remove", "op_Equality", "op_Inequality" }) keep.Keep(LibType("System.Delegate"), n);
            var dec = LibType("System.Decimal");
            foreach (var n in dec.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion).Select(m => m.Name).Distinct())
                keep.Keep(dec, n);
        }
        if (Dynamic.Any)
        {
            for (int id = 0; id < TypeById.Count; id++)
            {
                if (TypeById[id] is not INamedTypeSymbol nt || nt.IsUnboundGenericType || nt.TypeArguments.Any(a => a is ITypeParameterSymbol) || SyntheticById.ContainsKey(id) || DisplayById.ContainsKey(id)) continue;
                if (nt.ContainingNamespace?.ToDisplayString() is { } ns && (ns.StartsWith("Microsoft.CSharp") || ns.StartsWith("Cs2Wasm"))) continue;
                foreach (var name in Dynamic.MemberNames)
                    if (name != ".ctor" && nt.GetMembers(name).Any())
                    {
                        keep.Keep(nt, name);
                        // (the binder reads a generic instance's members from its definition too)
                        if (nt.IsGenericType && !SymbolEqualityComparer.Default.Equals(nt, nt.OriginalDefinition)) keep.Keep(nt.OriginalDefinition, name);
                    }
            }
            foreach (var ct in Dynamic.ConstructedTypes) keep.Keep(ct, null, ctors: true);
        }
        if (wantsDelegateMethod)
            foreach (var rm in delegateRefs)
                if (rm.IsGenericMethod) keep.KeepInstance(rm.ContainingType, rm); else keep.Keep(rm.ContainingType, rm.Name);
        // types marked [DynamicallyAccessedMembers] keep everything
        foreach (var comp in Inputs)
            foreach (var t in AllTypes(comp.Assembly.GlobalNamespace))
                if (t.GetAttributes().Any(a => a.AttributeClass?.Name == "DynamicallyAccessedMembersAttribute")) { keep.Keep(t, null); keep.Keep(t, null, ctors: true); }
        // reflection on types the compiler cannot see: when the program lists its own types (Assembly.GetTypes), they
        // are the program's, and each such use keeps its kind of member on all of them; otherwise a warning
        foreach (var (loc, what, kname, kctors) in _unknownReflection)
        {
            if (!_assemblyTypes) { Report(Diags.ReflectionUnknown, loc, what); continue; }
            foreach (var pt in ProgramTypes().OfType<INamedTypeSymbol>())
            {
                if (kctors) { if (pt.TypeKind is TypeKind.Class or TypeKind.Struct && !pt.IsAbstract && !pt.IsStatic && !pt.IsGenericType) keep.Keep(pt, null, ctors: true); }
                else if (kname == null || pt.GetMembers(kname).Any()) keep.Keep(pt, kname);
            }
        }
        foreach (var (t, name, ctors) in _keepFromTrees) keep.Keep(t, name, ctors);
        foreach (var (t, inst) in _keepInstancesFromTrees) keep.KeepInstance(t, inst);
        // EnumerableQuery runs a Queryable operator as the Enumerable one of its name, made with the same type
        // arguments (System.Linq.Queryable's EnumerableRewriter)
        if (Lib.TypeByMetadataName("System.Linq.Queryable") is { } qt && keep.GenericInstances.TryGetValue(Compiler.Types.Key(qt), out var qinsts) && Lib.TypeByMetadataName("System.Linq.Enumerable") is { } en)
            foreach (var qi in qinsts.ToList())
                foreach (var em in en.GetMembers(qi.Name).OfType<IMethodSymbol>())
                    if (em.IsGenericMethod && em.Arity == qi.Arity)
                        try { keep.KeepInstance(en, em.Construct([.. qi.TypeArguments])); } catch (ArgumentException) { }
        if (keep.Types.Count == 0) return;
        _reflect = keep;
        // thunks for the kept members (compiled in the next round)
        foreach (var (t, all, names, ctors) in keep.Types.Values.ToList())
            foreach (var m in KeptMembers(t, all, names, ctors))
                // (a generic method definition, or a member of a generic type definition, has no code: no thunk)
                if ((m is not IMethodSymbol { IsGenericMethod: true } gd || !IsMethodDefinition(gd)) && !OfOpenType(m)) ThunkFor(m);
    }

    bool _assemblyTypes;
    readonly List<(Location loc, string what, string? name, bool ctors)> _unknownReflection = [];

    void Visit(IInvocationOperation op, TypeMap map, ReflectionKeep keep, bool input = true)
    {
        var m = op.TargetMethod;
        var owner = m.ContainingType?.Name;
        if (owner == "Activator" && m.Name == "CreateInstance")
        {
            if (m.IsGenericMethod && Types.Resolve(m.TypeArguments[0], map) is INamedTypeSymbol gt) { keep.Keep(gt, null, ctors: true); return; }
            if (op.Arguments.Length > 0 && ReceiverType(op.Arguments[0].Value, map) is { } ct) { keep.Keep(ct, null, ctors: true); return; }
            // typeof(G<>).MakeGenericType(x): the constructors of the instances of G the program makes
            if (op.Arguments.Length > 0 && GenericDefinitionMade(op.Arguments[0].Value, map) is { } gd)
            {
                for (int id = 0; id < TypeById.Count; id++)
                    if (TypeById[id] is INamedTypeSymbol { IsGenericType: true } inst && SymbolEqualityComparer.Default.Equals(inst.OriginalDefinition, gd.OriginalDefinition)
                        && !inst.TypeArguments.Any(a => a is ITypeParameterSymbol) && Constructed.Contains(id))
                        keep.Keep(inst, null, ctors: true);
                return;
            }
            if (input) _unknownReflection.Add((op.Syntax.GetLocation(), "Activator.CreateInstance", null, true));
            return;
        }
        if (owner is "Assembly" or "RuntimeAssembly" or "Module" or "RuntimeModule" && m.Name is "GetTypes" or "GetExportedTypes" or "GetType" && input) _assemblyTypes = true;
        // Delegate.CreateDelegate(type, target, "Name", ...): the named method, on every type the program names
        if (owner == "Delegate" && m.Name == "CreateDelegate" && op.Arguments.FirstOrDefault(a => a.Parameter?.Type.SpecialType == SpecialType.System_String)?.Value.ConstantValue is { HasValue: true, Value: string dn })
        {
            for (int id = 0; id < TypeById.Count; id++)
                if (TypeById[id] is INamedTypeSymbol nt && !nt.IsUnboundGenericType && !nt.TypeArguments.Any(a => a is ITypeParameterSymbol) && !SyntheticById.ContainsKey(id) && !DisplayById.ContainsKey(id)
                    && nt.GetMembers(dn).Any())
                    keep.Keep(nt, dn);
            return;
        }
        if (owner is "MethodInfo" or "RuntimeMethodInfo" && m.Name == "MakeGenericMethod" && MethodOf(op.Instance, map) is var (mt, mname))
        {
            var targs = TypeArgsOf(op, map);
            foreach (var gm in mt.GetMembers(mname).OfType<IMethodSymbol>())
            {
                if (!gm.IsGenericMethod) continue;
                if (targs != null) { if (gm.TypeParameters.Length == targs.Count) keep.KeepInstance(mt, gm.OriginalDefinition.Construct([.. targs])); continue; }
                // arguments the compiler cannot see: the instances the program makes (no code exists for others)
                foreach (var inst in AllMethods.Select(x => x.Sym).Where(x => x.IsGenericMethod && SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, CanonicalMethod(gm).OriginalDefinition)
                             && !x.TypeArguments.Any(a => a is ITypeParameterSymbol)).ToList())
                    keep.KeepInstance(mt, inst);
            }
            return;
        }
        if (owner is not ("Type" or "TypeInfo" or "MemberInfo" or "CustomAttributeExtensions" or "Attribute") || !s_reflectNames.Contains(m.Name)) return;
        if (owner is "MemberInfo" or "CustomAttributeExtensions" or "Attribute") return; // attributes of kept members are kept with them
        var t = op.Instance != null ? ReceiverType(op.Instance, map) : null;
        string? name = null;
        if (m.Name is "GetMethod" or "GetProperty" or "GetField" or "GetMember" or "GetEvent" && op.Arguments.Length > 0 && op.Arguments[0].Value.ConstantValue is { HasValue: true, Value: string s }) name = s;
        if (t == null && name != null)
        {
            // a member named by a constant on a type the compiler cannot see: that member of every type the program
            // names (delegate types' Invoke)
            for (int id = 0; id < TypeById.Count; id++)
                if (TypeById[id] is INamedTypeSymbol nt && !nt.IsUnboundGenericType && !nt.TypeArguments.Any(a => a is ITypeParameterSymbol) && !SyntheticById.ContainsKey(id) && !DisplayById.ContainsKey(id)
                    && nt.GetMembers(name).Any())
                    keep.Keep(nt, name);
            if (!input) return;
        }
        if (t == null)
        {
            if (input) _unknownReflection.Add((op.Syntax.GetLocation(), "Type." + m.Name, name, m.Name is "GetConstructor" or "GetConstructors"));
            return;
        }
        if (m.Name is "GetConstructor" or "GetConstructors") keep.Keep(t, null, ctors: true);
        else if (m.Name is "GetCustomAttributes" or "GetCustomAttribute" or "IsDefined" or "GetInterfaces") keep.Keep(t, "<type>");
        else keep.Keep(t, name);
    }

    /// <summary>`typeof(X).GetMethod("M")` (through a local assigned once): X and M.</summary>
    (INamedTypeSymbol, string)? MethodOf(IOperation? op, TypeMap map)
    {
        while (op is IConversionOperation c) op = c.Operand;
        if (op is ILocalReferenceOperation lr && lr.Local.DeclaringSyntaxReferences.FirstOrDefault() is { } decl
            && ModelFor(decl.SyntaxTree).GetOperation(decl.GetSyntax()) is IVariableDeclaratorOperation { Initializer.Value: { } init })
            return MethodOf(init, map);
        if (op is IInvocationOperation { TargetMethod.Name: "GetMethod", Instance: { } ti, Arguments: [var a0, ..] }
            && a0.Value.ConstantValue is { HasValue: true, Value: string name } && ReceiverType(ti, map) is { } t)
            return (t, name);
        return null;
    }

    /// <summary>The type arguments of MakeGenericMethod(typeof(A), ...), when all are typeof constants.</summary>
    List<ITypeSymbol>? TypeArgsOf(IInvocationOperation op, TypeMap map)
    {
        if (op.Arguments is not [{ Value: var v }]) return null;
        while (v is IConversionOperation c) v = c.Operand;
        if (v is not IArrayCreationOperation { Initializer: { } init }) return null;
        var r = new List<ITypeSymbol>();
        foreach (var e in init.ElementValues)
        {
            var x = e;
            while (x is IConversionOperation c2) x = c2.Operand;
            if (x is not ITypeOfOperation to) return null;
            var at = Types.Resolve(to.TypeOperand, map);
            if (at is ITypeParameterSymbol) return null;
            r.Add(Types.Canonical(at));
        }
        return r;
    }

    /// <summary>The type a Type-valued operation names, when the compiler can see it.</summary>
    /// <summary>`typeof(G<>).MakeGenericType(...)` (through a local assigned once): G's definition.</summary>
    INamedTypeSymbol? GenericDefinitionMade(IOperation op, TypeMap map)
    {
        while (op is IConversionOperation c) op = c.Operand;
        if (op is ILocalReferenceOperation lr && lr.Local.DeclaringSyntaxReferences.FirstOrDefault() is { } decl
            && ModelFor(decl.SyntaxTree).GetOperation(decl.GetSyntax()) is IVariableDeclaratorOperation { Initializer.Value: { } init })
            return GenericDefinitionMade(init, map);
        if (op is IInvocationOperation { TargetMethod.Name: "MakeGenericType", Instance: ITypeOfOperation to } && to.TypeOperand is INamedTypeSymbol { IsGenericType: true } g)
            return g.OriginalDefinition;
        return null;
    }

    INamedTypeSymbol? ReceiverType(IOperation op, TypeMap map)
    {
        while (op is IConversionOperation { IsImplicit: true } c) op = c.Operand;
        switch (op)
        {
            case ITypeOfOperation to: return Types.Resolve(to.TypeOperand, map) as INamedTypeSymbol;
            case IInvocationOperation { TargetMethod.Name: "GetType", Instance: { } inst, Arguments.Length: 0 }:
                var it = Types.Resolve(inst.Type!, map);
                return it is INamedTypeSymbol n && (n.IsSealed || n.IsValueType) ? n : null;
            case ILocalReferenceOperation lr:
                {
                    // a local assigned once from something visible
                    var decl = lr.Local.DeclaringSyntaxReferences.FirstOrDefault();
                    if (decl == null) return null;
                    var model = ModelFor(decl.SyntaxTree);
                    if (model.GetOperation(decl.GetSyntax()) is IVariableDeclaratorOperation { Initializer.Value: { } init } && !lr.Local.IsRef)
                        return ReceiverType(init, map);
                    return null;
                }
        }
        return null;
    }

    /// <summary>Members of `t` (and its bases, for inherited lookups) a kept entry covers.</summary>
    IEnumerable<ISymbol> KeptMembers(INamedTypeSymbol t, bool all, HashSet<string> names, bool ctors) =>
        KeptMembersRaw(t, all, names, ctors).Distinct(SymbolEqualityComparer.Default);

    IEnumerable<ISymbol> KeptMembersRaw(INamedTypeSymbol t, bool all, HashSet<string> names, bool ctors)
    {
        for (var x = t; x != null; x = x.BaseType)
        {
            // upstream: object's methods are inherited members too (GetType first, as the runtime lists them)
            bool isObject = x.SpecialType == SpecialType.System_Object || Compiler.Types.IsObject(x);
            var members = isObject ? x.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).OrderBy(m => m.Name == "GetType" ? 0 : 1).Cast<ISymbol>() : x.GetMembers();
            foreach (var mem in members)
            {
                bool want = all || names.Contains(mem.Name) || names.Contains(mem.MetadataName) || ctors && mem is IMethodSymbol { MethodKind: MethodKind.Constructor } && SymbolEqualityComparer.Default.Equals(x, t);
                if (!want) continue;
                switch (mem)
                {
                    case IEventSymbol ev:
                        if (ev.AddMethod is { } ea) yield return ea;
                        if (ev.RemoveMethod is { } er) yield return er;
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary, IsGenericMethod: true, IsAbstract: false } gm when gm.Parameters.All(p => !p.Type.IsRefLikeType && p.Type is not IPointerTypeSymbol) && !gm.ReturnType.IsRefLikeType && !gm.ReturnsByRef && !gm.ReturnsByRefReadonly && !gm.IsExtern:
                        // a generic method definition (invoked only through its instances)
                        yield return gm;
                        if (_reflect != null && _reflect.GenericInstances.TryGetValue(Compiler.Types.Key(t), out var insts))
                            foreach (var inst in insts) if (SymbolEqualityComparer.Default.Equals(inst.OriginalDefinition, gm.OriginalDefinition)) yield return inst;
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.DelegateInvoke or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.UserDefinedOperator or MethodKind.Conversion } ms when !ms.IsGenericMethod && (!ms.IsAbstract || !ms.IsStatic && SymbolEqualityComparer.Default.Equals(x, t)) && ms.Parameters.All(p => !p.Type.IsRefLikeType && p.Type is not IPointerTypeSymbol) && !ms.ReturnType.IsRefLikeType && !ms.ReturnsByRef && !ms.ReturnsByRefReadonly && !ms.IsExtern:
                        yield return ms; break;
                    case IMethodSymbol { MethodKind: MethodKind.Constructor } cs when SymbolEqualityComparer.Default.Equals(x, t) && cs.Parameters.All(p => !p.Type.IsRefLikeType && p.Type is not IPointerTypeSymbol) && !t.IsAbstract && !t.IsRefLikeType:
                        yield return cs; break;
                    case IPropertySymbol ps:
                        if (AutoPropertyField(ps.OriginalDefinition) is { } bf) yield return bf;
                        // (an abstract accessor: on the reflected type itself, called virtually)
                        bool callable(IMethodSymbol a) => !a.IsAbstract || !a.IsStatic && SymbolEqualityComparer.Default.Equals(x, t);
                        if (ps.GetMethod is { } g && callable(g)) yield return g;
                        if (ps.SetMethod is { } s2 && callable(s2)) yield return s2;
                        break;
                    case IFieldSymbol fs when !fs.IsConst && !fs.IsImplicitlyDeclared:
                        yield return fs; break;
                }
            }
        }
    }

    string ThunkKey(ISymbol m) => m switch { IMethodSymbol ms => Types.MethodKey(CanonicalMethod(ms)), IFieldSymbol f => "F:" + Types.Key(Types.Canonical(f.ContainingType)) + "::" + f.Name, _ => m.ToDisplayString() };

    internal WasmFunction ThunkFor(ISymbol member)
    {
        var key = ThunkKey(member);
        if (_reflThunks.TryGetValue(key, out var f)) return f;
        f = Module.AddFunction("reflect " + member.ToDisplayString(), ThunkType);
        f.TableRoot = true; // the reflection tables refer to it
        _reflThunks[key] = f;
        var mi = new MethodInst { Sym = LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Sym, Fn = f, Key = "thunk " + key, Impl = MethodImpl.Body };
        mi.Compile = () => FunctionCompiler.CompileReflectionThunk(this, f, member, mi);
        EnqueueExtra(mi);
        return f;
    }

    readonly Dictionary<string, WasmFunction> _attrFactories = [];
    static string AttrKey(AttributeData a) => a.AttributeClass!.ToDisplayString() + "(" + string.Join(",", a.ConstructorArguments.Select(c => c.ToCSharpString())) + ")" + string.Join(",", a.NamedArguments.Select(n => n.Key + "=" + n.Value.ToCSharpString()));

    WasmFunction AttributeFactory(AttributeData a)
    {
        var key = AttrKey(a);
        if (_attrFactories.TryGetValue(key, out var f)) return f;
        // GetCustomAttributes returns an array of the attribute's type (upstream: Array.CreateInstance)
        {
            for (var at = (INamedTypeSymbol?)a.AttributeClass; at != null && !Compiler.Types.IsObject(at); at = at.BaseType) MarkConstructed(Types.LibCompilationArray(at));
            // and AttributeUsage, which GetCustomAttributes reads of the attribute's type
            MarkConstructed(Types.LibCompilationArray(LibType("System.AttributeUsageAttribute")));
        }
        f = Module.AddFunction("attribute " + key, ThunkType);
        f.TableRoot = true; // the reflection tables refer to it
        _attrFactories[key] = f;
        var mi = new MethodInst { Sym = LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Sym, Fn = f, Key = "attr " + key, Impl = MethodImpl.Body };
        mi.Compile = () => FunctionCompiler.CompileAttributeFactory(this, f, a, mi);
        EnqueueExtra(mi);
        return f;
    }

    /// <summary>Attribute factories must exist before the last compile round: requested with the members.</summary>
    internal void RequestAttributeFactories()
    {
        if (_reflect == null) return;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
        {
            foreach (var a in KeptAttributes(t)) AttributeFactory(a);
            foreach (var m in KeptMembers(t, all, names, ctors))
                foreach (var a in KeptAttributes(m is IMethodSymbol { AssociatedSymbol: IPropertySymbol p } ? p : m)) AttributeFactory(a);
        }
    }

    IEnumerable<AttributeData> KeptAttributes(ISymbol s) => s.GetAttributes().Where(a => a.AttributeClass != null && a.AttributeConstructor != null
        && a.AttributeClass.ContainingNamespace?.ToDisplayString() is not ("System.Runtime.CompilerServices" or "System.Diagnostics.CodeAnalysis" or "System.Runtime.InteropServices"))
        .Concat(SynthesizedAttributes(s));

    /// <summary>[DefaultMember(name)] on a type that declares an indexer, as the C# compiler emits it (from the
    /// generated carrier class: DynamicLowering.cs).</summary>
    IEnumerable<AttributeData> SynthesizedAttributes(ISymbol s)
    {
        if (s is not INamedTypeSymbol nt || nt.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => p.IsIndexer) is not { } ix) yield break;
        if (Main.GetTypeByMetadataName("Cs2Wasm.Generated.DefaultMember_" + ix.MetadataName)?.GetAttributes().FirstOrDefault() is { } a) yield return a;
    }

    // ---------------- the tables (Finish, before memory layout) ----------------
    internal void EmitReflectionTables()
    {
        PatchDisplayFields();
        if (_metaOf == null) return;
        var e = new Emitter(_metaOf);
        if (_reflect == null) { e.I32(0); return; }
        var records = new Dictionary<int, IAddress>();
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
        {
            int id = TypeId(t);
            records[id] = TypeRecord(t, KeptMembers(t, all, names, ctors).ToList());
        }
        int tid = e.AddLocal(ValType.I32);
        e.Get(0); e.Set(tid);
        foreach (var (id, addr) in records)
        {
            e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(addr); e.Op(Op.Return); e.End();
        }
        e.I32(0);
    }

    int ExtTypeId(ITypeSymbol t) => ExtId(TypeId(t));

    // a member's metadata token: one per definition (List<int>.Add and List<T>.Add share it, as in metadata)
    readonly Dictionary<string, int> _tokens = [];
    int Token(int table, ISymbol m)
    {
        var key = table + Types.DocId(m.OriginalDefinition is IMethodSymbol { ReducedFrom: { } rf } ? rf : m.OriginalDefinition);
        if (!_tokens.TryGetValue(key, out var t))
        {
            _tokenRows[table] = _tokenRows.GetValueOrDefault(table) + 1;
            _tokens[key] = t = table | _tokenRows[table];
        }
        return t;
    }
    readonly Dictionary<int, int> _tokenRows = [];

    /// <summary>A method's parameters' default values (0 when none has one): per parameter [kind, low, high],
    /// kind 0 none, 1 null, else the constant's SpecialType kind (Meta.DefaultValue), with 0x100 for an enum.</summary>
    int Defaults(IMethodSymbol m)
    {
        if (!m.Parameters.Any(p => p.HasExplicitDefaultValue)) return 0;
        var l = new List<int>();
        foreach (var p in m.Parameters)
        {
            if (!p.HasExplicitDefaultValue) { l.AddRange([0, 0, 0]); continue; }
            var v = p.ExplicitDefaultValue;
            int en = p.Type.TypeKind == TypeKind.Enum ? 0x100 : 0;
            switch (v)
            {
                case null: l.AddRange([1, 0, 0]); break;
                case bool b: l.AddRange([2 | en, b ? 1 : 0, 0]); break;
                case char c: l.AddRange([3 | en, c, 0]); break;
                case sbyte x: l.AddRange([4 | en, x, 0]); break;
                case byte x: l.AddRange([5 | en, x, 0]); break;
                case short x: l.AddRange([6 | en, x, 0]); break;
                case ushort x: l.AddRange([7 | en, x, 0]); break;
                case int x: l.AddRange([8 | en, x, 0]); break;
                case uint x: l.AddRange([9 | en, (int)x, 0]); break;
                case long x: l.AddRange([10 | en, (int)x, (int)(x >> 32)]); break;
                case ulong x: l.AddRange([11 | en, (int)x, (int)(x >> 32)]); break;
                case float x: l.AddRange([12, BitConverter.SingleToInt32Bits(x), 0]); break;
                case double x: { long bits = BitConverter.DoubleToInt64Bits(x); l.AddRange([13, (int)bits, (int)(bits >> 32)]); break; }
                case string x: l.AddRange([14, Str(x), 0]); break;
                default: l.AddRange([0, 0, 0]); break; // decimal: from its attribute (not kept)
            }
        }
        return Ints(l).Address;
    }

    // ---------------- assemblies: a table of full names (the program's first), by index ----------------
    readonly List<IAssemblySymbol> _assemblies = [];
    WasmFunction? _assemblyNames;
    public WasmFunction AssemblyNamesFn => _assemblyNames ??= Module.AddFunction("rt.assembly_names", new FuncType([], [ValType.I32]));

    public int AssemblyIndex(IAssemblySymbol a)
    {
        if (_assemblies.Count == 0) _assemblies.Add(Inputs[0].Assembly);
        for (int i = 0; i < _assemblies.Count; i++) if (SymbolEqualityComparer.Default.Equals(_assemblies[i], a)) return i;
        _assemblies.Add(a);
        return _assemblies.Count - 1;
    }

    /// <summary>An assembly's display name: a library's is the running .NET's (its version and key token), the
    /// program's from its identity (as the C# compiler emits it).</summary>
    string AssemblyFullName(IAssemblySymbol a)
    {
        if (!Inputs.Any(c => SymbolEqualityComparer.Default.Equals(c.Assembly, a)))
            try { if (System.Reflection.Assembly.Load(a.Identity.Name).FullName is { } fn) return fn; } catch { }
        var id = a.Identity;
        var token = id.PublicKeyToken.IsDefaultOrEmpty ? "null" : string.Concat(id.PublicKeyToken.Select(b => b.ToString("x2")));
        return $"{id.Name}, Version={id.Version}, Culture={(string.IsNullOrEmpty(id.CultureName) ? "neutral" : id.CultureName)}, PublicKeyToken={token}";
    }

    internal void EmitAssemblyNames()
    {
        if (_assemblyNames == null) return;
        if (_assemblies.Count == 0) AssemblyIndex(Inputs[0].Assembly);
        var e = new Emitter(_assemblyNames);
        e.I32Addr(Ints([_assemblies.Count, .. _assemblies.Select(a => Str(AssemblyFullName(a)))]));
    }

    /// <summary>A parameter in a record: its type's id, with the by-ref bit for ref/out/in (Meta.ByRef) and the
    /// out (Meta.ParamOut) or in (Meta.ParamIn) bit.</summary>
    int ParamWord(IParameterSymbol p) => ExtTypeId(p.Type) | p.RefKind switch
    {
        RefKind.None => 0,
        RefKind.Out => 0x40000000 | 0x20000000,
        RefKind.In or RefKind.RefReadOnlyParameter => 0x40000000 | 0x10000000,
        _ => 0x40000000,
    };

    /// <summary>The types the reflection records name (numbered for the module: RuntimeGen.AssignTypeIds).</summary>
    internal IEnumerable<ITypeSymbol> ReflectedTypes()
    {
        if (_reflect == null) yield break;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
        {
            yield return t;
            if (t.BaseType != null) yield return t.BaseType;
            foreach (var i in t.AllInterfaces) yield return i;
            foreach (var a in KeptAttributes(t)) yield return a.AttributeClass!;
            foreach (var m in KeptMembers(t, all, names, ctors))
            {
                yield return m.ContainingType;
                foreach (var a in KeptAttributes(m is IMethodSymbol { AssociatedSymbol: IPropertySymbol pp } ? pp : m)) yield return a.AttributeClass!;
                switch (m)
                {
                    case IFieldSymbol f: yield return f.Type; break;
                    case IMethodSymbol ms:
                        if (!ms.ReturnsVoid) yield return ms.ReturnType;
                        foreach (var p in ms.Parameters) yield return p.Type;
                        // a generic method's parameters (a definition) or arguments (an instance)
                        foreach (var a in ms.TypeArguments) yield return a;
                        if (ms.AssociatedSymbol is IPropertySymbol prop) yield return prop.Type;
                        break;
                }
            }
        }
    }

    static bool IsMethodDefinition(IMethodSymbol m) => m.TypeArguments.Zip(m.TypeParameters).All(x => x.First is ITypeParameterSymbol tp && tp.Name == x.Second.Name && tp.TypeParameterKind == TypeParameterKind.Method);

    static bool OfOpenType(ISymbol m)
    {
        for (var t = m.ContainingType; t != null; t = t.ContainingType)
            if (t.TypeArguments.Any(a => a is ITypeParameterSymbol)) return true;
        return false;
    }

    int Thunk(ISymbol m) => _reflThunks.TryGetValue(ThunkKey(m), out var f) && f.Body.Count > 0 ? RootSlot(f) + 1 : 0;
    int RootSlot(WasmFunction f) { f.TableRoot = true; return Module.TableSlot(f); }
    int TypeIdOrVoid(ITypeSymbol t) => ExtTypeId(t);
    int Str(string s) => StringLiteral(s).Address;

    IAddress Ints(IEnumerable<int> values) { var b = values.SelectMany(BitConverter.GetBytes).ToArray(); return DataBlob(b.Length == 0 ? new byte[4] : b, 4); }

    (int count, int addr) Attrs(ISymbol s)
    {
        var list = KeptAttributes(s).Where(a => _attrFactories.ContainsKey(AttrKey(a))).ToList();
        var ints = new List<int>();
        foreach (var a in list)
        {
            ints.Add(ExtTypeId(a.AttributeClass!));
            ints.Add(RootSlot(AttributeFactory(a)) + 1);
        }
        return (list.Count, Ints(ints).Address);
    }

    const int FStatic = 1, FPublic = 2, FVirtual = 4, FAbstract = 8, FReadOnly = 16, FPrivate = 32, FFamily = 64, FAssembly = 128;
    /// <summary>A method that is an event's add or remove accessor (GetEvents pairs them).</summary>
    const int FEventAccessor = 1 << 12;
    /// <summary>MethodAttributes the CLR gives: SpecialName (accessors, operators), Final, NewSlot.</summary>
    const int FSpecialName = 1 << 13, FFinal = 1 << 14, FNewSlot = 1 << 15, FInitOnly = 1 << 16;

    static int MethodShape(IMethodSymbol ms)
    {
        int f = (ms.IsInitOnly ? FInitOnly : 0) | (ms.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove
            or MethodKind.UserDefinedOperator or MethodKind.Conversion ? FSpecialName : 0);
        if (ms.IsStatic) return f;
        bool virt = ms.IsVirtual || ms.IsAbstract || ms.IsOverride;
        if (ms.ContainingType.TypeKind == TypeKind.Interface) return f | (virt ? FNewSlot : 0);
        if (virt)
        {
            if (!ms.IsOverride) f |= FNewSlot;
            if (ms.IsSealed) f |= FFinal;
            return f;
        }
        // an implicit implementation of an interface method: virtual, final, a new slot
        foreach (var i in ms.ContainingType.AllInterfaces)
            foreach (var im in i.GetMembers(ms.Name).OfType<IMethodSymbol>())
                if (SymbolEqualityComparer.Default.Equals(ms.ContainingType.FindImplementationForInterfaceMember(im), ms)) return f | FFinal | FNewSlot | FVirtual;
        return f;
    }
    static int Access(ISymbol s) => s.DeclaredAccessibility switch
    {
        Accessibility.Public => FPublic,
        Accessibility.Private => FPrivate,
        Accessibility.Protected => FFamily,
        Accessibility.Internal => FAssembly,
        _ => FFamily | FAssembly,
    };

    IAddress TypeRecord(INamedTypeSymbol t, List<ISymbol> members)
    {
        var fields = new List<int>(); int nf = 0;
        var props = new List<int>(); int np = 0;
        var methods = new List<int>(); int nm = 0;
        var methodSyms = new List<IMethodSymbol>();
        var ctors = new List<int>(); int nc = 0;
        var seenProps = new HashSet<string>();
        foreach (var m in members)
        {
            switch (m)
            {
                case IFieldSymbol f:
                    {
                        var (na, aa) = Attrs(f);
                        fields.AddRange([Str(f.Name), ExtTypeId(f.Type), (f.IsStatic ? FStatic : 0) | Access(f) | (f.IsReadOnly ? FReadOnly : 0), Thunk(f), ExtTypeId(f.ContainingType), na, aa, Token(0x04000000, f)]);
                        nf++;
                        break;
                    }
                case IMethodSymbol { MethodKind: MethodKind.Constructor } c:
                    {
                        var (na, aa) = Attrs(c);
                        ctors.AddRange([(c.IsStatic ? FStatic : 0) | Access(c), Thunk(c), c.Parameters.Length, Ints(c.Parameters.Select(ParamWord)).Address, Ints(c.Parameters.Select(p => Str(p.Name))).Address, na, aa]);
                        ctors.Add(ExtTypeId(c.ContainingType));
                        ctors.Add(Defaults(c));
                        ctors.Add(Token(0x06000000, c));
                        nc++;
                        break;
                    }
                case IMethodSymbol ms:
                    {
                        var (na, aa) = Attrs(ms);
                        int flags = (ms.IsStatic ? FStatic : 0) | Access(ms) | (ms.IsVirtual || ms.IsOverride || ms.IsAbstract ? FVirtual : 0) | (ms.IsAbstract ? FAbstract : 0)
                            | (ms.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove ? FEventAccessor : 0) | MethodShape(ms);
                        methods.AddRange([Str(ms.Name), ms.ReturnsVoid ? -1 : ExtTypeId(ms.ReturnType), flags, Thunk(ms), ms.Parameters.Length,
                            Ints(ms.Parameters.Select(ParamWord)).Address, Ints(ms.Parameters.Select(p => Str(p.Name))).Address, na, aa, ExtTypeId(ms.ContainingType), 0, Defaults(ms), Token(0x06000000, ms)]);
                        methodSyms.Add(ms);
                        nm++;
                        if (ms.AssociatedSymbol is IPropertySymbol p && seenProps.Add(Types.Key(p.ContainingType) + "." + p.Name))
                        {
                            var (pa, pb) = Attrs(p);
                            // (an accessor without code, of a generic definition: -1, present)
                            int g = p.GetMethod != null ? Thunk(p.GetMethod) is var gt && gt != 0 ? gt : -1 : 0, s = p.SetMethod != null ? Thunk(p.SetMethod) is var st && st != 0 ? st : -1 : 0;
                            int pflags = (p.IsStatic ? FStatic : 0) | Access(p.GetMethod ?? p.SetMethod!) | (p.GetMethod?.DeclaredAccessibility == Accessibility.Public ? 256 : 0) | (p.SetMethod?.DeclaredAccessibility == Accessibility.Public ? 512 : 0);
                            props.AddRange([Str(p.MetadataName), ExtTypeId(p.Type), pflags, g, s, ExtTypeId(p.ContainingType), pa, pb, Token(0x17000000, p)]);
                            np++;
                        }
                        break;
                    }
            }
        }
        // generic methods: a definition lists its parameters and its instances (their entries' addresses), an
        // instance its arguments and its definition's entry; written into the entries once the array has its place
        var methodsBlob = (DataAddr)Ints(methods);
        int mBase = methodsBlob.Address;
        int EntryAt(int j) => mBase + 4 * 13 * j;
        for (int i = 0; i < methodSyms.Count; i++) NoteMethodEntry(t, methodSyms[i], EntryAt(i));
        for (int i = 0; i < methodSyms.Count; i++)
        {
            var ms = methodSyms[i];
            if (!ms.IsGenericMethod) continue;
            List<int> g;
            if (IsMethodDefinition(ms))
            {
                g = [1, ms.TypeParameters.Length, .. ms.TypeParameters.Select(p => ExtTypeId(p))];
                var inst = Enumerable.Range(0, methodSyms.Count).Where(j => methodSyms[j].IsGenericMethod && !IsMethodDefinition(methodSyms[j]) && SymbolEqualityComparer.Default.Equals(methodSyms[j].OriginalDefinition, ms.OriginalDefinition)).ToList();
                g.Add(inst.Count);
                foreach (var j in inst) g.Add(EntryAt(j));
            }
            else
            {
                int def = Enumerable.Range(0, methodSyms.Count).FirstOrDefault(j => IsMethodDefinition(methodSyms[j]) && SymbolEqualityComparer.Default.Equals(methodSyms[j].OriginalDefinition, ms.OriginalDefinition), -1);
                g = [2, ms.TypeArguments.Length, .. ms.TypeArguments.Select(a => ExtTypeId(a)), def < 0 ? 0 : EntryAt(def)];
            }
            PatchData(methodsBlob, (i * 13 + 10) * 4, Ints(g).Address);
        }
        var (ta, tb) = Attrs(t);
        var rec = new[] { nf, Ints(fields).Address, np, Ints(props).Address, nm, mBase, nc, Ints(ctors).Address, ta, tb,
            t.BaseType != null ? ExtTypeId(t.BaseType) + 1 : 0, t.Interfaces.Length, Ints(t.AllInterfaces.Select(i => ExtTypeId(i))).Address };
        return Ints(rec);
    }
}

sealed partial class FunctionCompiler
{
    public static void CompileReflectionThunk(Driver d, WasmFunction fn, ISymbol member, MethodInst ctx)
    {
        var fc = new FunctionCompiler(d, fn, ctx.Sym, ctx);
        fc.EmitThunk(member);
    }

    public static void CompileDisplayFieldThunk(Driver d, WasmFunction fn, int offset, ITypeSymbol type, MethodInst ctx)
    {
        var fc = new FunctionCompiler(d, fn, ctx.Sym, ctx);
        fc.EmitDisplayFieldThunk(offset, type);
    }

    /// <summary>A closure field's reflection thunk: args null reads it (boxed); else writes args[0].</summary>
    void EmitDisplayFieldThunk(int off, ITypeSymbol type)
    {
        using (BodyScope())
        {
            _retLocal = E.AddLocal(ValType.I32);
            BeginTemps();
            var ft = T.Canonical(type);
            E.Get(1); E.Op(Op.I32Eqz);
            E.If(ValType.I32);
            {
                E.Get(0);
                Own o;
                if (Types.IsStruct(ft)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } o = Own.None; }
                else { E.Load(Types.LoadOp(ft), off); o = Types.IsRef(ft) ? Own.Unstable : Own.None; }
                BoxResult(ft, o);
            }
            E.Else();
            {
                E.Get(1); E.Load(Op.I32Load, Types.ArrayData);
                var vo = Own.Unstable;
                ConvertValue(D.ObjectType, ft, false, ref vo, explicitConv: true, null);
                StoreToAddressFromStack(() => E.Get(0), off, ft, vo, pub: Publish.Container, pubContainer: () => E.Get(0));
                E.I32(0);
            }
            E.End();
            E.Set(_retLocal);
            EndTemps();
            EmitReturnSequence(true);
        }
        Finish();
    }

    public static void CompileAttributeFactory(Driver d, WasmFunction fn, AttributeData a, MethodInst ctx)
    {
        var fc = new FunctionCompiler(d, fn, ctx.Sym, ctx);
        fc.EmitAttributeFactory(a);
    }

    void PushArgAs(int i, ITypeSymbol pt)
    {
        pt = T.Canonical(pt);
        E.Get(1); E.Load(Op.I32Load, Types.ArrayData + 4 * i);
        var o = Own.Unstable;
        ConvertValue(D.ObjectType, pt, false, ref o, explicitConv: true, null);
        if (Types.IsStruct(pt))
        {
            int src = E.AddLocal(ValType.I32); E.Set(src);
            int off = TempStruct(pt);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Get(src); CopyStructInit(pt);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
        }
    }

    // a ref/out/in argument: a value type's variable is the data of its box in the array (the invoker's copy, which
    // the callee writes); a reference's is a frame slot, copied back into the array after the call
    void PushParam(IParameterSymbol p, int i, List<(int i, int off)> back)
    {
        if (p.RefKind == RefKind.None) { PushArgAs(i, p.Type); return; }
        var pt = T.Canonical(T.Resolve(p.Type, Map));
        if (pt.IsValueType)
        {
            D.MarkConstructed(pt); // the invoker boxes a default for a null argument (RuntimeHelpers.GetUninitializedObject)
            E.Get(1); E.Load(Op.I32Load, Types.ArrayData + 4 * i);
            E.I32(FunctionCompilerHelpers.BoxDataOffset(T, pt)); E.Op(Op.I32Add);
            return;
        }
        int off = TempStruct(pt);
        E.Get(_fb);
        E.Get(1); E.Load(Op.I32Load, Types.ArrayData + 4 * i);
        var o = Own.Unstable;
        ConvertValue(D.ObjectType, pt, false, ref o, explicitConv: true, null);
        E.Call(Rt.Retain);
        E.Store(Op.I32Store, off);
        E.Get(_fb); if (off != 0) { E.I32(off); E.Op(Op.I32Add); }
        back.Add((i, off));
    }

    void CopyBack(List<(int i, int off)> back)
    {
        foreach (var (i, off) in back)
        {
            int old = E.AddLocal(ValType.I32);
            E.Get(1); E.Load(Op.I32Load, Types.ArrayData + 4 * i); E.Set(old);
            E.Get(1); E.Get(_fb); E.Load(Op.I32Load, off); E.Call(Rt.Retain); E.Store(Op.I32Store, Types.ArrayData + 4 * i);
            E.Get(old); E.Call(Rt.Release);
        }
    }

    void PushTargetAs(ITypeSymbol owner)
    {
        owner = T.Canonical(owner);
        // a value type's method runs on the box's data, in place (as .NET's Invoke on a boxed value)
        if (owner.IsValueType && owner.TypeKind != TypeKind.Interface)
        {
            E.Get(0); E.I32(FunctionCompilerHelpers.BoxDataOffset(T, owner)); E.Op(Op.I32Add);
            return;
        }
        E.Get(0);
        var o = Own.Unstable;
        ConvertValue(D.ObjectType, owner, false, ref o, explicitConv: true, null);
    }

    void BoxResult(ITypeSymbol rt, Own o)
    {
        rt = T.Canonical(rt);
        ConvertValue(rt, D.ObjectType, false, ref o, false, null);
        ToOwned(o);
    }

    void EmitThunk(ISymbol member)
    {
        using (BodyScope())
        {
            _retLocal = E.AddLocal(ValType.I32);
            BeginTemps();
            var back = new List<(int i, int off)>();
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Constructor } c:
                    {
                        var ctor = D.CanonicalMethod(c);
                        var t = T.Canonical(ctor.ContainingType);
                        int tid = D.MarkConstructed(t);
                        int obj = E.AddLocal(ValType.I32);
                        if (t.IsValueType)
                        {
                            int dataOff = FunctionCompilerHelpers.BoxDataOffset(T, t);
                            E.I32(dataOff + T.StorageOf(t).size); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
                            if (!ctor.IsImplicitlyDeclared)
                                CallWith(ctor, false, () => { E.Get(obj); E.I32(dataOff); E.Op(Op.I32Add); }, () => { for (int i = 0; i < ctor.Parameters.Length; i++) PushParam(ctor.Parameters[i], i, back); }, null, null);
                        }
                        else
                        {
                            E.I32(Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
                            CallWith(ctor, false, () => E.Get(obj), () => { for (int i = 0; i < ctor.Parameters.Length; i++) PushParam(ctor.Parameters[i], i, back); }, null, null);
                        }
                        CopyBack(back);
                        E.Get(obj);
                        break;
                    }
                case IMethodSymbol ms:
                    {
                        var m = D.CanonicalMethod(ms);
                        bool virt = !m.IsStatic && (m.IsVirtual || m.IsOverride || m.IsAbstract || m.ContainingType.TypeKind == TypeKind.Interface);
                        var own = CallWith(m, virt, m.IsStatic ? null : () => PushTargetAs(m.ContainingType),
                            () => { for (int i = 0; i < m.Parameters.Length; i++) PushParam(m.Parameters[i], i, back); }, null, null);
                        if (m.ReturnsVoid) E.I32(0); else BoxResult(m.ReturnType, own);
                        if (back.Count > 0) { int r = E.AddLocal(ValType.I32); E.Set(r); CopyBack(back); E.Get(r); }
                        break;
                    }
                case IFieldSymbol f:
                    {
                        // args null: read; else write args[0]
                        var owner = (INamedTypeSymbol)T.Canonical(f.ContainingType);
                        var ft = T.Resolve(f.Type, Map);
                        int off;
                        void PushAddr()
                        {
                            if (f.IsStatic) { EmitStaticInitGuard(owner); E.I32Addr(D.StaticField(owner, f, ft)); return; }
                            PushTargetAs(owner);
                        }
                        off = f.IsStatic ? 0 : T.FieldIn(owner, f).Offset;
                        E.Get(1); E.Op(Op.I32Eqz);
                        E.If(ValType.I32);
                        {
                            PushAddr();
                            Own o;
                            if (Types.IsStruct(ft)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } o = Own.None; }
                            else { E.Load(Types.LoadOp(ft), off); o = Types.IsRef(ft) ? Own.Unstable : Own.None; }
                            BoxResult(ft, o);
                        }
                        E.Else();
                        {
                            int a = E.AddLocal(ValType.I32);
                            PushAddr(); E.Set(a);
                            E.Get(1); E.Load(Op.I32Load, Types.ArrayData);
                            var vo = Own.Unstable;
                            ConvertValue(D.ObjectType, ft, false, ref vo, explicitConv: true, null);
                            StoreToAddressFromStack(() => E.Get(a), off, ft, vo, pub: f.IsStatic ? Publish.Always : owner.IsValueType ? Publish.Unknown : Publish.Container, pubContainer: () => E.Get(a));
                            E.I32(0);
                        }
                        E.End();
                        break;
                    }
            }
            E.Set(_retLocal);
            EndTemps();
            EmitReturnSequence(true);
        }
        Finish();
    }

    void EmitAttributeFactory(AttributeData a)
    {
        using (BodyScope())
        {
            _retLocal = E.AddLocal(ValType.I32);
            BeginTemps();
            var ctor = D.CanonicalMethod(a.AttributeConstructor!);
            var t = T.Canonical(ctor.ContainingType);
            int tid = D.MarkConstructed(t);
            int obj = E.AddLocal(ValType.I32);
            E.I32(Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
            CallWith(ctor, false, () => E.Get(obj), () =>
            {
                for (int i = 0; i < ctor.Parameters.Length; i++) PushTypedConstant(a.ConstructorArguments[i], ctor.Parameters[i].Type);
            }, null, null);
            foreach (var (name, val) in a.NamedArguments)
            {
                var mem = t.GetMembers(name).FirstOrDefault() ?? ((INamedTypeSymbol)t).BaseType?.GetMembers(name).FirstOrDefault();
                if (mem is IPropertySymbol { SetMethod: { } set })
                    CallWith(D.CanonicalMethod(set), false, () => E.Get(obj), () => PushTypedConstant(val, set.Parameters[0].Type), null, null);
                else if (mem is IFieldSymbol fld)
                {
                    var ft = T.Resolve(fld.Type, Map);
                    PushTypedConstant(val, ft);
                    var o = Types.IsRef(ft) ? Own.Stable : Own.None;
                    int v = E.AddLocal(Types.WasmType(ft) ?? ValType.I32); E.Set(v);
                    StoreToAddressFromStack(() => E.Get(obj), T.FieldIn((INamedTypeSymbol)t, fld).Offset, ft, Types.IsRef(ft) ? EmitGetOwn(v) : EmitGetPlain(v), pub: Publish.Container, pubContainer: () => E.Get(obj));
                }
            }
            E.Get(obj);
            E.Set(_retLocal);
            EndTemps();
            EmitReturnSequence(true);
        }
        Finish();
    }

    Own EmitGetOwn(int l) { E.Get(l); return Own.Stable; }
    Own EmitGetPlain(int l) { E.Get(l); return Own.None; }

    void PushTypedConstant(TypedConstant c, ITypeSymbol target)
    {
        target = T.Canonical(target);
        if (c.Kind == TypedConstantKind.Type && c.Value is ITypeSymbol ts)
        {
            E.Tid(D.TypeId(T.Canonical(ts)));
            var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
            E.Call(D.Method(fromId).Fn);
            int tl = TempRef(D.ObjectType); E.Tee(tl);
            return;
        }
        if (c.Kind == TypedConstantKind.Array || c.IsNull) { E.I32(0); return; }
        var ct = c.Type != null ? T.Canonical(c.Type) : target;
        var o = EmitConstant(c.Value, ct is INamedTypeSymbol { TypeKind: TypeKind.Enum } en ? T.Canonical(en.EnumUnderlyingType!) : ct);
        var from = ct is INamedTypeSymbol { TypeKind: TypeKind.Enum } en2 ? T.Canonical(en2.EnumUnderlyingType!) : ct;
        if (Types.Key(from) != Types.Key(target)) ConvertValue(from, target, false, ref o, true, null);
    }
}
