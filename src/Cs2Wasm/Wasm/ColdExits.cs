namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: the calls a count's other path makes, outside the loop that counts. A count written out in a loop
/// (RcFastPaths.cs, WasmModule.ExpandLeafReleases) is `if (count above the bound) { count changed in place } else
/// { a call: an arena's member, an immortal object, the last reference and what its end runs }`. The else is taken
/// seldom, but it is a call that comes back, in the loop: the engine (V8's register allocator) then keeps every
/// value that is alive across it in memory, written where the value is made, on every turn (measured, with the
/// engine's own listing, `node --print-wasm-code`: the churn loop's array store had five such writes and a read a
/// turn, tools/churn-count.cs: the same loop with its counts by hand and no call 39 us a frame, the compiler's 43).
/// A call that never comes back does not cost that (NoReturn.cs); a call that does cannot be marked.
/// What the engine does keep in registers: a loop whose body has no such call. So the loop is given an outer loop
/// and a local P:
///     P = 0
///     block X { loop O { block H { br_if H (P)
///                                  loop L { the body; at a count's other path: P = j, br O }
///                                  br X }
///                        P = 0; path j's own code, then what was left of the turn: the rest of each block it was
///                        in, as blocks again, and the rest of the body; a branch to L there is a branch to O; br X } }
/// The other path's code runs at the same point of the program with the same locals: only the place of the code
/// changes (the control flow is the same, no value moves). What it uses of the turn reaches it as the outer loop's
/// own values (the engine's phis at O), which the engine writes to memory only where the call is; written after L
/// in the code's order, so that what L uses and the loop does not change stays in registers through L. A value the
/// turn has on the operand stack at the count (the address an element is stored at) is also put into a local where
/// it is made (a branch drops it), and pushed again before the path's code.
/// Only where it is exact and of use: an innermost loop without a result or a try_table inside, every value on the
/// stack at the count of a type that is known, and no other call in the loop that comes back (then the values are
/// in memory anyway, and nothing is done). CS2WASM_NO_COLD_EXITS=1: off. CS2WASM_COLD_EXITS_WITHOUT=stack|rest|
/// labels|inner: a rule taken out (test 208 then prints something else, or the module is not valid).
/// </summary>
static class ColdExits
{
    static readonly bool s_off = Environment.GetEnvironmentVariable("CS2WASM_NO_COLD_EXITS") == "1";
    static readonly string s_without = Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_WITHOUT") ?? "";
    static readonly bool s_stats = Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_STATS") == "1";
    static readonly int s_maxBody = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_BODY"), out var mb) ? mb : 400;
    static readonly int s_maxPoints = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_POINTS"), out var mp) ? mp : 4;
    internal static int Loops, Points;

    sealed class Frame
    {
        public int Start;                 // the Block or If instruction (the Loop for the body itself)
        public bool InElse;
        public List<int> Made = [];       // the stack inside this frame: the instruction that pushed each value
        public bool Dead;                 // after a branch that does not go on: the stack is not followed until the frame's else or end
    }

    sealed class Point
    {
        public int If, Else, End;
        public int[] Chain = [];          // the frames it is in, the loop's own child first: their Block or If instruction
        public bool[] ChainElse = [];
        public int[][] Stack = [];        // the values on the stack at the count: [0] the body's own, [k] chain frame k-1's; the instructions that made them
        public int[][] Saved = [];        // the locals they are kept in
    }

    static bool Opens(Op op) => op is Op.Block or Op.Loop or Op.If or Op.TryTable;

    /// <summary>A count's test and its common case: `local.get p; i32.load 0; local.tee c; i32.const 0|1; i32.gt_s;
    /// if; local.get p; local.get c; i32.const 1; i32.add|sub; i32.store 0; else`, the `if` at k.</summary>
    static bool IsCount(List<Instr> b, int k)
    {
        if (k < 5 || k + 6 >= b.Count || b[k].Op != Op.If || b[k].Ref != null) return false;
        if (b[k - 1].Op != Op.I32GtS || b[k - 2].Op != Op.I32Const || b[k - 2].Imm is not (0 or 1) || b[k - 2].Ref != null) return false;
        if (b[k - 3].Op != Op.LocalTee || b[k - 4].Op != Op.I32Load || b[k - 4].Imm != 0 || b[k - 5].Op is not (Op.LocalGet or Op.LocalTee)) return false;
        long p = b[k - 5].Imm, c = b[k - 3].Imm;
        return b[k + 1].Op == Op.LocalGet && b[k + 1].Imm == p && b[k + 2].Op == Op.LocalGet && b[k + 2].Imm == c
            && b[k + 3].Op == Op.I32Const && b[k + 3].Imm == 1 && b[k + 3].Ref == null && b[k + 4].Op == (b[k - 2].Imm == 0 ? Op.I32Add : Op.I32Sub)
            && b[k + 5].Op == Op.I32Store && b[k + 5].Imm == 0 && b[k + 6].Op == Op.Else;
    }

    /// <summary>The type of the one value an instruction pushes; null when it is not known here.</summary>
    static ValType? Made(WasmFunction f, List<Instr> b, int i, Dictionary<int, int> startOf)
    {
        var ins = b[i];
        var op = ins.Op;
        switch (op)
        {
            case Op.LocalGet or Op.LocalTee:
                {
                    int n = (int)ins.Imm, np = f.Type.Params.Length;
                    return n < np ? f.Type.Params[n] : f.Locals[n - np];
                }
            case Op.I32Const or Op.MemorySize or Op.MemoryGrow: return ValType.I32;
            case Op.I64Const: return ValType.I64;
            case Op.F32Const: return ValType.F32;
            case Op.F64Const: return ValType.F64;
            case Op.Call: return ins.Ref is WasmFunction { Type.Results.Length: 1 } g ? g.Type.Results[0] : null;
            case Op.CallIndirect: return ins.Ref is FuncType { Results.Length: 1 } t ? t.Results[0] : null;
            case Op.End: return startOf.TryGetValue(i, out int s) && b[s].Ref is ValType bt ? bt : null;
            case Op.I32Load or Op.I32Load8S or Op.I32Load8U or Op.I32Load16S or Op.I32Load16U: return ValType.I32;
            case Op.F32Load: return ValType.F32;
            case Op.F64Load: return ValType.F64;
            case Op.I64Load or Op.I64Load8S or Op.I64Load8U or Op.I64Load16S or Op.I64Load16U or Op.I64Load32S or Op.I64Load32U: return ValType.I64;
            case Op.I32WrapI64 or Op.I32ReinterpretF32 or Op.I32Extend8S or Op.I32Extend16S: return ValType.I32;
            case Op.I64ExtendI32S or Op.I64ExtendI32U or Op.I64ReinterpretF64 or Op.I64Extend8S or Op.I64Extend16S or Op.I64Extend32S: return ValType.I64;
            case Op.F32DemoteF64 or Op.F32ReinterpretI32: return ValType.F32;
            case Op.F64PromoteF32 or Op.F64ReinterpretI64: return ValType.F64;
        }
        if (op == Op.I32Eqz || op == Op.I64Eqz || op >= Op.I32Eq && op <= Op.F64Ge) return ValType.I32;
        if (op >= Op.I32Clz && op <= Op.I32Rotr) return ValType.I32;
        if (op >= Op.I64Clz && op <= Op.I64Rotr) return ValType.I64;
        if (op >= Op.F32Abs && op <= Op.F32Copysign) return ValType.F32;
        if (op >= Op.F64Abs && op <= Op.F64Copysign) return ValType.F64;
        if (op >= Op.I32TruncF32S && op <= Op.I32TruncF64U || op >= Op.I32TruncSatF32S && op <= Op.I32TruncSatF64U) return ValType.I32;
        if (op >= Op.I64TruncF32S && op <= Op.I64TruncF64U || op >= Op.I64TruncSatF32S && op <= Op.I64TruncSatF64U) return ValType.I64;
        if (op >= Op.F32ConvertI32S && op <= Op.F32ConvertI64U) return ValType.F32;
        if (op >= Op.F64ConvertI32S && op <= Op.F64ConvertI64U) return ValType.F64;
        return null;
    }

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        if (s_off || f.IsImport || f.ForwardTo != null) return false;
        var b = f.Body;
        // the innermost loops, last first (a change moves only what comes after it)
        var loops = new List<(int start, int end)>();
        {
            var open = new List<(int at, bool inner)>();
            for (int i = 0; i < b.Count; i++)
            {
                var op = b[i].Op;
                if (Opens(op))
                {
                    // (a loop or a try_table inside a loop: that loop is left alone)
                    if (op is Op.Loop or Op.TryTable && s_without != "inner") for (int k = 0; k < open.Count; k++) open[k] = (open[k].at, false);
                    open.Add((i, op == Op.Loop));
                }
                else if (op == Op.End && open.Count > 0)
                {
                    var (at, inner) = open[^1];
                    open.RemoveAt(open.Count - 1);
                    if (inner) loops.Add((at, i));
                }
            }
        }
        bool changed = false;
        for (int n = loops.Count - 1; n >= 0; n--)
            if (Rewrite(f, loops[n].start, loops[n].end)) changed = true;
        return changed;
    }

    static bool Why(WasmFunction f, int ls, int le, int why, int at = -1)
    {
        if (s_stats)
        {
            bool has = false;
            for (int i = ls; i < le; i++) if (IsCount(f.Body, i)) { has = true; break; }
            if (has) Console.Error.WriteLine($"cold exits: not ({why}) a loop of {le - ls} in {f.Name}");
            if (has && at >= 0 && Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_DUMP") is { } dn && f.Name.Contains(dn)) for (int q = Math.Max(ls, at - 24); q <= at; q++) Console.Error.WriteLine($"  {q} {f.Body[q]}");
        }
        return false;
    }

    static bool Rewrite(WasmFunction f, int ls, int le)
    {
        var b = f.Body;
        if (b[ls].Ref != null || le - ls > s_maxBody) return Why(f, ls, le, 1);
        var frames = new List<Frame> { new() { Start = ls } };
        var points = new List<Point>();
        int coldUntil = -1;   // inside a count's other path up to here
        var elseOf = new Dictionary<int, int>();
        var endOf = new Dictionary<int, int>();
        var startOf = new Dictionary<int, int>();
        static bool Pop(Frame fr, int n)
        {
            if (fr.Dead) return true;
            if (fr.Made.Count < n) return false;
            fr.Made.RemoveRange(fr.Made.Count - n, n);
            return true;
        }
        for (int i = ls + 1; i < le; i++)
        {
            var ins = b[i];
            var top = frames[^1];
            switch (ins.Op)
            {
                case Op.Block:
                case Op.If:
                    {
                        if (ins.Ref is FuncType) return Why(f, ls, le, 2);
                        if (ins.Op == Op.If && !Pop(top, 1)) return Why(f, ls, le, 10);
                        if (ins.Op == Op.If && i > coldUntil && !top.Dead && IsCount(b, i))
                        {
                            // every value on the stack here, in the body and in each block the count is in: of a known type
                            bool known = true;
                            var pt = new Point { If = i, Chain = new int[frames.Count - 1], ChainElse = new bool[frames.Count - 1], Stack = new int[frames.Count][] };
                            for (int k = 0; k < frames.Count && known; k++)
                            {
                                if (k > 0) { pt.Chain[k - 1] = frames[k].Start; pt.ChainElse[k - 1] = frames[k].InElse; }
                                pt.Stack[k] = frames[k].Made.ToArray();
                                foreach (int at in pt.Stack[k]) if (Made(f, b, at, startOf) == null) known = false;
                            }
                            if (known) points.Add(pt);
                        }
                        frames.Add(new Frame { Start = i, Dead = top.Dead });
                        break;
                    }
                case Op.Loop:
                case Op.TryTable:
                    return Why(f, ls, le, 3);
                case Op.Else:
                    elseOf[top.Start] = i;
                    top.InElse = true; top.Made.Clear(); top.Dead = frames[^2].Dead;
                    if (points.Count > 0 && points[^1].If == top.Start && points[^1].Else == 0) { points[^1].Else = i; coldUntil = int.MaxValue; }
                    break;
                case Op.End:
                    {
                        endOf[top.Start] = i; startOf[i] = top.Start;
                        if (points.Count > 0 && points[^1].If == top.Start && points[^1].End == 0) { points[^1].End = i; coldUntil = i; }
                        frames.RemoveAt(frames.Count - 1);
                        var outer = frames[^1];
                        if (!outer.Dead && b[top.Start].Ref != null) outer.Made.Add(i);
                        break;
                    }
                case Op.Br:
                case Op.BrTable:
                case Op.Return:
                case Op.Unreachable:
                case Op.Throw:
                case Op.ThrowRef:
                    top.Dead = true;
                    break;
                case Op.BrIf:
                    if (!Pop(top, 1)) return Why(f, ls, le, 10);
                    break;
                default:
                    {
                        // (a call that comes back, a copy, a fill, memory's growth: the engine's values are in memory for it anyway)
                        if (ins.Op is Op.Call or Op.CallIndirect && i > coldUntil && !(i + 1 < le && b[i + 1].Op == Op.Unreachable)) return Why(f, ls, le, 4, i);
                        if (ins.Op is Op.MemoryCopy or Op.MemoryFill or Op.MemoryGrow or Op.MemoryInit && i > coldUntil) return Why(f, ls, le, 5);
                        if ((int)ins.Op >= 0xFE00) return Why(f, ls, le, 6);
                        if (Locals.PlainEffect(ins) is not { } e) return Why(f, ls, le, 7);
                        if (!Pop(top, e.pop)) return Why(f, ls, le, 10);
                        if (e.push > 1) return Why(f, ls, le, 11);
                        if (!top.Dead && e.push == 1) top.Made.Add(i);
                        break;
                    }
            }
        }
        if (points.Count == 0 || points.Count > s_maxPoints || frames.Count != 1) return Why(f, ls, le, 8);
        foreach (var pt in points) if (pt.Else == 0 || pt.End == 0) return Why(f, ls, le, 9);

        int np = f.Type.Params.Length;
        int P = np + f.Locals.Count;
        f.Locals.Add(ValType.I32);
        // the stack's values at each count: a local each, set where the value is made
        var keep = new Dictionary<int, List<int>>();
        foreach (var pt in points)
        {
            pt.Saved = new int[pt.Stack.Length][];
            for (int k = 0; k < pt.Stack.Length; k++)
            {
                pt.Saved[k] = new int[pt.Stack[k].Length];
                for (int v = 0; v < pt.Stack[k].Length; v++)
                {
                    int at = pt.Stack[k][v];
                    int local = np + f.Locals.Count;
                    f.Locals.Add(Made(f, b, at, startOf)!.Value);
                    pt.Saved[k][v] = local;
                    if (!keep.TryGetValue(at, out var l)) keep[at] = l = [];
                    l.Add(local);
                }
            }
        }
        var o = new List<Instr>(b.Count + 3 * (le - ls));
        for (int i = 0; i < ls; i++) o.Add(b[i]);
        int pos = b[ls].Pos, inl = b[ls].Inl;
        void A(Op op, long imm = 0) => o.Add(new Instr { Op = op, Imm = imm, Pos = pos, Inl = inl });
        bool noLabels = s_without == "labels", noRest = s_without == "rest", noStack = s_without == "stack";
        void One(int i, Instr ins)
        {
            o.Add(ins);
            if (keep.TryGetValue(i, out var l)) foreach (int local in l) o.Add(new Instr { Op = Op.LocalTee, Imm = local, Pos = ins.Pos, Inl = ins.Inl });
        }

        // a copy of b[from..to) whose first instruction is `depth` blocks inside L: a branch to L or past it by `shiftL`
        // and `shiftOut` more
        void Copy(int from, int to, int depth, int shiftL, int shiftOut)
        {
            for (int i = from; i < to; i++)
            {
                var ins = b[i];
                switch (ins.Op)
                {
                    case Op.Block or Op.If: depth++; break;
                    case Op.End: depth--; break;
                    case Op.Br or Op.BrIf:
                        if (!noLabels) { if (ins.Imm == depth) ins.Imm += shiftL; else if (ins.Imm > depth) ins.Imm += shiftOut; }
                        break;
                    case Op.BrTable:
                        {
                            var t = (int[])((int[])ins.Ref!).Clone();
                            for (int k = 0; k < t.Length; k++) { if (t[k] == depth) t[k] += shiftL; else if (t[k] > depth) t[k] += shiftOut; }
                            if (ins.Imm == depth) ins.Imm += shiftL; else if (ins.Imm > depth) ins.Imm += shiftOut;
                            ins.Ref = t;
                            break;
                        }
                }
                One(i, ins);
            }
        }

        A(Op.I32Const, 0); A(Op.LocalSet, P);
        A(Op.Block);          // X
        A(Op.Loop);           // O
        A(Op.Block);          // H
        A(Op.LocalGet, P); A(Op.BrIf, 0);
        o.Add(b[ls]);         // L
        {
            int at = ls + 1, depth = 0;
            for (int j = 0; j < points.Count; j++)
            {
                var pt = points[j];
                Copy(at, pt.If - 1, depth, 0, 3);
                // (the depth of the count's `if` inside L: the blocks it is in)
                depth = pt.Chain.Length;
                // `count above the bound` becomes `count not above it`: the other path leaves the loop
                o.Add(b[pt.If - 1] with { Op = Op.I32LeS });
                pos = b[pt.If].Pos; inl = b[pt.If].Inl;
                A(Op.If);
                A(Op.I32Const, j + 1); A(Op.LocalSet, P);
                A(Op.Br, depth + 3);
                A(Op.End);
                for (int i = pt.If + 1; i < pt.Else; i++) o.Add(b[i]);
                at = pt.End + 1;
            }
            Copy(at, le, depth, 0, 3);
        }
        o.Add(b[le]);         // L's end
        pos = b[le].Pos; inl = b[le].Inl;
        A(Op.Br, 2);          // the loop ran out: past the other paths
        A(Op.End);            // H
        for (int j = 0; j < points.Count; j++)
        {
            var pt = points[j];
            bool last = j == points.Count - 1;
            int d = last ? 0 : 1;
            pos = b[pt.If].Pos; inl = b[pt.If].Inl;
            if (!last) { A(Op.LocalGet, P); A(Op.I32Const, j + 1); A(Op.I32Eq); A(Op.If); }
            A(Op.I32Const, 0); A(Op.LocalSet, P);
            int n = pt.Chain.Length;
            for (int k = 0; k <= n; k++)
            {
                if (k > 0) o.Add(new Instr { Op = Op.Block, Ref = b[pt.Chain[k - 1]].Ref, Pos = pos, Inl = inl });
                if (!noStack) foreach (int local in pt.Saved[k]) A(Op.LocalGet, local);
            }
            A(Op.Block);
            Copy(pt.Else + 1, pt.End, n + 1, d, d + 1);
            A(Op.End);
            // what is left of each block the count was in, innermost first
            int child = pt.End;
            for (int k = n - 1; k >= 0; k--)
            {
                int start = pt.Chain[k];
                int stop = !pt.ChainElse[k] && elseOf.TryGetValue(start, out int el) ? el : endOf[start];
                if (!noRest) Copy(child + 1, stop, k + 1, d, d + 1);
                One(endOf[start], b[endOf[start]]);
                child = endOf[start];
            }
            Copy(child + 1, le, 0, d, d + 1);
            pos = b[le].Pos; inl = b[le].Inl;
            A(Op.Br, d + 1);   // the loop ran out
            if (!last) A(Op.End);
        }
        A(Op.End);            // O
        A(Op.End);            // X
        for (int i = le + 1; i < b.Count; i++) o.Add(b[i]);
        f.Body = o;
        System.Threading.Interlocked.Increment(ref Loops); System.Threading.Interlocked.Add(ref Points, points.Count);
        if (s_stats) Console.Error.WriteLine($"cold exits: {points.Count} in a loop of {le - ls} in {f.Name}");
        return true;
    }
}
