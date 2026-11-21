namespace Cs2Wasm.Wasm.Ssa;

/// <summary>
/// The spike's passes on the final bodies (docs/AUDIT.md step 5, "the spike"), run by WasmModule.Encode before the
/// counts' fast paths are written out. All off by default:
///   CS2WASM_IR_PAIRS=1        count pairs across blocks and calls (CountPairs.cs)
///   CS2WASM_IR_GVN=1          loads reused across blocks and calls that store nothing (LoadReuse.cs)
///   CS2WASM_IR_STATS=1        what each found and why it left what it left
///   CS2WASM_IR_STRIP_RC=1     measurement only, leaks: every rt.release of program code removed (a bound on what
///                             removing releases could give: their calls and the frees they lead to)
/// Programs without threads only (another thread could drop what this one borrows).
/// </summary>
static class IrOpt
{
    static readonly bool s_pairs = Environment.GetEnvironmentVariable("CS2WASM_IR_PAIRS") == "1";
    static readonly bool s_gvn = Environment.GetEnvironmentVariable("CS2WASM_IR_GVN") == "1";
    static readonly bool s_stats = Environment.GetEnvironmentVariable("CS2WASM_IR_STATS") == "1";
    static readonly bool s_strip = Environment.GetEnvironmentVariable("CS2WASM_IR_STRIP_RC") == "1";
    static readonly string? s_dump = Environment.GetEnvironmentVariable("CS2WASM_IR_DUMP") is { Length: > 0 } d ? d : null;

    /// <summary>Program and library code: not the runtime's own functions (rt.*, Cs2Wasm.Runtime.*) nor the
    /// destruction functions, whose counts are the machinery itself.</summary>
    static bool Eligible(WasmFunction f) => !f.IsImport && f.ForwardTo == null && f.Body.Count > 0
        && !f.Name.StartsWith("rt.", StringComparison.Ordinal) && !f.Name.StartsWith("Cs2Wasm.Runtime.", StringComparison.Ordinal)
        && !f.Name.StartsWith("destroy ", StringComparison.Ordinal) && !f.Name.StartsWith("framedisplay.", StringComparison.Ordinal)
        && !f.Name.StartsWith("arena.", StringComparison.Ordinal);

    public static void Run(List<WasmFunction> fns, HashSet<WasmFunction> mayThrow, bool threads)
    {
        if (threads || !s_pairs && !s_gvn && !s_strip) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = Effects.Compute(fns, mayThrow);
        if (s_strip)
        {
            foreach (var f in fns)
            {
                if (!Eligible(f)) continue;
                var b = f.Body;
                for (int i = 0; i < b.Count; i++)
                {
                    if (b[i].Op != Op.Call) continue;
                    // (releases only: a retain removed while a store's release of the old value stays frees too early;
                    // without the releases counts only go up, and less is freed)
                    if (ReferenceEquals(b[i].Ref, e.Release)) b[i] = new Instr { Op = Op.Drop, Pos = -1 };
                }
                f.Body = b.Where(x => x.Op != Op.Nop).ToList();
                Peephole.Run(f);
            }
        }
        var pst = new CountPairs.Stats();
        var gst = new LoadReuse.Stats();
        Locals.MayThrow = mayThrow;
        try
        {
            foreach (var f in fns)
            {
                if (!Eligible(f)) continue;
                bool changed = false;
                if (s_pairs) changed |= CountPairs.Run(f, e, pst);
                if (s_gvn) changed |= LoadReuse.Run(f, e, gst);
                if (changed) { Peephole.Run(f); Locals.Run(f); if (s_dump != null && f.Name.Contains(s_dump)) CountPairs.Dump(f, "after the spike's passes"); }
            }
        }
        finally { Locals.MayThrow = null; }
        if (s_stats)
        {
            Console.Error.WriteLine($"ir: effects {e.MayDrop.Count} may drop, {e.MayStore.Count} may store, {e.NeverReturns.Count} never return, of {fns.Count}; {sw.ElapsedMilliseconds} ms");
            if (s_pairs) Console.Error.Write(pst.ToString());
            if (s_gvn) Console.Error.Write(gst.ToString());
            if (Environment.GetEnvironmentVariable("CS2WASM_IR_STATS_FILE") is { Length: > 0 } file)
                File.WriteAllLines(file, gst.ByFunction.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}"));
        }
    }
}
