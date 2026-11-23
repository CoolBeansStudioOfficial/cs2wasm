namespace Il2Wasm.Wasm;

/// <summary>
/// Section 5: an index check a loop's own condition has already made. In
/// `for (int i = c; i &lt; a.Length; i++) .. a[i] ..` (c a constant not below zero, i changed nowhere but by its
/// `i++` at the loop's end, a not assigned in the loop) every `a[i]` in the body is inside the array: the condition
/// said i &lt; a.Length when the body began, i has not changed since, i is not negative (it began at c and only
/// grew by one, below a length), and an array's or a string's length never changes. The check and its throw are
/// taken out; nothing else moves, so every exception that could be thrown still is, where it was.
///
/// The length loads are the ones the compiler marked (Length): a load at the same offset from another object is a
/// field, which may change.
/// </summary>
static class Loops
{
    /// <summary>The Ref of a load that reads the length of an array or a string.</summary>
    public static readonly object Length = new();

    static bool IsLength(in Instr x) => x.Op == Op.I32Load && ReferenceEquals(x.Ref, Length);

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f, WasmFunction? nonNull)
    {
        var b = f.Body;
        int n = b.Count;
        bool any = false;
        for (int i = 0; i < n && !any; i++) any = b[i].Op == Op.Loop;
        if (!any) return false;
        var end = new int[n];
        var open = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            var op = b[i].Op;
            if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) open.Push(i);
            else if (op == Op.End && open.Count > 0) end[open.Pop()] = i;
        }
        bool[]? remove = null;
        for (int l = 3; l + 8 < n; l++)
        {
            if (b[l].Op != Op.Loop || b[l - 1].Op != Op.Block) continue;
            // the condition: local.get i ; local.get a ; [null check] ; length ; i32.lt_s ; i32.eqz ; br_if 1
            int p = l + 1;
            if (b[p].Op != Op.LocalGet || b[p + 1].Op != Op.LocalGet) continue;
            int iv = (int)b[p].Imm, av = (int)b[p + 1].Imm;
            p += 2;
            if (b[p].Op == Op.Call && ReferenceEquals(b[p].Ref, nonNull)) p++;
            if (!IsLength(b[p]) || b[p + 1].Op != Op.I32LtS || b[p + 2].Op != Op.I32Eqz || b[p + 3].Op != Op.BrIf || b[p + 3].Imm != 1) continue;
            int body = p + 4, e = end[l];
            // i begins at a constant not below zero
            if (b[l - 2].Op != Op.LocalSet || b[l - 2].Imm != iv || b[l - 3].Op != Op.I32Const || b[l - 3].Ref != null || (int)b[l - 3].Imm < 0) continue;
            // its one change: i = i + 1, the last thing before the branch back; a is not assigned
            int inc = e - 2;
            if (inc - 3 < body || b[e - 1].Op != Op.Br || b[e - 1].Imm != 0 || b[inc].Op != Op.LocalSet || b[inc].Imm != iv || b[inc - 1].Op != Op.I32Add
                || b[inc - 2].Op != Op.I32Const || b[inc - 2].Imm != 1 || b[inc - 2].Ref != null || b[inc - 3].Op != Op.LocalGet || b[inc - 3].Imm != iv) continue;
            bool ok = true;
            for (int k = l + 1; k < e && ok; k++)
                if (b[k].Op is Op.LocalSet or Op.LocalTee && (b[k].Imm == av || b[k].Imm == iv && k != inc)) ok = false;
            if (!ok) continue;
            // the checks: local.get i ; [local.tee t ;] local.get a ; length ; i32.ge_u ; if ; call IndexOutOfRange ; [unreachable ;] end
            for (int k = body; k + 5 < inc - 3; k++)
            {
                if (b[k].Op != Op.LocalGet || b[k].Imm != av || !IsLength(b[k + 1]) || b[k + 2].Op != Op.I32GeU || b[k + 3].Op != Op.If) continue;
                if (b[k + 4].Op != Op.Call || b[k + 4].Ref is not WasmFunction h || !h.Name.Contains("ThrowHelpers.IndexOutOfRange", StringComparison.Ordinal)) continue;
                int stop = b[k + 5].Op == Op.Unreachable ? k + 6 : k + 5;
                if (b[stop].Op != Op.End) continue;
                bool tee = b[k - 1].Op == Op.LocalTee && b[k - 2].Op == Op.LocalGet && b[k - 2].Imm == iv;
                bool plain = b[k - 1].Op == Op.LocalGet && b[k - 1].Imm == iv;
                if (!tee && !plain) continue;
                remove ??= new bool[n];
                // (with the index kept in t: the tee becomes a set; without: the index goes too)
                for (int q = tee ? k : k - 1; q <= stop; q++) remove[q] = true;
                if (tee) b[k - 1] = b[k - 1] with { Op = Op.LocalSet };
                k = stop;
            }
        }
        if (remove == null) return false;
        var o = new List<Instr>(n);
        for (int i = 0; i < n; i++) if (!remove[i]) o.Add(b[i]);
        f.Body = o;
        return true;
    }
}
