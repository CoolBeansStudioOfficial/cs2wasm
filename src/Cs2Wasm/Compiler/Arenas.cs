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
        public int Offset; // field sites
        public readonly Dictionary<string, ITypeSymbol> Values = [];
        public bool AddressTaken;
        public bool Cyclic;
        public required WasmFunction Link, Unlink;

        public void AddValue(ITypeSymbol? t)
        {
            if (t == null || t.TypeKind == TypeKind.Error) return;
            Values.TryAdd(Types.Key(t), t);
        }
    }

    enum Kind { Link, Unlink, Pin }

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
        s.Link = Placeholder("arena.link " + key, new FuncType([I, I], [I]), Kind.Link, s);
        s.Unlink = Placeholder("arena.unlink " + key, new FuncType([I, I], []), Kind.Unlink, s);
        _sites[key] = s;
        return s;
    }

    /// <summary>The site for a reference-typed instance field of a class.</summary>
    public Site? FieldSite(ITypeSymbol owner, FieldSlot slot)
    {
        owner = D.Types.Canonical(owner);
        if (owner.IsValueType || !Types.IsRef(slot.Type)) return null;
        if (ImmutableEdge(owner, slot)) return null;
        return Get("f " + Types.Key(owner) + " " + slot.Offset, owner, false, slot.Offset);
    }

    readonly Dictionary<string, bool> _keepsThis = [];

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
                        if (!IsWrite(fr)) continue;
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
                Microsoft.CodeAnalysis.Operations.IReturnOperation when o.Type != null && false => true,
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

    // ------------------------------------------------------------------
    // analysis
    // ------------------------------------------------------------------

    /// <summary>Builds the type graph from the recorded sites and finds the cycle-capable components.</summary>
    public void Analyze()
    {
        var nodes = D.Constructed.Where(id => id != 0 && !D.DisplayById.ContainsKey(id) && !D.SyntheticById.ContainsKey(id))
            .Select(id => (id, t: D.TypeById[id])).ToList();
        int n = D.TypeById.Count;
        // the type graph: an edge from a container type to each type a site of it can hold, through one node per
        // site (per site and array type for arrays) so that the edges are containers plus targets, not their
        // product; such a node passes paths through, so the types' components and cycles are the same
        var subs = new SubtypeIndex(D.Types);
        foreach (var (id, t) in nodes) subs.Add(id, t is IArrayTypeSymbol ? null : t);
        IEnumerable<(int id, ITypeSymbol t)> SubtypesOf(ITypeSymbol target)
        {
            var cl = subs.Candidates(target);
            foreach (var id in cl == subs.All ? cl : cl.Concat(subs.Special))
                if (D.Types.IsSubtype(D.TypeById[id], target)) yield return (id, D.TypeById[id]);
        }
        var adj = new List<List<int>>();
        for (int i = 0; i < n; i++) adj.Add([]);
        var siteNodes = new Dictionary<Site, List<int>>();
        foreach (var s in _sites.Values)
        {
            var hubs = new List<int>();
            siteNodes[s] = hubs;
            if (s.AddressTaken || s.Values.Count == 0) continue;
            // (an immortal object is never freed: no cycle through it can leak, and it is never an arena's member)
            var containers = SubtypesOf(s.Container).Where(x => s.IsArray ? x.t is IArrayTypeSymbol : x.t.TypeKind == TypeKind.Class && x.t is not IArrayTypeSymbol && !Types.IsImmortalType(x.t)).ToList();
            if (containers.Count == 0) continue;
            var targets = s.Values.Values.SelectMany(SubtypesOf).Where(x => !x.t.IsValueType && !Types.IsImmortalType(x.t)).Select(x => x.id).Distinct().ToList();
            if (targets.Count == 0) continue;
            int Hub(IEnumerable<int> to) { int h = adj.Count; adj.Add([.. to]); hubs.Add(h); return h; }
            if (!s.IsArray)
            {
                int h = Hub(targets);
                foreach (var c in containers) adj[c.id].Add(h);
            }
            else
                foreach (var c in containers)
                {
                    // a covariant array (string[] seen as object[]) only ever holds its own element type
                    var et = ((IArrayTypeSymbol)c.t).ElementType;
                    var held = targets.Where(t => D.Types.IsSubtype(D.TypeById[t], et)).ToList();
                    if (held.Count > 0) adj[c.id].Add(Hub(held));
                }
        }
        int total = adj.Count;

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
                sccCount++;
                foreach (var m in comp) scc[m] = sccCount;
            }
        }
        // (numbered by their smallest type id: the numbers do not depend on the order the search met them)
        var renumber = new Dictionary<int, int>();
        foreach (var g in Enumerable.Range(0, n).Where(id => scc[id] != 0).GroupBy(id => scc[id]).OrderBy(g => g.Min()))
            renumber[g.Key] = renumber.Count + 1;
        for (int i = 0; i < total; i++) if (scc[i] != 0) scc[i] = renumber.GetValueOrDefault(scc[i], -1);
        _scc = scc[..n];
        for (int id = 0; id < n; id++) if (_scc[id] != 0) CycleTypes.Add(id);
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
        [("ArenaLink", 2), ("ArenaUnlink", 2), ("ArenaPin", 1), ("ArenaRetain", 1), ("ArenaRelease", 1), ("ArenaArrayCopy", 5), ("ArenaArrayClear", 3), ("ArenaVisit", 1),
         ("AllocObjectArena", 2), ("FreeObjectArena", 2), ("ArenaLinkContents", 1)];

    /// <summary>Queues the library's arena runtime for compilation (only when some site is cyclic).</summary>
    public void RequestRuntime()
    {
        foreach (var (name, arity) in RuntimeMethods) D.LibMethod("Cs2Wasm.Runtime.Rt", name, arity);
    }

    public WasmFunction RuntimeFn(string name, int arity) => D.LibMethod("Cs2Wasm.Runtime.Rt", name, arity).Fn;

    /// <summary>Replaces placeholder calls in every function: arena calls at cyclic sites, plain counting elsewhere.</summary>
    public void Resolve()
    {
        WasmFunction? link = null, unlink = null, pin = null;
        if (Enabled)
        {
            link = RuntimeFn("ArenaLink", 2);
            unlink = RuntimeFn("ArenaUnlink", 2);
            pin = RuntimeFn("ArenaPin", 1);
        }
        foreach (var f in D.Module.Functions)
        {
            if (f.IsImport || !f.Body.Any(x => x.Op == Op.Call && x.Ref is WasmFunction w && _placeholders.ContainsKey(w))) continue;
            var body = new List<Instr>(f.Body.Count + 8);
            foreach (var ins in f.Body)
            {
                if (ins.Op != Op.Call || ins.Ref is not WasmFunction w || !_placeholders.TryGetValue(w, out var ph)) { body.Add(ins); continue; }
                var x = ins;
                switch (ph.kind)
                {
                    case Kind.Link:
                        // (value, container) -> value
                        if (Enabled && ph.site!.Cyclic) { x.Ref = link; body.Add(x); }
                        else { x.Op = Op.Drop; x.Ref = null; body.Add(x); }
                        break;
                    case Kind.Unlink:
                        // (old, container) -> ()
                        if (Enabled && ph.site!.Cyclic) { x.Ref = unlink; body.Add(x); }
                        else { var d = x; d.Op = Op.Drop; d.Ref = null; body.Add(d); x.Ref = D.Rt.Release; body.Add(x); }
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
