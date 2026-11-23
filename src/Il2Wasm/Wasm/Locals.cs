namespace Il2Wasm.Wasm;

/// <summary>
/// The final pass over local traffic (section 5): liveness over the function's control flow (branches, loops, and the
/// edges an exception takes to a try_table's catch), then
/// - a store to a local nobody reads afterwards is dropped (`local.tee` of it removed);
/// - a value stored to a local and read once, a few instructions later, with nothing in between that reaches below it
///   on the stack, stays on the stack instead (`local.set x … local.get x` → `…`);
/// - locals nothing uses any more are removed, the rest grouped by type (fewer local declarations).
/// </summary>
static partial class Locals
{
    public static void Run(WasmFunction f)
    {
        if (f.IsImport || f.Body.Count == 0) return;
        for (int round = 0; round < 3; round++)
            if (!Round(f)) break;
        if (Counts(f)) for (int round = 0; round < 2; round++) if (!Round(f)) break;
        Renumber(f);
    }

    /// <summary>On the final bodies (WasmModule, after every other pass: those before read a local written once as one
    /// value), Coalesce and the renumbering.</summary>
    public static void Final(WasmFunction f)
    {
        if (s_noCoalesce || f.IsImport || f.Body.Count == 0) return;
        Coalesce(f);
        Renumber(f);
    }

    static readonly bool s_noCoalesce = Environment.GetEnvironmentVariable("IL2WASM_NO_COALESCE") == "1";
    static readonly bool s_noParamReuse = Environment.GetEnvironmentVariable("IL2WASM_NO_PARAM_REUSE") == "1";

    /// <summary>Locals of one type whose values are never live at once share one local: x and y interfere when one is
    /// written where the other is live after the write, and every local live at the function's entry (it holds its
    /// zero from there) interferes with every other such. Each local in turn takes the first one of its type it does not
    /// interfere with. In the engine's baseline tier every local is a slot of the frame, cleared at each call: fewer
    /// locals, a shorter prologue and a smaller frame (the compiler's temporaries and inlined bodies' locals are many and
    /// short-lived).</summary>
    static void Coalesce(WasmFunction f)
    {
        int np = f.Type.Params.Length, nl = np + f.Locals.Count;
        if (f.Locals.Count < 1 || f.Locals.Count < 2 && np == 0) return;
        var fl = Analyze(f);
        var b = f.Body;
        int w = fl.Words;
        var inter = new ulong[nl][];
        for (int i = 0; i < nl; i++) inter[i] = new ulong[w];
        void Edge(int x, ulong[] live)
        {
            for (int k = 0; k < w; k++)
            {
                ulong bits = live[k];
                while (bits != 0)
                {
                    int t = System.Numerics.BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
                    int y = k * 64 + t;
                    if (y == x || y >= nl) continue;
                    Set(inter[x], y); Set(inter[y], x);
                }
            }
        }
        for (int i = 0; i < b.Count; i++)
            if (b[i].Op is Op.LocalSet or Op.LocalTee && b[i].Imm < nl) Edge((int)b[i].Imm, fl.LiveOut[i]);
        var entry = fl.LiveIn[0];
        for (int x = 0; x < nl; x++) if (Has(entry, x)) Edge(x, entry);
        // (a parameter is written at the entry, with its argument: against every local live there)
        for (int x = 0; x < np; x++) Edge(x, entry);
        // (each local's class: the first local, or parameter, of its type it joined; a parameter stays itself)
        var rep = new int[nl];
        for (int i = 0; i < nl; i++) rep[i] = i;
        var members = new Dictionary<int, List<int>>();
        if (!s_noParamReuse) for (int x = 0; x < np; x++) members[x] = [x];
        ValType TypeOf(int i) => i < np ? f.Type.Params[i] : f.Locals[i - np];
        bool any = false;
        for (int x = np; x < nl; x++)
        {
            var t = f.Locals[x - np];
            foreach (var (r, list) in members)
            {
                if (TypeOf(r) != t) continue;
                bool ok = true;
                foreach (var m in list) if (Has(inter[x], m)) { ok = false; break; }
                if (!ok) continue;
                list.Add(x); rep[x] = r; any = true;
                break;
            }
            if (rep[x] == x) members[x] = [x];
        }
        if (!any) return;
        for (int i = 0; i < b.Count; i++)
            if (b[i].Op is Op.LocalGet or Op.LocalSet or Op.LocalTee && b[i].Imm < nl && rep[b[i].Imm] != b[i].Imm)
                b[i] = b[i] with { Imm = rep[b[i].Imm] };
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

    /// <summary>The functions an exception can come out of (WasmModule.Encode sets it before this pass); null: any call.</summary>
    [ThreadStatic] public static HashSet<WasmFunction>? MayThrow;
    /// <summary>The program can start threads: a borrow is safe only from this thread's own changes.</summary>
    [ThreadStatic] public static bool Threads;

    static bool IsThrowing(Op op) => op is Op.Call or Op.CallIndirect or Op.Throw or Op.ThrowRef;
    static bool IsThrowing(in Instr ins) => ins.Op is Op.CallIndirect or Op.Throw or Op.ThrowRef
        || ins.Op == Op.Call && (MayThrow == null || ins.Ref is not WasmFunction g || MayThrow.Contains(g));

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
            if (IsThrowing(ins))
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

    internal static (int pop, int push)? PlainEffect(in Instr ins) => Effect(ins);

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
        switch (op)
        {
            case Op.V128Load: case Op.I16x8Splat: case Op.I16x8Bitmask: return (1, 1);
            case Op.F64x2Splat: case Op.F64x2ExtractLane: case Op.F64x2Sqrt: return (1, 1);
            case Op.V128Store: return (2, 0);
            case Op.I16x8Eq: case Op.I16x8LtU: case Op.I16x8GtU: case Op.V128And: case Op.V128Or: case Op.I16x8Sub: case Op.I16x8Add: return (2, 1);
        }
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
            if (i > 0 && Propagate(b, fl, i, ExcReads)) { any = true; continue; }
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
                if (IsThrowing(y) && ExcReads(j, x)) break;
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

    /// <summary>
    /// A local set from a constant or from another local (`i32.const c; local.set x`, `local.get a; local.tee x`):
    /// its reads in the straight-line code after it read the constant (or `a`, while `a` is unchanged) instead, and
    /// when no read of this value is left the store goes.
    /// </summary>
    static bool Propagate(List<Instr> b, Flow fl, int i, Func<int, int, bool> excReads)
    {
        var src = b[i - 1];
        int x = (int)b[i].Imm;
        bool isConst = src.Op is Op.I32Const or Op.I64Const or Op.F32Const or Op.F64Const;
        bool isCopy = src.Op == Op.LocalGet && (int)src.Imm != x;
        if (!isConst && !isCopy) return false;
        int a = isCopy ? (int)src.Imm : -1;
        int n = b.Count, s = i + 1;
        bool replaced = false, excRead = false, killed = false;
        for (; s < n; s++)
        {
            var y = b[s];
            if (y.Op is Op.LocalSet or Op.LocalTee && ((int)y.Imm == x || (int)y.Imm == a)) { killed = (int)y.Imm == x; break; }
            if (y.Op == Op.LocalGet && (int)y.Imm == x) { b[s] = src with { Pos = y.Pos }; replaced = true; continue; }
            if (Effect(y) is null) break;
            if (IsThrowing(y) && excReads(s, x)) excRead = true;
        }
        // (a write to a stops the reads that could be replaced; x's later reads then still need the store)
        bool deadAfter = s >= n ? true : killed || !Has(fl.LiveIn[s], x);
        if (!deadAfter || excRead)
            return replaced;
        // the value is read nowhere else: no store
        var nop = new Instr { Op = Op.Nop, Pos = -1 };
        if (b[i].Op == Op.LocalSet) { b[i - 1] = nop with { Pos = b[i - 1].Pos }; b[i] = nop with { Pos = b[i].Pos }; }
        else b[i] = nop with { Pos = b[i].Pos };
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
