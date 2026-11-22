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
//
// Which calls (complete since 2026-11-22; before, a wrapper one level deep that returned the span):
// - CollectionsMarshal's AsSpan, GetValueRefOrNullRef and GetValueRefOrAddDefault;
// - any method, property, indexer, operator, local function or constructor, the program's or the library's, through
//   which such a reference can come out, and whose body makes one of these calls or a call that is itself one of
//   these, through any number of frames (a fixed point over the calls: ExposesStorage). A reference can come out of
//   a call four ways, and each is kept where the call is made: the value returned (a `ref`, a span, or a ref struct
//   with spans or `ref` fields anywhere inside it); an `out` or `ref` argument of such a type (the library's own
//   `TryGetSpan(out ReadOnlySpan<T>)`, which LINQ uses over a list while it calls the program's predicate); the ref
//   struct a constructor fills; the ref struct an instance method is called on;
// - a virtual or interface method when it, or any override or implementation in the program, is one of those; a
//   delegate that returns or gives out such a type (its target is not known: always kept).
// What flows where inside a body is not followed: a body that makes such a call anywhere counts for all its ways
// out. That keeps an array a little longer than .NET needs it (to the calling function's return), never shorter.
// Not covered: a function pointer. CS2WASM_NO_STORAGE_KEEP=1: none; CS2WASM_STORAGE_KEEP_SHALLOW=1: the calls
// themselves and their direct wrappers by return value only (as it was, and what test 222's deeper cases fail on).
internal sealed partial class Driver
{
    static readonly bool s_noStorageKeep = Environment.GetEnvironmentVariable("CS2WASM_NO_STORAGE_KEEP") == "1";
    static readonly bool s_shallowStorageKeep = Environment.GetEnvironmentVariable("CS2WASM_STORAGE_KEEP_SHALLOW") == "1";
    readonly Dictionary<ISymbol, bool> _exposesStorage = new(SymbolEqualityComparer.Default);
    readonly HashSet<ISymbol> _exposing = new(SymbolEqualityComparer.Default);
    Dictionary<ISymbol, List<IMethodSymbol>>? _storageOverriders;

    static bool IsStorageCall(IMethodSymbol def) =>
        def.ContainingType is { Name: "CollectionsMarshal", ContainingNamespace: { Name: "InteropServices", ContainingNamespace: { Name: "Runtime", ContainingNamespace.Name: "System" } } }
        && def.Name is "AsSpan" or "GetValueRefOrNullRef" or "GetValueRefOrAddDefault";

    /// <summary>Whether a reference into storage can come out of a call of the method at all: by its result, an
    /// `out`/`ref` argument that is a ref struct, the ref struct it constructs, or the ref struct it is called on.</summary>
    internal static bool StorageWayOut(IMethodSymbol def) =>
        def.ReturnsByRef || def.ReturnsByRefReadonly || def.ReturnType.IsRefLikeType
        || def.ContainingType is { IsRefLikeType: true } ct && !def.IsStatic && (def.MethodKind == MethodKind.Constructor || !def.IsReadOnly && !ct.IsReadOnly)
        || def.Parameters.Any(p => p.RefKind is RefKind.Out or RefKind.Ref && p.Type.IsRefLikeType);

    /// <summary>Whether a call of the method can give a reference into a collection's storage (see above).</summary>
    internal bool ExposesStorage(IMethodSymbol m)
    {
        if (s_noStorageKeep) return false;
        var def = (m.ReducedFrom ?? m).OriginalDefinition;
        if (IsStorageCall(def)) return true;
        if (!StorageWayOut(def)) return false;
        if (s_shallowStorageKeep)
        {
            if (!(def.ReturnsByRef || def.ReturnsByRefReadonly || def.ReturnType.IsRefLikeType) || def.DeclaringSyntaxReferences.Length != 1 || IsLibrary(def)) return false;
            if (_exposesStorage.TryGetValue(def, out var was)) return was;
            _exposesStorage[def] = false;
            var r = def.DeclaringSyntaxReferences[0];
            var syntax = r.GetSyntax();
            bool direct = false;
            if (syntax.ToString().Contains("CollectionsMarshal") && ModelFor(r.SyntaxTree).GetOperation(syntax) is { } b)
                direct = b.Descendants().Any(o => o is IInvocationOperation inv && IsStorageCall(inv.TargetMethod.OriginalDefinition));
            return _exposesStorage[def] = direct;
        }
        return Exposes(def, out _);
    }

    /// <summary>The fixed point, depth first. A method met again while it is being decided counts as "no" for the
    /// moment; an answer "no" that leaned on that is not remembered (unless it is the outermost question's own, which
    /// has then seen everything it can reach), so it is asked again when the method in question is settled.</summary>
    bool Exposes(IMethodSymbol def, out bool leaned)
    {
        leaned = false;
        if (_exposesStorage.TryGetValue(def, out var known)) return known;
        if (!_exposing.Add(def)) { leaned = true; return false; }
        bool found = false;
        // (a delegate's Invoke: the target is not known)
        if (def.ContainingType?.TypeKind == TypeKind.Delegate) found = def.Name == "Invoke";
        foreach (var r in def.DeclaringSyntaxReferences)
        {
            if (found) break;
            if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not { } body) continue;
            foreach (var o in body.Descendants())
            {
                IMethodSymbol? callee = o switch
                {
                    IInvocationOperation inv => inv.TargetMethod,
                    IPropertyReferenceOperation pr => pr.Property.GetMethod,
                    IObjectCreationOperation oc => oc.Constructor,
                    IBinaryOperation bo => bo.OperatorMethod,
                    IUnaryOperation uo => uo.OperatorMethod,
                    IConversionOperation co => co.OperatorMethod,
                    _ => null,
                };
                if (callee == null) continue;
                var cd = (callee.ReducedFrom ?? callee).OriginalDefinition;
                if (IsStorageCall(cd)) { found = true; break; }
                if (!StorageWayOut(cd)) continue;
                if (Exposes(cd, out bool l)) { found = true; break; }
                leaned |= l;
            }
        }
        // (a method that is called through its slot: any override or implementation the program has)
        if (!found && (def.IsVirtual || def.IsAbstract || def.IsOverride || def.ContainingType?.TypeKind == TypeKind.Interface))
        {
            _storageOverriders ??= StorageOverriders();
            if (_storageOverriders.TryGetValue(def, out var impls))
                foreach (var impl in impls)
                {
                    if (Exposes(impl, out bool l)) { found = true; break; }
                    leaned |= l;
                }
        }
        _exposing.Remove(def);
        if (found || !leaned || _exposing.Count == 0) { _exposesStorage[def] = found; leaned = false; }
        return found;
    }

    /// <summary>For each virtual or interface method through which a reference can come out: the program's methods
    /// that override or implement it (the program's own types: the library's are found through their bodies).</summary>
    Dictionary<ISymbol, List<IMethodSymbol>> StorageOverriders()
    {
        var map = new Dictionary<ISymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
        void Add(IMethodSymbol slot, IMethodSymbol impl)
        {
            var key = slot.OriginalDefinition;
            if (!map.TryGetValue(key, out var l)) map[key] = l = [];
            l.Add(impl.OriginalDefinition);
        }
        var work = new Stack<INamespaceOrTypeSymbol>();
        work.Push(Main.Assembly.GlobalNamespace);
        while (work.Count > 0)
        {
            var s = work.Pop();
            foreach (var member in s.GetMembers())
            {
                if (member is INamespaceSymbol ns) { work.Push(ns); continue; }
                if (member is INamedTypeSymbol nested) { work.Push(nested); continue; }
                if (member is not IMethodSymbol mem || !StorageWayOut(mem)) continue;
                for (var o = mem.OverriddenMethod; o != null; o = o.OverriddenMethod) Add(o, mem);
                foreach (var e in mem.ExplicitInterfaceImplementations) Add(e, mem);
            }
            if (s is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } t)
                foreach (var i in t.AllInterfaces)
                    foreach (var im in i.GetMembers())
                        if (im is IMethodSymbol ims && StorageWayOut(ims) && t.FindImplementationForInterfaceMember(ims) is IMethodSymbol impl) Add(ims, impl);
        }
        return map;
    }
}

sealed partial class FunctionCompiler
{
    /// <summary>The calls of this function that give a reference into storage (found before the body is compiled),
    /// and the locals each keeps the storage's objects in: one for each reference that can come out of the call.</summary>
    readonly List<IOperation> _storageSites = [];
    readonly Dictionary<SyntaxNode, List<int>> _storageKeeps = [];

    static IMethodSymbol? StorageCallee(IOperation op) => op switch
    {
        IInvocationOperation inv => inv.TargetMethod,
        IPropertyReferenceOperation pr => pr.Property.GetMethod,
        IObjectCreationOperation oc => oc.Constructor,
        _ => null,
    };

    void NoteStorageSite(IOperation op)
    {
        if (StorageCallee(op) is { } m && D.ExposesStorage(m)) _storageSites.Add(op);
    }

    /// <summary>Where the references are in a ref struct (a span's `_reference`, a `ref` field, the same inside the
    /// ref structs it holds).</summary>
    List<int> RefOffsets(ITypeSymbol type)
    {
        var r = new List<int>();
        void Walk(ITypeSymbol s, int at, int depth)
        {
            if (depth > 8 || !Types.IsStruct(s) || !s.IsRefLikeType) return;
            foreach (var f in T.LayoutOf(s).Fields)
            {
                if (f.Symbol is IFieldSymbol { RefKind: not RefKind.None } || f.Symbol.Name == "_reference") r.Add(at + f.Offset);
                else if (f.Type.IsRefLikeType) Walk(T.Canonical(f.Type), at + f.Offset, depth + 1);
            }
        }
        Walk(T.Canonical(type), 0, 0);
        return r;
    }

    /// <summary>How many references can come out of a call of the (substituted) method: by its result, by the ref
    /// struct it is called on or constructs, by each `out`/`ref` argument that is a ref struct. The same order where
    /// they are kept.</summary>
    (int ret, int self, int[] args) StoragePlan(IMethodSymbol m)
    {
        int ret = m.ReturnsByRef || m.ReturnsByRefReadonly ? 1 : m.ReturnType.IsRefLikeType ? RefOffsets(m.ReturnType).Count : 0;
        int self = !m.IsStatic && m.ContainingType is { IsRefLikeType: true } ct && (m.MethodKind == MethodKind.Constructor || !m.IsReadOnly && !ct.IsReadOnly) ? RefOffsets(ct).Count : 0;
        var args = new int[m.Parameters.Length];
        for (int i = 0; i < args.Length; i++)
            if (m.Parameters[i].RefKind is RefKind.Out or RefKind.Ref && m.Parameters[i].Type.IsRefLikeType) args[i] = RefOffsets(m.Parameters[i].Type).Count;
        return (ret, self, args);
    }

    /// <summary>The locals, owned by the function's own scope: every return and every unwinding lets them go.</summary>
    void PrepareStorageKeeps()
    {
        foreach (var site in _storageSites)
        {
            if (_storageKeeps.ContainsKey(site.Syntax)) continue;
            var (ret, self, args) = StoragePlan(SubstMethod(StorageCallee(site)!));
            var locals = new List<int>();
            for (int n = ret + self + args.Sum(); n > 0; n--)
            {
                int l = E.AddLocal(ValType.I32);
                locals.Add(l);
                AddOwned(new Slot { Local = l, Type = D.ObjectType });
            }
            _storageKeeps[site.Syntax] = locals;
        }
    }

    int StorageKeepLocal(SyntaxNode? syntax, int k)
    {
        if (syntax != null && _storageKeeps.TryGetValue(syntax, out var l) && k < l.Count) return l[k];
        // (a call the scan before the body did not see: kept for the scope the call is in)
        int keep = E.AddLocal(ValType.I32);
        AddOwned(new Slot { Local = keep, Type = D.ObjectType });
        return keep;
    }

    /// <summary>The object the reference in local `r` points into is counted into the keep, in place of what the
    /// keep held.</summary>
    void KeepObjectOf(int r, int keep)
    {
        int o = Scratch(ValType.I32);
        E.Get(r);
        E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ObjectOf", 1).Fn);
        E.Tee(o);
        E.If();
        E.Get(o); E.Call(Rt.Retain); E.Op(Op.Drop);
        E.End();
        E.Get(keep); E.Call(Rt.ReleaseOf(D.ObjectType));
        E.Get(o); E.Set(keep);
        FreeScratch(ValType.I32, o);
    }

    /// <summary>The address of a ref struct on the stack (taken off it): each reference in it is kept.</summary>
    void KeepRefsOfStruct(ITypeSymbol type, SyntaxNode? syntax, int k)
    {
        var offs = RefOffsets(type);
        int a = Scratch(ValType.I32), r = Scratch(ValType.I32);
        E.Set(a);
        foreach (int off in offs)
        {
            E.Get(a); E.Load(Op.I32Load, off); E.Set(r);
            KeepObjectOf(r, StorageKeepLocal(syntax, k++));
        }
        FreeScratch(ValType.I32, r); FreeScratch(ValType.I32, a);
    }

    static bool StorageLvalue(IOperation? op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        return op switch
        {
            ILocalReferenceOperation or IParameterReferenceOperation or IInstanceReferenceOperation => true,
            IDeclarationExpressionOperation de => StorageLvalue(de.Expression),
            IFieldReferenceOperation { Field.IsStatic: false, Field.RefKind: RefKind.None } f => f.Instance != null && f.Instance.Type is { IsValueType: true } && StorageLvalue(f.Instance),
            _ => false,
        };
    }

    /// <summary>After a call that gives references into storage. Its result, if it has one, is on the stack and stays
    /// there (a ref struct's address, or with refAddress the reference itself): the objects the references that came
    /// out point into are counted into the site's locals: the result's, then those of the ref struct the method was
    /// called on, then those of each `out`/`ref` argument that is a ref struct.</summary>
    void KeepStorage(IMethodSymbol m, SyntaxNode? syntax, bool refAddress, IOperation? instance = null, IReadOnlyList<IOperation>? args = null)
    {
        var (ret, self, argRefs) = StoragePlan(m);
        int k = 0;
        if (m.ReturnsByRef || m.ReturnsByRefReadonly)
        {
            if (refAddress)
            {
                int a = Scratch(ValType.I32);
                E.Tee(a);
                KeepObjectOf(a, StorageKeepLocal(syntax, 0));
                FreeScratch(ValType.I32, a);
            }
        }
        else if (ret > 0)
        {
            int a = Scratch(ValType.I32);
            E.Tee(a); E.Get(a);
            KeepRefsOfStruct(m.ReturnType, syntax, 0);
            FreeScratch(ValType.I32, a);
        }
        k += ret;
        if (self > 0 && m.MethodKind != MethodKind.Constructor && instance != null && StorageLvalue(instance))
        {
            EmitAddress(instance);
            KeepRefsOfStruct(m.ContainingType, syntax, k);
        }
        k += self;
        for (int i = 0; i < argRefs.Length; i++)
        {
            if (argRefs[i] == 0) continue;
            if (args != null && args.FirstOrDefault(x => x is IArgumentOperation ao && ao.Parameter?.Ordinal == i) is IArgumentOperation { Value: var v } && v is not IDiscardOperation && StorageLvalue(v))
            {
                EmitAddress(v);
                KeepRefsOfStruct(m.Parameters[i].Type, syntax, k);
            }
            k += argRefs[i];
        }
    }

    /// <summary>After a ref struct's constructor that gives references into storage: those of the struct it filled
    /// (pushDst: its address), and of its `out`/`ref` arguments.</summary>
    void KeepStorageOfNew(IMethodSymbol ctor, SyntaxNode? syntax, Action pushDst, IReadOnlyList<IOperation> args)
    {
        var (ret, self, _) = StoragePlan(ctor);
        if (self > 0) { pushDst(); KeepRefsOfStruct(ctor.ContainingType, syntax, ret); }
        KeepStorage(ctor, syntax, false, null, args);
    }
}
