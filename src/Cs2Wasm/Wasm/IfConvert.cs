namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: a conditional step without a branch. `if (c) x += k;` (x a local, k a constant, c a comparison) is
/// emitted as `c; if; local.get x; const k; add; local.set x; end`: a branch the processor must guess, wrong about as
/// often as not when c depends on the data (counting the items of a list that have some property). A comparison's
/// result is 0 or 1, so the same value is `x + c * k`, computed without a branch: `c; (extend;) (const k; mul;)
/// local.get x; add; local.set x`. Nothing else changes: the arithmetic wraps as the addition did, and none of it
/// can fail.
/// </summary>
static class IfConvert
{
    static bool Compare(Op op) => op is Op.I32Eqz or Op.I32Eq or Op.I32Ne or Op.I32LtS or Op.I32LtU or Op.I32GtS or Op.I32GtU or Op.I32LeS or Op.I32LeU or Op.I32GeS or Op.I32GeU
        or Op.I64Eqz or Op.I64Eq or Op.I64Ne or Op.I64LtS or Op.I64LtU or Op.I64GtS or Op.I64GtU or Op.I64LeS or Op.I64LeU or Op.I64GeS or Op.I64GeU;

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        var b = f.Body;
        List<Instr>? o = null;
        int copied = 0;
        for (int i = 1; i + 5 < b.Count; i++)
        {
            if (b[i].Op != Op.If || b[i].Ref != null || !Compare(b[i - 1].Op)) continue;
            if (b[i + 1].Op != Op.LocalGet || b[i + 4].Op != Op.LocalSet || b[i + 4].Imm != b[i + 1].Imm || b[i + 5].Op != Op.End) continue;
            bool wide;
            if (b[i + 2].Op == Op.I64Const && b[i + 3].Op is Op.I64Add or Op.I64Sub) wide = true;
            else if (b[i + 2].Op == Op.I32Const && b[i + 2].Ref == null && b[i + 3].Op is Op.I32Add or Op.I32Sub) wide = false;
            else continue;
            long k = b[i + 3].Op is Op.I64Sub or Op.I32Sub ? -b[i + 2].Imm : b[i + 2].Imm;
            if (!wide) k = (int)k;
            o ??= new List<Instr>(b.Count);
            for (int j = copied; j < i; j++) o.Add(b[j]);
            int pos = b[i].Pos;
            if (wide) o.Add(new Instr { Op = Op.I64ExtendI32U, Pos = pos });
            if (k != 1)
            {
                o.Add(new Instr { Op = wide ? Op.I64Const : Op.I32Const, Imm = k, Pos = pos });
                o.Add(new Instr { Op = wide ? Op.I64Mul : Op.I32Mul, Pos = pos });
            }
            o.Add(b[i + 1]);
            o.Add(new Instr { Op = wide ? Op.I64Add : Op.I32Add, Pos = pos });
            o.Add(b[i + 4]);
            copied = i + 6;
            i += 5;
        }
        if (o == null) return false;
        for (int j = copied; j < b.Count; j++) o.Add(b[j]);
        f.Body = o;
        return true;
    }
}
