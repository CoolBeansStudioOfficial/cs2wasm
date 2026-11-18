namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: the check that a type's static initialization has run (`flag != -1 ? static init T`), where the code
/// has already made it. A type that is initialized stays so, and a check that returns leaves it initialized (or
/// being initialized by this very thread, which every later check passes as well; a failed initialization throws at
/// every check), so a check that follows another of the same type on every path does nothing and goes. The knowledge
/// follows the structure: what a block or a branch learned is forgotten at its end (a branch may have left it before
/// the check); a loop keeps what the code before it knew.
///
/// A loop's first check moves before the loop when nothing but the loop's own condition is evaluated before it: in
/// `block; loop; cond; br_if 1; (block;) pure..; check` with a condition that only reads (locals, constants, loads,
/// arithmetic that cannot trap), the check is made before the loop under a copy of the condition, so it runs exactly
/// when the loop's first turn would have run it, and before the same effects (there are none in between). Inside the
/// (After the condition, instructions that only compute values into the function's locals may come before the check
/// as well: nothing outside the function can tell the order of those and the initialization.) Inside the
/// loop it is then one that follows another, and goes: the loop calls nothing for it, and the passes that need a
/// loop without calls (LoopLoads) see one.
/// </summary>
static class InitChecks
{
    const int Len = 7; // i32.const flag; i32.load; i32.const -1; i32.ne; if; call init; end

    static WasmFunction? CheckAt(List<Instr> b, int i)
    {
        if (i + Len > b.Count) return null;
        if (b[i].Op != Op.I32Const || b[i].Ref == null) return null;
        if (b[i + 1].Op != Op.I32Load || b[i + 2].Op != Op.I32Const || b[i + 2].Imm != -1 || b[i + 2].Ref != null) return null;
        if (b[i + 3].Op != Op.I32Ne || b[i + 4].Op != Op.If || b[i + 4].Ref != null || b[i + 6].Op != Op.End) return null;
        if (b[i + 5].Op != Op.Call || b[i + 5].Ref is not WasmFunction g || !g.Name.StartsWith("static init ", StringComparison.Ordinal)) return null;
        return g;
    }

    static bool Pure(Op op) => op is Op.LocalGet or Op.I32Const or Op.I64Const or Op.I32Add or Op.I32Sub or Op.I32Mul or Op.I32And
        or Op.I32LtS or Op.I32LtU or Op.I32GtS or Op.I32GtU or Op.I32LeS or Op.I32LeU or Op.I32GeS or Op.I32GeU or Op.I32Eq or Op.I32Ne
        or Op.I32Eqz or Op.I32Load or Op.I64ExtendI32S or Op.I32WrapI64;

    /// <summary>An instruction with no effect outside the function's own values, that cannot fail or branch.</summary>
    static bool Quiet(List<Instr> b, int k)
    {
        var op = b[k].Op;
        if (Pure(op) || op is Op.LocalSet or Op.LocalTee or Op.Drop or Op.I32Or or Op.I32Xor or Op.I32Shl or Op.I32ShrS or Op.I32ShrU) return true;
        if (op == Op.Block && b[k].Ref == null) return true;
        // (a division by a constant that is neither 0 nor -1 cannot trap)
        if (op is Op.I32DivU or Op.I32RemU or Op.I32DivS or Op.I32RemS) return k > 0 && b[k - 1].Op == Op.I32Const && b[k - 1].Ref == null && b[k - 1].Imm != 0 && b[k - 1].Imm != -1;
        return false;
    }

    static int Push(Op op) => op is Op.LocalGet or Op.I32Const or Op.I64Const ? 1 : op is Op.I32Eqz or Op.I32Load or Op.I64ExtendI32S or Op.I32WrapI64 ? 0 : -1;

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        var b = f.Body;
        bool any = false;
        foreach (var ins in b) if (ins.Op == Op.Call && ins.Ref is WasmFunction { Name: var n } && n.StartsWith("static init ", StringComparison.Ordinal)) { any = true; break; }
        if (!any) return false;
        bool changed = false;
        // (a check moved before an inner loop may be the first of the loop around it: again, a few times)
        for (int round = 0; round < 4 && Hoist(f); round++) changed = true;
        return Dedupe(f) | changed;
    }

    static bool Hoist(WasmFunction f)
    {
        var b = f.Body;
        List<Instr>? o = null;
        int copied = 0;
        for (int i = 0; i < b.Count; i++)
        {
            if (b[i].Op != Op.Loop || b[i].Ref != null) continue;
            // the loop's start: its condition and exit (if any), the body's block (if any), pushes; then the check
            int k = i + 1, exit = -1, depth = 0;
            while (k < b.Count && Pure(b[k].Op) && CheckAt(b, k) == null) depth += Push(b[k++].Op);
            if (k < b.Count && b[k].Op == Op.BrIf && b[k].Imm == 1)
            {
                // (the copy must be the whole condition and nothing else: one value, taken by the branch)
                if (depth != 1) continue;
                if (CheckAt(b, i + 1) != null) continue;
                exit = k++;
            }
            // (then what no one can observe: values computed and kept in locals, the body's own block)
            while (k < b.Count && Quiet(b, k) && CheckAt(b, k) == null) k++;
            if (CheckAt(b, k) == null) continue;
            // (the loop is inside the block its exit leaves: the copy's exit leaves the copy's own block)
            o ??= [];
            for (int j = copied; j < i; j++) o.Add(b[j]);
            int pos = b[k].Pos;
            if (exit >= 0)
            {
                o.Add(new Instr { Op = Op.Block, Pos = pos });
                for (int j = i + 1; j < exit; j++) o.Add(b[j]);
                o.Add(new Instr { Op = Op.BrIf, Imm = 0, Pos = pos });
            }
            for (int j = k; j < k + Len; j++) o.Add(b[j]);
            if (exit >= 0) o.Add(new Instr { Op = Op.End, Pos = pos });
            // the loop itself, without the check
            for (int j = i; j < k; j++) o.Add(b[j]);
            copied = k + Len;
            i = k + Len - 1;
        }
        if (o == null) return false;
        for (int j = copied; j < b.Count; j++) o.Add(b[j]);
        f.Body = o;
        return true;
    }

    static bool Dedupe(WasmFunction f)
    {
        var b = f.Body;
        var known = new HashSet<WasmFunction>();
        var log = new List<WasmFunction>();
        var scopes = new Stack<(int mark, bool loop)>();
        List<int>? remove = null;
        void Back(int mark) { for (int j = log.Count - 1; j >= mark; j--) known.Remove(log[j]); log.RemoveRange(mark, log.Count - mark); }
        for (int i = 0; i < b.Count; i++)
        {
            if (CheckAt(b, i) is { } g)
            {
                if (known.Contains(g)) (remove ??= []).Add(i);
                else { known.Add(g); log.Add(g); }
                i += Len - 1;
                continue;
            }
            switch (b[i].Op)
            {
                case Op.Block or Op.If or Op.TryTable: scopes.Push((log.Count, false)); break;
                case Op.Loop: scopes.Push((log.Count, true)); break;
                case Op.Else when scopes.Count > 0: Back(scopes.Peek().mark); break;
                case Op.End when scopes.Count > 0:
                    {
                        var s = scopes.Pop();
                        // (a loop's end is reached from its last instruction only: what its own level learned holds)
                        if (!s.loop) Back(s.mark);
                        break;
                    }
            }
        }
        if (remove == null) return false;
        var o = new List<Instr>(b.Count);
        int r = 0;
        for (int i = 0; i < b.Count; i++)
        {
            if (r < remove.Count && remove[r] == i) { i += Len - 1; r++; continue; }
            o.Add(b[i]);
        }
        f.Body = o;
        return true;
    }
}
