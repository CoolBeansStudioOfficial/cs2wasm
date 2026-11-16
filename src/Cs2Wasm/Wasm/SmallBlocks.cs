namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: a struct's copy and a frame's clearing are memory.copy and memory.fill of a few bytes, a size known when
/// compiling. Engines run those instructions as calls into their runtime, far slower than the loads and stores they
/// stand for, so a small one of constant size becomes the loads and stores: a copy reads every word before it writes
/// any (the same result as memory.copy when the two overlap).
/// </summary>
static class SmallBlocks
{
    const int MaxCopy = 64, MaxFill = 128;

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        var b = f.Body;
        bool any = false;
        for (int i = 1; i < b.Count && !any; i++) any = b[i].Op is Op.MemoryCopy or Op.MemoryFill && b[i - 1].Op == Op.I32Const;
        if (!any) return false;
        int np = f.Type.Params.Length;
        int Local(ValType t) { f.Locals.Add(t); return np + f.Locals.Count - 1; }
        int a = -1, s = -1, tail = -1;
        var words = new List<int>();
        var o = new List<Instr>(b.Count + 32);
        for (int i = 0; i < b.Count; i++)
        {
            var ins = b[i];
            void Add(Op op, long imm = 0, long imm2 = 0) => o.Add(new Instr { Op = op, Imm = imm, Imm2 = imm2, Pos = ins.Pos });
            bool sized = o.Count > 0 && o[^1].Op == Op.I32Const && o[^1].Ref == null;
            long n = sized ? o[^1].Imm : 0;
            if (ins.Op == Op.MemoryCopy && sized && n > 0 && n <= MaxCopy && n % 4 == 0)
            {
                o.RemoveAt(o.Count - 1);
                if (a < 0) a = Local(ValType.I32);
                if (s < 0) s = Local(ValType.I32);
                Add(Op.LocalSet, s); Add(Op.LocalSet, a);
                int nw = (int)(n / 8);
                while (words.Count < nw) words.Add(Local(ValType.I64));
                for (int k = 0; k < nw; k++) { Add(Op.LocalGet, s); Add(Op.I64Load, 8 * k); Add(Op.LocalSet, words[k]); }
                if (n % 8 != 0)
                {
                    if (tail < 0) tail = Local(ValType.I32);
                    Add(Op.LocalGet, s); Add(Op.I32Load, 8 * nw); Add(Op.LocalSet, tail);
                }
                for (int k = 0; k < nw; k++) { Add(Op.LocalGet, a); Add(Op.LocalGet, words[k]); Add(Op.I64Store, 8 * k); }
                if (n % 8 != 0) { Add(Op.LocalGet, a); Add(Op.LocalGet, tail); Add(Op.I32Store, 8 * nw); }
                continue;
            }
            if (ins.Op == Op.MemoryFill && sized && n > 0 && n <= MaxFill && n % 4 == 0
                && o.Count > 1 && o[^2].Op == Op.I32Const && o[^2].Ref == null && o[^2].Imm == 0)
            {
                o.RemoveAt(o.Count - 1); o.RemoveAt(o.Count - 1);
                if (a < 0) a = Local(ValType.I32);
                Add(Op.LocalSet, a);
                int nw = (int)(n / 8);
                for (int k = 0; k < nw; k++) { Add(Op.LocalGet, a); Add(Op.I64Const, 0); Add(Op.I64Store, 8 * k); }
                if (n % 8 != 0) { Add(Op.LocalGet, a); Add(Op.I32Const, 0); Add(Op.I32Store, 8 * nw); }
                continue;
            }
            o.Add(ins);
        }
        bool changed = o.Count != b.Count;
        f.Body = o;
        return changed;
    }
}
