using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Exclusive parts (docs/AUDIT.md A3.5, "The build, round 7" and "round 8"; the runtime's half is in Runtime.cs under
// "Exclusive parts"). A field whose object is its holder's alone needs no membership of its own in the holder's
// group: the list in `Children`, and that list's array in `_items`. The field's site is exclusive when, over the
// whole program:
//   1. every store into it stores a new object (`new`, a new array, a local that holds one and is used for nothing
//      that keeps it), null, or what a static readonly field holds (pinned: never a part); the new object's class has
//      no finalizer and its constructor keeps `this` in;
//   2. every read of it uses the value only as the receiver of a call, a property or an element access that keeps
//      nothing of it (the callee's `this` does not escape: Driver.ParamEscape), as an argument a callee does not
//      keep, in a comparison, or as the collection of a `foreach` the compiler turns into a loop; never stored,
//      returned, captured, handed to code the compiler cannot see;
//   3. a local takes the value only where the holder is one that stays alive for the method (this, a parameter or a
//      local that is never assigned again), and a `foreach` likewise: elsewhere the holder is held for the statement
//      (PartHold: a part read from a holder that code does not keep alive is used while its holder may go, and the
//      holder's end takes the members out of the part's slots);
//   4. its address is taken nowhere (ref, out, Interlocked) and reflection cannot reach it;
//   5. no object of the holder's class is copied (MemberwiseClone; a record's `with`): two holders of one part.
// Not with threads. CS2WASM_NO_EXCLUSIVE_PARTS=1: no site is exclusive. Each store that qualifies (1) is emitted
// through a placeholder of its own; a site with any other store left in the module, or a read that failed, is not
// exclusive, and its placeholders become the stores they would have been.
internal sealed partial class Arenas
{
    static readonly bool s_noParts = Environment.GetEnvironmentVariable("CS2WASM_NO_EXCLUSIVE_PARTS") == "1";
    static readonly bool s_partsDebug = Environment.GetEnvironmentVariable("CS2WASM_PARTS_DEBUG") == "1";
    /// <summary>CS2WASM_EXP_PARTS_SKIP=n (the tests' own: conformance tests 200 to 204 must change their output with
    /// each): condition n of the proof is not asked. 1 the stored value, 2 the reads' uses, 3 the holder kept
    /// alive, 4 addresses and reflection, 5 copies.</summary>
    internal static readonly int PartsSkip = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_EXP_PARTS_SKIP"), out var ps) ? ps : 0;

    public bool PartsOn => !s_noParts && !D.ThreadMode;
    /// <summary>Some site is exclusive (decided in DecideParts): Rt.Parts().</summary>
    public bool AnyParts { get; private set; }

    readonly Dictionary<WasmFunction, WasmFunction> _partFallback = [];
    readonly Dictionary<(Site, WasmFunction), WasmFunction> _partFns = [];
    readonly List<ITypeSymbol> _cloned = [];
    bool _partsDecided;

    /// <summary>The placeholder of a store that qualifies as a part's (condition 1), in place of the site's own
    /// link or unlink placeholder `fallback`, which it becomes when the site is not exclusive.</summary>
    public WasmFunction PartFn(Site s, WasmFunction fallback, bool link)
    {
        s.PartStores = true;
        if (_partFns.TryGetValue((s, fallback), out var f)) return f;
        f = Placeholder((link ? "arena.part store " : "arena.part unstore ") + s.Key, fallback.Type, link ? Kind.PartLink : Kind.PartUnlink, s);
        _partFallback[f] = fallback;
        return _partFns[(s, fallback)] = f;
    }

    /// <summary>(holder) → holder, and (holder) → (): the holder of a part read where code does not keep it alive,
    /// held for the statement (condition 3). A count and its release when the site is exclusive, nothing else.</summary>
    public (WasmFunction hold, WasmFunction unhold) PartHold(Site s)
    {
        if (s.Hold == null)
        {
            s.Hold = Placeholder("arena.part hold " + s.Key, new FuncType([I], [I]), Kind.PartHold, s);
            s.Unhold = Placeholder("arena.part unhold " + s.Key, new FuncType([I], []), Kind.PartUnhold, s);
        }
        return (s.Hold, s.Unhold!);
    }

    /// <summary>A read of the field failed the proof (or anything else that rules the site out).</summary>
    public void PartFail(Site s, string why)
    {
        if (s.PartBad != null) return;
        s.PartBad = why;
        if (_partsDecided && s.Exclusive) throw new CompileException("internal: exclusive part " + s.Key + " ruled out after the decision: " + why);
    }

    /// <summary>An object of this static type is copied field by field (MemberwiseClone): condition 5.</summary>
    public void PartCloned(ITypeSymbol t) => _cloned.Add(D.Types.Canonical(t));

    /// <summary>Which sites are exclusive: once, when every function of the program exists (before Resolve).</summary>
    public void DecideParts()
    {
        _partsDecided = true;
        if (!Enabled || !PartsOn) return;
        var cands = _sites.Values.Where(s => s.PartStores && s.Cyclic && !s.IsArray && s.ContainerId < 0 && s.Member != null).ToList();
        if (cands.Count == 0) return;
        foreach (var s in cands)
        {
            if (PartsSkip != 4 && (s.AddressTaken || s.RefDirty)) s.PartBad ??= "its address is taken";
            if (s.Container is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsRecord: false }) s.PartBad ??= "the holder is no plain class";
            else if (PartsSkip != 5 && _cloned.Any(c => D.Types.IsSubtype(s.Container, c) || D.Types.IsSubtype(c, s.Container))) s.PartBad ??= "the holder's class is copied";
        }
        if (PartsSkip != 4) foreach (var (s, _) in _reflectStores) s.PartBad ??= "reflection reaches it";
        // (a store of any other kind left in the module: a value that is not new, a store the compiler made itself)
        var other = new Dictionary<WasmFunction, Site>();
        foreach (var s in cands) { other[s.Link] = s; other[s.LinkFresh] = s; other[s.LinkOwned] = s; other[s.LinkIso] = s; }
        if (PartsSkip != 1)
            foreach (var f in D.Module.Functions)
                foreach (var ins in f.Body)
                    if (ins.Op == Op.Call && ins.Ref is WasmFunction w && other.TryGetValue(w, out var os) && os.PartBad == null) os.PartBad = "another store in " + f.Name;
        foreach (var s in cands)
        {
            s.Exclusive = s.PartBad == null;
            if (s_partsDebug) Console.Error.WriteLine($"parts: {s.Key} ({s.Member}): {(s.Exclusive ? "exclusive" : "no: " + s.PartBad)}");
        }
        AnyParts = cands.Any(s => s.Exclusive);
        _exclusive = null;
    }

    Dictionary<string, HashSet<int>>? _exclusive;

    /// <summary>The offsets of the exclusive fields an object of this type has (its class's and its bases').</summary>
    public HashSet<int> ExclusiveOffsets(int id)
    {
        var r = new HashSet<int>();
        if (!AnyParts || D.DisplayById.ContainsKey(id)) return r;
        if (_exclusive == null)
        {
            _exclusive = [];
            foreach (var s in _sites.Values)
            {
                if (!s.Exclusive) continue;
                var k = Types.Key(D.Types.Canonical(s.Container));
                if (!_exclusive.TryGetValue(k, out var l)) _exclusive[k] = l = [];
                l.Add(s.Offset);
            }
        }
        for (var b = D.TypeById[id]; b != null; b = (b as INamedTypeSymbol)?.BaseType is { } bt ? D.Types.Canonical(bt) : null)
        {
            if (b is IArrayTypeSymbol) break;
            if (_exclusive.TryGetValue(Types.Key(D.Types.Canonical(b)), out var l)) r.UnionWith(l);
        }
        return r;
    }
}

sealed partial class FunctionCompiler
{
    readonly Dictionary<ISymbol, bool> _partCandidates = new(SymbolEqualityComparer.Default);

    /// <summary>A field that can be exclusive at all, by its declaration: an instance field of a class that is no
    /// record, of a class or array type. (What its stores and reads say is asked at each of them.)</summary>
    bool PartCandidate(IFieldSymbol f)
    {
        if (!D.Arenas.PartsOn) return false;
        f = f.OriginalDefinition;
        if (_partCandidates.TryGetValue(f, out var r)) return r;
        r = !f.IsStatic && !f.IsVolatile && f.RefKind == RefKind.None && f.ContainingType is { TypeKind: TypeKind.Class, IsRecord: false, IsAnonymousType: false }
            && f.Type is IArrayTypeSymbol or INamedTypeSymbol { TypeKind: TypeKind.Class, SpecialType: SpecialType.None }
            && !f.GetAttributes().Any(a => a.AttributeClass?.Name is "FieldOffsetAttribute" or "ThreadStaticAttribute");
        return _partCandidates[f] = r;
    }

    /// <summary>Condition 1: the value a store puts into a candidate field is one only this store knows.</summary>
    bool PartValueOk(IFieldSymbol f, IOperation value)
    {
        if (!PartCandidate(f)) return false;
        if (Arenas.PartsSkip == 1) return true;
        while (value is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) value = c.Operand;
        switch (value)
        {
            case IObjectCreationOperation oc:
                return PartNewOk(oc);
            case IArrayCreationOperation:
                return true;
            case IDefaultValueOperation:
                return true;
            case ILiteralOperation { ConstantValue: { HasValue: true, Value: null } }:
                return true;
            case IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } }:
                return true;
            case ILocalReferenceOperation lr:
                return PartFreshLocal(lr);
            default:
                return false;
        }
    }

    bool PartNewOk(IObjectCreationOperation oc)
    {
        if (oc.Constructor == null || TypeOf(oc) is not INamedTypeSymbol { TypeKind: TypeKind.Class } nt) return false;
        for (var t = nt; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            if (t.GetMembers("Finalize").OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Destructor)) return false;
        return D.CtorsKeepThis(nt) || D.CtorKeepsThis(oc.Constructor);
    }

    /// <summary>A local declared with a new object or array, never assigned again, whose every other use keeps
    /// nothing of it (`T[] newItems = new T[n]; Array.Copy(_items, newItems, _size); _items = newItems;`).</summary>
    bool PartFreshLocal(ILocalReferenceOperation store)
    {
        var l = store.Local;
        if (l.IsRef || IsCaptured(l) || WrittenLocals().Contains(l)) return false;
        IOperation root = store;
        while (root.Parent != null) root = root.Parent;
        bool declared = false;
        foreach (var op in root.DescendantsAndSelf())
        {
            if (op is IVariableDeclaratorOperation vd && SymbolEqualityComparer.Default.Equals(vd.Symbol, l))
            {
                var init = vd.Initializer?.Value;
                while (init is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) init = c.Operand;
                if (!(init is IArrayCreationOperation || init is IObjectCreationOperation ioc && PartNewOk(ioc))) return false;
                declared = true;
            }
            if (op is not ILocalReferenceOperation lr || !SymbolEqualityComparer.Default.Equals(lr.Local, l) || lr == store) continue;
            // (another store of it into a field: two holders)
            if (PartUse(lr, false) != null) return false;
        }
        return declared;
    }

    /// <summary>Conditions 2 and 3: what a use of a part's value (v: the field's read, or a local that took it)
    /// lets it do; null when nothing of it is kept, else why not. `alias`: a local may take it.</summary>
    string? PartUse(IOperation v, bool alias)
    {
        var p = v.Parent;
        switch (p)
        {
            case IParenthesizedOperation:
                return PartUse(p, alias);
            case IConversionOperation conv when conv.OperatorMethod == null && (conv.Conversion.IsIdentity || conv.Conversion.IsReference && conv.Conversion.IsImplicit):
                return PartUse(conv, alias);
            case IArrayElementReferenceOperation ae when ae.ArrayReference == v:
                return null;
            case IPropertyReferenceOperation { Property.Name: "Length" or "LongLength" } alen when alen.Instance == v && v.Type is IArrayTypeSymbol:
                return null;
            case IForEachLoopOperation fe when fe.Collection == v:
                // (the loops the compiler writes itself, over an array or a List<T>: no enumerator takes the object)
                {
                    var ct = v is IConversionOperation cc ? cc.Operand.Type : v.Type;
                    if (ct is IArrayTypeSymbol { Rank: 1 }) return null;
                    if (ct is INamedTypeSymbol { Name: "List", Arity: 1 } ln && ln.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic") return null;
                    return "a foreach by its enumerator";
                }
            case IBinaryOperation { OperatorMethod: null }:
            case IExpressionStatementOperation:
                return null;
            case IIsPatternOperation { Pattern: IConstantPatternOperation or INegatedPatternOperation { Pattern: IConstantPatternOperation } } ip when ip.Value == v:
                return null;
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: { RefKind: RefKind.None } il } }:
                {
                    if (!alias) return "a local takes it";
                    if (IsCaptured(il)) return "a captured local takes it";
                    IOperation root = v;
                    while (root.Parent != null) root = root.Parent;
                    foreach (var op in root.DescendantsAndSelf())
                    {
                        if (op is not ILocalReferenceOperation lr || !SymbolEqualityComparer.Default.Equals(lr.Local, il)) continue;
                        if (lr.Parent is ISimpleAssignmentOperation sa && sa.Target == lr) continue;
                        if (PartUse(lr, false) is { } why) return why;
                    }
                    return null;
                }
            case IArgumentOperation { Parent: IInvocationOperation { TargetMethod: { IsStatic: true, ContainingType: { Name: "Array", ContainingNamespace.Name: "System" } } am } } aa
                when aa.Parameter is { RefKind: RefKind.None } && am.Name is "Copy" or "Clear" or "IndexOf" or "LastIndexOf" or "Reverse" or "Sort" or "BinarySearch" or "Fill" or "ConstrainedCopy":
                // (the library's array functions read and write elements, and keep no array)
                return null;
            case IArgumentOperation { Parent: IObjectCreationOperation { Type: INamedTypeSymbol { Name: "Span" or "ReadOnlySpan", ContainingNamespace.Name: "System" } } } sa2 when sa2.Parameter is { RefKind: RefKind.None }:
                // (a span is a reference into the array for the length of a statement or a call, no reference to it)
                return null;
            case IInvocationOperation inv when inv.Instance == v:
            case IPropertyReferenceOperation pr when pr.Instance == v:
            case IArgumentOperation arg when arg.Value == v:
                return D.UseEscape(v, null, alias) == Driver.Esc.No ? null : "it escapes at `" + Short(p.Syntax.ToString()) + "`";
            default:
                return "used in `" + Short(p?.Syntax.ToString() ?? "?") + "` (" + p?.Kind + ")";
        }
        static string Short(string s) => s.Length > 60 ? s[..60] : s;
    }

    /// <summary>A read of a candidate field (f: the field's read, or the read of the auto-property that owns it):
    /// conditions 2 and 3, noted on the site when they fail.</summary>
    void NotePartRead(IOperation read, IOperation instance, Arenas.Site site)
    {
        if (site.PartBad != null) return;
        bool stable = IsStableBase(instance);
        string? why = Arenas.PartsSkip == 2 ? null : PartUse(read, stable || Arenas.PartsSkip == 3);
        if (why == null && !stable && Arenas.PartsSkip != 3)
        {
            // (a loop holds the part past the statement the holder is held for)
            IOperation x = read;
            while (x.Parent is IConversionOperation or IParenthesizedOperation) x = x.Parent;
            if (x.Parent is IForEachLoopOperation) why = "a foreach over the part of a holder that may go";
        }
        if (why != null) D.Arenas.PartFail(site, why + " in " + M.ToDisplayString());
    }

    /// <summary>The read of a candidate field under way, whose holder is not one that stays alive for the method:
    /// EmitFieldSlotBase holds the holder for the statement (condition 3).</summary>
    (IOperation op, Arenas.Site site)? _partHold;

    void EmitPartHold(Own o, ITypeSymbol holderType, Arenas.Site site)
    {
        // (an owned holder is the statement's already, ToTransient; a stable one is the method's)
        if (o != Own.Unstable || Arenas.PartsSkip == 3) return;
        var (hold, unhold) = D.Arenas.PartHold(site);
        E.Call(hold);
        int l = E.AddLocal(ValType.I32);
        RegisterTemp(new Slot { Local = l, Type = holderType, ReleaseMarker = unhold, OwnLocal = true });
        E.Tee(l);
    }
}
