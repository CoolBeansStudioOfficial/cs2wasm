using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Locals that borrow from a field (section 4.1: "a variable that is never reassigned borrows from its source for its
// scope"). `var x = this.f;` (or `p.f` for a parameter never assigned) would retain the field's object and release it
// when x goes out of scope. The object is already kept alive by the field, as long as nothing writes the field
// meanwhile: so when no code the function can run (itself and everything it calls, through dispatch stubs and the
// function table too) stores to that field, its address is never taken, and no other thread can run, x holds no
// count. Decided once the whole program is compiled: the declaration stores through a placeholder that becomes the
// retain, or nothing; on success the local's releases (scope exit, unwinding) go too.
sealed partial class FunctionCompiler
{
    /// <summary>The field a declaration can borrow from (key), when the local and its source qualify.</summary>
    string? FieldBorrowCandidate(ILocalSymbol l, IOperation init)
    {
        var r = FieldBorrowCandidateCore(l, init);
        if (Driver.BorrowDebug && r == null) Console.Error.WriteLine($"borrow? {M.Name} {l.Name}: none for {init.Kind} {init.Syntax}");
        return r;
    }

    string? FieldBorrowCandidateCore(ILocalSymbol l, IOperation init)
    {
        if (l.IsRef || IsCaptured(l) || WrittenLocals().Contains(l) || _closure != null || _machine != null)
        {
            if (Driver.BorrowDebug) Console.Error.WriteLine($"borrow? {M.Name} {l.Name}: no (ref {l.IsRef}, captured {IsCaptured(l)}, written {WrittenLocals().Contains(l)}, closure {_closure != null}, machine {_machine != null})");
            return null;
        }
        while (init is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) init = c.Operand;
        // a copy of a reference that stays put for the whole method: the object outlives the local ("": always)
        if (IsStableBase(init)) return "";
        // an element of an array that stays put (a stable reference, or a field of one): no element store may run
        if (init is IArrayElementReferenceOperation { Indices.Length: 1 } ae && Types.IsRef(T.Resolve(ae.Type!, Map)))
        {
            var ar = ae.ArrayReference;
            while (ar is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } ac) ar = ac.Operand;
            if (IsStableBase(ar)) return Driver.ElementsKey;
            if (ar is IFieldReferenceOperation { Field.IsStatic: false, Instance: { } ai } afr && IsStableBase(ai) && afr.Field.ContainingType.TypeKind == TypeKind.Class
                && !(ai is IParameterReferenceOperation && TypeOf(ai).IsValueType))
            {
                var ak = Driver.FieldKey(Owner(afr.Field), StorageField(afr.Field));
                D.NoteBorrowField(ak, StorageField(afr.Field));
                return ak + "\n" + Driver.ElementsKey;
            }
            return null;
        }
        if (init is not IFieldReferenceOperation { Field.IsStatic: false, Instance: { } inst } fr) return null;
        if (!Types.IsRef(T.Resolve(fr.Field.Type, Map)) || fr.Field.ContainingType.TypeKind != TypeKind.Class) return null;
        switch (inst)
        {
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } when M.ContainingType.TypeKind == TypeKind.Class: break;
            case IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } p } when !IsCaptured(p) && !_assignedParams.Contains(p) && !_assignedParams.Contains(p.OriginalDefinition) && !TypeOf(inst).IsValueType: break;
            default: return null;
        }
        var key = Driver.FieldKey(Owner(fr.Field), StorageField(fr.Field));
        D.NoteBorrowField(key, StorageField(fr.Field));
        return key;
    }

    /// <summary>The declaration of a borrowing candidate: the field's value goes to the local through the placeholder.</summary>
    void EmitFieldBorrow(Var v, IOperation init, string key)
    {
        var o = EmitValue(init);
        ConvertTop(TypeOf(init), v.Type, ref o);
        var marker = D.Module.AddFunction("borrow? " + key, new FuncType([ValType.I32], [ValType.I32]));
        if (o == Own.Owned) { E.Set(v.Local); return; }
        E.Call(marker);
        E.Set(v.Local);
        D.FieldBorrows.Add((E.Fn, v.Local, key, marker));
    }
}

internal sealed partial class Driver
{
    public static string FieldKey(ITypeSymbol owner, ISymbol storage) => Types.Key(owner) + "::" + storage.Name;
    /// <summary>The pseudo-field of every reference element (and of memory written through an address).</summary>
    public const string ElementsKey = "[]";
    static readonly bool s_borrowDebug = Environment.GetEnvironmentVariable("CS2WASM_BORROW") == "1";
    internal static bool BorrowDebug => s_borrowDebug;

    public static bool WritesMemory(Op op) => op is Op.I32Store or Op.I32Store8 or Op.I32Store16 or Op.I64Store or Op.I64Store8 or Op.I64Store16 or Op.I64Store32
        or Op.MemoryCopy or Op.MemoryFill || (int)op >= 0xFE00 && op is not (Op.I32AtomicLoad or Op.I64AtomicLoad);
    /// <summary>The instance fields each function stores to (by key).</summary>
    public readonly Dictionary<WasmFunction, HashSet<string>> FieldWrites = [];
    /// <summary>Fields whose address code takes (ref, out, Interlocked): written by whoever holds the address.</summary>
    public readonly HashSet<string> FieldsAddressTaken = [];
    public readonly List<(WasmFunction fn, int local, string field, WasmFunction marker)> FieldBorrows = [];
    /// <summary>Temps holding a field of a stable object for a call: the retain and the release placeholders.</summary>
    readonly List<(WasmFunction fn, string field, WasmFunction retain, WasmFunction release)> _tempBorrows = [];

    public (WasmFunction retain, WasmFunction release) NewFieldBorrow(WasmFunction fn, string key)
    {
        var r = Module.AddFunction("borrow? " + key, new FuncType([ValType.I32], [ValType.I32]));
        var d = Module.AddFunction("unborrow? " + key, new FuncType([ValType.I32], []));
        _tempBorrows.Add((fn, key, r, d));
        return (r, d);
    }

    /// <summary>A store whose place in the function is not marked (the runtime's own functions): anywhere in it.</summary>
    public void NoteFieldWrite(WasmFunction fn, string key)
    {
        if (!FieldWrites.TryGetValue(fn, out var s)) FieldWrites[fn] = s = [];
        s.Add(key);
        if (!_unplacedWrites.TryGetValue(fn, out var u)) _unplacedWrites[fn] = u = [];
        u.Add(key);
    }

    /// <summary>Where a function stores a field (a no-op instruction carrying the key, beside the store): a borrow is
    /// broken only by a store that runs while the borrowing local is live (section 4.1, retroactive owners). Taken out
    /// again once the borrows are decided.</summary>
    internal sealed class WriteMark(string key) { public readonly string Key = key; }
    readonly Dictionary<string, WriteMark> _writeMarks = [];
    readonly Dictionary<WasmFunction, HashSet<string>> _unplacedWrites = [];

    public void NoteFieldWrite(Emitter e, string key)
    {
        if (!FieldWrites.TryGetValue(e.Fn, out var s)) FieldWrites[e.Fn] = s = [];
        s.Add(key);
        if (!_writeMarks.TryGetValue(key, out var m)) _writeMarks[key] = m = new WriteMark(key);
        e.Emit(Op.Nop, 0, 0, m);
        e.Body[^1] = e.Body[^1] with { Pos = -1 };
    }

    void StripWriteMarks()
    {
        foreach (var f in Module.Functions)
            if (!f.IsImport) f.Body.RemoveAll(x => x.Op == Op.Nop && x.Ref is WriteMark);
    }

    /// <summary>Whether a store of one of the keys may run while the local is in scope (section 4.1: a variable
    /// borrows from its source for its scope): from its definition (at `def`) to the end of its scope (the mark
    /// PopScope leaves; the function's end without one). Everything derived from the local (temporaries, references
    /// into its object, locals that borrow from its fields) lives inside that scope, so the lexical range is enough.
    /// Stores: the function's own (marks), and those of what it calls in the range.</summary>
    bool StoredWhileInScope(WasmFunction fn, int local, int def, string[] keys, StoreSummary summary)
    {
        if (_unplacedWrites.TryGetValue(fn, out var u) && keys.Any(u.Contains)) return true;
        var b = fn.Body;
        for (int i = def; i < b.Count; i++)
        {
            var ins = b[i];
            if (ins.Op == Op.Nop)
            {
                if (ins.Ref is WriteMark wm) { if (Array.IndexOf(keys, wm.Key) >= 0) return true; }
                else if (ReferenceEquals(ins.Ref, ScopeEnd) && ins.Imm == local) return false;
            }
            else if (ins.Op == Op.Call && ins.Ref is WasmFunction g) { foreach (var k in keys) if (summary.Has(g, k)) return true; }
            else if (ins.Op == Op.CallIndirect) { foreach (var k in keys) if (summary.HasIndirect(k)) return true; }
        }
        return false;
    }

    /// <summary>The end of a local's scope (a no-op instruction: Imm is the local), for StoredWhileInScope.</summary>
    internal static readonly WriteMark ScopeEnd = new("scope end");

    /// <summary>Resolves the placeholders (RuntimeGen.Finish, when every function exists).</summary>
    public void ResolveFieldBorrows()
    {
        if (FieldBorrows.Count == 0 && _tempBorrows.Count == 0) { StripWriteMarks(); return; }
        var byFn = FieldBorrows.GroupBy(b => b.fn).ToDictionary(g => g.Key, g => g.ToList());
        bool threads = UsesThreads;
        // what each function may store, with everything it can call (an indirect call: any function in the table)
        StoreSummary? summary = null;
        if (!threads)
        {
            var queried = new HashSet<string>();
            foreach (var b in FieldBorrows) foreach (var k in b.field.Split('\n')) if (k != "") queried.Add(k);
            foreach (var b in _tempBorrows) queried.Add(b.field);
            summary = new StoreSummary(this, queried);
        }
        var reflected = ReflectedFieldKeys();
        foreach (var (fn, list) in byFn)
        {
            var b = fn.Body;
            foreach (var (_, local, field, marker) in list)
            {
                // (an init-only field never changes once its object is constructed: no code, on no thread, writes it)
                bool ok = field == "" || field.Split('\n').All(k => !FieldsAddressTaken.Contains(k) && !reflected.Contains(k)
                    && (InitOnlyKey(k) || !threads && summary != null));
                if (ok && field != "" && summary != null)
                {
                    var keys = field.Split('\n').Where(k => !InitOnlyKey(k)).ToArray();
                    int def = b.FindIndex(x => x.Op == Op.Call && x.Ref == marker);
                    if (keys.Length > 0 && (def < 0 || StoredWhileInScope(fn, local, def, keys, summary))) ok = false;
                }
                if (s_borrowDebug) Console.Error.WriteLine($"borrow {fn.Name} local {local} {field.Replace('\n', '|')}: {ok}"
                    + (summary != null ? " own=" + (FieldWrites.TryGetValue(fn, out var ow) && ow.Contains(field) ? "y" : "n") + " via "
                        + string.Join(",", fn.Body.Where(x => x.Op == Op.Call && x.Ref is WasmFunction g && summary.Has(g, field)).Select(x => ((WasmFunction)x.Ref!).Name).Distinct().Take(5)) : ""));
                // the local's other writes are the releases at scope exit (get; release; 0; set): nothing moves its value
                if (ok)
                    for (int i = 0; i < b.Count && ok; i++)
                        if (b[i].Op is Op.LocalSet or Op.LocalTee && b[i].Imm == local && !(i > 0 && b[i - 1].Op == Op.Call && b[i - 1].Ref == marker))
                            ok = i >= 3 && b[i - 1].Op == Op.I32Const && b[i - 1].Imm == 0 && b[i - 1].Ref == null
                                 && b[i - 2].Op == Op.Call && b[i - 2].Ref == Rt.Release && b[i - 3].Op == Op.LocalGet && b[i - 3].Imm == local;
                for (int i = 0; i < b.Count; i++)
                {
                    if (b[i].Op == Op.Call && b[i].Ref == marker)
                        b[i] = ok ? b[i] with { Op = Op.Nop, Ref = null } : b[i] with { Ref = Rt.Retain };
                    else if (ok && b[i].Op == Op.LocalGet && b[i].Imm == local && i + 1 < b.Count && b[i + 1].Op == Op.Call && b[i + 1].Ref == Rt.Release)
                    {
                        b[i] = b[i] with { Op = Op.Nop };
                        b[i + 1] = b[i + 1] with { Op = Op.Nop, Ref = null };
                    }
                }
            }
            fn.Body.RemoveAll(x => x.Op == Op.Nop && x.Ref == null && x.Pos < 0);
        }
        // the temps: the retain and the release go together
        var markers = new Dictionary<WasmFunction, WasmFunction?>();
        foreach (var (fn, field, retain, release) in _tempBorrows)
        {
            bool initOnly = InitOnlyKey(field) && !FieldsAddressTaken.Contains(field) && !reflected.Contains(field);
            bool ok = initOnly || !threads && summary != null && !FieldsAddressTaken.Contains(field) && !reflected.Contains(field);
            // the borrow lasts from the retain to the statement's release: only what runs in between may not store the
            // field (the function's own stores anywhere: the whole function's summary decides)
            if (ok && !initOnly && FieldWrites.TryGetValue(fn, out var own) && own.Contains(field)) ok = false;
            if (ok && !initOnly)
            {
                var b = fn.Body;
                int r = b.FindIndex(x => x.Op == Op.Call && x.Ref == retain);
                int e = r < 0 ? -1 : b.FindIndex(r + 1, x => x.Op == Op.Call && x.Ref == release);
                if (r < 0 || e < 0) ok = !summary!.Has(fn, field);
                else
                    for (int k = r + 1; k < e && ok; k++)
                    {
                        var ins = b[k];
                        if (ins.Op == Op.Call && ins.Ref is WasmFunction g && g != retain && summary!.Has(g, field)) ok = false;
                        else if (ins.Op == Op.CallIndirect && summary!.Has(fn, field)) ok = false;
                    }
            }
            markers[retain] = ok ? null : Rt.Retain;
            markers[release] = ok ? null : Rt.Release;
        }
        if (markers.Count > 0)
            foreach (var f in Module.Functions)
            {
                var b = f.Body;
                bool any = false;
                for (int i = 0; i < b.Count; i++)
                    if (b[i].Op == Op.Call && b[i].Ref is WasmFunction g && markers.TryGetValue(g, out var to))
                    {
                        any = true;
                        // (retain → the value stays; release → its operand is dropped)
                        if (to != null) b[i] = b[i] with { Ref = to };
                        else b[i] = g.Type.Results.Length == 1 ? b[i] with { Op = Op.Nop, Ref = null } : b[i] with { Op = Op.Drop, Ref = null };
                    }
                if (any) b.RemoveAll(x => x.Op == Op.Nop && x.Ref == null && x.Pos < 0);
            }
        var dead = markers.Keys.Concat(FieldBorrows.Select(b => b.marker)).ToHashSet();
        Module.Functions.RemoveAll(dead.Contains);
        StripWriteMarks();
    }

    /// <summary>What each function may store, with everything it can call: bits over the fields some borrow asks about,
    /// unioned over the call graph's strongly connected components (callees first); an indirect call reaches every
    /// function in the table (one node stands for them).</summary>
    sealed class StoreSummary
    {
        readonly Dictionary<string, int> _key = [];
        readonly Dictionary<WasmFunction, int> _node = [];
        readonly ulong[][] _bits;

        public StoreSummary(Driver d, HashSet<string> queried)
        {
            foreach (var k in queried) _key[k] = _key.Count;
            int words = Math.Max(1, (_key.Count + 63) / 64);
            var fns = d.Module.Functions;
            for (int i = 0; i < fns.Count; i++) _node[fns[i]] = i;
            int table = fns.Count, n = fns.Count + 1;
            // what writes only memory no program code can reach: the runtime's throw helpers (bounds, null and cast
            // checks: the exception they make), the allocator (fresh or freed blocks), the arenas' and the marks'
            // own tables (a finalizer a release runs still counts: it is compiled code the destroy path calls)
            string[] internals = ["Cs2Wasm.Runtime.ThrowHelpers.", "Cs2Wasm.Runtime.Rt.Alloc", "Cs2Wasm.Runtime.Rt.Free(", "Cs2Wasm.Runtime.Rt.Arena", "Cs2Wasm.Runtime.ObjectMarks."];
            var skip = new bool[n];
            for (int i = 0; i < fns.Count; i++) skip[i] = internals.Any(p => fns[i].Name.StartsWith(p, StringComparison.Ordinal));
            var edges = new List<int>[n];
            for (int i = 0; i < n; i++) edges[i] = [];
            _bits = new ulong[n][];
            for (int i = 0; i < n; i++) _bits[i] = new ulong[words];
            for (int i = 0; i < fns.Count; i++)
            {
                var f = fns[i];
                foreach (var ins in f.Body)
                {
                    if (ins.Op == Op.Call && ins.Ref is WasmFunction g && _node.TryGetValue(g, out int gi) && !skip[gi] && gi != i) edges[i].Add(gi);
                    else if (ins.Op == Op.CallIndirect) edges[i].Add(table);
                    else if (ins.Op == Op.I32Const && ins.Ref is WasmFunction t && _node.TryGetValue(t, out int ti)) edges[table].Add(ti);
                }
                if (f.InTable || f.TableRoot) edges[table].Add(i);
                if (f.ForwardTo != null && _node.TryGetValue(f.ForwardTo, out int fi)) edges[i].Add(fi);
                if (d.FieldWrites.TryGetValue(f, out var w))
                    foreach (var k in w) if (_key.TryGetValue(k, out int ki)) _bits[i][ki >> 6] |= 1UL << (ki & 63);
            }
            // Tarjan, iterative: components come out callees first
            var index = new int[n]; var low = new int[n]; var on = new bool[n];
            Array.Fill(index, -1);
            var stack = new Stack<int>(); int next = 0;
            var call = new Stack<(int v, int e)>();
            for (int s0 = 0; s0 < n; s0++)
            {
                if (index[s0] >= 0) continue;
                call.Push((s0, 0)); index[s0] = low[s0] = next++; stack.Push(s0); on[s0] = true;
                while (call.Count > 0)
                {
                    var (v, e) = call.Pop();
                    if (e < edges[v].Count)
                    {
                        call.Push((v, e + 1));
                        int w2 = edges[v][e];
                        if (index[w2] < 0) { index[w2] = low[w2] = next++; stack.Push(w2); on[w2] = true; call.Push((w2, 0)); }
                        else if (on[w2]) low[v] = Math.Min(low[v], index[w2]);
                        continue;
                    }
                    if (call.Count > 0) { var (p, _) = call.Peek(); low[p] = Math.Min(low[p], low[v]); }
                    if (low[v] != index[v]) continue;
                    var comp = new List<int>();
                    int x;
                    do { x = stack.Pop(); on[x] = false; comp.Add(x); } while (x != v);
                    var acc = new ulong[words];
                    foreach (var c in comp)
                    {
                        for (int k = 0; k < words; k++) acc[k] |= _bits[c][k];
                        foreach (var t in edges[c]) if (!on[t]) for (int k = 0; k < words; k++) acc[k] |= _bits[t][k];
                    }
                    foreach (var c in comp) _bits[c] = acc;
                }
            }
        }

        /// <summary>Whether some function an indirect call can reach stores the field.</summary>
        public bool HasIndirect(string key) => _key.TryGetValue(key, out int k) && (_bits[^1][k >> 6] & (1UL << (k & 63))) != 0;

        public bool Has(WasmFunction f, string key) =>
            _key.TryGetValue(key, out int k) && _node.TryGetValue(f, out int i) && (_bits[i][k >> 6] & (1UL << (k & 63))) != 0;
    }

    /// <summary>Fields reflection can set (kept for the program's reflection: FieldInfo.SetValue writes them).</summary>
    HashSet<string> ReflectedFieldKeys()
    {
        var r = new HashSet<string>();
        if (_reflect == null) return r;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
            foreach (var m in KeptMembers(t, all, names, ctors))
                if (m is IFieldSymbol f && !f.IsStatic) r.Add(FieldKey(Types.Canonical(f.ContainingType), f));
        return r;
    }
}
