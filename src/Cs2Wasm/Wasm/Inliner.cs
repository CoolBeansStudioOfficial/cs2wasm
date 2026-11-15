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

    public static void Run(List<WasmFunction> functions, HashSet<WasmFunction> mayThrow)
    {
        var callers = new Dictionary<WasmFunction, int>();
        foreach (var f in functions)
        {
            if (f.IsImport) continue;
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g) callers[g] = callers.GetValueOrDefault(g) + 1;
        }
        var candidates = new HashSet<WasmFunction>();
        foreach (var g in functions)
        {
            if (mayThrow.Contains(g) || !Inlinable(g, mayThrow)) continue;
            int n = g.Body.Count;
            bool single = callers.GetValueOrDefault(g) == 1 && g.Exports.Count == 0 && !g.InTable;
            // at many sites only when the copy is about as small as the call (parameter moves, block, local resets)
            int copy = n + g.Type.Params.Length + 2 * g.Locals.Count + 2;
            // (a function that calls others, inlined into its one caller up to a smaller size: measured on the module)
            bool leaf = !g.Body.Any(x => x.Op == Op.Call);
            if (copy <= SmallBody || single && n <= (leaf ? SingleCallerBody : SingleCallerCalling)) candidates.Add(g);
        }
        if (candidates.Count == 0) return;
        foreach (var f in functions)
        {
            if (f.IsImport) continue;
            bool any = false;
            foreach (var ins in f.Body) if (ins.Op == Op.Call && ins.Ref is WasmFunction g && candidates.Contains(g) && g != f) { any = true; break; }
            if (!any) continue;
            var outBody = new List<Instr>(f.Body.Count + 32);
            foreach (var ins in f.Body)
            {
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g && candidates.Contains(g) && g != f) Expand(f, g, outBody, ins.Pos);
                else outBody.Add(ins);
            }
            f.Body = outBody;
        }
    }

    /// <summary>No calls of functions that can throw, no throws, no exception handling, no frame (stack pointer), no
    /// imports.</summary>
    static bool Inlinable(WasmFunction g, HashSet<WasmFunction> mayThrow)
    {
        if (g.IsImport || g.ForwardTo != null || g.Body.Count == 0) return false;
        foreach (var ins in g.Body)
        {
            switch (ins.Op)
            {
                case Op.Call when ins.Ref is WasmFunction h && !h.IsImport && !mayThrow.Contains(h) && h != g:
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

    static void Expand(WasmFunction f, WasmFunction g, List<Instr> outBody, int pos)
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
