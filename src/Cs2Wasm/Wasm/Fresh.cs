namespace Cs2Wasm.Wasm;

/// <summary>
/// Stores into a freshly allocated object (section 4: ownership). The allocator returns zeroed memory, so a counted
/// store into a field nothing has written since the allocation has no old value to release: the load of the old
/// value and its release go. Followed through straight-line code from the allocation while the object stays in its
/// local (a call that gets it as its first argument may write the fields that function's summary names: the field
/// offsets it can store through its first parameter, or any when it lets the parameter go elsewhere).
/// </summary>
static class Fresh
{
    // null: may write anything
    static Dictionary<WasmFunction, HashSet<int>?> s_writes = [];

    public static void Run(List<WasmFunction> functions, WasmFunction? allocObject)
    {
        if (allocObject == null) return;
        s_writes = [];
        // summaries to a fixpoint (optimistic: start from nothing written, grow)
        var defined = functions.Where(f => !f.IsImport && f.Type.Params.Length > 0 && f.Type.Params[0] == ValType.I32).ToList();
        foreach (var f in defined) s_writes[f] = [];
        bool changed = true;
        for (int iter = 0; changed && iter < 20; iter++)
        {
            changed = false;
            foreach (var f in defined)
            {
                var cur = s_writes[f];
                if (cur == null) continue;
                var w = Summary(f);
                if (w == null) { s_writes[f] = null; changed = true; }
                else if (!w.IsSubsetOf(cur)) { cur.UnionWith(w); changed = true; }
            }
        }
        if (changed) return; // (no fixpoint: leave everything as it is)
        foreach (var f in functions) if (!f.IsImport) Rewrite(f, allocObject);
    }

    static HashSet<int>? WritesOf(WasmFunction g) => g.IsImport ? null : s_writes.TryGetValue(g, out var w) ? w : null;

    /// <summary>The instruction that takes the value pushed at i, and which of its operands it is (from the first).</summary>
    static (int at, int operand)? Consumer(List<Instr> b, int i)
    {
        int above = 0;
        for (int j = i + 1; j < b.Count; j++)
        {
            if (Effect(b[j]) is not { } e) return null;
            if (e.pop > above) return (j, e.pop - 1 - above);
            above += e.push - e.pop;
        }
        return null;
    }

    static (int pop, int push)? Effect(in Instr ins) => ins.Op switch
    {
        Op.Call => (((WasmFunction)ins.Ref!).Type.Params.Length, ((WasmFunction)ins.Ref!).Type.Results.Length),
        Op.CallIndirect => (((FuncType)ins.Ref!).Params.Length + 1, ((FuncType)ins.Ref!).Results.Length),
        _ => Locals.PlainEffect(ins),
    };

    static IEnumerable<int> Bytes(Instr st) => Enumerable.Range((int)st.Imm, st.Op switch
    {
        Op.I32Store8 or Op.I64Store8 => 1,
        Op.I32Store16 or Op.I64Store16 => 2,
        Op.I64Store or Op.F64Store => 8,
        _ => 4,
    });

    static bool IsStore(Op op) => op >= Op.I32Store && op <= Op.I64Store32;
    static bool IsLoad(Op op) => op >= Op.I32Load && op <= Op.I64Load32U;

    /// <summary>The field offsets f can store through its first parameter; null when it can do anything with it.</summary>
    static HashSet<int>? Summary(WasmFunction f)
    {
        var b = f.Body;
        var w = new HashSet<int>();
        for (int i = 0; i < b.Count; i++)
        {
            var ins = b[i];
            if (ins.Op is Op.LocalSet or Op.LocalTee && ins.Imm == 0) return null;
            if (ins.Op != Op.LocalGet || ins.Imm != 0) continue;
            if (Consumer(b, i) is not var (at, operand)) return null;
            var c = b[at];
            if (IsLoad(c.Op) && operand == 0) continue;
            if (IsStore(c.Op) && operand == 0) { w.UnionWith(Bytes(c)); continue; }
            if (c.Op is Op.I32Eqz or Op.Drop) continue;
            if (c.Op == Op.Call && c.Ref is WasmFunction g)
            {
                if (g.Name is "rt.nonnull" && operand == 0)
                {
                    // (the checked value flows on: its consumer counts)
                    if (Consumer(b, at) is not var (at2, op2)) return null;
                    var c2 = b[at2];
                    if (IsLoad(c2.Op) && op2 == 0) continue;
                    if (IsStore(c2.Op) && op2 == 0) { w.UnionWith(Bytes(c2)); continue; }
                    if (c2.Op == Op.Call && c2.Ref is WasmFunction g2 && op2 == 0 && WritesOf(g2) is { } w3) { w.UnionWith(w3); continue; }
                    return null;
                }
                if (operand == 0 && WritesOf(g) is { } gw) { w.UnionWith(gw); continue; }
            }
            return null;
        }
        return w;
    }

    static void Rewrite(WasmFunction f, WasmFunction alloc)
    {
        var b = f.Body;
        var drop = new HashSet<int>();
        for (int i = 0; i + 1 < b.Count; i++)
        {
            if (b[i].Op != Op.Call || b[i].Ref != alloc) continue;
            if (b[i + 1].Op is not (Op.LocalSet or Op.LocalTee)) continue;
            // the locals holding the object (it moves between them), the instructions whose result is the object, and
            // the stores that put it in a local
            var aliases = new HashSet<int> { (int)b[i + 1].Imm };
            var pushes = new HashSet<int>();
            var moves = new HashSet<int> { i + 1 };
            if (b[i + 1].Op == Op.LocalTee) pushes.Add(i + 1);
            // (bytes of the object that may have been written; counted from where the store's base is pushed)
            var written = new HashSet<int>();
            for (int j = i + 2; j < b.Count; j++)
            {
                var y = b[j];
                if (Effect(y) is null) break; // control flow: stop following
                if (y.Op is Op.LocalSet or Op.LocalTee && !moves.Contains(j) && aliases.Remove((int)y.Imm) && aliases.Count == 0) break;
                if (y.Op == Op.LocalGet && aliases.Contains((int)y.Imm)) pushes.Add(j);
                if (!pushes.Contains(j)) continue;
                if (Consumer(b, j) is not var (at, operand)) break;
                var c = b[at];
                if (IsLoad(c.Op) && operand == 0)
                {
                    // the counted store's old value: get o; i32.load off; get o; value; i32.store off; call rt.release
                    if (y.Op == Op.LocalGet && c.Op == Op.I32Load && at == j + 1 && !Enumerable.Range((int)c.Imm, 4).Any(written.Contains) && Consumer(b, at) is var (rel, rop) && rop == 0
                        && b[rel].Op == Op.Call && b[rel].Ref is WasmFunction { Name: "rt.release" } && IsRcStore(b, at + 1, rel, aliases, (int)c.Imm))
                    {
                        drop.Add(j); drop.Add(at); drop.Add(rel);
                    }
                    continue;
                }
                if (IsStore(c.Op) && operand == 0) { written.UnionWith(Bytes(c)); continue; }
                if (c.Op == Op.Call && c.Ref is WasmFunction g && operand == 0 && WritesOf(g) is { } gw) { written.UnionWith(gw); continue; }
                if (c.Op is Op.LocalSet or Op.LocalTee)
                {
                    // moved or copied to another local
                    moves.Add(at);
                    aliases.Add((int)c.Imm);
                    if (c.Op == Op.LocalTee) pushes.Add(at);
                    continue;
                }
                break; // the object leaves its locals: stop following
            }
        }
        if (drop.Count == 0) return;
        var nb = new List<Instr>(b.Count);
        for (int i = 0; i < b.Count; i++)
        {
            if (!drop.Contains(i)) { nb.Add(b[i]); continue; }
            if (b[i].Pos >= 0 && i + 1 < b.Count && !drop.Contains(i + 1) && b[i + 1].Pos < 0) b[i + 1] = b[i + 1] with { Pos = b[i].Pos };
        }
        f.Body = nb;
    }

    /// <summary>Between from and rel: `local.get o; value; i32.store off` and nothing else that refers to o.</summary>
    static bool IsRcStore(List<Instr> b, int from, int rel, HashSet<int> aliases, int off)
    {
        if (b[from].Op != Op.LocalGet || !aliases.Contains((int)b[from].Imm)) return false;
        if (Consumer(b, from) is not var (st, sop) || sop != 0 || b[st].Op != Op.I32Store || b[st].Imm != off || st + 1 != rel) return false;
        for (int k = from + 1; k < st; k++)
            if (b[k].Op is Op.LocalGet or Op.LocalSet or Op.LocalTee && aliases.Contains((int)b[k].Imm)) return false;
        return true;
    }
}
