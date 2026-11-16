namespace Cs2Wasm.Wasm;

/// <summary>Final cleanup over emitted instructions (section 5): redundant local traffic, dead pushes.</summary>
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
                if (ins.Op == Op.Call && prev.Op == Op.I32Const && (prev.Ref is IAddress { Immortal: true } || prev.Ref == null && prev.Imm == 0) && ins.Ref is WasmFunction { Name: "rt.retain" or "rt.release" } rf)
                {
                    if (rf.Name == "rt.release") dst.RemoveAt(dst.Count - 1);
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
                // i32.const 0 ; i32.ne ; i32.eqz  =>  i32.eqz   (`while (x != null)`: leave when x is null)
                if (ins.Op == Op.I32Eqz && prev.Op == Op.I32Ne && dst.Count > 1 && dst[^2].Op == Op.I32Const && dst[^2].Imm == 0 && dst[^2].Ref == null)
                {
                    dst.RemoveAt(dst.Count - 1);
                    dst[^1] = ins;
                    continue;
                }
                // i32.eqz ; i32.eqz ; br_if  =>  br_if   (only when followed by a branch/if: value used as condition)
            }
            dst.Add(ins);
        }
        DeadValues(dst);
        // Remove instructions after an unconditional transfer until the end of the enclosing block.
        var outList = new List<Instr>(dst.Count);
        for (int i = 0; i < dst.Count; i++)
        {
            var ins = dst[i];
            outList.Add(ins);
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
        f.Body = outList;
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
