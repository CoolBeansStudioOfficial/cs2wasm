namespace Cs2Wasm.Wasm;

/// <summary>
/// A function that keeps variables in memory takes its frame off the thread's stack when it starts: the stack pointer
/// goes down by the frame's size, and the frame is cleared. Written out in every such function that is some twenty
/// bytes, and nothing asks whether the stack has that much left: a frame past its end would be the program's statics
/// (the first thread's stack lies above them) or other objects (a worker's stack is a block of the heap).
/// So the last thing before a module is encoded: each such start becomes a call of one small function, rt.frame,
/// which takes the frame, traps when the stack has not that much left (section 3: stack overflow ends the instance)
/// and clears it. The functions get shorter, and the check is in one place.
/// </summary>
internal static class Frames
{
    public static void Run(WasmModule m, WasmGlobal sp, WasmGlobal lo)
    {
        WasmFunction? frame = null;
        // (in code that runs often, a small frame is taken where it is: RcFastPaths' reasoning, the call costs more than
        // the check, the move and a few stores)
        var hot = s_inlineMax > 0 ? NullChecks.CalledFromLoops(m.Functions.Where(f => !f.IsImport), 3) : [];
        foreach (var f in m.Functions.ToList())
        {
            if (f.IsImport) continue;
            var b = f.Body;
            // global.get sp; [local.tee entry]; i32.const n; i32.sub; local.tee fb; global.set sp
            int i = 0;
            if (b.Count < 5 || b[0].Op != Op.GlobalGet || !ReferenceEquals(b[0].Ref, sp)) continue;
            i = 1;
            int entry = -1;
            if (b[i].Op == Op.LocalTee) { entry = (int)b[i].Imm; i++; }
            if (i + 3 >= b.Count || b[i].Op != Op.I32Const || b[i].Ref != null || b[i + 1].Op != Op.I32Sub || b[i + 2].Op != Op.LocalTee
                || b[i + 3].Op != Op.GlobalSet || !ReferenceEquals(b[i + 3].Ref, sp)) continue;
            long n = b[i].Imm;
            int fb = (int)b[i + 2].Imm;
            if (n <= 0 || fb == entry) continue;
            i += 4;
            // the clearing that follows: a fill, or stores of zero through the frame's base (all of it or a part: the
            // function clears the whole frame)
            if (i + 3 < b.Count && b[i].Op == Op.LocalGet && b[i].Imm == fb && b[i + 1].Op == Op.I32Const && b[i + 1].Imm == 0 && b[i + 1].Ref == null
                && b[i + 2].Op == Op.I32Const && b[i + 2].Imm == n && b[i + 3].Op == Op.MemoryFill)
                i += 4;
            else
                while (i + 2 < b.Count && b[i].Op == Op.LocalGet && b[i].Imm == fb
                    && (b[i + 1].Op == Op.I64Const || b[i + 1].Op == Op.I32Const && b[i + 1].Ref == null) && b[i + 1].Imm == 0
                    && b[i + 2].Op is Op.I64Store or Op.I32Store && b[i + 2].Imm >= 0 && b[i + 2].Imm < n)
                    i += 3;
            int pos = b[0].Pos;
            List<Instr> start;
            if (n <= s_inlineMax && n % 8 == 0 && hot.Contains(f))
            {
                // more than the stack has left: the end of the instance (as rt.frame); then the frame, cleared
                start = [
                    new() { Op = Op.GlobalGet, Ref = sp, Pos = pos }, new() { Op = Op.GlobalGet, Ref = lo, Pos = pos }, new() { Op = Op.I32Sub, Pos = pos },
                    new() { Op = Op.I32Const, Imm = n, Pos = pos }, new() { Op = Op.I32LtU, Pos = pos },
                    new() { Op = Op.If, Pos = pos }, new() { Op = Op.Unreachable, Pos = pos }, new() { Op = Op.End, Pos = pos },
                    new() { Op = Op.GlobalGet, Ref = sp, Pos = pos }, new() { Op = Op.I32Const, Imm = n, Pos = pos }, new() { Op = Op.I32Sub, Pos = pos },
                    new() { Op = Op.LocalTee, Imm = fb, Pos = pos }, new() { Op = Op.GlobalSet, Ref = sp, Pos = pos },
                ];
                for (int k = 0; k < n; k += 8)
                {
                    start.Add(new() { Op = Op.LocalGet, Imm = fb, Pos = pos });
                    start.Add(new() { Op = Op.I64Const, Imm = 0, Pos = pos });
                    start.Add(new() { Op = Op.I64Store, Imm = k, Imm2 = 3, Pos = pos });
                }
            }
            else
            {
                frame ??= Make(m, sp, lo);
                start = [
                    new() { Op = Op.I32Const, Imm = n, Pos = pos },
                    new() { Op = Op.Call, Ref = frame, Pos = pos },
                    new() { Op = Op.LocalSet, Imm = fb, Pos = pos },
                ];
            }
            // (the stack pointer as it was, where the function puts it back from a local of its own)
            if (entry >= 0 && Reads(b, i, entry))
            {
                start.Add(new() { Op = Op.LocalGet, Imm = fb, Pos = pos });
                start.Add(new() { Op = Op.I32Const, Imm = n, Pos = pos });
                start.Add(new() { Op = Op.I32Add, Pos = pos });
                start.Add(new() { Op = Op.LocalSet, Imm = entry, Pos = pos });
            }
            b.RemoveRange(0, i);
            b.InsertRange(0, start);
        }
    }

    static readonly int s_inlineMax = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_FRAME_INLINE"), out var fi) ? fi : 64;

    static bool Reads(List<Instr> b, int from, int local)
    {
        for (int i = from; i < b.Count; i++)
            if (b[i].Op is Op.LocalGet or Op.LocalTee && b[i].Imm == local) return true;
        return false;
    }

    /// <summary>rt.frame(n) -> the frame's base: the stack pointer less n, the n bytes cleared (n a multiple of 16).</summary>
    static WasmFunction Make(WasmModule m, WasmGlobal sp, WasmGlobal lo)
    {
        var f = m.AddFunction("rt.frame", new FuncType([ValType.I32], [ValType.I32]));
        f.NoInline = true;
        f.Locals.Add(ValType.I32); // 1: the base
        f.Locals.Add(ValType.I32); // 2: the place being cleared
        var b = f.Body;
        void A(Op op, long imm = 0, object? r = null, long imm2 = 0) => b.Add(new Instr { Op = op, Imm = imm, Imm2 = imm2, Ref = r, Pos = -1 });
        // more than the stack has left: the end of the instance
        A(Op.LocalGet, 0); A(Op.GlobalGet, 0, sp); A(Op.GlobalGet, 0, lo); A(Op.I32Sub); A(Op.I32GtU);
        A(Op.If); A(Op.Unreachable); A(Op.End);
        A(Op.GlobalGet, 0, sp); A(Op.LocalGet, 0); A(Op.I32Sub); A(Op.LocalTee, 1); A(Op.GlobalSet, 0, sp);
        // a small frame word by word (a fill is a call into the engine), a large one by a fill
        A(Op.LocalGet, 0); A(Op.I32Const, 128); A(Op.I32GtU);
        A(Op.If);
        A(Op.LocalGet, 1); A(Op.I32Const, 0); A(Op.LocalGet, 0); A(Op.MemoryFill);
        A(Op.Else);
        A(Op.LocalGet, 1); A(Op.LocalSet, 2);
        A(Op.Loop);
        A(Op.LocalGet, 2); A(Op.I64Const, 0); A(Op.I64Store, 0, null, 3);
        A(Op.LocalGet, 2); A(Op.I64Const, 0); A(Op.I64Store, 8, null, 3);
        A(Op.LocalGet, 2); A(Op.I32Const, 16); A(Op.I32Add); A(Op.LocalTee, 2);
        A(Op.LocalGet, 1); A(Op.LocalGet, 0); A(Op.I32Add); A(Op.I32LtU);
        A(Op.BrIf, 0);
        A(Op.End);
        A(Op.End);
        A(Op.LocalGet, 1);
        return f;
    }
}
