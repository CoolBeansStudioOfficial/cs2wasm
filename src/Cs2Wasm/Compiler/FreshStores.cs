using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// A store that cannot close a cycle (section 4.3). A store into an object closes a cycle only if the value stored
// reaches the object, and nothing reaches an object that only the code running knows: one it has just made (`new`, or
// a call that returns one it made) and has not yet let out of its hands, neither stored anywhere, nor passed to
// anything that could keep it, nor captured. A store into such an object is a plain counted store: no arena, whatever
// the types say. If the object is later stored somewhere a cycle can close, that store links it, and its contents with
// it (the arena runtime links what a joining object already holds), so nothing is missed: only the cost moves, from
// every store that builds a structure to the first store that could make it cyclic, and never comes at all for a
// tree or a list that is built, used and let go.
internal sealed partial class Driver
{
    readonly Dictionary<string, bool> _returnsFresh = [];
    readonly Dictionary<string, bool> _ctorsKeepThis = [];
    static readonly bool s_noFreshStores = Environment.GetEnvironmentVariable("CS2WASM_NO_FRESH_STORES") == "1";
    internal static bool NoFreshStores => s_noFreshStores;

    /// <summary>No constructor of the class or of its bases lets `this` out (stores it, passes it on, captures it): an
    /// object just constructed is known to the constructing code only.</summary>
    internal bool CtorsKeepThis(INamedTypeSymbol t)
    {
        t = t.OriginalDefinition;
        if (t.SpecialType == SpecialType.System_Object) return true;
        if (t.TypeKind != TypeKind.Class) return false;
        var key = Types.Key(t);
        if (_ctorsKeepThis.TryGetValue(key, out var r)) return r;
        _ctorsKeepThis[key] = false;
        r = t.BaseType == null || CtorsKeepThis(t.BaseType);
        // (a finalizer may let `this` out too, but only once nothing refers to the object)
        foreach (var c in t.InstanceConstructors)
        {
            if (!r) break;
            if (c.IsImplicitlyDeclared && c.DeclaringSyntaxReferences.Length == 0) { if (t.DeclaringSyntaxReferences.Length == 0) r = false; continue; }
            if (c.DeclaringSyntaxReferences.Length == 0) { r = false; break; }
            foreach (var sr in c.DeclaringSyntaxReferences)
            {
                if (ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) { if (sr.GetSyntax() is not TypeDeclarationSyntax) r = false; continue; }
                foreach (var op in body.DescendantsAndSelf())
                {
                    if (op is not IInstanceReferenceOperation ir) continue;
                    if (ir.Parent is IFieldReferenceOperation) continue;
                    if (ir.Parent is IPropertyReferenceOperation pr && AutoPropertyField(pr.Property.OriginalDefinition) != null) continue;
                    if (ir.Parent is IInvocationOperation { TargetMethod.MethodKind: MethodKind.Constructor }) continue; // : base(...) / : this(...)
                    r = false;
                    break;
                }
            }
        }
        // field and property initializers run in the constructor: they cannot name `this`, but a lambda in one can
        // capture nothing of it either (C# forbids it): nothing to look at
        _ctorsKeepThis[key] = r;
        return r;
    }

    /// <summary>The local holds an object only this code knows, at every point before `end` in the text: it is declared
    /// with `new` (constructors that keep `this`) or a call that returns such an object, never assigned again, and every
    /// use of it before `end` keeps it in (a field read or written, a call that does not keep its argument).</summary>
    internal bool FreshLocalBefore(IOperation body, ILocalSymbol l, int end)
    {
        if (l.IsRef || l.Type.TypeKind != TypeKind.Class) return false;
        var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (decl is not VariableDeclaratorSyntax || ModelFor(decl.SyntaxTree).GetOperation(decl) is not IVariableDeclaratorOperation vd) return false;
        var init = vd.Initializer?.Value ?? (vd.Parent as IVariableDeclarationOperation)?.Initializer?.Value;
        while (init is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) init = c.Operand;
        INamedTypeSymbol? exact = null;
        switch (init)
        {
            case IObjectCreationOperation { Constructor: not null, Type: INamedTypeSymbol { TypeKind: TypeKind.Class } nt } oc:
                if (!CtorsKeepThis(nt)) return false;
                if (oc.Initializer != null && !InitializerKeepsIn(oc.Initializer, nt)) return false;
                exact = nt;
                break;
            case IInvocationOperation inv:
                if (!ReturnsFresh(inv)) return false;
                break;
            default:
                return false;
        }
        foreach (var op in body.DescendantsAndSelf())
        {
            if (op is not ILocalReferenceOperation lr || !SymbolEqualityComparer.Default.Equals(lr.Local, l)) continue;
            // (written again, or used in a lambda or a local function: no longer the one object, or known elsewhere)
            var p = lr.Parent;
            if (p is IAssignmentOperation a && a.Target == lr || p is IIncrementOrDecrementOperation || p is IAddressOfOperation
                || p is IArgumentOperation { Parameter.RefKind: not RefKind.None } || p is ITupleOperation { Parent: IDeconstructionAssignmentOperation }
                || lr.IsDeclaration) return false;
            for (var q = lr.Parent; q != null && !ReferenceEquals(q, body); q = q.Parent)
                if (q is IAnonymousFunctionOperation or ILocalFunctionOperation) return false;
            if (lr.Syntax.SpanStart >= end) continue;
            if (UseEscape(lr, exact, alias: false) != Esc.No) return false;
        }
        return true;
    }

    /// <summary>An initializer's assignments keep the new object in: fields, and properties whose setters do.</summary>
    bool InitializerKeepsIn(IObjectOrCollectionInitializerOperation init, INamedTypeSymbol t)
    {
        foreach (var i in init.Initializers)
        {
            if (i is not ISimpleAssignmentOperation { Target: var target }) return false;
            switch (target)
            {
                case IFieldReferenceOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver } }: break;
                case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }, Property.SetMethod: { } set }:
                    if (ParamEscape(set, -1, t) != Esc.No) return false;
                    break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>The call returns an object only it knew: the method (not one a subclass can replace) returns, at every
    /// return, an object it made and kept in until then.</summary>
    internal bool ReturnsFresh(IInvocationOperation inv)
    {
        var m = inv.TargetMethod.OriginalDefinition;
        if (m.IsAbstract || m.IsExtern || m.IsAsync || m.IsIterator || m.ReturnsByRef || m.ReturnsByRefReadonly) return false;
        if (inv.IsVirtual && (m.IsVirtual || m.IsOverride) && !m.IsSealed && !m.ContainingType.IsSealed) return false;
        if (m.ContainingType.TypeKind == TypeKind.Interface) return false;
        if (m.ReturnType.TypeKind != TypeKind.Class) return false;
        var key = Types.MethodKey(m);
        if (_returnsFresh.TryGetValue(key, out var r)) return r;
        _returnsFresh[key] = false; // (while deciding: a call of itself is not one)
        r = false;
        if (m.DeclaringSyntaxReferences.Length == 1 && ModelFor(m.DeclaringSyntaxReferences[0].SyntaxTree).GetOperation(m.DeclaringSyntaxReferences[0].GetSyntax()) is { } body
            && !body.Descendants().Any(o => o is ILabeledOperation || o is IBranchOperation { BranchKind: BranchKind.GoTo } || o.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak))
        {
            var nested = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).SelectMany(o => o.DescendantsAndSelf()).ToHashSet();
            var rets = body.DescendantsAndSelf().OfType<IReturnOperation>().Where(o => !nested.Contains(o)).ToList();
            r = rets.Count > 0 && rets.All(ret =>
            {
                var v = ret.ReturnedValue;
                while (v is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) v = c.Operand;
                return v switch
                {
                    IObjectCreationOperation { Constructor: not null, Type: INamedTypeSymbol { TypeKind: TypeKind.Class } nt } oc
                        => CtorsKeepThis(nt) && (oc.Initializer == null || InitializerKeepsIn(oc.Initializer, nt)),
                    ILocalReferenceOperation lr => !InLoopOutsideDeclaration(ret, lr.Local, body) && FreshLocalBefore(body, lr.Local, ret.Syntax.SpanStart),
                    IInvocationOperation call => ReturnsFresh(call),
                    _ => false,
                };
            });
        }
        _returnsFresh[key] = r;
        return r;
    }

    /// <summary>The operation is in a loop the local is declared outside of (code later in the text runs before it then).</summary>
    internal static bool InLoopOutsideDeclaration(IOperation at, ILocalSymbol l, IOperation body)
    {
        var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (decl == null) return true;
        for (var p = at.Parent; p != null && !ReferenceEquals(p, body); p = p.Parent)
            if (p is ILoopOperation loop && !loop.Syntax.Span.Contains(decl.Span)) return true;
        return false;
    }
}

sealed partial class FunctionCompiler
{
    IOperation? _bodyOp;
    bool? _bodyHasGoto;

    /// <summary>A store into this object cannot close a cycle: the object an initializer fills (`new C { F = v }`), or a
    /// local that holds an object only this method knows up to here.</summary>
    bool StoresIntoFresh(IOperation? instance, IOperation at)
    {
        if (Driver.NoFreshStores || instance == null || _closure != null || _machine != null) return false;
        while (instance is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } c) instance = c.Operand;
        if (instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver, Type: INamedTypeSymbol { TypeKind: TypeKind.Class } nt })
            return D.CtorsKeepThis(nt);
        if (instance is not ILocalReferenceOperation { Local: var l }) return false;
        if (IsCaptured(l) || _addrTaken.Contains(l)) return false;
        if (_bodyOp == null)
        {
            foreach (var r in M.OriginalDefinition.DeclaringSyntaxReferences)
                if (D.ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is { } b) { _bodyOp = b; break; }
            if (_bodyOp == null) return false;
        }
        _bodyHasGoto ??= _bodyOp.Descendants().Any(o => o is ILabeledOperation || o is IBranchOperation { BranchKind: BranchKind.GoTo });
        if (_bodyHasGoto == true) return false;
        if (Driver.InLoopOutsideDeclaration(at, l, _bodyOp)) return false;
        return D.FreshLocalBefore(_bodyOp, l, at.Syntax.Span.End);
    }
}
