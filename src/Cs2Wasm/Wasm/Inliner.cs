namespace Cs2Wasm.Wasm;

/// <summary>
/// Inlining (section 5) of small functions: property getters and setters, field accessors, small arithmetic
/// helpers, compiler-made thunks, wrappers. A callee is inlined only if no exception can leave it (it throws nothing
/// and calls only functions that cannot throw), so it is never on the stack at a throw and stack traces (which come
/// from real frames) are unchanged. Its parameters and locals become fresh locals of the caller (locals
/// re-zeroed on each entry, as a call would), its body goes into a block, and `return` becomes a branch to that block.
/// </summary>
static class Inliner
{
    const int SmallBody = 7;      // instructions of the inlined copy; inlined at every call site
    const int SingleCallerBody = 120; // a function with one call site and no other use is inlined up to this size
    const int SingleCallerCalling = 100; // the same for one that calls functions
    const int LoopBody = 24;      // a straight-line body that calls nothing, at a call site inside a loop
    const int HintedBody = 16;    // a method marked AggressiveInlining, at every call site
    static readonly int s_loopBody = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_LOOP_INLINE"), out var lb) ? lb : LoopBody;

    // (CS2WASM_EXP_INLINE=n, an experiment: bodies up to n instructions at every site, whether they can throw or not,
    // in three rounds; traces lose the inlined frames)
    static readonly int s_exp = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_EXP_INLINE"), out var x) ? x : 0;

    /// <summary>Returns the functions it changed.</summary>
    public static HashSet<WasmFunction> Run(List<WasmFunction> functions, HashSet<WasmFunction> mayThrow, bool tiny = false)
    {
        if (s_exp > 0) { for (int round = 0; round < 3; round++) RunOnce(functions, [], false); return []; }
        return RunOnce(functions, mayThrow, tiny);
    }

    /// <summary>tiny: only bodies of a few instructions that call nothing and have no locals (a getter once its local
    /// traffic is gone): their copy is no larger than the call.</summary>
    static HashSet<WasmFunction> RunOnce(List<WasmFunction> functions, HashSet<WasmFunction> mayThrow, bool tiny)
    {
        var changed = new HashSet<WasmFunction>();
        var callers = new Dictionary<WasmFunction, int>();
        foreach (var f in functions)
        {
            if (f.IsImport) continue;
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g) callers[g] = callers.GetValueOrDefault(g) + 1;
        }
        var candidates = new HashSet<WasmFunction>();
        // (in a loop the call is paid for every time round: a larger body goes in there, and only there)
        var inLoops = new HashSet<WasmFunction>();
        foreach (var g in functions)
        {
            // (the null check: it has no frame of its own in a trace, so a copy of it throws from the same frames; in
            // the second round, when the checks that are not needed are gone)
            if (tiny && g.Name == "rt.nonnull" && !g.IsImport && s_loopBody > 0) { inLoops.Add(g); continue; }
            if (mayThrow.Contains(g) || !Inlinable(g, mayThrow))
            {
                // (marked AggressiveInlining: in although it can throw, when it is small; larger in a loop)
                // (the count operations: they have no frame of their own in a trace, so a copy throws from the same
                // frames; in a loop the call would make the engine keep the loop's values in memory)
                if (tiny && g.FrameName == null && g.Name is "rt.retain" or "rt.release" && s_loopBody > 0 && InlinableHinted(g)) { inLoops.Add(g); continue; }
                if (!g.InlineHint || s_exp > 0 || !InlinableHinted(g)) continue;
                int hc = g.Body.Count + g.Type.Params.Length + 2 * g.Locals.Count + 2;
                if (hc <= HintedBody) candidates.Add(g); else if (hc <= 2 * s_loopBody) inLoops.Add(g);
                continue;
            }
            int n = g.Body.Count;
            bool single = callers.GetValueOrDefault(g) == 1 && g.Exports.Count == 0 && !g.InTable;
            // at many sites only when the copy is about as small as the call (parameter moves, block, local resets)
            int copy = n + g.Type.Params.Length + 2 * g.Locals.Count + 2;
            // (a function that calls others, inlined into its one caller up to a smaller size: measured on the module)
            bool leaf = !g.Body.Any(x => x.Op == Op.Call);
            if (tiny)
            {
                if (leaf && n <= 4 && g.Locals.Count == 0 && g.Type.Params.Length <= 2) candidates.Add(g);
                else if (leaf && copy <= s_loopBody && StraightLine(g)) inLoops.Add(g);
                continue;
            }
            if (copy <= (s_exp > 0 ? s_exp : SmallBody) || single && n <= (leaf ? SingleCallerBody : SingleCallerCalling)) candidates.Add(g);
            else if (leaf && copy <= s_loopBody && StraightLine(g)) inLoops.Add(g);
        }
        if (candidates.Count == 0 && inLoops.Count == 0) return changed;
        foreach (var f in functions)
        {
            if (f.IsImport) continue;
            bool any = false;
            foreach (var ins in f.Body) if (ins.Op == Op.Call && ins.Ref is WasmFunction g && (candidates.Contains(g) || inLoops.Contains(g)) && g != f) { any = true; break; }
            if (!any) continue;
            var outBody = new List<Instr>(f.Body.Count + 32);
            var open = new List<bool>(); // the blocks open here: whether each is a loop
            int loops = 0;
            any = false;
            foreach (var ins in f.Body)
            {
                switch (ins.Op)
                {
                    case Op.Loop: open.Add(true); loops++; break;
                    case Op.Block or Op.If or Op.TryTable: open.Add(false); break;
                    case Op.End when open.Count > 0: if (open[^1]) loops--; open.RemoveAt(open.Count - 1); break;
                }
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g && g != f && (candidates.Contains(g) || loops > 0 && inLoops.Contains(g))) { Expand(f, g, outBody, ins.Pos); any = true; }
                else outBody.Add(ins);
            }
            if (!any) continue;
            f.Body = outBody;
            changed.Add(f);
        }
        return changed;
    }

    /// <summary>No calls of functions that can throw, no throws, no exception handling, no frame (stack pointer), no
    /// imports.</summary>
    internal static bool Inlinable(WasmFunction g, HashSet<WasmFunction> mayThrow)
    {
        if (g.IsImport || g.ForwardTo != null || g.Body.Count == 0) return false;
        foreach (var ins in g.Body)
        {
            switch (ins.Op)
            {
                case Op.Call when ins.Ref is WasmFunction h && !h.IsImport && !mayThrow.Contains(h) && h != g:
                    break;
                case Op.Call or Op.CallIndirect or Op.Throw or Op.ThrowRef when s_exp > 0:
                    break;
                case Op.Call or Op.CallIndirect or Op.Throw or Op.ThrowRef or Op.TryTable:
                    return false;
                case Op.GlobalGet or Op.GlobalSet when ins.Ref is WasmGlobal { Export: "__stack_pointer" }:
                    return false;
            }
            if ((int)ins.Op >= 0xFE00) return false; // atomics: keep their functions (threads mode bodies are filled late)
        }
        return true;
    }

    /// <summary>A method marked AggressiveInlining: no exception handling of its own, no frame, no atomics.</summary>
    internal static bool InlinableHinted(WasmFunction g)
    {
        if (g.IsImport || g.ForwardTo != null || g.Body.Count == 0) return false;
        foreach (var ins in g.Body)
        {
            if (ins.Op is Op.TryTable) return false;
            if (ins.Op == Op.Call && ReferenceEquals(ins.Ref, g)) return false;
            if (ins.Op is Op.GlobalGet or Op.GlobalSet && ins.Ref is WasmGlobal { Export: "__stack_pointer" }) return false;
            if ((int)ins.Op >= 0xFE00) return false;
        }
        return true;
    }

    /// <summary>No return but possibly a last one, and no branch out of the body.</summary>
    static bool StraightLine(WasmFunction g)
    {
        int depth = 0;
        for (int k = 0; k < g.Body.Count; k++)
        {
            var ins = g.Body[k];
            switch (ins.Op)
            {
                case Op.Block or Op.Loop or Op.If or Op.TryTable: depth++; break;
                case Op.End: depth--; break;
                case Op.Return: if (k != g.Body.Count - 1) return false; break;
                case Op.Br or Op.BrIf: if (ins.Imm >= depth) return false; break;
                case Op.BrTable: return false;
                case Op.Unreachable: if (depth == 0 && k != g.Body.Count - 1) return false; break;
            }
        }
        return depth == 0;
    }

    internal static void Expand(WasmFunction f, WasmFunction g, List<Instr> outBody, int pos)
    {
        int np = g.Type.Params.Length;
        int baseF = f.Type.Params.Length;
        var map = new int[np + g.Locals.Count];
        for (int i = 0; i < np; i++) { f.Locals.Add(g.Type.Params[i]); map[i] = baseF + f.Locals.Count - 1; }
        for (int i = 0; i < g.Locals.Count; i++) { f.Locals.Add(g.Locals[i]); map[np + i] = baseF + f.Locals.Count - 1; }
        void Add(Op op, long imm = 0, object? r = null) => outBody.Add(new Instr { Op = op, Imm = imm, Ref = r, Pos = pos });
        // arguments: last on top of the stack
        for (int i = np - 1; i >= 0; i--) Add(Op.LocalSet, map[i]);
        // the callee's locals start at zero, as on a call
        for (int i = 0; i < g.Locals.Count; i++)
        {
            switch (g.Locals[i])
            {
                case ValType.I32: Add(Op.I32Const, 0); break;
                case ValType.I64: Add(Op.I64Const, 0); break;
                case ValType.F32: Add(Op.F32Const, 0); break;
                case ValType.F64: Add(Op.F64Const, 0); break;
                default: continue; // reference locals start null; none in inlinable bodies
            }
            Add(Op.LocalSet, map[np + i]);
        }
        // a body that runs straight to its one return, at its end, goes in as it is: no block, no branch
        if (StraightLine(g))
        {
            for (int k = 0; k < g.Body.Count - 1; k++)
            {
                var ins = g.Body[k];
                if (ins.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee) ins.Imm = map[(int)ins.Imm];
                outBody.Add(ins);
            }
            if (g.Body[^1].Op != Op.Return) { var last = g.Body[^1]; if (last.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee) last.Imm = map[(int)last.Imm]; outBody.Add(last); }
            return;
        }
        object? bt = g.Type.Results.Length switch { 0 => null, 1 => g.Type.Results[0], _ => new FuncType([], g.Type.Results) };
        Add(Op.Block, 0, bt);
        int depth = 0;
        foreach (var src in g.Body)
        {
            var ins = src;
            switch (ins.Op)
            {
                case Op.LocalGet or Op.LocalSet or Op.LocalTee: ins.Imm = map[(int)ins.Imm]; break;
                case Op.Block or Op.Loop or Op.If: depth++; break;
                case Op.End: depth--; break;
                case Op.Return: ins.Op = Op.Br; ins.Imm = depth; break;
            }
            outBody.Add(ins);
        }
        Add(Op.End);
    }
}
