using System.Text;

namespace Cs2Wasm.Wasm;

/// <summary>
/// Functions whose bodies differ only in some i32 constants (the type ids, static addresses and table indices of a
/// generic method's instantiations over reference types) and in which member of such a group they call: one body for
/// the group, the differing constants its last parameters; the members become stubs that pass theirs. Traces stay
/// exact: a group of one frame name keeps it on the shared body (the stubs are hidden, and direct calls go to the
/// shared body); a group whose names differ (a second pass, over what the first left) keeps each member as its frame,
/// a stub every call goes through, and the shared body lends its place (lines, inlined calls) to the stub's frame
/// (cs2wasm.frames lists such bodies; host/engine.mjs). Positions in the library's files are not compared: a trace
/// shows no lines there. CS2WASM_SHARE=0 turns it off, CS2WASM_SHARE_LEND=0 the second pass, CS2WASM_SHARE_STATS=1
/// prints what is shared.
/// </summary>
static class ShareConstants
{
    /// <summary>The groups (partition refinement: a function's key masks its i32 constants and names each call by the
    /// callee's group, until the groups stop splitting).</summary>
    public static List<List<WasmFunction>> Groups(List<WasmFunction> fns, HashSet<WasmFunction> mayThrow, Func<int, bool>? posShown, bool lend, out Dictionary<WasmFunction, int> cls)
    {
        var live = fns.Where(f => !f.IsImport && f.ForwardTo == null && f.Body.Count > 0).ToList();
        cls = [];
        foreach (var f in live) cls[f] = 0;
        int count = 1;
        for (int round = 0; round < 12; round++)
        {
            var keys = new Dictionary<string, int>();
            var next = new Dictionary<WasmFunction, int>();
            foreach (var f in live)
            {
                var k = Key(f, cls, mayThrow, posShown, lend);
                if (!keys.TryGetValue(k, out int id)) keys[k] = id = keys.Count;
                next[f] = id;
            }
            cls = next;
            if (keys.Count == count) break;
            count = keys.Count;
        }
        var byCls = new Dictionary<int, List<WasmFunction>>();
        foreach (var f in live) { if (!byCls.TryGetValue(cls[f], out var l)) byCls[cls[f]] = l = []; l.Add(f); }
        return byCls.Values.Where(l => l.Count > 1).ToList();
    }

    static string Key(WasmFunction f, Dictionary<WasmFunction, int> cls, HashSet<WasmFunction> mayThrow, Func<int, bool>? posShown, bool lend)
    {
        var sb = new StringBuilder();
        foreach (var p in f.Type.Params) sb.Append((char)p);
        sb.Append('|'); foreach (var r in f.Type.Results) sb.Append((char)r);
        sb.Append('|'); foreach (var l in f.Locals) sb.Append((char)l);
        // (a frame of a trace keeps its name and lines: the members share the lines a trace shows; their names too,
        // unless their frames lend: then each member stays a frame, a stub calling the shared body)
        bool framed = f.FrameName != null && mayThrow.Contains(f) && !s_ignoreFrames;
        if (framed) { sb.Append("#F"); if (!lend) sb.Append(f.FrameName); }
        if (f.Exports.Count > 0) sb.Append("#export").Append(f.Order);
        foreach (var ins in f.Body)
        {
            sb.Append(';').Append((int)ins.Op);
            if (ins.Op == Op.I32Const) { sb.Append('k'); continue; }
            sb.Append(',').Append(ins.Imm).Append(',').Append(ins.Imm2);
            if (ins.Inl != 0) sb.Append('@').Append(ins.Inl);
            if (ins.Probes != 0) sb.Append('r').Append(ins.Probes);
            if (framed && !s_ignorePos && ins.Pos >= 0 && (posShown == null || posShown(ins.Pos))) sb.Append('p').Append(ins.Pos);
            switch (ins.Ref)
            {
                case null: break;
                case WasmFunction g when ins.Op == Op.Call:
                    sb.Append(",c").Append(cls.TryGetValue(g, out int c) ? c : -1 - g.Order);
                    if (!cls.ContainsKey(g)) sb.Append(':').Append(g.Name);
                    break;
                case WasmFunction g: sb.Append(",f").Append(g.Order).Append(':').Append(g.Name); break;
                case ValType vt: sb.Append(",v").Append((int)vt); break;
                case FuncType ft: sb.Append(",t").Append(string.Join(".", ft.Params)).Append('>').Append(string.Join(".", ft.Results)); break;
                case int[] arr: sb.Append(",b").Append(string.Join(".", arr)); break;
                case TryTableInfo tti:
                    sb.Append(",tt").Append(tti.BlockType?.ToString());
                    foreach (var cc in tti.Catches) sb.Append(':').Append(cc.Kind).Append(cc.Tag?.Name).Append(cc.Label);
                    break;
                default: sb.Append(",o").Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ins.Ref)); break;
            }
        }
        return sb.ToString();
    }

    static readonly bool s_ignoreFrames = Environment.GetEnvironmentVariable("CS2WASM_SHARE_IGNORE_FRAMES") == "1";
    static readonly bool s_ignorePos = Environment.GetEnvironmentVariable("CS2WASM_SHARE_IGNORE_POS") == "1";

    static bool SameConst(in Instr a, in Instr b) => a.Imm == b.Imm && (ReferenceEquals(a.Ref, b.Ref) || a.Ref != null && a.Ref.Equals(b.Ref));

    /// <summary>The measurement: per group, the constants that differ and the calls whose callee differs.</summary>
    public static void Stats(List<WasmFunction> fns, HashSet<WasmFunction> mayThrow, Func<int, bool>? posShown, bool lend)
    {
        var groups = Groups(fns, mayThrow, posShown, lend, out var cls);
        var callers = new Dictionary<WasmFunction, int>();
        foreach (var f in fns) if (!f.IsImport) foreach (var ins in f.Body) if (ins.Op == Op.Call && ins.Ref is WasmFunction g) callers[g] = callers.GetValueOrDefault(g) + 1;
        long saved = 0, members = 0, siteCost = 0;
        var kHist = new SortedDictionary<int, (int groups, long instrs)>();
        foreach (var g in groups)
        {
            var r = g[0];
            int kConst = 0, kCall = 0;
            for (int i = 0; i < r.Body.Count; i++)
            {
                var ri = r.Body[i];
                if (ri.Op == Op.I32Const) { if (g.Any(m => !SameConst(m.Body[i], ri))) kConst++; }
                else if (ri.Op == Op.Call && g.Any(m => !ReferenceEquals(m.Body[i].Ref, ri.Ref))) kCall++;
            }
            int k = kConst + kCall; // (a differing call: its callee's constants, at least one)
            long body = r.Body.Count;
            saved += (g.Count - 1) * body;
            members += g.Count;
            long sites = g.Sum(m => (long)callers.GetValueOrDefault(m));
            siteCost += sites * k;
            var h = kHist.GetValueOrDefault(Math.Min(k, 9));
            kHist[Math.Min(k, 9)] = (h.groups + 1, h.instrs + (g.Count - 1) * body);
        }
        if (Environment.GetEnvironmentVariable("CS2WASM_SHARE_EXAMPLES") == "1")
            foreach (var g in groups.Where(g => g.Select(m => m.FrameName).Distinct().Count() > 1).Take(8))
                Console.Error.WriteLine("  frames: " + string.Join(" | ", g.Select(m => m.FrameName ?? "-").Distinct().Take(3)) + "   (" + string.Join(", ", g.Take(2).Select(m => m.Name)) + ")");
        long total = fns.Where(f => !f.IsImport).Sum(f => (long)f.Body.Count);
        Console.Error.WriteLine($"share: {groups.Count} groups of {members} functions; {saved} of {total} instructions would go ({100.0 * saved / total:F1}%), call sites would push {siteCost} constants");
        foreach (var (k, (n, ins)) in kHist) Console.Error.WriteLine($"  {(k == 9 ? "9+" : k.ToString()),3} differing: {n,6} groups, {ins,9} instructions");
    }

    static readonly int MaxSlots = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_SHARE_MAX"), out var sm) ? sm : 6;

    /// <summary>A group and its differing values: per slot, each member's constant (an i32.const instruction).</summary>
    sealed class Plan
    {
        public required List<WasmFunction> Members;
        public required Dictionary<WasmFunction, int> Index;
        public List<Instr[]> Columns = [];              // [slot][member]
        public Dictionary<int, int> ConstSlot = [];     // body position of a differing constant: its slot
        public Dictionary<int, int[]> CallSlots = [];   // body position of a differing call: per callee slot, own slot, or -1 - constant
        public Dictionary<int, Instr[]> CallConsts = []; // body position: the callee slots whose value is the same for every member
        public WasmFunction? Shared;
        public bool Dead;
        public bool Framed; // the members are frames of a trace
        public bool Lend;   // the members' frame names differ: each stays a frame (a stub that every call goes through)
    }

    /// <summary>Shares the groups (WasmModule, on the final bodies). Returns the number of members made stubs.</summary>
    public static int Run(List<WasmFunction> fns, HashSet<WasmFunction> mayThrow, Func<int, bool>? posShown, bool lend, Func<string, FuncType, WasmFunction> add)
    {
        var groups = Groups(fns, mayThrow, posShown, lend, out _);
        var plans = new Dictionary<WasmFunction, Plan>();
        foreach (var g in groups)
        {
            var pl = new Plan { Members = g, Index = g.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i) };
            // (members in a trace's frames all have one; others never are on the stack when one is taken: a function
            // there calls an import, which may throw)
            pl.Framed = g[0].FrameName != null && mayThrow.Contains(g[0]);
            pl.Lend = pl.Framed && g.Any(m => m.FrameName != g[0].FrameName);
            foreach (var m in g) plans[m] = pl;
        }
        var all = plans.Values.Distinct().ToList();
        // the slots, to a fixpoint: a differing call needs its callee group's values for each member's callee
        bool changed = true;
        for (int round = 0; changed && round < 30; round++)
        {
            changed = false;
            foreach (var pl in all)
            {
                if (pl.Dead) continue;
                int before = pl.Columns.Count;
                if (!Slots(pl, plans)) { pl.Dead = true; changed = true; continue; }
                if (pl.Columns.Count != before) changed = true;
            }
        }
        var calls = new Dictionary<WasmFunction, int>();
        foreach (var f in fns) if (!f.IsImport) foreach (var ins in f.Body) if (ins.Op == Op.Call && ins.Ref is WasmFunction cg) calls[cg] = calls.GetValueOrDefault(cg) + 1;
        foreach (var pl in all) if (!pl.Dead && (pl.Columns.Count > MaxSlots || !Pays(pl, calls))) pl.Dead = true;
        // (a group that calls members of an unshared group at differing places is not shared either)
        for (bool again = true; again;)
        {
            again = false;
            foreach (var pl in all)
            {
                if (pl.Dead) continue;
                foreach (var i in pl.CallSlots.Keys)
                    if (!plans.TryGetValue((WasmFunction)pl.Members[0].Body[i].Ref!, out var cp) || cp.Dead) { pl.Dead = true; again = true; break; }
            }
        }
        foreach (var pl in all)
        {
            if (pl.Dead) continue;
            var r = pl.Members[0];
            var ft = new FuncType([.. r.Type.Params, .. Enumerable.Repeat(ValType.I32, pl.Columns.Count)], r.Type.Results);
            pl.Shared = add("shared " + r.Name, ft);
        }
        // the shared bodies
        foreach (var pl in all)
        {
            if (pl.Dead) continue;
            var r = pl.Members[0];
            int np = r.Type.Params.Length, k = pl.Columns.Count;
            var sh = pl.Shared!;
            sh.FrameName = pl.Framed && !pl.Lend ? r.FrameName : null; sh.LendsLines = pl.Lend; sh.ProgramCode = r.ProgramCode; sh.Locals.AddRange(r.Locals);
            var b = new List<Instr>(r.Body.Count);
            for (int i = 0; i < r.Body.Count; i++)
            {
                var ins = r.Body[i];
                if (ins.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee && ins.Imm >= np) ins = ins with { Imm = ins.Imm + k };
                if (pl.ConstSlot.TryGetValue(i, out int slot)) { b.Add(ins with { Op = Op.LocalGet, Imm = np + slot, Ref = null }); continue; }
                if (pl.CallSlots.TryGetValue(i, out var map))
                {
                    var callee = plans[(WasmFunction)ins.Ref!];
                    for (int c = 0; c < map.Length; c++)
                        b.Add(map[c] >= 0 ? new Instr { Op = Op.LocalGet, Imm = np + map[c], Pos = ins.Pos, Inl = ins.Inl } : pl.CallConsts[i][-1 - map[c]] with { Pos = ins.Pos, Inl = ins.Inl });
                    b.Add(ins with { Ref = callee.Shared });
                    continue;
                }
                // (a call of one member of a shared group, the same for every member: its values, the shared body)
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g && plans.TryGetValue(g, out var gp) && !gp.Dead && !gp.Lend)
                {
                    foreach (var col in gp.Columns) b.Add(col[gp.Index[g]] with { Pos = ins.Pos, Inl = ins.Inl });
                    b.Add(ins with { Ref = gp.Shared });
                    continue;
                }
                b.Add(ins);
            }
            sh.Body = b;
        }
        // every other direct call of a member: its values and the shared body
        var stubsOf = new HashSet<WasmFunction>(all.Where(p => !p.Dead).SelectMany(p => p.Members));
        var sharedNow = new HashSet<WasmFunction>(all.Where(p => !p.Dead).Select(p => p.Shared!));
        foreach (var f in fns)
        {
            if (f.IsImport || f.Body.Count == 0 || stubsOf.Contains(f) || sharedNow.Contains(f)) continue;
            List<Instr>? o = null;
            for (int i = 0; i < f.Body.Count; i++)
            {
                var ins = f.Body[i];
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g && plans.TryGetValue(g, out var pl) && !pl.Dead && !pl.Lend)
                {
                    o ??= [.. f.Body.Take(i)];
                    foreach (var col in pl.Columns) o.Add(col[pl.Index[g]] with { Pos = ins.Pos, Inl = ins.Inl });
                    o.Add(ins with { Ref = pl.Shared });
                    continue;
                }
                o?.Add(ins);
            }
            if (o != null) f.Body = o;
        }
        // the members: stubs that pass their own values (their identity stays, for the table and the exports), no frame
        int stubs = 0;
        foreach (var pl in all)
        {
            if (pl.Dead) continue;
            foreach (var m in pl.Members)
            {
                int np = m.Type.Params.Length, mi = pl.Index[m];
                var b = new List<Instr>();
                for (int i = 0; i < np; i++) b.Add(new Instr { Op = Op.LocalGet, Imm = i, Pos = -1 });
                foreach (var col in pl.Columns) b.Add(col[mi] with { Pos = -1, Inl = 0 });
                b.Add(new Instr { Op = Op.Call, Ref = pl.Shared, Pos = -1 });
                m.Body = b; m.Locals.Clear();
                if (!pl.Lend) m.FrameName = null;
                stubs++;
            }
        }
        return stubs;
    }

    /// <summary>The group's slots: its differing constants, then for each differing call the callee group's slots in
    /// the callee member each member calls (one slot per distinct column of values). False: not shareable.</summary>
    static bool Slots(Plan pl, Dictionary<WasmFunction, Plan> plans)
    {
        var g = pl.Members; var r = g[0];
        var cols = new List<Instr[]>(); var keyOf = new Dictionary<string, int>();
        int SlotOf(Instr[] col)
        {
            var key = string.Join("|", col.Select(c => c.Imm + ":" + (c.Ref == null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.Ref).ToString())));
            if (!keyOf.TryGetValue(key, out int s)) { keyOf[key] = s = cols.Count; cols.Add(col); }
            return s;
        }
        var constSlot = new Dictionary<int, int>(); var callSlots = new Dictionary<int, int[]>(); var callConsts = new Dictionary<int, Instr[]>();
        for (int i = 0; i < r.Body.Count; i++)
        {
            var ri = r.Body[i];
            if (ri.Op == Op.I32Const)
            {
                if (g.All(m => SameConst(m.Body[i], ri))) continue;
                constSlot[i] = SlotOf([.. g.Select(m => m.Body[i])]);
            }
            else if (ri.Op == Op.Call && ri.Ref is WasmFunction)
            {
                var targets = g.Select(m => (WasmFunction)m.Body[i].Ref!).ToArray();
                if (targets.All(t => ReferenceEquals(t, targets[0]))) continue;
                if (!plans.TryGetValue(targets[0], out var cp) || cp.Dead || targets.Any(t => !plans.TryGetValue(t, out var tp) || tp != cp)) return false;
                // (a frame that lends is called through its stub, which differs here)
                if (cp.Lend) return false;
                // (the group calling itself: each member itself, its slots passed on as they are)
                if (cp == pl && Enumerable.Range(0, g.Count).Any(m => !ReferenceEquals(targets[m], g[m]))) return false;
                var map = new int[cp.Columns.Count];
                var consts = new List<Instr>();
                for (int c = 0; c < cp.Columns.Count; c++)
                {
                    var col = targets.Select(t => cp.Columns[c][cp.Index[t]]).ToArray();
                    if (col.All(x => SameConst(x, col[0]))) { map[c] = -1 - consts.Count; consts.Add(col[0]); }
                    else map[c] = SlotOf(col);
                }
                callSlots[i] = map; callConsts[i] = [.. consts];
            }
        }
        pl.Columns = cols; pl.ConstSlot = constSlot; pl.CallSlots = callSlots; pl.CallConsts = callConsts;
        return true;
    }

    /// <summary>CS2WASM_SHARE_WHY=1 (after sharing): families of functions (names with their type arguments taken
    /// out, one signature) that still have several bodies, and for a pair of them the first instruction that differs.</summary>
    public static void Why(List<WasmFunction> fns)
    {
        static string Strip(string n)
        {
            var sb = new StringBuilder(); int d = 0;
            foreach (var c in n.Split('(')[0]) { if (c == '<') { if (d++ == 0) sb.Append("<>"); } else if (c == '>') d--; else if (d == 0) sb.Append(c); }
            return sb.ToString();
        }
        var fam = new Dictionary<string, List<WasmFunction>>();
        foreach (var f in fns)
        {
            if (f.IsImport || f.Body.Count == 0 || f.ForwardTo != null || !f.Name.Contains('<') || f.Name.StartsWith("shared ")) continue;
            // (a stub: a call of a shared body)
            if (f.Body.Count > 0 && f.Body[^1].Op == Op.Call && f.Body[^1].Ref is WasmFunction s && s.Name.StartsWith("shared ")) continue;
            var k = Strip(f.Name) + " | " + string.Join(",", f.Type.Params) + ">" + string.Join(",", f.Type.Results);
            if (!fam.TryGetValue(k, out var l)) fam[k] = l = [];
            l.Add(f);
        }
        var reasons = new Dictionary<string, (int n, long bytes, string ex)>();
        foreach (var (k, l) in fam)
        {
            if (l.Count < 2) continue;
            long extra = l.Sum(f => (long)f.Body.Count) - l.Max(f => f.Body.Count);
            var a = l[0]; var b = l.FirstOrDefault(x => x != a && x.Body.Count == a.Body.Count) ?? l[1];
            string why;
            if (a.Body.Count != b.Body.Count) why = "length";
            else if (!a.Locals.SequenceEqual(b.Locals)) why = "locals";
            else
            {
                why = "same?";
                for (int i = 0; i < a.Body.Count; i++)
                {
                    var x = a.Body[i]; var y = b.Body[i];
                    if (x.Op != y.Op) { why = "op " + x.Op + "/" + y.Op; break; }
                    if (x.Op == Op.I32Const) continue;
                    if (x.Imm != y.Imm || x.Imm2 != y.Imm2) { why = "imm " + x.Op; break; }
                    if (x.Ref is TryTableInfo ta && y.Ref is TryTableInfo tb)
                    {
                        string TK(TryTableInfo t) => t.BlockType + string.Concat(t.Catches.Select(cc => ":" + cc.Kind + cc.Tag?.Name + cc.Label));
                        if (TK(ta) != TK(tb)) { why = "trytable"; break; }
                    }
                    else if (!ReferenceEquals(x.Ref, y.Ref) && !(x.Ref != null && x.Ref.Equals(y.Ref)))
                    {
                        why = x.Ref is WasmFunction g && y.Ref is WasmFunction h ? "call " + Strip(g.Name).Split(' ')[0] + " / " + Strip(h.Name).Split(' ')[0] : "ref " + x.Op; break;
                    }
                    if (x.Inl != y.Inl) { why = "inl"; break; }
                    if (x.Pos != y.Pos) { why = "pos"; break; }
                }
                if (why == "same?" && a.FrameName != b.FrameName) why = "frame";
            }
            var cat = why.StartsWith("call ") ? "call" : why.StartsWith("op") ? "op" : why;
            var e = reasons.GetValueOrDefault(cat);
            reasons[cat] = (e.n + 1, e.bytes + extra, e.ex ?? (k + "  [" + why + "]  " + a.Name + "  ~  " + b.Name));
            var e2 = reasons.GetValueOrDefault("  " + why);
            reasons["  " + why] = (e2.n + 1, e2.bytes + extra, e2.ex ?? (a.Name + "  ~  " + b.Name));
        }
        foreach (var (r, (n, bytes, ex)) in reasons.Where(kv => !kv.Key.StartsWith("  ")).OrderByDescending(kv => kv.Value.bytes))
            Console.Error.WriteLine($"why: {r,-10} {n,6} families {bytes,9} instructions   e.g. {ex}");
        foreach (var (r, (n, bytes, ex)) in reasons.Where(kv => kv.Key.StartsWith("  ")).OrderByDescending(kv => kv.Value.bytes).Take(40))
            Console.Error.WriteLine($"why: {r,-60} {n,6} {bytes,9}   e.g. {ex}");
        static string Last(string n) { var h = n.Split(' '); var s = h[0]; int i = s.LastIndexOf('.'); return (h.Length > 1 && h[0] is "virtual" or "interface" or "rt.isinst" or "static" ? h[0] + " " : "") + (i >= 0 ? s[(i + 1)..] : s); }
        // per family, the calls one member makes that the other does not (and the reverse), summed over families
        static Dictionary<string, int> Calls(WasmFunction f)
        {
            var d = new Dictionary<string, int>();
            foreach (var ins in f.Body)
            {
                string? k = ins.Ref is WasmFunction g && ins.Op == Op.Call ? Last(Strip(g.Name)) : ins.Op is Op.CallIndirect ? "call_indirect" : ins.Op is Op.TryTable ? "try_table" : null;
                if (k != null) d[k] = d.GetValueOrDefault(k) + 1;
            }
            return d;
        }
        var callDiff = new Dictionary<string, (int fams, long bytes, string ex)>();
        foreach (var (k, l) in fam)
        {
            if (l.Count < 2) continue;
            var ca = l.Select(Calls).ToList();
            var seen = new HashSet<string>();
            for (int i = 0; i < l.Count; i++)
                for (int j = 0; j < l.Count; j++)
                {
                    if (i == j) continue;
                    foreach (var (c, n) in ca[i]) if (ca[j].GetValueOrDefault(c) < n && seen.Add(c))
                    {
                        var e = callDiff.GetValueOrDefault(c);
                        callDiff[c] = (e.fams + 1, e.bytes + l.Sum(f => (long)f.Body.Count) - l.Max(f => f.Body.Count), e.ex ?? (l[i].Name + "  ~  " + l[j].Name));
                    }
                }
        }
        foreach (var (c, (n, bytes, ex)) in callDiff.OrderByDescending(kv => kv.Value.bytes).Take(40))
            Console.Error.WriteLine($"why-calls: {c,-70} {n,6} {bytes,9}   e.g. {ex}");
    }

    /// <summary>Whether sharing saves more than it costs: the bodies but one, against a stub per member and the values
    /// pushed at the calls.</summary>
    static bool Pays(Plan pl, Dictionary<WasmFunction, int> calls)
    {
        int n = pl.Members.Count, body = pl.Members[0].Body.Count, k = pl.Columns.Count;
        // (a stub stays only for a member the table or an export holds: the others' calls all go to the shared body)
        int kept = pl.Lend ? n : pl.Members.Count(m => m.InTable || m.TableRoot || m.Exports.Count > 0);
        long sites = pl.Lend ? 0 : pl.Members.Sum(m => (long)calls.GetValueOrDefault(m));
        long cost = kept * (long)(pl.Members[0].Type.Params.Length + k + 2) + sites * k + body / 4;
        return (long)(n - 1) * body > cost;
    }
}
