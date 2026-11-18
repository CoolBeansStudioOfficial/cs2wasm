using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Cycles (section 4.3): which stores can close a reference cycle, and the arena runtime they call.
///
/// Every store of a reference into a class field or an array element is a <see cref="Site"/>. The compiler records,
/// per site, the static types of the values stored there (field-target analysis), so an <c>object</c> or interface
/// field only points at the types the program actually puts in it. When the whole program is lowered, the type graph
/// (container type → stored type, per site) is built over the constructed types and its strongly connected
/// components are the cycle-capable groups. Stores at a site whose edges stay inside one such component link the two
/// objects into one arena; every other store compiles to the plain counted store, so types that cannot form cycles
/// never pay. Sites are emitted as calls to placeholder functions and resolved here once the graph is known.
/// See docs/STATUS.md (section 4) for the run-time half.
/// </summary>
internal sealed class Arenas
{
    readonly Driver D;
    public Arenas(Driver d) { D = d; }

    internal sealed class Site
    {
        public required string Key;
        public required ITypeSymbol Container; // declaring class of the field, or the array type
        public required bool IsArray;
        /// <summary>A field set only while its object is constructed, by constructors that keep `this` in: objects cannot
        /// form a cycle through such fields alone (the later object would have to exist before the earlier is done).</summary>
        public bool Immutable;
        public int Offset; // field sites
        public readonly Dictionary<string, ITypeSymbol> Values = [];
        public bool AddressTaken;
        /// <summary>Some store through this site is into an object that other code may know (Analyze: the site's
        /// general link is called somewhere). A field without one is only ever stored into objects that nothing else
        /// can reach yet (FreshStores.cs), and no such store closes a cycle: like a field filled at construction,
        /// it can be part of a cycle only where some other edge closes it.</summary>
        public bool General;
        public bool Cyclic;
        public required WasmFunction Link, Unlink;
        /// <summary>The same for a store into an object only the storing code knows (FreshStores.cs).</summary>
        public WasmFunction LinkFresh = null!, UnlinkFresh = null!;
        /// <summary>The link for a store into an object held in a local that owns it (OwnedLocals in Expressions.cs):
        /// when that local's is the only reference there is, nothing can reach the object and no cycle closes.</summary>
        public WasmFunction LinkOwned = null!;

        public void AddValue(ITypeSymbol? t)
        {
            if (t == null || t.TypeKind == TypeKind.Error) return;
            Values.TryAdd(Types.Key(t), t);
        }
    }

    enum Kind { Link, Unlink, Pin, LinkFresh, UnlinkFresh, LinkOwned, LinkAt, UnlinkAt, MoveAt }

    readonly Dictionary<string, Site> _sites = [];
    readonly Dictionary<WasmFunction, (Kind kind, Site? site, ITypeSymbol? pinType)> _placeholders = [];
    readonly Dictionary<string, WasmFunction> _pins = [];
    int[] _scc = [];
    public bool Enabled { get; private set; }
    /// <summary>Type ids of cycle-capable types (members of some arena at run time).</summary>
    public readonly List<int> CycleTypes = [];
    public int SccOf(int id) => id < _scc.Length ? _scc[id] : 0;
    public bool AnyArrayCycle { get; private set; }

    static readonly ValType I = ValType.I32;

    WasmFunction Placeholder(string name, FuncType t, Kind k, Site? s, ITypeSymbol? pin = null)
    {
        var f = new WasmFunction(name, t);
        _placeholders[f] = (k, s, pin);
        return f;
    }

    Site Get(string key, ITypeSymbol container, bool isArray, int offset)
    {
        if (_sites.TryGetValue(key, out var s)) return s;
        s = new Site
        {
            Key = key, Container = container, IsArray = isArray, Offset = offset,
            Link = null!, Unlink = null!,
        };
        s.Link = Placeholder("arena.link " + key, new FuncType([I, I, I], [I]), Kind.Link, s);
        s.Unlink = Placeholder("arena.unlink " + key, new FuncType([I, I, I], []), Kind.Unlink, s);
        s.LinkFresh = Placeholder("arena.link fresh " + key, new FuncType([I, I, I], [I]), Kind.LinkFresh, s);
        s.UnlinkFresh = Placeholder("arena.unlink fresh " + key, new FuncType([I, I, I], []), Kind.UnlinkFresh, s);
        s.LinkOwned = Placeholder("arena.link owned " + key, new FuncType([I, I, I], [I]), Kind.LinkOwned, s);
        _sites[key] = s;
        return s;
    }

    /// <summary>The site for a reference-typed instance field of a class.</summary>
    public Site? FieldSite(ITypeSymbol owner, FieldSlot slot)
    {
        owner = D.Types.Canonical(owner);
        if (owner.IsValueType || !Types.IsRef(slot.Type)) return null;
        // (a readonly field is an edge like any other: what it stores may come to refer back later, a list of children
        // whose items refer to their parent. Only a cycle made of such edges alone cannot exist: Analyze)
        var site = Get("f " + Types.Key(owner) + " " + slot.Offset, owner, false, slot.Offset);
        site.Immutable = ImmutableEdge(owner, slot);
        return site;
    }

    static readonly bool s_noFreshSites = Environment.GetEnvironmentVariable("CS2WASM_NO_FRESH_SITES") == "1";
    readonly Dictionary<string, bool> _keepsThis = [];
    /// <summary>CS2WASM_IMMUTABLE_EDGES=1 (measurement only: the old, unsound rule).</summary>
    static readonly bool s_immutableEdges = Environment.GetEnvironmentVariable("CS2WASM_IMMUTABLE_EDGES") == "1";

    /// <summary>A readonly field (or get-only auto-property) is set only while its object is being constructed. If no
    /// constructor of the type or its bases lets `this` out before then, nothing the stored value reaches can refer
    /// back to the new object, so the store cannot close a cycle and is no edge of the type graph (an exception's
    /// inner exception, say).</summary>
    bool ImmutableEdge(ITypeSymbol owner, FieldSlot slot)
    {
        bool ro = slot.Symbol switch
        {
            IFieldSymbol f => f.IsReadOnly || f.DeclaredAccessibility == Accessibility.Private && WrittenOnlyByConstructors(f),
            IPropertySymbol p => p.SetMethod == null,
            _ => false,
        };
        if (!ro) return false;
        for (var t = owner as INamedTypeSymbol; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            if (!KeepsThis(t)) { if (Environment.GetEnvironmentVariable("CS2WASM_ARENA_DEBUG") == "1") Console.Error.WriteLine($"edge {slot.Symbol}: {t} lets this out"); return false; }
        return true;
    }

    readonly Dictionary<string, bool> _ctorOnly = [];
    HashSet<IMethodSymbol>? _compiledCtors;

    /// <summary>A non-virtual property whose accessors use `this` only to reach its fields (HResult).</summary>
    bool FieldOnlyProperty(IPropertySymbol p)
    {
        p = p.OriginalDefinition;
        if (p.IsVirtual || p.IsOverride || p.IsAbstract || p.IsStatic) return false;
        foreach (var acc in new[] { p.GetMethod, p.SetMethod })
        {
            if (acc == null) continue;
            foreach (var sr in acc.DeclaringSyntaxReferences)
            {
                if (D.ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) return false;
                foreach (var op in body.DescendantsAndSelf())
                    if (op is Microsoft.CodeAnalysis.Operations.IInstanceReferenceOperation ir && ir.Parent is not Microsoft.CodeAnalysis.Operations.IFieldReferenceOperation) return false;
            }
            if (acc.DeclaringSyntaxReferences.Length == 0) return false;
        }
        return true;
    }

    /// <summary>A private field that only its type's instance constructors write, on the object being made (so in
    /// effect readonly: the inner exception of Mono's Exception, declared without `readonly`).</summary>
    bool WrittenOnlyByConstructors(IFieldSymbol f)
    {
        f = f.OriginalDefinition;
        var key = Types.Key(f.ContainingType) + "::" + f.Name;
        if (_ctorOnly.TryGetValue(key, out var r)) return r;
        r = true;
        var types = new Stack<INamedTypeSymbol>();
        types.Push(f.ContainingType);
        while (types.Count > 0 && r)
        {
            var t = types.Pop();
            foreach (var m in t.GetMembers())
            {
                if (m is INamedTypeSymbol nt) { types.Push(nt); continue; }
                foreach (var sr in m.DeclaringSyntaxReferences)
                {
                    if (D.ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) continue;
                    foreach (var op in body.DescendantsAndSelf())
                    {
                        if (op is not Microsoft.CodeAnalysis.Operations.IFieldReferenceOperation fr || !SymbolEqualityComparer.Default.Equals(fr.Field.OriginalDefinition, f)) continue;
                        // (returned by reference: whoever gets the reference can store through it, any time)
                        bool refReturned = m is IMethodSymbol { ReturnsByRef: true } or IPropertySymbol { ReturnsByRef: true }
                            && Ancestors(fr).TakeWhile(x => x is Microsoft.CodeAnalysis.Operations.IConversionOperation or Microsoft.CodeAnalysis.Operations.IParenthesizedOperation
                                or Microsoft.CodeAnalysis.Operations.IConditionalOperation or Microsoft.CodeAnalysis.Operations.IReturnOperation)
                                .Any(x => x is Microsoft.CodeAnalysis.Operations.IReturnOperation);
                        if (!IsWrite(fr) && !refReturned) continue;
                        bool inCtor = m is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && SymbolEqualityComparer.Default.Equals(ctor.ContainingType, f.ContainingType)
                            && fr.Instance is Microsoft.CodeAnalysis.Operations.IInstanceReferenceOperation
                            && !Ancestors(fr).Any(x => x is Microsoft.CodeAnalysis.Operations.IAnonymousFunctionOperation or Microsoft.CodeAnalysis.Operations.ILocalFunctionOperation);
                        if (!inCtor) { if (Environment.GetEnvironmentVariable("CS2WASM_ARENA_DEBUG") == "1") Console.Error.WriteLine($"field {f} written in {m}: {fr.Syntax}"); r = false; break; }
                    }
                    if (!r) break;
                }
                if (!r) break;
            }
        }
        _ctorOnly[key] = r;
        return r;

        static IEnumerable<IOperation> Ancestors(IOperation o) { for (var p = o.Parent; p != null; p = p.Parent) yield return p; }
        static bool IsWrite(IOperation o)
        {
            var p = o.Parent;
            while (p is Microsoft.CodeAnalysis.Operations.IConversionOperation or Microsoft.CodeAnalysis.Operations.IParenthesizedOperation) { o = p; p = p.Parent; }
            return p switch
            {
                Microsoft.CodeAnalysis.Operations.IAssignmentOperation a => a.Target == o,
                Microsoft.CodeAnalysis.Operations.IIncrementOrDecrementOperation => true,
                Microsoft.CodeAnalysis.Operations.IAddressOfOperation => true,
                Microsoft.CodeAnalysis.Operations.IArgumentOperation { Parameter.RefKind: not RefKind.None and not RefKind.In and not RefKind.RefReadOnlyParameter } => true,
                Microsoft.CodeAnalysis.Operations.IVariableInitializerOperation { Parent: Microsoft.CodeAnalysis.Operations.IVariableDeclaratorOperation { Symbol.IsRef: true } } => true,
                Microsoft.CodeAnalysis.Operations.ITupleOperation => true,
                _ => p is Microsoft.CodeAnalysis.Operations.IInvocationOperation { Instance: var inst, TargetMethod.IsReadOnly: false } && inst == o && o.Type?.IsValueType == true,
            };
        }
    }

    bool KeepsThis(INamedTypeSymbol t)
    {
        var k = Types.Key(t.OriginalDefinition);
        if (_keepsThis.TryGetValue(k, out var r)) return r;
        r = true;
        // (only constructors the program can run: the compiled ones)
        _compiledCtors ??= D.AllMethods.Where(m => m.Compiled && m.Sym.MethodKind == MethodKind.Constructor).Select(m => m.Sym.OriginalDefinition).ToHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var c in t.OriginalDefinition.InstanceConstructors.Where(c => _compiledCtors.Contains(c)))
            foreach (var sr in c.DeclaringSyntaxReferences)
            {
                var model = D.ModelFor(sr.SyntaxTree);
                if (model.GetOperation(sr.GetSyntax()) is not { } body) continue;
                foreach (var op in body.DescendantsAndSelf())
                {
                    if (op is not Microsoft.CodeAnalysis.Operations.IInstanceReferenceOperation ir) continue;
                    // `this` used only to reach its own fields and auto-properties stays in
                    if (ir.Parent is Microsoft.CodeAnalysis.Operations.IFieldReferenceOperation) continue;
                    if (ir.Parent is Microsoft.CodeAnalysis.Operations.IPropertyReferenceOperation pr && (Driver.AutoPropertyField(pr.Property.OriginalDefinition) != null || FieldOnlyProperty(pr.Property))) continue;
                    if (ir.Parent is Microsoft.CodeAnalysis.Operations.IInvocationOperation { TargetMethod.MethodKind: MethodKind.Constructor }) continue; // : base(...) / : this(...)
                    if (Environment.GetEnvironmentVariable("CS2WASM_ARENA_DEBUG") == "1") Console.Error.WriteLine($"{t}: ctor {c} lets this out: {ir.Parent?.Syntax}");
                    r = false;
                }
            }
        // field initializers cannot use `this`; implicit constructors only call the base one
        _keepsThis[k] = r;
        return r;
    }

    /// <summary>The site for the elements of an array of references.</summary>
    public Site? ArraySite(IArrayTypeSymbol at)
    {
        var et = D.Types.Canonical(at.ElementType);
        if (!Types.IsRef(et) || at.Rank != 1) return null;
        var t = D.Types.Canonical(at);
        return Get("a " + Types.Key(t), t, true, 0);
    }

    /// <summary>(value) → value: a store into a static field; the object is pinned (never joins an arena).</summary>
    public WasmFunction Pin(ITypeSymbol fieldType)
    {
        fieldType = D.Types.Canonical(fieldType);
        var k = Types.Key(fieldType);
        if (!_pins.TryGetValue(k, out var f))
            _pins[k] = f = Placeholder("arena.pin " + k, new FuncType([I], [I]), Kind.Pin, null, fieldType);
        return f;
    }

    readonly Dictionary<string, WasmFunction> _linkAt = [], _unlinkAt = [];
    WasmFunction? _moveAt;

    /// <summary>(value, slot address) → value: a store of a reference into a slot the code knows by its address
    /// alone (through a `ref`, a span, a struct's own method). The runtime finds the object the slot is in.</summary>
    public WasmFunction LinkAt(ITypeSymbol slotType)
    {
        slotType = D.Types.Canonical(slotType);
        var k = Types.Key(slotType);
        if (!_linkAt.TryGetValue(k, out var f))
            _linkAt[k] = f = Placeholder("arena.link at " + k, new FuncType([I, I], [I]), Kind.LinkAt, null, slotType);
        return f;
    }

    /// <summary>(old value, slot address): the old value of such a slot.</summary>
    public WasmFunction UnlinkAt(ITypeSymbol slotType)
    {
        slotType = D.Types.Canonical(slotType);
        var k = Types.Key(slotType);
        if (!_unlinkAt.TryGetValue(k, out var f))
            _unlinkAt[k] = f = Placeholder("arena.unlink at " + k, new FuncType([I, I], []), Kind.UnlinkAt, null, slotType);
        return f;
    }

    /// <summary>(destination, source, count) → whether it was done: references copied over such slots.</summary>
    public WasmFunction MoveAt() => _moveAt ??= Placeholder("arena.move at", new FuncType([I, I, I], [I]), Kind.MoveAt, null);

    // ------------------------------------------------------------------
    // analysis
    // ------------------------------------------------------------------

    /// <summary>Builds the type graph from the recorded sites and finds the cycle-capable components.</summary>
    public void Analyze()
    {
        // which sites have a store into an object that may be known elsewhere
        {
            var general = new Dictionary<WasmFunction, Site>();
            foreach (var s in _sites.Values) { general[s.Link] = s; general[s.LinkOwned] = s; }
            foreach (var f in D.Module.Functions)
                foreach (var ins in f.Body)
                    if (ins.Ref is WasmFunction w && general.TryGetValue(w, out var gs)) gs.General = true;
            // (a slot whose address is taken is stored through it by code that knows nothing of the object)
            foreach (var s in _sites.Values) if (s.AddressTaken) s.General = true;
        }
        var nodes = D.Constructed.Where(id => id != 0 && !D.DisplayById.ContainsKey(id) && !D.SyntheticById.ContainsKey(id))
            .Select(id => (id, t: D.TypeById[id])).ToList();
        int n = D.TypeById.Count;
        // the type graph: an edge from a container type to each type a site of it can hold, through one node per
        // site (per site and array type for arrays) so that the edges are containers plus targets, not their
        // product; such a node passes paths through, so the types' components and cycles are the same
        var subs = new SubtypeIndex(D.Types);
        foreach (var (id, t) in nodes) subs.Add(id, t is IArrayTypeSymbol ? null : t);
        // (asked for the same few types, object and the common interfaces among them, by thousands of sites: each
        // answer is kept; without that this was three minutes of the compiler compiling itself)
        var subtypes = new Dictionary<string, List<(int id, ITypeSymbol t)>>();
        List<(int id, ITypeSymbol t)> SubtypesOf(ITypeSymbol target)
        {
            string key = Types.Key(target);
            if (subtypes.TryGetValue(key, out var known)) return known;
            var r = new List<(int id, ITypeSymbol t)>();
            // (only an array is a subtype of an array type: the arrays are among the special ones)
            var cl = target is IArrayTypeSymbol ? [] : subs.Candidates(target);
            foreach (var id in cl == subs.All ? cl : cl.Concat(subs.Special))
                if (D.Types.IsSubtype(D.TypeById[id], target)) r.Add((id, D.TypeById[id]));
            return subtypes[key] = r;
        }
        D.Mark("    arenas: subtype index");
        long arrayTicks = 0, fieldTicks = 0, edges = 0, contTicks = 0, targTicks = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
        // (and what each site takes of them: the classes or the arrays that can be the container, the ids of the
        // objects that can be the value)
        var containerLists = new Dictionary<(string, bool), List<(int id, ITypeSymbol t)>>();
        List<(int id, ITypeSymbol t)> ContainersOf(ITypeSymbol target, bool arrays)
        {
            var key = (Types.Key(target), arrays);
            if (containerLists.TryGetValue(key, out var known)) return known;
            // (an immortal object is never freed: no cycle through it can leak, and it is never an arena's member)
            return containerLists[key] = SubtypesOf(target).Where(x => arrays ? x.t is IArrayTypeSymbol : x.t.TypeKind == TypeKind.Class && x.t is not IArrayTypeSymbol && !Types.IsImmortalType(x.t)).ToList();
        }
        var targetLists = new Dictionary<string, List<int>>();
        List<int> TargetsOf(ITypeSymbol target)
        {
            string key = Types.Key(target);
            if (targetLists.TryGetValue(key, out var known)) return known;
            return targetLists[key] = SubtypesOf(target).Where(x => !x.t.IsValueType && !Types.IsImmortalType(x.t)).Select(x => x.id).ToList();
        }
        var adj = new List<List<int>>();
        for (int i = 0; i < n; i++) adj.Add([]);
        var siteNodes = new Dictionary<Site, List<int>>();
        var hubSite = new Dictionary<int, Site>();
        foreach (var s in _sites.Values)
        {
            var hubs = new List<int>();
            siteNodes[s] = hubs;
            // (a slot whose address is taken is an edge like any other: a store through the address finds its object,
            // Rt.ArenaLinkAt)
            if (s.Values.Count == 0) continue;
            // (an immortal object is never freed: no cycle through it can leak, and it is never an arena's member)
            long tc = sw.ElapsedTicks;
            var containers = ContainersOf(s.Container, s.IsArray);
            contTicks += sw.ElapsedTicks - tc; tc = sw.ElapsedTicks;
            if (containers.Count == 0) continue;
            var targets = s.Values.Count == 1 ? TargetsOf(s.Values.Values.First()) : s.Values.Values.SelectMany(TargetsOf).Distinct().ToList();
            targTicks += sw.ElapsedTicks - tc;
            if (targets.Count == 0) continue;
            int Hub(IEnumerable<int> to) { int h = adj.Count; adj.Add([.. to]); hubs.Add(h); hubSite[h] = s; return h; }
            HashSet<int>? targetSet = null;
            long t0 = sw.ElapsedTicks;
            if (!s.IsArray)
            {
                int h = Hub(targets);
                foreach (var c in containers) adj[c.id].Add(h);
                edges += targets.Count + containers.Count;
            }
            else
                foreach (var c in containers)
                {
                    // a covariant array (string[] seen as object[]) only ever holds its own element type
                    var et = ((IArrayTypeSymbol)c.t).ElementType;
                    targetSet ??= [.. targets];
                    var held = et.IsValueType ? [] : SubtypesOf(et).Where(x => targetSet.Contains(x.id)).Select(x => x.id).ToList();
                    if (held.Count > 0) adj[c.id].Add(Hub(held));
                    edges += held.Count + 1;
                }
            if (s.IsArray) arrayTicks += sw.ElapsedTicks - t0; else fieldTicks += sw.ElapsedTicks - t0;
        }
        int total = adj.Count;
        D.Mark($"    arenas: graph ({_sites.Count} sites, {total} nodes, {edges} edges; containers {contTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, targets {targTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, array sites {arrayTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, field sites {fieldTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms of it)");

        // Tarjan's strongly connected components (iterative)
        var scc = new int[total];
        var index = new int[total]; var low = new int[total]; var onStack = new bool[total];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        int next = 0, sccCount = 0;
        for (int root = 0; root < total; root++)
        {
            if (index[root] >= 0 || adj[root].Count == 0) continue;
            var work = new Stack<(int v, int i)>();
            work.Push((root, 0));
            index[root] = low[root] = next++; stack.Push(root); onStack[root] = true;
            while (work.Count > 0)
            {
                var (v, i) = work.Pop();
                if (i < adj[v].Count)
                {
                    work.Push((v, i + 1));
                    int w = adj[v][i];
                    if (index[w] < 0)
                    {
                        index[w] = low[w] = next++; stack.Push(w); onStack[w] = true;
                        work.Push((w, 0));
                    }
                    else if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                    continue;
                }
                if (work.Count > 0) { var p = work.Peek().v; low[p] = Math.Min(low[p], low[v]); }
                if (low[v] != index[v]) continue;
                var comp = new List<int>();
                int x;
                do { x = stack.Pop(); onStack[x] = false; comp.Add(x); } while (x != v);
                // (no node has an edge to itself: a type's own cycle goes through a site's node)
                if (comp.Count < 2) continue;
                // (a component whose every edge is a field filled at construction: no objects can make that cycle)
                // (nor one whose other edges are fields only ever stored into objects nothing else knows yet)
                if (!comp.Any(m => hubSite.TryGetValue(m, out var hs) && !hs.Immutable && (hs.IsArray || hs.General || s_noFreshSites))) continue;
                sccCount++;
                foreach (var m in comp) scc[m] = sccCount;
            }
        }
        D.Mark("    arenas: components");
        // (numbered by their smallest type id: the numbers do not depend on the order the search met them)
        var renumber = new Dictionary<int, int>();
        foreach (var g in Enumerable.Range(0, n).Where(id => scc[id] != 0).GroupBy(id => scc[id]).OrderBy(g => g.Min()))
            renumber[g.Key] = renumber.Count + 1;
        for (int i = 0; i < total; i++) if (scc[i] != 0) scc[i] = renumber.GetValueOrDefault(scc[i], -1);
        _scc = scc[..n];
        for (int id = 0; id < n; id++) if (_scc[id] != 0) CycleTypes.Add(id);
        // The program's own classes among them are told to the programmer (section 4.3; as Lobster reports cycles, but
        // when building): nothing looks for a let-go part of such a group at run time, so where that matters the
        // cycle is the programmer's to break.
        foreach (var id in CycleTypes)
            if (D.TypeById[id] is INamedTypeSymbol { TypeKind: TypeKind.Class, IsImplicitlyDeclared: false } nt && !D.IsLibrary(nt)
                && nt.Locations.FirstOrDefault(l => l.IsInSource) is { } at && nt.CanBeReferencedByName)
                D.Report(Diags.CyclePossible, at, nt.ToDisplayString());
        // a site is cyclic when one of its nodes is on a cycle (then some container and a type it holds are on it)
        foreach (var (s, hubs) in siteNodes)
            s.Cyclic = hubs.Any(h => scc[h] != 0);
        Enabled = _sites.Values.Any(s => s.Cyclic);
        AnyArrayCycle = Enabled && CycleTypes.Any(id => D.TypeById[id] is IArrayTypeSymbol);
        if (Environment.GetEnvironmentVariable("CS2WASM_ARENA_DEBUG") == "1")
        {
            foreach (var g in CycleTypes.GroupBy(id => _scc[id]))
                Console.Error.WriteLine($"arena group {g.Key}: " + string.Join(", ", g.Select(id => Types.Key(D.TypeById[id]))));
            foreach (var s in _sites.Values.Where(s => s.Cyclic)) Console.Error.WriteLine("cyclic site " + s.Key);
        }
    }

    /// <summary>The cyclic field sites whose slots an object of this type has (for the arena visitor).</summary>
    public IEnumerable<int> CyclicFieldOffsets(ITypeSymbol t) =>
        _sites.Values.Where(s => s.Cyclic && !s.IsArray && D.Types.IsSubtype(t, s.Container)).Select(s => s.Offset).Distinct().Order();

    public bool HasCyclicElements(ITypeSymbol t) =>
        t is IArrayTypeSymbol && _sites.Values.Any(s => s.Cyclic && s.IsArray && D.Types.IsSubtype(t, s.Container));

    static readonly (string name, int arity)[] RuntimeMethods =
        [("ArenaLink", 3), ("ArenaUnlink", 3), ("ArenaVisitElement", 1), ("ArenaPin", 1), ("ArenaRetain", 1), ("ArenaRelease", 1), ("ArenaArrayCopy", 5), ("ArenaArrayClear", 3), ("ArenaVisit", 1),
         ("AllocObjectArena", 2), ("FreeObjectArena", 2), ("ArenaLinkContents", 1), ("ArenaLinkFresh", 3), ("ArenaUnlinkFresh", 3), ("ArenaLinkOwned", 3),
         ("ArenaLinkAt", 2), ("ArenaUnlinkAt", 2), ("ArenaMove", 3)];

    /// <summary>Queues the library's arena runtime for compilation (only when some site is cyclic).</summary>
    public void RequestRuntime()
    {
        foreach (var (name, arity) in RuntimeMethods) D.LibMethod("Cs2Wasm.Runtime.Rt", name, arity);
    }

    public WasmFunction RuntimeFn(string name, int arity) => D.LibMethod("Cs2Wasm.Runtime.Rt", name, arity).Fn;

    /// <summary>Replaces placeholder calls in every function: arena calls at cyclic sites, plain counting elsewhere.</summary>
    static readonly bool s_noInlineLinks = Environment.GetEnvironmentVariable("CS2WASM_NO_INLINE_LINKS") == "1";

    public void Resolve()
    {
        WasmFunction? link = null, unlink = null, pin = null, linkFresh = null, unlinkFresh = null, linkOwned = null;
        if (Enabled)
        {
            link = RuntimeFn("ArenaLink", 3);
            unlink = RuntimeFn("ArenaUnlink", 3);
            linkFresh = RuntimeFn("ArenaLinkFresh", 3);
            unlinkFresh = RuntimeFn("ArenaUnlinkFresh", 3);
            linkOwned = RuntimeFn("ArenaLinkOwned", 3);
            pin = RuntimeFn("ArenaPin", 1);
        }
        // A store by address asks the runtime only for an address in the heap: the statics and the first thread's
        // stack lie below it, and a slot there is no object's.
        WasmFunction? linkAt = null, unlinkAt = null, moveAt = null;
        if (Enabled)
        {
            moveAt = RuntimeFn("ArenaMove", 3);
            linkAt = D.Module.AddFunction("arena.link at", new FuncType([I, I], [I]));
            var le = new Emitter(linkAt);
            le.Get(1); le.I32Addr(new LateAddr(() => D.HeapBase)); le.Op(Op.I32LtU);
            le.If(); le.Get(0); le.Op(Op.Return); le.End();
            le.Get(0); le.Get(1); le.Call(RuntimeFn("ArenaLinkAt", 2));
            unlinkAt = D.Module.AddFunction("arena.unlink at", new FuncType([I, I], []));
            var ue = new Emitter(unlinkAt);
            ue.Get(1); ue.I32Addr(new LateAddr(() => D.HeapBase)); ue.Op(Op.I32LtU);
            ue.If(); ue.Get(0); ue.Call(D.Rt.Release); ue.Op(Op.Return); ue.End();
            ue.Get(0); ue.Get(1); ue.Call(RuntimeFn("ArenaUnlinkAt", 2));
        }
        var holdsCyclic = new Dictionary<string, bool>();
        bool HoldsCyclic(ITypeSymbol slotType)
        {
            var k = Types.Key(slotType);
            if (!holdsCyclic.TryGetValue(k, out var r)) holdsCyclic[k] = r = CycleTypes.Any(id => D.Types.IsSubtype(D.TypeById[id], slotType));
            return r;
        }
        foreach (var f in D.Module.Functions)
        {
            if (f.IsImport || f == linkAt || f == unlinkAt || !f.Body.Any(x => x.Op == Op.Call && x.Ref is WasmFunction w && _placeholders.ContainsKey(w))) continue;
            var body = new List<Instr>(f.Body.Count + 8);
            foreach (var ins in f.Body)
            {
                if (ins.Op != Op.Call || ins.Ref is not WasmFunction w || !_placeholders.TryGetValue(w, out var ph)) { body.Add(ins); continue; }
                var x = ins;
                switch (ph.kind)
                {
                    case Kind.Link or Kind.LinkFresh or Kind.LinkOwned:
                        // (value, container) -> value
                        if (Enabled && ph.site!.Cyclic)
                        {
                            if (ph.kind == Kind.Link || s_noInlineLinks) { x.Ref = ph.kind == Kind.Link ? link : ph.kind == Kind.LinkOwned ? linkOwned : linkFresh; body.Add(x); break; }
                            // Rt.ArenaLinkFresh and ArenaLinkOwned, their first question here and not behind a call:
                            // a container that is a plain object (count above 0; for an owned one, exactly 1) takes
                            // the value as a plain counted store. Only otherwise the call, of ArenaLink itself.
                            // (The word ArenaLink leaves for the ArenaUnlink that follows it is not reset here: a
                            // store that takes this way takes the plain way in its ArenaUnlinkFresh too, which does
                            // not read it, and every ArenaUnlink that does read it follows an ArenaLink that set it.)
                            int slot = f.Type.Params.Length + f.Locals.Count; f.Locals.Add(ValType.I32);
                            int cont = slot + 1; f.Locals.Add(ValType.I32);
                            int val = slot + 2; f.Locals.Add(ValType.I32);
                            void A(Op op, long imm = 0, object? r = null) => body.Add(new Instr { Op = op, Imm = imm, Imm2 = op == Op.I32Load ? 2 : 0, Ref = r, Pos = ins.Pos });
                            A(Op.LocalSet, slot); A(Op.LocalSet, cont); A(Op.LocalSet, val);
                            A(Op.Block, 0, ValType.I32);
                            A(Op.LocalGet, val);
                            A(Op.LocalGet, cont); A(Op.I32Eqz); A(Op.BrIf, 0);
                            A(Op.LocalGet, cont); A(Op.I32Load); A(Op.I32Const, ph.kind == Kind.LinkOwned ? 1 : 0); A(ph.kind == Kind.LinkOwned ? Op.I32Eq : Op.I32GtS); A(Op.BrIf, 0);
                            A(Op.Drop);
                            A(Op.LocalGet, val); A(Op.LocalGet, cont); A(Op.LocalGet, slot); A(Op.Call, 0, link);
                            A(Op.End);
                        }
                        else { x.Op = Op.Drop; x.Ref = null; body.Add(x); body.Add(x); }
                        break;
                    case Kind.Unlink or Kind.UnlinkFresh:
                        // (old, container) -> ()
                        if (Enabled && ph.site!.Cyclic)
                        {
                            if (ph.kind == Kind.Unlink || s_noInlineLinks) { x.Ref = ph.kind == Kind.Unlink ? unlink : unlinkFresh; body.Add(x); break; }
                            // Rt.ArenaUnlinkFresh, likewise: a plain container's old value is released as any
                            int slot = f.Type.Params.Length + f.Locals.Count; f.Locals.Add(ValType.I32);
                            int cont = slot + 1; f.Locals.Add(ValType.I32);
                            int old = slot + 2; f.Locals.Add(ValType.I32);
                            void A(Op op, long imm = 0, object? r = null) => body.Add(new Instr { Op = op, Imm = imm, Imm2 = op == Op.I32Load ? 2 : 0, Ref = r, Pos = ins.Pos });
                            A(Op.LocalSet, slot); A(Op.LocalSet, cont); A(Op.LocalSet, old);
                            A(Op.Block);
                            A(Op.Block);
                            A(Op.LocalGet, cont); A(Op.I32Eqz); A(Op.BrIf, 0);
                            A(Op.LocalGet, cont); A(Op.I32Load); A(Op.I32Const, 0); A(Op.I32GtS); A(Op.BrIf, 0);
                            A(Op.LocalGet, old); A(Op.LocalGet, cont); A(Op.LocalGet, slot); A(Op.Call, 0, unlink);
                            A(Op.Br, 1);
                            A(Op.End);
                            A(Op.LocalGet, old); A(Op.Call, 0, D.Rt.Release);
                            A(Op.End);
                        }
                        else { var d = x; d.Op = Op.Drop; d.Ref = null; body.Add(d); body.Add(d); x.Ref = D.Rt.Release; body.Add(x); }
                        break;
                    case Kind.LinkAt:
                        // (value, slot) -> value
                        if (Enabled && HoldsCyclic(ph.pinType!)) { x.Ref = linkAt; body.Add(x); }
                        else { x.Op = Op.Drop; x.Ref = null; body.Add(x); }
                        break;
                    case Kind.UnlinkAt:
                        // (old, slot) -> ()
                        if (Enabled && HoldsCyclic(ph.pinType!)) { x.Ref = unlinkAt; body.Add(x); }
                        else { var d = x; d.Op = Op.Drop; d.Ref = null; body.Add(d); x.Ref = D.Rt.Release; body.Add(x); }
                        break;
                    case Kind.MoveAt:
                        // (destination, source, count) -> done
                        if (Enabled && AnyArrayCycle) { x.Ref = moveAt; body.Add(x); }
                        else
                        {
                            var d = x; d.Op = Op.Drop; d.Ref = null; body.Add(d); body.Add(d); body.Add(d);
                            body.Add(new Instr { Op = Op.I32Const, Imm = 0, Pos = ins.Pos });
                        }
                        break;
                    case Kind.Pin:
                        if (Enabled && CycleTypes.Any(id => D.Types.IsSubtype(D.TypeById[id], ph.pinType!))) { x.Ref = pin; body.Add(x); }
                        break;
                }
            }
            f.Body = body;
        }
    }
}
