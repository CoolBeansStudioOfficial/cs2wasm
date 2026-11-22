namespace Cs2Wasm.Wasm;

/// <summary>
/// Inlining (section 5) of small functions: property getters and setters, field accessors, small arithmetic
/// helpers, compiler-made thunks, wrappers. A callee is inlined only if no exception can leave it (it throws nothing
/// and calls only functions that cannot throw), so it is never on the stack at a throw and stack traces (which come
/// from real frames) are unchanged. Its parameters and locals become fresh locals of the caller (locals
/// re-zeroed on each entry, as a call would), its body goes into a block, and `return` becomes a branch to that block.
/// </summary>
static class Inliner
{
    static readonly int SmallBody = Env("CS2WASM_SMALL_BODY", 12);     // instructions of the inlined copy; inlined at every call site (12: a call costs more than that in the baseline tier; hello world 34 bytes smaller than at 7, at 16 it grows by 1.6 KB)
    static readonly bool s_inlineDebug = Environment.GetEnvironmentVariable("CS2WASM_INLINE_DEBUG") == "1";
    static readonly int s_tinyBody = Env("CS2WASM_TINY_BODY", 12); // a leaf in the second round (the final bodies), at every call site
    const int SingleCallerBody = 120; // a function with one call site and no other use is inlined up to this size
    const int SingleCallerCalling = 100; // the same for one that calls functions
    const int LoopBody = 24;      // a straight-line body that calls nothing, at a call site inside a loop
    static readonly int HintedBody = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_HINTED_BODY"), out var hb) ? hb : 24;    // a method marked AggressiveInlining, at every call site (24: the runtime's word reads, PL, at every site; hello world 45 bytes smaller than at 16)
    // A callee an exception can leave (it checks an index, calls what may throw): inlined too, and a stack trace
    // still shows its frame, from the table of inlined calls (WasmModule.InlineScopes). An engine's baseline tier
    // runs the module as it is written, every call a call: what a short run over a great many functions gets.
    static readonly int s_throwBody = Env("CS2WASM_INLINE_THROWING", 0);         // at every call site (0: off, the default: see below)
    static readonly int s_throwSingle = Env("CS2WASM_INLINE_THROWING_SINGLE", 60); // into its one caller
    static readonly int s_throwLoop = Env("CS2WASM_INLINE_THROWING_LOOP", 24);   // at a call site inside a loop
    // (CS2WASM_RC_IN_LOOPS=1: rt.retain and rt.release whole in loops. Off: on the self-hosted compiler 0.5% of wall time either
    // way and 2% less processor time without, 6.5 MB of its 72.3 less code; the optimizing tier inlines them where it matters)
    static readonly bool s_rcInLoops = Environment.GetEnvironmentVariable("CS2WASM_RC_IN_LOOPS") == "1";
    static readonly bool s_rcEverywhere = Environment.GetEnvironmentVariable("CS2WASM_INLINE_RC") == "1";
    static readonly bool s_ignoreNoInline = Environment.GetEnvironmentVariable("CS2WASM_IGNORE_NOINLINE") == "1";
    static int Env(string name, int dflt) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : dflt;
    /// <summary>The module being compiled: its table of inlined calls.</summary>
    internal static WasmModule? Module;
    static readonly int s_loopBody = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_LOOP_INLINE"), out var lb) ? lb : LoopBody;

    // (CS2WASM_EXP_INLINE=n, an experiment: bodies up to n instructions at every site, whether they can throw or not,
    // in three rounds; traces lose the inlined frames)
    static readonly int s_exp = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_EXP_INLINE"), out var x) ? x : 0;

    /// <summary>Returns the functions it changed.</summary>
    /// <summary>fastPaths (second round, once): the fast paths of FastPathIf go in too (once only: the call each leaves
    /// on its other path is a call of the same function).</summary>
    public static HashSet<WasmFunction> Run(List<WasmFunction> functions, HashSet<WasmFunction> mayThrow, bool tiny = false, bool fastPaths = false)
    {
        if (s_exp > 0) { for (int round = 0; round < 3; round++) RunOnce(functions, [], false, false); return []; }
        return RunOnce(functions, mayThrow, tiny, fastPaths);
    }

    /// <summary>tiny: only bodies of a few instructions that call nothing and have no locals (a getter once its local
    /// traffic is gone): their copy is no larger than the call.</summary>
    static HashSet<WasmFunction> RunOnce(List<WasmFunction> functions, HashSet<WasmFunction> mayThrow, bool tiny, bool fastPaths)
    {
        var changed = new HashSet<WasmFunction>();
        var callers = new Dictionary<WasmFunction, int>();
        foreach (var f in functions)
        {
            if (f.IsImport) continue;
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g) callers[g] = callers.GetValueOrDefault(g) + 1;
        }
        // (a generic family's members are counted alike, by the most called of them: the rules that look at the
        // number of callers then decide the same for each, and their callers stay one shared body: ShareConstants)
        if (s_familyCallers)
        {
            var famMax = new Dictionary<string, int>();
            foreach (var (g, c) in callers) if (g.Family != null) famMax[g.Family] = Math.Max(famMax.GetValueOrDefault(g.Family), c);
            if (famMax.Count > 0) foreach (var g in functions) if (g.Family != null && famMax.TryGetValue(g.Family, out var mc)) callers[g] = mc;
        }
        var candidates = new HashSet<WasmFunction>();
        // (in a loop the call is paid for every time round: a larger body goes in there, and only there)
        var inLoops = new HashSet<WasmFunction>();
        // (the callees whose frame a trace shows where they are inlined: those an exception can leave, inlined by
        // the rule for them. The others never were on the stack at a throw, or are marked AggressiveInlining, whose
        // frames .NET leaves out too)
        var framed = new HashSet<WasmFunction>();
        foreach (var g in functions)
        {
            // (the null check: it has no frame of its own in a trace, so a copy of it throws from the same frames; in
            // the second round, when the checks that are not needed are gone)
            if (tiny && g.Name == "rt.nonnull" && !g.IsImport && s_loopBody > 0) { inLoops.Add(g); continue; }
            // (a function that does nothing, a lock's in a program without threads: its calls go)
            if (!g.IsImport && g.ForwardTo == null && g.Body.Count == 0 && g.Type.Results.Length == 0 && g.Exports.Count == 0) { candidates.Add(g); continue; }
            if (mayThrow.Contains(g) || !Inlinable(g, mayThrow))
            {
                // (marked AggressiveInlining: in although it can throw, when it is small; larger in a loop)
                // (the count operations: they have no frame of their own in a trace, so a copy throws from the same
                // frames; in a loop the call would make the engine keep the loop's values in memory)
                if (tiny && s_rcInLoops && g.FrameName == null && g.Name is "rt.retain" or "rt.release" or "rt.retain_plain" or "rt.release_plain" && s_loopBody > 0 && InlinableHinted(g)) { if (s_rcEverywhere) candidates.Add(g); else inLoops.Add(g); continue; }
                if (g.InlineHint && s_exp == 0 && InlinableHinted(g))
                {
                    int hc = g.Body.Count + g.Type.Params.Length + 2 * g.Locals.Count + 2;
                    if (hc <= HintedBody) { candidates.Add(g); continue; }
                    if (hc <= 2 * s_loopBody) { inLoops.Add(g); continue; }
                }
                if (tiny || s_exp > 0 || s_throwBody <= 0 || Module == null || g.NoInline && !s_ignoreNoInline || !InlinableThrowing(g)) continue;
                int tc = g.Body.Count + g.Type.Params.Length + 2 * g.Locals.Count + 2;
                bool one = callers.GetValueOrDefault(g) == 1 && g.Exports.Count == 0 && !g.InTable && !g.TableRoot;
                if (tc <= s_throwBody || one && g.Body.Count <= s_throwSingle) { candidates.Add(g); framed.Add(g); }
                else if (tc <= s_throwLoop) { inLoops.Add(g); framed.Add(g); }
                continue;
            }
            int n = g.Body.Count;
            bool single = callers.GetValueOrDefault(g) == 1 && g.Exports.Count == 0 && !g.InTable;
            // at many sites only when the copy is about as small as the call (parameter moves, block, local resets)
            int copy = n + g.Type.Params.Length + 2 * g.Locals.Count + 2;
            // (a function that calls others, inlined into its one caller up to a smaller size: measured on the module)
            bool leaf = !g.Body.Any(x => x.Op == Op.Call);
            if (tiny)
            {
                if (leaf && n <= 4 && g.Locals.Count == 0 && g.Type.Params.Length <= 2) candidates.Add(g);
                // (a leaf the first round found too large only for the local traffic it had then: Hash.Combine)
                else if (leaf && copy <= s_tinyBody && ProgramCode(g)) { candidates.Add(g); if (s_inlineDebug) Console.Error.WriteLine($"tiny inline {copy} {callers.GetValueOrDefault(g)} {g.Name}"); }
                // (marked AggressiveInlining, and small now that what it called went into it)
                else if (g.InlineHint && copy <= HintedBody) candidates.Add(g);
                else if (leaf && copy <= s_loopBody && StraightLine(g)) inLoops.Add(g);
                continue;
            }
            if (copy <= (s_exp > 0 ? s_exp : SmallBody) || single && n <= (leaf ? SingleCallerBody : SingleCallerCalling)) candidates.Add(g);
            // (marked AggressiveInlining: as for one that can throw, above; it was left to the size rules alone)
            else if (g.InlineHint && s_exp == 0 && copy <= HintedBody) candidates.Add(g);
            else if (g.InlineHint && s_exp == 0 && copy <= 2 * s_loopBody) inLoops.Add(g);
            else if (leaf && copy <= s_loopBody && StraightLine(g)) inLoops.Add(g);
        }
        // (the second round: a function whose result is ready without a call most of the time, a getter that makes
        // its value once, `_x ?? Make()`: that test goes in, and the call stays for the other case)
        var fast = new Dictionary<WasmFunction, int>();
        if (tiny && fastPaths && s_fastPath > 0)
            foreach (var g in functions)
                // (where it is called from more than a couple of places: a getter used once or twice, at a start, is
                // left a call; the parser's current token is read from hundreds)
                if (!candidates.Contains(g) && !inLoops.Contains(g) && callers.GetValueOrDefault(g) >= s_fastPathCallers && FastPathIf(g) is int at)
                {
                    fast[g] = at;
                    if (s_inlineDebug) Console.Error.WriteLine($"fast path {at} {g.Body.Count} {callers.GetValueOrDefault(g)} {g.Name}");
                }
        if (candidates.Count == 0 && inLoops.Count == 0 && fast.Count == 0) return changed;
        foreach (var f in functions)
        {
            if (f.IsImport) continue;
            bool any = false;
            foreach (var ins in f.Body) if (ins.Op == Op.Call && ins.Ref is WasmFunction g && (candidates.Contains(g) || inLoops.Contains(g) || fast.ContainsKey(g)) && g != f) { any = true; break; }
            if (!any) continue;
            var outBody = new List<Instr>(f.Body.Count + 32);
            var open = new List<bool>(); // the blocks open here: whether each is a loop
            var openAt = new List<int>(); // where each starts in outBody (FieldHoist.Inlined: the loops' sets)
            int loops = 0;
            any = false;
            foreach (var ins in f.Body)
            {
                switch (ins.Op)
                {
                    case Op.Loop: open.Add(true); openAt.Add(outBody.Count); loops++; break;
                    case Op.Block or Op.If or Op.TryTable: open.Add(false); openAt.Add(-1); break;
                    case Op.End when open.Count > 0: if (open[^1]) loops--; open.RemoveAt(open.Count - 1); openAt.RemoveAt(openAt.Count - 1); break;
                }
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g && g != f && (candidates.Contains(g) || loops > 0 && inLoops.Contains(g))) { Expand(f, g, outBody, ins.Pos, ins.Inl, framed.Contains(g), openAt); any = true; }
                else if (ins.Op == Op.Call && ins.Ref is WasmFunction h && h != f && fast.TryGetValue(h, out int ifAt)) { Module?.FieldHoist?.Inlined(f, h, outBody, openAt); ExpandFastPath(f, h, ifAt, outBody, ins); any = true; }
                else outBody.Add(ins);
            }
            if (!any) continue;
            f.Body = outBody;
            changed.Add(f);
        }
        return changed;
    }

    /// <summary>No calls of functions that can throw, no throws, no exception handling, no frame (stack pointer), no
    /// imports.</summary>
    internal static bool Inlinable(WasmFunction g, HashSet<WasmFunction> mayThrow)
    {
        if (g.IsImport || g.ForwardTo != null || g.Body.Count == 0) return false;
        foreach (var ins in g.Body)
        {
            switch (ins.Op)
            {
                case Op.Call when ins.Ref is WasmFunction h && !h.IsImport && !mayThrow.Contains(h) && h != g:
                    break;
                case Op.Call or Op.CallIndirect or Op.Throw or Op.ThrowRef when s_exp > 0:
                    break;
                case Op.Call or Op.CallIndirect or Op.Throw or Op.ThrowRef or Op.TryTable:
                    return false;
                case Op.GlobalGet or Op.GlobalSet when ins.Ref is WasmGlobal { Export: "__stack_pointer" }:
                    return false;
            }
            if ((int)ins.Op >= 0xFE00) return false; // atomics: keep their functions (threads mode bodies are filled late)
        }
        return true;
    }

    /// <summary>A program's or the library's method that an exception can leave: no exception handling of its own, no
    /// frame, no atomics, not calling itself. The runtime's own functions stay calls (the passes after this one know
    /// them by their calls: the counts, the allocations, a type's initialization), and so do the dispatch stubs.</summary>
    internal static bool InlinableThrowing(WasmFunction g)
    {
        if (g.Name.StartsWith("rt.", StringComparison.Ordinal) || g.Name.StartsWith("Cs2Wasm.Runtime.", StringComparison.Ordinal)
            || g.Name.StartsWith("static init", StringComparison.Ordinal) || g.Name.StartsWith("destroy ", StringComparison.Ordinal)
            || g.Name.StartsWith("virtual ", StringComparison.Ordinal) || g.Name.StartsWith("interface ", StringComparison.Ordinal)
            || g.Name.StartsWith("arena.", StringComparison.Ordinal) || g.Name.StartsWith("borrow?", StringComparison.Ordinal) || g.Name.StartsWith("frameobj.", StringComparison.Ordinal)) return false;
        if (g.Exports.Count > 0) return false;
        return InlinableHinted(g);
    }

    /// <summary>A method marked AggressiveInlining: no exception handling of its own, no frame, no atomics.</summary>
    internal static bool InlinableHinted(WasmFunction g)
    {
        if (g.IsImport || g.ForwardTo != null || g.Body.Count == 0) return false;
        foreach (var ins in g.Body)
        {
            if (ins.Op is Op.TryTable) return false;
            if (ins.Op == Op.Call && ReferenceEquals(ins.Ref, g)) return false;
            if (ins.Op is Op.GlobalGet or Op.GlobalSet && ins.Ref is WasmGlobal { Export: "__stack_pointer" }) return false;
            if ((int)ins.Op >= 0xFE00) return false;
        }
        return true;
    }

    /// <summary>A method of the program or the library, not one of the runtime's helpers or a dispatch stub (those
    /// have rules of their own: the passes after this one know the counts and the stores by their calls).</summary>
    static bool ProgramCode(WasmFunction g) => !(g.Name.StartsWith("rt.", StringComparison.Ordinal) || g.Name.StartsWith("Cs2Wasm.Runtime.", StringComparison.Ordinal)
        || g.Name.StartsWith("virtual ", StringComparison.Ordinal) || g.Name.StartsWith("interface ", StringComparison.Ordinal)
        || g.Name.StartsWith("arena.", StringComparison.Ordinal) || g.Name.StartsWith("destroy ", StringComparison.Ordinal) || g.Name.StartsWith("static init", StringComparison.Ordinal));

    static readonly int s_fastPath = Env("CS2WASM_FAST_PATH", 8);
    static readonly bool s_familyCallers = Environment.GetEnvironmentVariable("CS2WASM_FAMILY_CALLERS") == "1"; // (measured: within 0.1% either way, off)
    static readonly int s_fastPathCallers = Env("CS2WASM_FAST_PATH_CALLERS", 3); // the instructions of a fast path's test and value together (0: none)

    /// <summary>Whether an instruction computes without an effect, and cannot trap: a local's or a global's read or
    /// write, a load, a constant, arithmetic but division (a module's own checks throw before a division by zero).</summary>
    static bool Pure(in Instr ins) => ins.Op switch
    {
        Op.LocalGet or Op.LocalSet or Op.LocalTee or Op.GlobalGet or Op.Drop or Op.Select => true,
        Op.Nop => ins.Ref == null,
        >= Op.I32Load and <= Op.I64Load32U => true,
        >= Op.I32Const and <= Op.F64Const => true,
        Op.I32DivS or Op.I32DivU or Op.I32RemS or Op.I32RemU or Op.I64DivS or Op.I64DivU or Op.I64RemS or Op.I64RemU => false,
        >= Op.I32TruncF32S and <= Op.I32TruncF64U or >= Op.I64TruncF32S and <= Op.I64TruncF64U => false,
        >= Op.I32Eqz and <= Op.I64Extend32S => true,
        _ => false,
    };

    /// <summary>A function of the shape `test; if { anything } end; value; return`, where the test and the value are
    /// pure (Pure) and short, and the test is whether a value read from memory is made yet: the index of its `if`,
    /// else null. At a call site the test and the value go in, and the
    /// `if` calls the function itself (which runs its test again, as nothing was changed meanwhile): what is left out
    /// of the caller runs in the function's own frame, so a trace is the same. Program and library methods only, not
    /// marked NoInlining.</summary>
    static int? FastPathIf(WasmFunction g)
    {
        if (g.IsImport || g.ForwardTo != null || g.NoInline || g.Type.Results.Length != 1 || !ProgramCode(g) || g.Name.StartsWith("borrow?", StringComparison.Ordinal)) return null;
        var b = g.Body;
        int at = 0;
        while (at < b.Count && Pure(b[at])) at++;
        if (at == 0 || at >= b.Count || b[at].Op != Op.If || b[at].Ref != null) return null;
        // (the test is of a value read from memory that is not made yet: a field that is null, `_x ?? Make()`, or a
        // type's initializer that has not run. A test of anything else may well hold most of the time, Append's
        // `value != null`, and then the copy is only more work before the same call)
        static bool IsLoad(Op op) => op >= Op.I32Load && op <= Op.I64Load32U;
        bool lazy = at >= 2 && b[at - 1].Op == Op.I32Eqz && IsLoad(b[at - 2].Op)
            || at >= 3 && b[at - 1].Op == Op.I32Ne && b[at - 2].Op == Op.I32Const && b[at - 2].Imm == -1 && IsLoad(b[at - 3].Op);
        if (!lazy) return null;
        // the `if`'s end (no `else`)
        int depth = 0, end = -1;
        for (int k = at; k < b.Count; k++)
        {
            var op = b[k].Op;
            if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
            else if (op == Op.Else && depth == 1) return null;
            else if (op == Op.End && --depth == 0) { end = k; break; }
        }
        if (end < 0) return null;
        int last = b.Count;
        if (b[^1].Op == Op.Return) last--;
        for (int k = end + 1; k < last; k++) if (!Pure(b[k])) return null;
        if (at + (last - end - 1) > s_fastPath || last == end + 1) return null;
        return at;
    }

    /// <summary>The call of a function with a fast path (FastPathIf) becomes the path: its arguments in locals of the
    /// caller, the test; when it holds, the call itself, its result the block's; else the value.</summary>
    static void ExpandFastPath(WasmFunction f, WasmFunction g, int ifAt, List<Instr> outBody, Instr call)
    {
        int np = g.Type.Params.Length, baseF = f.Type.Params.Length;
        var map = new int[np + g.Locals.Count];
        for (int i = 0; i < np; i++) { f.Locals.Add(g.Type.Params[i]); map[i] = baseF + f.Locals.Count - 1; }
        for (int i = 0; i < g.Locals.Count; i++) { f.Locals.Add(g.Locals[i]); map[np + i] = baseF + f.Locals.Count - 1; }
        void Add(Op op, long imm = 0, object? r = null) => outBody.Add(new Instr { Op = op, Imm = imm, Ref = r, Pos = call.Pos, Inl = call.Inl });
        void Copy(Instr ins)
        {
            if (ins.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee) ins.Imm = map[(int)ins.Imm];
            ins.Pos = call.Pos; ins.Inl = call.Inl;
            outBody.Add(ins);
        }
        for (int i = np - 1; i >= 0; i--) Add(Op.LocalSet, map[i]);
        for (int i = 0; i < g.Locals.Count; i++)
        {
            switch (g.Locals[i])
            {
                case ValType.I32: Add(Op.I32Const, 0); break;
                case ValType.I64: Add(Op.I64Const, 0); break;
                case ValType.F32: Add(Op.F32Const, 0); break;
                case ValType.F64: Add(Op.F64Const, 0); break;
                default: continue;
            }
            Add(Op.LocalSet, map[np + i]);
        }
        var b = g.Body;
        Add(Op.Block, 0, g.Type.Results[0]);
        for (int k = 0; k < ifAt; k++) Copy(b[k]);
        Add(Op.If);
        for (int i = 0; i < np; i++) Add(Op.LocalGet, map[i]);
        outBody.Add(call);
        Add(Op.Br, 1);
        Add(Op.End);
        int depth = 0, end = ifAt;
        for (; end < b.Count; end++)
        {
            if (b[end].Op is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
            else if (b[end].Op == Op.End && --depth == 0) break;
        }
        int last = b[^1].Op == Op.Return ? b.Count - 1 : b.Count;
        for (int k = end + 1; k < last; k++) Copy(b[k]);
        Add(Op.End);
    }

    /// <summary>No return but possibly a last one, and no branch out of the body.</summary>
    static bool StraightLine(WasmFunction g)
    {
        int depth = 0;
        for (int k = 0; k < g.Body.Count; k++)
        {
            var ins = g.Body[k];
            switch (ins.Op)
            {
                case Op.Block or Op.Loop or Op.If or Op.TryTable: depth++; break;
                case Op.End: depth--; break;
                case Op.Return: if (k != g.Body.Count - 1) return false; break;
                case Op.Br or Op.BrIf: if (ins.Imm >= depth) return false; break;
                case Op.BrTable: return false;
                case Op.Unreachable: if (depth == 0 && k != g.Body.Count - 1) return false; break;
            }
        }
        return depth == 0;
    }

    internal static void Expand(WasmFunction f, WasmFunction g, List<Instr> outBody, int pos, int siteScope = 0, bool framed = false, List<int>? openLoops = null)
    {
        // (the fields g's code stores are stored where it went: in f, in the loops open here; FieldLoads.cs)
        Module?.FieldHoist?.Inlined(f, g, outBody, openLoops);
        // The scope of the copy: the call's own, and inside it the callee's when the callee is a frame of a trace.
        // What the callee had inlined in itself keeps its scopes, inside that one.
        int scope = framed && g.FrameName != null && Module != null ? Module.InlineScope(siteScope, g, pos) : siteScope;
        Dictionary<int, int>? inner = null;
        int Scoped(int own)
        {
            if (own == 0 || Module == null) return scope;
            if ((inner ??= []).TryGetValue(own, out int s)) return s;
            var (parent, fn, at) = Module.InlineScopes[own];
            return inner[own] = Module.InlineScope(Scoped(parent), fn, at);
        }
        int np = g.Type.Params.Length;
        int baseF = f.Type.Params.Length;
        // The last arguments that are a constant or a read of a local, of parameters the callee never writes: not put
        // into a local of their own; each read of the parameter is the argument itself (a constant folds with what
        // uses it; the caller's local cannot change inside the copy, which writes only its own). The others go into
        // locals, as a call's arguments would.
        var written = new bool[np];
        foreach (var ins in g.Body) if (ins.Op is Op.LocalSet or Op.LocalTee && ins.Imm < np) written[ins.Imm] = true;
        var subst = new Instr?[np];
        int direct = 0;
        for (int i = np - 1; i >= 0; i--)
        {
            int at = outBody.Count - 1 - direct;
            if (at < 0 || written[i]) break;
            var a = outBody[at];
            if (a.Op is not (Op.I32Const or Op.I64Const or Op.F32Const or Op.F64Const or Op.LocalGet)) break;
            subst[i] = a;
            direct++;
        }
        if (direct > 0) outBody.RemoveRange(outBody.Count - direct, direct);
        var map = new int[np + g.Locals.Count];
        for (int i = 0; i < np; i++) { if (subst[i] != null) { map[i] = -1; continue; } f.Locals.Add(g.Type.Params[i]); map[i] = baseF + f.Locals.Count - 1; }
        for (int i = 0; i < g.Locals.Count; i++) { f.Locals.Add(g.Locals[i]); map[np + i] = baseF + f.Locals.Count - 1; }
        void Add(Op op, long imm = 0, object? r = null) => outBody.Add(new Instr { Op = op, Imm = imm, Ref = r, Pos = pos, Inl = siteScope });
        // a read of a parameter whose argument went in directly: the argument
        Instr Mapped(Instr ins)
        {
            if (ins.Op == Op.LocalGet && ins.Imm < np && subst[ins.Imm] is { } a) return a with { Pos = ins.Pos, Inl = ins.Inl };
            if (ins.Op is Op.LocalGet or Op.LocalSet or Op.LocalTee) ins.Imm = map[(int)ins.Imm];
            return ins;
        }
        // arguments: last on top of the stack
        for (int i = np - 1; i >= 0; i--) if (subst[i] == null) Add(Op.LocalSet, map[i]);
        // the callee's locals start at zero, as on a call
        for (int i = 0; i < g.Locals.Count; i++)
        {
            switch (g.Locals[i])
            {
                case ValType.I32: Add(Op.I32Const, 0); break;
                case ValType.I64: Add(Op.I64Const, 0); break;
                case ValType.F32: Add(Op.F32Const, 0); break;
                case ValType.F64: Add(Op.F64Const, 0); break;
                default: continue; // reference locals start null; none in inlinable bodies
            }
            Add(Op.LocalSet, map[np + i]);
        }
        if (g.Body.Count == 0) return;
        // a body that runs straight to its one return, at its end, goes in as it is: no block, no branch
        if (StraightLine(g))
        {
            for (int k = 0; k < g.Body.Count - 1; k++)
            {
                var ins = Mapped(g.Body[k]);
                ins.Inl = Scoped(g.Body[k].Inl);
                Emitter.StampInlined(ref ins, g);
                outBody.Add(ins);
            }
            if (g.Body[^1].Op != Op.Return) { var last = Mapped(g.Body[^1]); last.Inl = Scoped(g.Body[^1].Inl); outBody.Add(last); }
            return;
        }
        object? bt = g.Type.Results.Length switch { 0 => null, 1 => g.Type.Results[0], _ => new FuncType([], g.Type.Results) };
        Add(Op.Block, 0, bt);
        int depth = 0;
        foreach (var src in g.Body)
        {
            var ins = src;
            switch (ins.Op)
            {
                case Op.LocalGet or Op.LocalSet or Op.LocalTee: ins = Mapped(ins); break;
                case Op.Block or Op.Loop or Op.If or Op.TryTable: depth++; break;
                case Op.End: depth--; break;
                case Op.Return: ins.Op = Op.Br; ins.Imm = depth; break;
            }
            ins.Inl = Scoped(ins.Inl);
            Emitter.StampInlined(ref ins, g);
            outBody.Add(ins);
        }
        Add(Op.End);
    }
}
