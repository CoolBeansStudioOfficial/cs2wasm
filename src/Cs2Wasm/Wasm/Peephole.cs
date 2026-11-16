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
}
