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
    /// <summary>By a field's number: only the constructors of its own class store it, and it is `readonly` (the
    /// store is to the object the constructor is making): no call can store that field of an object that was
    /// there before the call.</summary>
    public readonly bool[] CtorOnly = new bool[count + 1];
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
///    A `readonly` field that only constructors of its own class store (FieldHoist.CtorOnly) is asked about the
///    loop's own code alone: a constructor stores to the object it is making, which is not the object the loop
///    had before it started, unless the loop is the constructor's own code;
///  - the length of an array in such a local (it never changes).
/// The read before the loop cannot fail: the local holds null or an object (the loop itself reads a field of it, and
/// no code of the loop assigns it), and a field's offset from either is inside the memory. The loop's own null check
/// stays where it was, so a NullReferenceException comes at the same place, after the same effects; what the read
/// got from a null is not used, but as the address of the next read before the loop (`a.B.C`: B read once, then C
/// of it): the memory after address 0 holds the program's data, and a word of it taken for a reference would send
/// that read anywhere, outside the memory too. A word-sized field read from a null is therefore made null (a
/// select on the local), and every read before a loop is from null or from an object. LoopLoads.cs, which runs
/// before this and reads before its loops as well, does the same while this pass is on (its `nullSafe`).
/// A place (a local, a load, an offset) is read before the loop only when one of its loads stands in no `if` of the
/// loop, so that it runs on every turn that gets that far: a loop whose arms each read another field (a parser's
/// loop over a switch) would pay for all of them at every entry (the self-hosted compiler ran a tenth slower that
/// way). The place's loads in the arms then take the value too.
/// And a loop that takes no turn must not pay for what its turns read (a recursive walk over a tree is entered
/// once a node, and most nodes are leaves: with every read before the loop the tree scene's walk was a tenth
/// slower). A place the loop reads before its first test, on every entry (`i &lt; list.Count`: the size), is read
/// before the loop as it is; a place it reads only after that test (the list's array, its length, the fields the
/// body adds up) is read behind a copy of the test, `test; if (it goes on) { reads }`, put before the loop: the
/// test's instructions are copied when they write no memory, call nothing (a check that throws aside) and give
/// no local a value made from the local's own, so that running them twice is running them once; the loop keeps
/// its own test. A loop whose first instructions are no such test (an enumerator's MoveNext) reads before it
/// only what it reads on every entry. CS2WASM_FIELD_HOIST_ALWAYS=1 (measurement): every place before the loop
/// as it is. CS2WASM_FIELD_HOIST_WITHOUT=test (measurement; the module is right): no copy of the test, only
/// what is read on every entry.
/// The local may be one the loop assigns, when it is a copy of one the loop does not (`local.get L; local.set t`:
/// an inlined callee's parameter, the null check's second local) made before every read: Source.
/// A throw that a handler inside the loop takes, after which the loop goes on: in a program with filters the catch
/// site calls rt.first_pass itself (FunctionCompiler.EmitHandlerDecision), which the calls' rule sees as the call
/// it is, with every filter's stores; a state machine's try statements are written another way, and its function
/// is left alone (WasmFunction.NoFieldHoist, set by FunctionCompiler.FinishMachine).
/// The second stage (Reuse) shares a place's loads in a straight line.
/// CS2WASM_NO_FIELD_HOIST=1: no load has a tag, and this pass finds nothing. CS2WASM_FIELD_HOIST_DEBUG=1: each
/// candidate and why not. CS2WASM_FIELD_HOIST_STATS=1: the counts.
/// </summary>
static class FieldLoads
{
    static readonly bool s_debug = Environment.GetEnvironmentVariable("CS2WASM_FIELD_HOIST_DEBUG") == "1";
    static readonly bool s_noLength = Environment.GetEnvironmentVariable("CS2WASM_NO_LENGTH_HOIST") == "1";
    /// <summary>CS2WASM_FIELD_HOIST_ALWAYS=1 (measurement): a place whose loads all stand in an `if` of the loop is read before it too.</summary>
    static readonly bool s_always = Environment.GetEnvironmentVariable("CS2WASM_FIELD_HOIST_ALWAYS") == "1";
    internal static int Hoisted, HoistedLengths, Guarded;
    /// <summary>An `if` this pass put before a loop (its Imm2): the loop's test, and behind it the reads of the
    /// places the loop reads only after its test.</summary>
    const long Guard = 0x46484c44;
    /// <summary>CS2WASM_FIELD_HOIST_WITHOUT=a,b (for the proof of tests 230 to 234 only: the module it makes is
    /// wrong): the pass without one of its conditions, so that the test's loop for that condition can be seen to
    /// print something else. address, walk (the other fields of an object one field of which has its address taken),
    /// reflection, layout, volatile, threads (Driver.PrepareFieldLoads); loop, calls,
    /// indirect, assigned, dominance, null, twice (here: a test that cannot be run twice is copied all the same); merge,
    /// inline (FieldHoist); machine (FunctionCompiler.FinishMachine);
    /// constructors: a field only constructors store is asked about calls like any other (this one makes the pass
    /// weaker, not wrong).</summary>
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
            else if (b[i].Op is Op.Loop or Op.If && b[i].Imm2 != 0) b[i] = b[i] with { Imm2 = 0 };
        }
    }

    /// <summary>Why a call of g may store the field with this number; null: it cannot.</summary>
    static string? CallKills(FieldHoist? h, WasmFunction g, int id, int depth)
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
            if (x.Op == Op.Call && x.Ref is WasmFunction c) { if (!ReferenceEquals(c, g) && CallKills(h, c, id, depth + 1) is { } w) return w; }
            else if (x.Op == Op.CallIndirect) { if (h.Stores.Indirect(x.Ref as FuncType, id)) return "an indirect call in " + g.Name; }
        }
        return null;
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
                else if (!h.CtorOnly[id] || Without("constructors"))
                {
                    foreach (var g in li.Calls) if ((why = CallKills(h, g, id, 0)) != null) break;
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
            // The loads that qualify, by place (the local, the load, the offset). A place is read before the loop
            // only when one of its loads runs on every turn that gets that far: it stands in no `if` of the loop
            // (a load on a path few turns take, of a loop that is entered often, would be paid for at every entry:
            // a parser's loop over a switch reads a different field in each arm). Its other loads, in the arms,
            // then take the same value.
            Dictionary<(int, Op, long), int>? slots = null;
            List<(int local, Op op, long off, Instr load, bool length, bool always, bool entry, List<(int at, int form)> uses)>? found = null;
            int ifs = 0;
            // The loop's first instructions, as far as they run one after the other on every entry (a check that
            // throws between them counts as running through): lineEnd, the first that is not one of them. When that
            // is the loop's test (a br_if that leaves it) and nothing before it writes memory or gives a local a
            // value made from the local's own (so that running them twice is running them once): testAt.
            int lineEnd = l + 1, testAt = -1;
            {
                bool twice = true, check = !Without("twice");
                int height = 0;
                HashSet<int>? set = null, readFirst = null;
                for (; lineEnd < e; lineEnd++)
                {
                    var x = b[lineEnd];
                    if (x.Op == Op.If && x.Ref == null && lineEnd + 2 < e && Throws(b[lineEnd + 1]) && (b[lineEnd + 2].Op == Op.End || lineEnd + 3 < e && b[lineEnd + 2].Op == Op.Unreachable && b[lineEnd + 3].Op == Op.End))
                    { height--; lineEnd += b[lineEnd + 2].Op == Op.End ? 2 : 3; if (height < 0) { lineEnd++; break; } continue; }
                    if (x.Op == Op.BrIf) { if (x.Imm >= 1 && height == 1 && twice && !Without("test")) testAt = lineEnd; break; }
                    if (x.Op == Op.Call) { if (nonNull != null && ReferenceEquals(x.Ref, nonNull) && height >= 1) continue; break; }
                    if (x.Op is Op.CallIndirect or Op.GlobalSet or Op.MemoryGrow or Op.MemoryCopy or Op.MemoryFill or Op.MemoryInit or Op.DataDrop or Op.V128Store || (int)x.Op >= 0xFC00) break;
                    if (Locals.PlainEffect(x) is not { } eff) break;
                    if (x.Op >= Op.I32Store && x.Op <= Op.I64Store32) twice = !check;
                    else if (x.Op == Op.LocalGet) { if (set == null || !set.Contains((int)x.Imm)) (readFirst ??= []).Add((int)x.Imm); }
                    else if (x.Op is Op.LocalSet or Op.LocalTee) { if (readFirst != null && readFirst.Contains((int)x.Imm)) twice = !check; (set ??= []).Add((int)x.Imm); }
                    height += eff.push - eff.pop;
                    if (height < 0) break;
                }
            }
            for (int i = l + 1; i < e; i++)
            {
                var x = b[i];
                if (x.Op == Op.If) { ifs++; continue; }
                if (x.Op == Op.End) { if (b[opener[i]].Op == Op.If) ifs--; continue; }
                if (i < l + 2 || x.Ref == null || !IsLoad(x.Op)) continue;
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
                if (s_debug) Console.Error.WriteLine($"field hoist {f.Name} @{l} {(tag != null ? tag.Key : "length")}: {why ?? (i < lineEnd || s_always ? "yes" : ifs != 0 ? "yes, in an if" : testAt >= 0 ? "yes, after the test" : "yes, but not on every entry")}");
                if (why != null) continue;
                // (two loads of one place, each allowed by its own field: one value)
                var slotKey = (baseLocal, x.Op, x.Imm);
                if (!(slots ??= []).TryGetValue(slotKey, out int k)) { slots[slotKey] = k = (found ??= []).Count; found.Add((baseLocal, x.Op, x.Imm, x, length, false, false, [])); }
                var fnd = found![k];
                fnd.uses.Add((i, form));
                if (ifs == 0 || s_always) fnd.always = true;
                if (i < lineEnd || s_always) fnd.entry = true;
                found[k] = fnd;
            }
            if (found == null) continue;
            // (before the block the loop leaves by, when it opens right before the loop: `block; loop` stays together)
            int at = l;
            while (at > 0 && b[at - 1].Op == Op.Block && b[at - 1].Ref == null) at--;
            // the reads behind the test: inside the `if` an earlier round made for this loop, or a new one (below)
            List<Instr>? behind = null;
            bool newGuard = false;
            foreach (var (baseLocal, _, _, load, length, always, entry, uses) in found)
            {
                if (!always || !entry && testAt < 0) continue;
                int hl = firstLocal + f.Locals.Count;
                f.Locals.Add(LoadType(load.Op));
                List<Instr> pre;
                if (entry) pre = (before ??= []).TryGetValue(at, out var list) ? list : before[at] = [];
                else if (behind != null) pre = behind;
                else if (at > 0 && b[at - 1].Op == Op.End && b[opener[at - 1]] is { Op: Op.If, Imm2: Guard }) pre = behind = (before ??= []).TryGetValue(at - 1, out var list2) ? list2 : before[at - 1] = [];
                else { pre = behind = []; newGuard = true; }
                pre.Add(new Instr { Op = Op.LocalGet, Imm = baseLocal, Pos = -1 });
                pre.Add(load with { Pos = -1, Inl = 0 });
                // (a word read from a null is no reference: null in its place, so that a field read through it in
                // turn, before the loop as well, is read from null or from an object)
                if (!length && load.Op == Op.I32Load && !Without("null"))
                {
                    pre.Add(new Instr { Op = Op.I32Const, Imm = 0, Pos = -1 });
                    pre.Add(new Instr { Op = Op.LocalGet, Imm = baseLocal, Pos = -1 });
                    pre.Add(new Instr { Op = Op.Select, Pos = -1 });
                }
                pre.Add(new Instr { Op = Op.LocalSet, Imm = hl, Pos = -1 });
                if (length) HoistedLengths++; else Hoisted++;
                foreach (var (ui, form) in uses) (replace ??= [])[ui] = (hl, form);
            }
            if (newGuard)
            {
                // the loop's test once more before it, as the loop will have it (the reads it makes now come from
                // the locals above), and the reads behind it
                var pre = (before ??= []).TryGetValue(at, out var list) ? list : before[at] = [];
                for (int k = l + 1; k < testAt; k++) Rewritten(k, pre);
                pre.Add(new Instr { Op = Op.I32Eqz, Pos = -1 });
                pre.Add(new Instr { Op = Op.If, Imm2 = Guard, Pos = -1 });
                pre.AddRange(behind!);
                pre.Add(new Instr { Op = Op.End, Pos = -1 });
                Guarded++;
            }
        }
        if (replace == null) return false;
        var o = new List<Instr>(n + 16);
        for (int i = 0; i < n; i++)
        {
            if (before != null && before.TryGetValue(i, out var pre)) o.AddRange(pre);
            Rewritten(i, o);
        }
        f.Body = o;
        return true;

        // The instruction at i as the function will have it: a load that is read before its loop is the local.
        void Rewritten(int i, List<Instr> to)
        {
            // (the read of the local that was the address goes with the load, unless the check between them uses it)
            if (i + 1 < n && b[i].Op == Op.LocalGet && replace!.TryGetValue(i + 1, out var next) && next.form != 2) return;
            if (replace!.TryGetValue(i, out var r))
            {
                if (r.form == 2) to.Add(new Instr { Op = Op.Drop, Pos = b[i].Pos, Inl = b[i].Inl });
                to.Add(new Instr { Op = Op.LocalGet, Imm = r.local, Pos = b[i].Pos, Inl = b[i].Inl });
                return;
            }
            to.Add(b[i]);
        }
    }

    static readonly bool s_noReuse = Environment.GetEnvironmentVariable("CS2WASM_NO_FIELD_REUSE") == "1";
    internal static int Reused;

    sealed class Avail
    {
        public int Base; public Op Op; public long Off; public FieldTag? Tag;
        /// <summary>Where the load is; the order it was found in (a block drops what was found inside it).</summary>
        public int At, Seq;
    }

    /// <summary>
    /// The second read of a field (stage B): a load of the field of the object in a local, when the same load of
    /// the same local ran before on every way here, the local was not assigned since, and nothing between the two
    /// can store the field, is the value the first one got (kept in a local). What can store it between them:
    ///  - the function's own code, where the two are: the set of the loop they are both in, or the function's when
    ///    they are in none (no field with a tag is stored by code without a mark, so a store instruction between
    ///    them that is not in that set is to another place);
    ///  - a call between them, by its summary (not asked for a field only constructors store: Run's rule).
    /// "On every way here": the first load stands in a block still open at the second (what a block found inside
    /// it is dropped at its end, and at an `else`; what was killed inside stays killed after it, whichever way the
    /// block was left). A loop starts with nothing (its turns come back to its start) and leaves nothing. A local
    /// that is a copy of another (`local.get L; local.set t`) stands for it while neither is assigned.
    /// An array's length likewise, with nothing that can store it.
    /// CS2WASM_NO_FIELD_REUSE=1: off.
    /// </summary>
    public static bool Reuse(WasmFunction f, FieldHoist? h, WasmFunction? nonNull)
    {
        if (s_noReuse) return false;
        var b = f.Body;
        int n = b.Count;
        int cands = 0;
        for (int i = 0; i < n && cands < 2; i++) if (b[i].Ref != null && IsLoad(b[i].Op) && (b[i].Ref is FieldTag || ReferenceEquals(b[i].Ref, Loops.Length))) cands++;
        if (cands < 2) return false;
        var avail = new List<Avail>();
        var copies = new List<(int local, int root, int seq)>();
        // the blocks open here: the order number when each began, whether it is a loop, the loop around it
        var blocks = new Stack<(int seq, bool isLoop, int outer)>();
        int seq = 0, loop = -1;
        Dictionary<int, int>? keep = null;
        Dictionary<int, (int local, int form)>? replace = null;
        int firstLocal = f.Type.Params.Length;
        bool ctorRule = !Without("constructors");
        int Root(int t) { foreach (var c in copies) if (c.local == t) return c.root; return t; }
        for (int i = 0; i < n; i++)
        {
            var x = b[i];
            switch (x.Op)
            {
                case Op.Loop:
                    avail.Clear(); copies.Clear();
                    blocks.Push((seq, true, loop)); loop = i;
                    continue;
                case Op.If:
                    // (a check that throws: its body does not come back, nothing is found or killed in it)
                    if (i + 2 < n && Throws(b[i + 1]) && (b[i + 2].Op == Op.End || i + 3 < n && b[i + 2].Op == Op.Unreachable && b[i + 3].Op == Op.End)) { i += b[i + 2].Op == Op.End ? 2 : 3; continue; }
                    blocks.Push((seq, false, loop));
                    continue;
                case Op.Block or Op.TryTable:
                    blocks.Push((seq, false, loop));
                    continue;
                case Op.Else:
                    if (blocks.Count > 0) { int s0 = blocks.Peek().seq; avail.RemoveAll(a => a.Seq > s0); copies.RemoveAll(c => c.seq > s0); }
                    continue;
                case Op.End:
                    if (blocks.Count > 0)
                    {
                        var (s0, isLoop, outer) = blocks.Pop();
                        if (isLoop) { avail.Clear(); copies.Clear(); loop = outer; }
                        else { avail.RemoveAll(a => a.Seq > s0); copies.RemoveAll(c => c.seq > s0); }
                    }
                    continue;
                case Op.LocalSet or Op.LocalTee:
                    {
                        int v = (int)x.Imm;
                        int root = i > 0 && b[i - 1].Op == Op.LocalGet && b[i - 1].Imm != v ? Root((int)b[i - 1].Imm) : -1;
                        if (avail.Count > 0) avail.RemoveAll(a => a.Base == v);
                        if (copies.Count > 0) copies.RemoveAll(c => c.local == v || c.root == v);
                        if (root >= 0 && root != v && copies.Count < 64) copies.Add((v, root, ++seq));
                        continue;
                    }
                case Op.Call:
                    if (avail.Count > 0 && x.Ref is WasmFunction g && !ReferenceEquals(g, nonNull) && !Throws(x))
                        avail.RemoveAll(a =>
                        {
                            if (a.Tag == null) return false;
                            foreach (int id in a.Tag.Ids) if (!(ctorRule && h!.CtorOnly[id]) && CallKills(h, g, id, 0) != null) return true;
                            return false;
                        });
                    continue;
                case Op.CallIndirect:
                    if (avail.Count > 0 && !Without("indirect"))
                    {
                        var sig = x.Ref as FuncType;
                        avail.RemoveAll(a =>
                        {
                            if (a.Tag == null) return false;
                            foreach (int id in a.Tag.Ids) if (!(ctorRule && h!.CtorOnly[id]) && h!.Stores.Indirect(sig, id)) return true;
                            return false;
                        });
                    }
                    continue;
            }
            if (x.Ref == null || !IsLoad(x.Op)) continue;
            var tag = x.Ref as FieldTag;
            if (tag == null && !ReferenceEquals(x.Ref, Loops.Length)) continue;
            if (tag == null && s_noLength) continue;
            int t, form;
            if (i >= 1 && b[i - 1].Op == Op.LocalGet) { t = (int)b[i - 1].Imm; form = 1; }
            else if (i >= 2 && b[i - 1].Op == Op.Call && nonNull != null && ReferenceEquals(b[i - 1].Ref, nonNull) && b[i - 2].Op == Op.LocalGet) { t = (int)b[i - 2].Imm; form = 2; }
            else continue;
            if (x.Imm < 0 || x.Imm >= 32768) continue;
            int root0 = Root(t);
            Avail? found = null;
            foreach (var a in avail) if (a.Base == root0 && a.Op == x.Op && a.Off == x.Imm && ReferenceEquals(a.Tag, tag)) { found = a; break; }
            if (found != null)
            {
                keep ??= [];
                if (!keep.TryGetValue(found.At, out int hl)) { hl = firstLocal + f.Locals.Count; f.Locals.Add(LoadType(x.Op)); keep[found.At] = hl; }
                (replace ??= [])[i] = (hl, form);
                Reused++;
                if (s_debug) Console.Error.WriteLine($"field reuse {f.Name} @{i} {(tag != null ? tag.Key : "length")}: the load at {found.At}");
                continue;
            }
            if (tag != null)
            {
                // (the function's own stores: the set of the loop both loads will be in, or the function's)
                if (h == null || f.NoFieldHoist || tag.Ids.Length == 0) continue;
                int set = loop >= 0 ? (int)b[loop].Imm2 : f.FieldStoreSet;
                bool stored = false;
                if (!Without("loop")) foreach (int id in tag.Ids) if (h.Contains(set, id)) { stored = true; break; }
                if (stored) continue;
            }
            if (avail.Count >= 24) avail.RemoveAt(0);
            avail.Add(new Avail { Base = root0, Op = x.Op, Off = x.Imm, Tag = tag, At = i, Seq = ++seq });
        }
        if (replace == null) return false;
        var o = new List<Instr>(n + 8);
        for (int i = 0; i < n; i++)
        {
            if (i + 1 < n && b[i].Op == Op.LocalGet && replace.TryGetValue(i + 1, out var next) && next.form != 2) continue;
            if (replace.TryGetValue(i, out var r))
            {
                if (r.form == 2) o.Add(new Instr { Op = Op.Drop, Pos = b[i].Pos, Inl = b[i].Inl });
                o.Add(new Instr { Op = Op.LocalGet, Imm = r.local, Pos = b[i].Pos, Inl = b[i].Inl });
                continue;
            }
            o.Add(b[i]);
            if (keep!.TryGetValue(i, out int kl)) o.Add(new Instr { Op = Op.LocalTee, Imm = kl, Pos = b[i].Pos, Inl = b[i].Inl });
        }
        f.Body = o;
        return true;
    }
}
