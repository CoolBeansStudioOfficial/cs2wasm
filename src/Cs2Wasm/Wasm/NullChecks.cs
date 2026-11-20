namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: a null check (rt.nonnull) of a local that is already known not to be null is taken out. A local is known
/// after a check of it, after `if (x != null)` and `while (x != null)` (the branch taken when it is null), and when it is
/// set from a checked value; it stops being known when it is set to anything else. The knowledge follows the structure:
/// what a block, loop or branch sets is unknown after it (and at a loop's start), and what was learned inside is
/// forgotten at its end. Nothing else changes: the check that stays is the first one on every path, so the exception
/// is thrown where it was.
/// </summary>
static class NullChecks
{
    public static void Run(WasmFunction f, WasmFunction nonNull)
    {
        var b = f.Body;
        bool any = false;
        foreach (var ins in b) if (ins.Op == Op.Call && (ReferenceEquals(ins.Ref, nonNull) || IsThrowNull(ins))) { any = true; break; }
        if (!any) return;
        int n = b.Count;
        // each construct's end
        var end = new int[n];
        var open = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            var op = b[i].Op;
            if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) open.Push(i);
            else if (op == Op.End && open.Count > 0) end[open.Pop()] = i;
        }
        var known = new HashSet<int>();
        var scopes = new Stack<(HashSet<int> entry, HashSet<int> written, bool isIf)>();
        var remove = new bool[n];
        var drop = new bool[n]; // (a check written out, taken out: its value is dropped where the test was)
        int topSource = -1;     // the local a `local.tee` copied the value from
        var topAlias = new List<int>(); // every local the value on top was written into by a run of `local.tee`s
        bool top = false;   // the value on top of the stack is known not to be null
        int topLocal = -1;  // and it is this local's (just read), for the branch patterns
        for (int i = 0; i < n; i++)
        {
            var ins = b[i];
            switch (ins.Op)
            {
                case Op.LocalGet:
                    top = known.Contains((int)ins.Imm);
                    topLocal = (int)ins.Imm;
                    topSource = -1;
                    topAlias.Clear(); topAlias.Add(topLocal);
                    continue;
                case Op.I32Eqz when CheckEnd(b, i) is int last && topLocal >= 0 && i > 0 && b[i - 1].Op is Op.LocalGet or Op.LocalTee:
                    // the check written out where an array's length or element is read: `x; i32.eqz; if; throw;
                    // [unreachable;] end` (the unreachable is gone once the pass for calls that never return has run)
                    if (top) { drop[i] = true; for (int k = i; k <= last; k++) remove[k] = true; }
                    else { known.Add(topLocal); if (topSource >= 0) known.Add(topSource); known.UnionWith(topAlias); }
                    i = last;
                    break;
                case Op.Call when ReferenceEquals(ins.Ref, nonNull):
                    if (top) remove[i] = true;
                    else if (topLocal >= 0) { known.Add(topLocal); known.UnionWith(topAlias); }
                    top = true; // (the checked value; topLocal stays: the same value)
                    continue;
                case Op.Call when ins.Ref is WasmFunction { Name: var an } && an.StartsWith("Cs2Wasm.Runtime.Rt.Alloc", StringComparison.Ordinal) && ((WasmFunction)ins.Ref).Type.Results.Length == 1:
                    // (a new object, array or string: never null)
                    top = true; topLocal = -1;
                    continue;
                case Op.LocalTee:
                    if (top) known.Add((int)ins.Imm); else known.Remove((int)ins.Imm);
                    topSource = topLocal >= 0 && topLocal != (int)ins.Imm && i > 0 && b[i - 1].Op == Op.LocalGet ? topLocal : -1;
                    // (a run of tees: every local of it holds the value; anything else before, only this one)
                    if (!(i > 0 && b[i - 1].Op is Op.LocalGet or Op.LocalTee)) topAlias.Clear();
                    topAlias.Remove((int)ins.Imm);
                    topAlias.Add((int)ins.Imm);
                    topLocal = (int)ins.Imm;
                    continue;
                case Op.LocalSet:
                    if (top) known.Add((int)ins.Imm); else known.Remove((int)ins.Imm);
                    break;
                case Op.I32Eqz when topLocal >= 0 && i + 1 < n && b[i + 1].Op == Op.BrIf && i > 0 && b[i - 1].Op == Op.LocalGet:
                    // local.get x ; i32.eqz ; br_if: x is not null where the code goes on
                    known.Add(topLocal);
                    i++;
                    break;
                case Op.Block or Op.Loop or Op.If or Op.TryTable:
                    {
                        var written = new HashSet<int>();
                        for (int k = i + 1; k < end[i]; k++) if (b[k].Op is Op.LocalSet or Op.LocalTee) written.Add((int)b[k].Imm);
                        // local.get x ; if: x is not null in the branch (a reference used as a condition is compared first:
                        // local.get x ; i32.const 0 ; i32.ne ; if)
                        int cond = -1;
                        if (ins.Op == Op.If && i >= 3 && b[i - 1].Op == Op.I32Ne && b[i - 2].Op == Op.I32Const && b[i - 2].Imm == 0 && b[i - 2].Ref == null && b[i - 3].Op == Op.LocalGet)
                            cond = (int)b[i - 3].Imm;
                        scopes.Push((new HashSet<int>(known), written, ins.Op == Op.If));
                        if (ins.Op == Op.Loop) known.ExceptWith(written);
                        if (cond >= 0 && !written.Contains(cond)) known.Add(cond);
                        break;
                    }
                case Op.Else when scopes.Count > 0:
                    {
                        var s = scopes.Peek();
                        known = new HashSet<int>(s.entry);
                        known.ExceptWith(s.written);
                        break;
                    }
                case Op.End when scopes.Count > 0:
                    {
                        var s = scopes.Pop();
                        known = s.entry;
                        known.ExceptWith(s.written);
                        break;
                    }
            }
            top = false; topLocal = -1; topAlias.Clear();
        }
        if (!Array.Exists(remove, x => x)) return;
        var o = new List<Instr>(n);
        for (int i = 0; i < n; i++)
        {
            if (drop[i]) o.Add(new Instr { Op = Op.Drop, Pos = b[i].Pos });
            if (!remove[i]) o.Add(b[i]);
        }
        f.Body = o;
    }

    /// <summary>Each remaining check by call (rt.nonnull) written out: `local.tee t; i32.eqz; if; throw; end; local.get
    /// t`, inside loops. The engine's tier that runs the module as written makes every call a call; a branch on a value already in a
    /// register is far cheaper. The exception is thrown in the same frame as before (rt.nonnull's own frame is hidden
    /// in traces), so the trace is the same.</summary>
    public static void Expand(WasmFunction f, WasmFunction nonNull, WasmFunction throwNull, bool whole = false)
    {
        var b = f.Body;
        // (only inside loops, where the time is: a check run once is a call of three bytes instead of nine, and
        // Console.WriteLine(42) has a size to keep)
        var inLoop = new bool[b.Count];
        var open = new Stack<bool>();
        int loops = 0, count = 0;
        for (int i = 0; i < b.Count; i++)
        {
            var op = b[i].Op;
            if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) { open.Push(op == Op.Loop); if (op == Op.Loop) loops++; }
            else if (op == Op.End && open.Count > 0) { if (open.Pop()) loops--; }
            inLoop[i] = whole || loops > 0;
            if (inLoop[i] && b[i].Op == Op.Call && ReferenceEquals(b[i].Ref, nonNull)) count++;
        }
        if (count == 0) return;
        f.Locals.Add(ValType.I32);
        int t = f.Type.Params.Length + f.Locals.Count - 1;
        var o = new List<Instr>(b.Count + count * 5);
        for (int i = 0; i < b.Count; i++)
        {
            var ins = b[i];
            if (!(inLoop[i] && ins.Op == Op.Call && ReferenceEquals(ins.Ref, nonNull))) { o.Add(ins); continue; }
            o.Add(new Instr { Op = Op.LocalTee, Imm = t, Pos = ins.Pos });
            o.Add(new Instr { Op = Op.I32Eqz, Pos = ins.Pos });
            o.Add(new Instr { Op = Op.If, Pos = ins.Pos });
            o.Add(new Instr { Op = Op.Call, Ref = throwNull, Pos = ins.Pos, Inl = ins.Inl });
            o.Add(new Instr { Op = Op.End, Pos = ins.Pos });
            o.Add(new Instr { Op = Op.LocalGet, Imm = t, Pos = ins.Pos });
        }
        f.Body = o;
    }

    /// <summary>The functions a loop calls, and the functions they call, to the depth given (each call in a loop's body:
    /// what runs as often as the loop does).</summary>
    public static HashSet<WasmFunction> CalledFromLoops(IEnumerable<WasmFunction> fns, int depth)
    {
        var hot = new HashSet<WasmFunction>();
        var frontier = new List<WasmFunction>();
        foreach (var f in fns)
        {
            if (f.IsImport) continue;
            int loops = 0;
            var open = new Stack<bool>();
            foreach (var ins in f.Body)
            {
                if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) { open.Push(ins.Op == Op.Loop); if (ins.Op == Op.Loop) loops++; }
                else if (ins.Op == Op.End && open.Count > 0) { if (open.Pop()) loops--; }
                else if (loops > 0 && ins.Op == Op.Call && ins.Ref is WasmFunction g && !g.IsImport && hot.Add(g)) frontier.Add(g);
            }
        }
        for (int d = 1; d < depth && frontier.Count > 0; d++)
        {
            var next = new List<WasmFunction>();
            foreach (var f in frontier)
                foreach (var ins in f.Body)
                    if (ins.Op == Op.Call && ins.Ref is WasmFunction g && !g.IsImport && hot.Add(g)) next.Add(g);
            frontier = next;
        }
        return hot;
    }

    /// <summary>The index of the `end` of a written-out null check whose `i32.eqz` is at i, or null.</summary>
    static int? CheckEnd(List<Instr> b, int i)
    {
        int n = b.Count;
        if (i + 3 >= n || b[i + 1].Op != Op.If || b[i + 1].Ref != null || !IsThrowNull(b[i + 2])) return null;
        if (b[i + 3].Op == Op.End) return i + 3;
        if (b[i + 3].Op == Op.Unreachable && i + 4 < n && b[i + 4].Op == Op.End) return i + 4;
        return null;
    }

    static bool IsThrowNull(in Instr ins) => ins.Op == Op.Call && ins.Ref is WasmFunction { Name: "Cs2Wasm.Runtime.ThrowHelpers.NullReference()" };
}
