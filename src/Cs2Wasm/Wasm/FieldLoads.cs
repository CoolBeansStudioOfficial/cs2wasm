namespace Cs2Wasm.Wasm;

/// <summary>Which field a load reads (the load's Ref): an instance field of a class, by its declaration (every
/// instantiation of a generic class alike). Made by the compiler where a field is read by its name
/// (FunctionCompiler.EmitFieldLoad, an auto-property's field); the stores of the same field are marked beside
/// (FieldStoreMark) until Driver.PrepareFieldLoads has gathered them.</summary>
internal sealed class FieldTag
{
    public readonly string Key;
    public readonly bool Readonly;
    /// <summary>Some code has the field's address, or sets it by reflection: stored by whoever holds that.</summary>
    public volatile bool Escaped;
    /// <summary>The fields this load may be of (one; several once bodies that differ only in their tags were made
    /// one, WasmModule.MergeIdenticalBodies): numbers given by Driver.PrepareFieldLoads; none: not a candidate.</summary>
    public int[] Ids = [];
    public readonly FieldStoreMark Mark;
    public FieldTag(string key, bool ro) { Key = key; Readonly = ro; Mark = new FieldStoreMark(this); }
    public override string ToString() => "field " + Key;
}

/// <summary>A no-op instruction's Ref where a function stores the field (taken out by Driver.StripWriteMarks).</summary>
internal sealed class FieldStoreMark(FieldTag tag)
{
    public readonly FieldTag Tag = tag;
    public override string ToString() => "store of " + Tag.Key;
}

/// <summary>What a call can store, with everything it calls (Driver's StoreSummary).</summary>
internal interface IFieldStores
{
    bool Knows(WasmFunction g);
    bool Stores(WasmFunction g, int id);
    /// <summary>An indirect call of this type (null: of any type).</summary>
    bool Indirect(FuncType? sig, int id);
    /// <summary>A call of the host: what it can call back.</summary>
    bool Host(int id);
}

/// <summary>The module's knowledge of field stores, kept from the compiler to the pass below: sets of field numbers
/// (a loop's Imm2 names the set of fields stored inside it by the function's own code and what was inlined there;
/// a function's FieldStoreSet the same for its whole body), and the summary of calls.</summary>
internal sealed class FieldHoist(int count)
{
    public IFieldStores Stores = null!;
    /// <summary>How many fields have a number (1..Count).</summary>
    public readonly int Count = count;
    public const int Unknown = 0, None = 1;
    readonly List<int[]> _sets = [[], []];
    readonly Dictionary<string, int> _intern = [];
    readonly Dictionary<(int, int), int> _unions = [];
    readonly Dictionary<WasmFunction, List<WasmFunction>> _merged = [];
    readonly Dictionary<string, FieldTag> _mergedTags = [];

    /// <summary>The set of these numbers (sorted here, each once).</summary>
    public int Intern(List<int> ids)
    {
        if (ids.Count == 0) return None;
        ids.Sort();
        int w = 1;
        for (int i = 1; i < ids.Count; i++) if (ids[i] != ids[w - 1]) ids[w++] = ids[i];
        var key = string.Join(',', ids.Take(w));
        if (_intern.TryGetValue(key, out int id)) return id;
        _sets.Add([.. ids.Take(w)]);
        return _intern[key] = _sets.Count - 1;
    }

    public int Union(int a, int b)
    {
        if (a == Unknown || b == Unknown) return Unknown;
        if (a == b || b == None) return a;
        if (a == None) return b;
        if (a > b) (a, b) = (b, a);
        if (_unions.TryGetValue((a, b), out int u)) return u;
        var l = new List<int>(_sets[a].Length + _sets[b].Length);
        l.AddRange(_sets[a]); l.AddRange(_sets[b]);
        return _unions[(a, b)] = Intern(l);
    }

    public int[] Set(int set) => _sets[set];

    public bool Contains(int set, int id) => set == Unknown || set != None && Array.BinarySearch(_sets[set], id) >= 0;

    /// <summary>Whether a call of g can store the field: g's summary, and those of the functions whose bodies were
    /// the same as g's and whose calls are now calls of g.</summary>
    public bool CallStores(WasmFunction g, int id)
    {
        if (Stores.Stores(g, id)) return true;
        if (_merged.TryGetValue(g, out var l)) foreach (var m in l) if (Stores.Stores(m, id)) return true;
        return false;
    }

    /// <summary>`from`'s calls become calls of `into` (their bodies are the same but for the fields their loads and
    /// loops name): `into` stands for both.</summary>
    public void Merged(WasmFunction into, WasmFunction from)
    {
        if (FieldLoads.Without("merge")) return;
        into.FieldStoreSet = Union(into.FieldStoreSet, from.FieldStoreSet);
        if (from.NoFieldHoist) into.NoFieldHoist = true;
        if (!_merged.TryGetValue(into, out var l)) _merged[into] = l = [];
        l.Add(from);
        if (_merged.TryGetValue(from, out var fl)) l.AddRange(fl);
        var a = into.Body; var b = from.Body;
        if (a.Count != b.Count) { into.NoFieldHoist = true; return; }
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Op == Op.Loop) { if (x.Imm2 != y.Imm2) a[i] = x with { Imm2 = Union((int)x.Imm2, (int)y.Imm2) }; }
            else if (x.Ref is FieldTag tx && !ReferenceEquals(x.Ref, y.Ref)) a[i] = x with { Ref = y.Ref is FieldTag ty ? Both(tx, ty) : null };
        }
    }

    FieldTag Both(FieldTag a, FieldTag b)
    {
        var ids = a.Ids.Union(b.Ids).Order().ToArray();
        var key = string.Join(',', ids);
        if (!_mergedTags.TryGetValue(key, out var t)) _mergedTags[key] = t = new FieldTag(a.Key + " | " + b.Key, false) { Ids = ids };
        return t;
    }

    /// <summary>g's body went into f (Inliner.Expand), inside the loops that start at `openLoops` in `body` (the
    /// innermost last; null: where is not known).</summary>
    public void Inlined(WasmFunction f, WasmFunction g, List<Instr>? body, List<int>? openLoops)
    {
        if (FieldLoads.Without("inline")) return;
        int gs = g.FieldStoreSet;
        f.FieldStoreSet = Union(f.FieldStoreSet, gs);
        if (g.NoFieldHoist) f.NoFieldHoist = true;
        if (gs == None) return;
        if (body == null || openLoops == null) { f.NoFieldHoist = true; return; }
        foreach (int at in openLoops)
            if (at >= 0 && body[at].Op == Op.Loop) body[at] = body[at] with { Imm2 = Union((int)body[at].Imm2, gs) };
    }
}

/// <summary>
/// Section 5: an instance field read again on every turn of a loop. V8 does not carry a load from memory across a
/// store or a call: it cannot know that the store is to another place. The compiler knows which field every load by
/// name reads (FieldTag) and which fields every function stores (the marks beside its stores, and through its calls
/// Driver's StoreSummary), so here a loop reads once, before it starts,
///  - a field of the object in a local the loop does not assign, when nothing the loop runs can store that field of
///    any object: not the loop's own code (the set its Loop instruction names, with what was inlined there), not a
///    call (its summary: direct, indirect by what it reaches, the host by what it can call back; a release by what
///    destruction can run), and no code has the field's address (Driver.PrepareFieldLoads: not such a field);
///  - the length of an array in such a local (it never changes).
/// The read before the loop cannot fail: the local holds null or an object (the loop itself reads a field of it, and
/// no code of the loop assigns it), and a field's offset from either is inside the memory. The loop's own null check
/// stays where it was, so a NullReferenceException comes at the same place, after the same effects; what the read
/// got from a null is not used.
/// CS2WASM_NO_FIELD_HOIST=1: no load has a tag, and this pass finds nothing. CS2WASM_FIELD_HOIST_DEBUG=1: each
/// candidate and why not.
/// </summary>
static class FieldLoads
{
    static readonly bool s_debug = Environment.GetEnvironmentVariable("CS2WASM_FIELD_HOIST_DEBUG") == "1";
    static readonly bool s_noLength = Environment.GetEnvironmentVariable("CS2WASM_NO_LENGTH_HOIST") == "1";
    internal static int Hoisted, HoistedLengths;
    /// <summary>CS2WASM_FIELD_HOIST_WITHOUT=a,b (for the proof of tests 230 to 234 only: the module it makes is
    /// wrong): the pass without one of its conditions, so that the test's loop for that condition can be seen to
    /// print something else. address, reflection, layout, volatile, threads (Driver.PrepareFieldLoads); loop, calls,
    /// indirect, assigned, dominance (here); merge, inline (FieldHoist).</summary>
    static readonly HashSet<string> s_without = [.. (Environment.GetEnvironmentVariable("CS2WASM_FIELD_HOIST_WITHOUT") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];
    internal static bool Without(string condition) => s_without.Count > 0 && s_without.Contains(condition);

    static bool IsLoad(Op op) => op >= Op.I32Load && op <= Op.I64Load32U;

    static ValType LoadType(Op op) => op switch
    {
        Op.I64Load or Op.I64Load8S or Op.I64Load8U or Op.I64Load16S or Op.I64Load16U or Op.I64Load32S or Op.I64Load32U => ValType.I64,
        Op.F32Load => ValType.F32,
        Op.F64Load => ValType.F64,
        _ => ValType.I32,
    };

    static bool Throws(in Instr x) => x.Op == Op.Call && x.Ref is WasmFunction h && h.Name.StartsWith("Cs2Wasm.Runtime.ThrowHelpers.", StringComparison.Ordinal);

    sealed class LoopInfo
    {
        public HashSet<int> Assigned = [];
        public List<WasmFunction> Calls = [];
        public List<FuncType?> Indirect = [];
        public Dictionary<int, string?> Killed = [];
    }

    /// <summary>Takes the tags and the loops' sets out again (every function, once the pass is done).</summary>
    public static void Strip(WasmFunction f)
    {
        var b = f.Body;
        for (int i = 0; i < b.Count; i++)
        {
            if (b[i].Ref is FieldTag) b[i] = b[i] with { Ref = null };
            else if (b[i].Op == Op.Loop && b[i].Imm2 != 0) b[i] = b[i] with { Imm2 = 0 };
        }
    }

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f, FieldHoist? h, WasmFunction? nonNull)
    {
        var b = f.Body;
        int n = b.Count;
        bool anyLoop = false, anyLoad = false;
        for (int i = 0; i < n; i++)
        {
            var x = b[i];
            if (x.Op == Op.Loop) anyLoop = true;
            else if (x.Ref != null && IsLoad(x.Op) && (x.Ref is FieldTag || ReferenceEquals(x.Ref, Loops.Length))) anyLoad = true;
        }
        if (!anyLoop || !anyLoad) return false;
        // end: of the block that opens at an index; opener: of the block an `end` or an `else` belongs to
        var end = new int[n];
        var opener = new int[n];
        var loops = new List<int>();
        {
            var open = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                var op = b[i].Op;
                if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) { open.Push(i); if (op == Op.Loop) loops.Add(i); }
                else if (op == Op.Else && open.Count > 0) opener[i] = open.Peek();
                else if (op == Op.End && open.Count > 0) { int at = open.Pop(); end[at] = i; opener[i] = at; }
                else if (op == Op.End) return false;
            }
        }
        int budget = 200_000 + 40 * n;
        bool AssignedIn(int from, int to, int v)
        {
            for (int k = from; k <= to; k++) if (b[k].Op is Op.LocalSet or Op.LocalTee && b[k].Imm == v) return true;
            budget -= to - from;
            return false;
        }
        int firstLocal = f.Type.Params.Length;
        ValType TypeOfLocal(int v) => v < firstLocal ? f.Type.Params[v] : f.Locals[v - firstLocal];
        // the edits: at a load's index, the local that holds its value and how many instructions before it go too
        Dictionary<int, (int local, int form)>? replace = null;
        SortedDictionary<int, List<Instr>>? before = null;

        string? CallKills(WasmFunction g, int id, int depth)
        {
            if (h == null) return "no summary";
            if (Without("calls")) return null;
            if (g.IsImport) return h.Stores.Host(id) ? "the host, " + g.Name : null;
            if (h.Stores.Knows(g)) return h.CallStores(g, id) ? "a call of " + g.Name : null;
            // a function made since the summary (a type's release written out: WasmModule.ExpandLeafReleases): its
            // own code stores no field unless it says so; what it calls is asked in turn
            if (g.FieldStoreSet == FieldHoist.Unknown || depth > 3) return "a call of " + g.Name + " (not known)";
            if (h.Contains(g.FieldStoreSet, id)) return "a call of " + g.Name;
            foreach (var x in g.Body)
            {
                if (x.Op == Op.Call && x.Ref is WasmFunction c) { if (!ReferenceEquals(c, g) && CallKills(c, id, depth + 1) is { } w) return w; }
                else if (x.Op == Op.CallIndirect) { if (h.Stores.Indirect(x.Ref as FuncType, id)) return "an indirect call in " + g.Name; }
            }
            return null;
        }

        foreach (int l in loops)
        {
            int e = end[l];
            if (e <= l) continue;
            LoopInfo? info = null;
            LoopInfo Info()
            {
                if (info != null) return info;
                info = new LoopInfo();
                for (int k = l + 1; k < e; k++)
                {
                    var x = b[k];
                    if (x.Op is Op.LocalSet or Op.LocalTee) info.Assigned.Add((int)x.Imm);
                    else if (x.Op == Op.Call && x.Ref is WasmFunction g) { if (!ReferenceEquals(g, nonNull) && !Throws(x) && !info.Calls.Contains(g)) info.Calls.Add(g); }
                    else if (x.Op == Op.CallIndirect) { var sig = x.Ref as FuncType; if (!info.Indirect.Contains(sig)) info.Indirect.Add(sig); }
                }
                return info;
            }
            string? Killed(int id)
            {
                var li = Info();
                if (li.Killed.TryGetValue(id, out var known)) return known;
                string? why = null;
                if (h == null || f.NoFieldHoist) why = "the function's stores are not known";
                else if (b[l].Imm2 == FieldHoist.Unknown) why = "the loop's stores are not known";
                else if (h.Contains((int)b[l].Imm2, id) && !Without("loop")) why = "the loop stores it";
                else
                {
                    foreach (var g in li.Calls) if ((why = CallKills(g, id, 0)) != null) break;
                    if (why == null && !Without("indirect")) foreach (var sig in li.Indirect) if (h.Stores.Indirect(sig, id)) { why = "an indirect call"; break; }
                }
                return li.Killed[id] = why;
            }
            // The local the loop does not assign whose value the read of `t` at `at` has: t itself, or the one t
            // was last given a copy of (`local.get L; local.set t`, also through further copies), when that copy
            // runs before every read at `at`: it stands in a block still open at `at` (not in one closed since,
            // not in the other arm of an `if`), and no loop that began since assigns t (its turns come back to
            // its start, after the copy). -1: none.
            int Source(int t, int at, int depth)
            {
                if (!Info().Assigned.Contains(t) || Without("assigned")) return t;
                if (depth == 0) return -1;
                bool dom = !Without("dominance");
                for (int k = at - 1; k > l; k--)
                {
                    if (--budget < 0) return -1;
                    var y = b[k];
                    switch (y.Op)
                    {
                        case Op.End when dom: { int op0 = opener[k]; if (op0 <= l || AssignedIn(op0, k, t)) return -1; k = op0; break; }
                        case Op.Else when dom: k = opener[k]; if (k <= l) return -1; break;
                        case Op.Loop when dom: if (AssignedIn(k, end[k], t)) return -1; break;
                        case Op.LocalSet or Op.LocalTee when y.Imm == t:
                            return k - 1 > l && b[k - 1].Op == Op.LocalGet ? Source((int)b[k - 1].Imm, k - 1, depth - 1) : -1;
                    }
                }
                return -1;
            }
            Dictionary<(int, Op, long), int>? slots = null;
            for (int i = l + 2; i < e; i++)
            {
                var x = b[i];
                if (x.Ref == null || !IsLoad(x.Op)) continue;
                var tag = x.Ref as FieldTag;
                bool length = tag == null && ReferenceEquals(x.Ref, Loops.Length);
                if (tag == null && !(length && !s_noLength)) continue;
                if (replace != null && replace.ContainsKey(i)) continue;
                // the address: a local read as it is, or with its null check's call between
                int baseLocal = -1, form = 0;
                string? why = null;
                if (b[i - 1].Op == Op.LocalGet) { baseLocal = (int)b[i - 1].Imm; form = 1; }
                else if (b[i - 1].Op == Op.Call && nonNull != null && ReferenceEquals(b[i - 1].Ref, nonNull) && i - 2 > l && b[i - 2].Op == Op.LocalGet)
                { baseLocal = (int)b[i - 2].Imm; form = 2; }
                if (baseLocal < 0) why = "the address is no local";
                else if (x.Imm < 0 || x.Imm >= 32768) why = "the offset";
                else if ((baseLocal = Source(baseLocal, i - form, 4)) < 0) why = "the loop assigns the local";
                else if (TypeOfLocal(baseLocal) != ValType.I32) why = "the address is no local";
                else if (tag != null)
                {
                    if (tag.Ids.Length == 0) why = "no candidate";
                    else foreach (int id in tag.Ids) if ((why = Killed(id)) != null) break;
                }
                if (s_debug) Console.Error.WriteLine($"field hoist {f.Name} @{l} {(tag != null ? tag.Key : "length")}: {why ?? "yes"}");
                if (why != null) continue;
                slots ??= [];
                // (two loads of one place, each allowed by its own field: one value)
                var slotKey = (baseLocal, x.Op, x.Imm);
                if (!slots.TryGetValue(slotKey, out int hl))
                {
                    hl = firstLocal + f.Locals.Count;
                    f.Locals.Add(LoadType(x.Op));
                    slots[slotKey] = hl;
                    // (before the block the loop leaves by, when it opens right before the loop: `block; loop` stays together)
                    int at = l;
                    while (at > 0 && b[at - 1].Op == Op.Block && b[at - 1].Ref == null) at--;
                    var pre = (before ??= []).TryGetValue(at, out var list) ? list : before[at] = [];
                    pre.Add(new Instr { Op = Op.LocalGet, Imm = baseLocal, Pos = -1 });
                    pre.Add(x with { Pos = -1, Inl = 0 });
                    pre.Add(new Instr { Op = Op.LocalSet, Imm = hl, Pos = -1 });
                    if (length) HoistedLengths++; else Hoisted++;
                }
                (replace ??= [])[i] = (hl, form);
            }
        }
        if (replace == null) return false;
        var o = new List<Instr>(n + 16);
        for (int i = 0; i < n; i++)
        {
            if (before != null && before.TryGetValue(i, out var pre)) o.AddRange(pre);
            // (the read of the local that was the address goes with the load, unless the check between them uses it)
            if (i + 1 < n && b[i].Op == Op.LocalGet && replace.TryGetValue(i + 1, out var next) && next.form != 2) continue;
            if (replace.TryGetValue(i, out var r))
            {
                if (r.form == 2) o.Add(new Instr { Op = Op.Drop, Pos = b[i].Pos, Inl = b[i].Inl });
                o.Add(new Instr { Op = Op.LocalGet, Imm = r.local, Pos = b[i].Pos, Inl = b[i].Inl });
                continue;
            }
            o.Add(b[i]);
        }
        f.Body = o;
        return true;
    }
}
