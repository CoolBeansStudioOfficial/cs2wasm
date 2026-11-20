using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>An operation on the entries of a collection that a field or property holds (or a local the plugin host
/// resolved to one): <see cref="Operation"/> is where a hook runs (the assignment, the call, the element read, the
/// loop), <see cref="Key"/> the one entry it names (null: the whole collection, or an entry it does not name: an
/// append, a sort, a clear, a count).</summary>
sealed record EntrySite(IOperation Collection, IOperation Operation, IOperation? Key, AccessKind Kind, bool Hookable);

/// <summary>Which operations are entry accesses (PluginContext.Accesses with IsEntry). One function for the analysis
/// and for the code that runs entry hooks (FunctionCompiler.TryInstrumentedEntry), so the two cannot disagree on a site
/// (audit A4.5). A collection is an array or a library type that is enumerable (not a string): a collection type of
/// the program's own is not one, its state is its fields, which are accesses themselves.</summary>
static class Entries
{
    public static bool IsCollection(ITypeSymbol? t) => t switch
    {
        IArrayTypeSymbol => true,
        INamedTypeSymbol n => n.SpecialType != SpecialType.System_String && !n.OriginalDefinition.Locations.Any(l => l.IsInSource)
            && (n.SpecialType == SpecialType.System_Collections_IEnumerable || n.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable)),
        _ => false,
    };

    /// <summary>Library methods that change the collection they are called on (or, for a static or extension method,
    /// the collection passed first). Every other method of a library collection reads it.</summary>
    static readonly HashSet<string> s_mutators = ["Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange",
        "RemoveWhere", "Clear", "TryAdd", "TryRemove", "TryUpdate", "AddOrUpdate", "GetOrAdd", "Push", "PushRange", "Pop", "TryPop", "PopRange",
        "TryPopRange", "Enqueue", "EnqueueRange", "Dequeue", "TryDequeue", "Sort", "Reverse", "UnionWith", "IntersectWith", "ExceptWith",
        "SymmetricExceptWith", "Fill", "Resize", "Initialize", "AddFirst", "AddLast", "AddBefore", "AddAfter", "RemoveFirst", "RemoveLast",
        "SetCount", "Shuffle", "Set", "SetAll", "And", "Or", "Xor", "Not", "LeftShift", "RightShift", "Replace", "ReplaceRange", "Move"];

    public static IOperation Strip(IOperation o) { while (o is IConversionOperation { IsImplicit: true } c) o = c.Operand; return o; }

    /// <summary>An element of something: the thing and the one index (null with several or none).</summary>
    static bool ElementOf(IOperation o, out IOperation container, out IOperation? index)
    {
        switch (o)
        {
            case IArrayElementReferenceOperation ae: container = ae.ArrayReference; index = ae.Indices.Length == 1 ? ae.Indices[0] : null; return true;
            case IPropertyReferenceOperation { Property.IsIndexer: true, Instance: { } pi } pr: container = pi; index = pr.Arguments.Length == 1 ? pr.Arguments[0].Value : null; return true;
            case IImplicitIndexerReferenceOperation ii: container = ii.Instance; index = ii.Argument; return true;
        }
        container = null!; index = null;
        return false;
    }

    /// <summary>The collection (a member or local of collection type) that `c` is, or is an element of (nested), and
    /// the key of the access on it: the index on the collection itself, or `key` when `c` is the collection.</summary>
    static bool Base(IOperation? c, IOperation? key, out IOperation coll, out IOperation? baseKey)
    {
        coll = null!; baseKey = null;
        for (int guard = 0; c != null && guard < 64; guard++)
        {
            c = Strip(c);
            if (c is IConditionalAccessInstanceOperation)
            {
                var p = c.Parent;
                while (p != null && p is not IConditionalAccessOperation) p = p.Parent;
                if (p is not IConditionalAccessOperation ca) return false;
                c = ca.Operation;
                continue;
            }
            if (c is IFieldReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: false } && IsCollection(c.Type)) { coll = c; baseKey = key; return true; }
            if (c is ILocalReferenceOperation && IsCollection(c.Type)) { coll = c; baseKey = key; return true; }
            if (!ElementOf(c, out var inner, out var k)) return false;
            c = inner; key = k;
        }
        return false;
    }

    static IOperation? WriteTarget(IOperation op) => op switch
    {
        ISimpleAssignmentOperation a => a.Target,
        ICompoundAssignmentOperation c => c.Target,
        IIncrementOrDecrementOperation i => i.Target,
        ICoalesceAssignmentOperation k => k.Target,
        _ => null,
    };

    /// <summary>Where an element or member of a collection is written by an assignment: the collection and key.</summary>
    static bool TargetBase(IOperation target, out IOperation coll, out IOperation? key)
    {
        target = Strip(target);
        if (ElementOf(target, out var c, out var k)) return Base(c, k, out coll, out key);
        if (target is IMemberReferenceOperation { Instance: { } inst })
        {
            // (a property of the collection itself: a list's Capacity)
            if (IsCollection(Strip(inst).Type)) return Base(inst, null, out coll, out key);
            // (a field of a struct element, in place: arr[i].X = 3, arr[i].Pos.X = 3)
            var x = Strip(inst);
            while (x.Type?.IsValueType == true && x is IFieldReferenceOperation { Instance: { } fi }) x = Strip(fi);
            if (x.Type?.IsValueType == true && ElementOf(x, out var c2, out var k2)) return Base(c2, k2, out coll, out key);
        }
        coll = null!; key = null;
        return false;
    }

    /// <summary>Whether an operation is (part of) the target an assignment writes, so not a read of its own.</summary>
    static bool InWriteTarget(IOperation op)
    {
        var child = op;
        for (var p = op.Parent; p != null; child = p, p = p.Parent)
        {
            if (WriteTarget(p) is { } t) return ReferenceEquals(t, child);
            if (p is IConversionOperation { IsImplicit: true }) continue;
            // (a struct element whose field is written: the element is the field's instance)
            if (p is IFieldReferenceOperation f && ReferenceEquals(f.Instance, child) && child.Type?.IsValueType == true) continue;
            return false;
        }
        return false;
    }

    static IOperation? KeyArgument(IInvocationOperation inv)
    {
        bool set = inv.TargetMethod.ContainingType?.AllInterfaces.Any(i => i.Name == "ISet" && i.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic") == true
            || inv.TargetMethod.ContainingType is { Name: "ISet" };
        foreach (var a in inv.Arguments)
            if (a.Parameter?.Name is "key" or "index" || set && a.Parameter?.Name == "item") return a.Value;
        return null;
    }

    static bool CopiesInto(IMethodSymbol m, IArgumentOperation a) =>
        m.Name is "CopyTo" or "Copy" or "ConstrainedCopy" or "BlockCopy" or "TryCopyTo" && a.Parameter?.Name is { } n && (n.StartsWith("dest", StringComparison.Ordinal) || n == "array");

    /// <summary>The entry accesses an operation makes (an assignment, a call, an element, a member of a collection, a
    /// foreach); empty for every other operation.</summary>
    public static IEnumerable<EntrySite> Of(IOperation op)
    {
        switch (op)
        {
            case ISimpleAssignmentOperation or ICompoundAssignmentOperation or IIncrementOrDecrementOperation or ICoalesceAssignmentOperation:
                if (TargetBase(WriteTarget(op)!, out var coll, out var key))
                    yield return new EntrySite(coll, op, key, op is ISimpleAssignmentOperation ? AccessKind.Write : AccessKind.ReadWrite, true);
                yield break;
            case IArrayElementReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: true } or IImplicitIndexerReferenceOperation:
                {
                    if (InWriteTarget(op) || !ElementOf(op, out var c, out var k) || !Base(c, k, out var ec, out var ek)) yield break;
                    var (kind, hookable) = ElementUse(op);
                    yield return new EntrySite(ec, op, ek, kind, hookable);
                    yield break;
                }
            case IPropertyReferenceOperation { Property.IsIndexer: false, Instance: { } pi } pr when IsCollection(Strip(pi).Type):
                if (!InWriteTarget(op) && Base(pi, null, out var pc, out var pk)) yield return new EntrySite(pc, op, pk, AccessKind.Read, true);
                yield break;
            case IInvocationOperation inv:
                {
                    var m = inv.TargetMethod;
                    bool mut = s_mutators.Contains(m.Name) && !m.OriginalDefinition.Locations.Any(l => l.IsInSource);
                    if (inv.Instance != null && IsCollection(Strip(inv.Instance).Type) && Base(inv.Instance, KeyArgument(inv), out var ic, out var ik))
                        yield return new EntrySite(ic, op, ik, mut ? AccessKind.Write : AccessKind.Read, true);
                    // a static or extension method of the library given the collection (Array.Fill, Enumerable.Sum,
                    // CollectionExtensions.TryAdd); the program's own methods given one are not resolved (unsure)
                    if (m.OriginalDefinition.Locations.Any(l => l.IsInSource)) yield break;
                    for (int i = 0; i < inv.Arguments.Length; i++)
                    {
                        var a = inv.Arguments[i];
                        if (a.Parameter?.RefKind is RefKind.Ref or RefKind.Out || !IsCollection(Strip(a.Value).Type) || !Base(a.Value, null, out var ac, out var ak)) continue;
                        bool subject = inv.Instance == null && i == 0;
                        bool w = subject && mut || CopiesInto(m, a);
                        yield return new EntrySite(ac, op, subject && (m.IsExtensionMethod || m.IsStatic) ? KeyArgument(inv) : ak, w ? AccessKind.Write : AccessKind.Read, true);
                    }
                    yield break;
                }
            case IObjectCreationOperation oc when oc.Constructor != null && !oc.Constructor.OriginalDefinition.Locations.Any(l => l.IsInSource):
                // (a library collection made from one: new List<T>(Scores) reads it)
                foreach (var a in oc.Arguments)
                    if (IsCollection(Strip(a.Value).Type) && Base(a.Value, null, out var oc2, out var ok2)) yield return new EntrySite(oc2, op, ok2, AccessKind.Read, true);
                yield break;
            case IForEachLoopOperation fe:
                if (Base(fe.Collection, null, out var fc, out var fk)) yield return new EntrySite(fc, op, fk, AccessKind.Read, true);
                yield break;
        }
    }

    /// <summary>How an element that is not an assignment's target is used: read where it stands (a hook runs after
    /// it), or a place handed on (a `ref` or `out` argument, a `ref` local or return, a deconstruction's target, a
    /// struct element whose member is used in place) that no hook follows.</summary>
    static (AccessKind, bool) ElementUse(IOperation op)
    {
        var child = op;
        var p = op.Parent;
        while (p is IConversionOperation { IsImplicit: true }) { child = p; p = p.Parent; }
        switch (p)
        {
            case IArgumentOperation { Parameter.RefKind: RefKind.Out }: return (AccessKind.Write, false);
            case IArgumentOperation { Parameter.RefKind: RefKind.Ref }: return (AccessKind.ReadWrite, false);
            case IArgumentOperation { Parameter.RefKind: RefKind.In or RefKind.RefReadOnlyParameter }: return (AccessKind.Read, false);
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } }: return (AccessKind.ReadWrite, false);
            case IReturnOperation when op.SemanticModel?.GetEnclosingSymbol(op.Syntax.SpanStart) is IMethodSymbol { ReturnsByRef: true }: return (AccessKind.ReadWrite, false);
            case ISimpleAssignmentOperation { IsRef: true }: return (AccessKind.ReadWrite, false);
            case ITupleOperation t when t.Parent is IDeconstructionAssignmentOperation da && ReferenceEquals(da.Target, t): return (AccessKind.Write, false);
        }
        if (op.Type?.IsValueType == true && Types.IsStruct(op.Type) && p is IMemberReferenceOperation mr && ReferenceEquals(mr.Instance, child)) return (AccessKind.Read, false);
        if (op.Type?.IsValueType == true && Types.IsStruct(op.Type) && p is IInvocationOperation iv && ReferenceEquals(iv.Instance, child)) return (AccessKind.Read, false);
        return (AccessKind.Read, true);
    }
}
