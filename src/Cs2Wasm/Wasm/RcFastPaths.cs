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
    // (default "loops": the counts inside loops, of every function, with a release of the last reference written out as
    // rt.release does it; the optimizing tier's bench needs that much in loops that free, the self-hosted compiler no
    // more. "1": also every count of the hot functions; "0": none)
    static readonly string s_mode = Environment.GetEnvironmentVariable("CS2WASM_RC_FAST") is { Length: > 0 } rm ? rm : "loops";
    // ("1": the hot functions' every count; "loops": only the counts inside loops, where the Inliner otherwise puts the
    // whole function, CS2WASM_RC_IN_LOOPS=0)
    static readonly bool s_off = s_mode is not ("1" or "loops");
    static readonly bool s_loopsOnly = s_mode == "loops";
    static readonly bool s_everywhere = Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_ALL") == "1";
    static readonly int s_depth = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_DEPTH"), out var d) ? d : 3;
    static readonly int s_maxBody = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_RC_FAST_MAXBODY"), out var mb) ? mb : int.MaxValue;

    /// <summary>The function rt.release calls when the count is 1, after storing 0 into it: found in its body as
    /// `local.get 0; i32.const 0; i32.store; local.get 0; call d`, else null (then the call stays for that case).</summary>
    static WasmFunction? DestroyOf(WasmFunction release)
    {
        var b = release.Body;
        for (int i = 0; i + 4 < b.Count; i++)
            if (b[i].Op == Op.LocalGet && b[i].Imm == 0 && b[i + 1].Op == Op.I32Const && b[i + 1].Imm == 0 && b[i + 2].Op == Op.I32Store && b[i + 2].Imm == 0
                && b[i + 3].Op == Op.LocalGet && b[i + 3].Imm == 0 && b[i + 4].Op == Op.Call && b[i + 4].Ref is WasmFunction d && d.Name == "rt.destroy")
                return d;
        return null;
    }

    /// <summary>CS2WASM_NO_PLAIN_FAST=1: in a program with threads no count is written out, as before. With threads the
    /// counts written out are the plain ones (rt.retain_plain, rt.release_plain: of objects no other thread can reach,
    /// TypedCounts.cs), whose common case is the same as without threads; the atomic ones stay calls.</summary>
    static readonly bool s_noPlainFast = Environment.GetEnvironmentVariable("CS2WASM_NO_PLAIN_FAST") == "1";
    /// <summary>CS2WASM_EXP_FAST_ATOMIC=1 (a test of the tests, unsound): with threads the atomic counts are written
    /// out as plain ones too (test 305 then loses counts of an object two threads count).</summary>
    static readonly bool s_expFastAtomic = Environment.GetEnvironmentVariable("CS2WASM_EXP_FAST_ATOMIC") == "1";

    public static void Run(WasmModule m, bool threads)
    {
        if (s_off) return;
        // (threads: the check of a plain count's thread, CS2WASM_SHARE_CHECK, is in the function: no copy of it)
        if (threads && (s_noPlainFast || Compiler.RuntimeFns.ShareCheckOn)) return;
        string rn = threads && !s_expFastAtomic ? "rt.retain_plain" : "rt.retain", ln = threads && !s_expFastAtomic ? "rt.release_plain" : "rt.release";
        var retain = m.Functions.FirstOrDefault(f => f.Name == rn && !f.IsImport);
        var release = m.Functions.FirstOrDefault(f => f.Name == ln && !f.IsImport);
        if (retain == null || release == null) return;
        // (what rt.release calls for the last reference, when it is that shape: its count word set to 0, then this)
        WasmFunction? destroy = s_loopsOnly ? DestroyOf(release) : null;
        var fns = m.Functions.Where(f => !f.IsImport).ToList();
        var hot = s_everywhere || s_loopsOnly ? fns.ToHashSet() : NullChecks.CalledFromLoops(fns, s_depth);
        // (each function by itself: on several threads, WasmModule.EachFunction)
        m.EachFunction(f =>
        {
            if (f == retain || f == release) return;
            if (!s_loopsOnly && (!hot.Contains(f) || f.Body.Count > s_maxBody || f.Name.StartsWith("rt.", StringComparison.Ordinal) || f.Name.StartsWith("Cs2Wasm.Runtime.", StringComparison.Ordinal)
                || !s_destroys && f.Name.StartsWith("destroy ", StringComparison.Ordinal))) return;
            bool any = false;
            foreach (var ins in f.Body) if (ins.Op == Op.Call && (ReferenceEquals(ins.Ref, retain) || ReferenceEquals(ins.Ref, release))) { any = true; break; }
            if (!any) return;
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
                // (a release of the last reference, as rt.release does it: the count cleared, the object destroyed)
                if (isRelease && destroy != null)
                {
                    A(Op.LocalGet, c); A(Op.I32Const, 1); A(Op.I32Eq);
                    A(Op.If);
                    A(Op.LocalGet, p); A(Op.I32Const, 0); A(Op.I32Store, 0, 2);
                    A(Op.LocalGet, p); o.Add(new Instr { Op = Op.Call, Ref = destroy, Pos = ins.Pos, Inl = ins.Inl });
                    A(Op.Else);
                    A(Op.LocalGet, p); o.Add(ins);
                    A(Op.End);
                }
                else { A(Op.LocalGet, p); o.Add(ins); if (isRetain) A(Op.Drop); }
                A(Op.End);
                A(Op.End);
                if (isRetain) A(Op.LocalGet, p);
            }
            if (s_debug) Console.Error.WriteLine($"rc fast {o.Count - f.Body.Count} {f.Body.Count} {f.Name}");
            f.Body = o;
        });
    }
}
