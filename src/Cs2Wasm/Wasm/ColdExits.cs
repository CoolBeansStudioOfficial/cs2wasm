namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: the calls a loop makes only on paths that are next to never taken, outside the loop. A count written
/// out in a loop (RcFastPaths.cs, WasmModule.ExpandLeafReleases) has such calls: the count of an object that is in
/// a group or is never freed (rt.retain), the end of an object that has a mark (rt.on_destroy), a block that is its
/// page's first freed one or its last one in use (Rt.FreeSmallOld), a string's own memory (Rt.FreeObject). They are
/// calls that come back, in the loop: the engine (V8's register allocator) then keeps every value that is alive
/// across one of them in memory, written where the value is made, on every turn (measured, with the engine's own
/// listing, `node --print-wasm-code`: the churn loop's array store had five such writes and a read a turn,
/// tools/churn-count.cs: the same loop with its counts by hand and no call 39 us a frame, the compiler's 43). A call
/// that never comes back does not cost that (NoReturn.cs); a call that does cannot be marked.
/// What the engine does keep in registers: a loop whose body has no such call. So the loop is given an outer loop
/// and locals P (which path), C and I0:
///     P = 0
///     block X { loop O { block H { br_if H (P)
///                                  loop L { the body; in place of such a path's code: P = j, br O }
///                                  br X }
///                        block S { P = 0; path j's own code, then what was left of the turn: the rest of each block
///                                  it was in, as blocks again, and the rest of the body; a branch to L there is a
///                                  branch to S; br X }
///                        br_if O (no turn to run in L2)
///                        loop L2 { br_if O (enough turns without such a path); the body as it was }
///                        br X } }
/// The path's code runs at the same point of the program with the same locals: only the place of the code changes
/// (the control flow is the same, no value moves). What it uses of the turn reaches it as the outer loop's own
/// values (the engine's phis at O), which the engine writes to memory only where the call is; written after L in
/// the code's order, so that what L uses and the loop does not change stays in registers through L. A value the
/// turn has on the operand stack there (the address an element is stored at) is also put into a local where it is
/// made (a branch drops it), and pushed again before the path's code.
/// Which paths: an arm of an `if` that calls one of those functions whenever it is run (the call is not inside a
/// further `if` or block of the arm), calls nothing else, and has no branch. The retain is one only where the
/// compiler said the object is of a sealed class that looks flat (FunctionCompiler.ToOwned: in no group, never
/// immortal; of a string or of an object that can be in a group that path is the usual one for literals and
/// members). The loop is changed only when every call in it that comes back is on such a path: then the paths are
/// rare by what they are, and the loop that is left has no call. An object's end itself (its count's other path
/// when the object is of a flat class) stays in the loop, and so that it calls nothing unless one of the paths
/// above is taken, the two calls a free makes there are written out first: Rt.FreeSmallOld's (the block that was
/// kept aside goes back onto its page) as Rt.FreeBack's body, Rt.FreeObject's (a string's memory) as Rt.FreeSmall's,
/// the same code with what is left for a call in one `else` (CS2WASM_NO_INLINE_FREE=1: not written out; kept only
/// when the loop is then changed).
/// L2, the loop as it was, is for a program where such a path is not rare after all (every object that ends has a
/// mark, or a long string of its own): leaving L costs more than the call in it did (5 to 7 ns: the loop's values
/// through three loop tops). A loop with a counter (`i += c; br L` its last instructions, nothing else assigning i)
/// remembers the turn of its last such path in I0: one taken within 8 turns of the last goes on in L2, which is
/// left when 8 turns took none. A loop without one runs C = 4 turns in L2 after each, 8 more after each taken there.
/// Only where it is exact: an innermost loop without a result or a try_table inside, every value on the stack at
/// the `if` of a type that is known; not a container's own end (a destroy function).
/// CS2WASM_NO_COLD_EXITS=1: off. CS2WASM_COLD_EXITS_WITHOUT=stack, rest or labels: a rule taken out (tests 208 and
/// 260 then print something else, or the module is not valid: tools/cold-exits-proof.sh). CS2WASM_COLD_EXITS_ANY=1:
/// every retain's and release's call is such a path (the pass as it was first built; measured: a loop whose other
/// path is taken once in 4 to 50 turns 3 to 17% slower, tools/drop-range.cs, tools/bench's list_sort 3 to 4%).
/// </summary>
static class ColdExits
{
    static readonly bool s_off = Environment.GetEnvironmentVariable("CS2WASM_NO_COLD_EXITS") == "1";
    static readonly bool s_any = Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_ANY") == "1";
    static readonly string s_without = Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_WITHOUT") ?? "";
    static readonly bool s_stats = Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_STATS") == "1";
    static readonly bool s_noInlineFree = Environment.GetEnvironmentVariable("CS2WASM_NO_INLINE_FREE") == "1";
    // (Rt.FreeSmall's body, a string's free, only in a loop of up to this many instructions: in a larger one its
    // values are more than the engine has registers for beside the loop's own, which it then keeps in memory on
    // every turn: tools/churn-deaths.cs, the scene's loop 55.6-56.5 us a frame, 57.8-59.0 with it written out,
    // 53.3-54.5 with the string's free left a call on a path of its own)
    static readonly int s_smallLoop = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_SMALL"), out var sl) ? sl : 160;
    static readonly int s_maxBody = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_BODY"), out var mb) ? mb : 640;
    static readonly int s_maxPoints = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_POINTS"), out var mp) ? mp : 8;
    /// <summary>The module being compiled: Rt.FreeBack and Rt.FreeSmall.</summary>
    internal static WasmModule? Module;
    // (the turns in the loop as it was after a turn took such a path; and after one took it there)
    static readonly int s_turns = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_TURNS"), out var tn) ? tn : 4;
    static readonly int s_stay = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_STAY"), out var st) ? st : 8;
    // (a loop with a counter: a path taken less than this many turns after the last one is "near"; 0: the counter
    // is not looked for)
    static readonly int s_near = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_NEAR"), out var nr) ? nr : 8;
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
        public int If, End;
        public int From, To;              // the path's code: one arm of the `if`, [From, To)
        public int[] Chain = [];          // the frames it is in, the loop's own child first: their Block or If instruction
        public bool[] ChainElse = [];
        public int[][] Stack = [];        // the values on the stack at the `if`: [0] the body's own, [k] chain frame k-1's; the instructions that made them
        public int[][] Saved = [];        // the locals they are kept in
    }

    static bool Opens(Op op) => op is Op.Block or Op.Loop or Op.If or Op.TryTable;

    /// <summary>A call that comes back and is next to never made: see the summary.</summary>
    static bool IsRare(in Instr ins)
    {
        if (ins.Op != Op.Call || ins.Ref is not WasmFunction g) return false;
        if (g.Name == "rt.retain") return ins.Imm2 != 0 || s_any;
        if (g.Name == "rt.on_destroy") return true;
        if (g.Name.StartsWith("Cs2Wasm.Runtime.Rt.FreeSmallOld(", StringComparison.Ordinal) || g.Name.StartsWith("Cs2Wasm.Runtime.Rt.FreeObject(", StringComparison.Ordinal)) return true;
        return s_any && (g.Name is "rt.release" or "rt.destroy" || g.Name.StartsWith("rt.release of ", StringComparison.Ordinal) || g.Name.StartsWith("Cs2Wasm.Runtime.Rt.FreeSmallOld(", StringComparison.Ordinal));
    }

    /// <summary>An arm [from, to) that makes such a call whenever it is run: one at its own level, no other call
    /// anywhere in it, no branch, no loop.</summary>
    static bool RareArm(List<Instr> b, int from, int to)
    {
        int depth = 0;
        bool top = false, any = false;
        for (int i = from; i < to; i++)
        {
            var ins = b[i];
            switch (ins.Op)
            {
                case Op.Block or Op.If: depth++; break;
                case Op.End: depth--; break;
                case Op.Else: break;
                case Op.Loop or Op.TryTable or Op.Br or Op.BrIf or Op.BrTable or Op.Return or Op.Unreachable or Op.Throw or Op.ThrowRef or Op.CallIndirect
                    or Op.MemoryCopy or Op.MemoryFill or Op.MemoryGrow or Op.MemoryInit:
                    return false;
                case Op.Call:
                    if (!IsRare(ins)) return false;
                    any = true;
                    if (depth == 0) top = true;
                    break;
                default:
                    if ((int)ins.Op >= 0xFE00 || Locals.PlainEffect(ins) == null) return false;
                    break;
            }
        }
        return (top || s_any && any) && depth == 0;
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

    /// <summary>rt.on_destroy's own first test (ObjectMarks.MarkedBit of the object's third word: nearly no object has
    /// it), written before its calls in loops: `p; call rt.on_destroy; drop` becomes `if (p's word has the bit)
    /// { p; call rt.on_destroy; drop }`. The function tests it again; without the bit it does nothing but answer 1.</summary>
    static bool MarkTests(WasmFunction f)
    {
        var b = f.Body;
        List<int>? at = null;
        int loops = 0;
        var open = new List<bool>();
        for (int i = 0; i < b.Count; i++)
        {
            var op = b[i].Op;
            if (Opens(op)) { open.Add(op == Op.Loop); if (op == Op.Loop) loops++; }
            else if (op == Op.End && open.Count > 0) { if (open[^1]) loops--; open.RemoveAt(open.Count - 1); }
            if (loops == 0 || op != Op.Call || b[i].Ref is not WasmFunction { Name: "rt.on_destroy" } g) continue;
            if (i < 1 || i + 1 >= b.Count || b[i - 1].Op != Op.LocalGet || b[i + 1].Op != Op.Drop) continue;
            // (already behind its test)
            if (i >= 6 && b[i - 2].Op == Op.If && b[i - 3].Op == Op.I32And && b[i - 4].Op == Op.I32Const && b[i - 4].Imm == 0x40000000) continue;
            var gb = g.Body;
            if (!(gb.Count > 4 && gb[0].Op == Op.LocalGet && gb[0].Imm == 0 && gb[1].Op == Op.I32Load && gb[1].Imm == 8 && gb[2].Op == Op.I32Const && gb[2].Imm == 0x40000000 && gb[3].Op == Op.I32And)) continue;
            (at ??= []).Add(i);
        }
        if (at == null) return false;
        var o = new List<Instr>(b.Count + 6 * at.Count);
        int next = 0;
        for (int i = 0; i < b.Count; i++)
        {
            if (next < at.Count && at[next] - 1 == i)
            {
                var get = b[i];
                Instr A(Op op, long imm = 0, long imm2 = 0) => new Instr { Op = op, Imm = imm, Imm2 = imm2, Pos = get.Pos, Inl = get.Inl };
                o.Add(get); o.Add(A(Op.I32Load, 8, 2)); o.Add(A(Op.I32Const, 0x40000000)); o.Add(A(Op.I32And)); o.Add(A(Op.If));
                o.Add(get); o.Add(b[i + 1]); o.Add(b[i + 2]);
                o.Add(A(Op.End));
                i += 2; next++;
                continue;
            }
            o.Add(b[i]);
        }
        f.Body = o;
        return true;
    }

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        if (s_off || f.IsImport || f.ForwardTo != null) return false;
        // (a container's end lets each of its values go: the last reference of most)
        if (f.Name.StartsWith("destroy ", StringComparison.Ordinal) || f.Name.StartsWith("rt.destroy", StringComparison.Ordinal) || f.Name == "rt.on_destroy") return false;
        bool changed = MarkTests(f);
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
                    if (op is Op.Loop or Op.TryTable) for (int k = 0; k < open.Count; k++) open[k] = (open[k].at, false);
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
        for (int n = loops.Count - 1; n >= 0; n--)
        {
            var (ls, le) = loops[n];
            // first with the frees' calls written out; as it was when the loop is not one to change even then
            var body = f.Body;
            int locals = f.Locals.Count;
            if (WriteFreesOut(f, ls, ref le))
            {
                if (Rewrite(f, ls, le)) { changed = true; continue; }
                f.Body = body;
                f.Locals.RemoveRange(locals, f.Locals.Count - locals);
                le = loops[n].end;
            }
            if (Rewrite(f, ls, le)) changed = true;
        }
        return changed;
    }

    static bool Usable(WasmFunction? g) => g is { IsImport: false, ForwardTo: null, Body.Count: > 0 };

    /// <summary>In the loop [ls, le]: Rt.FreeSmallOld's calls as Rt.FreeBack's body, Rt.FreeObject's as Rt.FreeSmall's
    /// (each does what the call did). le: the loop's end in the new body.</summary>
    static bool WriteFreesOut(WasmFunction f, int ls, ref int le)
    {
        if (s_noInlineFree || Module is not { } m || !Usable(m.FreeBackFn) || !Usable(m.FreeSmallFn) || f == m.FreeBackFn || f == m.FreeSmallFn) return false;
        var b = f.Body;
        bool any = false;
        for (int i = ls + 1; i < le && !any; i++)
            if (b[i].Op == Op.Call && b[i].Ref is WasmFunction g && (ReferenceEquals(g, m.FreeFn) || g.Name.StartsWith("Cs2Wasm.Runtime.Rt.FreeSmallOld(", StringComparison.Ordinal))) any = true;
        if (!any) return false;
        var o = new List<Instr>(b.Count + 256);
        int end = le;
        for (int i = 0; i < b.Count; i++)
        {
            var ins = b[i];
            if (i > ls && i < end && ins.Op == Op.Call && ins.Ref is WasmFunction g)
            {
                var with = ReferenceEquals(g, m.FreeFn) ? (end - ls <= s_smallLoop ? m.FreeSmallFn : null) : g.Name.StartsWith("Cs2Wasm.Runtime.Rt.FreeSmallOld(", StringComparison.Ordinal) ? m.FreeBackFn : null;
                if (with != null) { Inliner.Expand(f, with, o, ins.Pos, ins.Inl); continue; }
            }
            o.Add(ins);
            if (i == end) le = o.Count - 1;
        }
        f.Body = o;
        return true;
    }

    static bool Why(WasmFunction f, int ls, int le, int why, int at = -1)
    {
        if (s_stats)
        {
            bool has = false;
            for (int i = ls; i < le; i++) if (IsRare(f.Body[i])) { has = true; break; }
            if (has) Console.Error.WriteLine($"cold exits: not ({why}) a loop of {le - ls} in {f.Name}");
            if (has && at >= 0 && Environment.GetEnvironmentVariable("CS2WASM_COLD_EXITS_DUMP") is { } dn && f.Name.Contains(dn)) for (int q = Math.Max(ls, at - 24); q <= at; q++) Console.Error.WriteLine($"  {q} {f.Body[q]}");
        }
        return false;
    }

    static bool Rewrite(WasmFunction f, int ls, int le)
    {
        var b = f.Body;
        if (b[ls].Ref != null || le - ls > s_maxBody) return Why(f, ls, le, 1);
        // each block's else and end
        var elseOf = new Dictionary<int, int>();
        var endOf = new Dictionary<int, int>();
        var startOf = new Dictionary<int, int>();
        {
            var open = new List<int>();
            for (int i = ls + 1; i < le; i++)
            {
                var op = b[i].Op;
                if (op is Op.Loop or Op.TryTable) return Why(f, ls, le, 3);
                if (op is Op.Block or Op.If) open.Add(i);
                else if (op == Op.Else && open.Count > 0) elseOf[open[^1]] = i;
                else if (op == Op.End)
                {
                    if (open.Count == 0) return Why(f, ls, le, 12);
                    endOf[open[^1]] = i; startOf[i] = open[^1];
                    open.RemoveAt(open.Count - 1);
                }
            }
            if (open.Count != 0) return Why(f, ls, le, 12);
        }
        var frames = new List<Frame> { new() { Start = ls } };
        var points = new List<Point>();
        static bool Pop(Frame fr, int n)
        {
            if (fr.Dead) return true;
            if (fr.Made.Count < n) return false;
            fr.Made.RemoveRange(fr.Made.Count - n, n);
            return true;
        }
        var skip = new bool[le - ls + 1];   // a path's own code: not looked into
        for (int i = ls + 1; i < le; i++)
        {
            if (skip[i - ls]) continue;
            var ins = b[i];
            var top = frames[^1];
            switch (ins.Op)
            {
                case Op.Block:
                case Op.If:
                    {
                        if (ins.Ref is FuncType) return Why(f, ls, le, 2);
                        if (ins.Op == Op.If && !Pop(top, 1)) return Why(f, ls, le, 10);
                        if (ins.Op == Op.If && !top.Dead && ins.Ref == null)
                        {
                            int end = endOf[i];
                            bool hasElse = elseOf.TryGetValue(i, out int el);
                            int from = -1, to = -1;
                            if (RareArm(b, i + 1, hasElse ? el : end)) { from = i + 1; to = hasElse ? el : end; }
                            else if (hasElse && RareArm(b, el + 1, end)) { from = el + 1; to = end; }
                            if (from >= 0)
                            {
                                // every value on the stack here, in the body and in each block the `if` is in: of a known type
                                bool known = true;
                                var pt = new Point { If = i, End = end, From = from, To = to, Chain = new int[frames.Count - 1], ChainElse = new bool[frames.Count - 1], Stack = new int[frames.Count][] };
                                for (int k = 0; k < frames.Count && known; k++)
                                {
                                    if (k > 0) { pt.Chain[k - 1] = frames[k].Start; pt.ChainElse[k - 1] = frames[k].InElse; }
                                    pt.Stack[k] = frames[k].Made.ToArray();
                                    foreach (int at in pt.Stack[k]) if (Made(f, b, at, startOf) == null) known = false;
                                }
                                if (known) { points.Add(pt); for (int q = from; q < to; q++) skip[q - ls] = true; }
                            }
                        }
                        frames.Add(new Frame { Start = i, Dead = top.Dead });
                        break;
                    }
                case Op.Else:
                    top.InElse = true; top.Made.Clear(); top.Dead = frames[^2].Dead;
                    break;
                case Op.End:
                    {
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
                        if (ins.Op is Op.Call or Op.CallIndirect && !(i + 1 < le && b[i + 1].Op == Op.Unreachable)) return Why(f, ls, le, 4, i);
                        if (ins.Op is Op.MemoryCopy or Op.MemoryFill or Op.MemoryGrow or Op.MemoryInit) return Why(f, ls, le, 5);
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
        // (in the order of their code: an `if` whose path is its else comes after the paths inside its other arm)
        points.Sort((x, y) => x.From.CompareTo(y.From));

        int np = f.Type.Params.Length;
        int P = np + f.Locals.Count, C = P + 1, I0 = P + 2;
        f.Locals.Add(ValType.I32); f.Locals.Add(ValType.I32); f.Locals.Add(ValType.I32);
        // the loop's counter, when it has one: a local that its last instructions step by a constant (`i++; br L`)
        // and nothing else in it assigns. By it a path knows how many turns ago the last one was taken.
        int iv = -1; long step = 0;
        if (le - ls > 6 && s_near > 0 && b[le - 1].Op == Op.Br && b[le - 1].Imm == 0 && b[le - 2].Op == Op.LocalSet && b[le - 3].Op == Op.I32Add
            && b[le - 4].Op == Op.I32Const && b[le - 4].Ref == null && b[le - 4].Imm != 0 && b[le - 4].Imm is > -4096 and < 4096 && b[le - 5].Op == Op.LocalGet && b[le - 5].Imm == b[le - 2].Imm)
        {
            iv = (int)b[le - 2].Imm; step = b[le - 4].Imm;
            for (int i = ls + 1; i < le - 2; i++) if (b[i].Op is Op.LocalSet or Op.LocalTee && b[i].Imm == iv) { iv = -1; break; }
        }
        var starts = new HashSet<int>();
        foreach (var pt in points) starts.Add(pt.From);
        // the stack's values at each `if`: a local each, set where the value is made
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
        // and `shiftOut` more. slow: the loop as it was, for the turns after such a path was taken (no value kept;
        // a turn that takes one there makes it stay)
        void Copy(int from, int to, int depth, int shiftL, int shiftOut, bool slow = false)
        {
            for (int i = from; i < to; i++)
            {
                var ins = b[i];
                if (slow && starts.Contains(i))
                {
                    if (iv >= 0) { o.Add(new Instr { Op = Op.LocalGet, Imm = iv, Pos = ins.Pos, Inl = ins.Inl }); o.Add(new Instr { Op = Op.LocalSet, Imm = I0, Pos = ins.Pos, Inl = ins.Inl }); }
                    else { o.Add(new Instr { Op = Op.I32Const, Imm = s_stay, Pos = ins.Pos, Inl = ins.Inl }); o.Add(new Instr { Op = Op.LocalSet, Imm = C, Pos = ins.Pos, Inl = ins.Inl }); }
                }
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
                if (slow) o.Add(ins); else One(i, ins);
            }
        }

        A(Op.I32Const, 0); A(Op.LocalSet, P);
        // (the turn of the last such path: far back at the start)
        if (iv >= 0) { A(Op.LocalGet, iv); A(Op.I32Const, step > 0 ? 0x40000000 : -0x40000000); A(Op.I32Sub); A(Op.LocalSet, I0); }
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
                // up to the path's code (its `if`, and the other arm when that comes first), then in its place: out
                Copy(at, pt.From, depth, 0, 3);
                pos = b[pt.If].Pos; inl = b[pt.If].Inl;
                // (inside the `if`: the blocks it is in, then L, H, O)
                A(Op.I32Const, j + 1); A(Op.LocalSet, P);
                A(Op.Br, pt.Chain.Length + 3);
                at = pt.To; depth = pt.Chain.Length + 1;
            }
            Copy(at, le, depth, 0, 3);
        }
        o.Add(b[le]);         // L's end
        pos = b[le].Pos; inl = b[le].Inl;
        A(Op.Br, 2);          // the loop ran out: past the other paths
        A(Op.End);            // H
        A(Op.Block);          // S: the paths; a turn that ends in one goes on in the loop as it was, below
        for (int j = 0; j < points.Count; j++)
        {
            var pt = points[j];
            bool last = j == points.Count - 1;
            int d = last ? 0 : 1;
            pos = b[pt.If].Pos; inl = b[pt.If].Inl;
            if (!last) { A(Op.LocalGet, P); A(Op.I32Const, j + 1); A(Op.I32Eq); A(Op.If); }
            A(Op.I32Const, 0); A(Op.LocalSet, P);
            // whether to go on in the loop as it was after this turn: by the counter, when the last such path was
            // near (else the loop without calls is entered again at once); without a counter, for a few turns
            if (iv >= 0)
            {
                if (step > 0) { A(Op.LocalGet, iv); A(Op.LocalGet, I0); } else { A(Op.LocalGet, I0); A(Op.LocalGet, iv); }
                A(Op.I32Sub); A(Op.I32Const, s_near * Math.Abs(step)); A(Op.I32LtU); A(Op.LocalSet, C);
                A(Op.LocalGet, iv); A(Op.LocalSet, I0);
            }
            else { A(Op.I32Const, s_turns); A(Op.LocalSet, C); }
            int n = pt.Chain.Length;
            for (int k = 0; k <= n; k++)
            {
                if (k > 0) o.Add(new Instr { Op = Op.Block, Ref = b[pt.Chain[k - 1]].Ref, Pos = pos, Inl = inl });
                if (!noStack) foreach (int local in pt.Saved[k]) A(Op.LocalGet, local);
            }
            A(Op.Block);
            Copy(pt.From, pt.To, n + 1, d, d + 2);
            A(Op.End);
            // what is left of each block the `if` was in, innermost first
            int child = pt.End;
            for (int k = n - 1; k >= 0; k--)
            {
                int start = pt.Chain[k];
                int stop = !pt.ChainElse[k] && elseOf.TryGetValue(start, out int el) ? el : endOf[start];
                if (!noRest) Copy(child + 1, stop, k + 1, d, d + 2);
                One(endOf[start], b[endOf[start]]);
                child = endOf[start];
            }
            Copy(child + 1, le, 0, d, d + 2);
            pos = b[le].Pos; inl = b[le].Inl;
            A(Op.Br, d + 2);   // the loop ran out
            if (!last) A(Op.End);
        }
        A(Op.End);            // S
        // the loop as it was, for the next turns: where such a path is the usual one after all, leaving the loop
        // for each would cost more than the calls in it did
        pos = b[ls].Pos; inl = b[ls].Inl;
        A(Op.LocalGet, C); A(Op.I32Eqz); A(Op.BrIf, 0);   // no turn to run there: O
        o.Add(b[ls]);         // L2
        if (iv >= 0)
        {
            // (with a counter: until the last such path is `stay` turns back; nothing is written on a turn that takes none)
            if (step > 0) { A(Op.LocalGet, iv); A(Op.LocalGet, I0); } else { A(Op.LocalGet, I0); A(Op.LocalGet, iv); }
            A(Op.I32Sub); A(Op.I32Const, s_stay * Math.Abs(step)); A(Op.I32GeU); A(Op.BrIf, 1);
        }
        else
        {
            A(Op.LocalGet, C); A(Op.I32Eqz); A(Op.BrIf, 1);
            A(Op.LocalGet, C); A(Op.I32Const, 1); A(Op.I32Sub); A(Op.LocalSet, C);
        }
        Copy(ls + 1, le, 0, 0, 2, slow: true);
        o.Add(b[le]);         // L2's end
        A(Op.Br, 1);          // the loop ran out
        A(Op.End);            // O
        A(Op.End);            // X
        for (int i = le + 1; i < b.Count; i++) o.Add(b[i]);
        f.Body = o;
        System.Threading.Interlocked.Increment(ref Loops); System.Threading.Interlocked.Add(ref Points, points.Count);
        if (s_stats) Console.Error.WriteLine($"cold exits: {points.Count} in a loop of {le - ls} in {f.Name}");
        return true;
    }
}
