namespace Cs2Wasm.Wasm;

/// <summary>
/// The counts' common case written out where they are counted, in a program without threads (section 5): a retain of
/// an object whose count is positive adds one, a release of one whose count is above one takes one; anything else (null,
/// the last reference, an arena's member, an immortal object) is the call, which reads the count again and does what it
/// always did. In the engine's baseline tier every call is a call: rt.retain and rt.release are the module's most
/// called functions. Only in code that runs often: functions a loop calls, three calls deep (NullChecks.CalledFromLoops; not the destroy functions, whose many field releases mostly end their objects),
/// where the copies (about twenty bytes each against a call's three) pay; loops' own bodies have the whole function
/// inlined already (Inliner).
/// Off by default (CS2WASM_RC_FAST=1): 4% on the parser bench, but the self-hosted compiler was within 1% either way
/// over 14 interleaved rounds while its module grew by 8.7 MB (11.7%), which the engine compiles too.
/// </summary>
static class RcFastPaths
{
    static readonly bool s_debug = Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_DEBUG") == "1";
    static readonly bool s_destroys = Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_DESTROY") == "1";
    static readonly string? s_mode = Environment.GetEnvironmentVariable("CS2WASM_RC_FAST");
    // ("1": the hot functions' every count; "loops": only the counts inside loops, where the Inliner otherwise puts the
    // whole function, CS2WASM_RC_IN_LOOPS=0)
    static readonly bool s_off = s_mode is not ("1" or "loops");
    static readonly bool s_loopsOnly = s_mode == "loops";
    static readonly bool s_everywhere = Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_ALL") == "1";
    static readonly int s_depth = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_DEPTH"), out var d) ? d : 3;
    static readonly int s_maxBody = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_MAXBODY"), out var mb) ? mb : int.MaxValue;

    public static void Run(WasmModule m, bool threads)
    {
        if (s_off || threads) return;
        var retain = m.Functions.FirstOrDefault(f => f.Name == "rt.retain" && !f.IsImport);
        var release = m.Functions.FirstOrDefault(f => f.Name == "rt.release" && !f.IsImport);
        if (retain == null || release == null) return;
        var fns = m.Functions.Where(f => !f.IsImport).ToList();
        var hot = s_everywhere || s_loopsOnly ? fns.ToHashSet() : NullChecks.CalledFromLoops(fns, s_depth);
        foreach (var f in fns)
        {
            if (!hot.Contains(f) || f.Body.Count > s_maxBody || f == retain || f == release || f.Name.StartsWith("rt.", StringComparison.Ordinal) || f.Name.StartsWith("Cs2Wasm.Runtime.", StringComparison.Ordinal)
                || !s_destroys && f.Name.StartsWith("destroy ", StringComparison.Ordinal)) continue;
            bool any = false;
            foreach (var ins in f.Body) if (ins.Op == Op.Call && (ReferenceEquals(ins.Ref, retain) || ReferenceEquals(ins.Ref, release))) { any = true; break; }
            if (!any) continue;
            int np = f.Type.Params.Length;
            int p = -1, c = -1;
            int Local() { f.Locals.Add(ValType.I32); return np + f.Locals.Count - 1; }
            var o = new List<Instr>(f.Body.Count + 64);
            var open = new Stack<bool>(); int loops = 0;
            foreach (var ins in f.Body)
            {
                if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) { open.Push(ins.Op == Op.Loop); if (ins.Op == Op.Loop) loops++; }
                else if (ins.Op == Op.End && open.Count > 0 && open.Pop()) loops--;
                bool isRetain = ReferenceEquals(ins.Ref, retain), isRelease = ReferenceEquals(ins.Ref, release);
                if (ins.Op != Op.Call || !isRetain && !isRelease || s_loopsOnly && loops == 0) { o.Add(ins); continue; }
                if (p < 0) { p = Local(); c = Local(); }
                void A(Op op, long imm = 0, long imm2 = 0) => o.Add(new Instr { Op = op, Imm = imm, Imm2 = imm2, Pos = ins.Pos, Inl = ins.Inl });
                // p = the object; when it is one and its count is above the fast path's bound: the count changed in place
                A(Op.LocalSet, p);
                A(Op.LocalGet, p);
                A(Op.If);
                A(Op.LocalGet, p); A(Op.I32Load, 0, 2); A(Op.LocalTee, c);
                A(Op.I32Const, isRetain ? 0 : 1); A(Op.I32GtS);
                A(Op.If);
                A(Op.LocalGet, p); A(Op.LocalGet, c); A(Op.I32Const, 1); A(isRetain ? Op.I32Add : Op.I32Sub); A(Op.I32Store, 0, 2);
                A(Op.Else);
                A(Op.LocalGet, p); o.Add(ins); if (isRetain) A(Op.Drop);
                A(Op.End);
                A(Op.End);
                if (isRetain) A(Op.LocalGet, p);
            }
            if (s_debug) Console.Error.WriteLine($"rc fast {o.Count - f.Body.Count} {f.Body.Count} {f.Name}");
            f.Body = o;
        }
    }
}
