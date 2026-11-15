using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// A lambda converted to Expression<TDelegate> (section 3: Roslyn's lowering, ExpressionLambdaRewriter): the calls to
// System.Linq.Expressions' factory methods that build the tree, as the C# compiler emits them. Members come from
// reflection at run time (typeof(T).GetProperty(name) and the like); the compiler keeps the members it names
// (ReflectionKeep), so they are found. Not yet: captured variables (Roslyn reads them through its closure class's
// fields), generic methods, statement lambdas, user-defined operators: those are build errors, not silent differences.
sealed partial class FunctionCompiler
{
    static bool IsExpressionTree(ITypeSymbol t) =>
        t is INamedTypeSymbol { Name: "Expression", Arity: 1 } n && n.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions";

    Dictionary<IParameterSymbol, int>? _exprParams;
    const int AllMembers = 4 | 8 | 16 | 32; // Instance | Static | Public | NonPublic

    INamedTypeSymbol ExprType => D.LibType("System.Linq.Expressions.Expression");

    IMethodSymbol ExprFactory(string name, params string[] paramTypes)
    {
        foreach (var m in ExprType.GetMembers(name).OfType<IMethodSymbol>())
            if (m.IsStatic && !m.IsGenericMethod && m.Parameters.Length == paramTypes.Length
                && m.Parameters.Zip(paramTypes).All(x => x.First.Type.ToDisplayString() == x.Second))
                return D.CanonicalMethod(m);
        throw new CompileException("System.Linq.Expressions.Expression." + name + "(" + string.Join(", ", paramTypes) + ") not found");
    }

    /// <summary>Calls a factory with the given argument temps (and constant pushes); the result goes to a temp.</summary>
    int CallToTemp(IMethodSymbol m, Action pushArgs)
    {
        var o = CallWith(m, false, null, pushArgs, null, null);
        var rt = T.Canonical(m.ReturnType);
        ToOwned(o);
        int t = TempRef(rt);
        E.Set(t);
        return t;
    }

    int TypeObjectTemp(ITypeSymbol t)
    {
        t = T.Resolve(t, Map);
        if (t.TypeKind == TypeKind.Enum) D.MarkConstructed(t);
        E.Tid(D.TypeId(t));
        var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
        E.Call(D.Method(fromId).Fn);
        int l = TempRef(D.LibType("System.Type"));
        E.Set(l);
        return l;
    }

    /// <summary>A new array of the element type holding the temps' values.</summary>
    int ArrayTemp(ITypeSymbol elem, List<int> items)
    {
        var at = T.Canonical(D.LibCompilation.CreateArrayTypeSymbol(T.Canonical(elem)));
        int tid = D.MarkConstructed(at);
        E.I32(items.Count); E.I32(4); E.Tid(tid); E.Call(Rt.AllocArray);
        int arr = TempRef(at);
        E.Set(arr);
        for (int i = 0; i < items.Count; i++)
        {
            E.Get(items[i]);
            StoreToAddressFromStack(() => E.Get(arr), Types.ArrayDataOffset(1) + 4 * i, T.Canonical(elem), Own.Stable, pub: Publish.None);
        }
        return arr;
    }

    Own EmitExpressionLambda(IAnonymousFunctionOperation af, INamedTypeSymbol exprType)
    {
        var td = (INamedTypeSymbol)T.Canonical(exprType.TypeArguments[0]);
        var saved = _exprParams;
        _exprParams = new Dictionary<IParameterSymbol, int>(SymbolEqualityComparer.Default);
        try
        {
            int lambda = ExpressionLambdaTemp(af, td);
            E.Get(lambda);
            return Own.Stable;
        }
        finally { _exprParams = saved; }
    }

    int ExpressionLambdaTemp(IAnonymousFunctionOperation af, INamedTypeSymbol td)
    {
        var parms = new List<int>();
        var pe = D.LibType("System.Linq.Expressions.ParameterExpression");
        foreach (var p in af.Symbol.Parameters)
        {
            int tt = TypeObjectTemp(p.Type);
            int pt = CallToTemp(ExprFactory("Parameter", "System.Type", "string?"), () => { E.Get(tt); E.I32Addr(D.StringLiteral(p.Name)); });
            _exprParams![p] = pt;
            parms.Add(pt);
        }
        var body = af.Body.Operations is [IReturnOperation { ReturnedValue: { } rv }] ? rv
            : af.Body.Operations is [IExpressionStatementOperation es, ..] && af.Body.Operations.Skip(1).All(o => o is IReturnOperation { ReturnedValue: null }) ? es.Operation
            : af.Body.Operations is [IReturnOperation { ReturnedValue: null }] ? null
            : throw new CompileException("a statement lambda cannot be an expression tree", af.Syntax.GetLocation());
        int b = body != null ? ExprNode(body) : CallToTemp(ExprFactory("Empty"), () => { });
        int arr = ArrayTemp(pe, parms);
        var lam = ExprType.GetMembers("Lambda").OfType<IMethodSymbol>().First(m => m.IsGenericMethod && m.Parameters.Length == 2
            && m.Parameters[0].Type.Name == "Expression" && m.Parameters[1].Type is IArrayTypeSymbol);
        var lm = D.CanonicalMethod(lam.Construct(td));
        return CallToTemp(lm, () => { E.Get(b); E.Get(arr); });
    }

    /// <summary>The tree node for an operation, in a temp.</summary>
    int ExprNode(IOperation op)
    {
        var ty = op.Type != null ? T.Resolve(op.Type, Map) : null;
        switch (op)
        {
            case { ConstantValue.HasValue: true } k:
                {
                    var objT = D.ObjectType;
                    var o = EmitConstant(k.ConstantValue.Value, ty);
                    ConvertTop(ty ?? objT, objT, ref o);
                    ToOwned(o);
                    int v = TempRef(objT); E.Set(v);
                    int tt = TypeObjectTemp(ty ?? objT);
                    return CallToTemp(ExprFactory("Constant", "object?", "System.Type"), () => { E.Get(v); E.Get(tt); });
                }
            case IParenthesizedOperation p: return ExprNode(p.Operand);
            case IConversionOperation c when c.Conversion.IsIdentity && !c.IsImplicit == false: return ExprNode(c.Operand);
            case IConversionOperation c:
                {
                    if (c.Conversion.IsIdentity) return ExprNode(c.Operand);
                    int inner = ExprNode(c.Operand);
                    int tt = TypeObjectTemp(ty!);
                    if (c.OperatorMethod != null)
                    {
                        int cm = MethodInfoTemp(c.OperatorMethod);
                        return CallToTemp(ExprFactory(c.IsChecked ? "ConvertChecked" : "Convert", "System.Linq.Expressions.Expression", "System.Type", "System.Reflection.MethodInfo?"), () => { E.Get(inner); E.Get(tt); E.Get(cm); });
                    }
                    return CallToTemp(ExprFactory(c.IsChecked ? "ConvertChecked" : "Convert", "System.Linq.Expressions.Expression", "System.Type"), () => { E.Get(inner); E.Get(tt); });
                }
            case IParameterReferenceOperation pr when _exprParams != null && _exprParams.TryGetValue(pr.Parameter, out int pl):
                return pl;
            // `this`: a constant, as Roslyn writes it
            case IInstanceReferenceOperation ir:
                {
                    var tt0 = T.Resolve(ir.Type!, Map);
                    var o = EmitValue(ir); ToOwned(o);
                    int v = TempRef(D.ObjectType); E.Set(v);
                    int tt = TypeObjectTemp(tt0);
                    return CallToTemp(ExprFactory("Constant", "object?", "System.Type"), () => { E.Get(v); E.Get(tt); });
                }
            // a captured variable: its field of the closure, read when the tree runs (Field(Constant(closure), field))
            case IParameterReferenceOperation or ILocalReferenceOperation:
                {
                    ISymbol sym = op is IParameterReferenceOperation pr2 ? pr2.Parameter : ((ILocalReferenceOperation)op).Local;
                    if (!IsCaptured(sym))
                    {
                        // (read by the tree only: its value)
                        var vt = T.Resolve(op.Type!, Map);
                        var o = EmitValue(op);
                        ConvertTop(vt, D.ObjectType, ref o); ToOwned(o);
                        int v0 = TempRef(D.ObjectType); E.Set(v0);
                        int t0 = TypeObjectTemp(vt);
                        return CallToTemp(ExprFactory("Constant", "object?", "System.Type"), () => { E.Get(v0); E.Get(t0); });
                    }
                    var disp = CapturedDisplay(sym);
                    CapturedAddress(sym); E.Call(Rt.Retain);
                    int dobj = TempRef(D.ObjectType); E.Set(dobj);
                    E.Tid(disp.TypeId);
                    E.Call(D.Method(D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First()).Fn);
                    int dt = TempRef(D.LibType("System.Type")); E.Set(dt);
                    int c = CallToTemp(ExprFactory("Constant", "object?", "System.Type"), () => { E.Get(dobj); E.Get(dt); });
                    var entry = D.DisplayFieldEntry(disp, sym, CapturedType(sym));
                    E.I32Addr(entry);
                    E.Call(D.LibMethod("Cs2Wasm.Runtime.Meta", "FieldFromEntry", 1).Fn);
                    int fi = TempRef(D.LibType("System.Reflection.FieldInfo")); E.Set(fi);
                    return CallToTemp(ExprFactory("Field", "System.Linq.Expressions.Expression?", "System.Reflection.FieldInfo"), () => { E.Get(c); E.Get(fi); });
                }
            case IBinaryOperation bo:
                {
                    string name = bo.OperatorKind switch
                    {
                        BinaryOperatorKind.Add => bo.IsChecked ? "AddChecked" : "Add",
                        BinaryOperatorKind.Subtract => bo.IsChecked ? "SubtractChecked" : "Subtract",
                        BinaryOperatorKind.Multiply => bo.IsChecked ? "MultiplyChecked" : "Multiply",
                        BinaryOperatorKind.Divide => "Divide",
                        BinaryOperatorKind.Remainder => "Modulo",
                        BinaryOperatorKind.And => "And",
                        BinaryOperatorKind.Or => "Or",
                        BinaryOperatorKind.ExclusiveOr => "ExclusiveOr",
                        BinaryOperatorKind.LeftShift => "LeftShift",
                        BinaryOperatorKind.RightShift => "RightShift",
                        BinaryOperatorKind.Equals => "Equal",
                        BinaryOperatorKind.NotEquals => "NotEqual",
                        BinaryOperatorKind.LessThan => "LessThan",
                        BinaryOperatorKind.LessThanOrEqual => "LessThanOrEqual",
                        BinaryOperatorKind.GreaterThan => "GreaterThan",
                        BinaryOperatorKind.GreaterThanOrEqual => "GreaterThanOrEqual",
                        BinaryOperatorKind.ConditionalAnd => "AndAlso",
                        BinaryOperatorKind.ConditionalOr => "OrElse",
                        _ => throw new CompileException("expression tree: operator " + bo.OperatorKind, bo.Syntax.GetLocation()),
                    };
                    int l = ExprNode(bo.LeftOperand), r = ExprNode(bo.RightOperand);
                    // string concatenation: Add with String.Concat for the operand types, as Roslyn writes it
                    var concat = bo.OperatorMethod is { ContainingType.SpecialType: SpecialType.System_String } ? bo.OperatorMethod : null;
                    if (concat == null && bo.OperatorKind == BinaryOperatorKind.Add && ty?.SpecialType == SpecialType.System_String)
                    {
                        bool both = bo.LeftOperand.Type?.SpecialType == SpecialType.System_String && bo.RightOperand.Type?.SpecialType == SpecialType.System_String;
                        var pt = both ? SpecialType.System_String : SpecialType.System_Object;
                        concat = D.LibType("System.String").GetMembers("Concat").OfType<IMethodSymbol>().First(c => c.Parameters.Length == 2 && c.Parameters.All(p => p.Type.SpecialType == pt));
                    }
                    if (concat != null)
                    {
                        int mi = MethodInfoTemp(concat);
                        return CallToTemp(ExprFactory(name, "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression", "System.Reflection.MethodInfo?"), () => { E.Get(l); E.Get(r); E.Get(mi); });
                    }
                    if (bo.OperatorMethod != null && name is "Equal" or "NotEqual" or "LessThan" or "LessThanOrEqual" or "GreaterThan" or "GreaterThanOrEqual")
                    {
                        // comparisons: (left, right, liftToNull: false, method)
                        int cmi = MethodInfoTemp(bo.OperatorMethod);
                        return CallToTemp(ExprFactory(name, "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression", "bool", "System.Reflection.MethodInfo?"), () => { E.Get(l); E.Get(r); E.I32(0); E.Get(cmi); });
                    }
                    if (bo.OperatorMethod != null)
                    {
                        // string + string: Expression.Add(l, r, string.Concat(object, object)) as Roslyn writes it
                        int mi = MethodInfoTemp(bo.OperatorMethod);
                        return CallToTemp(ExprFactory(name, "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression", "System.Reflection.MethodInfo?"), () => { E.Get(l); E.Get(r); E.Get(mi); });
                    }
                    return CallToTemp(ExprFactory(name, "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression"), () => { E.Get(l); E.Get(r); });
                }
            case IUnaryOperation u:
                {
                    string name = u.OperatorKind switch
                    {
                        UnaryOperatorKind.Minus => u.IsChecked ? "NegateChecked" : "Negate",
                        UnaryOperatorKind.Plus => "UnaryPlus",
                        UnaryOperatorKind.Not or UnaryOperatorKind.BitwiseNegation => "Not",
                        _ => throw new CompileException("expression tree: operator " + u.OperatorKind, u.Syntax.GetLocation()),
                    };
                    int x = ExprNode(u.Operand);
                    if (u.OperatorMethod != null)
                    {
                        int um = MethodInfoTemp(u.OperatorMethod);
                        return CallToTemp(ExprFactory(name, "System.Linq.Expressions.Expression", "System.Reflection.MethodInfo?"), () => { E.Get(x); E.Get(um); });
                    }
                    return CallToTemp(ExprFactory(name, "System.Linq.Expressions.Expression"), () => E.Get(x));
                }
            case IConditionalOperation { WhenFalse: { } wf } co:
                {
                    int t = ExprNode(co.Condition), a = ExprNode(co.WhenTrue), b = ExprNode(wf);
                    return CallToTemp(ExprFactory("Condition", "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression"), () => { E.Get(t); E.Get(a); E.Get(b); });
                }
            case IPropertyReferenceOperation { Instance: { } ai, Property.Name: "Length" } when T.Resolve(ai.Type!, Map) is IArrayTypeSymbol { Rank: 1 }:
                {
                    int a = ExprNode(ai);
                    return CallToTemp(ExprFactory("ArrayLength", "System.Linq.Expressions.Expression"), () => E.Get(a));
                }
            case IPropertyReferenceOperation pr when pr.Arguments.Length == 0:
                {
                    int inst = pr.Instance != null ? ExprNode(pr.Instance) : -1;
                    int info = MemberInfoTemp(pr.Property.ContainingType, pr.Property.Name, "GetProperty");
                    return CallToTemp(ExprFactory("Property", "System.Linq.Expressions.Expression?", "System.Reflection.PropertyInfo"), () => { if (inst >= 0) E.Get(inst); else E.I32(0); E.Get(info); });
                }
            case IFieldReferenceOperation fr:
                {
                    int inst = fr.Instance != null ? ExprNode(fr.Instance) : -1;
                    int info = MemberInfoTemp(fr.Field.ContainingType, fr.Field.Name, "GetField");
                    return CallToTemp(ExprFactory("Field", "System.Linq.Expressions.Expression?", "System.Reflection.FieldInfo"), () => { if (inst >= 0) E.Get(inst); else E.I32(0); E.Get(info); });
                }
            case IInvocationOperation inv:
                {
                    int inst = inv.Instance != null ? ExprNode(inv.Instance) : -1;
                    int mi = MethodInfoTemp(inv.TargetMethod);
                    var args = inv.Arguments.OrderBy(a => a.Parameter!.Ordinal).Select(a => ExprNode(a.Value)).ToList();
                    int arr = ArrayTemp(ExprType, args);
                    return CallToTemp(ExprFactory("Call", "System.Linq.Expressions.Expression?", "System.Reflection.MethodInfo", "System.Linq.Expressions.Expression[]?"), () => { if (inst >= 0) E.Get(inst); else E.I32(0); E.Get(mi); E.Get(arr); });
                }
            case IObjectCreationOperation oc when oc.Constructor != null && oc.Initializer == null:
                {
                    int ci = ConstructorInfoTemp(oc.Constructor);
                    var args = oc.Arguments.OrderBy(a => a.Parameter!.Ordinal).Select(a => ExprNode(a.Value)).ToList();
                    int arr = ArrayTemp(ExprType, args);
                    return CallToTemp(ExprFactory("New", "System.Reflection.ConstructorInfo", "System.Linq.Expressions.Expression[]?"), () => { E.Get(ci); E.Get(arr); });
                }
            case IArrayElementReferenceOperation ae when ae.Indices.Length == 1:
                {
                    int a = ExprNode(ae.ArrayReference), i = ExprNode(ae.Indices[0]);
                    return CallToTemp(ExprFactory("ArrayIndex", "System.Linq.Expressions.Expression", "System.Linq.Expressions.Expression"), () => { E.Get(a); E.Get(i); });
                }
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation inner } dc:
                return ExpressionLambdaTemp(inner, (INamedTypeSymbol)T.Canonical(T.Resolve(dc.Type!, Map)));
        }
        throw new CompileException("expression tree: " + op.Kind + " is not supported yet", op.Syntax.GetLocation());
    }

    int MemberInfoTemp(ITypeSymbol owner, string name, string getter)
    {
        var ot = (INamedTypeSymbol)T.Canonical(T.Resolve(owner, Map));
        D.KeepReflected(ot, name);
        int tt = TypeObjectTemp(ot);
        var g = D.LibType("System.Type").GetMembers(getter).OfType<IMethodSymbol>().First(m => m.Parameters.Length == 2 && m.Parameters[0].Type.SpecialType == SpecialType.System_String && m.Parameters[1].Type.Name == "BindingFlags");
        var o = CallWith(D.CanonicalMethod(g), true, () => E.Get(tt), () => { E.I32Addr(D.StringLiteral(name)); E.I32(AllMembers); }, null, null);
        ToOwned(o);
        int l = TempRef(T.Canonical(g.ReturnType)); E.Set(l);
        return l;
    }

    int MethodInfoTemp(IMethodSymbol m)
    {
        if (m.ReducedFrom != null) m = m.ReducedFrom.Construct([.. m.TypeArguments]);
        if (m.IsGenericMethod)
        {
            // the definition by name, parameter count and order among such, made with the arguments
            var targs = m.TypeArguments.Select(a => T.Canonical(T.Resolve(a, Map))).ToArray();
            var gt = (INamedTypeSymbol)T.Canonical(T.Resolve(m.ContainingType, Map));
            D.KeepReflectedInstance(gt, m.OriginalDefinition.Construct(targs));
            int got = TypeObjectTemp(gt);
            int ta = ArrayTemp(D.LibType("System.Type"), targs.Select(a => TypeObjectTemp(a)).ToList());
            var gm = D.LibMethod("Cs2Wasm.Runtime.Meta", "GenericMethod", 5);
            int idx = GenericOverloadIndex(m);
            var go = CallWith(gm.Sym, false, null, () => { E.Get(got); E.I32Addr(D.StringLiteral(m.Name)); E.I32(m.Parameters.Length); E.I32(idx); E.Get(ta); }, null, null);
            ToOwned(go);
            int gl = TempRef(D.LibType("System.Reflection.MethodInfo")); E.Set(gl);
            return gl;
        }
        var ot = (INamedTypeSymbol)T.Canonical(T.Resolve(m.ContainingType, Map));
        D.KeepReflected(ot, m.Name);
        int tt = TypeObjectTemp(ot);
        var types = m.Parameters.Select(p => TypeObjectTemp(p.Type)).ToList();
        int arr = ArrayTemp(D.LibType("System.Type"), types);
        var g = D.LibType("System.Type").GetMembers("GetMethod").OfType<IMethodSymbol>().First(x => x.Parameters.Length == 3 && x.Parameters[1].Type.Name == "BindingFlags" && x.Parameters[2].Type is IArrayTypeSymbol);
        var o = CallWith(D.CanonicalMethod(g), true, () => E.Get(tt), () => { E.I32Addr(D.StringLiteral(m.Name)); E.I32(AllMembers); E.Get(arr); }, null, null);
        ToOwned(o);
        int l = TempRef(T.Canonical(g.ReturnType)); E.Set(l);
        return l;
    }

    /// <summary>Which of the type's generic methods of this name, arity and parameter count (in member order) it is.</summary>
    static int GenericOverloadIndex(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        int i = 0;
        foreach (var x in def.ContainingType.GetMembers(def.Name).OfType<IMethodSymbol>())
        {
            if (!x.IsGenericMethod || x.Arity != def.Arity || x.Parameters.Length != def.Parameters.Length) continue;
            if (SymbolEqualityComparer.Default.Equals(x, def)) return i;
            i++;
        }
        return 0;
    }

    int ConstructorInfoTemp(IMethodSymbol c)
    {
        var ot = (INamedTypeSymbol)T.Canonical(T.Resolve(c.ContainingType, Map));
        D.KeepReflected(ot, null, ctors: true);
        int tt = TypeObjectTemp(ot);
        var types = c.Parameters.Select(p => TypeObjectTemp(p.Type)).ToList();
        int arr = ArrayTemp(D.LibType("System.Type"), types);
        var g = D.LibType("System.Type").GetMembers("GetConstructor").OfType<IMethodSymbol>().First(x => x.Parameters.Length == 1 && x.Parameters[0].Type is IArrayTypeSymbol);
        var o = CallWith(D.CanonicalMethod(g), true, () => E.Get(tt), () => E.Get(arr), null, null);
        ToOwned(o);
        int l = TempRef(T.Canonical(g.ReturnType)); E.Set(l);
        return l;
    }
}

internal sealed partial class Driver
{
    readonly List<(INamedTypeSymbol type, string? name, bool ctors)> _keepFromTrees = [];

    /// <summary>A member the compiler's own code looks up by reflection (expression trees).</summary>
    public void KeepReflected(INamedTypeSymbol t, string? name, bool ctors = false) => _keepFromTrees.Add((t, name, ctors));
    readonly List<(INamedTypeSymbol type, IMethodSymbol inst)> _keepInstancesFromTrees = [];
    public void KeepReflectedInstance(INamedTypeSymbol t, IMethodSymbol inst) => _keepInstancesFromTrees.Add((t, inst));

    // a captured variable's field, as reflection sees it (expression trees): an entry of a field record whose
    // type, declaring type (the closure) and thunk are written at Finish, when ids and slots exist
    readonly List<(DataAddr entry, Display d, ITypeSymbol type, WasmFunction thunk)> _displayFields = [];
    readonly Dictionary<(Display, string), DataAddr> _displayFieldEntries = [];

    public DataAddr DisplayFieldEntry(Display d, ISymbol v, ITypeSymbol type)
    {
        if (_displayFieldEntries.TryGetValue((d, v.Name), out var e)) return e;
        int off = d.Slots[v].offset;
        var fn = Module.AddFunction("reflect closure field " + v.Name, ThunkType);
        fn.TableRoot = true;
        var mi = new MethodInst { Sym = LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Sym, Fn = fn, Key = "closure field " + d.TypeId + " " + v.Name, Impl = MethodImpl.Body };
        mi.Compile = () => FunctionCompiler.CompileDisplayFieldThunk(this, fn, off, type, mi);
        EnqueueExtra(mi);
        // [name, type, flags (public instance), thunk, declaring type, 0, 0, token]
        e = (DataAddr)Ints([StringLiteral(v.Name).Address, 0, 2, 0, 0, 0, 0, 0x04000000 | (0xFFFFFF - _displayFields.Count)]);
        _displayFieldEntries[(d, v.Name)] = e;
        _displayFields.Add((e, d, type, fn));
        return e;
    }

    internal void PatchDisplayFields()
    {
        foreach (var (e, d, type, fn) in _displayFields)
        {
            PatchData(e, 4, ExtId(TypeId(Types.Canonical(type))));
            PatchData(e, 12, Module.TableSlot(fn) + 1);
            PatchData(e, 16, ExtId(d.TypeId));
        }
    }
}
