using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// A reference into a collection's storage keeps that storage (section 4.1; found 2026-11-23: a span over a list's
// items, `CollectionsMarshal.AsSpan(list)`, or a `ref` to a dictionary's value, `GetValueRefOrNullRef`, held while
// the collection grew read the array the growth had freed; .NET's collector keeps the old array through the span).
// A span or a `ref` holds no count here: what keeps its array is whoever holds the array. For these calls that is
// the collection's field, which a growth stores over. So the call site holds the array itself: the object the
// returned reference points into (Rt.ObjectOf: the allocator's table says which, no search) is counted into a local
// of the calling function, let go when the function returns (a later call at the same place lets the earlier one go).
// The span then reads and writes the old array whatever the collection does, as in .NET, and a growth meanwhile finds
// two holders and copies (Rt.MoveRange moves only an array nothing else holds). Nothing changes for a program that
// makes no such call: the growth's own code is as it was.
// The calls: CollectionsMarshal's AsSpan, GetValueRefOrNullRef and GetValueRefOrAddDefault, and a method or a
// property of the program's own that returns by reference, or returns a span, and calls one of those in its body
// (one level: a wrapper of a wrapper is not seen). CS2WASM_NO_STORAGE_KEEP=1.
internal sealed partial class Driver
{
    static readonly bool s_noStorageKeep = Environment.GetEnvironmentVariable("CS2WASM_NO_STORAGE_KEEP") == "1";
    readonly Dictionary<ISymbol, bool> _exposesStorage = new(SymbolEqualityComparer.Default);

    static bool IsStorageCall(IMethodSymbol def) =>
        def.ContainingType is { Name: "CollectionsMarshal", ContainingNamespace: { Name: "InteropServices", ContainingNamespace: { Name: "Runtime", ContainingNamespace.Name: "System" } } }
        && def.Name is "AsSpan" or "GetValueRefOrNullRef" or "GetValueRefOrAddDefault";

    /// <summary>Whether a call of the method gives a reference into a collection's storage (see above).</summary>
    internal bool ExposesStorage(IMethodSymbol m)
    {
        if (s_noStorageKeep) return false;
        var def = m.OriginalDefinition;
        if (IsStorageCall(def)) return true;
        if (!(def.ReturnsByRef || def.ReturnsByRefReadonly || def.ReturnType.IsRefLikeType) || def.DeclaringSyntaxReferences.Length != 1 || IsLibrary(def)) return false;
        if (_exposesStorage.TryGetValue(def, out var known)) return known;
        _exposesStorage[def] = false;
        var r = def.DeclaringSyntaxReferences[0];
        var syntax = r.GetSyntax();
        bool found = false;
        if (syntax.ToString().Contains("CollectionsMarshal") && ModelFor(r.SyntaxTree).GetOperation(syntax) is { } body)
            found = body.Descendants().Any(o => o is IInvocationOperation inv && IsStorageCall(inv.TargetMethod.OriginalDefinition));
        return _exposesStorage[def] = found;
    }
}

sealed partial class FunctionCompiler
{
    /// <summary>The calls of this function that give a reference into storage (found before the body is compiled),
    /// and the local each keeps the storage's object in.</summary>
    readonly List<SyntaxNode> _storageSites = [];
    readonly Dictionary<SyntaxNode, int> _storageKeeps = [];

    void NoteStorageSite(IOperation op)
    {
        IMethodSymbol? m = op switch { IInvocationOperation inv => inv.TargetMethod, IPropertyReferenceOperation pr => pr.Property.GetMethod, _ => null };
        if (m != null && D.ExposesStorage(m)) _storageSites.Add(op.Syntax);
    }

    /// <summary>The locals, owned by the function's own scope: every return and every unwinding lets them go.</summary>
    void PrepareStorageKeeps()
    {
        foreach (var site in _storageSites)
        {
            if (_storageKeeps.ContainsKey(site)) continue;
            int l = E.AddLocal(ValType.I32);
            _storageKeeps[site] = l;
            AddOwned(new Slot { Local = l, Type = D.ObjectType });
        }
    }

    /// <summary>After a call that gave a reference into storage, its result on the stack (a span's address, or the
    /// reference itself): the object it points into is counted into the site's local.</summary>
    void KeepStorage(IMethodSymbol m, SyntaxNode? syntax, bool refAddress)
    {
        var rt = T.Canonical(m.ReturnType);
        int off = 0;
        bool byRef = m.ReturnsByRef || m.ReturnsByRefReadonly;
        if (byRef) { if (!refAddress) return; }
        else
        {
            if (!Types.IsStruct(rt) || T.LayoutOf(rt).Fields.FirstOrDefault(f => f.Symbol.Name == "_reference") is not { } rf) return;
            off = rf.Offset;
        }
        if (syntax == null || !_storageKeeps.TryGetValue(syntax, out int keep))
        {
            // (a call the scan before the body did not see: kept for the scope the call is in)
            keep = E.AddLocal(ValType.I32);
            AddOwned(new Slot { Local = keep, Type = D.ObjectType });
            if (syntax != null) _storageKeeps[syntax] = keep;
        }
        int a = Scratch(ValType.I32), o = Scratch(ValType.I32);
        E.Tee(a);
        if (!byRef) E.Load(Op.I32Load, off);
        E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ObjectOf", 1).Fn);
        E.Tee(o);
        E.If();
        E.Get(o); E.Call(Rt.Retain); E.Op(Op.Drop);
        E.End();
        E.Get(keep); E.Call(Rt.ReleaseOf(D.ObjectType));
        E.Get(o); E.Set(keep);
        E.Get(a);
        FreeScratch(ValType.I32, o); FreeScratch(ValType.I32, a);
    }
}
