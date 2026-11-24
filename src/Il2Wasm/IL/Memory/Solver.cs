#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>The whole-program fixed point (docs/MEMORY-IR.md sections 1.2 and 3), step 1's skeleton.
/// <para>Phase 1, reach: from the roots, every method reached is imported (its body kept for lowering) and summarized
/// (Summary.cs); its edges reach more: direct calls, constructors, function pointers, the static constructors a use
/// may start, and dispatch: each called slot over each constructed type (rapid type analysis, the dispatch today's
/// IlCompiler.ResolveDispatch does). Reaching a method only imports it.</para>
/// <para>Phase 2, solve (<see cref="Solve"/>): the program's solution over what was reached: method ids in a stable
/// order, dispatch targets, receiver types, and the may-facts (Mods, release effects, escape, signatures), each solved
/// when first asked for. Every fact only grows and the solution is the least fixed point, so it does not depend on the
/// order of the worklist (3.1, 3.5).</para>
/// <para>Lowering then plans and lowers each method in the order it is reached from main, as before (IlCompiler's
/// lowering phase), taking the body imported here. What lowering reaches that the IR does not show (the runtime's
/// helpers the generated code calls, string's constructors, a type's activation) is imported then: a late import,
/// counted in <see cref="LateImports"/>, and outside the solution (<see cref="ProgramSolution.Covers"/> is false for it:
/// a plan treats such a callee as unknown).</para></summary>
public sealed class Solver
{
    readonly IReachHost _host;
    readonly Dictionary<MethodDesc, ImportedMethod?> _units = new();
    readonly List<MethodDesc> _order = [];
    readonly Queue<MethodDesc> _work = new();
    readonly List<MethodDesc> _slots = [];
    readonly HashSet<MethodDesc> _slotSet = [];
    readonly List<TypeDesc> _constructed = [];
    readonly HashSet<TypeDesc> _constructedSet = [];
    readonly HashSet<(TypeDesc, MethodDesc)> _resolved = [];
    readonly HashSet<MethodDesc> _taken = [];
    readonly HashSet<TypeDesc> _initialized = [];
    /// <summary>IL2WASM_MEM_TRACE=1: the reach phase's counts on stderr.</summary>
    static readonly bool s_trace = Environment.GetEnvironmentVariable("IL2WASM_MEM_TRACE") == "1";

    public Solver(IReachHost host) { _host = host; }
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static readonly bool s_heap = Environment.GetEnvironmentVariable("IL2WASM_MEM_TRACE_HEAP") == "1";

    /// <summary>Methods lowering imported itself (not reached here).</summary>
    public int LateImports;
    /// <summary>Methods imported here that lowering never took (their import was work for nothing).</summary>
    public int Unused => _units.Count(u => u.Value != null && !_taken.Contains(u.Key));

    // ------------------------------------------------------------------ phase 1: reach

    /// <summary>The methods rooted before the reach began (the entry, the exports, the round before's seeds: what
    /// code outside the IR calls), for the type sets' unknown parameters.</summary>
    readonly List<MethodDesc> _externalRoots = [];
    bool _reaching;

    public void Root(MethodDesc m)
    {
        if (!_reaching) _externalRoots.Add(m);
        if (_units.ContainsKey(m)) return;
        _units[m] = null;
        _order.Add(m);
        _work.Enqueue(m);
    }

    /// <summary>Reaches the program from the roots given: imports every method reached, to a fixed point.</summary>
    public void Reach()
    {
        _reaching = true;
        for (; ; )
        {
            while (_work.Count > 0)
            {
                var m = _work.Dequeue();
                var u = _host.Import(m);
                _units[m] = u;
                if (u?.Summary is not { } s) continue;
                foreach (var c in s.Calls) Root(c);
                foreach (var t in s.Initializes) if (_initialized.Add(t) && t.GetStaticConstructor() is { } cctor) Root(cctor);
                foreach (var t in s.Constructs) { _irConstructed.Add(t); Construct(t); }
                foreach (var slot in s.Slots) if (_slotSet.Add(slot)) _slots.Add(slot);
            }
            if (!Dispatch() && !MarksReached()) break;
        }
        if (s_trace) Console.Error.WriteLine($"mem: reached {_units.Count} methods ({_units.Count(u => u.Value?.Summary != null)} imported), {_slots.Count} slots, {_constructed.Count} constructed types; {_clock.ElapsedMilliseconds} ms, live heap {(s_heap ? GC.GetTotalMemory(true) / (1 << 20) : -1)} MB, peak {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
    }

    /// <summary>Once a mark can be set (ObjectMarks.SetAt reached), every destroy calls ObjectMarks.OnDestroy and a
    /// finalizable one OnFinalize (Lower/Finalizers.cs: generated code, which no IR edge shows): they are rooted, so
    /// that their facts are solved; out of the solution, every destroy's effects were Wild. True when they were new.
    /// IL2WASM_NO_MARKS_ROOTS=1: not rooted.</summary>
    bool MarksReached()
    {
        if (s_noMarksRoots || _marksRooted) return false;
        var setAt = _units.Keys.FirstOrDefault(m => m.GetName() == "SetAt" && m.OwningType is MetadataType mt && mt.GetName() == "ObjectMarks" && mt.GetNamespace() == "Il2Wasm.Runtime");
        if (setAt == null) return false;
        _marksRooted = true;
        bool any = false;
        foreach (var m in ((MetadataType)setAt.OwningType).GetMethods())
            if (m.GetName() is "OnDestroy" or "OnFinalize" && !_units.ContainsKey(m)) { Root(m); any = true; }
        return any;
    }

    bool _marksRooted;
    static readonly bool s_noMarksRoots = Environment.GetEnvironmentVariable("IL2WASM_NO_MARKS_ROOTS") == "1";
    static readonly bool s_noStubSummaries = Environment.GetEnvironmentVariable("IL2WASM_NO_STUB_SUMMARIES") == "1";

    void Construct(TypeDesc t)
    {
        if (!_constructedSet.Add(t)) return;
        _constructed.Add(t);
        foreach (var m in _host.TypeEdges(t)) Root(m);
    }

    /// <summary>Each (constructed type, called slot) pair not yet resolved: its implementation reached. True when
    /// anything new was reached.</summary>
    bool Dispatch()
    {
        bool any = false;
        foreach (var slot in _slots.ToList())
            foreach (var x in _constructed.ToList())
            {
                if (!_resolved.Add((x, slot))) continue;
                if (_host.Resolve(x, slot) is { } impl && !_units.ContainsKey(impl)) { Root(impl); any = true; }
            }
        return any || _work.Count > 0;
    }

    /// <summary>The method as reached here, for lowering (once: its body belongs to the lowering from then on), or null
    /// when it was not reached here.</summary>
    public ImportedMethod? Take(MethodDesc m)
    {
        if (!_units.TryGetValue(m, out var u) || u == null || !_taken.Add(m)) return null;
        _units[m] = new ImportedMethod(m) { Summary = u.Summary }; // (the body is the lowering's now: let it go after)
        return u;
    }

    /// <summary>The callees (as the summary names them: a constructor by its target, a virtual call by its slot) that a
    /// body calls inside a try region: an exception from them may be caught and the method go on. A call outside every
    /// try that does not return leaves the method by its exception.</summary>
    HashSet<MethodDesc> CalledInTry(Il2Wasm.IL.Ir.Body body)
    {
        var r = new HashSet<MethodDesc>();
        foreach (var b in body.Blocks)
        {
            bool inTry = false;
            for (var x = b.Region; x != null && !inTry; x = x.Parent) inTry = x.Kind == Il2Wasm.IL.Ir.RegionKind.Try;
            if (!inTry) continue;
            foreach (var n in b.Code)
            {
                if (n.X is MethodDesc m)
                {
                    if (n.Op == Il2Wasm.IL.Ir.Opc.NewObj) r.Add(_host.ConstructorTarget(m));
                    else if (n.Op is Il2Wasm.IL.Ir.Opc.Call or Il2Wasm.IL.Ir.Opc.LdFtn or Il2Wasm.IL.Ir.Opc.LdVirtFtn) r.Add(m);
                    // (a static constructor the call or the construction may start: Summary's Initializes rules)
                    if (m.OwningType.GetStaticConstructor() is { } mc) r.Add(mc);
                }
                // (a static of a type, read or written: its static constructor may start here)
                if (n.Op is Il2Wasm.IL.Ir.Opc.LdSFld or Il2Wasm.IL.Ir.Opc.StSFld or Il2Wasm.IL.Ir.Opc.LdSFldA && n.X is FieldDesc sf && sf.OwningType.GetStaticConstructor() is { } sc) r.Add(sc);
            }
        }
        return r;
    }

    /// <summary>A method that only throws (no return in its body: a throw helper).</summary>
    bool NeverReturns(MethodDesc m) => _units.TryGetValue(m, out var u) && u?.Summary is { MayReturn: false };

    /// <summary>A method lowering reached and imported itself.</summary>
    public void Late(ImportedMethod u)
    {
        LateImports++; LateMethods.Add(u.Method); _late.Add(u.Method);
        if (s_lateTrace) Console.Error.WriteLine("mem: late import " + u.Method);
        // (the type sets did not see its stores and calls: another round must be made; not for one with no reference
        // in it, operand or value, which can change no type set: Rt.InitFrameObject, which lowering calls for a frame
        // object, imported late in the last round. A method it calls that is new is imported late itself)
        if (_typeSets && !RefFree(u)) NeedsAnotherRound = true;
        if (ProgramState.IsDependentSetter(u.Method))
        {
            // (from now on the program has dependents; this round's solve left their releases out, so its plans may be
            // wrong: another round must be made, whose solve counts them)
            ProgramState.Of(u.Method.Context).DependentsSeen = true;
            if (_skipDependents) NeedsAnotherRound = true;
        }
    }

    bool _skipDependents, _typeSets;

    static bool RefFree(ImportedMethod u) => u.Body is { } b && b.Blocks.All(bl => bl.Phis.Concat(bl.Code).All(n =>
        n.Kind != VK.Ref && !(n.Kind == VK.Ptr && n.Type is ByRefType) && n.A.All(a => a.Kind != VK.Ref && !(a.Kind == VK.Ptr && a.Type is ByRefType))));

    /// <summary>This round's module must not be the one encoded: its solve left out releases a late import showed can
    /// happen (ObjectMarks' [ReleasesDependents]). The round loop goes on (IlCompiler.Compile).</summary>
    public bool NeedsAnotherRound { get; private set; }

    /// <summary>Before a round's module is encoded: a hard error when it needed another round and none is left.</summary>
    public void CheckEncodable()
    {
        if (NeedsAnotherRound)
            throw new InvalidOperationException("release effects: a dependent handle's setter was imported late in the last round, after its solve left dependents' releases out (raise IL2WASM_IL_ROUNDS)");
    }
    static readonly bool s_verify = Environment.GetEnvironmentVariable("IL2WASM_VERIFY") == "1";
    static readonly bool s_lateTrace = Environment.GetEnvironmentVariable("IL2WASM_LATE_TRACE") == "1";
    readonly HashSet<MethodDesc> _late = [];

    /// <summary>The methods lowering imported itself, in order: the next round's reach phase starts from them too, so
    /// that the solve covers what the generated code reaches (the generated parts are made during lowering).</summary>
    public readonly List<MethodDesc> LateMethods = [];

    /// <summary>A type the reach phase found constructed in the IR (not only seeded).</summary>
    public bool IsConstructed(TypeDesc t) => _irConstructed.Contains(t);
    readonly HashSet<TypeDesc> _irConstructed = [];

    /// <summary>Lowering took the method from the reach phase, or imported it itself.</summary>
    public bool WasLowered(MethodDesc m) => _taken.Contains(m) || _late.Contains(m);

    /// <summary>A type constructed by something the IR does not show (the generated code, the round before's lowering):
    /// its dispatch and finalizer are reached too.</summary>
    public void SeedConstructed(TypeDesc t) => Construct(t);

    // ------------------------------------------------------------------ phase 2: solve

    /// <summary>The solution over what was reached (section 3). Called once the reach phase is done.</summary>
    public ProgramSolution Solve()
    {
        var sol = new ProgramSolution(_host);
        // (row a: Signatures.Compute reads covered bodies, read-only, before lowering takes them)
        sol.BodyOf = m => _units.TryGetValue(m, out var u) && u != null ? (u.Body ?? (u.Il != null && u.Error == null && !u.Substituted ? Il2Wasm.IL.Ir.Importer.Import(u.Method, u.Il, u.Facts) : null)) : null;
        // (ObjectMarks' [ReleasesDependents] contract: dependents exist only once WeakSlots.LinkDependent or
        // ExchangeDependent can run, in any round of this program, a late import included; round 0 cannot know its own
        // late imports yet, so the rule starts at round 1)
        var state = ProgramState.Of(_order.Count > 0 ? _order[0].Context : null);
        if (_order.Any(ProgramState.IsDependentSetter)) state.DependentsSeen = true;
        bool skip = !state.DependentsSeen && state.Solves > 0;
        state.Solves++;
        _skipDependents = skip;
        sol.DependentsReleased = !skip;
        foreach (var m in _order)
        {
            // (an ILLink substitution's stub: it returns its constant and does nothing else, so it is in the solution with
            // no effects; out of it, every call of it was a call the solve cannot see, Wild. EventSource.IsEnabled is
            // one, on ArrayPool's paths. IL2WASM_NO_STUB_SUMMARIES=1: left out)
            if (_units[m] is { Summary: null, Substituted: true, Error: null } su && !s_noStubSummaries) su.Summary = new MethodSummary(m) { MayReturn = true };
            if (_units[m] is { Summary: { } s } u) sol.AddMethod(m, s, null, null,
                u.Body is { } tb ? CalledInTry(tb) : u.Body == null && (u.Il == null || u.Substituted) ? [] : null);
        }
        foreach (var t in _constructed) sol.AddConstructed(t);
        foreach (var slot in _slots) sol.AddSlot(slot);
        sol.Freeze();
        sol.HeldBodies = m => _units.TryGetValue(m, out var u) && u is { Error: null, Substituted: false } ? u.Body : null;
        // (the type sets from round 1 on: round 0 cannot know its own late imports, whose stores they do not see)
        if (state.Solves > 1) { sol.SolveTypeSets(_externalRoots); sol.DumpTypeSets(); }
        _typeSets = sol.TypeSetsSolved;
        foreach (var m in _order)
            if (_units[m] is { Summary: { } } u)
            {
                Func<Il2Wasm.IL.Ir.Node, IEnumerable<TypeDesc>?>? exact = sol.TypeSetsSolved ? n => sol.ExactTypes(m, n) : null;
                sol.SetReleased(m, u.Body is { } b ? ProgramSolution.ReleasedTypes(b, skip, exact: exact) : u.Body == null && (u.Il == null || u.Substituted) ? [] : null,
                    u.Body is { } rb ? ProgramSolution.ReleasedOnReturn(rb, skip, NeverReturns, exact) : u.Body == null && (u.Il == null || u.Substituted) ? [] : null);
                if (s_verify && u.Body is { } vb && ProgramSolution.ReleaseViolations(vb, skip, exact) is { Count: > 0 } bad)
                    throw new InvalidOperationException("release effects: " + m + ": " + string.Join("; ", bad));
            }
        if (s_trace) Console.Error.WriteLine($"mem: solved {sol.Methods.Count} methods, {sol.Slots.Count} slots; {_clock.ElapsedMilliseconds} ms");
        _solution = sol;
        return sol;
    }

    ProgramSolution? _solution;

    /// <summary>After lowering: how the reach phase matched it, and the solved facts' share (IL2WASM_MEM_TRACE).</summary>
    public void Report()
    {
        if (s_trace && _solution is { } sol && sol.Methods.Count > 0)
        {
            // (after lowering, so after Signatures.Compute filled the intrinsic table; solved here if no plan asked)
            long t0 = _clock.ElapsedMilliseconds;
            int all = 0, tame = 0, lib = 0, libTame = 0;
            foreach (var m in sol.Methods.Items)
            {
                bool core = m.OwningType is MetadataType { Module: var mod } && mod == m.Context.SystemModule;
                bool t = !sol.Facts(m).Mods.Wild;
                all++; if (t) tame++;
                if (core) { lib++; if (t) libTame++; }
            }
            Console.Error.WriteLine($"mem: release effects Wild for {sol.Methods.Items.Count(m => sol.Facts(m).ReleaseEffects.Wild)} methods; {sol.DestroyReport}; facts {sol.FactsTime.Release} ms release targets ({sol.FactsTime.ReleaseNodes} release nodes), {sol.FactsTime.Graph} ms graph, {sol.FactsTime.Solve} ms fixed point");
            Console.Error.WriteLine($"mem: Mods not Wild for {tame} of {all} methods, {libTame} of {lib} CoreLib methods ({(lib == 0 ? 0 : 100.0 * libTame / lib):F1}%); facts solved in {_clock.ElapsedMilliseconds - t0} ms");
        }
        if (Environment.GetEnvironmentVariable("IL2WASM_MEM_WHY") is { Length: > 0 } why && _solution is { } ws)
            foreach (var wm in ws.Methods.Items.Where(x => x.ToString()!.Contains(why, StringComparison.Ordinal)).Take(5))
            {
                Console.Error.WriteLine("mem: why Wild " + wm + ": " + ws.WhyWild(wm));
                Console.Error.WriteLine("mem: why Wild on return " + wm + ": " + ws.WhyReturnWild(wm));
            }
        if (Environment.GetEnvironmentVariable("IL2WASM_MEM_FACTS_DUMP") is { Length: > 0 } dump && _solution is { } ds && ds.Methods.Count > 0) ds.DumpFacts(dump);
        if (s_trace) Console.Error.WriteLine($"mem: lowering took {_taken.Count} reached methods, imported {LateImports} late; {Unused} reached methods never lowered; {_clock.ElapsedMilliseconds} ms since the reach phase began, live heap {(s_heap ? GC.GetTotalMemory(true) / (1 << 20) : -1)} MB, peak {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
    }
}

/// <summary>The program's solution (section 3; the successor of RoundFacts as the facts a plan reads). Method ids are
/// dense and in a stable order (by the method's printed identity, 3.5); the may-facts are solved when first asked for
/// (Mods and release effects over the call graph's strongly connected components, callees first), so a build that
/// asks for none pays nothing. Facts for a method outside the solution (a late import) are the top: unknown.</summary>
public sealed partial class ProgramSolution
{
    readonly IReachHost _host;
    /// <summary>A covered method's body before lowering takes it (Signatures.Compute reads it; null once taken or when there is none).
    /// Use BodyOf when you need a body (it imports again when the held one is gone); HeldBody when you would rather skip the method than pay for an import.</summary>
    public Func<MethodDesc, Il2Wasm.IL.Ir.Body?> BodyOf { get; internal set; } = _ => null;
    internal Func<MethodDesc, Body?>? HeldBodies;
    /// <summary>The body the reach phase imported for a method, while lowering has not taken it (null otherwise, and
    /// with IL2WASM_MEM_REIMPORT=1). Read only: the IR inliner (Ir/Inline.cs) copies it instead of importing the
    /// method again. Use HeldBody when you would rather skip the method than pay for an import; BodyOf when you need a body.</summary>
    public Body? HeldBody(MethodDesc m) => HeldBodies?.Invoke(m);
    readonly IdTable<MethodDesc> _methods = new();
    readonly IdTable<TypeDesc> _types = new();
    /// <summary>The methods of the solution, by MethodId (assigned on first use, in stable order).</summary>
    public IdTable<MethodDesc> Methods { get { EnsureIds(); return _methods; } }
    /// <summary>Type ids: the constructed types first (in stable order), then declared types a Cone names.</summary>
    public IdTable<TypeDesc> Types { get { EnsureIds(); return _types; } }
    public readonly KeyTable Keys = new();
    public readonly SiteTable Sites = new();
    public readonly SetPool Pool = new();
    readonly List<MethodDesc> _arrival = [];
    readonly Dictionary<MethodDesc, MethodSummary> _summaries = new();
    readonly List<TypeDesc> _constructed = [];
    public readonly List<MethodDesc> Slots = [];
    readonly Dictionary<MethodDesc, MethodDesc[]> _targets = new();
    MethodFacts[]? _facts;
    bool _frozen;

    internal ProgramSolution(IReachHost host) { _host = host; }

    internal void SetReleased(MethodDesc m, TypeDesc[]? released, (TypeDesc, int, FieldDesc?, MethodDesc?)[]? releasedReturning)
    {
        _released[m] = released; _releasedReturning[m] = releasedReturning;
    }

    internal void AddMethod(MethodDesc m, MethodSummary s, TypeDesc[]? released = null, (TypeDesc, int, FieldDesc?, MethodDesc?)[]? releasedReturning = null, HashSet<MethodDesc>? calledInTry = null)
    {
        _arrival.Add(m); _summaries[m] = s; _released[m] = released; _releasedReturning[m] = releasedReturning; _inTry[m] = calledInTry;
    }

    /// <summary>The released types of the blocks from which the method can still return (ReleasedTypes over them):
    /// what a release on a run that returns can be. Null: not known.</summary>
    readonly Dictionary<MethodDesc, (TypeDesc Type, int Arg, FieldDesc? Static, MethodDesc? Getter)[]?> _releasedReturning = new();

    /// <summary>Per method, the callees called inside a try region (null: not known, every call counts as one).</summary>
    readonly Dictionary<MethodDesc, HashSet<MethodDesc>?> _inTry = new();
    KeySet[]? _returnMods, _local;
    List<(int Callee, bool Full)>[]? _rsucc;

    /// <summary>IL2WASM_MEM_WHY, for the runs that return: the chain of return-path edges to the first method whose own
    /// effects, or a callee's whole Mods or release effects, make the ReturnMods Wild.</summary>
    internal string WhyReturnWild(MethodDesc root)
    {
        int r0 = MethodId(root);
        if (r0 < 0) return "outside the solution";
        if (!ReturnMods(root).Wild) return "not Wild on return: " + string.Join(", ", ReturnMods(root).Keys.Items.ToArray().Select(k => Keys[k] + " #" + k));
        var parent = new Dictionary<int, int> { [r0] = -1 };
        var q = new Queue<int>();
        q.Enqueue(r0);
        string Chain(int i) { var l = new List<string>(); for (int x = i; x >= 0; x = parent[x]) l.Add(Methods[x].ToString()!); l.Reverse(); return string.Join(" -> ", l); }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            if (_local![i].Wild) return "its own effects (" + WhyWild(Methods[i]).Split(':')[0] + "): " + Chain(i);
            var sig = SignaturesComputed ? _facts![i].Signature : null;
            var rt = (_releasedReturning.GetValueOrDefault(Methods[i]) ?? [(Object, -1, null, null)]).Where(e => !(e.Arg >= 0 && sig != null && e.Arg < sig.Params.Length && sig.Params[e.Arg] == ParamConv.Lend) && !(e.Static != null && Persistent(e.Static)) && !(e.Getter != null && PersistentGetter(e.Getter)) && DestroyEffects(e.Type).Wild).Select(e => e.Type + (e.Arg >= 0 ? " (parameter " + e.Arg + ")" : "")).ToList();
            if (rt.Count > 0) return "its releases on returning runs, of " + string.Join(", ", rt) + ": " + Chain(i);
            foreach (var (j, full) in _rsucc![i])
            {
                if (full && (_facts![j].Mods.Wild || _facts[j].ReleaseEffects.Wild)) return "a call inside a try of a Wild method: " + Chain(i) + " -> " + Methods[j] + "; " + WhyWild(Methods[j]);
                if (!full && _returnMods![j].Wild && parent.TryAdd(j, i)) q.Enqueue(j);
            }
        }
        return "no cause found";
    }

    /// <summary>The keys the method may write on the runs that return normally (or that it ends itself): its own
    /// stores and those of its callees, except the callees it calls outside every try region that never return (a
    /// throw helper: if it runs, the method leaves by its exception, so a caller that gets control back normally saw
    /// none of it), and with only the return-path effects of the callees it calls outside try regions; its releases
    /// count from the blocks that can still return (a handler that only rethrows does not). A borrow whose interval holds the call but no handler it can reach (Borrows.cs) needs only
    /// these; one live into a handler needs <see cref="MethodFacts.Mods"/>. For a method outside the solution: any key.</summary>
    public KeySet ReturnMods(MethodDesc m)
    {
        int id = Methods.IdOf(m);
        if (id < 0) return KeySet.All;
        SolveReturnMods();
        return _returnMods![id];
    }

    /// <summary>The static types of the counted values a method's body may release (3.6): its counted values, and the
    /// old values of the counted slots it stores. Null: not known (any type).</summary>
    readonly Dictionary<MethodDesc, TypeDesc[]?> _released = new();

    /// <summary>The types a body may release: every counted value (a reference: its static type, object when the IR has
    /// none; a struct with references) and every counted slot it stores (the old value goes).</summary>
    /// <summary>The types released on the runs that return (ReleasedTypes over the blocks that can still return), each
    /// with the parameter it is the value of (an Arg, or a cast of one), or -1.</summary>
    internal static (TypeDesc, int, FieldDesc?, MethodDesc?)[] ReleasedOnReturn(Il2Wasm.IL.Ir.Body body, bool skipDependents, Func<MethodDesc, bool>? neverReturns = null, Func<Il2Wasm.IL.Ir.Node, IEnumerable<TypeDesc>?>? exact = null)
    {
        var all = ReleasedTypes(body, skipDependents, onlyReturning: true, argsApart: true, neverReturns: neverReturns, exact: exact);
        var r = new List<(TypeDesc, int, FieldDesc?, MethodDesc?)>();
        foreach (var t in all) r.Add((t, -1, null, null));
        var obj = body.Method.Context.GetWellKnownType(WellKnownType.Object);
        // (the values read from a static readonly field, apart: if the field is persistent, ProgramSolution.Persistent,
        // the object outlives every release of them)
        foreach (var b in body.Blocks)
            foreach (var n in b.Code)
            {
                if (n.Kind != Il2Wasm.IL.Ir.VK.Ref) continue;
                var nt = n.Type == null ? obj : !n.Type.IsValueType ? n.Type : n.Type;
                if (StaticReadOnlyLoad(n) is { } sf) foreach (var et in exact?.Invoke(n) ?? [nt]) r.Add((et, -1, sf, null));
                // (the result of a direct call, apart: a getter of a persistent static returns an object a release
                // cannot free, ProgramSolution.PersistentGetter)
                else if (GetterCall(n) is { } gm) foreach (var et in exact?.Invoke(n) ?? [nt]) r.Add((et, -1, null, gm));
            }
        return r.Distinct().OrderBy(e => StableKey.Of(e.Item1), StringComparer.Ordinal).ThenBy(e => e.Item2)
            .ThenBy(e => e.Item3 == null ? "" : StableKey.Of(e.Item3), StringComparer.Ordinal).ThenBy(e => e.Item4 == null ? "" : StableKey.Of(e.Item4), StringComparer.Ordinal).ToArray();
    }

    /// <summary>The direct, static, parameterless callee a value is the result of (or a cast of one), or null.</summary>
    static MethodDesc? GetterCall(Il2Wasm.IL.Ir.Node n)
    {
        if (IsForward(n)) n = n.A[0];
        return n.Op == Il2Wasm.IL.Ir.Opc.Call && (Il2Wasm.IL.Ir.CallKind)n.Sub == Il2Wasm.IL.Ir.CallKind.Direct && n.A.Length == 0 && n.X is MethodDesc m && m.Signature.IsStatic ? m : null;
    }

    readonly Dictionary<MethodDesc, bool> _persistentGetter = new();

    /// <summary>A static method of no parameters every return of which is a read of a persistent static (Persistent),
    /// and that stores nothing and calls nothing (the field's type initializer may run first: a call effect, counted as
    /// the call's): its result is an object no release can free.</summary>
    public bool PersistentGetter(MethodDesc m)
    {
        if (_persistentGetter.TryGetValue(m, out var r)) return r;
        r = false;
        try
        {
            if (Covers(m) && m.GetTypicalMethodDefinition() is Internal.TypeSystem.Ecma.EcmaMethod typical)
            {
                Internal.IL.MethodIL il = Internal.IL.EcmaMethodIL.Create(typical);
                if (m != typical) il = new Internal.IL.InstantiatedMethodIL(m, il);
                var body = Il2Wasm.IL.Ir.Importer.Import(m, il);
                bool any = false, ok = true;
                foreach (var b in body.Blocks)
                    foreach (var n in b.Code)
                    {
                        if (n.Op == Il2Wasm.IL.Ir.Opc.Ret) { any = true; if (n.A.Length != 1 || StaticReadOnlyLoad(n.A[0]) is not { } f || !Persistent(f)) ok = false; }
                        else if (n.Op is Il2Wasm.IL.Ir.Opc.Call or Il2Wasm.IL.Ir.Opc.CallI or Il2Wasm.IL.Ir.Opc.NewObj or Il2Wasm.IL.Ir.Opc.StFld or Il2Wasm.IL.Ir.Opc.StSFld or Il2Wasm.IL.Ir.Opc.StElem or Il2Wasm.IL.Ir.Opc.StInd or Il2Wasm.IL.Ir.Opc.CpBlk or Il2Wasm.IL.Ir.Opc.VarSt) ok = false;
                    }
                r = any && ok;
            }
        }
        catch (Exception) { r = false; }
        _persistentGetter[m] = r;
        return r;
    }

    /// <summary>The static readonly field a value is read from (LdSFld, or a cast of one), or null.</summary>
    static FieldDesc? StaticReadOnlyLoad(Il2Wasm.IL.Ir.Node n)
    {
        if (IsForward(n)) n = n.A[0];
        return n.Op == Il2Wasm.IL.Ir.Opc.LdSFld && n.X is FieldDesc f && f.IsStatic && f.IsInitOnly && !f.IsThreadStatic ? f : null;
    }

    readonly Dictionary<FieldDesc, bool> _persistent = new();
    bool? _reflectionSets;

    /// <summary>A static readonly field that, once set, holds its object for the program's life (MEMORY-IR 2.5's
    /// Persistent, for one static): only its type's static constructor stores it, at one store outside any loop; no code
    /// takes its address; reflection cannot set it (no FieldInfo.SetValue reached). A release of a value read from it
    /// then frees nothing. Needs the constant propagation's record of statics written (off with IL2WASM_NO_SCCP).</summary>
    public bool Persistent(FieldDesc f)
    {
        f = f.GetTypicalFieldDefinition();
        if (_persistent.TryGetValue(f, out var r)) return r;
        r = ComputePersistent(f);
        _persistent[f] = r;
        return r;
    }

    bool ComputePersistent(FieldDesc f)
    {
        if (!f.IsStatic || !f.IsInitOnly || f.IsThreadStatic || Il2Wasm.IL.Reach.ProgramFacts.Off) return false;
        _reflectionSets ??= Methods.Items.Any(m => m.GetName().StartsWith("SetValue", StringComparison.Ordinal) && DerivesFrom(m.OwningType, "System.Reflection", "FieldInfo"));
        if (_reflectionSets == true) return false;
        var key = MemKey.Static(f);
        var writers = new List<MethodDesc>();
        foreach (var m in Methods.Items)
        {
            var su = _summaries[m];
            bool writes = su.Stores.Contains(key) || su.StaticsWritten.Any(x => x.GetTypicalFieldDefinition() == f);
            if (!writes) continue;
            if (!m.IsStaticConstructor || m.OwningType.GetTypeDefinition() != f.OwningType.GetTypeDefinition()) return false;
            if (su.Stores.Count(x => x == key) > 1) return false;
            // (each instantiation's static constructor writes its own instantiation's field)
            writers.Add(m);
        }
        // (the store outside every loop of the constructor, and no address of the field taken in it)
        foreach (var writer in writers)
        try
        {
            if (writer.GetTypicalMethodDefinition() is not Internal.TypeSystem.Ecma.EcmaMethod typical) return false;
            Internal.IL.MethodIL il = Internal.IL.EcmaMethodIL.Create(typical);
            if (writer != typical) il = new Internal.IL.InstantiatedMethodIL(writer, il);
            var body = Il2Wasm.IL.Ir.Importer.Import(writer, il);
            var looping = new HashSet<Il2Wasm.IL.Ir.Block>();
            foreach (var b in body.Blocks)
            {
                var seen = new HashSet<Il2Wasm.IL.Ir.Block>();
                var work = new Stack<Il2Wasm.IL.Ir.Block>(b.Succs);
                while (work.Count > 0) { var x = work.Pop(); if (x == b) { looping.Add(b); break; } if (seen.Add(x)) foreach (var t in x.Succs) work.Push(t); }
            }
            foreach (var b in body.Blocks)
                foreach (var n in b.Code)
                {
                    if (n.Op == Il2Wasm.IL.Ir.Opc.LdSFldA && n.X is FieldDesc af && af.GetTypicalFieldDefinition() == f) return false;
                    if (n.Op == Il2Wasm.IL.Ir.Opc.StSFld && n.X is FieldDesc sf && sf.GetTypicalFieldDefinition() == f && looping.Contains(b)) return false;
                }
        }
        catch (Exception) { return false; }
        return true;
    }

    static bool DerivesFrom(TypeDesc? t, string ns, string name)
    {
        for (; t != null; t = t.BaseType) if (t is MetadataType mt && mt.GetName() == name && mt.GetNamespace() == ns) return true;
        return false;
    }

    /// <summary>Per reference variable whose address the body never takes (no LocA), the static types of the values it
    /// stores into it (null for a null): the only things the variable can hold.</summary>
    static Dictionary<int, List<TypeDesc?>> VarStores(Il2Wasm.IL.Ir.Body body)
    {
        var taken = new HashSet<int>();
        var r = new Dictionary<int, List<TypeDesc?>>();
        foreach (var b in body.Blocks)
            foreach (var n in b.Code)
            {
                if (n.Op == Il2Wasm.IL.Ir.Opc.LocA) taken.Add((int)n.I);
                else if (n.Op == Il2Wasm.IL.Ir.Opc.VarSt && n.A.Length > 0)
                {
                    if (!r.TryGetValue((int)n.I, out var l)) r[(int)n.I] = l = [];
                    var v = n.A[0];
                    if (v.Op != Il2Wasm.IL.Ir.Opc.LdNull && !l.Contains(v.Type)) l.Add(v.Type);
                }
            }
        foreach (int t in taken) r.Remove(t);
        // (a struct argument's or a parameter's own storage may hold what the caller put there)
        foreach (var k in r.Keys.ToList()) if (body.Vars[k].ArgIndex >= 0) r.Remove(k);
        return r;
    }

    static bool IsForward(Il2Wasm.IL.Ir.Node n) => n.Op is Il2Wasm.IL.Ir.Opc.CastClass or Il2Wasm.IL.Ir.Opc.IsInst
        || n.Op == Il2Wasm.IL.Ir.Opc.Call && n.A.Length == 1 && n.X is MethodDesc m && m.GetName() == "As" && m.OwningType is MetadataType t && t.GetName() == "Unsafe";

    internal static TypeDesc[] ReleasedTypes(Il2Wasm.IL.Ir.Body body, bool skipDependents = false, bool onlyReturning = false, bool argsApart = false, Func<MethodDesc, bool>? neverReturns = null, Func<Il2Wasm.IL.Ir.Node, IEnumerable<TypeDesc>?>? exact = null)
    {
        // (onlyReturning: the blocks from which a return is reachable, through branches and handlers; a block that can
        // only end in a throw releases on the way out by its exception)
        HashSet<Il2Wasm.IL.Ir.Block>? returning = null;
        if (onlyReturning)
        {
            var preds = new Dictionary<Il2Wasm.IL.Ir.Block, List<Il2Wasm.IL.Ir.Block>>();
            foreach (var b in body.Blocks)
                // (a block that calls a method that never returns goes on only by that call's exception)
                foreach (var t in (neverReturns != null && b.Code.Any(n => n.Op == Il2Wasm.IL.Ir.Opc.Call && n.X is MethodDesc cm && neverReturns(cm)) ? [] : b.Succs).Concat(Il2Wasm.IL.Ir.Ownership.ExceptionSuccs(b)))
                {
                    if (!preds.TryGetValue(t, out var l)) preds[t] = l = [];
                    l.Add(b);
                }
            returning = [];
            var work = new Stack<Il2Wasm.IL.Ir.Block>(body.Blocks.Where(b => b.Term?.Op == Il2Wasm.IL.Ir.Opc.Ret));
            while (work.Count > 0)
            {
                var b = work.Pop();
                if (!returning.Add(b)) continue;
                foreach (var p in preds.GetValueOrDefault(b) ?? []) work.Push(p);
            }
            // (a finally or fault block runs on the way to a return too: its end continues to the leave targets)
            foreach (var r in body.Regions)
                if (r.Kind is Il2Wasm.IL.Ir.RegionKind.Finally or Il2Wasm.IL.Ir.RegionKind.Fault)
                    foreach (var fb in Il2Wasm.IL.Ir.Ownership.BlocksWithin(r)) returning.Add(fb);
        }
        bool dependents = skipDependents && ReleasesDependents(body.Method);
        var set = new HashSet<TypeDesc>();
        var obj = body.Method.Context.GetWellKnownType(WellKnownType.Object);
        void Add(TypeDesc? t, Il2Wasm.IL.Ir.VK kind)
        {
            // (a reference whose static type is a value type is a box of it: its destroy releases the struct's counted
            // fields, a Nullable's box is one of the underlying type)
            if (kind == Il2Wasm.IL.Ir.VK.Ref) set.Add(t == null ? obj : !t.IsValueType ? t : t.IsNullable ? t.Instantiation[0] : t);
            else if (kind == Il2Wasm.IL.Ir.VK.Struct && t != null && Il2Wasm.IL.Ir.Kinds.Counted(t)) set.Add(t);
        }
        void Slot(TypeDesc? t) { if (t != null && !t.IsPointer && !t.IsByRef && !t.IsFunctionPointer && !t.IsGenericParameter) Add(t, Il2Wasm.IL.Ir.Kinds.Of(t)); }
        Dictionary<int, List<TypeDesc?>>? varStores = null;
        foreach (var b in body.Blocks)
        {
            if (returning != null && !returning.Contains(b)) continue;
            foreach (var n in b.Phis.Concat(b.Code))
            {
                // (null, literal strings and type objects hold no count: immortal, FunctionLowering's TypeOf)
                if (n.Op is Il2Wasm.IL.Ir.Opc.LdNull or Il2Wasm.IL.Ir.Opc.LdStr or Il2Wasm.IL.Ir.Opc.TypeOf) continue;
                if (dependents && IsBorrow(n)) continue;
                // (a parameter's object, or a cast of it, is not the method's to free: the caller holds it for the whole
                // call, or hands its count over at its last use, and then counts that release as its own at the call,
                // Borrows' moved operands. So a method's releases never include its parameters)
                if (n.Kind == Il2Wasm.IL.Ir.VK.Ref && (n.Op == Il2Wasm.IL.Ir.Opc.Arg || IsForward(n) && n.A[0].Op == Il2Wasm.IL.Ir.Opc.Arg)) continue;
                // (argsApart: the values read from statics and getters are listed apart, ReleasedOnReturn)
                if (argsApart && n.Kind == Il2Wasm.IL.Ir.VK.Ref && (StaticReadOnlyLoad(n) != null || GetterCall(n) != null)) continue;
                // (Generics.New<T..>("Name"): an object of exactly the type its literal names, MethodSummary.NewByName)
                if (n.Op == Il2Wasm.IL.Ir.Opc.Call && n.X is MethodDesc nm && MethodSummary.NewByName(nm, n) is { } made) { set.Add(made.OwningType); continue; }
                // (one lowering makes a trap: no value, nothing to release)
                if (n.Op == Il2Wasm.IL.Ir.Opc.Call && n.X is MethodDesc tm && MethodSummary.NewTraps(tm, n)) continue;
                // (a reference whose exact types the type sets know: those, not its static type's cone)
                if (n.Kind == Il2Wasm.IL.Ir.VK.Ref && exact?.Invoke(n) is { } ets) foreach (var et in ets) set.Add(et);
                else Add(n.Type, n.Kind);
                switch (n.Op)
                {
                    case Il2Wasm.IL.Ir.Opc.StFld or Il2Wasm.IL.Ir.Opc.StSFld when n.X is FieldDesc f: Slot(f.FieldType); break;
                    case Il2Wasm.IL.Ir.Opc.StElem: Slot(n.A[0].Type is ArrayType at ? at.ElementType : n.A[2].Type); break;
                    case Il2Wasm.IL.Ir.Opc.VarSt:
                    {
                        // (a frame variable whose address is never taken holds only what this body stores into it:
                        // its old value is one of those, a pinned local's hidden root among them)
                        int slot = (int)n.I;
                        var vt = body.Vars[slot].Type;
                        if (body.Vars[slot].Kind == Il2Wasm.IL.Ir.VK.Ref && (varStores ??= VarStores(body)).TryGetValue(slot, out var stored))
                            foreach (var t in stored) Add(t, Il2Wasm.IL.Ir.VK.Ref);
                        else Slot(vt);
                        break;
                    }
                    // (lowering releases a slot's old value only for a counted value stored: a reference, a struct with
                    // references; InitObj clears the struct type it names)
                    case Il2Wasm.IL.Ir.Opc.StInd when n.A.ElementAtOrDefault(1) is { } sv:
                    {
                        // (the old value is of the slot's type: the address's, else the instruction's, else anything)
                        var slotType = n.A[0].Type is ByRefType br ? br.ParameterType : n.X as TypeDesc;
                        if (sv.Kind == Il2Wasm.IL.Ir.VK.Ref) Add(slotType is { IsValueType: false } ? slotType : obj, Il2Wasm.IL.Ir.VK.Ref);
                        else if (sv.Kind == Il2Wasm.IL.Ir.VK.Struct) Slot(slotType ?? sv.Type);
                    }
                        break;
                    case Il2Wasm.IL.Ir.Opc.InitObj: Slot(n.X as TypeDesc); break;
                    // (CpBlk and InitBlk lower to memory.copy and memory.fill: no count, nothing released)
                    // (Rt.ReleaseElements<T>(addr, count): T's at an address, the elements' type itself)
                    case Il2Wasm.IL.Ir.Opc.Call when n.X is MethodDesc rm && rm.GetName() == "ReleaseElements" && IsRt(rm) && rm.HasInstantiation: Slot(rm.Instantiation[0]); break;
                    // (the runtime's own code releasing a parameter by hand, Rt.Release(p): that one is the method's)
                    case Il2Wasm.IL.Ir.Opc.Call when n.X is MethodDesc pm && pm.GetName() == "Release" && IsRt(pm) && n.A.Length == 1 && n.A[0] is var pa && (pa.Op == Il2Wasm.IL.Ir.Opc.Arg || IsForward(pa) && pa.A[0].Op == Il2Wasm.IL.Ir.Opc.Arg):
                        Add(pa.Type, Il2Wasm.IL.Ir.VK.Ref); break;
                }
            }
        }
        return set.OrderBy(t => StableKey.Of(t), StringComparer.Ordinal).ToArray();
    }

    /// <summary>The dependents' releases count (a dependent can exist: WeakSlots.LinkDependent or ExchangeDependent
    /// reached in some round, or this is round 0); false: ObjectMarks' [ReleasesDependents] methods release none.</summary>
    public bool DependentsReleased { get; internal set; } = true;

    static bool ReleasesDependents(MethodDesc m) => m.GetTypicalMethodDefinition() is Internal.TypeSystem.Ecma.EcmaMethod em && em.HasCustomAttribute("Il2Wasm.Runtime", "ReleasesDependentsAttribute");

    /// <summary>Rt.Borrow&lt;T&gt;(int): an object from an address (no count of its own).</summary>
    static bool IsBorrow(Il2Wasm.IL.Ir.Node n) => n.Op == Il2Wasm.IL.Ir.Opc.Call && n.X is MethodDesc m && m.GetName() == "Borrow" && IsRt(m);
    static bool IsRt(MethodDesc m) => m.OwningType is MetadataType t && t.GetName() == "Rt" && t.GetNamespace() == "Il2Wasm.Runtime";

    /// <summary>The check behind the Rt.Release and Rt.ReleaseElements intrinsics' effects (IntrinsicSignatures: none
    /// of their own, because the solve's release nodes cover them): each such call's operand, followed back through
    /// casts, is a counted value of the body whose static type is in its released types, and not an object made from an
    /// address with a type narrower than object (Rt.Borrow&lt;T&gt;, Unsafe.As of an int), which would claim less than
    /// the object may be. Dependents' releases left out by the [ReleasesDependents] rule are exempt. The problems, or
    /// none. Run in every solve with IL2WASM_VERIFY=1.</summary>
    public static List<string> ReleaseViolations(Il2Wasm.IL.Ir.Body body, bool skipDependents, Func<Il2Wasm.IL.Ir.Node, IEnumerable<TypeDesc>?>? exact = null)
    {
        var bad = new List<string>();
        var types = ReleasedTypes(body, skipDependents, exact: exact).ToHashSet();
        bool dependents = skipDependents && ReleasesDependents(body.Method);
        var obj = body.Method.Context.GetWellKnownType(WellKnownType.Object);
        foreach (var b in body.Blocks)
            foreach (var n in b.Code)
            {
                if (n.Op != Il2Wasm.IL.Ir.Opc.Call || n.X is not MethodDesc rm || !IsRt(rm) || rm.GetName() is not ("Release" or "ReleaseElements") || n.A.Length == 0) continue;
                if (rm.GetName() == "ReleaseElements")
                {
                    // (the elements' type is a type argument: counted or not, it is among the released types when counted)
                    var et = rm.HasInstantiation ? rm.Instantiation[0] : null;
                    if (et == null || et.IsGenericParameter) bad.Add("ReleaseElements without a type");
                    else if (Il2Wasm.IL.Ir.Kinds.Of(et) is Il2Wasm.IL.Ir.VK.Ref or Il2Wasm.IL.Ir.VK.Struct && Il2Wasm.IL.Ir.Kinds.Counted(et) && !types.Contains(et)) bad.Add($"ReleaseElements<{et}> not among the released types");
                    continue;
                }
                var x = n.A[0];
                for (int guard = 0; guard < 16; guard++)
                {
                    if (dependents && IsBorrow(x)) break;
                    if (x.Kind != Il2Wasm.IL.Ir.VK.Ref) { bad.Add($"{rm.GetName()} of v{x.Id}, not a reference"); break; }
                    var st = x.Type == null ? obj : !x.Type.IsValueType ? x.Type : x.Type.IsNullable ? x.Type.Instantiation[0] : x.Type;
                    if (exact?.Invoke(x) is { } xs ? !xs.All(types.Contains) : !types.Contains(st)) { bad.Add($"{rm.GetName()} of v{x.Id} ({st}) not among the released types"); break; }
                    if (IsBorrow(x) && x.X is MethodDesc bm && bm.HasInstantiation && !bm.Instantiation[0].IsObject) { bad.Add($"{rm.GetName()} of Rt.Borrow<{bm.Instantiation[0]}>: narrower than the object may be"); break; }
                    if (x.Op is Il2Wasm.IL.Ir.Opc.CastClass or Il2Wasm.IL.Ir.Opc.IsInst || x.Op == Il2Wasm.IL.Ir.Opc.Call && x.A.Length == 1 && x.X is MethodDesc am && am.GetName() == "As" && am.OwningType is MetadataType ut && ut.GetName() == "Unsafe") { x = x.A[0]; continue; }
                    break;
                }
            }
        return bad;
    }

    internal void AddConstructed(TypeDesc t) => _constructed.Add(t);
    internal void AddSlot(MethodDesc slot) => Slots.Add(slot);

    internal void Freeze() => _frozen = true;

    bool _ids;

    /// <summary>Dense ids by the objects' identity, not by arrival (3.5); made when first asked for, so a build that
    /// reads no fact does not pay for the sort.</summary>
    void EnsureIds()
    {
        if (_ids) return;
        if (!_frozen) throw new InvalidOperationException("the solution's ids are asked for before the reach phase ended");
        _ids = true;
        foreach (var m in _arrival.OrderBy(m => StableKey.Of(m), StringComparer.Ordinal)) _methods.Intern(m);
        foreach (var t in _constructed.OrderBy(t => StableKey.Of(t), StringComparer.Ordinal)) _types.Intern(t);
        _constructedCount = _types.Count;
    }

    int _constructedCount;

    /// <summary>The types the program constructs, in stable order: type ids 0 to Count-1 (a type id past them is a
    /// declared type a Cone names).</summary>
    public IReadOnlyList<TypeDesc> Constructed => Types.Items.Take(_constructedCount).ToArray();
    /// <summary>The number of constructed types (type ids below it are constructed).</summary>
    public int ConstructedCount { get { EnsureIds(); return _constructedCount; } }

    /// <summary>The program can start a thread: Rt.SpawnCore is reached (6.1, 6.2). Without it every abstract object is
    /// Local and no atomic is emitted.</summary>
    public bool Threaded => _threaded ??= _arrival.Any(m => m.GetName() == "SpawnCore" && m.OwningType is MetadataType t && t.GetName() == "Rt" && t.GetNamespace() == "Il2Wasm.Runtime");
    bool? _threaded;

    /// <summary>The method's facts come from the solve (it was reached by the reach phase).</summary>
    public bool Covers(MethodDesc m) => _summaries.ContainsKey(m);
    public int MethodId(MethodDesc m) => Methods.IdOf(m);
    public MethodSummary? Summary(MethodDesc m) => _summaries.GetValueOrDefault(m);

    /// <summary>A dispatch slot's implementations over the constructed types (the dispatch group of 4.5 before receiver
    /// types narrow it), in stable order.</summary>
    public IReadOnlyList<MethodDesc> Targets(MethodDesc slot)
    {
        if (_targets.TryGetValue(slot, out var r)) return r;
        var set = new HashSet<MethodDesc>();
        // (only the constructed types the slot's owner admits can implement it: its cone)
        foreach (int i in ConeOf(slot.OwningType)) if (_host.Resolve(_all![i], slot) is { } impl) set.Add(impl);
        r = set.OrderBy(m => StableKey.Of(m), StringComparer.Ordinal).ToArray();
        _targets[slot] = r;
        return r;
    }

    readonly Dictionary<(MethodDesc, TypeDesc), MethodDesc[]> _targetsByReceiver = new();

    /// <summary>A virtual or interface call's targets: the slot's implementations over the constructed types its
    /// receiver's static type admits (all the slot's targets when the receiver's type says nothing more).</summary>
    public IReadOnlyList<MethodDesc> Targets(MethodDesc slot, TypeDesc? receiver)
    {
        if (receiver == null || receiver.IsObject || receiver == slot.OwningType || receiver.IsGenericParameter || receiver.IsValueType || receiver.IsPointer || receiver.IsByRef) return Targets(slot);
        if (_targetsByReceiver.TryGetValue((slot, receiver), out var r)) return r;
        var set = new HashSet<MethodDesc>();
        foreach (int i in ConeOf(receiver)) if (_host.Resolve(_all![i], slot) is { } impl) set.Add(impl);
        r = set.OrderBy(m => StableKey.Of(m), StringComparer.Ordinal).ToArray();
        _targetsByReceiver[(slot, receiver)] = r;
        return r;
    }

    /// <summary>A call's possible targets: the method itself for a direct call, else the slot's targets over the call's
    /// receiver types.</summary>
    public IReadOnlyList<MethodDesc> CallTargets(Node call)
    {
        if (call.X is not MethodDesc m) return [];
        if (call.Op == Opc.NewObj || (CallKind)call.Sub == CallKind.Direct) return [m];
        var rt = ReceiverTypes(call);
        if (rt.Saturated) return Targets(m);
        var set = new HashSet<MethodDesc>();
        foreach (int t in rt.Types.Items) if (_host.Resolve(Types[t], m) is { } impl) set.Add(impl);
        return set.OrderBy(x => StableKey.Of(x), StringComparer.Ordinal).ToArray();
    }

    /// <summary>A virtual or interface call's receiver types (2.6): the one type of an object made right there, else
    /// the constructed types the receiver's static type admits, saturated to Cone(declared) past the cap.</summary>
    public TypeSet ReceiverTypes(Node call)
    {
        if (call.X is not MethodDesc m || call.A.Length == 0) return TypeSet.Empty;
        var recv = call.A[0];
        if (recv.Op == Opc.NewObj && recv.X is MethodDesc ctor && Types.IdOf(ctor.OwningType) is int id and >= 0) return new TypeSet(Pool.Of(id), -1);
        var declared = recv.Type is { IsValueType: false } rtype ? rtype : m.OwningType;
        int cone = Types.Intern(declared);
        var ids = new List<int>();
        for (int i = 0; i < _constructed.Count; i++)
        {
            var t = _constructed[i];
            if (!t.CanCastTo(declared)) continue;
            ids.Add(Types.IdOf(t));
            if (ids.Count > TypeSet.Cap) return TypeSet.ConeOf(cone);
        }
        return new TypeSet(Pool.Of(ids), -1);
    }

    /// <summary>The solved facts of a method (2.8). For a method outside the solution: the top (it may write anything,
    /// throw, return, observe lifetimes; no signature).</summary>
    public MethodFacts Facts(MethodDesc m)
    {
        int id = Methods.IdOf(m);
        if (id < 0) return Unknown;
        SolveFacts();
        return _facts![id];
    }

    static readonly MethodFacts Unknown = new() { Mods = KeySet.All, ReleaseEffects = KeySet.All, NotFresh = Escape.Unknown, MayReturn = true, MayThrow = true, ObservesLifetime = true };

    /// <summary>Effects of an intrinsic call (its body is the compiler's own): the table of Memory/IntrinsicSignatures.cs
    /// once it exists; until then, any key.</summary>
    public static Func<ProgramSolution, MethodDesc, MethodDesc, KeySet> IntrinsicEffects = (_, _, _) => KeySet.All;
    /// <summary>The effects a method declares ([Il2Wasm.Runtime.DeclaredEffects]: the heap's entry points), from
    /// Memory/IntrinsicSignatures.cs; null: solve its body.</summary>
    public static Func<ProgramSolution, MethodDesc, KeySet?> DeclaredEffects = (_, _) => null;

    /// <summary>Mods, release effects, throw/return and lifetime observation over the call graph: Tarjan's strongly
    /// connected components, callees first, each component joined to its fixed point (3.3).</summary>
    void SolveFacts()
    {
        if (_facts != null) return;
        if (!_frozen) throw new InvalidOperationException("the solution is asked for facts before the reach phase ended");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int n = Methods.Count;
        // (3.6: a release runs the destroys of the value's types, the finalizers below them and the marks' hooks. Each
        // distinct set of such targets is one node of the graph (a release node, ids from n), so a method's releases
        // are a few edges to release nodes and each set's effects are joined once)
        var setIds = new Dictionary<string, int>();
        var sets = new List<int[]>();
        var setOfType = new Dictionary<TypeDesc, int>();
        int SetOf(TypeDesc t)
        {
            if (setOfType.TryGetValue(t, out int id)) return id;
            var targets = DestroyTargets(t);
            if (targets.Length == 0) id = -1;
            else
            {
                var key = string.Join(",", targets);
                if (!setIds.TryGetValue(key, out id)) { id = sets.Count; setIds[key] = id; sets.Add(targets); }
            }
            setOfType[t] = id;
            return id;
        }
        var relOf = new int[n][];
        for (int i = 0; i < n; i++)
        {
            var types = _released.GetValueOrDefault(Methods[i]) ?? [Object];
            var l = new SortedSet<int>();
            foreach (var t in types) if (SetOf(t) is int sid and >= 0) l.Add(n + sid);
            relOf[i] = [.. l];
        }
        long tRelease = clock.ElapsedMilliseconds;
        int total = n + sets.Count;
        var facts = new MethodFacts[total];
        var succ = new List<int>[total];
        // (the return-path successors: callee id, and whether its whole Mods count, it is called inside a try or is a
        // static constructor, or only its return-path ones)
        var rsucc = new List<(int Callee, bool Full)>[n];
        var local = new KeySet[n];
        var rel = new int[total][];
        for (int i = 0; i < n; i++)
        {
            var m = Methods[i];
            var s = _summaries[m];
            var f = new MethodFacts { MayReturn = s.MayReturn, MayThrow = s.MayThrow, ObservesLifetime = s.ObservesLifetime };
            var keys = new List<int>();
            bool wild = s.WildStores || s.CallsUnknown;
            foreach (var k in s.Stores) if (k.Kind != KeyKind.Var) keys.Add(Keys.Intern(k));
            foreach (var im in s.Intrinsics)
            {
                // (one that writes only through a known address here: its keys are among the stores)
                if (s.IntrinsicsByAddress.Contains(im)) continue;
                var e = IntrinsicEffects(this, m, im); wild |= e.Wild; foreach (int k in e.Keys.Items) keys.Add(k);
            }
            var callees = new List<int>();
            foreach (var c in s.Calls) Callee(c, callees, ref wild, f);
            // (a virtual call's targets over its receiver's type set, when the type sets know it)
            foreach (var (slot, recv, nid) in s.SlotCalls) foreach (var c in (nid >= 0 ? CallTargetsOf(m, nid) : null) ?? Targets(slot, recv)) Callee(c, callees, ref wild, f);
            // (the static constructors a use in the body may start run inside it: their effects are the method's)
            foreach (var t in s.Initializes) if (t.GetStaticConstructor() is { } cctor) Callee(cctor, callees, ref wild, f);
            f.Mods = wild ? KeySet.All : new KeySet(Pool.Of(keys), false);
            local[i] = f.Mods;
            {
                var inTry = _inTry.GetValueOrDefault(m);
                var rs = new List<(int, bool)>();
                void Ret(MethodDesc c, bool full)
                {
                    int j = Methods.IdOf(c);
                    if (j < 0) return; // (outside the solution: already Wild above)
                    if (!full && _summaries[c] is { MayReturn: false } && DeclaredEffects(this, c) == null) return; // (it only throws)
                    rs.Add((j, full));
                }
                foreach (var c in s.Calls) Ret(c, inTry == null || inTry.Contains(c));
                foreach (var (slot, recv, nid) in s.SlotCalls) { bool full = inTry == null || inTry.Contains(slot); foreach (var c in (nid >= 0 ? CallTargetsOf(m, nid) : null) ?? Targets(slot, recv)) Ret(c, full); }
                // (a static constructor started outside every try: if it throws, the method leaves by the exception)
                foreach (var t in s.Initializes) if (t.GetStaticConstructor() is { } cctor) Ret(cctor, inTry == null || inTry.Contains(cctor));
                rsucc[i] = rs;
            }
            // (the Mods of the destroys its releases can run, joined below from its release nodes)
            f.ReleaseEffects = KeySet.Empty;
            f.Signature = OwnershipSignature.Bottom(m);
            facts[i] = f;
            succ[i] = callees.Distinct().ToList();
            rel[i] = relOf[i];
            // (declared effects: its body is not solved; it may throw a fresh exception, releases nothing)
            if (DeclaredEffects(this, m) is { } declared) { f.Mods = declared; f.ReleaseEffects = KeySet.Empty; f.MayThrow = true; f.ObservesLifetime = false; succ[i] = []; rel[i] = []; local[i] = declared; rsucc[i] = []; }
        }
        for (int k = 0; k < sets.Count; k++)
        {
            // (a target outside the solution: anything)
            var targets = sets[k];
            facts[n + k] = new MethodFacts { Mods = KeySet.Empty, ReleaseEffects = targets[0] < 0 ? KeySet.All : KeySet.Empty };
            succ[n + k] = [];
            rel[n + k] = targets[0] < 0 ? targets[1..] : targets;
        }
        // (the components over calls and releases together: a finalizer's releases can reach back)
        var edges = new List<int>[total];
        for (int i = 0; i < total; i++) { edges[i] = [.. succ[i]]; edges[i].AddRange(rel[i]); }
        long tGraph = clock.ElapsedMilliseconds;
        foreach (var comp in Components(edges))
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (int i in comp)
                {
                    var f = facts[i];
                    foreach (int j in succ[i])
                    {
                        var g = facts[j];
                        var mods = Join(Join(f.Mods, g.Mods), g.ReleaseEffects);
                        bool thr = f.MayThrow | g.MayThrow, obs = f.ObservesLifetime | g.ObservesLifetime;
                        if (mods != f.Mods || thr != f.MayThrow || obs != f.ObservesLifetime) { f.Mods = mods; f.MayThrow = thr; f.ObservesLifetime = obs; changed = true; }
                    }
                    if (f.ReleaseEffects.Wild) continue;
                    foreach (int j in rel[i])
                    {
                        var g = facts[j];
                        var re = Join(Join(f.ReleaseEffects, g.Mods), g.ReleaseEffects);
                        if (re != f.ReleaseEffects) { f.ReleaseEffects = re; changed = true; }
                        if (re.Wild) break;
                    }
                }
            }
        _rsucc = rsucc;
        _local = local;
        FactsTime = (tRelease, tGraph - tRelease, clock.ElapsedMilliseconds - tGraph, sets.Count);
        _facts = facts;
    }

    /// <summary>The ownership signatures are in the facts (Signatures.Compute ran): the return-path releases can leave
    /// out the parameters a method only borrows. Set by the compiler after Signatures.Compute.</summary>
    public bool SignaturesComputed { get; set; }

    /// <summary>The return-path Mods (<see cref="ReturnMods"/>), solved when first asked for, after the Mods and release
    /// effects: the same call graph, callees first. A release on a returning run counts unless it is of a parameter (or
    /// a cast of one) whose convention is Lend: the caller holds such an object for the whole call, so its release in
    /// the callee frees nothing (with the signatures not computed, every parameter counts).</summary>
    void SolveReturnMods()
    {
        if (_returnMods != null) return;
        SolveFacts();
        int n = Methods.Count;
        var facts = _facts!;
        var rsucc = _rsucc!;
        var rmods = new KeySet[n];
        for (int i = 0; i < n; i++)
        {
            var m = Methods[i];
            rmods[i] = _local![i];
            if (DeclaredEffects(this, m) != null) continue;
            var types = _releasedReturning.GetValueOrDefault(m);
            if (types == null) { rmods[i] = Join(rmods[i], DestroyEffects(Object)); continue; }
            var sig = SignaturesComputed ? facts[i].Signature : null;
            foreach (var (t, arg, stat, getter) in types)
            {
                if (arg >= 0 && sig != null && arg < sig.Params.Length && sig.Params[arg] == ParamConv.Lend) continue;
                if (stat != null && Persistent(stat)) continue;
                if (getter != null && PersistentGetter(getter)) continue;
                rmods[i] = Join(rmods[i], DestroyEffects(t));
                if (rmods[i].Wild) break;
            }
        }
        var rgraph = new List<int>[n];
        for (int i = 0; i < n; i++) rgraph[i] = rsucc[i].Select(e => e.Callee).Distinct().ToList();
        foreach (var comp in Components(rgraph))
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (int i in comp)
                {
                    if (rmods[i].Wild) continue;
                    foreach (var (j, full) in rsucc[i])
                    {
                        // (a callee called inside a try: its whole Mods and release effects; else what it does on
                        // its returning runs, its own releases there included)
                        var x = full ? Join(Join(rmods[i], facts[j].Mods), facts[j].ReleaseEffects) : Join(rmods[i], rmods[j]);
                        if (x != rmods[i]) { rmods[i] = x; changed = true; }
                        if (x.Wild) break;
                    }
                }
            }
        _returnMods = rmods;
    }

    // ------------------------------------------------------------------ 3.6: destroy effects per type

    TypeDesc Object => _object ??= (_arrival.Count > 0 ? _arrival[0].Context : _constructed[0].Context).GetWellKnownType(WellKnownType.Object);
    TypeDesc? _object;
    List<TypeDesc>? _carriers;
    readonly Dictionary<TypeDesc, int[]> _destroyTargets = new();
    int _onDestroy = -2, _onFinalize = -2;
    internal string DestroyReport => $"{_carriers?.Count} constructed types can run a finalizer at their destroy; weak marks {(_onDestroy == -2 ? "not reached" : _onDestroy < 0 || _onFinalize < 0 ? "reached, their hooks outside the solution (anything)" : "reached")}";

    /// <summary>What a release of a value of a static type can write (3.6): the Mods and release effects of the
    /// finalizers its destroy can run (the value's own types' and those below them through counted fields and
    /// elements, over the constructed types) and, once a weak mark can be set (ObjectMarks.SetAt reached), of
    /// ObjectMarks.OnDestroy (every destroy) and OnFinalize (a finalizable one). Freeing writes no live object's field,
    /// so a type with none of these has no effect. Anything when one of them is outside the solution.</summary>
    /// <summary>IL2WASM_MEM_WHY=text (diagnosis): for the methods whose name contains it, why their Mods are Wild: the
    /// shortest chain of calls, static constructors and release targets to a method that is Wild by itself, and that
    /// method's own reason.</summary>
    internal string WhyWild(MethodDesc root)
    {
        if (!Facts(root).Mods.Wild) return "not Wild";
        var reach = (_released.GetValueOrDefault(root) ?? [Object]).Where(t => DestroyTargets(t).Any(j => j != _onDestroy && j != _onFinalize)).Select(t => t.ToString()).ToList();
        if (reach.Count > 0) Console.Error.WriteLine("mem: why: released types of " + root + " whose destroy can run a finalizer: " + string.Join(", ", reach));
        var parent = new Dictionary<MethodDesc, MethodDesc?> { [root] = null };
        var q = new Queue<MethodDesc>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var m = q.Dequeue();
            string? why = null;
            var dcl = DeclaredEffects(this, m);
            if (dcl is { Wild: false }) continue;
            _summaries.TryGetValue(m, out var s);
            if (dcl != null) why = "declared Wild";
            else if (s == null) why = "outside the solution";
            else if (s.WildStores) why = "a store through a raw address";
            else if (s.CallsUnknown) why = "an indirect call (calli, a delegate's Invoke)";
            else foreach (var im in s.Intrinsics) if (IntrinsicEffects(this, m, im).Wild) { why = "the intrinsic " + im; break; }
            if (why == null) foreach (var c in s!.Calls) if (MethodId(c) < 0) { why = "a call outside the solution: " + c; break; }
            if (why != null)
            {
                var chain = new List<string>();
                for (MethodDesc? x = m; x != null; x = parent[x]) chain.Add(x.ToString()!);
                chain.Reverse();
                return why + ": " + string.Join(" -> ", chain);
            }
            var next = s!.Calls.Concat(s.SlotCalls.SelectMany(x => (x.NodeId >= 0 ? CallTargetsOf(m, x.NodeId) : null) ?? Targets(x.Slot, x.Receiver))).Concat(s.Initializes.Select(t => t.GetStaticConstructor()).Where(c => c != null)!).ToList();
            foreach (var c in next) if (c != null && MethodId(c) >= 0 && Facts(c).Mods.Wild | Facts(c).ReleaseEffects.Wild && parent.TryAdd(c, m)) q.Enqueue(c);
            if (_released.GetValueOrDefault(m) is var types)
                foreach (var t in types ?? [Object]) foreach (int j in DestroyTargets(t)) if (j >= 0 && parent.TryAdd(Methods[j], m)) q.Enqueue(Methods[j]);
        }
        return "no Wild cause found";
    }

    /// <summary>IL2WASM_MEM_FACTS_DUMP=file: every method's solved facts, one line each in method id order, appended per
    /// solve (to compare two builds of the solver: the solution must not change).</summary>
    internal void DumpFacts(string file)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# solve of ").Append(Methods.Count).Append(" methods\n");
        string Keys(KeySet k) => k.Wild ? "*" : string.Join(",", k.Keys.Items.ToArray().Select(i => this.Keys[i].ToString()).OrderBy(x => x, StringComparer.Ordinal));
        for (int i = 0; i < Methods.Count; i++)
        {
            var f = Facts(Methods[i]);
            sb.Append(StableKey.Of(Methods[i])).Append('\t').Append(Keys(f.Mods)).Append('\t').Append(Keys(f.ReleaseEffects)).Append('\t').Append(f.MayThrow ? 'T' : '-').Append(f.ObservesLifetime ? 'O' : '-').Append('\n');
        }
        System.IO.File.AppendAllText(file, sb.ToString());
    }

    /// <summary>IL2WASM_DESTROY_TRACE=1: why a destroy's effects are Wild.</summary>
    static readonly bool s_destroyTrace = Environment.GetEnvironmentVariable("IL2WASM_DESTROY_TRACE") == "1";

    public KeySet DestroyEffects(TypeDesc? t)
    {
        SolveFacts();
        var e = KeySet.Empty;
        foreach (int j in DestroyTargets(t ?? Object))
        {
            if (j < 0) { if (s_destroyTrace) Console.Error.WriteLine($"mem: destroy of {t}: a target outside the solution (targets {string.Join(",", DestroyTargets(t ?? Object))}; marks {_onDestroy}/{_onFinalize}; finalizers {string.Join(",", _finIds!)})"); return KeySet.All; }
            e = e.Join(_facts![j].Mods, Pool).Join(_facts[j].ReleaseEffects, Pool);
            if (e.Wild) { if (s_destroyTrace) Console.Error.WriteLine($"mem: destroy of {t}: Wild by {Methods[j]} (Mods {(_facts[j].Mods.Wild ? "Wild" : "known")}, release effects {(_facts[j].ReleaseEffects.Wild ? "Wild" : "known")})"); break; }
        }
        return e;
    }

    static MethodDesc? FinalizerOf(TypeDesc x) =>
        x is DefType { IsValueType: false, IsInterface: false } && !x.IsArray && !x.IsString && !x.IsObject ? x.GetFinalizer() : null;

    /// <summary>The method ids a destroy of a value of the type can run, sorted; -1 for one outside the solution.</summary>
    int[] DestroyTargets(TypeDesc t)
    {
        if (_destroyTargets.TryGetValue(t, out var r)) return r;
        if (_val == null) PrepareDestroys();
        var bits = Below(t);
        var set = new SortedSet<int>();
        for (int i = 0; i < _finIds!.Length; i++) if ((bits[i >> 6] & (1UL << (i & 63))) != 0) set.Add(_finIds[i]);
        if (_onDestroy != -2)
        {
            set.Add(_onDestroy);
            if (set.Count > 1 || set.Contains(-1)) set.Add(_onFinalize);
        }
        r = [.. set];
        _destroyTargets[t] = r;
        return r;
    }

    // The finalizers below each type, as bit sets over the program's finalizers (bit i is _finIds[i]): _val per
    // constructed type (index in _all), a least fixed point; Below(t) for any static type, cached once the point is
    // reached. Cones (the constructed types a static type admits) come from an index of base classes for a class, the
    // whole set for object, CanCastTo for the rest (interfaces, arrays).
    int[]? _finIds;
    int _words;
    ulong[][]? _val;
    TypeDesc[]? _all;
    Dictionary<TypeDesc, List<int>>? _byBase;
    readonly Dictionary<TypeDesc, int[]> _cones = new();
    readonly Dictionary<TypeDesc, ulong[]> _below = new();
    bool _belowFinal;

    /// <summary>The constructed types (indices in _all) a static type admits.</summary>
    int[] ConeOf(TypeDesc t)
    {
        if (_cones.TryGetValue(t, out var r)) return r;
        EnsureIndex();
        if (t.IsObject) r = Enumerable.Range(0, _all!.Length).ToArray();
        // (a generic delegate type can be variant: CanCastTo below)
        else if (t is DefType { IsInterface: false } && !t.IsArray && !(t.IsDelegate && t.HasInstantiation)) r = _byBase!.TryGetValue(t, out var l) ? [.. l] : [];
        else r = Enumerable.Range(0, _all!.Length).Where(i => _all[i].CanCastTo(t)).ToArray();
        _cones[t] = r;
        return r;
    }

    /// <summary>The finalizers a destroy of a value of static type t can reach: a reference's, those of every constructed
    /// type it admits; a struct's, its counted fields'.</summary>
    ulong[] Below(TypeDesc t)
    {
        if (_belowFinal && _below.TryGetValue(t, out var known)) return known;
        var acc = new ulong[_words];
        BelowInto(t, acc, []);
        if (_belowFinal) _below[t] = acc;
        return acc;
    }

    void BelowInto(TypeDesc t, ulong[] acc, HashSet<TypeDesc> seen)
    {
        if (!seen.Add(t)) return;
        if (t.IsValueType)
        {
            if (t is DefType dt && Il2Wasm.IL.Ir.Kinds.Counted(t))
                foreach (var f in dt.GetFields()) if (!f.IsStatic && Il2Wasm.IL.Ir.Kinds.Counted(f.FieldType)) BelowInto(f.FieldType, acc, seen);
            return;
        }
        if (t.IsPointer || t.IsByRef || t.IsFunctionPointer) return;
        if (_belowFinal && _below.TryGetValue(t, out var k)) { for (int w = 0; w < _words; w++) acc[w] |= k[w]; return; }
        foreach (int x in ConeOf(t)) { var v = _val![x]; for (int w = 0; w < _words; w++) acc[w] |= v[w]; }
    }

    /// <summary>The constructed types in id order, and the index of their base classes (for the cones).</summary>
    void EnsureIndex()
    {
        if (_all != null) return;
        var all = _all = [.. Constructed];
        _byBase = new Dictionary<TypeDesc, List<int>>();
        for (int i = 0; i < all.Length; i++)
            for (var b = all[i]; b != null; b = b.BaseType)
            {
                if (!_byBase.TryGetValue(b, out var l)) _byBase[b] = l = [];
                l.Add(i);
            }
    }

    /// <summary>Per constructed type, the finalizers its destroy can run: its own, and those of what its counted fields
    /// and elements can hold (a least fixed point over the constructed types).</summary>
    void PrepareDestroys()
    {
        bool IsMarks(MethodDesc m, string name) => m.GetName() == name && m.OwningType is MetadataType mt && mt.GetName() == "ObjectMarks" && mt.GetNamespace() == "Il2Wasm.Runtime";
        if (Methods.Items.Any(m => IsMarks(m, "SetAt")))
        {
            // (Lower/Finalizers.cs: every destroy calls OnDestroy once a mark can be set, a finalizable one OnFinalize)
            _onDestroy = Methods.Items.FirstOrDefault(m => IsMarks(m, "OnDestroy")) is { } od ? MethodId(od) : -1;
            _onFinalize = Methods.Items.FirstOrDefault(m => IsMarks(m, "OnFinalize")) is { } of ? MethodId(of) : -1;
        }
        EnsureIndex();
        var all = _all!;
        var own = new int[all.Length];
        var ids = new SortedSet<int>();
        for (int i = 0; i < all.Length; i++)
        {
            own[i] = FinalizerOf(all[i]) is { } fin ? (MethodId(fin) is int id and >= 0 ? id : -1) : -2;
            if (own[i] != -2) ids.Add(own[i]);
        }
        _finIds = [.. ids];
        _words = Math.Max(1, (_finIds.Length + 63) / 64);
        var bit = new Dictionary<int, int>();
        for (int i = 0; i < _finIds.Length; i++) bit[_finIds[i]] = i;
        _val = new ulong[all.Length][];
        for (int i = 0; i < all.Length; i++)
        {
            _val[i] = new ulong[_words];
            if (own[i] != -2) { int k = bit[own[i]]; _val[i][k >> 6] |= 1UL << (k & 63); }
        }
        if (_finIds.Length > 0)
        {
            var children = new TypeDesc[all.Length][];
            for (int i = 0; i < all.Length; i++) children[i] = ChildTypes(all[i]).Distinct().ToArray();
            var acc = new ulong[_words];
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int i = 0; i < all.Length; i++)
                {
                    if (children[i].Length == 0) continue;
                    Array.Clear(acc);
                    var seen = new HashSet<TypeDesc>();
                    foreach (var c in children[i]) BelowInto(c, acc, seen);
                    var v = _val[i];
                    for (int w = 0; w < _words; w++)
                    {
                        ulong nv = v[w] | acc[w];
                        if (nv != v[w]) { v[w] = nv; changed = true; }
                    }
                }
            }
        }
        _belowFinal = true;
        _carriers = Enumerable.Range(0, all.Length).Where(i => _val[i].Any(w => w != 0)).Select(i => all[i]).ToList();
    }

    /// <summary>A constructed type's counted children: an array's element type, a class's instance fields (its base
    /// types' too).</summary>
    static IEnumerable<TypeDesc> ChildTypes(TypeDesc x)
    {
        if (x is ArrayType at) { if (Il2Wasm.IL.Ir.Kinds.Counted(at.ElementType)) yield return at.ElementType; yield break; }
        for (var t = x as DefType; t != null; t = t.BaseType)
            foreach (var f in t.GetFields())
                if (!f.IsStatic && !f.FieldType.IsPointer && !f.FieldType.IsByRef && !f.FieldType.IsFunctionPointer && !f.FieldType.IsGenericParameter && Il2Wasm.IL.Ir.Kinds.Counted(f.FieldType)) yield return f.FieldType;
    }

    /// <summary>The join, with Wild absorbing (a wild set's keys say nothing more).</summary>
    KeySet Join(KeySet a, KeySet b) => a.Wild || b.Wild ? KeySet.All : b.Keys.IsEmpty ? a : a.Join(b, Pool);

    /// <summary>The facts' solve time (IL2WASM_MEM_TRACE): release targets, graph, fixed point (ms), release nodes.</summary>
    internal (long Release, long Graph, long Solve, int ReleaseNodes) FactsTime;

    void Callee(MethodDesc c, List<int> callees, ref bool wild, MethodFacts f)
    {
        int j = Methods.IdOf(c);
        if (j >= 0) { callees.Add(j); return; }
        // (a callee outside the solution: an import, a P/Invoke, a late import, a runtime helper)
        wild = true; f.MayThrow = true; f.ObservesLifetime = true;
    }

    /// <summary>Tarjan's strongly connected components, each component after every component it reaches (callees
    /// first); iterative, in id order.</summary>
    static List<int[]> Components(List<int>[] succ)
    {
        int n = succ.Length, index = 0;
        var idx = new int[n]; var low = new int[n]; var on = new bool[n];
        Array.Fill(idx, -1);
        var stack = new Stack<int>();
        var result = new List<int[]>();
        var call = new Stack<(int v, int i)>();
        for (int root = 0; root < n; root++)
        {
            if (idx[root] >= 0) continue;
            call.Push((root, 0));
            while (call.Count > 0)
            {
                var (v, i) = call.Pop();
                if (i == 0) { idx[v] = low[v] = index++; stack.Push(v); on[v] = true; }
                if (i > 0) { int w0 = succ[v][i - 1]; if (on[w0]) low[v] = Math.Min(low[v], low[w0]); }
                bool pushed = false;
                for (; i < succ[v].Count; i++)
                {
                    int w = succ[v][i];
                    if (idx[w] < 0) { call.Push((v, i + 1)); call.Push((w, 0)); pushed = true; break; }
                    if (on[w]) low[v] = Math.Min(low[v], idx[w]);
                }
                if (pushed) continue;
                if (low[v] == idx[v])
                {
                    var comp = new List<int>();
                    int w;
                    do { w = stack.Pop(); on[w] = false; comp.Add(w); } while (w != v);
                    comp.Sort();
                    result.Add(comp.ToArray());
                }
            }
        }
        return result;
    }
}

/// <summary>What the rounds of one program's compilation share (they share its type system context): how many solves
/// ran, and whether a dependent handle's setter was ever reached or imported late.</summary>
sealed class ProgramState
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ProgramState> s_of = new();
    static readonly ProgramState s_none = new();
    public int Solves;
    public bool DependentsSeen;

    public static ProgramState Of(TypeSystemContext? ctx) { if (ctx == null) return s_none; lock (s_of) return s_of.GetValue(ctx, _ => new ProgramState()); }

    /// <summary>WeakSlots.LinkDependent or ExchangeDependent: the only ways a dependent comes to exist.</summary>
    public static bool IsDependentSetter(MethodDesc m) => m.GetName() is "LinkDependent" or "ExchangeDependent"
        && m.OwningType is MetadataType t && t.GetName() == "WeakSlots" && t.GetNamespace() == "Il2Wasm.Runtime";
}
