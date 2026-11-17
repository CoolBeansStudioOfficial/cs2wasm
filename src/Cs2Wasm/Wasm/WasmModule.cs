using System.Text;

namespace Cs2Wasm.Wasm;

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

    static readonly List<(int, string)>? s_sizes = Environment.GetEnvironmentVariable("CS2WASM_SIZES") == "1" ? [] : null;
    static readonly string? s_dump = Environment.GetEnvironmentVariable("CS2WASM_DUMP") is { Length: > 0 } d ? d : null;
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

    /// <summary>CS2WASM_VALIDATE=1: every function type-checked (Validator), problems to stderr.</summary>
    void Validate(string when)
    {
        int n = 0;
        foreach (var f in Functions)
        {
            if (f.IsImport || f.ForwardTo != null) continue;
            if (Validator.Check(f, Globals) is { } problem && n++ < 60) Console.Error.WriteLine($"invalid ({when}): {problem}");
        }
        if (n > 0) Console.Error.WriteLine($"{n} invalid functions ({when})");
    }

    public sealed class Encoded
    {
        public byte[] Bytes = [];
        public List<LineEntry> Lines = [];
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

    /// <summary>Runs on the optimized bodies before they are encoded (measurement: CS2WASM_COUNT_RC's site counters).</summary>
    public Action<IEnumerable<WasmFunction>>? AfterOptimize;
    /// <summary>The helper that gives a float NaN the bits .NET gives it (double: true), for FloatStores.cs.</summary>
    public Func<bool, WasmFunction>? Canon;
    public WasmGlobal? StackPointer;
    /// <summary>CS2WASM_NO_MERGE=1: identical bodies stay apart (measurement: each site keeps its own name).</summary>
    static readonly bool s_noLoops = Environment.GetEnvironmentVariable("CS2WASM_NO_LOOPS") == "1";
    static readonly bool s_noMerge = Environment.GetEnvironmentVariable("CS2WASM_NO_MERGE") == "1";

    public Encoded Encode(bool optimize)
    {
        foreach (var f in Functions) ResolveForwards(f);
        bool validate = Environment.GetEnvironmentVariable("CS2WASM_VALIDATE") == "1";
        if (validate) Validate("before optimization");
        if (optimize)
        {
            ForwardPureForwarders();
            // (identical bodies first: a merged function's call sites count as its callers)
            if (!s_noMerge) MergeIdenticalBodies();
            foreach (var f in Functions) ResolveForwards(f);
            Inliner.Run(Functions, ComputeMayThrow());
            foreach (var f in Functions) if (!f.IsImport) Peephole.Run(f);
            if (Functions.FirstOrDefault(f => f.Name == "rt.nonnull") is { } nonNull && Environment.GetEnvironmentVariable("CS2WASM_NO_NULL_CHECKS") != "1")
                foreach (var f in Functions) if (!f.IsImport) NullChecks.Run(f, nonNull);
            if (Environment.GetEnvironmentVariable("CS2WASM_NO_LOCALS") != "1")
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
                        // (the index checks a loop's own condition has made: on the tidied body, where they are told)
                        if (!s_noLoops && Loops.Run(f, loopNonNull)) { Peephole.Run(f); Locals.Run(f); }
                    }
                }
                finally { Locals.MayThrow = null; }
                if (Environment.GetEnvironmentVariable("CS2WASM_NO_FRESH") != "1")
                    Fresh.Run(Functions, Functions.FirstOrDefault(f => f.Name.StartsWith("Cs2Wasm.Runtime.Rt.AllocObject(")));
                // Once more: a getter is a load and a return only after the passes above took its local traffic out, and
                // small enough to inline only then. The functions it went into are cleaned again.
                var mayThrow = ComputeMayThrow();
                var leafExpanded = ExpandLeafReleases();
                var again = Inliner.Run(Functions, mayThrow, tiny: true);
                again.UnionWith(leafExpanded);
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
                // the copies and fills of constant size that are left become loads and stores
                if (Environment.GetEnvironmentVariable("CS2WASM_NO_SMALL_BLOCKS") != "1")
                {
                    Locals.MayThrow = mayThrow;
                    try { foreach (var f in Functions) if (!f.IsImport && SmallBlocks.Run(f)) { Peephole.Run(f); Locals.Run(f); } }
                    finally { Locals.MayThrow = null; }
                }
                // the loads a loop repeats (on the final bodies: the structs are in locals, the small helpers inlined)
                if (Environment.GetEnvironmentVariable("CS2WASM_NO_LOOP_LOADS") != "1")
                {
                    Locals.MayThrow = mayThrow;
                    try { foreach (var f in Functions) if (!f.IsImport && LoopLoads.Run(f)) { Peephole.Run(f); Locals.Run(f); } }
                    finally { Locals.MayThrow = null; }
                }
            }
            else if (Environment.GetEnvironmentVariable("CS2WASM_NO_SMALL_BLOCKS") != "1")
                foreach (var f in Functions) if (!f.IsImport && SmallBlocks.Run(f)) Peephole.Run(f);
            if (Environment.GetEnvironmentVariable("CS2WASM_NO_SMALL_COPIES") != "1") SmallBlocks.Variable(this);
            if (Canon != null) FloatStores.Run(Functions, Canon);
            if (Environment.GetEnvironmentVariable("CS2WASM_NO_NORETURN") != "1") NoReturn.Run(Functions);
            if (!s_noMerge) MergeIdenticalBodies();
            foreach (var f in Functions) ResolveForwards(f);
            AfterOptimize?.Invoke(Functions.Where(f => !f.IsImport));
            if (validate) Validate("after optimization");
        }
        else if (Canon != null) FloatStores.Run(Functions, Canon);

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

        // Code section: encode bodies, remembering source positions.
        var result = new Encoded { FunctionOrder = imports.Concat(defined).ToList() };
        if (Environment.GetEnvironmentVariable("CS2WASM_INDEX") is { Length: > 0 } wantIdx) foreach (var x in wantIdx.Split(",")) if (int.TryParse(x, out int ix) && ix < result.FunctionOrder.Count) Console.Error.WriteLine($"function {ix}: {result.FunctionOrder.First(f => f.Index == ix || result.FunctionOrder.IndexOf(f) == ix).Name}");
        var code = new Writer();
        var relLines = new List<LineEntry>();
        code.U32(defined.Count);
        foreach (var f in defined)
        {
            var body = new Writer();
            var runs = new List<(int n, ValType t)>();
            foreach (var l in f.Locals)
                if (runs.Count > 0 && runs[^1].t == l) runs[^1] = (runs[^1].n + 1, l); else runs.Add((1, l));
            body.U32(runs.Count); foreach (var (n, t) in runs) { body.U32(n); body.Byte((byte)t); }
            var bodyLines = new List<(int off, int pos)>();
            int lastPos = -1;
            foreach (var ins in f.Body)
            {
                if (ins.Pos >= 0 && ins.Pos != lastPos) { bodyLines.Add((body.Length, ins.Pos)); lastPos = ins.Pos; }
                try { EncodeInstr(body, ins, TypeOf); } catch (Exception ex) when (ex.Message.StartsWith("internal: type")) { throw new Exception(ex.Message + " (in " + f.Name + WhyLive(f) + ")"); }
            }
            body.Byte(0x0B);
            if (s_sizes != null) s_sizes.Add((body.Length, f.FrameName ?? f.Name));
            if (s_dump != null && (f.FrameName ?? f.Name).Contains(s_dump)) { Console.Error.WriteLine($"== {f.FrameName ?? f.Name} ({body.Length} bytes)"); foreach (var ins in f.Body) Console.Error.WriteLine("  " + ins); }
            code.U32(body.Length);
            int start = code.Length;
            foreach (var (off, pos) in bodyLines) relLines.Add(new LineEntry(start + off, pos));
            code.Bytes(body.ToArray());
        }
        if (s_sizes != null)
        {
            Console.Error.WriteLine($"code {code.Length} bytes in {defined.Count} functions; largest:");
            foreach (var (n, name) in s_sizes.OrderByDescending(x => x.Item1).Take(40)) Console.Error.WriteLine($"  {n,7} {(name.Length > 110 ? name[..110] : name)}");
        }
        w.Byte(10); w.U32(code.Length);
        int codeStart = w.Length;
        w.Bytes(code.ToArray());
        foreach (var l in relLines) result.Lines.Add(new LineEntry(codeStart + l.Offset, l.Pos));

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
        // the program's own functions, by their .NET frame names (the library's frame names are in cs2wasm.frames)
        // (CS2WASM_NAMES=1: every function, by its compiler name, for traps in library code)
        bool allNames = Environment.GetEnvironmentVariable("CS2WASM_NAMES") == "1";
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
                while (g.ForwardTo != null && guard++ < 64) g = g.ForwardTo;
                body[i].Ref = g;
            }
    }

    void MergeIdenticalBodies()
    {
        for (int round = 0; round < 4; round++)
        {
            var seen = new Dictionary<string, WasmFunction>();
            bool changed = false;
            foreach (var f in Functions)
            {
                if (f.IsImport || f.ForwardTo != null || f.Exports.Count > 0 || f.InTable) continue;
                var key = BodyKey(f);
                if (seen.TryGetValue(key, out var other)) { f.ForwardTo = other; changed = true; }
                else seen[key] = f;
            }
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

    /// <summary>A release whose call says what class the object is of exactly (FunctionCompiler.ReleaseOf), where
    /// destroying such an object only frees it: the count's test and the class's own destruction, in place of the
    /// call that would find the class by the object's type. A count below zero is an object that is never freed.</summary>
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
        // (in a loop the test is written out; elsewhere it is a call of the one function that does it for the type,
        // no larger than the call it replaces)
        var releaseOf = new Dictionary<WasmFunction, WasmFunction>();
        void Sequence(List<Instr> o, WasmFunction destroy, int t, int c, int pos)
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
            Add(Op.LocalGet, t); Add(Op.Call, 0, destroy);
            Add(Op.End);
            Add(Op.End);
            Add(Op.End);
        }
        WasmFunction ReleaseOf(WasmFunction destroy)
        {
            if (releaseOf.TryGetValue(destroy, out var r)) return r;
            r = AddFunction("rt.release of " + destroy.Name, new FuncType([ValType.I32], []));
            r.Locals.Add(ValType.I32);
            r.Body.Add(new Instr { Op = Op.LocalGet, Imm = 0, Pos = -1 });
            Sequence(r.Body, destroy, 0, 1, -1);
            return releaseOf[destroy] = r;
        }
        foreach (var f in Functions.ToList())
        {
            if (f.IsImport) continue;
            bool any = false;
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Imm2 != 0 && ReferenceEquals(ins.Ref, ReleaseFn) && DestroyOf(ins.Imm2) != null) { any = true; break; }
            if (!any) continue;
            int np = f.Type.Params.Length;
            int t = -1, c = -1;
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
                if (ins.Op != Op.Call || ins.Imm2 == 0 || !ReferenceEquals(ins.Ref, ReleaseFn) || DestroyOf(ins.Imm2) is not { } destroy) { o.Add(ins); continue; }
                if (loops == 0) { o.Add(ins with { Imm2 = 0, Ref = ReleaseOf(destroy) }); continue; }
                if (t < 0)
                {
                    f.Locals.Add(ValType.I32); t = np + f.Locals.Count - 1;
                    f.Locals.Add(ValType.I32); c = np + f.Locals.Count - 1;
                }
                Sequence(o, destroy, t, c, ins.Pos);
                continue;
            }
            f.Body = o;
            changed.Add(f);
        }
        return changed;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> s_ids = new();
    static int s_next;
    static string Id(object o) => s_ids.GetValue(o, _ => Interlocked.Increment(ref s_next).ToString());

    internal static string BodyKey(WasmFunction f)
    {
        var sb = new StringBuilder();
        foreach (var p in f.Type.Params) sb.Append((char)p);
        sb.Append('|'); foreach (var r in f.Type.Results) sb.Append((char)r);
        sb.Append('|'); foreach (var l in f.Locals) sb.Append((char)l);
        foreach (var ins in f.Body)
        {
            sb.Append(';').Append((int)ins.Op).Append(',').Append(ins.Imm).Append(',').Append(ins.Imm2);
            switch (ins.Ref)
            {
                case null: break;
                case WasmFunction g: sb.Append(",f").Append(g.Order); break;
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
        return sb.ToString();
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
