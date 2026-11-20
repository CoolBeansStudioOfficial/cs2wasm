using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Plugin interface version 5: the list of unsure sites (section 7.1, audit A4.8) and delegate targets where known.
internal sealed partial class PluginHost
{
    /// <summary>The functions a delegate invocation can run: the lambdas and methods of every delegate its receiver can
    /// be (through locals, by def-use); null when it can be a delegate made elsewhere, or for a call that is not a
    /// delegate's.</summary>
    public IReadOnlyList<IMethodSymbol>? DelegateTargets(IOperation invocation)
    {
        if (invocation is not IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke, Instance: { } inst }) return null;
        var origins = Origins(inst);
        if (origins == null) return null;
        var r = new List<IMethodSymbol>();
        foreach (var o in origins)
        {
            if (o.Kind == OriginKind.Null) continue;
            if (o.Kind != OriginKind.Allocation || StripRef(o.Site!) is not IDelegateCreationOperation dc) return null;
            IMethodSymbol? t = dc.Target switch
            {
                IAnonymousFunctionOperation af => af.Symbol,
                IFlowAnonymousFunctionOperation ff => ff.Symbol,
                IMethodReferenceOperation mr => mr.Method,
                _ => null,
            };
            if (t == null) return null;
            if (!r.Contains(t, SymbolEqualityComparer.Default)) r.Add(t);
        }
        return r;
    }

    static readonly HashSet<string> s_reflectionOnType = ["GetMethod", "GetMethods", "GetField", "GetFields", "GetProperty", "GetProperties",
        "GetMember", "GetMembers", "GetConstructor", "GetConstructors", "GetEvent", "GetEvents", "GetType", "InvokeMember", "GetInterface",
        "GetNestedType", "MakeGenericType", "GetDefaultMembers"];

    static string? ReflectionCall(IMethodSymbol m)
    {
        var t = m.ContainingType;
        if (t == null) return null;
        var ns = t.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "System.Reflection" || ns.StartsWith("System.Reflection.", StringComparison.Ordinal)) return t.Name + "." + m.Name;
        if (ns == "System" && t.Name == "Activator") return "Activator." + m.Name;
        if (ns == "System" && t.Name == "Type" && s_reflectionOnType.Contains(m.Name) && !(m.Name == "GetType" && m.Parameters.Length == 0)) return "Type." + m.Name;
        if (ns == "System" && t.Name is "Delegate" or "MulticastDelegate" && m.Name is "DynamicInvoke" or "CreateDelegate") return "Delegate." + m.Name;
        return null;
    }

    static string? UnsafeCall(IMethodSymbol m) => m.ContainingType is { Name: "Unsafe" or "MemoryMarshal" or "CollectionsMarshal" } t
        && t.ContainingNamespace?.ToDisplayString() is "System.Runtime.CompilerServices" or "System.Runtime.InteropServices"
        && !(t.Name == "CollectionsMarshal" && m.Name is "AsSpan" or "GetValueRefOrNullRef" or "GetValueRefOrAddDefault" or "SetCount") ? t.Name + "." + m.Name : null;

    /// <summary>How a member or element reference is used through a reference, if it is.</summary>
    static string? RefUse(IOperation op)
    {
        var p = op.Parent;
        while (p is IConversionOperation { IsImplicit: true }) { op = p; p = p.Parent; }
        return p switch
        {
            IArgumentOperation { Parameter.RefKind: RefKind.Ref } => "passed as a ref argument",
            IArgumentOperation { Parameter.RefKind: RefKind.Out } => "passed as an out argument",
            IArgumentOperation { Parameter.RefKind: RefKind.In or RefKind.RefReadOnlyParameter } => "passed as an in argument",
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => "a ref local refers to it",
            ISimpleAssignmentOperation { IsRef: true } a when ReferenceEquals(a.Value, op) => "a ref local refers to it",
            IReturnOperation when op.Syntax.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.RefExpressionSyntax => "returned by reference",
            _ => null,
        };
    }

    /// <summary>A view that writes the entries it is over (a read-only one cannot).</summary>
    static bool IsView(ITypeSymbol? t) => t is INamedTypeSymbol { Name: "Span" or "Memory" or "ArraySegment" } n && n.ContainingNamespace?.ToDisplayString() == "System";

    List<UnsureSite>? _unsure;
    public IReadOnlyList<UnsureSite> UnsureSites
    {
        get
        {
            if (_unsure != null) return _unsure;
            _ = Accesses; // (resolves the locals that are a member's collection)
            _unsure = [];
            var list = _unsure;
            var entryOps = new HashSet<IOperation>(ReferenceEqualityComparer.Instance);
            foreach (var m in SourceDefinitions())
            {
                var body = Body(m);
                if (body == null) continue;
                foreach (var op in body.DescendantsAndSelf())
                    foreach (var s in Entries.Of(op)) entryOps.Add(Entries.Strip(s.Collection));
                foreach (var op in body.DescendantsAndSelf())
                {
                    void Add(UnsureKind k, string why, ISymbol? member = null) => list.Add(new UnsureSite(k, InnerFunction(op) ?? m, op, why, member?.OriginalDefinition));
                    switch (op)
                    {
                        case IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke } inv:
                            if (DelegateTargets(inv) == null) Add(UnsureKind.UnknownDelegate, "a delegate whose target is not known here: " + inv.Instance?.Syntax);
                            break;
                        case IInvocationOperation inv when ReflectionCall(inv.TargetMethod) is { } rc:
                            Add(UnsureKind.Reflection, rc);
                            break;
                        case IInvocationOperation inv when UnsafeCall(inv.TargetMethod) is { } uc:
                            Add(UnsureKind.Unsafe, uc);
                            break;
                        case IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation:
                            Add(UnsureKind.Dynamic, "dynamic: " + op.Syntax);
                            break;
                        case IAddressOfOperation ao when Entries.Strip(ao.Reference) is IMemberReferenceOperation or IArrayElementReferenceOperation:
                            Add(UnsureKind.Unsafe, "the address of " + ao.Reference.Syntax, (Entries.Strip(ao.Reference) as IMemberReferenceOperation)?.Member);
                            break;
                    }
                    // a member, or an element of the collection a member holds, used through a reference
                    if (op is IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation && RefUse(op) is { } ru)
                    {
                        ISymbol? member = op is IMemberReferenceOperation { Member: var mm } && op is not IPropertyReferenceOperation { Property.IsIndexer: true } ? mm
                            : Entries.Of(op).Select(s => (s.Collection as IMemberReferenceOperation)?.Member).FirstOrDefault(x => x != null);
                        if (member != null || op is not IArrayElementReferenceOperation) Add(UnsureKind.Reference, op.Syntax + ": " + ru, member);
                    }
                    // entries through a local that is a member's collection on some paths and not on others
                    foreach (var s in Entries.Of(op))
                        if (s.Collection is ILocalReferenceOperation { Syntax: { } ls } lr && !D.EntryLocalSites.ContainsKey(Driver.SiteKey(ls)) && MemberStoredIn(body, lr.Local) is { } held)
                        { Add(UnsureKind.CollectionEscape, held.Name + " is reached through " + lr.Local.Name + ", which is not always it", held); break; }
                    // a collection a member holds, where its entries are no longer followed
                    // (a view is one even where the call that makes it counts as an entry read)
                    if (op is IFieldReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: false } && Entries.IsCollection(op.Type)
                        && (ViewOf(op) ?? (entryOps.Contains(op) ? null : CollectionLeaves(op))) is { } why)
                        Add(UnsureKind.CollectionEscape, ((IMemberReferenceOperation)op).Member.Name + " " + why, ((IMemberReferenceOperation)op).Member);
                }
            }
            // reflection the compiler cannot follow (its own warnings)
            foreach (var d in D.Diagnostics)
            {
                if (d.Id != Diags.ReflectionUnknown.Id || !d.Location.IsInSource || d.Location.SourceTree is not { } tree || !D.IsInput(tree)) continue;
                var model = D.ModelFor(tree);
                var node = tree.GetRoot().FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
                IOperation? op = null;
                for (var n = node; n != null && op == null; n = n.Parent) op = model.GetOperation(n);
                if (op == null || model.GetEnclosingSymbol(node.SpanStart) is not IMethodSymbol em) continue;
                if (list.Any(u => u.Kind == UnsureKind.Reflection && u.Operation.Syntax.Span.Contains(d.Location.SourceSpan))) continue;
                list.Add(new UnsureSite(UnsureKind.Reflection, em.OriginalDefinition, op, d.GetMessage()));
            }
            return list;
        }
    }

    /// <summary>A member whose collection some assignment in the body stores in the local.</summary>
    static ISymbol? MemberStoredIn(IOperation body, ILocalSymbol local)
    {
        foreach (var o in body.Descendants())
        {
            var v = o switch
            {
                IVariableDeclaratorOperation { Initializer.Value: var iv } d when SymbolEqualityComparer.Default.Equals(d.Symbol, local) => iv,
                ISimpleAssignmentOperation { Target: ILocalReferenceOperation t } a when SymbolEqualityComparer.Default.Equals(t.Local, local) => a.Value,
                _ => null,
            };
            if (v == null) continue;
            foreach (var x in v.DescendantsAndSelf())
                if (x is IFieldReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: false } && Entries.IsCollection(x.Type)) return ((IMemberReferenceOperation)x).Member.OriginalDefinition;
        }
        return null;
    }

    /// <summary>A span, memory or segment made over the collection (its writes go to the entries unseen), else null.</summary>
    static string? ViewOf(IOperation op)
    {
        var child = op;
        var p = op.Parent;
        while (p is IConversionOperation { OperatorMethod: null }) { child = p; p = p.Parent; }
        if (p is IArgumentOperation { Parent: var call }) { child = p; p = call; }
        return p switch
        {
            IInvocationOperation inv when IsView(inv.Type) && (ReferenceEquals(inv.Instance, child) || child is IArgumentOperation) => "is given out as a " + inv.Type!.Name + " over its entries (" + inv.TargetMethod.Name + ")",
            IObjectCreationOperation oc when IsView(oc.Type) && child is IArgumentOperation => "is given out as a " + oc.Type!.Name + " over its entries",
            IConversionOperation cv when IsView(cv.Type) => "is given out as a " + cv.Type!.Name + " over its entries",
            _ => null,
        };
    }

    /// <summary>Where a collection read from a member goes when it is not the collection of an entry access: handed
    /// to the program's own method, stored, returned, made a view of; null for a use whose entries are followed (an
    /// access, a library reader, a local that is always the member) or that keeps no reference (a comparison).</summary>
    static string? CollectionLeaves(IOperation op)
    {
        var child = op;
        var p = op.Parent;
        while (p is IConversionOperation { IsImplicit: true } or IConversionOperation { Conversion.IsReference: true }) { child = p; p = p.Parent; }
        switch (p)
        {
            case IArgumentOperation { Parent: IInvocationOperation inv } when inv.TargetMethod.OriginalDefinition.Locations.Any(l => l.IsInSource):
                return "is handed to " + inv.TargetMethod.Name + ", where its entries are not followed";
            case IArgumentOperation { Parent: IObjectCreationOperation oc } when oc.Constructor?.OriginalDefinition.Locations.Any(l => l.IsInSource) == true:
                return "is handed to a constructor of " + oc.Type?.Name + ", where its entries are not followed";
            case IArgumentOperation { Parent: IInvocationOperation inv } when IsView(inv.Type):
                return "is given out as a " + inv.Type!.Name + " over its entries (" + inv.TargetMethod.Name + ")";
            case IInvocationOperation inv when ReferenceEquals(inv.Instance, child) && IsView(inv.Type):
                return "is given out as a " + inv.Type!.Name + " over its entries (" + inv.TargetMethod.Name + ")";
            case ISimpleAssignmentOperation a when ReferenceEquals(a.Value, child) && Entries.Strip(a.Target) is not ILocalReferenceOperation:
                return "is stored in " + a.Target.Syntax;
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: RefKind.None } }:
            case ISimpleAssignmentOperation:
                return null; // a local: its entries are the member's when every definition is (PluginHost.LocalCollection)
            case IReturnOperation: return "is returned";
            case IAnonymousFunctionOperation or IDelegateCreationOperation: return null;
            case IFieldInitializerOperation or IPropertyInitializerOperation: return "is stored by an initializer";
            case ITupleOperation or IArrayInitializerOperation or ICollectionExpressionOperation: return "is stored in a " + p.Kind;
            case IObjectOrCollectionInitializerOperation or IMemberInitializerOperation: return "is stored in a new object";
        }
        return null;
    }
}
