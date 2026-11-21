namespace Cs2Wasm.Wasm.Ssa;

/// <summary>
/// Count pairs across blocks and calls (spike of docs/AUDIT.md step 6.4; CS2WASM_IR_PAIRS=1, programs without threads).
///
/// A definition `v; call rt.retain; local.set t` (or local.tee) gives t a count of its own on v. Over the function's
/// control flow, exception edges included, the points where that count is still t's (owned: reached from the
/// definition without passing a release of t) form its region. When
///   - no instruction in the region can lower a count or free an object (Effects.MayDrop: a release of anything, a
///     destruction, a reference store, an arena operation, call_indirect, an import),
///   - every read of t in the region is reached by this definition alone (reaching definitions), except releases,
///   - the region never overwrites t, never returns, and no exception leaves the function from it,
/// then the count is never needed: whatever held v when it was retained still holds it at t's release (nothing in
/// between can take a reference away), so the release would only have taken the count back down, never to zero.
/// The pair goes: the value lives in a fresh local t' (no handler releases it), t's reads in the region read t', t's
/// releases that only this definition reaches go, and when a release reached also by other definitions is left (a
/// join, the function's cleanup handler), t is set to zero at the definition so that it releases nothing on these
/// paths. Nothing is freed at another moment than before; no exception moves (the removed calls cannot throw: a
/// release that cannot free runs nothing).
///
/// With CS2WASM_IR_PAIRS_THROW=1 (measurement), also across what drops only on the way out of the function by an
/// exception: a call that never returns (a throw helper, rt.nonnull's failing case) or the cleanup handler, after
/// which nothing reads v again. There an object can be freed a little earlier during unwinding than before.
/// </summary>
static class CountPairs
{
    static readonly bool s_debug = Environment.GetEnvironmentVariable("CS2WASM_IR_DEBUG") is { Length: > 0 };
    static readonly string? s_dump = Environment.GetEnvironmentVariable("CS2WASM_IR_DUMP") is { Length: > 0 } d ? d : null;
    static readonly bool s_throwPaths = Environment.GetEnvironmentVariable("CS2WASM_IR_PAIRS_THROW") == "1";

    public sealed class Stats
    {
        public int Candidates, Removed, RemovedReleases, SharedReleases;
        public readonly Dictionary<string, int> Rejected = [];
        public readonly Dictionary<string, int> DropBy = [];
        public void Reject(string why) => Rejected[why] = Rejected.GetValueOrDefault(why) + 1;
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"ir pairs: {Candidates} retains into locals, {Removed} removed ({RemovedReleases} releases removed, {SharedReleases} shared releases left)\n");
            foreach (var (k, v) in Rejected.OrderByDescending(x => x.Value)) sb.Append($"  rejected {v,7} {k}\n");
            foreach (var (k, v) in DropBy.OrderByDescending(x => x.Value).Take(40)) sb.Append($"  drop by {v,7} {k}\n");
            return sb.ToString();
        }
    }

    static bool IsRetain(in Instr i, Effects e) => i.Op == Op.Call && e.Retain != null && ReferenceEquals(i.Ref, e.Retain);
    static bool IsRelease(in Instr i, Effects e) => i.Op == Op.Call && e.Release != null && ReferenceEquals(i.Ref, e.Release);

    public static void Dump(WasmFunction f, string when)
    {
        Console.Error.WriteLine($"== {when} {f.Name} ({f.Body.Count} instrs)");
        int depth = 0;
        for (int i = 0; i < f.Body.Count; i++)
        {
            var x = f.Body[i];
            if (x.Op is Op.End or Op.Else) depth--;
            string r = x.Ref switch { WasmFunction g => g.Name, TryTableInfo t => string.Join(",", t.Catches.Select(c => c.Kind + ":" + c.Label)), int[] a => string.Join(",", a), null => "", var o => o.ToString() ?? "" };
            Console.Error.WriteLine($"{i,5} {new string(' ', 2 * Math.Max(depth, 0))}{x.Op} {(x.Imm != 0 || x.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee or Op.I32Const or Op.Br or Op.BrIf ? x.Imm.ToString() : "")} {r}");
            if (x.Op is Op.Block or Op.Loop or Op.If or Op.TryTable or Op.Else) depth++;
        }
    }

    /// <summary>Returns whether the body changed.</summary>
    public static bool Run(WasmFunction f, Effects e, Stats st)
    {
        var b = f.Body;
        int n = b.Count;
        if (e.Retain == null || e.Release == null) return false;
        if (s_dump != null && f.Name.Contains(s_dump)) Dump(f, "before pairs");
        // candidates by local
        Dictionary<int, List<int>>? cands = null;
        for (int i = 0; i + 1 < n; i++)
            if (IsRetain(b[i], e) && b[i + 1].Op is Op.LocalSet or Op.LocalTee)
            {
                int t = (int)b[i + 1].Imm;
                cands ??= [];
                if (!cands.TryGetValue(t, out var l)) cands[t] = l = [];
                l.Add(i + 1);
                st.Candidates++;
            }
        if (cands == null) return false;
        var g = Cfg.Build(b, e.MayThrow);
        int np = f.Type.Params.Length;
        // edits
        var removeAt = new bool[n];
        var renameTo = new Dictionary<int, int>();
        var zeroAfter = new HashSet<int>();
        var cleanup = s_throwPaths ? CleanupHandlers(b, g, e) : null;
        bool any = false;
        foreach (var (t, defsOfCands) in cands)
        {
            // every definition of t: bit 0 is the entry (a parameter's argument, a local's zero), bit k+1 the k-th
            var defs = new List<int>();
            for (int i = 0; i < n; i++) if (b[i].Op is Op.LocalSet or Op.LocalTee && b[i].Imm == t) defs.Add(i);
            int m = defs.Count + 1, w = (m + 63) / 64;
            var defBit = new Dictionary<int, int>();
            for (int k = 0; k < defs.Count; k++) defBit[defs[k]] = k + 1;
            var reach = new ulong[]?[n + 1];
            var own = new ulong[]?[n + 1];
            reach[0] = new ulong[w]; own[0] = new ulong[w];
            reach[0]![0] = 1; own[0]![0] = 0;
            var work = new Stack<int>();
            var inWork = new bool[n + 1];
            work.Push(0); inWork[0] = true;
            var ro = new ulong[w]; var oo = new ulong[w];
            bool Merge(int s, ulong[] r, ulong[] o)
            {
                if (reach[s] == null) { reach[s] = (ulong[])r.Clone(); own[s] = (ulong[])o.Clone(); return true; }
                bool ch = false;
                var rs = reach[s]!; var os = own[s]!;
                for (int k = 0; k < w; k++)
                {
                    ulong a = rs[k] | r[k], c = os[k] | o[k];
                    if (a != rs[k] || c != os[k]) { rs[k] = a; os[k] = c; ch = true; }
                }
                return ch;
            }
            while (work.Count > 0)
            {
                int i = work.Pop(); inWork[i] = false;
                if (i >= n) continue;
                var ri = reach[i]!; var oi = own[i]!;
                ri.CopyTo(ro, 0); oi.CopyTo(oo, 0);
                var x = b[i];
                if (x.Op is Op.LocalSet or Op.LocalTee && x.Imm == t)
                {
                    Array.Clear(ro); Array.Clear(oo);
                    int k = defBit[i];
                    ro[k >> 6] |= 1UL << (k & 63); oo[k >> 6] |= 1UL << (k & 63);
                }
                else if (x.Op == Op.LocalGet && x.Imm == t && i + 1 < n && IsRelease(b[i + 1], e)) Array.Clear(oo);
                foreach (var s in g.Succ[i]) if (Merge(s, ro, oo) && !inWork[s]) { inWork[s] = true; work.Push(s); }
                foreach (var s in g.Exc[i]) if (Merge(s, ri, oi) && !inWork[s]) { inWork[s] = true; work.Push(s); }
            }
            foreach (int def in defsOfCands)
            {
                int d = defBit[def];
                ulong dm = 1UL << (d & 63); int dw = d >> 6;
                bool Has(ulong[]? a) => a != null && (a[dw] & dm) != 0;
                bool Only(ulong[]? a)
                {
                    if (a == null || (a[dw] & dm) == 0) return false;
                    for (int k = 0; k < w; k++) if (a[k] != (k == dw ? dm : 0)) return false;
                    return true;
                }
                string? why = null;
                var rem = new List<int>(); var ren = new List<int>(); bool shared = false;
                if (Has(own[n])) why = "reaches the function's end owned";
                for (int j = 0; j < n && why == null; j++)
                {
                    var x = b[j];
                    bool owned = Has(own[j]);
                    if (x.Op == Op.LocalGet && x.Imm == t && Has(reach[j]))
                    {
                        bool rel = j + 1 < n && IsRelease(b[j + 1], e);
                        if (!owned) { why = rel ? "released twice" : "read after its release"; break; }
                        if (rel) { if (Only(reach[j])) rem.Add(j); else shared = true; }
                        else if (Only(reach[j])) ren.Add(j);
                        else why = "a read other definitions reach too";
                        continue;
                    }
                    if (!owned) continue;
                    if (x.Op is Op.LocalSet or Op.LocalTee && x.Imm == t) { why = "overwritten while owned (moved or kept)"; break; }
                    if (x.Op == Op.Return) { why = "returned (or left) while owned"; break; }
                    if (x.Op == Op.Br && g.Succ[j].Length == 1 && g.Succ[j][0] == n) { why = "returned (or left) while owned"; break; }
                    if (g.Throws[j] && g.Exc[j].Length == 0) { why = "an exception leaves the function while owned"; break; }
                    if (cleanup != null && cleanup.Contains(j)) continue;
                    if (e.Drops(x))
                    {
                        if (cleanup != null && x.Op == Op.Call && x.Ref is WasmFunction ng && (ng == e.NonNull || e.NeverReturns.Contains(ng) && ArgsConstant(b, j, ng))
                            && g.Exc[j].All(c => cleanup.Contains(c))) continue;
                        why = "a call that can drop references in between";
                        var nm = x.Ref is WasmFunction dg ? dg.Name : "call_indirect";
                        st.DropBy[nm] = st.DropBy.GetValueOrDefault(nm) + 1;
                        break;
                    }
                }
                if (why == null && rem.Count == 0 && !shared) why = "no release";
                if (why != null)
                {
                    st.Reject(why);
                    if (s_debug && f.Name.Contains(Environment.GetEnvironmentVariable("CS2WASM_IR_DEBUG")!)) Console.Error.WriteLine($"ir pairs: {f.Name} local {t} at {def}: {why}");
                    continue;
                }
                int tp = np + f.Locals.Count;
                f.Locals.Add(t < np ? f.Type.Params[t] : f.Locals[t - np]);
                removeAt[def - 1] = true;
                renameTo[def] = tp;
                foreach (var j in ren) renameTo[j] = tp;
                foreach (var j in rem) { removeAt[j] = true; removeAt[j + 1] = true; }
                if (shared) zeroAfter.Add(def);
                st.Removed++; st.RemovedReleases += rem.Count; if (shared) st.SharedReleases++;
                any = true;
            }
        }
        if (!any) return false;
        var o2 = new List<Instr>(n + 8);
        for (int i = 0; i < n; i++)
        {
            if (removeAt[i]) continue;
            var x = b[i];
            if (renameTo.TryGetValue(i, out int tp))
            {
                int t = (int)x.Imm;
                o2.Add(x with { Imm = tp });
                if (zeroAfter.Contains(i))
                {
                    o2.Add(new Instr { Op = Op.I32Const, Imm = 0, Pos = -1, Inl = x.Inl });
                    o2.Add(new Instr { Op = Op.LocalSet, Imm = t, Pos = -1, Inl = x.Inl });
                }
                continue;
            }
            o2.Add(x);
        }
        f.Body = o2;
        if (s_dump != null && f.Name.Contains(s_dump)) Dump(f, "after pairs");
        return true;
    }

    /// <summary>A call's arguments are all constants (the throw helpers' messages): it reads no local.</summary>
    static bool ArgsConstant(List<Instr> b, int j, WasmFunction g)
    {
        int k = g.Type.Params.Length;
        for (int q = j - 1; k > 0; q--, k--) if (q < 0 || b[q].Op is not (Op.I32Const or Op.I64Const)) return false;
        return true;
    }

    /// <summary>The instructions of the function's cleanup handlers: from a catch_all_ref's landing, only releases of
    /// locals (`local.get x; call <release>`) and ends, then throw_ref. What they reach reads nothing but to release.</summary>
    static HashSet<int> CleanupHandlers(List<Instr> b, Cfg g, Effects e)
    {
        var set = new HashSet<int>();
        int n = b.Count;
        var ctl = new List<int>();
        for (int k = 0; k < n; k++)
        {
            var op = b[k].Op;
            if (op == Op.End) { if (ctl.Count > 0) ctl.RemoveAt(ctl.Count - 1); continue; }
            if (op is Op.Block or Op.Loop or Op.If) { ctl.Add(k); continue; }
            if (op != Op.TryTable) continue;
            foreach (var c in ((TryTableInfo)b[k].Ref!).Catches)
            {
                if (c.Kind != 3) continue;
                // the landing: the end of the construct the label names (counted from the try_table's enclosing one)
                int idx = ctl.Count - 1 - c.Label;
                if (idx < 0) continue;
                int target = ctl[idx];
                if (b[target].Op == Op.Loop) continue;
                int land = g.Match[target];
                if (land < 0) continue;
                var mine = new List<int>();
                bool ok = false;
                for (int q = land; q < n; q++)
                {
                    var x = b[q];
                    mine.Add(q);
                    if (x.Op == Op.ThrowRef) { ok = true; break; }
                    if (x.Op is Op.End or Op.Nop) continue;
                    if (x.Op == Op.LocalGet && q + 1 < n && b[q + 1].Op == Op.Call && b[q + 1].Ref is WasmFunction r
                        && (r == e.Release || r.Name.StartsWith("rt.release", StringComparison.Ordinal) || r.Name.StartsWith("framedisplay.drop", StringComparison.Ordinal))
                        && r.Type.Params.Length == 1 && r.Type.Results.Length == 0)
                    { mine.Add(q + 1); q++; continue; }
                    // (a struct's references released through its address in the frame: local.get sp; i32.const k; i32.add; call)
                    if (x.Op == Op.LocalGet && q + 3 < n && b[q + 1].Op == Op.I32Const && b[q + 2].Op == Op.I32Add && b[q + 3].Op == Op.Call && b[q + 3].Ref is WasmFunction r2
                        && r2.Name.StartsWith("rt.release", StringComparison.Ordinal) && r2.Type.Params.Length == 1 && r2.Type.Results.Length == 0)
                    { mine.Add(q + 1); mine.Add(q + 2); mine.Add(q + 3); q += 3; continue; }
                    break;
                }
                if (ok) set.UnionWith(mine);
            }
            ctl.Add(k);
        }
        return set;
    }
}
