using System.Text;

namespace Il2Wasm.Wasm;

internal sealed class WasmModule
{
    public readonly List<WasmFunction> Functions = [];
    public readonly List<WasmGlobal> Globals = [];
    public readonly List<WasmTag> Tags = [];
    public readonly List<DataSegment> Data = [];
    public readonly List<CustomSection> CustomSections = [];
    public readonly List<WasmFunction> Table = [];
    public string MemoryModule = "env", MemoryName = "memory";
    public int MemoryMinPages = 256, MemoryMaxPages = 16384;
    public bool MemoryShared = true;
    /// <summary>The program can start threads (another thread may drop a reference this one reads).</summary>
    public bool UsesThreads = true;
    /// <summary>A build that counts its count operations (the IL front end's IL2WASM_IL_COUNT_RC): no written-out fast paths.</summary>
    public bool CountingRc;

    public WasmFunction AddFunction(string name, FuncType type)
    {
        var f = new WasmFunction(name, type) { Order = Functions.Count };
        Functions.Add(f);
        return f;
    }

    public WasmFunction AddImport(string module, string name, string debugName, FuncType type)
    {
        var f = AddFunction(debugName, type);
        f.ImportModule = module; f.ImportName = name;
        return f;
    }

    /// <summary>The functions reachable from the exports and the data's table references (before optimization:
    /// a superset of what the encoder keeps).</summary>
    public HashSet<WasmFunction> LiveFunctions(Func<WasmFunction, IEnumerable<WasmFunction>>? extra = null)
    {
        var live = new HashSet<WasmFunction>();
        var work = new Stack<WasmFunction>();
        foreach (var f in Functions) if (f.Exports.Count > 0 || f.TableRoot) { live.Add(f); work.Push(f); }
        while (work.Count > 0)
        {
            var f = work.Pop();
            if (f.ForwardTo != null && live.Add(f.ForwardTo)) work.Push(f.ForwardTo);
            foreach (var ins in f.Body)
                if (ins.Ref is WasmFunction g && (ins.Op == Op.Call || ins.Op == Op.I32Const) && live.Add(g)) work.Push(g);
            if (extra != null) foreach (var g in extra(f)) if (live.Add(g)) work.Push(g);
        }
        return live;
    }

    /// <summary>For an internal error's message: the shortest chain of calls from a root to the function.</summary>
    string WhyLive(WasmFunction target)
    {
        var parent = new Dictionary<WasmFunction, WasmFunction?>();
        var q = new Queue<WasmFunction>();
        foreach (var f in Functions) if (f.Exports.Count > 0 || f.TableRoot) { parent[f] = null; q.Enqueue(f); }
        while (q.Count > 0)
        {
            var f = q.Dequeue();
            if (f == target) break;
            if (f.ForwardTo != null && parent.TryAdd(f.ForwardTo, f)) q.Enqueue(f.ForwardTo);
            foreach (var ins in f.Body)
                if (ins.Ref is WasmFunction g && parent.TryAdd(g, f)) q.Enqueue(g);
        }
        var sb = new System.Text.StringBuilder();
        for (var f = parent.GetValueOrDefault(target); f != null; f = parent.GetValueOrDefault(f)) sb.Append(", called by ").Append(f.Name);
        return sb.ToString();
    }

    static readonly List<(int, string)>? s_sizes = Environment.GetEnvironmentVariable("IL2WASM_SIZES") == "1" ? [] : null;
    /// <summary>IL2WASM_SIZES_FILE=path: every function's encoded body size and name, one per line (a size report).</summary>
    static readonly List<string>? s_sizesFile = Environment.GetEnvironmentVariable("IL2WASM_SIZES_FILE") is { Length: > 0 } ? [] : null;
    static readonly string? s_dump = Environment.GetEnvironmentVariable("IL2WASM_DUMP") is { Length: > 0 } d ? d : null;
    /// <summary>The fields each loop and each call can store (Wasm/FieldLoads.cs); null: that pass is off.</summary>
    public FieldHoist? FieldHoist;
    /// <summary>The module's number for each internal type id (Driver.AssignTypeIds).</summary>
    public Func<int, int>? TypeIdMap;
    readonly HashSet<int> _deadTable = [];
    WasmFunction? _tableTrap;

    public int TableSlot(WasmFunction f)
    {
        if (!f.InTable) { f.InTable = true; f.TableIndex = Table.Count; Table.Add(f); }
        return f.TableIndex;
    }

    /// <summary>One emitted instruction that has a source position: absolute byte offset in the module.</summary>
    public readonly record struct LineEntry(int Offset, int Pos);

    /// <summary>Type-check every function (Validator) in Encode: a checked build (CompileOptions.Checked) and
    /// IL2WASM_VALIDATE=1. The problems found are the build's errors (ValidationProblems).</summary>
    public bool ValidateAlways;
    /// <summary>Whether a trace shows the line of a source position (the program's own files; .NET shows none for the
    /// library's frames). Null: every position counts.</summary>
    public Func<int, bool>? PosShown;
    public readonly List<string> ValidationProblems = [];

    void Validate(string when)
    {
        int n = 0;
        foreach (var f in Functions)
        {
            if (f.IsImport || f.ForwardTo != null) continue;
            if (Validator.Check(f, Globals) is { } problem && n++ < 60)
            {
                ValidationProblems.Add($"invalid code ({when}): {problem}");
                // (IL2WASM_DUMP_INVALID=name: the body of such a function, to see what was made)
                if (Environment.GetEnvironmentVariable("IL2WASM_DUMP_INVALID") is { } di && (f.FrameName ?? f.Name).Contains(di))
                    for (int i = 0; i < f.Body.Count; i++) Console.Error.WriteLine($"  {i}: {f.Body[i]}");
            }
        }
    }

    public sealed class Encoded
    {
        public byte[] Bytes = [];
        public List<LineEntry> Lines = [];
        /// <summary>Where an instruction that can be a frame's position in a trace (a call, a throw) lies in inlined
        /// code: its absolute byte offset in the module, and its inline scope.</summary>
        public List<(int Offset, int Scope)> Inlines = [];
        public List<WasmFunction> FunctionOrder = [];
    }

    /// <summary>The functions an exception can leave: those that throw (or rethrow), call indirectly, are imports
    /// (the host may throw), or call one that can.</summary>
    HashSet<WasmFunction> ComputeMayThrow()
    {
        var may = new HashSet<WasmFunction>();
        var callers = new Dictionary<WasmFunction, List<WasmFunction>>();
        var work = new Stack<WasmFunction>();
        foreach (var f in Functions)
        {
            bool t = f.IsImport;
            foreach (var ins in f.Body)
            {
                if (ins.Op is Op.Throw or Op.ThrowRef or Op.CallIndirect) t = true;
                else if (ins.Op == Op.Call && ins.Ref is WasmFunction g)
                {
                    if (!callers.TryGetValue(g, out var l)) callers[g] = l = [];
                    l.Add(f);
                }
            }
            if (f.ForwardTo != null) { if (!callers.TryGetValue(f.ForwardTo, out var l)) callers[f.ForwardTo] = l = []; l.Add(f); }
            if (t && may.Add(f)) work.Push(f);
        }
        while (work.Count > 0)
            if (callers.TryGetValue(work.Pop(), out var cs))
                foreach (var c in cs) if (may.Add(c)) work.Push(c);
        return may;
    }

    /// <summary>Runs on the optimized bodies before they are encoded (measurement: IL2WASM_COUNT_RC's site counters).</summary>
    public Action<IEnumerable<WasmFunction>>? AfterOptimize;
    /// <summary>The helper that gives a float NaN the bits .NET gives it (double: true), for FloatStores.cs.</summary>
    public Func<bool, WasmFunction>? Canon;
    public WasmGlobal? StackPointer;
    /// <summary>The lowest address of the running thread's stack (Frames.cs).</summary>
    public WasmGlobal? StackLo;
    /// <summary>IL2WASM_NO_MERGE=1: identical bodies stay apart (measurement: each site keeps its own name).</summary>
    static readonly bool s_noLoops = Environment.GetEnvironmentVariable("IL2WASM_NO_LOOPS") == "1";
    static readonly bool s_noMerge = Environment.GetEnvironmentVariable("IL2WASM_NO_MERGE") == "1";

    /// <summary>The calls the compiler inlined whose method is a frame of a stack trace (Inliner): the scope the call
    /// was in (0: the function's own code), the method, and the call's source position. An instruction's Inl names
    /// the innermost; the engine rebuilds the frames from the table Encode makes of them (il2wasm.inline).</summary>
    public readonly List<(int parent, WasmFunction fn, int pos)> InlineScopes = [default];
    readonly Dictionary<(int, WasmFunction, int), int> _inlineScopeIds = [];
    public int InlineScope(int parent, WasmFunction fn, int pos)
    {
        if (_inlineScopeIds.TryGetValue((parent, fn, pos), out int id)) return id;
        InlineScopes.Add((parent, fn, pos));
        return _inlineScopeIds[(parent, fn, pos)] = InlineScopes.Count - 1;
    }

    static readonly bool s_noConstLocals = Environment.GetEnvironmentVariable("IL2WASM_NO_CONST_LOCALS") == "1";
    static readonly bool s_noInitChecks = Environment.GetEnvironmentVariable("IL2WASM_NO_INIT_CHECKS") == "1";

    static readonly bool s_times = Environment.GetEnvironmentVariable("IL2WASM_TIMES") == "1";
    readonly System.Diagnostics.Stopwatch _encClock = new();
    /// <summary>IL2WASM_TIMES=1: the encoder's own steps, milliseconds since the last.</summary>
    void Step(string what) { if (!s_times) return; Console.Error.WriteLine($"      {_encClock.ElapsedMilliseconds,6} ms  encode: {what}"); _encClock.Restart(); }

    /// <summary>IL2WASM_SERIAL_PASSES=1: the passes that look at one function at a time run on this thread alone.</summary>
    static readonly bool s_serialPasses = Environment.GetEnvironmentVariable("IL2WASM_SERIAL_PASSES") == "1";
    /// <summary>From how many functions such a pass runs on several threads (IL2WASM_PARALLEL_FROM; 1 to check that a
    /// small program's module is the same either way).</summary>
    static readonly int s_parallelFrom = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_PARALLEL_FROM"), out var pf) && pf > 0 ? pf : 4096;

    /// <summary>Runs a pass over every function that is not an import, on several threads. Only for a pass that reads
    /// and writes the one function it is given (its body and locals), and of the others no more than what no pass
    /// changes (their names, types, whether they are imports): then each function ends the same whatever order the
    /// functions are done in, and the module is the one a loop over them in order makes. Locals' per-thread settings
    /// (Locals.MayThrow, Threads, StackPointer) are this thread's on each. An exception is the one the loop in order
    /// would have met first.</summary>
    internal void EachFunction(Action<WasmFunction> pass)
    {
        var fns = Functions;
        int n = fns.Count;
        if (s_serialPasses || n < s_parallelFrom || Environment.ProcessorCount < 2)
        {
            foreach (var f in fns) if (!f.IsImport) pass(f);
            return;
        }
        var mayThrow = Locals.MayThrow; bool threads = Locals.Threads; var sp = Locals.StackPointer;
        const int Chunk = 512;
        int next = 0;
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<int, Exception>();
        int workers = Math.Min(Environment.ProcessorCount, 16);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            var (m0, t0, s0) = (Locals.MayThrow, Locals.Threads, Locals.StackPointer);
            Locals.MayThrow = mayThrow; Locals.Threads = threads; Locals.StackPointer = sp;
            try
            {
                while (true)
                {
                    int start = Interlocked.Add(ref next, Chunk) - Chunk;
                    if (start >= n) break;
                    for (int i = start; i < Math.Min(n, start + Chunk); i++)
                    {
                        var f = fns[i];
                        if (f.IsImport) continue;
                        try { pass(f); }
                        catch (Exception ex) { failures.TryAdd(i, ex); break; }
                    }
                }
            }
            finally { Locals.MayThrow = m0; Locals.Threads = t0; Locals.StackPointer = s0; }
        });
        if (!failures.IsEmpty) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[failures.Keys.Min()]).Throw();
    }

    public Encoded Encode(bool optimize)
    {
        _encClock.Restart();
        foreach (var f in Functions) ResolveForwards(f);
        bool validate = ValidateAlways || Environment.GetEnvironmentVariable("IL2WASM_VALIDATE") == "1";
        if (validate) Validate("before optimization");
        if (optimize)
        {
            ForwardPureForwarders();
            // (identical bodies first: a merged function's call sites count as its callers)
            if (!s_noMerge) MergeIdenticalBodies();
            Step("forwarders, merge");
            foreach (var f in Functions) ResolveForwards(f);
            Inliner.Module = this;
            Inliner.Run(Functions, ComputeMayThrow());
            Step("inline");
            EachFunction(f => { ConstBranches.Run(f); Peephole.Run(f); });
            Step("const branches, peephole");
            if (!s_noInitChecks) EachFunction(f => InitChecks.Run(f));
            if (Functions.FirstOrDefault(f => f.Name == "rt.nonnull") is { } nonNull && Environment.GetEnvironmentVariable("IL2WASM_NO_NULL_CHECKS") != "1")
                foreach (var f in Functions) if (!f.IsImport) NullChecks.Run(f, nonNull);
            Step("init checks, null checks");
            if (Environment.GetEnvironmentVariable("IL2WASM_NO_LOCALS") != "1")
            {
                Locals.MayThrow = ComputeMayThrow();
                Locals.Threads = UsesThreads;
                Locals.StackPointer = StackPointer;
                var loopNonNull = Functions.FirstOrDefault(f => f.Name == "rt.nonnull");
                try
                {
                    foreach (var f in Functions)
                    {
                        if (f.IsImport) continue;
                        if (Locals.PromoteFrame(f)) Peephole.Run(f);
                        Locals.Run(f);
                        // (the constants a generic method's locals hold once its type arguments are known and
                        // Unsafe.SizeOf<T> went in: the compares on them fold, and the second round can inline what is left)
                        if (!s_noConstLocals && ConstBranches.ConstLocals(f)) { Peephole.Run(f); ConstBranches.Run(f); Peephole.Run(f); Locals.Run(f); }
                        // (the index checks a loop's own condition has made: on the tidied body, where they are told)
                        if (!s_noLoops && Loops.Run(f, loopNonNull)) { Peephole.Run(f); Locals.Run(f); }
                    }
                }
                finally { Locals.MayThrow = null; }
                Step("locals, frames, const locals, loops");
                if (Environment.GetEnvironmentVariable("IL2WASM_NO_FRESH") != "1")
                    Fresh.Run(Functions, Functions.FirstOrDefault(f => f.Name.StartsWith("Il2Wasm.Runtime.Rt.AllocObject(")));
                Step("fresh");
                // Once more: a getter is a load and a return only after the passes above took its local traffic out, and
                // small enough to inline only then. The functions it went into are cleaned again.
                var mayThrow = ComputeMayThrow();
                var leafExpanded = ExpandLeafReleases();
                var again = Inliner.Run(Functions, mayThrow, tiny: true, fastPaths: true);
                // (twice: what went into a function the first time may itself call one small enough now, a getter that
                // forwards to a helper whose body folded to a constant once what it called went in: Unsafe.SizeOf<T>,
                // merged with another function that returns the same constant, is inlined only in this round, and
                // a switch on it then folds, ConstLocals; the helper may throw no more)
                for (int round = 0, n = again.Count; round < 2 && n > 0; round++)
                {
                    if (!s_noConstLocals)
                    {
                        bool folded = false;
                        foreach (var f in again) if (ConstBranches.ConstLocals(f)) { Peephole.Run(f); ConstBranches.Run(f); Peephole.Run(f); folded = true; }
                        if (folded) mayThrow = ComputeMayThrow();
                    }
                    var more = Inliner.Run(Functions, mayThrow, tiny: true);
                    n = more.Count;
                    again.UnionWith(more);
                }
                again.UnionWith(leafExpanded);
                Step("leaf releases, inline again");
                {
                    var nn = Functions.FirstOrDefault(f => f.Name == "rt.nonnull");
                    Locals.MayThrow = mayThrow;
                    try
                    {
                        // (and the frames once more: a function whose own frame went in the first round can go into
                        // its callers now, and their struct values stay in locals in turn)
                        foreach (var f in Functions)
                        {
                            if (f.IsImport) continue;
                            bool promoted = Locals.PromoteFrame(f);
                            if (!promoted && !again.Contains(f)) continue;
                            Peephole.Run(f);
                            if (nn != null) NullChecks.Run(f, nn);
                            Locals.Run(f);
                        }
                    }
                    finally { Locals.MayThrow = null; }
                }
                Step("frames, locals again");
                // (the checks of a type's initialization that the inlined bodies brought next to the caller's own)
                if (!s_noInitChecks) foreach (var f in Functions) if (!f.IsImport) { if (again.Contains(f)) ConstBranches.Run(f); if (InitChecks.Run(f)) Peephole.Run(f); }
                Step("init checks again");
                // the copies and fills of constant size that are left become loads and stores
                if (Environment.GetEnvironmentVariable("IL2WASM_NO_SMALL_BLOCKS") != "1")
                {
                    Locals.MayThrow = mayThrow;
                    try { EachFunction(f => { if (SmallBlocks.Run(f)) { Peephole.Run(f); Locals.Run(f); } }); }
                    finally { Locals.MayThrow = null; }
                }
                Step("small blocks");
                // the loads a loop repeats (on the final bodies: the structs are in locals, the small helpers inlined)
                if (Environment.GetEnvironmentVariable("IL2WASM_NO_LOOP_LOADS") != "1")
                {
                    Locals.MayThrow = mayThrow;
                    try { foreach (var f in Functions) if (!f.IsImport && LoopLoads.Run(f, FieldHoist != null && !FieldLoads.Without("null"))) { Peephole.Run(f); Locals.Run(f); } }
                    finally { Locals.MayThrow = null; }
                }
                Step("loop loads");
                // (a static's reference read once before a loop that nothing in it can store to: StaticLoads.cs)
                {
                    Locals.MayThrow = mayThrow;
                    try { foreach (var f in Functions) if (!f.IsImport && StaticLoads.Run(f)) { Peephole.Run(f); Locals.Run(f); } }
                    finally { Locals.MayThrow = null; }
                }
                Step("static loads");
                // (a field read once before a loop that nothing in it can store, an array's length: FieldLoads.cs.
                // Again while it finds more: the array a hoisted field holds, its length)
                if (FieldHoist != null)
                {
                    Locals.MayThrow = mayThrow;
                    var fnn = Functions.FirstOrDefault(f => f.Name == "rt.nonnull");
                    try
                    {
                        foreach (var f in Functions)
                        {
                            if (f.IsImport) continue;
                            for (int round = 0; round < 3 && FieldLoads.Run(f, FieldHoist, fnn); round++) { Peephole.Run(f); Locals.Run(f); }
                            // (and a field read a second time with nothing between that can store it)
                            if (FieldLoads.Reuse(f, FieldHoist, fnn)) { Peephole.Run(f); Locals.Run(f); }
                        }
                    }
                    finally { Locals.MayThrow = null; }
                    if (Environment.GetEnvironmentVariable("IL2WASM_FIELD_HOIST_STATS") == "1") Console.Error.WriteLine($"field hoist: {FieldLoads.Hoisted} fields and {FieldLoads.HoistedLengths} lengths read before a loop ({FieldLoads.Guarded} loops with reads behind a copy of their test), {FieldLoads.Reused} loads a second time, {FieldHoist.Count} fields with a number");
                }
                Step("field loads");
                // (and the null checks once more, on the final bodies: the passes since the first round made the shapes it
                // knows of checks it did not see then, a loop's second and third check of a local that does not change)
                if (Functions.FirstOrDefault(f => f.Name == "rt.nonnull") is { } nn2 && Environment.GetEnvironmentVariable("IL2WASM_NO_NULL_CHECKS") != "1")
                {
                    foreach (var f in Functions) if (!f.IsImport) { int before = f.Body.Count; NullChecks.Run(f, nn2); if (f.Body.Count != before) Peephole.Run(f); }
                    // the checks left, written out where they are (IL2WASM_NO_NONNULL_INLINE=1: as calls, for measuring)
                    if (Environment.GetEnvironmentVariable("IL2WASM_NO_NONNULL_INLINE") != "1" && Functions.FirstOrDefault(f => f.Name == "Il2Wasm.Runtime.ThrowHelpers.NullReference()") is { } thr)
                    {
                        // (in loops, and in what loops call, to two calls deep: IL2WASM_NONNULL_DEPTH)
                        var hot = NullChecks.CalledFromLoops(Functions, int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_NONNULL_DEPTH"), out var dd) ? dd : 2);
                        // (a generic family's members alike: one hot, all, so that they stay one body: ShareConstants)
                        var hotFamilies = hot.Where(f => f.Family != null).Select(f => f.Family!).ToHashSet();
                        if (hotFamilies.Count > 0) foreach (var f in Functions) if (f.Family != null && hotFamilies.Contains(f.Family)) hot.Add(f);
                        foreach (var f in Functions) if (!f.IsImport && f != nn2) NullChecks.Expand(f, nn2, thr, hot.Contains(f));
                    }
                }
            }
            else if (Environment.GetEnvironmentVariable("IL2WASM_NO_SMALL_BLOCKS") != "1")
                foreach (var f in Functions) if (!f.IsImport && SmallBlocks.Run(f)) Peephole.Run(f);
            // (the loads' field tags and the loops' sets have done their work: bodies that differ only there are the same)
            if (FieldHoist != null) { foreach (var f in Functions) if (!f.IsImport) FieldLoads.Strip(f); FieldHoist = null; }
            Step("null checks again");
            // (the counts' common case written out in code that runs often: RcFastPaths.cs)
            RcFastPaths.Run(this, UsesThreads || CountingRc);
            Step("count fast paths");
            if (Environment.GetEnvironmentVariable("IL2WASM_NO_IF_CONVERT") != "1") EachFunction(f => IfConvert.Run(f));
            Step("if convert");
            if (Environment.GetEnvironmentVariable("IL2WASM_NO_SMALL_COPIES") != "1") SmallBlocks.Variable(this);
            Step("small copies");
            if (Canon != null) FloatStores.Run(Functions, Canon);
            // (a load repeated across stores to other bytes of the same object: after the NaN tests are in)
            EachFunction(f => SameBaseLoads.Run(f));
            if (Environment.GetEnvironmentVariable("IL2WASM_NO_NORETURN") != "1") NoReturn.Run(Functions);
            // (the calls of a count's other path, outside the loop that counts: ColdExits.cs)
            EachFunction(f => ColdExits.Run(f));
            Step("float stores, no return");
            if (!s_noMerge) MergeIdenticalBodies();
            foreach (var f in Functions) ResolveForwards(f);
            if (Environment.GetEnvironmentVariable("IL2WASM_SHARE_STATS") == "1") ShareConstants.Stats(Functions, ComputeMayThrow(), PosShown, Environment.GetEnvironmentVariable("IL2WASM_SHARE_LEND") == "1");
            // (bodies that differ only in constants and in which of such bodies they call: one, the constants its last
            // parameters, ShareConstants.cs)
            if (Environment.GetEnvironmentVariable("IL2WASM_SHARE") != "0")
            {
                // (first the groups of one frame name; then, over what is left, those whose names differ: their members
                // stay frames, which would keep a group of one name from going to its shared body directly)
                int n = ShareConstants.Run(Functions, ComputeMayThrow(), PosShown, false, (name, t) => AddFunction(name, t), TableSlot);
                int n2 = Environment.GetEnvironmentVariable("IL2WASM_SHARE_LEND") == "0" ? 0 : ShareConstants.Run(Functions, ComputeMayThrow(), PosShown, true, (name, t) => AddFunction(name, t), TableSlot);
                if (Environment.GetEnvironmentVariable("IL2WASM_SHARE_STATS") == "1") Console.Error.WriteLine($"share: {n} functions now call a shared body, then {n2} lend");
            }
            if (Environment.GetEnvironmentVariable("IL2WASM_SHARE_WHY") == "1") ShareConstants.Why(Functions);
            AfterOptimize?.Invoke(Functions.Where(f => !f.IsImport));
            Step("merge, forwards");
            if (validate) Validate("after optimization");
        }
        else if (Canon != null) FloatStores.Run(Functions, Canon);

        if (optimize && Environment.GetEnvironmentVariable("IL2WASM_NO_FINAL_TIDY") != "1") EachFunction(f => { if (f.ForwardTo == null) Peephole.FinalTidy(f); });
        Step("final tidy");
        // (locals that are never live at once share one: Locals.Coalesce)
        if (optimize) EachFunction(f => { if (f.ForwardTo == null) Locals.Final(f); });
        Step("locals final");
        // (a frame is taken, checked against the stack's end and cleared by one function: Frames.cs)
        if (StackPointer != null && StackLo != null) Frames.Run(this, StackPointer, StackLo);
        Step("frames");
        // (threads: each access the engine checks against the memory's size, after the thread has learnt that size)
        if (UsesThreads) MemSync.Run(this);
        Step("memory size");
        // Which functions are live: exported, in the table, or called from a live function.
        var live = new HashSet<WasmFunction>();
        var work = new Stack<WasmFunction>();
        // (a table entry is live when live code loads its index, or data refers to it)
        foreach (var f in Functions) if (f.Exports.Count > 0 || f.TableRoot) { live.Add(f); work.Push(f); }
        var callCounts = new Dictionary<WasmFunction, int>();
        while (work.Count > 0)
        {
            var f = work.Pop();
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g)
                {
                    callCounts[g] = callCounts.GetValueOrDefault(g) + 1;
                    if (live.Add(g)) work.Push(g);
                }
                else if (ins.Op == Op.I32Const && ins.Ref is WasmFunction t && live.Add(t)) work.Push(t);
        }
        // table entries no live code can load: one trap in their place (indices stay as they are)
        if (Table.Any(f => !live.Contains(f)))
        {
            var t0 = Table.First(f => !live.Contains(f));
            var trap = new WasmFunction("table trap", t0.Type) { Order = int.MaxValue };
            trap.Body.Add(new Instr { Op = Op.Unreachable, Pos = -1 });
            Functions.Add(trap);
            live.Add(trap);
            for (int i = 0; i < Table.Count; i++)
                if (!live.Contains(Table[i])) { _deadTable.Add(i); }
            _tableTrap = trap;
        }

        var imports = Functions.Where(f => f.IsImport && live.Contains(f)).ToList();
        var defined = Functions.Where(f => !f.IsImport && live.Contains(f))
            .OrderByDescending(f => callCounts.GetValueOrDefault(f)).ThenBy(f => f.Order).ToList();
        int idx = 0;
        foreach (var f in imports) f.Index = idx++;
        foreach (var f in defined) f.Index = idx++;

        // Globals: most used first (all are defined, none imported).
        var gUse = new Dictionary<WasmGlobal, int>();
        foreach (var f in defined) foreach (var ins in f.Body)
            if (ins.Ref is WasmGlobal g) gUse[g] = gUse.GetValueOrDefault(g) + 1;
        // (section 7.2: an instance's state is its memory and its mutable globals, so every mutable global the module
        // uses is exported, for the engine to save and restore)
        foreach (var g in Globals) if (g.Mutable && g.Export == null && gUse.ContainsKey(g)) g.Export = g.Name;
        var globals = Globals.Where(g => gUse.ContainsKey(g) || g.Export != null)
            .OrderByDescending(g => gUse.GetValueOrDefault(g)).ThenBy(g => Globals.IndexOf(g)).ToList();
        for (int i = 0; i < globals.Count; i++) globals[i].Index = i;
        for (int i = 0; i < Tags.Count; i++) Tags[i].Index = i;
        for (int i = 0; i < Data.Count; i++) Data[i].Index = i;

        // Types
        var types = new List<FuncType>();
        var typeIdx = new Dictionary<FuncType, int>();
        int TypeOf(FuncType t) { if (!typeIdx.TryGetValue(t, out var i)) { i = types.Count; types.Add(t); typeIdx[t] = i; } return i; }
        foreach (var f in imports) TypeOf(f.Type);
        foreach (var f in defined) TypeOf(f.Type);
        foreach (var t in Tags) TypeOf(t.Type);
        foreach (var f in defined) foreach (var ins in f.Body)
        {
            if (ins.Op == Op.CallIndirect) TypeOf((FuncType)ins.Ref!);
            var bt = ins.Ref is TryTableInfo tti ? tti.BlockType : (ins.Op is Op.Block or Op.Loop or Op.If ? ins.Ref : null);
            if (bt is FuncType ft) TypeOf(ft);
        }

        var w = new Writer();
        w.Bytes(0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00);

        Section(w, 1, s =>
        {
            s.U32(types.Count);
            foreach (var t in types)
            {
                s.Byte(0x60); s.U32(t.Params.Length); foreach (var p in t.Params) s.Byte((byte)p);
                s.U32(t.Results.Length); foreach (var r in t.Results) s.Byte((byte)r);
            }
        });
        Section(w, 2, s =>
        {
            s.U32(imports.Count + 1);
            s.Name(MemoryModule); s.Name(MemoryName); s.Byte(0x02);
            s.Byte((byte)(MemoryShared ? 0x03 : 0x01)); s.U32(MemoryMinPages); s.U32(MemoryMaxPages);
            foreach (var f in imports) { s.Name(f.ImportModule!); s.Name(f.ImportName!); s.Byte(0x00); s.U32(TypeOf(f.Type)); }
        });
        Section(w, 3, s => { s.U32(defined.Count); foreach (var f in defined) s.U32(TypeOf(f.Type)); });
        if (Table.Count > 0)
            Section(w, 4, s => { s.U32(1); s.Byte(0x70); s.Byte(0x01); s.U32(Table.Count); s.U32(Table.Count); });
        if (Tags.Count > 0)
            Section(w, 13, s => { s.U32(Tags.Count); foreach (var t in Tags) { s.Byte(0); s.U32(TypeOf(t.Type)); } });
        if (globals.Count > 0)
            Section(w, 6, s =>
            {
                s.U32(globals.Count);
                foreach (var g in globals)
                {
                    s.Byte((byte)g.Type); s.Byte((byte)(g.Mutable ? 1 : 0));
                    switch (g.Type)
                    {
                        case ValType.I32: s.Byte(0x41); s.S32((int)g.Init); break;
                        case ValType.I64: s.Byte(0x42); s.S64(g.Init); break;
                        case ValType.F32: s.Byte(0x43); s.Bytes(BitConverter.GetBytes((int)g.Init)); break;
                        case ValType.F64: s.Byte(0x44); s.Bytes(BitConverter.GetBytes(g.Init)); break;
                    }
                    s.Byte(0x0B);
                }
            });
        var exports = new List<(string name, byte kind, int idx)>();
        foreach (var f in defined.Concat(imports)) foreach (var e in f.Exports) exports.Add((e, 0, f.Index));
        foreach (var g in globals) if (g.Export != null) exports.Add((g.Export, 3, g.Index));
        exports.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        Section(w, 7, s => { s.U32(exports.Count); foreach (var e in exports) { s.Name(e.name); s.Byte(e.kind); s.U32(e.idx); } });
        if (Table.Count > 0)
            Section(w, 9, s =>
            {
                s.U32(1); s.U32(0); s.Byte(0x41); s.S32(0); s.Byte(0x0B);
                s.U32(Table.Count);
                for (int i = 0; i < Table.Count; i++) s.U32(_deadTable.Contains(i) ? _tableTrap!.Index : Table[i].Index);
            });
        if (Data.Count > 0) Section(w, 12, s => s.U32(Data.Count));

        Step("live, indices, sections");
        // Code section: encode bodies, remembering source positions.
        var result = new Encoded { FunctionOrder = imports.Concat(defined).ToList() };
        if (Environment.GetEnvironmentVariable("IL2WASM_INDEX") is { Length: > 0 } wantIdx) foreach (var x in wantIdx.Split(",")) if (int.TryParse(x, out int ix) && ix < result.FunctionOrder.Count) Console.Error.WriteLine($"function {ix}: {result.FunctionOrder.First(f => f.Index == ix || result.FunctionOrder.IndexOf(f) == ix).Name}");
        var code = new Writer();
        var relLines = new List<LineEntry>();
        var relInlines = new List<(int, int)>();
        code.U32(defined.Count);
        foreach (var f in defined)
        {
            var body = new Writer();
            var runs = new List<(int n, ValType t)>();
            foreach (var l in f.Locals)
                if (runs.Count > 0 && runs[^1].t == l) runs[^1] = (runs[^1].n + 1, l); else runs.Add((1, l));
            body.U32(runs.Count); foreach (var (n, t) in runs) { body.U32(n); body.Byte((byte)t); }
            var bodyLines = new List<(int off, int pos)>();
            List<(int off, int scope)>? bodyInlines = null;
            int lastPos = -1;
            foreach (var ins in f.Body)
            {
                if (ins.Pos >= 0 && ins.Pos != lastPos) { bodyLines.Add((body.Length, ins.Pos)); lastPos = ins.Pos; }
                if (ins.Inl != 0 && ins.Op is Op.Call or Op.CallIndirect or Op.Throw or Op.ThrowRef) (bodyInlines ??= []).Add((body.Length, ins.Inl));
                try { EncodeInstr(body, ins, TypeOf); } catch (Exception ex) when (ex.Message.StartsWith("internal: type")) { throw new Exception(ex.Message + " (in " + f.Name + WhyLive(f) + ")"); }
            }
            body.Byte(0x0B);
            if (s_sizes != null) s_sizes.Add((body.Length, f.FrameName ?? f.Name));
            s_sizesFile?.Add($"{body.Length}\t{f.Name}");
            if (s_dump != null && (f.FrameName ?? f.Name).Contains(s_dump)) { Console.Error.WriteLine($"== {f.FrameName ?? f.Name} [{f.Name}] ({body.Length} bytes)"); foreach (var ins in f.Body) Console.Error.WriteLine("  " + ins); }
            code.U32(body.Length);
            int start = code.Length;
            foreach (var (off, pos) in bodyLines) relLines.Add(new LineEntry(start + off, pos));
            if (bodyInlines != null) foreach (var (off, scope) in bodyInlines) relInlines.Add((start + off, scope));
            code.Bytes(body.ToArray());
        }
        if (s_sizesFile != null) { System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("IL2WASM_SIZES_FILE")!, s_sizesFile); s_sizesFile.Clear(); }
        if (s_sizes != null)
        {
            Console.Error.WriteLine($"code {code.Length} bytes in {defined.Count} functions; largest:");
            foreach (var (n, name) in s_sizes.OrderByDescending(x => x.Item1).Take(40)) Console.Error.WriteLine($"  {n,7} {(name.Length > 110 ? name[..110] : name)}");
        }
        w.Byte(10); w.U32(code.Length);
        int codeStart = w.Length;
        w.Bytes(code.ToArray());
        Step("code");
        foreach (var l in relLines) result.Lines.Add(new LineEntry(codeStart + l.Offset, l.Pos));
        foreach (var (off, scope) in relInlines) result.Inlines.Add((codeStart + off, scope));

        if (Data.Count > 0)
            Section(w, 11, s =>
            {
                s.U32(Data.Count);
                foreach (var d in Data) { s.U32(1); s.U32(d.Bytes.Count); s.Bytes(d.Bytes.ToArray()); }
            });

        // name section
        var names = new Writer();
        names.Name("name");
        var fnNames = new Writer();
        var all = imports.Concat(defined).ToList();
        // the program's own functions, by their .NET frame names (the library's frame names are in il2wasm.frames)
        // (IL2WASM_NAMES=1: every function, by its compiler name, for traps in library code)
        bool allNames = Environment.GetEnvironmentVariable("IL2WASM_NAMES") == "1";
        var named = all.Where(f => allNames || f.ProgramCode).ToList();
        fnNames.U32(named.Count);
        foreach (var f in named) { fnNames.U32(f.Index); fnNames.Name(allNames && !f.ProgramCode ? f.Name : f.FrameName ?? f.Name); }
        names.Byte(1); names.U32(fnNames.Length); names.Bytes(fnNames.ToArray());
        if (globals.Count > 0)
        {
            var gn = new Writer();
            gn.U32(globals.Count);
            foreach (var g in globals) { gn.U32(g.Index); gn.Name(g.Name); }
            names.Byte(7); names.U32(gn.Length); names.Bytes(gn.ToArray());
        }
        if (Tags.Count > 0)
        {
            var tn = new Writer();
            tn.U32(Tags.Count);
            foreach (var t in Tags) { tn.U32(t.Index); tn.Name(t.Name); }
            names.Byte(11); names.U32(tn.Length); names.Bytes(tn.ToArray());
        }
        w.Byte(0); w.U32(names.Length); w.Bytes(names.ToArray());

        foreach (var cs in CustomSections)
        {
            var s = new Writer();
            s.Name(cs.Name); s.Bytes(cs.Data);
            w.Byte(0); w.U32(s.Length); w.Bytes(s.ToArray());
        }
        result.Bytes = w.ToArray();
        return result;
    }

    static void ResolveForwards(WasmFunction f)
    {
        var body = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(f.Body);
        for (int i = 0; i < body.Length; i++)
            if (body[i].Op == Op.Call && body[i].Ref is WasmFunction g)
            {
                int guard = 0;
                long hint = body[i].Imm2;
                while (g.ForwardTo != null && guard++ < 64)
                {
                    // (a forwarder whose own call names the class of the object it releases, RuntimeGen.ReleaseOfField:
                    // the call that takes its place names it too)
                    int np = g.Type.Params.Length;
                    if (hint == 0 && g.Body.Count > np && g.Body[np].Op == Op.Call && ReferenceEquals(g.Body[np].Ref, g.ForwardTo)) hint = g.Body[np].Imm2;
                    g = g.ForwardTo;
                }
                body[i].Ref = g;
                body[i].Imm2 = hint;
            }
    }

    void MergeIdenticalBodies()
    {
        for (int round = 0; round < 4; round++)
        {
            // Two functions merge when their keys (MergeKey: BodyKey, with the frame's name and lines for a frame) are
            // equal. The keys of every function were made as text each round (hundreds of megabytes): now each key's
            // characters are hashed as they would be written, and the text is made only for functions whose hash
            // another's matches, to compare them; the first function with a key is the one the others forward to.
            var seen = new Dictionary<(ulong, ulong), List<(WasmFunction fn, string? key)>>();
            bool changed = false;
            // (a function that is a frame of a trace, and can be on the stack when one is taken, keeps its own name
            // and lines: two methods with the same code are two frames in .NET)
            var may = ComputeMayThrow();
            var hasher = new KeyHash();
            foreach (var f in Functions)
            {
                if (f.IsImport || f.ForwardTo != null || f.Exports.Count > 0 || f.InTable) continue;
                hasher.Reset();
                MergeKey(f, may, hasher);
                var h = hasher.Value;
                if (!seen.TryGetValue(h, out var bucket)) { seen[h] = [(f, null)]; continue; }
                var key = MergeKeyText(f, may);
                WasmFunction? other = null;
                for (int i = 0; i < bucket.Count; i++)
                {
                    var (cf, ck) = bucket[i];
                    if (ck == null) bucket[i] = (cf, ck = MergeKeyText(cf, may));
                    if (ck == key) { other = cf; break; }
                }
                if (other != null) { FieldHoist?.Merged(other, f); f.ForwardTo = other; changed = true; }
                else bucket.Add((f, key));
            }
            Step($"  merge round {round}{(changed ? "" : ", nothing merged")}");
            if (!changed) break;
            foreach (var f in Functions) ResolveForwards(f);
        }
    }

    /// <summary>A function that only passes its own arguments, in order, to another of the same type and returns what
    /// that returns, and is no frame of a trace: every call of it is a call of the other.</summary>
    void ForwardPureForwarders()
    {
        foreach (var f in Functions)
        {
            if (f.IsImport || f.ForwardTo != null || f.FrameName != null || f.Exports.Count > 0 || f.InTable || f.TableRoot) continue;
            int np = f.Type.Params.Length;
            var b = f.Body;
            if (b.Count != np + 1 && !(b.Count == np + 2 && b[^1].Op == Op.Return)) continue;
            if (b[np].Op != Op.Call || b[np].Ref is not WasmFunction g || g == f || g.ForwardTo == f) continue;
            if (!g.Type.Params.AsSpan().SequenceEqual(f.Type.Params) || !g.Type.Results.AsSpan().SequenceEqual(f.Type.Results)) continue;
            bool inOrder = true;
            for (int i = 0; i < np && inOrder; i++) inOrder = b[i].Op == Op.LocalGet && b[i].Imm == i;
            if (inOrder) f.ForwardTo = g;
        }
    }

    /// <summary>The release function, and for a type's id the destruction of its objects when that only frees them
    /// (null otherwise): set by the compiler for programs without threads.</summary>
    public WasmFunction? ReleaseFn;
    public Func<int, WasmFunction?>? LeafDestroy;
    /// <summary>For a type's id the destruction of its objects when it is flat and not a leaf's (it lets go of
    /// leaves, then frees the object: RuntimeGen.IsFlatDestroy), null otherwise.</summary>
    public Func<int, WasmFunction?>? FlatDestroy;
    /// <summary>Rt.FreeObject, and Rt.FreeFixed where a free of a known small size may be written as it (a program
    /// without threads, not checked, no share check), else null.</summary>
    public WasmFunction? FreeFn, FreeFixedFn;
    /// <summary>IL2WASM_NO_DESTROY_IN_PLACE=1: in a loop a release that names its class calls the class's destruction
    /// (a function of a few instructions of its own), as before; a flat class's goes through rt.release.</summary>
    /// <summary>A flat class's destruction is written in place outside loops too in a function of no more than this
    /// many instructions (IL2WASM_IN_PLACE_SMALL=n, 0 for none): an accessor the engine puts into its callers' loops,
    /// List&lt;T&gt;'s indexer compiled for a sealed class above all (tools/alloc-parts.cs through a List: made over old
    /// ones 600 ns to 400, let go 700 to 570-600; modules 0.3% larger).</summary>
    static readonly int s_inPlaceSmall = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_IN_PLACE_SMALL"), out var ips) ? ips : 60;
    static readonly bool s_noDestroyInPlace = Environment.GetEnvironmentVariable("IL2WASM_NO_DESTROY_IN_PLACE") == "1";
    /// <summary>IL2WASM_DESTROY_IN_PLACE_BREAK=1 (test 235's proof that the copy written in place is the destruction):
    /// the copy leaves out the releases of the object's fields.</summary>
    static readonly bool s_destroyInPlaceBreak = Environment.GetEnvironmentVariable("IL2WASM_DESTROY_IN_PLACE_BREAK") == "1";

    /// <summary>A release whose call says what class the object is of exactly (FunctionCompiler.ReleaseOf), where
    /// destroying such an object only frees it: the count's test and the class's own destruction, in place of the
    /// call that would find the class by the object's type. A count below zero is an object that is never freed.</summary>
    static readonly bool s_countRc = Environment.GetEnvironmentVariable("IL2WASM_COUNT_RC") == "1";

    HashSet<WasmFunction> ExpandLeafReleases()
    {
        var changed = new HashSet<WasmFunction>();
        if (ReleaseFn == null || LeafDestroy == null) return changed;
        var destroyOf = new Dictionary<long, WasmFunction?>();
        WasmFunction? DestroyOf(long hint)
        {
            if (!destroyOf.TryGetValue(hint, out var d)) destroyOf[hint] = d = LeafDestroy((int)hint - 1);
            return d;
        }
        // A flat class (not a leaf): only in a loop, and only with its destruction written out in place. A class's
        // own destruction is a function of a few instructions with few callers. Where the engine has compiled the
        // loop before the first object died there (no call counted: it inlines nothing), every free is a call of
        // it, and the engine's baseline code runs such a function for its first several hundred thousand calls (its
        // budget for the optimizing tier is counted in the function's size): slower than the shared rt.release and
        // rt.destroy_now, which every release of the program has made hot (tools/colstr-parts.cs "(100 objects
        // alone)": 568 ns to 757 with the call, tools/alloc-parts.cs "made over old ones" the same; written out:
        // see docs/AUDIT.md). Written out, what is left to call is Rt.FreeObject, shared and hot as well.
        var flatOf = new Dictionary<long, WasmFunction?>();
        WasmFunction? FlatOf(long hint)
        {
            if (s_noDestroyInPlace || FlatDestroy == null) return null;
            if (!flatOf.TryGetValue(hint, out var d)) flatOf[hint] = d = FlatDestroy((int)hint - 1);
            return d;
        }
        // (the body of a destruction that can be written out where it is called: straight-line code over its one
        // parameter, a few instructions)
        var inPlace = new Dictionary<WasmFunction, bool>();
        bool InPlace(WasmFunction destroy)
        {
            if (s_noDestroyInPlace) return false;
            if (inPlace.TryGetValue(destroy, out bool ok)) return ok;
            ok = destroy.Type.Params.Length == 1 && destroy.Type.Results.Length == 0 && destroy.Locals.Count == 0 && destroy.Body.Count <= 40;
            foreach (var x in destroy.Body)
            {
                if (!ok) break;
                ok = x.Op switch
                {
                    Op.LocalGet => x.Imm == 0,
                    Op.I32Load or Op.I32Const or Op.I32Add or Op.I32Mul or Op.I32Shl or Op.Call or Op.Drop or Op.Nop => true,
                    _ => false,
                };
            }
            return inPlace[destroy] = ok;
        }
        // (in a loop the test is written out; elsewhere it is a call of the one function that does it for the type,
        // no larger than the call it replaces)
        var releaseOf = new Dictionary<WasmFunction, WasmFunction>();
        var leafOfRelease = new Dictionary<WasmFunction, WasmFunction>();
        void Sequence(List<Instr> o, WasmFunction destroy, int t, int c, int pos, bool inLoop, int t2, int c2)
        {
            void Add(Op op, long imm = 0, object? r = null) => o.Add(new Instr { Op = op, Imm = imm, Imm2 = op is Op.I32Load or Op.I32Store ? 2 : 0, Ref = r, Pos = pos });
            Add(Op.LocalTee, t);
            Add(Op.If);
            Add(Op.LocalGet, t); Add(Op.I32Load); Add(Op.LocalTee, c); Add(Op.I32Const, 1); Add(Op.I32GtS);
            Add(Op.If);
            Add(Op.LocalGet, t); Add(Op.LocalGet, c); Add(Op.I32Const, 1); Add(Op.I32Sub); Add(Op.I32Store);
            Add(Op.Else);
            Add(Op.LocalGet, c); Add(Op.I32Const, 1); Add(Op.I32Eq);
            Add(Op.If);
            Add(Op.LocalGet, t); Add(Op.I32Const, 0); Add(Op.I32Store);
            if (inLoop && InPlace(destroy))
            {
                // (the destruction itself: its parameter is t. A release in it that names a leaf's class is written
                // out in its turn, one level, with the second pair of locals: a string field that is shared or a
                // literal costs a test, no call. The free of a known small size is Rt.FreeFixed, which the inliner
                // puts here: a few instructions, the block kept aside for the next allocation of its size)
                var b = destroy.Body;
                for (int i = 0; i < b.Count; i++)
                {
                    var x = b[i];
                    if (x.Op == Op.Nop) continue;
                    if (x.Op == Op.LocalGet) { o.Add(x with { Imm = t }); continue; }
                    if (x.Op == Op.I32Const && FreeFixedFn is { Body.Count: > 0 } && i + 1 < b.Count && b[i + 1].Op == Op.Call && ReferenceEquals(b[i + 1].Ref, FreeFn)
                        && i > 0 && b[i - 1].Op == Op.LocalGet && ((x.Imm + 3) & ~3L) is var fs && fs <= 128 && fs >= 8)
                    {
                        o.Add(x with { Imm = fs });
                        o.Add(b[i + 1] with { Ref = FreeFixedFn });
                        i++;
                        continue;
                    }
                    if (s_destroyInPlaceBreak && x.Op == Op.Call && t != t2 && (x.Imm2 != 0 && ReferenceEquals(x.Ref, ReleaseFn) || x.Ref is WasmFunction bw && leafOfRelease.ContainsKey(bw))) { o.Add(x with { Op = Op.Drop, Imm2 = 0, Ref = null }); continue; }
                    if (x.Op == Op.Call && x.Imm2 != 0 && ReferenceEquals(x.Ref, ReleaseFn) && DestroyOf(x.Imm2) is { } leaf)
                    {
                        if (t2 >= 0 && t != t2) Sequence(o, leaf, t2, c2, pos, true, -1, -1);
                        else o.Add(x with { Imm2 = 0, Ref = ReleaseOf(leaf) });
                        continue;
                    }
                    // (the same release where this pass has been over the destruction already)
                    if (x.Op == Op.Call && x.Ref is WasmFunction w && leafOfRelease.TryGetValue(w, out var leaf2) && t2 >= 0 && t != t2) { Sequence(o, leaf2, t2, c2, pos, true, -1, -1); continue; }
                    o.Add(x);
                }
            }
            else { Add(Op.LocalGet, t); Add(Op.Call, 0, destroy); }
            Add(Op.End);
            Add(Op.End);
            Add(Op.End);
        }
        WasmFunction ReleaseOf(WasmFunction destroy)
        {
            if (releaseOf.TryGetValue(destroy, out var r)) return r;
            r = AddFunction("rt.release of " + destroy.Name, new FuncType([ValType.I32], []));
            r.Locals.Add(ValType.I32);
            // (its own code stores a count, no field: what its call can store is what the destruction it calls can)
            r.FieldStoreSet = Wasm.FieldHoist.None;
            r.Body.Add(new Instr { Op = Op.LocalGet, Imm = 0, Pos = -1 });
            Sequence(r.Body, destroy, 0, 1, -1, false, -1, -1);
            leafOfRelease[r] = destroy;
            return releaseOf[destroy] = r;
        }
        foreach (var f in Functions.ToList())
        {
            if (f.IsImport) continue;
            bool any = false;
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Imm2 != 0 && ReferenceEquals(ins.Ref, ReleaseFn) && (DestroyOf(ins.Imm2) != null || FlatOf(ins.Imm2) != null)) { any = true; break; }
            if (!any) continue;
            int np = f.Type.Params.Length;
            int t = -1, c = -1, t2 = -1, c2 = -1;
            var o = new List<Instr>(f.Body.Count + 32);
            var open = new List<bool>();
            int loops = 0;
            foreach (var ins in f.Body)
            {
                switch (ins.Op)
                {
                    case Op.Loop: open.Add(true); loops++; break;
                    case Op.Block or Op.If or Op.TryTable: open.Add(false); break;
                    case Op.End when open.Count > 0: if (open[^1]) loops--; open.RemoveAt(open.Count - 1); break;
                }
                if (ins.Op != Op.Call || ins.Imm2 == 0 || !ReferenceEquals(ins.Ref, ReleaseFn)) { o.Add(ins); continue; }
                var destroy = DestroyOf(ins.Imm2);
                if (destroy == null)
                {
                    // (a flat class: in a loop or a small function, written out; elsewhere the release it was)
                    if (loops == 0 && f.Body.Count > s_inPlaceSmall || s_countRc || FlatOf(ins.Imm2) is not { } flat || !InPlace(flat)) { o.Add(ins); continue; }
                    destroy = flat;
                }
                // (IL2WASM_COUNT_RC: a call in loops too, where the site counters see it)
                else if (loops == 0 || s_countRc) { o.Add(ins with { Imm2 = 0, Ref = ReleaseOf(destroy) }); continue; }
                if (t < 0)
                {
                    f.Locals.Add(ValType.I32); t = np + f.Locals.Count - 1;
                    f.Locals.Add(ValType.I32); c = np + f.Locals.Count - 1;
                }
                if (t2 < 0 && InPlace(destroy) && destroy.Body.Any(x => x.Op == Op.Call && !ReferenceEquals(x.Ref, FreeFn)))
                {
                    f.Locals.Add(ValType.I32); t2 = np + f.Locals.Count - 1;
                    f.Locals.Add(ValType.I32); c2 = np + f.Locals.Count - 1;
                }
                Sequence(o, destroy, t, c, ins.Pos, true, t2, c2);
                continue;
            }
            f.Body = o;
            changed.Add(f);
        }
        // (a class's name on a release has no use from here on: without it the bodies that differ by nothing else are
        // the same again for the merge; IL2WASM_NO_HINT_CLEAR=1: left on)
        if (Environment.GetEnvironmentVariable("IL2WASM_NO_HINT_CLEAR") == "1") return changed;
        foreach (var f in Functions)
        {
            if (f.IsImport) continue;
            var b = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(f.Body);
            for (int i = 0; i < b.Length; i++)
                if (b[i].Op == Op.Call && b[i].Imm2 != 0 && ReferenceEquals(b[i].Ref, ReleaseFn)) b[i].Imm2 = 0;
        }
        return changed;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> s_ids = new();
    static int s_next;
    static string Id(object o) => s_ids.GetValue(o, _ => Interlocked.Increment(ref s_next).ToString());

    /// <summary>Where a key's characters go: text (StringBuilder's own way of writing each value), or a hash of the
    /// same characters (KeyHash).</summary>
    internal abstract class KeyOut
    {
        public abstract KeyOut Append(char c);
        public abstract KeyOut Append(string? s);
        public abstract KeyOut Append(long v);
        public KeyOut Append(int v) => Append((long)v);
    }

    sealed class KeyText : KeyOut
    {
        public readonly StringBuilder Sb = new();
        public override KeyOut Append(char c) { Sb.Append(c); return this; }
        public override KeyOut Append(string? s) { Sb.Append(s); return this; }
        public override KeyOut Append(long v) { Sb.Append(v); return this; }
    }

    /// <summary>Two 64-bit hashes of the characters a key's text would have.</summary>
    sealed class KeyHash : KeyOut
    {
        ulong _a, _b;
        public void Reset() { _a = 14695981039346656037UL; _b = 0x9E3779B97F4A7C15UL; }
        public (ulong, ulong) Value => (_a, _b);
        public override KeyOut Append(char c)
        {
            _a = (_a ^ c) * 1099511628211UL;
            _b = (_b + c) * 0xC2B2AE3D27D4EB4FUL; _b ^= _b >> 29;
            return this;
        }
        public override KeyOut Append(string? s) { if (s != null) foreach (var c in s) Append(c); return this; }
        public override KeyOut Append(long v)
        {
            // (the characters StringBuilder.Append(long) writes: the value's own formatting, no culture asked for)
            Span<char> buf = stackalloc char[24];
            v.TryFormat(buf, out int n, default, null);
            for (int i = 0; i < n; i++) Append(buf[i]);
            return this;
        }
    }

    /// <summary>MergeIdenticalBodies' key: BodyKey, and for a function that is a frame of a trace (it may throw) its
    /// frame name and lines.</summary>
    static void MergeKey(WasmFunction f, HashSet<WasmFunction> may, KeyOut sb)
    {
        BodyKey(f, sb);
        if (f.FrameName != null && may.Contains(f))
        {
            sb.Append('#').Append(f.FrameName);
            foreach (var ins in f.Body) if (ins.Pos >= 0) sb.Append('@').Append(ins.Pos);
        }
    }

    static string MergeKeyText(WasmFunction f, HashSet<WasmFunction> may)
    {
        var t = new KeyText();
        MergeKey(f, may, t);
        return t.Sb.ToString();
    }

    internal static string BodyKey(WasmFunction f)
    {
        var t = new KeyText();
        BodyKey(f, t);
        return t.Sb.ToString();
    }

    static void BodyKey(WasmFunction f, KeyOut sb)
    {
        foreach (var p in f.Type.Params) sb.Append((char)p);
        sb.Append('|'); foreach (var r in f.Type.Results) sb.Append((char)r);
        sb.Append('|'); foreach (var l in f.Locals) sb.Append((char)l);
        foreach (var ins in f.Body)
        {
            // (a loop's Imm2 is the set of fields stored in it, a load's FieldTag the field it reads: FieldLoads.cs.
            // Neither is code: two bodies that differ only there are made one, FieldHoist.Merged)
            sb.Append(';').Append((int)ins.Op).Append(',').Append(ins.Imm).Append(',').Append(ins.Op == Op.Loop ? 0 : ins.Imm2);
            // (two bodies that differ in what was inlined where show different frames in a trace)
            if (ins.Inl != 0) sb.Append('@').Append(ins.Inl);
            switch (ins.Ref)
            {
                case null: case FieldTag: break;
                // (a function not in the module has no order of its own: a placeholder for a type's counts,
                // TypedCounts.cs, which becomes the atomic or the plain function later, by its type)
                case WasmFunction g: sb.Append(",f").Append(g.Order); if (g.Order == 0) sb.Append(':').Append(g.Name); break;
                case ValType vt: sb.Append(",v").Append((int)vt); break;
                case FuncType ft: sb.Append(",t").Append(string.Join(".", ft.Params)).Append('>').Append(string.Join(".", ft.Results)); break;
                case int[] arr: sb.Append(",b").Append(string.Join(".", arr)); break;
                case TryTableInfo tti:
                    sb.Append(",tt").Append(tti.BlockType?.ToString());
                    foreach (var c in tti.Catches) sb.Append(':').Append(c.Kind).Append(c.Tag?.Name).Append(c.Label);
                    break;
                case IAddress a: sb.Append(",a").Append(Id(a)); break;
                default: sb.Append(",o").Append(Id(ins.Ref)); break;
            }
        }
    }

    void EncodeInstr(Writer b, Instr ins, Func<FuncType, int> typeOf)
    {
        var op = ins.Op;
        if ((int)op > 0xFF) { b.Byte((byte)((int)op >> 8)); b.U32((int)op & 0xFF); }
        else b.Byte((byte)op);
        switch (op)
        {
            case Op.Block: case Op.Loop: case Op.If: BlockType(b, ins.Ref, typeOf); break;
            case Op.TryTable:
                {
                    var tti = (TryTableInfo)ins.Ref!;
                    BlockType(b, tti.BlockType, typeOf);
                    b.U32(tti.Catches.Count);
                    foreach (var c in tti.Catches)
                    {
                        b.Byte(c.Kind);
                        if (c.Kind < 2) b.U32(c.Tag!.Index);
                        b.U32(c.Label);
                    }
                    break;
                }
            case Op.Throw: b.U32(((WasmTag)ins.Ref!).Index); break;
            case Op.Br: case Op.BrIf: b.U32((int)ins.Imm); break;
            case Op.BrTable:
                {
                    var t = (int[])ins.Ref!;
                    b.U32(t.Length); foreach (var x in t) b.U32(x); b.U32((int)ins.Imm);
                    break;
                }
            case Op.Call: b.U32(((WasmFunction)ins.Ref!).Index); break;
            case Op.CallIndirect: b.U32(typeOf((FuncType)ins.Ref!)); b.U32(0); break;
            case Op.LocalGet: case Op.LocalSet: case Op.LocalTee: b.U32((int)ins.Imm); break;
            case Op.GlobalGet: case Op.GlobalSet: b.U32(((WasmGlobal)ins.Ref!).Index); break;
            case >= Op.I32Load and <= Op.I64Store32: b.U32((int)ins.Imm2); b.U32((int)ins.Imm); break;
            case Op.AtomicFence: b.Byte(0); break;
            case Op.V128Load: case Op.V128Store: b.U32(0); b.U32((int)ins.Imm); break;
            case Op.F64x2ExtractLane: b.Byte((byte)ins.Imm); break;
            case >= Op.MemoryAtomicNotify and <= Op.I32AtomicRmw16CmpxchgU: b.U32((int)ins.Imm2); b.U32((int)ins.Imm); break;
            case Op.MemorySize: case Op.MemoryGrow: b.Byte(0); break;
            case Op.I32Const:
                b.S32(ins.Ref is IAddress a ? a.Address + (int)ins.Imm : ins.Ref is TypeIdRef tr ? TypeIdMap!(tr.Id) : (int)ins.Imm); break;
            case Op.I64Const: b.S64(ins.Imm); break;
            case Op.F32Const: b.Bytes(BitConverter.GetBytes((int)ins.Imm)); break;
            case Op.F64Const: b.Bytes(BitConverter.GetBytes(ins.Imm)); break;
            case Op.MemoryInit: b.U32(((DataSegment)ins.Ref!).Index); b.Byte(0); break;
            case Op.DataDrop: b.U32(((DataSegment)ins.Ref!).Index); break;
            case Op.MemoryCopy: b.Byte(0); b.Byte(0); break;
            case Op.MemoryFill: b.Byte(0); break;
        }
    }

    static void BlockType(Writer b, object? bt, Func<FuncType, int> typeOf)
    {
        switch (bt)
        {
            case null: b.Byte(0x40); break;
            case ValType v: b.Byte((byte)v); break;
            case FuncType ft: b.S64(typeOf(ft)); break;
            default: throw new InvalidOperationException("bad block type");
        }
    }

    static void Section(Writer w, byte id, Action<Writer> body)
    {
        var s = new Writer(); body(s);
        w.Byte(id); w.U32(s.Length); w.Bytes(s.ToArray());
    }
}

internal sealed class Writer
{
    readonly MemoryStream _ms = new();
    public int Length => (int)_ms.Length;
    public void Byte(byte b) => _ms.WriteByte(b);
    public void Bytes(params byte[] b) => _ms.Write(b);
    public void U32(int v)
    {
        uint u = (uint)v;
        do { byte x = (byte)(u & 0x7F); u >>= 7; if (u != 0) x |= 0x80; Byte(x); } while (u != 0);
    }
    public void S32(int v) => S64(v);
    public void S64(long v)
    {
        while (true)
        {
            byte x = (byte)(v & 0x7F); v >>= 7;
            bool done = (v == 0 && (x & 0x40) == 0) || (v == -1 && (x & 0x40) != 0);
            if (!done) x |= 0x80;
            Byte(x);
            if (done) break;
        }
    }
    public void Name(string s) { var b = Encoding.UTF8.GetBytes(s); U32(b.Length); Bytes(b); }
    public byte[] ToArray() => _ms.ToArray();
}
