namespace Cs2Wasm.Wasm;

/// <summary>
/// Branches on a constant (section 5: simplifying branches): `i32.const c` before `if`, `br_if` or `i32.eqz`, as the
/// compiler leaves them where a condition becomes known only once the whole program is (Rt.SingleThreaded). The arm
/// that cannot run goes, and with it the calls only it made.
/// </summary>
static class ConstBranches
{
    public static void Run(WasmFunction f)
    {
        var b = f.Body;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i + 1 < b.Count; i++)
            {
                if (b[i].Op != Op.I32Const || b[i].Ref != null) continue;
                bool c = b[i].Imm != 0;
                var next = b[i + 1];
                switch (next.Op)
                {
                    case Op.I32Eqz:
                        b[i] = b[i] with { Imm = c ? 0 : 1 };
                        b.RemoveAt(i + 1);
                        changed = true;
                        break;
                    case Op.BrIf:
                        if (c) { b[i + 1] = next with { Op = Op.Br }; b.RemoveAt(i); }
                        else b.RemoveRange(i, 2);
                        changed = true;
                        break;
                    case Op.If:
                        {
                            int depth = 0, els = -1, end = -1;
                            for (int j = i + 2; j < b.Count; j++)
                            {
                                var o = b[j].Op;
                                if (o is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
                                else if (o == Op.Else && depth == 0) els = j;
                                else if (o == Op.End) { if (depth == 0) { end = j; break; } depth--; }
                            }
                            if (end < 0) break;
                            var block = next with { Op = Op.Block };
                            if (c)
                            {
                                // then-arm in a block (its branch depths stay as they were)
                                if (els >= 0) b.RemoveRange(els, end - els);
                                b[i + 1] = block;
                                b.RemoveAt(i);
                                Unwrap(b, i);
                            }
                            else if (els >= 0)
                            {
                                b.RemoveRange(i + 2, els - (i + 2) + 1);
                                b[i + 1] = block;
                                b.RemoveAt(i);
                                Unwrap(b, i);
                            }
                            else b.RemoveRange(i, end - i + 1);
                            changed = true;
                            break;
                        }
                }
                if (changed) break;
            }
        }
    }

    /// <summary>A local that is given a constant once, at the function's top level, before anything reads it, and never
    /// written again holds that constant wherever it is read: its reads become the constant (and the compares and
    /// branches on it fold, ConstBranches and Peephole). `switch (Unsafe.SizeOf&lt;T&gt;())` in a generic method is such a
    /// local once T is known (Roslyn's SegmentedArray: SegmentShift, OffsetMask). A block whose whole body is a
    /// constant is the constant. Returns whether the function changed.</summary>
    public static bool ConstLocals(WasmFunction f)
    {
        var b = f.Body;
        bool changed = false;
        for (int i = 0; i + 2 < b.Count; i++)
            if (b[i].Op == Op.Block && b[i].Ref is ValType.I32 && b[i + 1].Op == Op.I32Const && b[i + 1].Ref == null && b[i + 2].Op == Op.End)
            {
                b.RemoveAt(i + 2); b.RemoveAt(i);
                changed = true;
            }
        int np = f.Type.Params.Length, nl = np + f.Locals.Count;
        // (an inlined callee's locals are set to zero where it went in: a constant too, before the one it is given)
        var writes = new int[nl];
        var defs = new int[nl];
        var firstRead = new int[nl];
        Array.Fill(firstRead, int.MaxValue);
        var lastDef = new int[nl];
        Array.Fill(lastDef, -1);
        int depth = 0;
        for (int i = 0; i < b.Count; i++)
        {
            var ins = b[i];
            switch (ins.Op)
            {
                case Op.Block or Op.Loop or Op.If or Op.TryTable: depth++; break;
                case Op.End: depth--; break;
                case Op.LocalGet when ins.Imm < nl: if (firstRead[ins.Imm] == int.MaxValue) firstRead[ins.Imm] = i; break;
                case Op.LocalSet or Op.LocalTee when ins.Imm < nl:
                    writes[ins.Imm]++;
                    if (ins.Op == Op.LocalSet && depth == 0 && i > 0 && b[i - 1].Op == Op.I32Const && b[i - 1].Ref == null && firstRead[ins.Imm] == int.MaxValue)
                    { defs[ins.Imm]++; lastDef[ins.Imm] = i; }
                    break;
            }
        }
        var value = new long?[nl];
        bool any = false;
        for (int l = np; l < nl; l++)
            if (writes[l] > 0 && writes[l] == defs[l] && f.Locals[l - np] == ValType.I32) { value[l] = b[lastDef[l] - 1].Imm; any = true; }
        if (!any) return changed;
        var o = new List<Instr>(b.Count);
        for (int i = 0; i < b.Count; i++)
        {
            var ins = b[i];
            if (ins.Op == Op.LocalSet && ins.Imm < nl && value[ins.Imm] != null) { o.RemoveAt(o.Count - 1); continue; }
            if (ins.Op == Op.LocalGet && ins.Imm < nl && value[ins.Imm] is long v) { o.Add(ins with { Op = Op.I32Const, Imm = v, Ref = null }); continue; }
            o.Add(ins);
        }
        f.Body = o;
        return true;
    }

    /// <summary>The block at k without its block, when no branch inside targets it (outer targets come one closer):
    /// what follows a return in it is then plainly dead.</summary>
    static void Unwrap(List<Instr> b, int k)
    {
        int depth = 0, end = -1;
        for (int j = k + 1; j < b.Count; j++)
        {
            var ins = b[j];
            switch (ins.Op)
            {
                case Op.Br or Op.BrIf when ins.Imm == depth: return;
                case Op.BrTable when ins.Imm == depth || ((int[])ins.Ref!).Contains(depth): return;
                case Op.TryTable when ((TryTableInfo)ins.Ref!).Catches.Any(c => c.Label == depth): return;
            }
            if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
            else if (ins.Op == Op.End) { if (depth == 0) { end = j; break; } depth--; }
        }
        if (end < 0) return;
        depth = 0;
        for (int j = k + 1; j < end; j++)
        {
            var ins = b[j];
            switch (ins.Op)
            {
                case Op.Br or Op.BrIf when ins.Imm > depth: b[j] = ins with { Imm = ins.Imm - 1 }; break;
                case Op.BrTable:
                    b[j] = ins with { Imm = ins.Imm > depth ? ins.Imm - 1 : ins.Imm, Ref = ((int[])ins.Ref!).Select(t => t > depth ? t - 1 : t).ToArray() };
                    break;
                case Op.TryTable:
                    {
                        var tt = (TryTableInfo)ins.Ref!;
                        var nt = new TryTableInfo { BlockType = tt.BlockType, Catches = tt.Catches.Select(c => new CatchClause { Kind = c.Kind, Tag = c.Tag, Label = c.Label > depth ? c.Label - 1 : c.Label }).ToList() };
                        b[j] = ins with { Ref = nt };
                        break;
                    }
            }
            if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
            else if (ins.Op == Op.End) depth--;
        }
        b.RemoveAt(end);
        b.RemoveAt(k);
    }
}
