namespace Cs2Wasm.Wasm;

/// <summary>
/// Calls that never come back (section 5). A function that always throws (the helpers behind a null check, a bounds
/// check, a failed cast, a division by zero) is called from the middle of hot code, and an engine that compiles one
/// function at a time cannot know the call does not return: it keeps every value the code after the call uses alive
/// across it, in memory, which puts a store and a load into loops whose values would otherwise stay in registers. An
/// `unreachable` after such a call says it: one byte, never executed.
/// </summary>
static class NoReturn
{
    public static void Run(List<WasmFunction> functions)
    {
        var never = new HashSet<WasmFunction>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var f in functions)
                if (!f.IsImport && f.ForwardTo == null && !never.Contains(f) && NeverReturns(f, never)) { never.Add(f); changed = true; }
        }
        if (never.Count == 0) return;
        foreach (var f in functions)
        {
            if (f.IsImport) continue;
            var b = f.Body;
            for (int i = 0; i < b.Count; i++)
            {
                if (b[i].Op != Op.Call || b[i].Ref is not WasmFunction g || !never.Contains(g)) continue;
                if (i + 1 < b.Count && b[i + 1].Op == Op.Unreachable) continue;
                b.Insert(i + 1, new Instr { Op = Op.Unreachable, Pos = b[i].Pos });
                i++;
            }
        }
    }

    /// <summary>The body's last instruction is one that does not go on (a throw, a trap, a call of such a function), and
    /// nothing leaves the body before it (no return, no branch to the function's own label).</summary>
    static bool NeverReturns(WasmFunction f, HashSet<WasmFunction> never)
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
                    // (a handler's label counts from outside the try_table)
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
}
