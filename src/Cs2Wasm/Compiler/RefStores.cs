using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Stores by address in the type graph (section 4.3). A store through a `ref T` (a span's element, MemoryMarshal's
// reference, Unsafe.Add, a ref local or parameter, Interlocked's location) names no site: the runtime finds the object
// the slot is in (Arenas.LinkAt). The graph must still know what such a store can put where, or a cycle closed only by
// it is in no group and is never freed. A ref T points at a slot of type T (refs do not convert):
// - into an array's element: from `ref a[i]` (the site is then address-taken and holds its element type), or from a
//   span or GetArrayDataReference, with no element's address taken at all; so a value stored through a ref T may be in
//   any array whose element type is T, and every such array type's site holds it;
// - into a class's field: only from that field's address, taken by name (Expressions.NoteAddressTaken gives the site
//   its declared type, which covers every value of it); a readonly one is then no field filled at construction alone,
//   since what is stored through the ref comes later;
// - Unsafe.As<TFrom, TTo>(ref) makes a ref of another type of the same slot: the two types' slots are one (a struct's
//   reference slots for a struct); one made from a ref to something that is no reference (raw bytes, a number) can be
//   any slot: a value stored through such a ref may be in any slot that can hold it.
// A store through a pointer is one of the last kind.
internal sealed partial class Arenas
{
    static readonly bool s_noRefStores = Environment.GetEnvironmentVariable("CS2WASM_NO_REF_STORES") == "1";

    /// <summary>Per slot type (its key): the static types of the values stored through refs of that type.</summary>
    readonly Dictionary<string, (ITypeSymbol slot, Dictionary<string, ITypeSymbol> values)> _refStores = [];
    /// <summary>Unsafe.As&lt;TFrom, TTo&gt;: a store through a ref of TTo can land in a slot of TFrom (by type key:
    /// TFrom's to the TTo it was seen as).</summary>
    readonly Dictionary<string, HashSet<string>> _refAs = [];
    /// <summary>Slot types a ref to raw memory was reinterpreted as: stores through them can be in any slot.</summary>
    readonly HashSet<string> _wildSlots = [];
    /// <summary>Where raw memory was seen as references (the method, the slot types), decided before the graph.</summary>
    readonly List<(IMethodSymbol at, List<ITypeSymbol> slots)> _rawAs = [];
    /// <summary>Values stored through pointers: into any slot; the runtime's array code's only into arrays' elements.</summary>
    readonly Dictionary<string, ITypeSymbol> _wildValues = [], _wildArrayValues = [];

    /// <summary>A store of a reference through an address the code does not know the object of.</summary>
    public void RefStore(ITypeSymbol slotType, ITypeSymbol? valueType, bool viaPointer = false)
    {
        if (!Types.IsRef(slotType) || Types.IsImmortalType(slotType)) return;
        var st = D.Types.Canonical(slotType);
        var vt = valueType != null && valueType.TypeKind != TypeKind.Error && Types.IsRef(valueType) ? D.Types.Canonical(valueType) : st;
        if (viaPointer)
        {
            if (s_arenaDebug && !_wildValues.ContainsKey(Types.Key(vt))) Console.Error.WriteLine($"by address: a store of {vt} through a pointer in {D.Compiling?.Sym.ToDisplayString()}");
            // (Cs2WasmArrays, the runtime's Array.SetValue and Array.Copy across element types: an element of the array it
            // is given, of an object it checked its element type takes)
            // (TypeInfo.Set: a QCall handle's slot, one whose address was made into a handle: a field's address taken by
            // name, which holds its declared type, or an array's element. Meta.CopyBack: a reflective call's by-ref
            // argument written back into the invoker's slot for it, the same kinds of place)
            if (D.Compiling?.Sym.ContainingType is { Name: "Cs2WasmArrays" or "TypeInfo" or "Meta" } rct && rct.ContainingNamespace?.ToDisplayString() == "Cs2Wasm.Runtime")
                _wildArrayValues.TryAdd(Types.Key(vt), vt);
            else _wildValues.TryAdd(Types.Key(vt), vt);
            return;
        }
        var k = Types.Key(st);
        if (!_refStores.TryGetValue(k, out var e)) _refStores[k] = e = (st, []);
        e.values.TryAdd(Types.Key(vt), vt);
    }

    /// <summary>The reference slot types a ref of this type names: itself, or a struct's reference slots.</summary>
    List<ITypeSymbol>? SlotTypesOf(ITypeSymbol t)
    {
        t = D.Types.Canonical(t);
        if (Types.IsRef(t)) return [t];
        if (Types.IsStruct(t) && D.Types.LayoutOf(t).HasRefs) return RefLeaves(t).Select(x => D.Types.Canonical(x.type)).ToList();
        return null; // (no reference in it: raw memory, a number)
    }

    /// <summary>Unsafe.As&lt;TFrom, TTo&gt;(ref TFrom): a ref of TTo to a slot of TFrom (or to a struct's reference
    /// slots). (Interlocked's and Volatile's own are not recorded: what they store through it their callers say, under
    /// the slot's own type: Calls.ArenaRefWrite.)</summary>
    public void RefAlias(ITypeSymbol from, ITypeSymbol to)
    {
        if (from.TypeKind == TypeKind.TypeParameter || to.TypeKind == TypeKind.TypeParameter) return;
        var a = SlotTypesOf(from);
        var b = SlotTypesOf(to);
        // (references seen as raw memory: what is written through that is no reference the graph follows, but the
        // runtime's own store, which says where it stores, Rt.LinkAt)
        if (b == null) return;
        // (raw memory seen as references: a store through those can land in any slot)
        if (a == null)
        {
            // (MemoryMarshal.GetArrayDataReference<T>(T[]): the array's own elements, T[]'s slots, as any ref T's are)
            if (D.Compiling?.Sym is { Name: "GetArrayDataReference", ContainingType.Name: "MemoryMarshal", TypeArguments.Length: 1, Parameters: [{ Type: IArrayTypeSymbol pat }] } gm
                && SymbolEqualityComparer.Default.Equals(D.Types.Canonical(pat.ElementType), D.Types.Canonical(to))) return;
            if (D.Compiling?.Sym is { } at) _rawAs.Add((at.OriginalDefinition, b));
            else foreach (var t in b) _wildSlots.Add(Types.Key(t));
            return;
        }
        foreach (var x in a)
        {
            if (!_refAs.TryGetValue(Types.Key(x), out var set)) _refAs[Types.Key(x)] = set = [];
            foreach (var y in b) set.Add(Types.Key(y));
        }
    }

    /// <summary>Every Unsafe.As&lt;TFrom, TTo&gt;(ref) in the method from memory with no reference to one with references
    /// is used as a value: read, not assigned through, not passed by reference, not returned or held as a ref.</summary>
    bool RawAsOnlyRead(IMethodSymbol m)
    {
        var refs = m.DeclaringSyntaxReferences;
        if (refs.Length == 0) return false;
        foreach (var sr in refs)
        {
            if (D.ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) return false;
            foreach (var op in body.DescendantsAndSelf())
            {
                if (op is not IInvocationOperation { TargetMethod: { Name: "As", ContainingType.Name: "Unsafe", TypeArguments.Length: 2 } am } inv) continue;
                if (am.Parameters[0].RefKind == RefKind.None) continue;
                IOperation x = inv;
                while (x.Parent is IParenthesizedOperation) x = x.Parent;
                if (inv.Syntax.Parent is RefExpressionSyntax || inv.Syntax.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 }) return false;
                bool read = x.Parent switch
                {
                    IAssignmentOperation a when ReferenceEquals(a.Target, x) => false,
                    ISimpleAssignmentOperation { IsRef: true } => false,
                    IIncrementOrDecrementOperation or IAddressOfOperation => false,
                    IArgumentOperation a => a.Parameter?.RefKind == RefKind.None,
                    IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => false,
                    IConditionalOperation { IsRef: true } => false,
                    IReturnOperation => !m.ReturnsByRef && !m.ReturnsByRefReadonly,
                    IInvocationOperation i when ReferenceEquals(i.Instance, x) => false,
                    IFieldReferenceOperation f when ReferenceEquals(f.Instance, x) => false,
                    _ => true,
                };
                if (!read) return false;
            }
        }
        return true;
    }

    /// <summary>Before the graph: the values stores by address can put in the sites they can land in.</summary>
    void AddRefStores()
    {
        if (s_noRefStores || _refStores.Count == 0 && _wildValues.Count == 0 && _wildArrayValues.Count == 0) return;
        // (raw memory seen as references in a method whose every such reinterpretation is read as a value, never stored
        // through nor given on as a ref: no store can be made through it)
        var readOnlyAs = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        foreach (var (at, slots) in _rawAs)
        {
            if (!readOnlyAs.TryGetValue(at, out var ro)) readOnlyAs[at] = ro = RawAsOnlyRead(at);
            if (s_arenaDebug) Console.Error.WriteLine($"by address: raw memory seen as {string.Join(", ", slots)} in {at.ToDisplayString()}{(ro ? ": only read" : "")}");
            if (!ro) foreach (var t in slots) _wildSlots.Add(Types.Key(t));
        }
        // the values that can land in a slot of each type: those stored through refs of the type, and through refs
        // it was seen as by Unsafe.As (and what those were seen as); and those that can land anywhere
        var wild = new Dictionary<string, ITypeSymbol>(_wildValues);
        foreach (var (k, (slot, values)) in _refStores)
            if (_wildSlots.Contains(k)) foreach (var (vk, v) in values) wild.TryAdd(vk, v);
        var into = new Dictionary<string, Dictionary<string, ITypeSymbol>?>();
        Dictionary<string, ITypeSymbol>? ValuesInto(string k)
        {
            if (into.TryGetValue(k, out var known)) return known;
            into[k] = null; // (a cycle of reinterpretations: what the others give)
            Dictionary<string, ITypeSymbol>? r = _refStores.TryGetValue(k, out var e) ? new(e.values) : null;
            if (_refAs.TryGetValue(k, out var seenAs))
                foreach (var y in seenAs)
                    if (ValuesInto(y) is { } more) { r ??= []; foreach (var (vk, v) in more) r.TryAdd(vk, v); }
            return into[k] = r;
        }
        Dictionary<string, ITypeSymbol>? ValuesFor(ITypeSymbol slot) => ValuesInto(Types.Key(slot));
        if (s_arenaDebug)
            Console.Error.WriteLine($"by address: anywhere: through pointers {string.Join(", ", _wildValues.Keys)}; raw memory seen as {string.Join(", ", _wildSlots)}");
        // (what a store of a value of static type v through a pointer can leave in a slot of type x: v's objects that
        // are x's; all of x's when v is wider, for the store was of one that fits the slot)
        ITypeSymbol? Fit(ITypeSymbol v, ITypeSymbol x) => D.Types.IsSubtype(v, x) ? v : D.Types.IsSubtype(x, v) ? x : null;
        var wildArrays = new Dictionary<string, ITypeSymbol>(wild);
        foreach (var (k, v) in _wildArrayValues) wildArrays.TryAdd(k, v);
        int arrays = 0, fields = 0, wilds = 0;
        // every array type of reference elements the program makes (one no code stores an element of has no site yet)
        foreach (var t in D.ConstructedTypes().ToList())
        {
            if (t is not IArrayTypeSymbol { Rank: 1 } at) continue;
            var et = D.Types.Canonical(at.ElementType);
            if (!Types.IsRef(et) || Types.IsImmortalType(et)) continue;
            var vs = ValuesFor(et);
            var ws = wildArrays.Values.Select(v => Fit(v, et)).OfType<ITypeSymbol>().ToList();
            if (vs == null && ws.Count == 0) continue;
            var s = ArraySite(at)!;
            if (vs != null) foreach (var v in vs.Values) s.AddValue(v);
            foreach (var v in ws) s.AddValue(v);
            arrays++;
            if (s_arenaDebug) Console.Error.WriteLine($"by address: site {s.Key} holds {string.Join(", ", (vs?.Keys.AsEnumerable() ?? []).Concat(ws.Select(Types.Key)).Distinct().Take(6))}");
        }
        foreach (var s in _sites.Values.ToList())
        {
            if (s.IsArray || s.ContainerId >= 0 || s.SlotType == null || !Types.IsRef(s.SlotType)) continue;
            // (a readonly field whose address is taken: a store through that ref comes after construction)
            if (s.AddressTaken && s.Immutable && ValuesFor(s.SlotType) != null) { s.Immutable = false; fields++; }
            var ws = wild.Values.Select(v => Fit(v, s.SlotType)).OfType<ITypeSymbol>().ToList();
            if (ws.Count == 0) continue;
            foreach (var v in ws) s.AddValue(v);
            s.Immutable = false; s.General = true; wilds++;
        }
        D.Mark($"    arenas: stores by address ({_refStores.Count} slot types, {wild.Count} values anywhere, {_wildArrayValues.Count} into any array; {arrays} array sites, {fields} readonly fields, {wilds} fields from raw memory)");
    }
}
