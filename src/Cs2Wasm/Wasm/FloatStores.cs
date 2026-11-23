namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 7.3: every float stored to memory is stored with .NET's NaN when it is a NaN, so no payload a machine's
/// arithmetic chose is ever in memory: not for code that reads the bits another way (a pointer cast, a struct with
/// overlapping fields, a span of bytes), and not for the engine, which copies and compares an instance's memory
/// (section 7.2). A constant and a value just loaded from memory are stored as they are. Runs on the final bodies: a
/// value that stays in locals (FrameSlots.cs) is never in memory and pays nothing. In a loop the test is written out
/// (a call there makes the engine keep the loop's values in memory); elsewhere it is a call of a two-byte helper.
/// </summary>
static class FloatStores
{
    public static void Run(List<WasmFunction> functions, Func<bool, WasmFunction> canon)
    {
        // (CS2WASM_NO_CANON=1, measurement only: what the stores cost)
        if (Environment.GetEnvironmentVariable("CS2WASM_NO_CANON") == "1") return;
        foreach (var f in functions.ToList())
        {
            if (f.IsImport || f.Name.StartsWith("rt.canon_", StringComparison.Ordinal)) continue;
            var b = f.Body;
            List<Instr>? nb = null;
            var open = new List<bool>();
            int loops = 0, t64 = -1, t32 = -1;
            for (int i = 0; i < b.Count; i++)
            {
                var op = b[i].Op;
                switch (op)
                {
                    case Op.Loop: open.Add(true); loops++; break;
                    case Op.Block or Op.If or Op.TryTable: open.Add(false); break;
                    case Op.End when open.Count > 0: if (open[^1]) loops--; open.RemoveAt(open.Count - 1); break;
                }
                if (op is Op.F64Store or Op.F32Store && !(i > 0 && b[i - 1].Op is Op.F64Const or Op.F32Const or Op.F64Load or Op.F32Load))
                {
                    if (nb == null) { nb = new List<Instr>(b.Count + 8); for (int k = 0; k < i; k++) nb.Add(b[k]); }
                    bool d = op == Op.F64Store;
                    if (loops == 0) nb.Add(new Instr { Op = Op.Call, Ref = canon(d), Pos = b[i].Pos });
                    else
                    {
                        ref int t = ref d ? ref t64 : ref t32;
                        if (t < 0) { f.Locals.Add(d ? ValType.F64 : ValType.F32); t = f.Type.Params.Length + f.Locals.Count - 1; }
                        void Add(Op o, long imm = 0) => nb.Add(new Instr { Op = o, Imm = imm, Pos = b[i].Pos });
                        // x == x ? x : NaN
                        Add(Op.LocalTee, t);
                        // (tried: the bits chosen by a select on integers, stored as an integer, which the engine makes
                        // without a branch: slower, tools/churn-raw.cs's read loop 29.9 us a frame to 34.8)
                        if (d) Add(Op.F64Const, unchecked((long)0xFFF8000000000000)); else Add(Op.F32Const, unchecked((int)0xFFC00000));
                        Add(Op.LocalGet, t); Add(Op.LocalGet, t); Add(d ? Op.F64Eq : Op.F32Eq);
                        Add(Op.Select);
                    }
                }
                nb?.Add(b[i]);
            }
            if (nb != null) f.Body = nb;
        }
    }
}
