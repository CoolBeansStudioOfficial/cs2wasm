namespace Cs2Wasm.Wasm.Ssa;

/// <summary>
/// A view over a final body (WasmFunction.Body as the passes leave it): its control flow per instruction (normal
/// successors and the catches an exception goes to), and module-wide summaries of what a call can do. The spike of
/// docs/AUDIT.md step 5/6 (the IR optimizer): the facts an IR with explicit ownership would carry, recovered from the
/// stack code, to measure what count-pair removal and load reuse pay before a front end is written for them.
/// </summary>
sealed class Cfg
{
    public int N;
    /// <summary>block/loop/if/try_table (and else) to its end.</summary>
    public int[] Match = [];
    public int[] ElseOf = [];
    /// <summary>Normal successors (N: the function's exit).</summary>
    public int[][] Succ = [];
    /// <summary>Where an exception thrown by the instruction goes (empty: out of the function, or it cannot throw).</summary>
    public int[][] Exc = [];
    /// <summary>The instruction can throw (a call of a function that can, call_indirect, throw).</summary>
    public bool[] Throws = [];
    /// <summary>The instruction is inside a try_table of this function (an exception goes to one of its catches).</summary>
    public bool[] Guarded = [];

    static readonly int[] s_none = [];

    public static Cfg Build(List<Instr> b, HashSet<WasmFunction>? mayThrow)
    {
        int n = b.Count;
        var g = new Cfg { N = n, Match = new int[n], ElseOf = new int[n], Succ = new int[n][], Exc = new int[n][], Throws = new bool[n], Guarded = new bool[n] };
        Array.Fill(g.Match, -1); Array.Fill(g.ElseOf, -1);
        var st = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            switch (b[i].Op)
            {
                case Op.Block: case Op.Loop: case Op.If: case Op.TryTable: st.Push(i); break;
                case Op.Else: if (st.Count > 0) g.ElseOf[st.Peek()] = i; break;
                case Op.End:
                    if (st.Count == 0) break;
                    int o = st.Pop();
                    g.Match[o] = i;
                    if (g.ElseOf[o] >= 0) g.Match[g.ElseOf[o]] = i;
                    break;
            }
        }
        var ctl = new List<int>();
        int Target(int depth, int below)
        {
            int k = below - 1 - depth;
            if (k < 0) return n;
            int c = ctl[k];
            return b[c].Op == Op.Loop ? c : g.Match[c];
        }
        var s = new List<int>(4);
        var exc = new List<int>(4);
        int tries = 0;
        for (int i = 0; i < n; i++)
        {
            var ins = b[i];
            s.Clear(); exc.Clear();
            switch (ins.Op)
            {
                case Op.Br: s.Add(Target((int)ins.Imm, ctl.Count)); break;
                case Op.BrIf: s.Add(i + 1); s.Add(Target((int)ins.Imm, ctl.Count)); break;
                case Op.BrTable:
                    foreach (var d in (int[])ins.Ref!) s.Add(Target(d, ctl.Count));
                    s.Add(Target((int)ins.Imm, ctl.Count));
                    break;
                case Op.Return: case Op.Unreachable: case Op.Throw: case Op.ThrowRef: break;
                case Op.If:
                    s.Add(i + 1);
                    s.Add(g.ElseOf[i] >= 0 ? g.ElseOf[i] + 1 : g.Match[i]);
                    break;
                case Op.Else: s.Add(g.Match[i]); break;
                default: s.Add(i + 1); break;
            }
            bool throws = ins.Op is Op.CallIndirect or Op.Throw or Op.ThrowRef
                || ins.Op == Op.Call && (mayThrow == null || ins.Ref is not WasmFunction fn || mayThrow.Contains(fn));
            g.Throws[i] = throws;
            g.Guarded[i] = tries > 0;
            if (throws)
                for (int k = ctl.Count - 1; k >= 0; k--)
                    if (b[ctl[k]].Op == Op.TryTable)
                        foreach (var c in ((TryTableInfo)b[ctl[k]].Ref!).Catches) exc.Add(Target(c.Label, k));
            g.Succ[i] = s.Count == 0 ? s_none : s.ToArray();
            g.Exc[i] = exc.Count == 0 ? s_none : exc.ToArray();
            if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) { ctl.Add(i); if (ins.Op == Op.TryTable) tries++; }
            else if (ins.Op == Op.End && ctl.Count > 0) { if (b[ctl[^1]].Op == Op.TryTable) tries--; ctl.RemoveAt(ctl.Count - 1); }
        }
        return g;
    }
}

/// <summary>What a call can do, for every function of the module (fixpoints over the call graph).</summary>
sealed class Effects
{
    /// <summary>Can free an object or lower a count (a release of anything, a destruction, a store of a reference
    /// that releases the old one, an arena operation), directly or through what it calls. An import (the host can
    /// call back in) and call_indirect count as dropping.</summary>
    public readonly HashSet<WasmFunction> MayDrop = [];
    /// <summary>Can write memory (a store, a bulk copy or fill, an atomic), directly or through what it calls; imports
    /// and call_indirect count as storing.</summary>
    public readonly HashSet<WasmFunction> MayStore = [];
    /// <summary>Never comes back normally (every path throws or traps).</summary>
    public readonly HashSet<WasmFunction> NeverReturns = [];
    public HashSet<WasmFunction> MayThrow = [];
    public WasmFunction? Retain, Release, NonNull;

    /// <summary>A function that frees or lowers counts by what it is (its name: the runtime's count, destruction and
    /// arena entry points). Conservative: a program function whose name contains one of these words is taken as one
    /// too, which only loses removals.</summary>
    static bool PrimitiveDropper(WasmFunction g)
    {
        if (g.IsImport) return true;
        var n = g.Name;
        return n.StartsWith("rt.release", StringComparison.Ordinal) || n.StartsWith("rt.destroy", StringComparison.Ordinal)
            || n.StartsWith("destroy ", StringComparison.Ordinal) || n.StartsWith("framedisplay.", StringComparison.Ordinal)
            || n.StartsWith("arena.", StringComparison.Ordinal) || n.StartsWith("count Release", StringComparison.Ordinal)
            || n.Contains("store_ref", StringComparison.Ordinal) || n.Contains("_assign", StringComparison.Ordinal)
            || n.Contains("Release", StringComparison.Ordinal) || n.Contains("Destroy", StringComparison.Ordinal)
            || n.Contains("Free", StringComparison.Ordinal) || n.Contains("free", StringComparison.Ordinal)
            || n.Contains("Arena", StringComparison.Ordinal) || n.Contains("Finaliz", StringComparison.Ordinal)
            || n.Contains("Unlink", StringComparison.Ordinal) || n.Contains("Kill", StringComparison.Ordinal)
            || n.Contains("drop", StringComparison.Ordinal) || n.Contains("Drop", StringComparison.Ordinal);
    }

    static bool IsStore(Op op) => op >= Op.I32Store && op <= Op.I64Store32 || op is Op.MemoryCopy or Op.MemoryFill or Op.MemoryInit or Op.V128Store
        || (int)op >= 0xFE00 && op is not (Op.I32AtomicLoad or Op.I64AtomicLoad);

    public static Effects Compute(List<WasmFunction> fns, HashSet<WasmFunction> mayThrow)
    {
        var e = new Effects { MayThrow = mayThrow };
        e.Retain = fns.FirstOrDefault(f => f.Name == "rt.retain" && !f.IsImport);
        e.Release = fns.FirstOrDefault(f => f.Name == "rt.release" && !f.IsImport);
        e.NonNull = fns.FirstOrDefault(f => f.Name == "rt.nonnull" && !f.IsImport);
        var callers = new Dictionary<WasmFunction, List<WasmFunction>>();
        var dropWork = new Stack<WasmFunction>();
        var storeWork = new Stack<WasmFunction>();
        foreach (var f in fns)
        {
            // (a retain only adds: the count goes up, and an arena member's first outside reference takes a hold on its
            // owner, UpdateHold with `want` true; nothing is freed or lowered. Its body calls the arena's entry points,
            // which the name rule would take as dropping.)
            bool isRetain = f == e.Retain || f.Name is "rt.retain_plain";
            bool drop = !isRetain && PrimitiveDropper(f), store = f.IsImport;
            foreach (var ins in f.Body)
            {
                if (ins.Op == Op.CallIndirect) { drop |= !isRetain; store = true; }
                else if (ins.Op == Op.Call && ins.Ref is WasmFunction g)
                {
                    if (!callers.TryGetValue(g, out var l)) callers[g] = l = [];
                    l.Add(f);
                }
                else if (IsStore(ins.Op) || ins.Op == Op.GlobalSet) store = true;
            }
            if (f.ForwardTo != null) { if (!callers.TryGetValue(f.ForwardTo, out var l)) callers[f.ForwardTo] = l = []; l.Add(f); }
            if (drop && e.MayDrop.Add(f)) dropWork.Push(f);
            if (store && e.MayStore.Add(f)) storeWork.Push(f);
        }
        while (dropWork.Count > 0)
            if (callers.TryGetValue(dropWork.Pop(), out var cs))
                foreach (var c in cs) if (c != e.Retain && c.Name != "rt.retain_plain" && e.MayDrop.Add(c)) dropWork.Push(c);
        while (storeWork.Count > 0)
            if (callers.TryGetValue(storeWork.Pop(), out var cs))
                foreach (var c in cs) if (e.MayStore.Add(c)) storeWork.Push(c);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var f in fns)
                if (!f.IsImport && f.ForwardTo == null && !e.NeverReturns.Contains(f) && NeverReturnsBody(f, e.NeverReturns)) { e.NeverReturns.Add(f); changed = true; }
        }
        return e;
    }

    /// <summary>As NoReturn.cs: the body ends in a throw, a trap or such a call, and nothing leaves it before.</summary>
    static bool NeverReturnsBody(WasmFunction f, HashSet<WasmFunction> never)
    {
        var b = f.Body;
        if (b.Count == 0) return false;
        var last = b[^1];
        bool ends = last.Op is Op.Throw or Op.ThrowRef or Op.Unreachable || last.Op == Op.Call && last.Ref is WasmFunction g && never.Contains(g);
        if (!ends) return false;
        int depth = 0;
        foreach (var ins in b)
        {
            switch (ins.Op)
            {
                case Op.Block or Op.Loop or Op.If or Op.TryTable:
                    if (ins.Ref is TryTableInfo tt) foreach (var c in tt.Catches) if (c.Label >= depth) return false;
                    depth++;
                    break;
                case Op.End: depth--; break;
                case Op.Return: return false;
                case Op.Br or Op.BrIf: if (ins.Imm >= depth) return false; break;
                case Op.BrTable: return false;
            }
        }
        return true;
    }

    public bool Drops(in Instr ins) => ins.Op == Op.CallIndirect || ins.Op == Op.Call && ins.Ref is WasmFunction g && MayDrop.Contains(g);
    public bool Stores(in Instr ins) => ins.Op == Op.CallIndirect || ins.Op == Op.Call && ins.Ref is WasmFunction g && MayStore.Contains(g)
        || IsStore(ins.Op) || ins.Op == Op.GlobalSet || ins.Op == Op.MemoryGrow;
}
