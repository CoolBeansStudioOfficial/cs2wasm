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
        foreach (var ins in b) if (ins.Op == Op.Call && ReferenceEquals(ins.Ref, nonNull)) { any = true; break; }
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
                    continue;
                case Op.Call when ReferenceEquals(ins.Ref, nonNull):
                    if (top) remove[i] = true;
                    else if (topLocal >= 0) known.Add(topLocal);
                    top = true; // (the checked value; topLocal stays: the same value)
                    continue;
                case Op.LocalTee:
                    if (top) known.Add((int)ins.Imm); else known.Remove((int)ins.Imm);
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
            top = false; topLocal = -1;
        }
        if (!Array.Exists(remove, x => x)) return;
        var o = new List<Instr>(n);
        for (int i = 0; i < n; i++) if (!remove[i]) o.Add(b[i]);
        f.Body = o;
    }
}
