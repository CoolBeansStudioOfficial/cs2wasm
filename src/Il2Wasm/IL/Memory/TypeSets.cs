#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>Per-key TypeSets (docs/MEMORY-IR.md 2.6): the exact constructed types a value, a parameter, a method's
/// result, an instance field, a static, an array element or a ref slot can hold, as a flow-insensitive,
/// context-insensitive least fixed point over the bodies the reach phase holds. A set past <see cref="Cap"/> types, or
/// one the solve cannot see all the sources of, is Top: unknown, read as today's cone of the static type. Every set only
/// grows, so the result does not depend on the order methods are visited in (3.1, 3.5).
/// <para>Open (Top) by rule, because stores the IR does not show may write them:</para>
/// <list type="bullet">
/// <item>the parameters of a method something outside the IR can call: a root of the reach phase (the entry, the
/// exports, the methods the generated code calls, seeded from the round before), a method whose address is taken
/// (ldftn, ldvirtftn: delegates, function pointers), a finalizer;</item>
/// <item>the result of a call to a method outside the solution, of an intrinsic other than the few it knows, of calli;</item>
/// <item>the fields of delegate types (lowering writes them at a delegate's construction);</item>
/// <item>every ref slot when the program stores a reference through a raw pointer, and every key the slot keys alias
/// (a ref T aliases the fields, statics and elements of a type T may be; Unsafe.As makes every reference type alias).</item>
/// </list>
/// <para>Reads see the keys a write may have reached (an element key: every element and slot key of a type that may
/// alias; a field or static: the slot keys) and keep the types the read's static type admits (static types are
/// truthful, as the cones assume). Used from round 1 on (ProgramState.Solves > 0): a round whose lowering imports a
/// method late (its stores unseen) needs another round (Solver.NeedsAnotherRound). IL2WASM_NO_TYPESETS=1: off.</para></summary>
public sealed partial class ProgramSolution
{
    public const int Cap = TypeSet.Cap;
    static readonly bool s_noTypeSets = Environment.GetEnvironmentVariable("IL2WASM_NO_TYPESETS") == "1";
    static readonly bool s_typeSetsTrace = Environment.GetEnvironmentVariable("IL2WASM_TYPESETS_TRACE") == "1";

    /// <summary>The type sets were solved for this round (from round 1 on, and not switched off).</summary>
    public bool TypeSetsSolved { get; private set; }
    internal (long Ms, int Methods, int Evaluations, int Vars, int TopVars) TypeSetsTime;

    // a set: null is Top (unknown); otherwise the constructed type indices (into _all), sorted. By (method, Node.Id):
    // no body is kept alive by them
    readonly Dictionary<(MethodDesc, int), IdSet?> _nodeTypes = new();
    // a virtual call's or ldvirtftn's targets over its receiver's set, by (method, Node.Id)
    readonly Dictionary<(MethodDesc, int), MethodDesc[]> _callTargets = new();

    /// <summary>The exact types a value of a method's body can have (constructed types, in stable order), or null when
    /// not known (Top, or the sets were not solved): then the value's static type's cone is all one can say.</summary>
    public IEnumerable<TypeDesc>? ExactTypes(MethodDesc m, Node n)
    {
        if (!TypeSetsSolved || !_nodeTypes.TryGetValue((m, n.Id), out var s) || s is not { } set) return null;
        return set.Items.ToArray().Select(i => _all![i]);
    }

    /// <summary>A virtual call's targets over its receiver's types (null: not solved; use the slot's).</summary>
    public IReadOnlyList<MethodDesc>? CallTargetsOf(MethodDesc m, int nodeId) => TypeSetsSolved && _callTargets.TryGetValue((m, nodeId), out var r) ? r : null;

    // ------------------------------------------------------------------ the solve

    /// <summary>The solve's variables: a parameter's or a result's types, a key's types, and for references (byrefs) a
    /// parameter's or a result's address set: the keys (dense ids) it may point at.</summary>
    enum VarKind : byte { Param, Ret, Key, AddrParam, AddrRet }
    readonly record struct Var(VarKind Kind, MethodDesc? Method, int Index, MemKey Key);

    sealed class State
    {
        public readonly Dictionary<Var, int> Ids = new();
        public readonly List<Var> Vars = [];
        public readonly List<IdSet?> Sets = [];
        public readonly List<HashSet<MethodDesc>> Readers = [];
        /// <summary>The slot-like keys (Slot, Elem) written so far, and the read keys registered, for aliasing.</summary>
        public readonly List<int> SlotLike = [];
        public readonly HashSet<int> ReadKeys = [];
        public bool RawRefStore;
    }

    State? _ts;
    HashSet<MethodDesc>? _externalParams;

    /// <summary>Solves the type sets over the held bodies (Solver.Solve, before the released types are taken).</summary>
    internal void SolveTypeSets(IReadOnlyCollection<MethodDesc> externalRoots)
    {
        if (s_noTypeSets) return;
        // (IL2WASM_TYPESETS_REPEAT=n: the solve n times over, for profiling it)
        if (int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_TYPESETS_REPEAT"), out int repeat))
            for (int i = 1; i < repeat; i++) { SolveTypeSetsOnce(externalRoots); ResetTypeSets(); }
        SolveTypeSetsOnce(externalRoots);
    }

    void ResetTypeSets()
    {
        _nodeTypes.Clear(); _callTargets.Clear(); _methodStates.Clear(); _aliasesOf.Clear(); _aliasesSeen = 0;
        _tBuild = _tValue = _tTargets = _tLoop = _tSinks = _tStore = 0;
        TypeSetsSolved = false;
    }

    void SolveTypeSetsOnce(IReadOnlyCollection<MethodDesc> externalRoots)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        EnsureIndex();
        var st = _ts = new State();
        var bodies = new Dictionary<MethodDesc, Body>();
        foreach (var m in Methods.Items) if (HeldBody(m) is { } b) bodies[m] = b;
        // the methods something outside the IR may call
        // (a root no IR edge reaches: the entry, an export, a seed only generated code calls. A seed the IR reaches too
        // is called by generated code only if it is one of the kinds below: lowering's own calls are to Il2Wasm.Runtime's
        // methods, finalizers, constructors (an allocation's this: seeded exact for every constructor below), attribute
        // constructors (from the metadata's thunks), Type.FromId (an int) and argument-less throw helpers)
        var irCallees = new HashSet<MethodDesc>();
        foreach (var m in Methods.Items)
            if (Summary(m) is { } su)
            {
                foreach (var c in su.Calls) irCallees.Add(c);
                foreach (var (slot, recv, _) in su.SlotCalls) foreach (var c in Targets(slot, recv)) irCallees.Add(c);
                foreach (var t in su.Initializes) if (t.GetStaticConstructor() is { } cc) irCallees.Add(cc);
            }
        var ext = _externalParams = [.. externalRoots.Where(r => !irCallees.Contains(r))];
        foreach (var m in Methods.Items)
            if (m.OwningType is MetadataType ot && (ot.GetNamespace() == "Il2Wasm.Runtime" || m.IsConstructor && DerivesFrom(ot, "System", "Attribute"))) ext.Add(m);
        foreach (var x in _all!) if (FinalizerOf(x) is { } fin) ext.Add(fin);
        foreach (var b in bodies.Values)
            foreach (var bl in b.Blocks)
                foreach (var n in bl.Code)
                {
                    if (n.Op == Opc.LdFtn && n.X is MethodDesc fm) { ext.Add(fm); if (s_typeSetsTrace && Environment.GetEnvironmentVariable("IL2WASM_TYPESETS_SHOW") is { } sh && fm.ToString()!.Contains(sh)) Console.Error.WriteLine("typesets: ldftn " + fm + " in " + b.Method); }
                    else if (n.Op == Opc.LdVirtFtn && n.X is MethodDesc vm) { ext.Add(vm); foreach (var t in Targets(vm)) ext.Add(t); }
                    else if (n.Op == Opc.StInd && n.A.Length > 1 && n.A[1].Kind == VK.Ref && AddressKeyOf(n.A[0]) == null && n.A[0].Type is not ByRefType) st.RawRefStore = true;
                }
        var work = new Queue<MethodDesc>();
        var queued = new HashSet<MethodDesc>();
        void Enqueue(MethodDesc m) { if (bodies.ContainsKey(m) && queued.Add(m)) work.Enqueue(m); }
        foreach (var m in Methods.Items) Enqueue(m);
        int evaluations = 0;
        long setupMs = clock.ElapsedMilliseconds, firstTicks = 0, laterTicks = 0;
        while (work.Count > 0)
        {
            var m = work.Dequeue();
            queued.Remove(m);
            evaluations++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool firstEval = !_methodStates.ContainsKey(m);
            Evaluate(m, bodies[m], st, Enqueue);
            if (firstEval) firstTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; else laterTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
        static long Ms(long t) => t * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (s_typeSetsTrace) Console.Error.WriteLine($"typesets: evaluate: build {Ms(_tBuild)}, loop {Ms(_tLoop - _tBuild)} (values {Ms(_tValue)}, targets {Ms(_tTargets)}), sinks {Ms(_tSinks)}, store {Ms(_tStore)} ms");
        if (s_typeSetsTrace) Console.Error.WriteLine($"typesets: phases: setup {setupMs} ms, first evaluations {firstTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, later {laterTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms");
        TypeSetsSolved = true;
        TypeSetsTime = (clock.ElapsedMilliseconds, bodies.Count, evaluations, st.Vars.Count, st.Sets.Count(x => x == null));
        if (s_typeSetsTrace)
        {
            var byKind = st.Vars.Select((v, i) => (v, i)).GroupBy(x => x.v.Kind == VarKind.Key ? "Key " + x.v.Key.Kind : x.v.Kind.ToString())
                .Select(g => $"{g.Key} {g.Count(x => st.Sets[x.i] == null)}/{g.Count()}");
            Console.Error.WriteLine("typesets: unknown by kind: " + string.Join(", ", byKind) + "; external methods " + ext.Count);
            if (Environment.GetEnvironmentVariable("IL2WASM_TYPESETS_SHOW") is { Length: > 0 } show)
                for (int i = 0; i < st.Vars.Count; i++)
                {
                    var v = st.Vars[i];
                    string name = v.Kind == VarKind.Key ? v.Key.ToString() : v.Kind + " " + v.Method + (v.Kind == VarKind.Param ? " #" + v.Index : "");
                    if (!name.Contains(show, StringComparison.Ordinal)) continue;
                    Console.Error.WriteLine("typesets: " + name + " = " + (st.Sets[i] is { } x ? "{" + string.Join(", ", x.Items.ToArray().Select(t => _all![t].ToString())) + "}" : "unknown") + (v.Kind == VarKind.Param && ext.Contains(v.Method!) ? " (external)" : ""));
                }
        }
        if (s_typeSetsTrace) Console.Error.WriteLine($"typesets: {bodies.Count} bodies, {evaluations} evaluations, {st.Vars.Count} variables ({TypeSetsTime.TopVars} unknown){(st.RawRefStore ? ", a reference stored through a raw pointer: every slot unknown" : "")}; {clock.ElapsedMilliseconds} ms");
    }

    int VarId(State st, Var v)
    {
        if (st.Ids.TryGetValue(v, out int id)) return id;
        id = st.Vars.Count;
        st.Ids[v] = id;
        st.Vars.Add(v);
        // (the parameters of an externally callable method, a delegate's fields, a raw store's slots: unknown)
        IdSet? init = IdSet.Empty;
        if (v.Kind is VarKind.Param or VarKind.AddrParam && _externalParams!.Contains(v.Method!)) init = null;
        if (v.Kind == VarKind.AddrRet && !Covers(v.Method!)) init = null;
        // (a constructor's this: the object an allocation made, of exactly its type, whoever allocated it; a base
        // constructor's comes from its subclasses' constructors in the IR)
        else if (v.Kind == VarKind.Param && v.Index == 0 && v.Method!.IsConstructor && !v.Method.Signature.IsStatic) init = Exact(v.Method.OwningType) ?? IdSet.Empty;
        if (v.Kind == VarKind.Ret && !Covers(v.Method!)) init = null;
        if (v.Kind == VarKind.Key && v.Key.Kind == KeyKind.Field && ((FieldDesc)v.Key.Target!).OwningType is MetadataType { } ot && IsDelegateBase(ot)) init = null;
        if (v.Kind == VarKind.Key && v.Key.Kind == KeyKind.Slot && st.RawRefStore) init = null;
        st.Sets.Add(init);
        st.Readers.Add([]);
        if (v.Kind == VarKind.Key && v.Key.Kind is KeyKind.Slot or KeyKind.Elem) st.SlotLike.Add(id);
        return id;
    }

    readonly Dictionary<int, List<int>> _aliasesOf = new();
    int _aliasesSeen;

    /// <summary>The slot-like keys that may alias a read key (other than itself), kept up to date as keys appear.</summary>
    List<int> AliasesOf(State st, int id)
    {
        if (_aliasesSeen < st.SlotLike.Count)
        {
            // (new slot-like keys: added to every list already made)
            for (int i = _aliasesSeen; i < st.SlotLike.Count; i++)
            {
                int sk = st.SlotLike[i];
                foreach (var (rid, l) in _aliasesOf) if (rid != sk && KeysMayAlias(st.Vars[rid].Key, st.Vars[sk].Key)) l.Add(sk);
            }
            _aliasesSeen = st.SlotLike.Count;
        }
        if (_aliasesOf.TryGetValue(id, out var r)) return r;
        r = [];
        foreach (int sk in st.SlotLike) if (sk != id && KeysMayAlias(st.Vars[id].Key, st.Vars[sk].Key)) r.Add(sk);
        _aliasesOf[id] = r;
        return r;
    }

    static bool IsDelegateBase(MetadataType t) => t.GetNamespace() == "System" && t.GetName() is "Delegate" or "MulticastDelegate";

    IdSet? Join(IdSet? a, IdSet? b)
    {
        if (a is not { } x || b is not { } y) return null;
        var u = Pool.Join(x, y);
        return u.Count > Cap ? null : u;
    }

    /// <summary>Grows a variable; its readers (and, for a slot-like key, the readers of the keys it aliases) run again.</summary>
    void Write(State st, Var v, IdSet? add, Action<MethodDesc> enqueue)
    {
        int id = VarId(st, v);
        var old = st.Sets[id];
        if (old == null) return;
        var nw = Join(old, add);
        if (nw is { } ns && old is { } os && ns.Equals(os)) return;
        st.Sets[id] = nw;
        Notify(st, id, enqueue);
        if (v.Kind == VarKind.Key && v.Key.Kind is KeyKind.Slot or KeyKind.Elem)
            foreach (int rk in st.ReadKeys)
                if (rk != id && KeysMayAlias(st.Vars[rk].Key, v.Key)) Notify(st, rk, enqueue);
    }

    IdSet? ReadVar(State st, Var v, MethodDesc reader)
    {
        int id = VarId(st, v);
        st.Readers[id].Add(reader);
        NoteRead(id);
        return st.Sets[id];
    }

    bool? _anyUnsafeAs;
    readonly Dictionary<(MemKey, MemKey), bool> _aliasCache = new();

    /// <summary>A write to slot-like key w may reach what a read of key r sees (RefStores' rule, by types).</summary>
    bool KeysMayAlias(MemKey r, MemKey w)
    {
        if (_aliasCache.TryGetValue((r, w), out var known)) return known;
        return _aliasCache[(r, w)] = KeysMayAlias0(r, w);
    }

    bool KeysMayAlias0(MemKey r, MemKey w)
    {
        if (r == w) return true;
        if (w.Kind == KeyKind.Elem && r.Kind != KeyKind.Elem) return false; // (an element store writes elements only)
        var rt = r.Kind switch { KeyKind.Field or KeyKind.Static => ((FieldDesc)r.Target!).FieldType, KeyKind.Elem or KeyKind.Slot => (TypeDesc)r.Target!, _ => null };
        var wt = (TypeDesc)w.Target!;
        if (rt == null) return false;
        if (rt == wt) return true;
        if (rt.IsValueType || wt.IsValueType || rt.IsPointer || wt.IsPointer || rt.IsByRef || wt.IsByRef) return false;
        _anyUnsafeAs ??= Methods.Items.Any(m => Summary(m) is { } su && su.Aliased.Count > 0);
        if (_anyUnsafeAs == true) return true;
        if (rt.IsInterface || wt.IsInterface || rt.IsObject || wt.IsObject || rt.IsGenericParameter || wt.IsGenericParameter) return true;
        return rt.CanCastTo(wt) || wt.CanCastTo(rt);
    }

    /// <summary>A read of a key: the key's set and those of the slot-like keys that may alias it, kept to the types the
    /// read's static type admits.</summary>
    IdSet? ReadKey(State st, MemKey k, TypeDesc? staticType, MethodDesc reader)
    {
        int id = VarId(st, new Var(VarKind.Key, null, 0, k));
        st.Readers[id].Add(reader);
        NoteRead(id);
        st.ReadKeys.Add(id);
        IdSet? acc = st.Sets[id];
        foreach (int s in AliasesOf(st, id))
        {
            if (acc == null) break;
            acc = Join(acc, st.Sets[s]);
        }
        return Filter(acc, staticType);
    }

    /// <summary>The types of a set that a static type admits (all of it for object or no type).</summary>
    IdSet? Filter(IdSet? s, TypeDesc? t)
    {
        // (unknown: every constructed type the static type admits, when they are few: the cone the static type gives)
        if (s == null) return ConeSet(t);
        if (s is not { } set || t == null || t.IsObject || set.IsEmpty) return s;
        var cone = ConeOf(t);
        var keep = set.Items.ToArray().Where(i => Array.BinarySearch(cone, i) >= 0).ToArray();
        return keep.Length == set.Count ? s : Pool.Of(keep);
    }

    readonly Dictionary<TypeDesc, IdSet?> _coneSets = new();

    /// <summary>A static type's cone as a set, or null (no type, or more than Cap types).</summary>
    IdSet? ConeSet(TypeDesc? t)
    {
        if (t == null || t.IsObject || t.IsPointer || t.IsByRef || t.IsGenericParameter) return null;
        if (t.IsValueType) t = t.IsNullable ? t.Instantiation[0] : t;
        if (_coneSets.TryGetValue(t, out var r)) return r;
        var cone = t.IsValueType ? (Exact(t) is { } e ? e.Items.ToArray() : []) : ConeOf(t);
        r = cone.Length > Cap ? null : Pool.Of(cone);
        _coneSets[t] = r;
        return r;
    }

    Dictionary<TypeDesc, int>? _allIndex;

    IdSet? Exact(TypeDesc? t)
    {
        if (t == null) return null;
        if (t.IsNullable) t = t.Instantiation[0];
        if (_allIndex == null) { _allIndex = new(); for (int i = 0; i < _all!.Length; i++) _allIndex[_all[i]] = i; }
        return _allIndex.TryGetValue(t, out int x) ? Pool.Of(x) : null;
    }

    /// <summary>A field's or a static's key for the type sets: the field as instantiated (List&lt;int&gt;._items apart
    /// from List&lt;string&gt;._items), not its typical definition.</summary>
    static MemKey FKey(FieldDesc f) => new(f.IsStatic ? KeyKind.Static : KeyKind.Field, f);

    /// <summary>The key a field's, a static's or an element's address names, or null.</summary>
    static MemKey? AddressKeyOf(Node addr) => addr.Op switch
    {
        Opc.LdFldA or Opc.LdSFldA when addr.X is FieldDesc f => FKey(f),
        Opc.LdElemA when addr.A.Length > 0 && addr.A[0].Type is ArrayType at => MemKey.Elem(at.ElementType),
        _ => null,
    };

    int _localKey = -1;
    /// <summary>The key id that stands for the method's own frame variables (a LocA): writes through it are the
    /// variable's, whose reads are unknown already.</summary>
    int LocalKey => _localKey >= 0 ? _localKey : _localKey = Keys.Intern(MemKey.Var(-1));

    static bool IsRef(Node n) => n.Kind == VK.Ptr && n.Type is ByRefType;

    /// <summary>A reference's address set: the key ids it may point at, or null (unknown: a raw pointer, address
    /// arithmetic, an intrinsic it does not know).</summary>
    IdSet? AddrOf(MethodDesc m, Node a, State st, Dictionary<Node, IdSet?> addrs)
    {
        if (addrs.TryGetValue(a, out var known))
        {
            // (a memoised address: the variables it read count as the asking node's reads too)
            if (_addrDeps.TryGetValue(a, out var deps)) foreach (int d in deps) NoteRead(d);
            return known;
        }
        var mine = new List<int>();
        _addrCollect.Add(mine);
        IdSet? r;
        switch (a.Op)
        {
            case Opc.LdFldA or Opc.LdSFldA or Opc.LdElemA when AddressKeyOf(a) is { } k: r = Pool.Of(Keys.Intern(k)); break;
            case Opc.LocA: r = Pool.Of(LocalKey); break;
            case Opc.Arg when IsRef(a): r = ReadVar(st, new Var(VarKind.AddrParam, m, (int)a.I, default), m); break;
            case Opc.Call when a.X is MethodDesc cm && IsRef(a):
            {
                var su = Summary(m);
                if (su != null && su.Intrinsics.Contains(cm))
                    // (Unsafe.As<TFrom, TTo>(ref), Unsafe.AsRef: the same place, seen as another type)
                    r = cm.OwningType is MetadataType ut && ut.GetName() == "Unsafe" && cm.GetName() is "As" or "AsRef" && a.A.Length == 1 && IsRef(a.A[0]) ? AddrOf(m, a.A[0], st, addrs) : null;
                else r = (CallKind)a.Sub == CallKind.Direct ? ReadVar(st, new Var(VarKind.AddrRet, cm, 0, default), m) : null;
                break;
            }
            case Opc.Phi when IsRef(a):
            {
                addrs[a] = IdSet.Empty; // (a cycle through phis: what the others add)
                IdSet? acc = IdSet.Empty;
                foreach (var x in a.A) { acc = JoinAddr(acc, AddrOf(m, x, st, addrs)); if (acc == null) break; }
                r = acc;
                break;
            }
            default: r = null; break;
        }
        addrs[a] = r;
        _addrCollect.RemoveAt(_addrCollect.Count - 1);
        _addrDeps[a] = mine;
        if (_addrCollect.Count > 0) _addrCollect[^1].AddRange(mine);
        return r;
    }

    /// <summary>The join of address sets (no cap: keys are few).</summary>
    IdSet? JoinAddr(IdSet? a, IdSet? b) => a is { } x && b is { } y ? Pool.Join(x, y) : null;

    void WriteAddr(State st, Var v, IdSet? add, Action<MethodDesc> enqueue)
    {
        int id = VarId(st, v);
        var old = st.Sets[id];
        if (old == null) return;
        var nw = JoinAddr(old, add);
        if (nw is { } ns && old is { } os && ns.Equals(os)) return;
        st.Sets[id] = nw;
        Notify(st, id, enqueue);
    }

    /// <summary>A variable grew: its readers run again, from the nodes that read it.</summary>
    void Notify(State st, int id, Action<MethodDesc> enqueue)
    {
        foreach (var r in st.Readers[id])
        {
            if (_methodStates.TryGetValue(r, out var rs)) rs.Changed.Add(id);
            enqueue(r);
        }
    }

    /// <summary>A read through a reference: the keys it may point at (unknown for a frame variable), or the slots of
    /// its type when its address set is unknown.</summary>
    IdSet? ReadThrough(MethodDesc m, Node addr, TypeDesc? staticType, State st, Dictionary<Node, IdSet?> addrs)
    {
        if (AddrOf(m, addr, st, addrs) is { } keys)
        {
            IdSet? acc = IdSet.Empty;
            foreach (int k in keys.Items) { if (k == LocalKey) return null; acc = Join(acc, ReadKey(st, Keys[k], staticType, m)); if (acc == null) break; }
            return acc;
        }
        return addr.Type is ByRefType br ? ReadKey(st, MemKey.Slot(br.ParameterType), staticType, m) : null;
    }

    /// <summary>A store through a reference.</summary>
    void WriteThrough(MethodDesc m, Node addr, Node value, State st, Dictionary<Node, IdSet?> addrs, Func<Node, IdSet?> of, Action<MethodDesc> enqueue)
    {
        if (value.Kind != VK.Ref) return;
        if (AddrOf(m, addr, st, addrs) is { } keys)
        {
            foreach (int k in keys.Items) if (k != LocalKey) Write(st, new Var(VarKind.Key, null, 0, Keys[k]), of(value), enqueue);
            return;
        }
        if (s_typeSetsTrace && Environment.GetEnvironmentVariable("IL2WASM_TYPESETS_SLOTS") == "1" && addr.Type is ByRefType sb && of(value) == null) Console.Error.WriteLine($"typesets: unknown into Slot {sb.ParameterType} from {m} (address {addr.Op} {addr.X})");
        if (addr.Type is ByRefType br) Write(st, new Var(VarKind.Key, null, 0, MemKey.Slot(br.ParameterType)), of(value), enqueue);
        // (a raw pointer: RawRefStore made every slot unknown)
    }

    /// <summary>A method's values between its evaluations: they only grow, so a later evaluation recomputes only the
    /// nodes that read a variable that grew, and what uses them.</summary>
    sealed class MethodState
    {
        public readonly Dictionary<Node, IdSet?> Local = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<int, HashSet<Node>> Reads = new();
        public readonly Dictionary<Node, List<Node>> Users = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<int, List<Node>> SlotStores = new();
        public readonly HashSet<int> Taken = [];
        public List<Block> Blocks = [];
        public readonly HashSet<int> Changed = [];
    }

    readonly Dictionary<MethodDesc, MethodState> _methodStates = new();
    long _tBuild, _tValue, _tTargets, _tLoop, _tSinks, _tStore;
    MethodState? _curState;
    Node? _curNode;
    // (the variables each memoised address read, and the addresses being worked out)
    readonly Dictionary<Node, List<int>> _addrDeps = new(ReferenceEqualityComparer.Instance);
    readonly List<List<int>> _addrCollect = [];

    /// <summary>Notes that the node being computed read a variable (it runs again when the variable grows).</summary>
    void NoteRead(int id)
    {
        if (_curState == null || _curNode == null) return;
        if (_addrCollect.Count > 0) _addrCollect[^1].Add(id);
        if (!_curState.Reads.TryGetValue(id, out var l)) _curState.Reads[id] = l = new(ReferenceEqualityComparer.Instance);
        l.Add(_curNode);
    }

    /// <summary>One method's values, to their local fixed point, and what they flow into: the whole body the first time,
    /// then only the nodes that read a variable that grew, what uses their values, and the sinks among them.</summary>
    void Evaluate(MethodDesc m, Body body, State st, Action<MethodDesc> enqueue)
    {
        bool first = !_methodStates.TryGetValue(m, out var ms);
        long tq0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (first)
        {
            ms = _methodStates[m] = new MethodState();
            foreach (var bl in body.Blocks) foreach (var n in bl.Code) if (n.Op == Opc.LocA) ms.Taken.Add((int)n.I);
            ms.Blocks = Reachable(body);
            var varLds = new Dictionary<int, List<Node>>();
            foreach (var bl in ms.Blocks)
                foreach (var n in bl.Phis.Concat(bl.Code))
                {
                    foreach (var x in n.A) { if (!ms.Users.TryGetValue(x, out var u)) ms.Users[x] = u = []; u.Add(n); }
                    if (n.Op == Opc.VarSt && n.A.Length > 0 && n.A[0].Kind == VK.Ref) { if (!ms.SlotStores.TryGetValue((int)n.I, out var l)) ms.SlotStores[(int)n.I] = l = []; l.Add(n.A[0]); }
                    if (n.Op == Opc.VarLd) { if (!varLds.TryGetValue((int)n.I, out var l)) varLds[(int)n.I] = l = []; l.Add(n); }
                }
            // (a stored value's readers through the variable: its VarLds)
            foreach (var (slot, stores) in ms.SlotStores)
                if (varLds.TryGetValue(slot, out var lds))
                    foreach (var v in stores) { if (!ms.Users.TryGetValue(v, out var u)) ms.Users[v] = u = []; u.AddRange(lds); }
        }
        _tBuild += System.Diagnostics.Stopwatch.GetTimestamp() - tq0;
        var local = ms!.Local;
        var addrs = new Dictionary<Node, IdSet?>(ReferenceEqualityComparer.Instance);
        _addrDeps.Clear();
        IdSet? Of(Node v) => v.Op == Opc.LdNull ? IdSet.Empty : local.TryGetValue(v, out var s) ? s : IdSet.Empty;
        _curState = ms;
        var work = new Queue<Node>();
        var queued = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var sinks = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        void Push(Node n) { if (queued.Add(n)) work.Enqueue(n); }
        if (first)
            foreach (var bl in ms.Blocks) foreach (var n in bl.Phis.Concat(bl.Code)) { Push(n); sinks.Add(n); }
        else
        {
            foreach (int id in ms.Changed) if (ms.Reads.TryGetValue(id, out var ns)) foreach (var n in ns) { Push(n); sinks.Add(n); }
        }
        ms.Changed.Clear();
        while (work.Count > 0)
        {
            var n = work.Dequeue();
            queued.Remove(n);
            bool changed = false;
            if (n.Kind == VK.Ref)
            {
                _curNode = n;
                // (unknown: the static type's cone when it is small)
                long tv = System.Diagnostics.Stopwatch.GetTimestamp();
                var v = Value(m, body, n, st, Of, ms, addrs) ?? ConeSet(n.Type);
                _tValue += System.Diagnostics.Stopwatch.GetTimestamp() - tv;
                if (!local.TryGetValue(n, out var old) || !Same(old, v)) { local[n] = v; changed = true; }
            }
            if (n.Op == Opc.Call && (CallKind)n.Sub != CallKind.Direct && n.X is MethodDesc vm && !(Summary(m)?.Intrinsics.Contains(vm) ?? false))
            {
                // (a virtual call's targets over its receiver's set: what reads their results and gets its arguments)
                long tt = System.Diagnostics.Stopwatch.GetTimestamp();
                var t = TargetsOver(vm, n.A.Length > 0 ? n.A[0] : null, Of).ToArray();
                _tTargets += System.Diagnostics.Stopwatch.GetTimestamp() - tt;
                if (!_callTargets.TryGetValue((m, n.Id), out var ot) || !ot.SequenceEqual(t)) { _callTargets[(m, n.Id)] = t; sinks.Add(n); if (!changed && n.Kind == VK.Ref && ot != null) { Push(n); } }
            }
            if (changed && ms.Users.TryGetValue(n, out var users)) foreach (var u in users) { Push(u); sinks.Add(u); }
            if (changed) sinks.Add(n);
        }
        _curNode = null;
        long ts = System.Diagnostics.Stopwatch.GetTimestamp();
        _tLoop += ts - tq0;
        foreach (var n in sinks) { _curNode = n; Sinks(m, n, st, Of, enqueue, addrs); }
        _curNode = null;
        _curState = null;
        long tst = System.Diagnostics.Stopwatch.GetTimestamp();
        _tSinks += tst - ts;
        foreach (var (n, v) in local) _nodeTypes[(m, n.Id)] = v;
        _tStore += System.Diagnostics.Stopwatch.GetTimestamp() - tst;
        if (s_typeSetsTrace && Environment.GetEnvironmentVariable("IL2WASM_TYPESETS_DUMP") is { Length: > 0 } dm && m.ToString()!.Contains(dm, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("typesets: body of " + m + ": " + body.Dump());
            foreach (var (n, v) in local) Console.Error.WriteLine("typesets:   v" + n.Id + " = " + (v is { } x ? "{" + string.Join(", ", x.Items.ToArray().Select(t => _all![t].ToString())) + "}" : "unknown"));
        }
    }

    /// <summary>The blocks that can run: from the entry and the handlers of the tries reached, past the branches on
    /// Generics.Implements, IsNullable and IsEnum, which lowering decides from the instantiation (IlCompiler.cs) and
    /// the constant propagation does not.</summary>
    static List<Block> Reachable(Body body)
    {
        var seen = new HashSet<Block>();
        var work = new Stack<Block>();
        work.Push(body.Blocks[0]);
        while (work.Count > 0)
        {
            var b = work.Pop();
            if (!seen.Add(b)) continue;
            foreach (var h in Ownership.ExceptionSuccs(b)) work.Push(h);
            if (b.Term is { Op: Opc.BrIf } br && br.A.Length > 0 && ConstOf(br.A[0]) is long c && b.Succs.Count == 2) { work.Push(c != 0 ? b.Succs[0] : b.Succs[1]); continue; }
            foreach (var x in b.Succs) work.Push(x);
        }
        // (a handler's entry, finally and fault ones too, comes in through ExceptionSuccs of its try's blocks; a
        // finally's end goes on to its leave targets, which the try's own leaves reach already)
        // (in the body's own order: the passes are faster)
        return body.Blocks.Where(seen.Contains).ToList();
    }

    /// <summary>An i32 the instantiation decides: Generics.IsNullable, IsEnum, Implements, constants, == and != of
    /// them; else null.</summary>
    static long? ConstOf(Node n)
    {
        switch (n.Op)
        {
            case Opc.Const when n.Kind == VK.I32: return n.I;
            case Opc.Cmp when n.A.Length == 2 && ConstOf(n.A[0]) is long a && ConstOf(n.A[1]) is long b:
                return (CmpOp)n.Sub switch { CmpOp.Eq => a == b ? 1 : 0, CmpOp.Ne => a != b ? 1 : 0, _ => null };
            case Opc.Call when n.X is MethodDesc m && m.OwningType is MetadataType g && g.GetName() == "Generics" && g.GetNamespace() == "Il2Wasm.Runtime" && m.HasInstantiation:
            {
                var t = m.Instantiation[0];
                switch (m.GetName())
                {
                    case "IsNullable": return t.IsNullable ? 1 : 0;
                    case "IsEnum": return t.IsEnum ? 1 : 0;
                    case "Implements" when n.A.Length > 0 && n.A[0].Op == Opc.LdStr && n.A[0].X is string name && name.LastIndexOf('.') is > 0 and var dot:
                        if (m.Context.SystemModule.GetType(System.Text.Encoding.UTF8.GetBytes(name[..dot]), System.Text.Encoding.UTF8.GetBytes(name[(dot + 1)..]), throwIfNotFound: false) is not MetadataType gi) return null;
                        try { return t.CanCastTo(gi.MakeInstantiatedType(t)) ? 1 : 0; } catch (Exception) { return null; }
                }
                return null;
            }
        }
        return null;
    }

    static bool Same(IdSet? a, IdSet? b) => a is { } x ? b is { } y && x.Equals(y) : b == null;

    IdSet? Value(MethodDesc m, Body body, Node n, State st, Func<Node, IdSet?> of, MethodState ms, Dictionary<Node, IdSet?> addrs)
    {
        switch (n.Op)
        {
            case Opc.LdNull: return IdSet.Empty;
            case Opc.LdStr: return Exact(n.Type);
            // (a type object is CoreLib's RuntimeType for the type)
            case Opc.TypeOf: return Exact(_all![0].Context.SystemModule.GetType("System"u8, "RuntimeType"u8, throwIfNotFound: false));
            case Opc.NewArr: return Exact(n.Type);
            case Opc.Box: return Exact(n.X as TypeDesc ?? n.Type);
            case Opc.NewObj when n.X is MethodDesc ctor: return Exact(ctor.OwningType);
            case Opc.Arg: return ReadVar(st, new Var(VarKind.Param, m, (int)n.I, default), m);
            case Opc.Phi:
            {
                IdSet? acc = IdSet.Empty;
                foreach (var a in n.A) { acc = Join(acc, of(a)); if (acc == null) break; }
                return acc;
            }
            case Opc.VarLd:
            {
                int slot = (int)n.I;
                // (a variable whose address is taken, or a parameter's own storage: unknown)
                if (ms.Taken.Contains(slot) || body.Vars[slot].ArgIndex >= 0) return null;
                IdSet? acc = IdSet.Empty;
                if (ms.SlotStores.TryGetValue(slot, out var stores)) foreach (var x in stores) { acc = Join(acc, of(x)); if (acc == null) break; }
                return acc;
            }
            case Opc.CastClass or Opc.IsInst when n.X is TypeDesc ct: return Filter(of(n.A[0]), ct);
            case Opc.LdFld when n.X is FieldDesc f: return ReadKey(st, FKey(f), n.Type, m);
            case Opc.LdSFld when n.X is FieldDesc sf: return ReadKey(st, FKey(sf), n.Type, m);
            case Opc.LdElem when n.A.Length > 0 && n.A[0].Type is ArrayType at: return ReadKey(st, MemKey.Elem(at.ElementType), n.Type, m);
            case Opc.LdInd or Opc.LdObj when n.A.Length > 0: return ReadThrough(m, n.A[0], n.Type, st, addrs);
            case Opc.Call when n.X is MethodDesc cm:
            {
                // (Interlocked.Exchange/CompareExchange and Volatile.Read of a reference, whatever their IL does with
                // addresses: the slot's value)
                if (RefSlotApi(cm) is "exchange" or "read" && n.A.Length > 0 && IsRef(n.A[0])) return ReadThrough(m, n.A[0], n.Type, st, addrs);
                var su = Summary(m);
                if (su != null && su.Intrinsics.Contains(cm))
                {
                    if (MethodSummary.NewByName(cm, n) is { } made) return Exact(made.OwningType);
                    if (MethodSummary.NewTraps(cm, n)) return IdSet.Empty;
                    if (cm.GetName() == "As" && cm.OwningType is MetadataType ut && ut.GetName() == "Unsafe" && n.A.Length == 1) return of(n.A[0]);
                    // (Interlocked.Exchange / CompareExchange of a reference: the slot's old value)
                    if (IntrinsicSignatures.WritesThroughFirstRef(cm) && n.A.Length > 0) return ReadThrough(m, n.A[0], n.Type, st, addrs);
                    return null;
                }
                if ((CallKind)n.Sub == CallKind.Direct) return ReadVar(st, new Var(VarKind.Ret, cm, 0, default), m);
                // (a virtual call: the results of the targets over the receiver's types)
                IdSet? acc = IdSet.Empty;
                foreach (var t in TargetsOver(cm, n.A.Length > 0 ? n.A[0] : null, of)) { acc = Join(acc, ReadVar(st, new Var(VarKind.Ret, t, 0, default), m)); if (acc == null) break; }
                return acc;
            }
            default: return null;
        }
    }

    /// <summary>The library's reference-slot methods: "exchange" (Interlocked.Exchange, CompareExchange: read the old
    /// value, maybe store the new one), "read" (Volatile.Read), "write" (Volatile.Write), or null.</summary>
    static string? RefSlotApi(MethodDesc m)
    {
        if (m.OwningType is not MetadataType t || t.GetNamespace() != "System.Threading" || m.Signature.Length == 0 || m.Signature[0] is not ByRefType br || Kinds.Of(br.ParameterType) != VK.Ref) return null;
        return (t.GetName(), m.GetName()) switch
        {
            ("Interlocked", "Exchange" or "CompareExchange") => "exchange",
            ("Volatile", "Read") => "read",
            ("Volatile", "Write") => "write",
            _ => null,
        };
    }

    /// <summary>A virtual call's targets over its receiver's set (the static type's when the set is unknown).</summary>
    IEnumerable<MethodDesc> TargetsOver(MethodDesc slot, Node? receiver, Func<Node, IdSet?> of)
    {
        var rs = receiver != null && receiver.Kind == VK.Ref ? of(receiver) : null;
        if (rs is not { } set) return Targets(slot, receiver?.Kind == VK.Ref ? receiver.Type : null);
        var r = new HashSet<MethodDesc>();
        foreach (int i in set.Items) if (_host.Resolve(_all![i], slot) is { } impl) r.Add(impl);
        return r.OrderBy(x => StableKey.Of(x), StringComparer.Ordinal);
    }

    void Sinks(MethodDesc m, Node n, State st, Func<Node, IdSet?> of, Action<MethodDesc> enqueue, Dictionary<Node, IdSet?> addrs)
    {
        void Key(MemKey k, Node v) { if (v.Kind == VK.Ref) Write(st, new Var(VarKind.Key, null, 0, k), of(v), enqueue); }
        void Args(MethodDesc target, Node call, int skip, IdSet? self)
        {
            if (self != null || skip == 1 && call.Op == Opc.NewObj) Write(st, new Var(VarKind.Param, target, 0, default), self, enqueue);
            for (int i = 0; i < call.A.Length; i++)
            {
                if (call.A[i].Kind == VK.Ref) Write(st, new Var(VarKind.Param, target, i + skip, default), of(call.A[i]), enqueue);
                else if (IsRef(call.A[i])) WriteAddr(st, new Var(VarKind.AddrParam, target, i + skip, default), AddrOf(m, call.A[i], st, addrs), enqueue);
            }
        }
        switch (n.Op)
        {
            case Opc.StFld when n.X is FieldDesc f && n.A.Length > 1: Key(FKey(f), n.A[1]); break;
            case Opc.StSFld when n.X is FieldDesc sf && n.A.Length > 0: Key(FKey(sf), n.A[0]); break;
            case Opc.StElem when n.A.Length > 2:
                Key(n.A[0].Type is ArrayType at ? MemKey.Elem(at.ElementType) : MemKey.Slot(n.A[2].Type ?? _all![0].Context.GetWellKnownType(WellKnownType.Object)), n.A[2]);
                break;
            case Opc.StInd when n.A.Length > 1 && n.A[1].Kind == VK.Ref: WriteThrough(m, n.A[0], n.A[1], st, addrs, of, enqueue); break;
            case Opc.Ret when n.A.Length > 0 && n.A[0].Kind == VK.Ref: Write(st, new Var(VarKind.Ret, m, 0, default), of(n.A[0]), enqueue); break;
            case Opc.Ret when n.A.Length > 0 && IsRef(n.A[0]): WriteAddr(st, new Var(VarKind.AddrRet, m, 0, default), AddrOf(m, n.A[0], st, addrs), enqueue); break;
            case Opc.NewObj when n.X is MethodDesc ctor:
            {
                var target = _host.ConstructorTarget(ctor);
                // (string's constructors are static methods: the arguments shift down)
                if (target != ctor) Args(target, n, 0, null);
                else Args(target, n, 1, Exact(ctor.OwningType));
                break;
            }
            case Opc.Call when n.X is MethodDesc cm:
            {
                // (Interlocked.Exchange/CompareExchange, Volatile.Write of a reference: the value goes into the slot; the
                // callee's own IL works on raw addresses, and the slot's readers see the value from here)
                if (RefSlotApi(cm) is "exchange" or "write" && n.A.Length > 1 && IsRef(n.A[0])) { WriteThrough(m, n.A[0], n.A[1], st, addrs, of, enqueue); break; }
                var su = Summary(m);
                if (su != null && su.Intrinsics.Contains(cm))
                {
                    if (MethodSummary.NewByName(cm, n) is { } made) Write(st, new Var(VarKind.Param, made, 0, default), Exact(made.OwningType), enqueue);
                    // (Interlocked.Exchange(ref slot, v), CompareExchange(ref slot, v, c): v may go into the slot)
                    if (IntrinsicSignatures.WritesThroughFirstRef(cm) && n.A.Length > 1) WriteThrough(m, n.A[0], n.A[1], st, addrs, of, enqueue);
                    break;
                }
                if ((CallKind)n.Sub == CallKind.Direct) Args(cm, n, 0, null);
                else foreach (var t in TargetsOver(cm, n.A.Length > 0 ? n.A[0] : null, of)) Args(t, n, 0, null);
                break;
            }
        }
    }
}
