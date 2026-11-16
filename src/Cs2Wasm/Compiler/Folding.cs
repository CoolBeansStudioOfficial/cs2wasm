using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Conditions the compiler knows the value of, beyond C# constants: what the module's target fixes (no SIMD instructions
/// yet, no event tracing, no dynamic code, invariant globalization), what an instantiation fixes (`typeof(T) ==
/// typeof(int)`, `RuntimeHelpers.IsReferenceOrContainsReferences&lt;T&gt;()`), and library properties whose body is a
/// constant. A branch on such a condition compiles only the side taken, so what the other side reaches is never
/// emitted (the upstream library guards its hardware-specific paths this way, as the JIT and the trimmer rely on).
/// </summary>
sealed partial class FunctionCompiler
{
    /// <summary>The value of a condition when it is known at compile time and evaluating it has no effect.</summary>
    bool? Fold(IOperation op)
    {
        if (Inst?.Consts != null && FoldBound(op) is bool fb) return fb;
        if (FoldKnownNull(op) is bool fn) return fn;
        switch (op)
        {
            case { ConstantValue: { HasValue: true, Value: bool b } }: return b;
            case IConversionOperation { Conversion.IsIdentity: true } c: return Fold(c.Operand);
            case IParenthesizedOperation p: return Fold(p.Operand);
            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } u: return !Fold(u.Operand);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } b:
                {
                    var l = Fold(b.LeftOperand);
                    if (l == false) return false;
                    var r = Fold(b.RightOperand);
                    if (l == true) return r;
                    // `x && false` with an x that has no effect: false (`_cached == 0 && !GlobalizationMode.Invariant`)
                    return r == false && IsPure(b.LeftOperand) ? false : null;
                }
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } b:
                {
                    var l = Fold(b.LeftOperand);
                    if (l == true) return true;
                    var r = Fold(b.RightOperand);
                    if (l == false) return r;
                    return r == true && IsPure(b.LeftOperand) ? true : null;
                }
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } b:
                {
                    bool? eq = null;
                    if (b.LeftOperand is ITypeOfOperation lt && b.RightOperand is ITypeOfOperation rt)
                        eq = Types.Key(T.Resolve(lt.TypeOperand, Map)) == Types.Key(T.Resolve(rt.TypeOperand, Map));
                    else if (FoldInt(b.LeftOperand) is { } li && FoldInt(b.RightOperand) is { } ri) eq = li == ri;
                    else if (Fold(b.LeftOperand) is { } lb && Fold(b.RightOperand) is { } rb) eq = lb == rb;
                    if (eq == null) return null;
                    return b.OperatorKind == BinaryOperatorKind.Equals ? eq : !eq;
                }
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual, OperatorMethod: null } b:
                {
                    if (FoldInt(b.LeftOperand) is not { } l || FoldInt(b.RightOperand) is not { } r) return null;
                    return b.OperatorKind switch
                    {
                        BinaryOperatorKind.LessThan => l < r, BinaryOperatorKind.LessThanOrEqual => l <= r,
                        BinaryOperatorKind.GreaterThan => l > r, _ => l >= r,
                    };
                }
            case IPropertyReferenceOperation { Instance: null } pr: return KnownBool(pr.Property);
            // an instance member the trimmer's substitutions fix (EventSource.IsEnabled() with tracing off), on a
            // receiver whose evaluation has no effect
            case IPropertyReferenceOperation { Instance: { } pi, Property.GetMethod: { } pg } when IsPure(pi) && SubstitutedBool(pg) is bool spv: return spv;
            case IInvocationOperation { Instance: { } ii } sinv when IsPure(ii) && SubstitutedBool(sinv.TargetMethod) is bool siv: return siv;
            case IInvocationOperation { Instance: null } stinv when SubstitutedBool(stinv.TargetMethod) is bool stv && stinv.Arguments.All(a => IsPure(a.Value)): return stv;
            case IFieldReferenceOperation { Instance: null, Field.IsReadOnly: true } fr when Library.FieldSubstitution(fr.Field) is "true" or "false": return Library.FieldSubstitution(fr.Field) == "true";
            case IPropertyReferenceOperation { Instance: ITypeOfOperation to, Property.Name: "IsValueType" or "IsEnum" or "IsPrimitive" or "IsInterface" } tp
                when T.Resolve(to.TypeOperand, Map) is var tt && tt is not ITypeParameterSymbol:
                return tp.Property.Name switch
                {
                    "IsValueType" => tt.IsValueType,
                    "IsEnum" => tt.TypeKind == TypeKind.Enum,
                    "IsInterface" => tt.TypeKind == TypeKind.Interface,
                    _ => tt.TypeKind != TypeKind.Enum && tt is not (IPointerTypeSymbol or IFunctionPointerTypeSymbol) && Types.PrimOf(tt) is not Prim.None,
                };
            case IInvocationOperation { Instance: null, Arguments.Length: 1, TargetMethod: { Name: "Implements", TypeArguments.Length: 1 } im } impl
                when Types.FullName(im.ContainingType) == "Cs2Wasm.Runtime.Generics" && impl.Arguments[0].Value.ConstantValue.Value is string ifaceName:
                {
                    var t = T.Resolve(im.TypeArguments[0], Map);
                    if (t is ITypeParameterSymbol) return null;
                    return t.AllInterfaces.Any(i => Types.FullName(i.OriginalDefinition) == ifaceName && i.TypeArguments.Length == 1 && Types.Key(T.Canonical(i.TypeArguments[0])) == Types.Key(t));
                }
            case IInvocationOperation { Instance: null, Arguments.Length: 0 } inv:
                {
                    var m = inv.TargetMethod;
                    var owner = Types.FullName(m.ContainingType);
                    if (owner == "Cs2Wasm.Runtime.Generics" && m.TypeArguments.Length == 1 && T.Resolve(m.TypeArguments[0], Map) is var gt && gt is not ITypeParameterSymbol)
                        switch (m.Name)
                        {
                            case "IsNullable": return gt is INamedTypeSymbol { Arity: 1 } nn && Types.FullName(nn) == "System.Nullable`1";
                            case "IsEnum": return gt.TypeKind == TypeKind.Enum;
                        }
                    if (owner == "System.Runtime.CompilerServices.RuntimeHelpers" && m.TypeArguments.Length == 1)
                    {
                        var t = T.Resolve(m.TypeArguments[0], Map);
                        if (t is ITypeParameterSymbol) return null;
                        switch (m.Name)
                        {
                            case "IsReferenceOrContainsReferences": return Types.IsRef(t) || T.NeedsRc(t);
                            case "IsBitwiseEquatable": return Types.PrimOf(t) is not (Prim.None or Prim.F32 or Prim.F64);
                            case "IsKnownConstant": return false;
                        }
                    }
                    return KnownBool(m);
                }
        }
        return null;
    }

    /// <summary>Evaluating it has no effect (and cannot throw): constants, variables, fields of such, and the
    /// built-in operators on them.</summary>
    static bool IsPure(IOperation op) => op switch
    {
        { ConstantValue.HasValue: true } => true,
        ILocalReferenceOperation or IParameterReferenceOperation or IInstanceReferenceOperation => true,
        IFieldReferenceOperation f => f.Instance == null || f.Instance is IInstanceReferenceOperation || (f.Instance.Type?.IsValueType == true && IsPure(f.Instance)),
        IConversionOperation { OperatorMethod: null, IsChecked: false } c => IsPure(c.Operand),
        IParenthesizedOperation p => IsPure(p.Operand),
        IUnaryOperation { OperatorMethod: null, IsChecked: false } u => IsPure(u.Operand),
        IBinaryOperation { OperatorMethod: null, IsChecked: false } b when b.OperatorKind is not (BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) => IsPure(b.LeftOperand) && IsPure(b.RightOperand),
        _ => false,
    };

    /// <summary>`x &amp;&amp; c` or `x || c` where c is known and has no effect: the value is fixed once x is evaluated
    /// (false, or true), so the condition is x for its effects, then that value.</summary>
    (IOperation, bool)? FoldAfterEffect(IOperation op) => op switch
    {
        IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } b when Fold(b.RightOperand) == false => (b.LeftOperand, false),
        IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } b when Fold(b.RightOperand) == true => (b.LeftOperand, true),
        _ => null,
    };

    /// <summary>An integer known at compile time: a constant, or a size an instantiation fixes.</summary>
    long? FoldInt(IOperation op)
    {
        if (Inst?.Consts != null && FoldIntBound(op) is long fi) return fi;
        switch (op)
        {
            case { ConstantValue: { HasValue: true, Value: var v } } when v is int or long or short or byte or sbyte or ushort or uint or char:
                return Convert.ToInt64(v);
            case IConversionOperation { OperatorMethod: null } c when Types.PrimOf(TypeOf(c)) is not (Prim.None or Prim.F32 or Prim.F64): return FoldInt(c.Operand);
            case ISizeOfOperation so:
                {
                    var t = T.Resolve(so.TypeOperand, Map);
                    return t is ITypeParameterSymbol ? null : T.StorageOf(t).size;
                }
            case IInvocationOperation { Instance: null, Arguments.Length: 0, TargetMethod: { Name: "SizeOf", TypeArguments.Length: 1 } m }
                when Types.FullName(m.ContainingType) == "System.Runtime.CompilerServices.Unsafe":
                {
                    var t = T.Resolve(m.TypeArguments[0], Map);
                    return t is ITypeParameterSymbol ? null : T.StorageOf(t).size;
                }
        }
        return null;
    }

    static bool? SubstitutedBool(IMethodSymbol m) => Library.Substitution(m) switch { "true" => true, "false" => false, _ => null };

    /// <summary>The value of a parameterless static bool member that the target fixes, or whose library body is a constant.</summary>
    bool? KnownBool(ISymbol member)
    {
        var def = member.OriginalDefinition;
        if ((def is IPropertySymbol { GetMethod: { } sg } ? SubstitutedBool(sg) : def is IMethodSymbol sm ? SubstitutedBool(sm) : null) is bool sub) return sub;
        var type = def.ContainingType;
        if (type == null) return null;
        var outer = type;
        while (outer.ContainingType != null) outer = outer.ContainingType;
        var ns = outer.ContainingNamespace?.ToDisplayString() ?? "";
        var owner = Types.FullName(type);
        switch (def.Name)
        {
            // no SIMD instructions are emitted yet: the scalar paths are the ones taken
            case "IsHardwareAccelerated" when ns is "System.Runtime.Intrinsics" or "System.Numerics": return false;
            case "IsSupported" when ns.StartsWith("System.Runtime.Intrinsics."): return false;
            // no event tracing (section 7.4), no code generation at run time, no debugger, no startup hooks
            case "IsSupported" when owner is "System.Diagnostics.Tracing.EventSource" or "System.StartupHookProvider": return false;
            case "IsDynamicCodeSupported" or "IsDynamicCodeCompiled" when owner == "System.Runtime.CompilerServices.RuntimeFeature": return false;
            case "IsAttached" when owner == "System.Diagnostics.Debugger": return false;
            // invariant globalization (section 7.4)
            case "Invariant" when owner is "System.Globalization.GlobalizationMode" or "System.Globalization.GlobalizationMode.Settings": return true;
            case "UseNls" when owner == "System.Globalization.GlobalizationMode": return false;
        }
        if (!def.IsStatic || !D.IsLibrary(def)) return null;
        IMethodSymbol? body = def switch
        {
            IPropertySymbol { GetMethod: { } g, Parameters.Length: 0 } => g,
            IMethodSymbol { Parameters.Length: 0, IsGenericMethod: false } m => m,
            _ => null,
        };
        if (body == null || body.DeclaringSyntaxReferences.Length != 1) return null;
        var syntax = body.DeclaringSyntaxReferences[0].GetSyntax();
        ExpressionSyntax? expr = syntax switch
        {
            ArrowExpressionClauseSyntax a => a.Expression,
            AccessorDeclarationSyntax { ExpressionBody: { } a } => a.Expression,
            AccessorDeclarationSyntax { Body.Statements: [ReturnStatementSyntax { Expression: { } r }] } => r,
            MethodDeclarationSyntax { ExpressionBody: { } a } => a.Expression,
            MethodDeclarationSyntax { Body.Statements: [ReturnStatementSyntax { Expression: { } r }] } => r,
            _ => null,
        };
        if (expr == null) return null;
        if (expr is LiteralExpressionSyntax)
            return D.ModelFor(expr.SyntaxTree).GetConstantValue(expr) is { HasValue: true, Value: bool v } ? v : null;
        // a getter whose body is itself known (`CanCompileToIL => RuntimeFeature.IsDynamicCodeSupported`)
        if (_knownDepth > 4 || D.ModelFor(expr.SyntaxTree).GetOperation(expr) is not { } eop) return null;
        _knownDepth++;
        try { return KnownExpr(eop); }
        finally { _knownDepth--; }
    }

    int _knownDepth;

    bool? KnownExpr(IOperation op)
    {
        while (op is IConversionOperation { OperatorMethod: null } c) op = c.Operand;
        switch (op)
        {
            case { ConstantValue: { HasValue: true, Value: bool cv } }: return cv;
            case IPropertyReferenceOperation { Instance: null } pr: return KnownBool(pr.Property);
            case IInvocationOperation { Instance: null, Arguments.Length: 0 } inv: return KnownBool(inv.TargetMethod);
            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } u: return KnownExpr(u.Operand) is bool x ? !x : null;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr, OperatorMethod: null } bo:
                {
                    bool and = bo.OperatorKind == BinaryOperatorKind.ConditionalAnd;
                    var l = KnownExpr(bo.LeftOperand);
                    if (l == !and) return !and;
                    var r = KnownExpr(bo.RightOperand);
                    if (r == !and) return !and;
                    return l is bool && r is bool ? and : null;
                }
        }
        return null;
    }
}

sealed partial class FunctionCompiler
{
    HashSet<ILocalSymbol>? _writtenLocals;

    /// <summary>`x == null` / `x != null` / `x is null` where x is known to be null: a null literal, a library call
    /// substituted to an empty body (Environment.GetEnvironmentVariable: the sandbox has no environment), or a local
    /// initialized from one and never assigned again.</summary>
    bool? FoldKnownNull(IOperation op)
    {
        switch (op)
        {
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: null } b:
                {
                    var side = IsNullLiteral(b.RightOperand) ? b.LeftOperand : IsNullLiteral(b.LeftOperand) ? b.RightOperand : null;
                    if (side == null) return null;
                    if (NeverNull(side)) return b.OperatorKind != BinaryOperatorKind.Equals;
                    if (!KnownNull(side, 0)) return null;
                    return b.OperatorKind == BinaryOperatorKind.Equals;
                }
            case IIsPatternOperation { Pattern: IConstantPatternOperation { Value: var pv0 } } ip0 when IsNullLiteral(pv0) && NeverNull(ip0.Value): return false;
            case IIsPatternOperation { Pattern: INegatedPatternOperation { Pattern: IConstantPatternOperation { Value: var nv0 } } } np0 when IsNullLiteral(nv0) && NeverNull(np0.Value): return true;
            case IIsPatternOperation { Pattern: IConstantPatternOperation { Value: var pv } } ip when IsNullLiteral(pv) && KnownNull(ip.Value, 0): return true;
            case IIsPatternOperation { Pattern: INegatedPatternOperation { Pattern: IConstantPatternOperation { Value: var nv } } } np when IsNullLiteral(nv) && KnownNull(np.Value, 0): return false;
        }
        return null;
    }

    /// <summary>A value of a value type that is not Nullable (a type parameter's, in an instance over one): compared
    /// with null it is never equal, and it need not be boxed to find out (generic code: `if (key == null)`). Only
    /// when reading it has no effect, as the comparison is then not evaluated at all.</summary>
    bool NeverNull(IOperation op)
    {
        while (op is IConversionOperation { OperatorMethod: null } c) op = c.Operand;
        if (op.Type == null || op.Type.TypeKind == TypeKind.Pointer || op.Type.TypeKind == TypeKind.FunctionPointer) return false;
        var t = T.Resolve(op.Type, Map);
        return t.IsValueType && t.TypeKind != TypeKind.TypeParameter && t.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T && IsPure(op);
    }

    bool KnownNull(IOperation op, int depth)
    {
        while (op is IConversionOperation { OperatorMethod: null } c) op = c.Operand;
        switch (op)
        {
            case IInvocationOperation inv when !inv.TargetMethod.ReturnsVoid && !inv.TargetMethod.ReturnType.IsValueType && Library.Substitution(inv.TargetMethod) == "":
                return true;
            case ILocalReferenceOperation lr when depth < 4:
                {
                    var l = lr.Local;
                    if (l.IsRef || WrittenLocals().Contains(l)) return false;
                    var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                    if (decl == null || D.ModelFor(decl.SyntaxTree).GetOperation(decl) is not IVariableDeclaratorOperation { Initializer.Value: { } init }) return false;
                    return IsNullLiteral(init) || KnownNull(init, depth + 1);
                }
        }
        return false;
    }

    /// <summary>The locals of the method being compiled that are assigned other than by their declaration.</summary>
    HashSet<ILocalSymbol> WrittenLocals()
    {
        if (_writtenLocals != null) return _writtenLocals;
        _writtenLocals = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        foreach (var r in M.OriginalDefinition.DeclaringSyntaxReferences)
        {
            if (D.ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not { } body) continue;
            foreach (var o in body.DescendantsAndSelf())
            {
                if (o is not ILocalReferenceOperation lr) continue;
                var p = o.Parent;
                if (p is ISimpleAssignmentOperation a && a.Target == o || p is ICompoundAssignmentOperation ca && ca.Target == o
                    || p is IIncrementOrDecrementOperation || p is IAddressOfOperation || p is IDeconstructionAssignmentOperation
                    || p is IArgumentOperation { Parameter.RefKind: not RefKind.None } || p is ITupleOperation
                    || lr.IsDeclaration && p is not IVariableDeclaratorOperation)
                    _writtenLocals.Add(lr.Local);
            }
        }
        return _writtenLocals;
    }
}
