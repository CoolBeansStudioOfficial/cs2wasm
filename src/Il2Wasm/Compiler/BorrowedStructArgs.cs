using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

/// <summary>
/// Struct arguments borrowed (section 4.1: parameters borrow). A struct passed by value is copied into a temporary of
/// the caller's, with a count for each reference it holds, and released after the call (`ImmutableArray&lt;T&gt;`: a
/// retain and a release of its array per call). When the callee never writes the parameter (no assignment to it or its
/// fields, no `ref`, `out` or address of it, only readonly members called on it: readonly structs' members are) and the
/// argument is a local or a parameter of the caller's that nothing can change during the call (not captured, not
/// address-taken, not assigned by the call's other arguments), the callee gets the address of the caller's own value:
/// the copy it would have made is the same bytes, and no count is taken.
/// </summary>
internal sealed partial class Driver
{
    readonly Dictionary<string, bool> _readOnlyStructParams = [];
    static readonly bool s_noBorrowedStructs = Environment.GetEnvironmentVariable("IL2WASM_NO_BORROWED_STRUCTS") == "1";

    /// <summary>Whether m's body never writes its struct parameter at ordinal (m is the one a direct call runs).</summary>
    internal bool ReadOnlyStructParam(IMethodSymbol m, int ordinal)
    {
        if (s_noBorrowedStructs) return false;
        var def = m.OriginalDefinition;
        if (def.IsVirtual || def.IsAbstract || def.IsOverride || def.IsExtern || def.ContainingType.TypeKind == TypeKind.Interface
            || def.MethodKind is MethodKind.DelegateInvoke or MethodKind.LocalFunction or MethodKind.AnonymousFunction || def.IsAsync || def.IsIterator)
            return false;
        var key = Types.MethodKey(def) + "#" + ordinal;
        if (_readOnlyStructParams.TryGetValue(key, out var r)) return r;
        r = ComputeReadOnlyStructParam(def, ordinal);
        _readOnlyStructParams[key] = r;
        return r;
    }

    bool ComputeReadOnlyStructParam(IMethodSymbol def, int ordinal)
    {
        if (def.DeclaringSyntaxReferences.Length != 1 || ordinal >= def.Parameters.Length) return false;
        var param = def.Parameters[ordinal];
        if (param.RefKind != RefKind.None) return false;
        var sr = def.DeclaringSyntaxReferences[0];
        if (ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) return false;
        foreach (var op in body.DescendantsAndSelf())
        {
            if (op is not IParameterReferenceOperation pr || !SymbolEqualityComparer.Default.Equals(pr.Parameter, param)) continue;
            // the location: the parameter, or a field (of a field...) of it
            IOperation use = pr;
            while (use.Parent is IFieldReferenceOperation fr && ReferenceEquals(fr.Instance, use)) use = fr;
            if (!ReadOnlyUse(use)) return false;
        }
        return true;
    }

    readonly Dictionary<string, bool> _leafCallees = [];

    /// <summary>m's body calls nothing and stores only into its locals (and, a constructor, its own fields): nothing it
    /// runs can write memory a caller's struct lives in (StructArgs: a readonly struct's `this` passed by address).</summary>
    internal bool LeafCallee(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        var key = Types.MethodKey(def);
        if (_leafCallees.TryGetValue(key, out var r)) return r;
        r = def.DeclaringSyntaxReferences.Length == 1 && ModelFor(def.DeclaringSyntaxReferences[0].SyntaxTree).GetOperation(def.DeclaringSyntaxReferences[0].GetSyntax()) is { } body
            && body.DescendantsAndSelf().All(o => o switch
            {
                IBlockOperation or IMethodBodyOperation or IConstructorBodyOperation or IExpressionStatementOperation or IReturnOperation
                    or IVariableDeclarationGroupOperation or IVariableDeclarationOperation or IVariableDeclaratorOperation or IVariableInitializerOperation
                    or IFieldReferenceOperation or IParameterReferenceOperation or ILocalReferenceOperation or IInstanceReferenceOperation or ILiteralOperation
                    or IConditionalOperation or IParenthesizedOperation or IEmptyOperation => true,
                IConversionOperation c => c.OperatorMethod == null,
                IBinaryOperation bo => bo.OperatorMethod == null,
                IUnaryOperation u => u.OperatorMethod == null,
                ISimpleAssignmentOperation { IsRef: false } sa => sa.Target is ILocalReferenceOperation
                    || def.MethodKind == MethodKind.Constructor && sa.Target is IFieldReferenceOperation { Field.IsStatic: false, Instance: IInstanceReferenceOperation },
                _ => false,
            });
        _leafCallees[key] = r;
        return r;
    }

    static bool ReadOnlyUse(IOperation use)
    {
        var p = use.Parent;
        switch (p)
        {
            case IAssignmentOperation a when ReferenceEquals(a.Target, use): return false;
            case ISimpleAssignmentOperation { IsRef: true } ra when ReferenceEquals(ra.Value, use): return false;
            case IIncrementOrDecrementOperation inc when ReferenceEquals(inc.Target, use): return false;
            case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }: return false;
            case IAddressOfOperation: return false;
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.IsRef: true } }: return false;
            case IDeconstructionAssignmentOperation: return false;
            case IInvocationOperation inv when ReferenceEquals(inv.Instance, use):
                return inv.TargetMethod.IsReadOnly || inv.TargetMethod.ContainingType.IsReadOnly || inv.TargetMethod.ContainingType.TypeKind != TypeKind.Struct;
            case IPropertyReferenceOperation prop when ReferenceEquals(prop.Instance, use):
                // a getter (an assignment through the property is a write: its parent is the assignment's target)
                if (prop.Parent is IAssignmentOperation pa && ReferenceEquals(pa.Target, prop)) return false;
                if (prop.Parent is IIncrementOrDecrementOperation pi && ReferenceEquals(pi.Target, prop)) return false;
                return prop.Property.GetMethod is { } g && (g.IsReadOnly || prop.Property.ContainingType.IsReadOnly || prop.Property.ContainingType.TypeKind != TypeKind.Struct);
            case IEventReferenceOperation: return false;
        }
        return true;
    }
}

internal sealed partial class FunctionCompiler
{
    static readonly bool s_borrowThis = Environment.GetEnvironmentVariable("IL2WASM_NO_BORROWED_THIS") != "1";

    /// <summary>A struct argument passed as the address of the caller's own value (see Driver.ReadOnlyStructParam).</summary>
    bool TryPushBorrowedStruct(IParameterSymbol p, IOperation value, ITypeSymbol pt)
    {
        var v = value;
        while (v is IConversionOperation { Conversion.IsIdentity: true } c) v = c.Operand;
        if (v.Type == null || Types.Key(TypeOf(v)) != Types.Key(pt)) return false;
        ISymbol? src = v switch
        {
            ILocalReferenceOperation { Local: { IsRef: false } l } when !IsCaptured(l) && !_addrTaken.Contains(l) => l,
            IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } q } when !IsCaptured(q) && !_addrTaken.Contains(q) && !_addrTaken.Contains(q.OriginalDefinition) && !IsPrimaryParamHere(q) => q,
            // a readonly struct's `this` (or a readonly member's): its storage is the caller's, which only a callee that
            // runs nothing else can leave alone (`new Reader(this)`)
            IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } when s_borrowThis && _machine == null && _closure == null
                && M.ContainingType is { TypeKind: TypeKind.Struct } ct && (ct.IsReadOnly || M.IsReadOnly) && M.MethodKind != MethodKind.Constructor
                && p.ContainingSymbol is IMethodSymbol lm && D.LeafCallee(lm) => M.ContainingType,
            _ => null,
        };
        if (src == null || p.ContainingSymbol is not IMethodSymbol m || !D.ReadOnlyStructParam(m, p.Ordinal)) return false;
        // the call's other arguments must not assign it (`M(s, s = t)`)
        var call = value.Parent?.Parent;
        if (call != null && call.Descendants().Any(o => o switch
        {
            IAssignmentOperation a => Refers(a.Target, src),
            IIncrementOrDecrementOperation i => Refers(i.Target, src),
            IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } ra => Refers(ra.Value, src),
            _ => false,
        })) return false;
        EmitAddress(v);
        return true;
    }

    static bool Refers(IOperation target, ISymbol s)
    {
        while (target is IFieldReferenceOperation f && f.Instance != null) target = f.Instance;
        return target is ILocalReferenceOperation l && SymbolEqualityComparer.Default.Equals(l.Local, s)
            || target is IParameterReferenceOperation pr && SymbolEqualityComparer.Default.Equals(pr.Parameter, s);
    }
}
