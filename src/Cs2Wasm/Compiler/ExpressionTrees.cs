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
                    if (c.OperatorMethod != null) throw new CompileException("expression tree: user-defined conversions are not supported yet", c.Syntax.GetLocation());
                    if (c.Conversion.IsIdentity) return ExprNode(c.Operand);
                    int inner = ExprNode(c.Operand);
                    int tt = TypeObjectTemp(ty!);
                    return CallToTemp(ExprFactory(c.IsChecked ? "ConvertChecked" : "Convert", "System.Linq.Expressions.Expression", "System.Type"), () => { E.Get(inner); E.Get(tt); });
                }
            case IParameterReferenceOperation pr when _exprParams != null && _exprParams.TryGetValue(pr.Parameter, out int pl):
                return pl;
            case IParameterReferenceOperation or ILocalReferenceOperation or IInstanceReferenceOperation:
                throw new CompileException("expression tree: captured variables are not supported yet", op.Syntax.GetLocation());
            case IBinaryOperation bo:
                {
                    if (bo.OperatorMethod != null && !(bo.OperatorKind == BinaryOperatorKind.Add && bo.OperatorMethod.ContainingType.SpecialType == SpecialType.System_String))
                        throw new CompileException("expression tree: user-defined operators are not supported yet", bo.Syntax.GetLocation());
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
                    var concat = bo.OperatorMethod;
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
                    if (u.OperatorMethod != null) throw new CompileException("expression tree: user-defined operators are not supported yet", u.Syntax.GetLocation());
                    string name = u.OperatorKind switch
                    {
                        UnaryOperatorKind.Minus => u.IsChecked ? "NegateChecked" : "Negate",
                        UnaryOperatorKind.Plus => "UnaryPlus",
                        UnaryOperatorKind.Not or UnaryOperatorKind.BitwiseNegation => "Not",
                        _ => throw new CompileException("expression tree: operator " + u.OperatorKind, u.Syntax.GetLocation()),
                    };
                    int x = ExprNode(u.Operand);
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
                    if (inv.TargetMethod.IsGenericMethod) throw new CompileException("expression tree: generic methods are not supported yet", inv.Syntax.GetLocation());
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
}
