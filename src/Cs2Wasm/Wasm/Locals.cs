namespace Cs2Wasm.Wasm;

/// <summary>
/// The final pass over local traffic (section 5): liveness over the function's control flow (branches, loops, and the
/// edges an exception takes to a try_table's catch), then
/// - a store to a local nobody reads afterwards is dropped (`local.tee` of it removed);
/// - a value stored to a local and read once, a few instructions later, with nothing in between that reaches below it
///   on the stack, stays on the stack instead (`local.set x … local.get x` → `…`);
/// - locals nothing uses any more are removed, the rest grouped by type (fewer local declarations).
/// </summary>
static class Locals
{
    public static void Run(WasmFunction f)
    {
        if (f.IsImport || f.Body.Count == 0) return;
        for (int round = 0; round < 3; round++)
            if (!Round(f)) break;
        Renumber(f);
    }

    sealed class Flow
    {
        public int N, Words;
        public int[] Match = [];            // block/loop/if/try_table -> its end; else -> its end
        public int[] ElseOf = [];           // if -> its else (or -1)
        public List<int>[] Succ = [];       // normal successors (N = exit)
        public List<int>[] Exc = [];        // where an exception thrown here goes
        public ulong[][] LiveIn = [], LiveOut = [];
    }

    static bool IsThrowing(Op op) => op is Op.Call or Op.CallIndirect or Op.Throw or Op.ThrowRef;

    static Flow Analyze(WasmFunction f)
    {
        var b = f.Body;
        int n = b.Count;
        var fl = new Flow { N = n, Match = new int[n], ElseOf = new int[n], Succ = new List<int>[n], Exc = new List<int>[n] };
        Array.Fill(fl.Match, -1); Array.Fill(fl.ElseOf, -1);
        // structure
        var st = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            switch (b[i].Op)
            {
                case Op.Block: case Op.Loop: case Op.If: case Op.TryTable: st.Push(i); break;
                case Op.Else: fl.ElseOf[st.Peek()] = i; break;
                case Op.End:
                    if (st.Count == 0) break;
                    int o = st.Pop();
                    fl.Match[o] = i;
                    if (fl.ElseOf[o] >= 0) fl.Match[fl.ElseOf[o]] = i;
                    break;
            }
        }
        // successors (label targets resolved with the control stack at each instruction)
        var ctl = new List<int>();
        int Target(int depth, int below)
        {
            // depth counts from the innermost construct of ctl[0..below)
            int k = below - 1 - depth;
            if (k < 0) return n; // the function's own label: its end
            int c = ctl[k];
            return b[c].Op == Op.Loop ? c : fl.Match[c];
        }
        for (int i = 0; i < n; i++)
        {
            var ins = b[i];
            var s = new List<int>();
            var exc = new List<int>();
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
                    s.Add(fl.ElseOf[i] >= 0 ? fl.ElseOf[i] + 1 : fl.Match[i]);
                    break;
                case Op.Else: s.Add(fl.Match[i]); break;
                default: s.Add(i + 1); break;
            }
            if (IsThrowing(ins.Op))
                for (int k = ctl.Count - 1; k >= 0; k--)
                    if (b[ctl[k]].Op == Op.TryTable)
                        foreach (var c in ((TryTableInfo)b[ctl[k]].Ref!).Catches) exc.Add(Target(c.Label, k));
            fl.Succ[i] = s;
            fl.Exc[i] = exc;
            if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) ctl.Add(i);
            else if (ins.Op == Op.End && ctl.Count > 0) ctl.RemoveAt(ctl.Count - 1);
        }
        // liveness
        int nl = f.Type.Params.Length + f.Locals.Count;
        int w = (nl + 63) / 64;
        fl.Words = w;
        fl.LiveIn = new ulong[n + 1][]; fl.LiveOut = new ulong[n][];
        for (int i = 0; i <= n; i++) fl.LiveIn[i] = new ulong[w];
        for (int i = 0; i < n; i++) fl.LiveOut[i] = new ulong[w];
        bool changed = true;
        var tmp = new ulong[w];
        while (changed)
        {
            changed = false;
            for (int i = n - 1; i >= 0; i--)
            {
                Array.Clear(tmp);
                foreach (var s in fl.Succ[i]) Or(tmp, fl.LiveIn[Math.Min(s, n)]);
                foreach (var s in fl.Exc[i]) Or(tmp, fl.LiveIn[Math.Min(s, n)]);
                tmp.CopyTo(fl.LiveOut[i], 0);
                var ins = b[i];
                if (ins.Op is Op.LocalSet or Op.LocalTee) Clear(tmp, (int)ins.Imm);
                if (ins.Op == Op.LocalGet) Set(tmp, (int)ins.Imm);
                // (an exception leaves before the instruction's own store: what the catch reads is live before it too)
                foreach (var s in fl.Exc[i]) Or(tmp, fl.LiveIn[Math.Min(s, n)]);
                if (!tmp.AsSpan().SequenceEqual(fl.LiveIn[i])) { tmp.CopyTo(fl.LiveIn[i], 0); changed = true; }
            }
        }
        return fl;
    }

    static void Or(ulong[] a, ulong[] b) { for (int i = 0; i < a.Length; i++) a[i] |= b[i]; }
    static void Set(ulong[] a, int x) => a[x >> 6] |= 1UL << (x & 63);
    static void Clear(ulong[] a, int x) => a[x >> 6] &= ~(1UL << (x & 63));
    static bool Has(ulong[] a, int x) => (a[x >> 6] & (1UL << (x & 63))) != 0;

    /// <summary>(values popped, values pushed) of a plain instruction; null for control flow and what this pass does not model.</summary>
    static (int pop, int push)? Effect(in Instr ins)
    {
        var op = ins.Op;
        switch (op)
        {
            case Op.Nop: return (0, 0);
            case Op.Drop: return (1, 0);
            case Op.Select: return (3, 1);
            case Op.LocalGet: case Op.GlobalGet: return (0, 1);
            case Op.LocalSet: case Op.GlobalSet: return (1, 0);
            case Op.LocalTee: return (1, 1);
            case Op.I32Const: case Op.I64Const: case Op.F32Const: case Op.F64Const: case Op.MemorySize: return (0, 1);
            case Op.MemoryGrow: return (1, 1);
            case Op.MemoryCopy: case Op.MemoryFill: case Op.MemoryInit: return (3, 0);
            case Op.DataDrop: return (0, 0);
            case Op.Call:
                {
                    var t = ((WasmFunction)ins.Ref!).Type;
                    return (t.Params.Length, t.Results.Length);
                }
            case Op.CallIndirect:
                {
                    var t = (FuncType)ins.Ref!;
                    return (t.Params.Length + 1, t.Results.Length);
                }
        }
        int v = (int)op;
        if (op >= Op.I32Load && op <= Op.I64Load32U) return (1, 1);
        if (op >= Op.I32Store && op <= Op.I64Store32) return (2, 0);
        if (op == Op.I32Eqz || op == Op.I64Eqz) return (1, 1);
        if (op >= Op.I32Eq && op <= Op.I32GeU || op >= Op.I64Eq && op <= Op.I64GeU || op >= Op.F32Eq && op <= Op.F64Ge) return (2, 1);
        if (op >= Op.I32Clz && op <= Op.I32Popcnt || op >= Op.I64Clz && op <= Op.I64Popcnt) return (1, 1);
        if (op >= Op.I32Add && op <= Op.I32Rotr || op >= Op.I64Add && op <= Op.I64Rotr) return (2, 1);
        if (op >= Op.F32Abs && op <= Op.F32Sqrt || op >= Op.F64Abs && op <= Op.F64Sqrt) return (1, 1);
        if (op >= Op.F32Add && op <= Op.F32Copysign || op >= Op.F64Add && op <= Op.F64Copysign) return (2, 1);
        if (v >= 0xA7 && v <= 0xC4) return (1, 1);
        if (op >= Op.I32TruncSatF32S && op <= Op.I64TruncSatF64U) return (1, 1);
        return null;
    }

    static bool Round(WasmFunction f)
    {
        var fl = Analyze(f);
        var b = f.Body;
        int n = b.Count;
        bool any = false;
        var nop = new Instr { Op = Op.Nop, Pos = -1 };
        bool ExcReads(int k, int x)
        {
            foreach (var s in fl.Exc[k]) if (Has(fl.LiveIn[Math.Min(s, n)], x)) return true;
            return false;
        }
        for (int i = 0; i < n; i++)
        {
            var ins = b[i];
            if (ins.Op is not (Op.LocalSet or Op.LocalTee)) continue;
            int x = (int)ins.Imm;
            if (!Has(fl.LiveOut[i], x))
            {
                // nobody reads it
                b[i] = ins.Op == Op.LocalSet ? ins with { Op = Op.Drop } : ins with { Op = Op.Nop };
                any = true;
                continue;
            }
            if (ins.Op != Op.LocalSet) continue;
            // the next access to x, within straight-line code that leaves the value where it is on the stack
            int h = 0;
            for (int j = i + 1; j < n; j++)
            {
                var y = b[j];
                if (y.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee && (int)y.Imm == x)
                {
                    if (y.Op == Op.LocalGet && h == 0 && !Has(fl.LiveOut[j], x))
                    {
                        // value flows straight to its one use
                        b[i] = nop with { Pos = ins.Pos };
                        b[j] = nop with { Pos = y.Pos };
                        any = true;
                    }
                    break;
                }
                if (Effect(y) is not { } e) break;
                if (h < e.pop) break;
                if (IsThrowing(y.Op) && ExcReads(j, x)) break;
                h += e.push - e.pop;
            }
        }
        if (!any) return false;
        // compact: no nops; a removed instruction's source position moves to the next one
        var outl = new List<Instr>(n);
        int carry = -1;
        foreach (var ins in b)
        {
            if (ins.Op == Op.Nop) { if (ins.Pos >= 0 && carry < 0) carry = ins.Pos; continue; }
            var z = ins;
            if (carry >= 0 && z.Pos < 0) z.Pos = carry;
            carry = -1;
            outl.Add(z);
        }
        f.Body = outl;
        Peephole.Run(f);
        return true;
    }

    /// <summary>Removes locals no instruction refers to; the rest are grouped by type (each run is one declaration).</summary>
    static void Renumber(WasmFunction f)
    {
        int np = f.Type.Params.Length;
        var used = new bool[f.Locals.Count];
        foreach (var ins in f.Body)
            if (ins.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee && ins.Imm >= np) used[ins.Imm - np] = true;
        var order = Enumerable.Range(0, f.Locals.Count).Where(i => used[i]).OrderBy(i => (int)f.Locals[i]).ThenBy(i => i).ToList();
        if (order.Count == f.Locals.Count && order.SequenceEqual(Enumerable.Range(0, f.Locals.Count))) return;
        var map = new int[f.Locals.Count];
        for (int k = 0; k < order.Count; k++) map[order[k]] = np + k;
        var b = f.Body;
        for (int i = 0; i < b.Count; i++)
            if (b[i].Op is Op.LocalGet or Op.LocalSet or Op.LocalTee && b[i].Imm >= np) b[i] = b[i] with { Imm = map[b[i].Imm - np] };
        f.Locals = order.Select(i => f.Locals[i]).ToList();
    }
}
