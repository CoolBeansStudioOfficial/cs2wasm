namespace Il2Wasm.Wasm;

/// <summary>Final cleanup over emitted instructions (section 5): redundant local traffic, dead pushes.</summary>
/// <summary>The counts' functions by what they do: a retain or a release is rt.retain / rt.release, the plain ones of
/// a program with threads, or (until the counts are resolved, TypedCounts.cs) a placeholder for a static type's.</summary>
static class Rc
{
    public static bool IsRetain(WasmFunction g) => g.Name is "rt.retain" or "rt.retain_plain" || g.Name.StartsWith("count Retain ", StringComparison.Ordinal);
    public static bool IsRelease(WasmFunction g) => g.Name is "rt.release" or "rt.release_plain" || g.Name.StartsWith("count Release ", StringComparison.Ordinal);
    public static bool IsRetain(in Instr i) => i.Op == Op.Call && i.Ref is WasmFunction g && IsRetain(g);
    public static bool IsRelease(in Instr i) => i.Op == Op.Call && i.Ref is WasmFunction g && IsRelease(g);
}

static class Peephole
{
    public static void Run(WasmFunction f)
    {
        var src = f.Body;
        var dst = new List<Instr>(src.Count);
        foreach (var ins in src)
        {
            if (dst.Count > 0)
            {
                var prev = dst[^1];
                // local.set x ; local.get x  =>  local.tee x
                if (ins.Op == Op.LocalGet && prev.Op == Op.LocalSet && prev.Imm == ins.Imm)
                {
                    prev.Op = Op.LocalTee; dst[^1] = prev; continue;
                }
                // an immortal object (a string literal): retain gives it back, release does nothing
                // (and null: a moved local's release, its local cleared, is a release of 0)
                if (ins.Op == Op.Call && prev.Op == Op.I32Const && (prev.Ref is IAddress { Immortal: true } || prev.Ref == null && prev.Imm == 0) && ins.Ref is WasmFunction rf && (Rc.IsRetain(rf) || Rc.IsRelease(rf)))
                {
                    if (Rc.IsRelease(rf)) dst.RemoveAt(dst.Count - 1);
                    continue;
                }
                // pure push ; drop  =>  nothing
                if (ins.Op == Op.Drop && prev.Op is Op.LocalGet or Op.I32Const or Op.I64Const or Op.F32Const or Op.F64Const or Op.GlobalGet)
                {
                    dst.RemoveAt(dst.Count - 1); continue;
                }
                // push ; push ; i32.add ; drop  =>  nothing   (an address nobody uses: FrameSlots.cs)
                if (ins.Op == Op.Drop && prev.Op == Op.I32Add && dst.Count >= 3 && dst[^2].Op is Op.LocalGet or Op.I32Const && dst[^3].Op is Op.LocalGet or Op.I32Const)
                {
                    dst.RemoveRange(dst.Count - 3, 3); continue;
                }
                // local.tee x ; drop  =>  local.set x
                if (ins.Op == Op.Drop && prev.Op == Op.LocalTee)
                {
                    prev.Op = Op.LocalSet; dst[^1] = prev; continue;
                }
                // const ; const ; op  =>  const   (sizeof(T) * 2 once T is known)
                if (prev.Op == Op.I32Const && prev.Ref == null && dst.Count > 1 && dst[^2].Op == Op.I32Const && dst[^2].Ref == null
                    && ins.Op is Op.I32Add or Op.I32Sub or Op.I32Mul or Op.I32Shl or Op.I32And or Op.I32Or or Op.I32Xor)
                {
                    int x = (int)dst[^2].Imm, y = (int)prev.Imm;
                    int r = ins.Op switch { Op.I32Add => x + y, Op.I32Sub => x - y, Op.I32Mul => x * y, Op.I32Shl => x << y, Op.I32And => x & y, Op.I32Or => x | y, _ => x ^ y };
                    dst.RemoveAt(dst.Count - 1);
                    dst[^1] = dst[^1] with { Imm = r };
                    continue;
                }
                // const ; const ; compare  =>  const   (a switch on a constant local: ConstBranches.ConstLocals)
                if (prev.Op == Op.I32Const && prev.Ref == null && dst.Count > 1 && dst[^2].Op == Op.I32Const && dst[^2].Ref == null
                    && ins.Op >= Op.I32Eq && ins.Op <= Op.I32GeU)
                {
                    int x = (int)dst[^2].Imm, y = (int)prev.Imm;
                    bool r = ins.Op switch
                    {
                        Op.I32Eq => x == y, Op.I32Ne => x != y, Op.I32LtS => x < y, Op.I32LtU => (uint)x < (uint)y, Op.I32GtS => x > y,
                        Op.I32GtU => (uint)x > (uint)y, Op.I32LeS => x <= y, Op.I32LeU => (uint)x <= (uint)y, Op.I32GeS => x >= y, _ => (uint)x >= (uint)y,
                    };
                    dst.RemoveAt(dst.Count - 1);
                    dst[^1] = dst[^1] with { Imm = r ? 1 : 0 };
                    continue;
                }
                // a division by a constant that is neither 0 nor -1 cannot fail: the instruction, not the checked helper
                if (ins.Op == Op.Call && ins.Ref is WasmFunction { Name: var dn } && dn.StartsWith("rt.", StringComparison.Ordinal) && dn.Length == 10
                    && (dn.StartsWith("rt.div_", StringComparison.Ordinal) || dn.StartsWith("rt.rem_", StringComparison.Ordinal))
                    && prev.Ref == null && prev.Imm != 0 && (prev.Op == Op.I32Const && dn.EndsWith("32", StringComparison.Ordinal) && (int)prev.Imm != -1
                        || prev.Op == Op.I64Const && dn.EndsWith("64", StringComparison.Ordinal) && prev.Imm != -1))
                {
                    bool div = dn[3] == 'd', sg = dn[7] == 's', l = prev.Op == Op.I64Const;
                    dst.Add(ins with
                    {
                        Ref = null,
                        Op = (div, l, sg) switch
                        {
                            (true, false, true) => Op.I32DivS, (true, false, false) => Op.I32DivU, (true, true, true) => Op.I64DivS, (true, true, false) => Op.I64DivU,
                            (false, false, true) => Op.I32RemS, (false, false, false) => Op.I32RemU, (false, true, true) => Op.I64RemS, _ => Op.I64RemU,
                        },
                    });
                    continue;
                }
                // i32.const 0 ; i32.ne ; i32.eqz  =>  i32.eqz   (`while (x != null)`: leave when x is null)
                if (ins.Op == Op.I32Eqz && prev.Op == Op.I32Ne && dst.Count > 1 && dst[^2].Op == Op.I32Const && dst[^2].Imm == 0 && dst[^2].Ref == null)
                {
                    dst.RemoveAt(dst.Count - 1);
                    dst[^1] = ins;
                    continue;
                }
                // i32.const k ; i64.extend_i32_u (or _s)  =>  i64.const k   (a shift count of a long)
                if (prev.Op == Op.I32Const && prev.Ref == null && ins.Op is Op.I64ExtendI32U or Op.I64ExtendI32S)
                {
                    dst[^1] = prev with { Op = Op.I64Const, Imm = ins.Op == Op.I64ExtendI32U ? (long)(uint)(int)prev.Imm : (long)(int)prev.Imm };
                    continue;
                }
                // i32.eqz ; i32.eqz ; br_if  =>  br_if   (only when followed by a branch/if: value used as condition)
            }
            dst.Add(ins);
        }
        DeadValues(dst);
        BranchesToOwnEnd(dst);
        TeeChains(f, dst);
        // Remove instructions after an unconditional transfer until the end of the enclosing block.
        // (in place: what is kept moves down over what is not, read ahead of where it is written)
        int w = 0;
        for (int i = 0; i < dst.Count; i++)
        {
            var ins = dst[i];
            dst[w++] = ins;
            if (ins.Op is Op.Br or Op.Return or Op.Unreachable or Op.Throw or Op.ThrowRef or Op.BrTable)
            {
                int depth = 0; int j = i + 1;
                for (; j < dst.Count; j++)
                {
                    var o = dst[j].Op;
                    if (o is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
                    else if (o == Op.End) { if (depth == 0) break; depth--; }
                    else if (o == Op.Else && depth == 0) break;
                }
                i = j - 1;
            }
        }
        dst.RemoveRange(w, dst.Count - w);
        f.Body = dst;
    }

    /// <summary>The last tidying, on the final bodies (WasmModule, before frames are taken): `i32.const 0 ; i32.ne`
    /// before an `if` or a `br_if`, which test for non-zero themselves; `local.set x ; local.get x` that the passes
    /// after Run left; `i32.const k ; i32.add` before a load (its offset); a `return` that is the body's last instruction, where the function's end returns the same
    /// values. Each is fewer bytes and the same meaning.</summary>
    static readonly bool s_tidyOffsets = Environment.GetEnvironmentVariable("IL2WASM_TIDY_OFFSETS") != "0";

    public static void FinalTidy(WasmFunction f)
    {
        var b = f.Body;
        var o = new List<Instr>(b.Count);
        foreach (var ins in b)
        {
            if (ins.Op is Op.If or Op.BrIf && o.Count >= 2 && o[^1].Op == Op.I32Ne && o[^2].Op == Op.I32Const && o[^2].Imm == 0 && o[^2].Ref == null)
            {
                o.RemoveRange(o.Count - 2, 2);
                o.Add(ins);
                continue;
            }
            // (`i32.eqz; i32.eqz` before a branch on it: the value's own truth)
            if (ins.Op is Op.If or Op.BrIf && o.Count >= 2 && o[^1].Op == Op.I32Eqz && o[^2].Op == Op.I32Eqz)
            {
                o.RemoveRange(o.Count - 2, 2);
                o.Add(ins);
                continue;
            }
            // (a pure push dropped at once)
            if (ins.Op == Op.Drop && o.Count > 0 && o[^1].Op is Op.LocalGet or Op.GlobalGet or Op.I32Const or Op.I64Const or Op.F32Const or Op.F64Const)
            {
                o.RemoveAt(o.Count - 1);
                continue;
            }
            if (ins.Op == Op.LocalGet && o.Count > 0 && o[^1].Op == Op.LocalSet && o[^1].Imm == ins.Imm) { o[^1] = o[^1] with { Op = Op.LocalTee }; continue; }
            // an address plus a constant, loaded from: the constant is the load's offset (a field of an object; an
            // address that wrapped round the 4 GiB end would be no object's)
            if (s_tidyOffsets && ins.Op is >= Op.I32Load and <= Op.I64Load32U && o.Count >= 2 && o[^1].Op == Op.I32Add && o[^2].Op == Op.I32Const && o[^2].Ref == null
                && o[^2].Imm >= 0 && o[^2].Imm + ins.Imm < int.MaxValue)
            {
                long k = o[^2].Imm;
                o.RemoveRange(o.Count - 2, 2);
                o.Add(ins with { Imm = ins.Imm + k });
                continue;
            }
            // the same for a store whose value is one push (a local, a constant, a global)
            if (s_tidyOffsets && ins.Op is >= Op.I32Store and <= Op.I64Store32 && o.Count >= 3 && o[^1].Op is Op.LocalGet or Op.GlobalGet or Op.I32Const or Op.I64Const or Op.F32Const or Op.F64Const
                && o[^2].Op == Op.I32Add && o[^3].Op == Op.I32Const && o[^3].Ref == null && o[^3].Imm >= 0 && o[^3].Imm + ins.Imm < int.MaxValue)
            {
                long k = o[^3].Imm;
                var v = o[^1];
                o.RemoveRange(o.Count - 3, 3);
                o.Add(v);
                o.Add(ins with { Imm = ins.Imm + k });
                continue;
            }
            o.Add(ins);
        }
        if (o.Count > 0 && o[^1].Op == Op.Return) o.RemoveAt(o.Count - 1);
        // (not `call thrower; unreachable` at a block's end without its unreachable: fewer bytes, but in the engine's
        // baseline tier the code after the block then merges with the throwing arm's state: micro 6% slower)
        f.Body = o;
    }

    /// <summary>`br 0` as the last instruction of a block or an if (an inlined body's return): the branch goes to where
    /// the code falls through anyway. (Not a loop's: there `br 0` goes back to its start.)</summary>
    static void BranchesToOwnEnd(List<Instr> b)
    {
        var kinds = new Stack<Op>();
        bool any = false;
        var remove = new bool[b.Count];
        for (int i = 0; i < b.Count; i++)
        {
            var op = b[i].Op;
            if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) kinds.Push(op);
            else if (op == Op.End && kinds.Count > 0)
            {
                var k = kinds.Pop();
                if (k is Op.Block or Op.If && i > 0 && b[i - 1].Op == Op.Br && b[i - 1].Imm == 0 && !remove[i - 1]) { remove[i - 1] = true; any = true; }
            }
        }
        if (!any) return;
        int w = 0;
        for (int i = 0; i < b.Count; i++) if (!remove[i]) b[w++] = b[i];
        b.RemoveRange(w, b.Count - w);
    }

    /// <summary>`local.tee a ; local.tee b` (or `local.set b`) where a and b are each written there and nowhere else:
    /// from that point on they hold the same value on every path, and before it both hold zero, so b is a: its reads
    /// read a, and its write goes. (Chains of copies a lowering makes: a value kept for a check, for a borrow, for an
    /// inlined callee's parameter; in the engine's baseline tier each local is a slot in memory.)</summary>
    static void TeeChains(WasmFunction f, List<Instr> b)
    {
        int np = f.Type.Params.Length;
        int nl = np + f.Locals.Count;
        var writes = new int[nl];
        foreach (var ins in b) if (ins.Op is Op.LocalSet or Op.LocalTee && ins.Imm < nl) writes[ins.Imm]++;
        var alias = new int[nl];
        for (int i = 0; i < nl; i++) alias[i] = i;
        bool any = false;
        var remove = new bool[b.Count];
        for (int i = 1; i < b.Count; i++)
        {
            var a = b[i - 1]; var x = b[i];
            if (a.Op != Op.LocalTee || x.Op is not (Op.LocalTee or Op.LocalSet)) continue;
            int ai = (int)a.Imm, xi = (int)x.Imm;
            // (locals only, not parameters: a parameter holds its argument before any write)
            if (ai < np || xi < np || ai == xi || writes[ai] != 1 || writes[xi] != 1) continue;
            if (f.Locals[ai - np] != f.Locals[xi - np]) continue;
            alias[xi] = alias[ai];
            remove[i] = true;
            if (x.Op == Op.LocalSet)
            {
                // (the value ends here: the chain's first tee, the one kept, becomes the set)
                int k = i - 1;
                while (remove[k]) k--;
                b[k] = b[k] with { Op = Op.LocalSet };
            }
            any = true;
        }
        if (!any) return;
        int wpos = 0;
        for (int i = 0; i < b.Count; i++)
        {
            if (remove[i]) continue;
            var ins = b[i];
            if (ins.Op == Op.LocalGet && ins.Imm < nl && alias[ins.Imm] != ins.Imm)
            {
                int r = (int)ins.Imm;
                while (alias[r] != r) r = alias[r];
                ins = ins with { Imm = r };
            }
            b[wpos++] = ins;
        }
        b.RemoveRange(wpos, b.Count - wpos);
    }

    /// <summary>A value that is computed, with no other effect, only to be dropped further on (an address whose use
    /// became a local: FrameSlots.cs): the drop looks back for the instruction that made it, over instructions that
    /// leave it where it is, and both go; an operation with operands becomes the drop of them, looked at in turn.</summary>
    static void DeadValues(List<Instr> b)
    {
        bool any = false;
        for (int d = 0; d < b.Count; d++)
        {
            if (b[d].Op != Op.Drop) continue;
            // the instruction that pushed the value this drop takes: going back, `need` values pushed are still to be found
            int need = 1, j = d - 1;
            for (; j >= 0 && d - j <= 96; j--)
            {
                if (b[j].Op == Op.Nop) continue;
                if (Locals.PlainEffect(b[j]) is not var (pop, push)) { j = -1; break; }
                if (push >= need) break;
                need += pop - push;
            }
            if (j < 0 || d - j > 96) continue;
            var p = b[j];
            if (Locals.PlainEffect(p) is not (var pp, 1) || need != 1) continue;
            var nop = new Instr { Op = Op.Nop, Pos = -1 };
            if (p.Op == Op.LocalTee) { b[j] = p with { Op = Op.LocalSet }; b[d] = nop; any = true; }
            else if (pp == 0 && p.Op is Op.LocalGet or Op.GlobalGet or Op.I32Const or Op.I64Const or Op.F32Const or Op.F64Const) { b[j] = nop; b[d] = nop; any = true; }
            else if (pp == 1 && PureUnary(p.Op)) { b[j] = nop; any = true; d--; }
            else if (pp == 2 && PureBinary(p.Op)) { b[j] = p with { Op = Op.Drop, Imm = 0, Imm2 = 0, Ref = null }; any = true; d = j - 1; }
        }
        if (any) b.RemoveAll(x => x.Op == Op.Nop && x.Ref == null && x.Pos < 0);
    }

    static bool PureUnary(Op op)
    {
        int v = (int)op;
        return op is Op.I32Eqz or Op.I64Eqz or Op.I32WrapI64 or Op.I64ExtendI32S or Op.I64ExtendI32U
            || op >= Op.I32Clz && op <= Op.I32Popcnt || op >= Op.I64Clz && op <= Op.I64Popcnt
            || op >= Op.F32Abs && op <= Op.F32Sqrt || op >= Op.F64Abs && op <= Op.F64Sqrt
            || v >= 0xB2 && v <= 0xC4 || op >= Op.I32TruncSatF32S && op <= Op.I64TruncSatF64U;
    }

    static bool PureBinary(Op op) =>
        op >= Op.I32Eq && op <= Op.I32GeU || op >= Op.I64Eq && op <= Op.I64GeU || op >= Op.F32Eq && op <= Op.F64Ge
        || op is Op.I32Add or Op.I32Sub or Op.I32Mul or Op.I64Add or Op.I64Sub or Op.I64Mul
        || op >= Op.I32And && op <= Op.I32Rotr || op >= Op.I64And && op <= Op.I64Rotr
        || op >= Op.F32Add && op <= Op.F32Copysign || op >= Op.F64Add && op <= Op.F64Copysign;
}
