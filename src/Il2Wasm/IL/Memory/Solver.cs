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

    public void Root(MethodDesc m)
    {
        if (_units.ContainsKey(m)) return;
        _units[m] = null;
        _order.Add(m);
        _work.Enqueue(m);
    }

    /// <summary>Reaches the program from the roots given: imports every method reached, to a fixed point.</summary>
    public void Reach()
    {
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
            if (!Dispatch()) break;
        }
        if (s_trace) Console.Error.WriteLine($"mem: reached {_units.Count} methods ({_units.Count(u => u.Value?.Summary != null)} imported), {_slots.Count} slots, {_constructed.Count} constructed types; {_clock.ElapsedMilliseconds} ms, live heap {(s_heap ? GC.GetTotalMemory(true) / (1 << 20) : -1)} MB, peak {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
    }

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

    /// <summary>A method lowering reached and imported itself.</summary>
    public void Late(ImportedMethod u) { LateImports++; LateMethods.Add(u.Method); _late.Add(u.Method); }
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
        foreach (var m in _order) if (_units[m] is { Summary: { } s } u) sol.AddMethod(m, s, u.Body is { } b ? ProgramSolution.ReleasedTypes(b) : u.Body == null && (u.Il == null || u.Substituted) ? [] : null);
        foreach (var t in _constructed) sol.AddConstructed(t);
        foreach (var slot in _slots) sol.AddSlot(slot);
        sol.Freeze();
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
        if (s_trace) Console.Error.WriteLine($"mem: lowering took {_taken.Count} reached methods, imported {LateImports} late; {Unused} reached methods never lowered; {_clock.ElapsedMilliseconds} ms since the reach phase began, live heap {(s_heap ? GC.GetTotalMemory(true) / (1 << 20) : -1)} MB, peak {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
    }
}

/// <summary>The program's solution (section 3; the successor of RoundFacts as the facts a plan reads). Method ids are
/// dense and in a stable order (by the method's printed identity, 3.5); the may-facts are solved when first asked for
/// (Mods and release effects over the call graph's strongly connected components, callees first), so a build that
/// asks for none pays nothing. Facts for a method outside the solution (a late import) are the top: unknown.</summary>
public sealed class ProgramSolution
{
    readonly IReachHost _host;
    /// <summary>A covered method's body before lowering takes it (Signatures.Compute reads it; null once taken or when there is none).</summary>
    public Func<MethodDesc, Il2Wasm.IL.Ir.Body?> BodyOf { get; internal set; } = _ => null;
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

    internal void AddMethod(MethodDesc m, MethodSummary s, TypeDesc[]? released = null) { _arrival.Add(m); _summaries[m] = s; _released[m] = released; }

    /// <summary>The static types of the counted values a method's body may release (3.6): its counted values, and the
    /// old values of the counted slots it stores. Null: not known (any type).</summary>
    readonly Dictionary<MethodDesc, TypeDesc[]?> _released = new();

    /// <summary>The types a body may release: every counted value (a reference: its static type, object when the IR has
    /// none; a struct with references) and every counted slot it stores (the old value goes).</summary>
    internal static TypeDesc[] ReleasedTypes(Il2Wasm.IL.Ir.Body body)
    {
        var set = new HashSet<TypeDesc>();
        var obj = body.Method.Context.GetWellKnownType(WellKnownType.Object);
        void Add(TypeDesc? t, Il2Wasm.IL.Ir.VK kind)
        {
            if (kind == Il2Wasm.IL.Ir.VK.Ref) set.Add(t is { IsValueType: false } ? t : obj);
            else if (kind == Il2Wasm.IL.Ir.VK.Struct && t != null && Il2Wasm.IL.Ir.Kinds.Counted(t)) set.Add(t);
        }
        void Slot(TypeDesc? t) { if (t != null && !t.IsPointer && !t.IsByRef && !t.IsFunctionPointer && !t.IsGenericParameter) Add(t, Il2Wasm.IL.Ir.Kinds.Of(t)); }
        foreach (var b in body.Blocks)
            foreach (var n in b.Phis.Concat(b.Code))
            {
                if (n.Op is Il2Wasm.IL.Ir.Opc.LdNull or Il2Wasm.IL.Ir.Opc.LdStr) continue;
                Add(n.Type, n.Kind);
                switch (n.Op)
                {
                    case Il2Wasm.IL.Ir.Opc.StFld or Il2Wasm.IL.Ir.Opc.StSFld when n.X is FieldDesc f: Slot(f.FieldType); break;
                    case Il2Wasm.IL.Ir.Opc.StElem: Slot(n.A[0].Type is ArrayType at ? at.ElementType : n.A[2].Type); break;
                    case Il2Wasm.IL.Ir.Opc.VarSt: Slot(body.Vars[(int)n.I].Type); break;
                    case Il2Wasm.IL.Ir.Opc.StInd or Il2Wasm.IL.Ir.Opc.InitObj: Slot(n.X as TypeDesc ?? n.A.ElementAtOrDefault(1)?.Type ?? obj); break;
                    case Il2Wasm.IL.Ir.Opc.CpBlk: set.Add(obj); break;
                }
            }
        return set.OrderBy(t => StableKey.Of(t), StringComparer.Ordinal).ToArray();
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
        for (int i = 0, k = ConstructedCount; i < k; i++) if (_host.Resolve(Types[i], slot) is { } impl) set.Add(impl);
        r = set.OrderBy(m => StableKey.Of(m), StringComparer.Ordinal).ToArray();
        _targets[slot] = r;
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
    public static Func<ProgramSolution, MethodDesc, KeySet> IntrinsicEffects = (_, _) => KeySet.All;

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
        var rel = new int[total][];
        for (int i = 0; i < n; i++)
        {
            var m = Methods[i];
            var s = _summaries[m];
            var f = new MethodFacts { MayReturn = s.MayReturn, MayThrow = s.MayThrow, ObservesLifetime = s.ObservesLifetime };
            var keys = new List<int>();
            bool wild = s.WildStores || s.CallsUnknown;
            foreach (var k in s.Stores) if (k.Kind != KeyKind.Var) keys.Add(Keys.Intern(k));
            foreach (var im in s.Intrinsics) { var e = IntrinsicEffects(this, im); wild |= e.Wild; foreach (int k in e.Keys.Items) keys.Add(k); }
            var callees = new List<int>();
            foreach (var c in s.Calls) Callee(c, callees, ref wild, f);
            foreach (var slot in s.Slots) foreach (var c in Targets(slot)) Callee(c, callees, ref wild, f);
            // (the static constructors a use in the body may start run inside it: their effects are the method's)
            foreach (var t in s.Initializes) if (t.GetStaticConstructor() is { } cctor) Callee(cctor, callees, ref wild, f);
            f.Mods = wild ? KeySet.All : new KeySet(Pool.Of(keys), false);
            // (the Mods of the destroys its releases can run, joined below from its release nodes)
            f.ReleaseEffects = KeySet.Empty;
            f.Signature = OwnershipSignature.Bottom(m);
            facts[i] = f;
            succ[i] = callees.Distinct().ToList();
            rel[i] = relOf[i];
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
        FactsTime = (tRelease, tGraph - tRelease, clock.ElapsedMilliseconds - tGraph, sets.Count);
        _facts = facts;
    }

    // ------------------------------------------------------------------ 3.6: destroy effects per type

    TypeDesc Object => _object ??= (_arrival.Count > 0 ? _arrival[0].Context : _constructed[0].Context).GetWellKnownType(WellKnownType.Object);
    TypeDesc? _object;
    Dictionary<TypeDesc, SortedSet<int>>? _fins;
    List<TypeDesc>? _carriers;
    readonly Dictionary<TypeDesc, int[]> _destroyTargets = new();
    int _onDestroy = -2, _onFinalize = -2;
    internal string DestroyReport => $"{_carriers?.Count} constructed types can run a finalizer at their destroy; weak marks {(_onDestroy == -2 ? "not reached" : _onDestroy < 0 || _onFinalize < 0 ? "reached, their hooks outside the solution (anything)" : "reached")}";

    /// <summary>What a release of a value of a static type can write (3.6): the Mods and release effects of the
    /// finalizers its destroy can run (the value's own types' and those below them through counted fields and
    /// elements, over the constructed types) and, once a weak mark can be set (ObjectMarks.SetAt reached), of
    /// ObjectMarks.OnDestroy (every destroy) and OnFinalize (a finalizable one). Freeing writes no live object's field,
    /// so a type with none of these has no effect. Anything when one of them is outside the solution.</summary>
    public KeySet DestroyEffects(TypeDesc? t)
    {
        SolveFacts();
        var e = KeySet.Empty;
        foreach (int j in DestroyTargets(t ?? Object))
        {
            if (j < 0) return KeySet.All;
            e = e.Join(_facts![j].Mods, Pool).Join(_facts[j].ReleaseEffects, Pool);
            if (e.Wild) break;
        }
        return e;
    }

    static MethodDesc? FinalizerOf(TypeDesc x) =>
        x is DefType { IsValueType: false, IsInterface: false } && !x.IsArray && !x.IsString && !x.IsObject ? x.GetFinalizer() : null;

    /// <summary>The method ids a destroy of a value of the type can run, sorted; -1 for one outside the solution.</summary>
    int[] DestroyTargets(TypeDesc t)
    {
        if (_destroyTargets.TryGetValue(t, out var r)) return r;
        if (_fins == null) PrepareDestroys();
        var set = new SortedSet<int>();
        Below(t, set, []);
        if (_onDestroy != -2)
        {
            set.Add(_onDestroy);
            if (set.Count > 1 || set.Contains(-1)) set.Add(_onFinalize);
        }
        r = [.. set];
        _destroyTargets[t] = r;
        return r;
    }

    /// <summary>The finalizers the destroy of a value of type t can reach: a reference's, every constructed carrier the
    /// type admits; a struct's, its counted fields'.</summary>
    void Below(TypeDesc t, SortedSet<int> set, HashSet<TypeDesc> seen)
    {
        if (!seen.Add(t)) return;
        if (t.IsValueType)
        {
            if (t is DefType dt && Il2Wasm.IL.Ir.Kinds.Counted(t))
                foreach (var f in dt.GetFields()) if (!f.IsStatic && Il2Wasm.IL.Ir.Kinds.Counted(f.FieldType)) Below(f.FieldType, set, seen);
            return;
        }
        if (t.IsPointer || t.IsByRef || t.IsFunctionPointer) return;
        foreach (var x in ConeOf(t)) if (_fins![x].Count > 0) set.UnionWith(_fins[x]);
    }

    readonly Dictionary<TypeDesc, TypeDesc[]> _cones = new();
    TypeDesc[]? _constructedArr;

    /// <summary>The constructed types a static type admits (each asked once: the destroys' fixed point asks again).</summary>
    TypeDesc[] ConeOf(TypeDesc t)
    {
        if (_cones.TryGetValue(t, out var r)) return r;
        r = (_constructedArr ??= [.. Constructed]).Where(x => x.CanCastTo(t)).ToArray();
        _cones[t] = r;
        return r;
    }

    /// <summary>Per constructed type, the finalizers its destroy can run: its own, and those of what its counted fields
    /// and elements can hold (a fixed point over the constructed types); the carriers are the types with any.</summary>
    void PrepareDestroys()
    {
        bool IsMarks(MethodDesc m, string name) => m.GetName() == name && m.OwningType is MetadataType mt && mt.GetName() == "ObjectMarks" && mt.GetNamespace() == "Il2Wasm.Runtime";
        if (Methods.Items.Any(m => IsMarks(m, "SetAt")))
        {
            // (Lower/Finalizers.cs: every destroy calls OnDestroy once a mark can be set, a finalizable one OnFinalize)
            _onDestroy = Methods.Items.FirstOrDefault(m => IsMarks(m, "OnDestroy")) is { } od ? MethodId(od) : -1;
            _onFinalize = Methods.Items.FirstOrDefault(m => IsMarks(m, "OnFinalize")) is { } of ? MethodId(of) : -1;
        }
        var fins = new Dictionary<TypeDesc, SortedSet<int>>();
        var all = Constructed;
        foreach (var x in all)
        {
            var s = new SortedSet<int>();
            if (FinalizerOf(x) is { } fin) s.Add(MethodId(fin) is int id and >= 0 ? id : -1);
            fins[x] = s;
        }
        _fins = fins;
        for (bool changed = true; changed;)
        {
            changed = false;
            _carriers = all.Where(x => fins[x].Count > 0).ToList();
            if (_carriers.Count == 0) break;
            foreach (var x in all)
            {
                var s = fins[x];
                int before = s.Count;
                var seen = new HashSet<TypeDesc>();
                foreach (var c in ChildTypes(x)) Below(c, s, seen);
                if (s.Count != before) changed = true;
            }
        }
        _carriers ??= [];
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
