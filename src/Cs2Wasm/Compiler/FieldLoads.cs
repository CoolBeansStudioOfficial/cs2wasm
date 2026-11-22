using System.Collections.Concurrent;
using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Which instance fields a loop can read once (Wasm/FieldLoads.cs does it): the compiler's side. A load of a class's
/// field by name carries the field's tag (FieldTag, by the field's declaration: `List&lt;T&gt;._size` is one field
/// whatever T is, so a store through one instantiation counts against a load through another); every store of such a
/// field by name leaves a mark beside it (FieldStoreMark). When every function exists (ResolveFieldBorrows),
/// PrepareFieldLoads gives the fields that qualify a number, writes into each loop's instruction the set of fields
/// stored inside it, into each function the set it stores anywhere, and the calls' stores go into the summary of all
/// stores (StoreSummary) the borrows use. The conditions, each of which takes a field out for the whole program:
///  - a class's instance field of a number or a reference: not a struct's (a struct is copied as a whole and written
///    through `this`, an address), not of a struct's type, not volatile, not in a class with explicit layout (fields
///    may overlap), not a field of String, Array or a delegate (the runtime writes those);
///  - no code has its address: every way to a field's place other than a plain load or a plain store goes through
///    FunctionCompiler.EmitFieldSlotBase, which takes the field out unless the load or the store said it is theirs
///    (`ref`, `out`, `in`, `fixed`, a call of a struct's method on it, Interlocked, Volatile, [UnsafeAccessor]);
///  - reflection cannot set it (the members kept for reflection);
///  - in a program with threads: only a readonly field (another thread may store any other meanwhile).
/// A local of an iterator or an async method, a captured variable, a primary constructor's parameter: fields of
/// objects the compiler makes, read and written by their own code; they have no tag and no mark, and no tagged
/// field shares their memory.
/// CS2WASM_NO_FIELD_HOIST=1: no tag and no mark is made; the module is the one from before this pass.
/// </summary>
internal sealed partial class Driver
{
    internal static readonly bool s_noFieldHoist = Environment.GetEnvironmentVariable("CS2WASM_NO_FIELD_HOIST") == "1";
    static readonly bool s_fieldHoistDebug = Environment.GetEnvironmentVariable("CS2WASM_FIELD_HOIST_DEBUG") == "1";
    readonly ConcurrentDictionary<ISymbol, FieldTag?> _fieldTags = new(SymbolEqualityComparer.Default);
    /// <summary>Per function, the numbers of the fields its own code stores; how many fields have a number.</summary>
    Dictionary<WasmFunction, int[]>? _hoistOwn;
    int _hoistCount;
    FieldHoist? _fieldHoist;

    static FieldTag? MakeFieldTag(ISymbol s)
    {
        var f = (IFieldSymbol)s;
        var owner = f.ContainingType;
        if (owner == null || owner.TypeKind != TypeKind.Class || f.IsStatic || f.IsConst || f.IsVolatile || f.RefKind != RefKind.None || f.IsFixedSizeBuffer) return null;
        if (owner.SpecialType is SpecialType.System_String or SpecialType.System_Array or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate or SpecialType.System_Object) return null;
        foreach (var a in owner.GetAttributes())
            if (a.AttributeClass?.Name == "StructLayoutAttribute" && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is { } lk && Convert.ToInt32(lk) == 2) return null;
        foreach (var a in f.GetAttributes()) if (a.AttributeClass?.Name == "FieldOffsetAttribute") return null;
        return new FieldTag(Types.Key(owner) + "::" + f.Name, f.IsReadOnly);
    }

    /// <summary>The tag of an instance field read or stored by name, as a value of type `fieldType` (resolved); null:
    /// not a field this pass looks at.</summary>
    internal FieldTag? FieldTagOf(IFieldSymbol storage, ITypeSymbol fieldType)
    {
        if (s_noFieldHoist || storage.IsStatic) return null;
        if (Types.IsStruct(fieldType) || Types.WasmType(fieldType) == null) return null;
        var tag = _fieldTags.GetOrAdd(storage.OriginalDefinition, MakeFieldTag);
        if (tag == null || ThreadMode && !tag.Readonly) return null;
        return tag;
    }

    /// <summary>The fields each function was seen to store when it was compiled: what its marks must show again
    /// (a body whose marks were lost has stores at places not known: none of its loops reads a field once).</summary>
    readonly Dictionary<WasmFunction, HashSet<FieldTag>> _fieldStoresNoted = [];
    internal void NoteFieldStoreIn(WasmFunction fn, FieldTag tag)
    {
        lock (_fieldStoresNoted)
        {
            if (!_fieldStoresNoted.TryGetValue(fn, out var s)) _fieldStoresNoted[fn] = s = [];
            s.Add(tag);
        }
    }

    /// <summary>Some code has the field's place by its address.</summary>
    internal void NoteFieldEscape(IFieldSymbol storage)
    {
        if (s_noFieldHoist || storage.IsStatic) return;
        if (_fieldTags.GetOrAdd(storage.OriginalDefinition, MakeFieldTag) is { } tag && !tag.Escaped)
        {
            tag.Escaped = true;
            if (s_fieldHoistDebug) Console.Error.WriteLine("field hoist: address taken of " + tag.Key);
        }
    }

    /// <summary>Numbers for the fields that qualify and are read somewhere; the loops' and the functions' own sets
    /// (the marks are still in the bodies). False: the pass is off.</summary>
    bool PrepareFieldLoads()
    {
        if (s_noFieldHoist) return false;
        bool threads = UsesThreads;
        if (_reflect != null)
            foreach (var (t, all, names, ctors) in _reflect.Types.Values)
                foreach (var m in KeptMembers(t, all, names, ctors))
                    if (m is IFieldSymbol { IsStatic: false } rf && _fieldTags.TryGetValue(rf.OriginalDefinition, out var rt) && rt != null) rt.Escaped = true;
        int next = 0;
        var fns = Module.Functions;
        foreach (var f in fns)
        {
            if (f.IsImport) continue;
            var b = f.Body;
            for (int i = 0; i < b.Count; i++)
                if (b[i].Ref is FieldTag t)
                {
                    if (t.Escaped || threads && !t.Readonly) b[i] = b[i] with { Ref = null };
                    else if (t.Ids.Length == 0) t.Ids = [++next];
                }
        }
        _hoistCount = next;
        var hoist = _fieldHoist = new FieldHoist(next);
        _hoistOwn = [];
        var open = new List<(int at, List<int>? ids, bool loop)>();
        foreach (var f in fns)
        {
            if (f.IsImport) continue;
            var b = f.Body;
            List<int>? own = null;
            open.Clear();
            for (int i = 0; i < b.Count; i++)
            {
                var x = b[i];
                switch (x.Op)
                {
                    case Op.Loop: open.Add((i, null, true)); break;
                    case Op.Block or Op.If or Op.TryTable: open.Add((i, null, false)); break;
                    case Op.End when open.Count > 0:
                        {
                            var (at, ids, loop) = open[^1];
                            open.RemoveAt(open.Count - 1);
                            if (!loop) break;
                            if (ids != null)
                                for (int k = open.Count - 1; k >= 0; k--)
                                    if (open[k].loop) { if (open[k].ids == null) open[k] = (open[k].at, [.. ids], true); else open[k].ids!.AddRange(ids); break; }
                            b[at] = b[at] with { Imm2 = ids == null ? FieldHoist.None : hoist.Intern(ids) };
                            break;
                        }
                    case Op.Nop when x.Ref is FieldStoreMark { Tag.Ids: [var id] }:
                        (own ??= []).Add(id);
                        for (int k = open.Count - 1; k >= 0; k--)
                            if (open[k].loop) { if (open[k].ids == null) open[k] = (open[k].at, [id], true); else open[k].ids!.Add(id); break; }
                        break;
                }
            }
            if (_fieldStoresNoted.TryGetValue(f, out var noted))
                foreach (var t in noted)
                    if (t.Ids is [var nid] && !(own != null && own.Contains(nid)))
                    {
                        // (stored somewhere in the body, the mark gone: no loop of it is known)
                        (own ??= []).Add(nid);
                        f.NoFieldHoist = true;
                        if (s_fieldHoistDebug) Console.Error.WriteLine($"field hoist: {f.Name} lost its mark of {t.Key}");
                    }
            if (own == null) { f.FieldStoreSet = FieldHoist.None; continue; }
            f.FieldStoreSet = hoist.Intern(own);
            _hoistOwn[f] = hoist.Set(f.FieldStoreSet);
        }
        if (s_fieldHoistDebug)
        {
            Console.Error.WriteLine($"field hoist: {next} fields with a number, {_hoistOwn.Count} functions store one, of {_fieldTags.Count} fields seen");
            foreach (var t in _fieldTags.Values.Where(t => t != null).OrderBy(t => t!.Key)) Console.Error.WriteLine($"field hoist: {t!.Key}: {(t.Escaped ? "address taken or reflection" : t.Ids.Length == 0 ? "never read by name" : "number " + t.Ids[0])}");
        }
        return true;
    }

    /// <summary>The module gets the sets and the summary of the calls (null: none was made, every call stores).</summary>
    void FinishFieldLoads(StoreSummary? summary)
    {
        if (_fieldHoist == null) return;
        _fieldHoist.Stores = summary == null ? new NoSummary() : new SummaryStores(this, summary);
        Module.FieldHoist = _fieldHoist;
        _hoistOwn = null;
    }

    sealed class NoSummary : IFieldStores
    {
        public bool Knows(WasmFunction g) => true;
        public bool Stores(WasmFunction g, int id) => true;
        public bool Indirect(FuncType? sig, int id) => true;
        public bool Host(int id) => true;
    }

    sealed class SummaryStores(Driver d, StoreSummary s) : IFieldStores
    {
        readonly Dictionary<int, bool> _host = [];
        readonly Dictionary<WasmFunction, bool> _internal = [];
        List<WasmFunction>? _exports;
        bool Internal(WasmFunction g)
        {
            if (!_internal.TryGetValue(g, out bool r)) _internal[g] = r = StoreSummary.Internal(g);
            return r;
        }
        public bool Knows(WasmFunction g) => s.Knows(g);
        // (what the summary leaves out as writing only memory no program code reaches, it leaves out here)
        public bool Stores(WasmFunction g, int id) => !Internal(g) && s.HasId(g, id);
        public bool Indirect(FuncType? sig, int id) => s.HasIndirectId(sig, id);
        public bool Host(int id)
        {
            if (_host.TryGetValue(id, out bool known)) return known;
            bool r = s.HasIndirectId(null, id);
            if (!r)
            {
                _exports ??= [.. d.Module.Functions.Where(x => x.Exports.Count > 0 && !x.IsImport)];
                foreach (var x in _exports) if (!s.Knows(x) || s.HasId(x, id)) { r = true; break; }
            }
            return _host[id] = r;
        }
    }
}
