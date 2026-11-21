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
internal sealed partial class Arenas
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
        /// <summary>A field site's field (or the property that owns its storage).</summary>
        public ISymbol? Member;
        /// <summary>The slot's declared type (an array's element type): what a release of its old value counts.</summary>
        public ITypeSymbol? SlotType;
        /// <summary>A struct's reference inside a class's object or a box (AddStructSites): its field's type.</summary>
        public ITypeSymbol? LeafType;
        /// <summary>The type of what the slot holds, for Sharing.SiteTypeIds.</summary>
        public ITypeSymbol? DeclaredType => SlotType ?? LeafType;
        public readonly Dictionary<string, ITypeSymbol> Values = [];
        public bool AddressTaken;
        /// <summary>The slot's address went where a ref of it may be kept or made into another (a ref local, a ref
        /// return, a span, a pointer, a call the compiler does not see into): a store through any ref of the slot's
        /// type can land here (Sharing.SiteTypeIds). Else its address went only to the `ref` parameters in RefUses
        /// ("method-key ordinal", Sharing.RefParamUse), or to Interlocked's and Volatile's.</summary>
        public bool RefDirty;
        public readonly HashSet<string> RefUses = [];
        /// <summary>Some store through this site is into an object that other code may know (Analyze: the site's
        /// general link is called somewhere). A field without one is only ever stored into objects that nothing else
        /// can reach yet (FreshStores.cs), and no such store closes a cycle: like a field filled at construction,
        /// it can be part of a cycle only where some other edge closes it.</summary>
        public bool General;
        public bool Cyclic;
        /// <summary>The slots of a closure's or a state machine's object (no type of the program's: its type id).</summary>
        public int ContainerId = -1;
        /// <summary>Such objects among what the slot holds (a delegate's target, a closure's enclosing one).</summary>
        public readonly HashSet<int> TargetIds = [];
        public required WasmFunction Link, Unlink;
        /// <summary>The same for a store into an object only the storing code knows (FreshStores.cs).</summary>
        public WasmFunction LinkFresh = null!, UnlinkFresh = null!;
        /// <summary>The link for a store into an object held in a local that owns it (OwnedLocals in Expressions.cs):
        /// when that local's is the only reference there is, nothing can reach the object and no cycle closes.</summary>
        public WasmFunction LinkOwned = null!;
        /// <summary>The link for a store of an isolated value (FreshStores.cs): it closes no cycle, so it does not make
        /// the site general; on a site that is cyclic through other stores it is the general link.</summary>
        public WasmFunction LinkIso = null!;
        /// <summary>Threads: around a store's link, exchange and unlink, the runtime lock (on a site that can close a
        /// cycle, in an object another thread may reach): the slot's bits say what it holds, and two threads' stores
        /// into one slot, each linked before its exchange and unlinked after, left them saying the other's value.</summary>
        public WasmFunction StoreLock = null!, StoreUnlock = null!;

        /// <summary>Where the values stored here come from (Sharing.cs): the sources given, else the static type.</summary>
        public readonly List<Sharing.Src> Sources = [];

        public void AddValue(ITypeSymbol? t, IEnumerable<Sharing.Src>? srcs = null)
        {
            if (srcs != null) Sources.AddRange(srcs);
            else if (t != null && t.TypeKind != TypeKind.Error) Sources.Add(new Sharing.Src(Sharing.K.Top, Type: t));
            if (t == null || t.TypeKind == TypeKind.Error) return;
            Values.TryAdd(Types.Key(t), t);
        }
    }

    enum Kind { Link, Unlink, Pin, LinkFresh, UnlinkFresh, LinkOwned, LinkAt, UnlinkAt, MoveAt, LinkIso, StoreLock, StoreUnlock }

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

    public Site? SiteByKey(string key) => _sites.TryGetValue(key, out var s) ? s : null;
    public IReadOnlyCollection<Site> AllSites => _sites.Values;

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
        s.LinkIso = Placeholder("arena.link isolated " + key, new FuncType([I, I, I], [I]), Kind.LinkIso, s);
        // (container, slot address) -> whether it took the lock (1: a store's region, Rt.StoreLockIf); (that) -> ()
        s.StoreLock = Placeholder("arena.store lock " + key, new FuncType([I, I], [I]), Kind.StoreLock, s);
        s.StoreUnlock = Placeholder("arena.store unlock " + key, new FuncType([I], []), Kind.StoreUnlock, s);
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
        site.SlotType ??= D.Types.Canonical(slot.Type);
        site.Member ??= slot.Symbol;
        site.Immutable = ImmutableEdge(owner, slot);
        return site;
    }

    /// <summary>A reference slot of a class's objects that code stores to by its address alone (a field-like
    /// event's, in its accessors): a site, for what the slot's type can hold.</summary>
    public void SlotByAddress(ITypeSymbol owner, int offset, ITypeSymbol slotType)
    {
        owner = D.Types.Canonical(owner);
        var s = Get("f " + Types.Key(owner) + " " + offset, owner, false, offset);
        s.AddressTaken = true;
        s.RefDirty = true;
        s.AddValue(D.Types.Canonical(slotType));
    }

    /// <summary>CS2WASM_ARENA_SOURCES=0: a field's targets are the subtypes of what its stores' static types are, not
    /// narrowed by where the values come from (to compare).</summary>
    static readonly bool s_arenaSources = Environment.GetEnvironmentVariable("CS2WASM_ARENA_SOURCES") != "0";
    /// <summary>Sources are recorded for the type graph (Sharing.SiteTypeIds), also in a program without threads.</summary>
    public static bool SourcesOn => s_arenaSources;
    /// <summary>More types than this from a site's sources: no narrower than its static types' (CS2WASM_FLOW_CAP).</summary>
    static readonly int s_flowCap = int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_FLOW_CAP"), out var fc) && fc > 0 ? fc : 2000;
    static readonly bool s_arenaDebug =Environment.GetEnvironmentVariable("CS2WASM_ARENA_DEBUG") == "1";
    List<(Site, int)>? _wide;
    static readonly bool s_noFreshSites =Environment.GetEnvironmentVariable("CS2WASM_NO_FRESH_SITES") == "1";
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
        var s = Get("a " + Types.Key(t), t, true, 0);
        s.SlotType ??= et;
        return s;
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
    WasmFunction? _linkAtFn, _unlinkAtFn;
    WasmFunction? _moveAt;

    /// <summary>(value, slot address) → value: a store of a reference into a slot the code knows by its address
    /// alone (through a `ref`, a span, a struct's own method). The runtime finds the object the slot is in.
    /// `container`, where known, is the static type of the object the slot is in (a class whose struct field holds it, an array of structs): a container no object of
    /// a cycle-capable type can be is never a group's member, and the store is plain, as a site's that is not
    /// cyclic.</summary>
    public WasmFunction LinkAt(ITypeSymbol slotType, ITypeSymbol? container = null)
    {
        slotType = D.Types.Canonical(slotType);
        if (container != null) container = D.Types.Canonical(container);
        var k = Types.Key(slotType) + (container != null ? " in " + Types.Key(container) : "");
        if (!_linkAt.TryGetValue(k, out var f))
        {
            _linkAt[k] = f = Placeholder("arena.link at " + k, new FuncType([I, I], [I]), Kind.LinkAt, null, slotType);
            if (container != null) _atContainer[f] = container;
        }
        return f;
    }
    readonly Dictionary<WasmFunction, ITypeSymbol> _atContainer = [];

    /// <summary>(old value, slot address): the old value of such a slot.</summary>
    /// <summary>Threads: the runtime lock around a store by address (its link, exchange and unlink), where the slot can
    /// hold an object of a cycle-capable type (Site.StoreLock says why); nothing elsewhere.</summary>
    public WasmFunction StoreLockAt(ITypeSymbol slotType, bool enter)
    {
        slotType = D.Types.Canonical(slotType);
        var k = (enter ? "lock " : "unlock ") + Types.Key(slotType);
        if (!_storeLockAt.TryGetValue(k, out var f))
            // (slot address) -> whether it took the lock; (that) -> ()
            _storeLockAt[k] = f = Placeholder("arena.store " + k, enter ? new FuncType([I], [I]) : new FuncType([I], []), enter ? Kind.StoreLock : Kind.StoreUnlock, null, slotType);
        return f;
    }
    readonly Dictionary<string, WasmFunction> _storeLockAt = [];

    public WasmFunction UnlinkAt(ITypeSymbol slotType, ITypeSymbol? container = null)
    {
        slotType = D.Types.Canonical(slotType);
        if (container != null) container = D.Types.Canonical(container);
        var k = Types.Key(slotType) + (container != null ? " in " + Types.Key(container) : "");
        if (!_unlinkAt.TryGetValue(k, out var f))
        {
            _unlinkAt[k] = f = Placeholder("arena.unlink at " + k, new FuncType([I, I], []), Kind.UnlinkAt, null, slotType);
            if (container != null) _atContainer[f] = container;
        }
        return f;
    }

    /// <summary>(destination, source, count) → whether it was done: references copied over such slots.</summary>
    public WasmFunction MoveAt() => _moveAt ??= Placeholder("arena.move at", new FuncType([I, I, I], [I]), Kind.MoveAt, null);

    readonly List<(Site site, Display env)> _delegateEnvs = [];

    /// <summary>A delegate is made with this target: an object of the type, or a closure's or a state machine's
    /// object. The slot is an edge like a field's (the delegate is new then: the store itself closes nothing, the
    /// field it is stored into does).</summary>
    public void DelegateTarget(ITypeSymbol delegateType, ITypeSymbol? targetType, Display? env)
    {
        if (targetType == null && env == null) return;
        var dt = D.Types.Canonical(delegateType);
        var s = Get("f " + Types.Key(dt) + " " + FunctionCompiler.DelegateTargetOffset, dt, false, FunctionCompiler.DelegateTargetOffset);
        if (targetType != null) s.AddValue(D.Types.Canonical(targetType));
        if (env != null) _delegateEnvs.Add((s, env));
    }

    /// <summary>Whether an object of the type with this id can be in a slot of this type (a closure's object has no
    /// type of the program's: in a slot of type object, or of an interface its state machine implements).</summary>
    bool IdIs(int id, ITypeSymbol slotType)
    {
        if (D.DisplayById.ContainsKey(id))
            return Types.IsObject(slotType) || D.SyntheticById.TryGetValue(id, out var st) && st.Interfaces.Any(i => D.Types.IsSubtype(i, slotType));
        return D.Types.IsSubtype(D.TypeById[id], slotType);
    }

    static readonly bool s_noDisplaySites = Environment.GetEnvironmentVariable("CS2WASM_NO_DISPLAY_SITES") == "1";

    /// <summary>The slots of the objects the compiler makes: a closure's captured variables and its enclosing
    /// closure, a state machine's variables, an anonymous type's properties, a delegate's target. A store into a
    /// captured variable has the slot's address (Arenas.LinkAt), the others are filled when the object is made.</summary>
    void AddDisplaySites()
    {
        if (s_noDisplaySites) return;
        foreach (var (s, env) in _delegateEnvs) if (env.TypeId > 0) s.TargetIds.Add(env.TypeId);
        foreach (var (id, d) in D.DisplayById.OrderBy(x => x.Key).ToList())
        {
            if (!D.Constructed.Contains(id)) continue;
            Site At(int off) { var s = Get("d " + id + " " + off, D.ObjectType, false, off); s.ContainerId = id; return s; }
            foreach (var v in d.Vars)
            {
                var (off, t) = d.Slots[v];
                if (Types.IsRef(t)) { if (!Types.IsImmortalType(t)) { var s = At(off); s.AddValue(t); s.General = true; } }
                else if (Types.IsStruct(t) && D.Types.LayoutOf(t).HasRefs)
                    foreach (var (lo, lt) in RefLeaves(t, off)) { var s = At(lo); s.AddValue(lt); s.General = true; }
            }
            if (d.Parent is { TypeId: > 0 } p) At(12).TargetIds.Add(p.TypeId);
        }
        foreach (var id in D.SyntheticById.Keys.OrderBy(x => x).ToList())
        {
            if (D.DisplayById.ContainsKey(id) || !D.Constructed.Contains(id) || D.TypeById[id] is not INamedTypeSymbol { IsAnonymousType: true } at) continue;
            var ct = D.Types.Canonical(at);
            foreach (var f in D.Types.LayoutOf(ct).Fields)
                if (Types.IsRef(f.Type) && !Types.IsImmortalType(f.Type)) Get("f " + Types.Key(ct) + " " + f.Offset, ct, false, f.Offset).AddValue(f.Type);
        }
    }

    // ------------------------------------------------------------------
    // analysis
    // ------------------------------------------------------------------

    /// <summary>The reference slots inside a struct (offset from `baseOff`, type), nested structs gone through; not
    /// those of a type whose objects are never freed (a string: no cycle through it is garbage).</summary>
    public IEnumerable<(int off, ITypeSymbol type)> RefLeaves(ITypeSymbol structType, int baseOff = 0)
    {
        foreach (var f in D.Types.LayoutOf(structType).Fields)
        {
            if (Types.IsRef(f.Type)) { if (!Types.IsImmortalType(f.Type)) yield return (baseOff + f.Offset, f.Type); }
            else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs)
                foreach (var x in RefLeaves(f.Type, baseOff + f.Offset)) yield return x;
        }
    }

    static readonly bool s_noStructSites = Environment.GetEnvironmentVariable("CS2WASM_NO_STRUCT_SITES") == "1";

    /// <summary>RefLeaves, each with where its values come from (Sharing.cs): what the struct's field holds wherever
    /// the struct is (Sharing.StructFieldNode: every store into that field of any such struct is recorded there).
    /// Null, any object of the leaf's type, inside an inline array (its elements past the first have no field).</summary>
    IEnumerable<(int off, ITypeSymbol type, List<Sharing.Src>? srcs)> RefLeafSources(ITypeSymbol structType, int baseOff, bool inline = false)
    {
        var st = D.Types.Canonical(structType);
        inline |= Types.InlineArrayLength(st) != null;
        foreach (var f in D.Types.LayoutOf(st).Fields)
        {
            if (Types.IsRef(f.Type))
            {
                if (Types.IsImmortalType(f.Type)) continue;
                yield return (baseOff + f.Offset, f.Type, D.Sharing == null || inline ? null : [new Sharing.Src(Sharing.K.Node, Sharing.StructFieldNode(st, f.Symbol), Type: D.Types.Canonical(f.Type))]);
            }
            else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs)
                foreach (var x in RefLeafSources(f.Type, baseOff + f.Offset, inline)) yield return x;
        }
    }

    /// <summary>A reference inside a struct that is a field of a class is a slot of the class's object like any other
    /// (a tuple field, a nullable struct, a struct of the program's): a site at its offset, for whatever its type can
    /// hold. Nothing records the stores (a struct is copied whole, and written through its own methods): the
    /// copies and the stores by address ask the runtime (Arenas.LinkAt), which asks the object's type.</summary>
    void AddStructSites()
    {
        if (s_noStructSites) return;
        foreach (var t in D.ConstructedTypes().ToList())
        {
            // an array of structs that hold references (a dictionary's entries, a list of tuples): its elements' slots
            if (t is IArrayTypeSymbol at)
            {
                var et = D.Types.Canonical(at.ElementType);
                if (!Types.IsStruct(et) || !D.Types.LayoutOf(et).HasRefs) continue;
                var act = D.Types.Canonical(at);
                foreach (var (_, lt) in RefLeaves(et))
                {
                    var s = Get("a " + Types.Key(act), act, true, 0);
                    s.AddValue(lt);
                    s.General = true;
                }
                continue;
            }
            // a struct in a box (a struct stored as an object, or as an interface it implements): the box's slots
            if (t.IsValueType)
            {
                var bt = D.Types.Canonical(t);
                if (!Types.IsStruct(bt) || !D.Types.LayoutOf(bt).HasRefs) continue;
                foreach (var (off, lt, srcs) in RefLeafSources(bt, FunctionCompilerHelpers.BoxDataOffset(D.Types, bt)))
                {
                    var s = Get("f " + Types.Key(bt) + " " + off, bt, false, off);
                    s.LeafType ??= D.Types.Canonical(lt);
                    s.AddValue(lt, srcs);
                    s.General = true;
                }
                continue;
            }
            if (t.TypeKind != TypeKind.Class || Types.IsImmortalType(t)) continue;
            var ct = D.Types.Canonical(t);
            foreach (var f in D.Types.LayoutOf(ct).Fields)
            {
                if (!Types.IsStruct(f.Type) || !D.Types.LayoutOf(f.Type).HasRefs) continue;
                foreach (var (off, lt, srcs) in RefLeafSources(f.Type, f.Offset))
                {
                    var s = Get("f " + Types.Key(ct) + " " + off, ct, false, off);
                    s.LeafType ??= D.Types.Canonical(lt);
                    s.AddValue(lt, srcs);
                    s.General = true;
                }
            }
        }
    }

    readonly List<(Site site, ITypeSymbol type)> _reflectStores = [];
    /// <summary>A field reflection can store into (FieldInfo.SetValue, Reflection.cs): any object of its type, if the
    /// program can call SetValue.</summary>
    public void ReflectStore(Site s, ITypeSymbol fieldType) => _reflectStores.Add((s, D.Types.Canonical(fieldType)));

    /// <summary>Builds the type graph from the recorded sites and finds the cycle-capable components.</summary>
    public void Analyze()
    {
        AddStructSites();
        AddDisplaySites();
        if (_reflectStores.Count > 0 && D.AllMethods.Any(m => m.Compiled && m.Sym.Name is "SetValue" or "SetValueDirect" && m.Sym.ContainingType?.Name is "RuntimeFieldInfo" or "FieldInfo"))
            foreach (var (s, t) in _reflectStores) { s.AddValue(t); s.General = true; }
        // (what stores by address can put where: RefStores.cs)
        AddRefStores();
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
        var nodes = D.Constructed.Where(id => id != 0 && !D.DisplayById.ContainsKey(id) && (!D.SyntheticById.ContainsKey(id) || D.TypeById[id] is INamedTypeSymbol { IsAnonymousType: true }))
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
            return containerLists[key] = SubtypesOf(target).Where(x => arrays ? x.t is IArrayTypeSymbol : (x.t.TypeKind is TypeKind.Class or TypeKind.Delegate || x.t.IsValueType) && x.t is not IArrayTypeSymbol && !Types.IsImmortalType(x.t)).ToList();
        }
        var targetLists = new Dictionary<string, List<int>>();
        var machineIds = s_noDisplaySites ? [] : D.SyntheticById.Keys.Where(id => D.DisplayById.ContainsKey(id) && D.Constructed.Contains(id)).OrderBy(x => x).ToList();
        List<int> TargetsOf(ITypeSymbol target)
        {
            string key = Types.Key(target);
            if (targetLists.TryGetValue(key, out var known)) return known;
            // (a struct that holds references is an object when it is boxed: a slot of type object or of an interface
            // can hold it)
            var r = SubtypesOf(target).Where(x => (!x.t.IsValueType || Types.IsStruct(x.t) && D.Types.LayoutOf(x.t).HasRefs) && !Types.IsImmortalType(x.t)).Select(x => x.id).ToList();
            // (and a state machine's object, through an interface it implements: an iterator in an IEnumerable's slot)
            foreach (var sid in machineIds) if (IdIs(sid, target)) r.Add(sid);
            return targetLists[key] = r;
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
            if (s.Values.Count == 0 && s.TargetIds.Count == 0) continue;
            // (an immortal object is never freed: no cycle through it can leak, and it is never an arena's member)
            long tc = sw.ElapsedTicks;
            var containers = s.ContainerId >= 0 ? [(s.ContainerId, (ITypeSymbol)D.ObjectType)] : ContainersOf(s.Container, s.IsArray);
            contTicks += sw.ElapsedTicks - tc; tc = sw.ElapsedTicks;
            if (containers.Count == 0) continue;
            var targets = s.Values.Count == 1 ? TargetsOf(s.Values.Values.First()) : s.Values.Values.SelectMany(TargetsOf).Distinct().ToList();
            if (s.TargetIds.Count > 0) targets = [.. targets.Concat(s.TargetIds.Order()).Distinct()];
            // (what a class's field holds by where its values come from, when that is
            // narrower. Not an array's elements: references also arrive there by the runtime's own copies, Array.Copy,
            // SetValue and Buffer.Memmove's, which no recorded store says)
            if (SourcesOn && (!s.IsArray || Sharing.OptimisticFlow) && s.ContainerId < 0 && D.Sharing is { } shf && targets.Count > 8 && shf.SiteTypeIds(s, s_flowCap) is { } refined)
            {
                var keep = new HashSet<int>(targets);
                var narrowed = refined.Where(keep.Contains).Order().ToList();
                if (s_arenaDebug && narrowed.Count < targets.Count) Console.Error.WriteLine($"narrowed site {s.Key}: {targets.Count} to {narrowed.Count}");
                targets = narrowed;
            }
            targTicks += sw.ElapsedTicks - tc;
            if (targets.Count == 0) continue;
            // (CS2WASM_ARENA_DEBUG: the sites that join thousands of types, an object slot's, the hubs of a large group)
            if (targets.Count > 1000 && s_arenaDebug) (_wide ??= []).Add((s, targets.Count));
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
                    // (an array of structs holds what the structs' reference slots can: the site's values)
                    var held = et.IsValueType ? targets : SubtypesOf(et).Where(x => targetSet.Contains(x.id)).Select(x => x.id).ToList();
                    if (held.Count > 0) adj[c.id].Add(Hub(held));
                    edges += held.Count + 1;
                }
            if (s.IsArray) arrayTicks += sw.ElapsedTicks - t0; else fieldTicks += sw.ElapsedTicks - t0;
        }
        int total = adj.Count;
        D.Mark($"    arenas: graph ({_sites.Count} sites, {total} nodes, {edges} edges; containers {contTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, targets {targTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, array sites {arrayTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, field sites {fieldTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms of it)");

        var scc = Components(adj, hubSite);
        // (owned arrays: the fields whose arrays are never written once there lead to arrays of their own, OwnedArrays.cs)
        if (!Driver.NoOwnedArrays && SealOwnedArrays(adj, hubSite, siteNodes, ref scc, n)) total = adj.Count;
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
        _cyclicByContainer = null; _cyclicByDisplay = null;
        Enabled = _sites.Values.Any(s => s.Cyclic);
        AnyArrayCycle = Enabled && CycleTypes.Any(id => D.TypeById[id] is IArrayTypeSymbol);
        if (Environment.GetEnvironmentVariable("CS2WASM_ARENA_DEBUG") == "1")
        {
            foreach (var g in CycleTypes.GroupBy(id => _scc[id]))
                Console.Error.WriteLine($"arena group {g.Key}: " + string.Join(", ", g.Select(id => Types.Key(D.TypeById[id]))));
            foreach (var s in _sites.Values.Where(s => s.Cyclic)) Console.Error.WriteLine("cyclic site " + s.Key);
            foreach (var (s, c) in _wide ?? [])
                Console.Error.WriteLine($"wide site {s.Key} cyclic={s.Cyclic} general={s.General} targets={c} values: {string.Join(", ", s.Values.Keys.Take(8))}");
        }
    }

    /// <summary>Tarjan's strongly connected components of the type graph (iterative): a component's number for each node
    /// on a cycle that some store can close, 0 for the others.</summary>
    int[] Components(List<List<int>> adj, Dictionary<int, Site> hubSite)
    {
        int total = adj.Count;
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
                // (CS2WASM_ARENA_DEBUG: what makes the component cycle-capable: its sites that are neither filled at
                // construction nor only stored into new objects)
                if (s_arenaDebug)
                    foreach (var hs in comp.Where(m => hubSite.ContainsKey(m)).Select(m => hubSite[m]).Distinct().Where(hs => !hs.Immutable && (hs.IsArray || hs.General)).Take(40))
                        Console.Error.WriteLine($"closing site (component of {comp.Count} nodes) {hs.Key} array={hs.IsArray} general={hs.General}");
                foreach (var m in comp) scc[m] = sccCount;
            }
        }
        return scc;
    }

    /// <summary>The cyclic field sites whose slots an object of this type has (for the arena visitor).</summary>
    public IEnumerable<int> CyclicFieldOffsets(int id)
    {
        // (by container, once: asked for every cycle-capable type, a search of all sites for each was most of the
        // compiler's own build, 17 of its 24 minutes)
        if (_cyclicByContainer == null)
        {
            _cyclicByContainer = [];
            _cyclicByDisplay = [];
            foreach (var s in _sites.Values)
            {
                if (!s.Cyclic) continue;
                if (s.ContainerId >= 0) { if (!_cyclicByDisplay.TryGetValue(s.ContainerId, out var dl)) _cyclicByDisplay[s.ContainerId] = dl = []; dl.Add(s.Offset); }
                else if (!s.IsArray) { var k = Types.Key(D.Types.Canonical(s.Container)); if (!_cyclicByContainer.TryGetValue(k, out var cl)) _cyclicByContainer[k] = cl = []; cl.Add(s.Offset); }
            }
        }
        if (D.DisplayById.ContainsKey(id)) return _cyclicByDisplay!.TryGetValue(id, out var d) ? d.Distinct().Order() : [];
        // (a field's container is the class that declares it, or a struct for a box's slots: the type or one of its bases)
        var r = new List<int>();
        for (var b = D.TypeById[id]; b != null; b = (b as INamedTypeSymbol)?.BaseType is { } bt ? D.Types.Canonical(bt) : null)
        {
            if (_cyclicByContainer.TryGetValue(Types.Key(D.Types.Canonical(b)), out var l)) r.AddRange(l);
            if (b is IArrayTypeSymbol) break;
        }
        return r.Distinct().Order();
    }
    Dictionary<string, List<int>>? _cyclicByContainer;
    Dictionary<int, List<int>>? _cyclicByDisplay;

    /// <summary>Where the references are in an element of the array (offsets), and the element's size: one at 0 in
    /// four bytes for an array of references.</summary>
    public (List<(int off, ITypeSymbol type)> leaves, int size) ElementSlots(IArrayTypeSymbol at)
    {
        var et = D.Types.Canonical(at.ElementType);
        if (Types.IsStruct(et)) return (RefLeaves(et).ToList(), D.Types.StorageOf(et).size);
        return ([(0, et)], 4);
    }

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

    SubtypeIndex? _cycleIndex;
    int _cycleIndexed;
    /// <summary>Whether an object of a cycle-capable type can be in a slot of this type: the cycle-capable types that
    /// may be subtypes of it, by an index (SubtypeIndex, as the graph's), each then asked as before. (Asked of every
    /// cycle-capable type in turn for each slot type, this was most of a minute of the compiler compiling itself.)</summary>
    bool AnyCycleTypeIs(ITypeSymbol slotType) => AnyCycleType(slotType, false);

    /// <summary>Whether some cycle-capable type's TypeById entry is a subtype of t (FieldBorrows' question), by the same index.</summary>
    public bool AnyCycleTypeSubtypeOf(ITypeSymbol t) => AnyCycleType(t, true);

    bool AnyCycleType(ITypeSymbol slotType, bool byTypeOnly)
    {
        if (_cycleIndex == null || _cycleIndexed != CycleTypes.Count)
        {
            _cycleIndex = new SubtypeIndex(D.Types);
            // (a display class, and an array, are among the special ones: asked whatever the slot's type)
            foreach (var id in CycleTypes) _cycleIndex.Add(id, D.DisplayById.ContainsKey(id) || D.TypeById[id] is IArrayTypeSymbol ? null : D.TypeById[id]);
            _cycleIndexed = CycleTypes.Count;
        }
        var cl = _cycleIndex.Candidates(slotType);
        foreach (var id in cl == _cycleIndex.All ? cl : cl.Concat(_cycleIndex.Special))
            if (byTypeOnly ? D.Types.IsSubtype(D.TypeById[id], slotType) : IdIs(id, slotType)) return true;
        return false;
    }

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
        WasmFunction? linkAt = _linkAtFn, unlinkAt = _unlinkAtFn, moveAt = Enabled ? RuntimeFn("ArenaMove", 3) : null;
        if (Enabled && linkAt == null)
        {
            _linkAtFn = linkAt = D.Module.AddFunction("arena.link at", new FuncType([I, I], [I]));
            var le = new Emitter(linkAt);
            le.Get(1); le.I32Addr(new LateAddr(() => D.HeapBase)); le.Op(Op.I32LtU);
            le.If(); le.Get(0); le.Op(Op.Return); le.End();
            le.Get(0); le.Get(1); le.Call(RuntimeFn("ArenaLinkAt", 2));
            _unlinkAtFn = unlinkAt = D.Module.AddFunction("arena.unlink at", new FuncType([I, I], []));
            var ue = new Emitter(unlinkAt);
            ue.Get(1); ue.I32Addr(new LateAddr(() => D.HeapBase)); ue.Op(Op.I32LtU);
            ue.If(); ue.Get(0); ue.Call(D.Rt.Release); ue.Op(Op.Return); ue.End();
            ue.Get(0); ue.Get(1); ue.Call(RuntimeFn("ArenaUnlinkAt", 2));
        }
        var holdsCyclic = new Dictionary<string, bool>();
        bool HoldsCyclic(ITypeSymbol slotType)
        {
            var k = Types.Key(slotType);
            if (!holdsCyclic.TryGetValue(k, out var r)) holdsCyclic[k] = r = AnyCycleTypeIs(slotType);
            return r;
        }
        // (a store by address whose container is known: also that the container can be a group's member)
        bool AtCyclic(WasmFunction ph, ITypeSymbol slotType) =>
            HoldsCyclic(slotType) && (!_atContainer.TryGetValue(ph, out var c) || HoldsCyclic(c));
        // (a pin's answer per placeholder, once: it was asked again at every call, of every cycle-capable type)
        var pinsCyclic = new Dictionary<WasmFunction, bool>();
        bool PinsCyclic(WasmFunction ph, ITypeSymbol pinType)
        {
            if (!pinsCyclic.TryGetValue(ph, out var r)) pinsCyclic[ph] = r = AnyCycleTypeIs(pinType);
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
                    case Kind.Link or Kind.LinkFresh or Kind.LinkOwned or Kind.LinkIso:
                        // (value, container) -> value
                        if (ph.kind == Kind.LinkIso && Enabled && ph.site!.Cyclic) { x.Ref = link; body.Add(x); break; }
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
                            // (not an object stored into its own slot: that closes a cycle whoever knows the object)
                            A(Op.LocalGet, cont); A(Op.I32Load); A(Op.I32Const, ph.kind == Kind.LinkOwned ? 1 : 0); A(ph.kind == Kind.LinkOwned ? Op.I32Eq : Op.I32GtS);
                            A(Op.LocalGet, cont); A(Op.LocalGet, val); A(Op.I32Ne); A(Op.I32And); A(Op.BrIf, 0);
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
                            A(Op.LocalGet, old); A(Op.Call, 0, D.Rt.ReleaseOf(ph.site!.SlotType));
                            A(Op.End);
                        }
                        else { var d = x; d.Op = Op.Drop; d.Ref = null; body.Add(d); body.Add(d); x.Ref = D.Rt.ReleaseOf(ph.site!.SlotType); body.Add(x); }
                        break;
                    case Kind.LinkAt:
                        // (value, slot) -> value
                        if (Enabled && AtCyclic(w, ph.pinType!)) { x.Ref = linkAt; body.Add(x); }
                        else { x.Op = Op.Drop; x.Ref = null; body.Add(x); }
                        break;
                    case Kind.UnlinkAt:
                        // (old, slot) -> ()
                        if (Enabled && AtCyclic(w, ph.pinType!)) { x.Ref = unlinkAt; body.Add(x); }
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
                        if (Enabled && PinsCyclic(w, ph.pinType!)) { x.Ref = pin; body.Add(x); }
                        break;
                    case Kind.StoreLock or Kind.StoreUnlock:
                        // (only while the container is a group's member: the race is over its slots' bits, which a
                        // plain object has none of, Rt.StoreLockIf)
                        if (Enabled && D.ThreadMode && (ph.site != null ? ph.site.Cyclic : HoldsCyclic(ph.pinType!)))
                            x.Ref = ph.kind == Kind.StoreUnlock ? RuntimeFn("StoreUnlockIf", 1) : ph.site != null ? RuntimeFn("StoreLockIf", 2) : RuntimeFn("StoreLockAtIf", 1);
                        else
                        {
                            body.Add(x with { Op = Op.Drop, Ref = null });
                            if (ph.kind == Kind.StoreLock && ph.site != null) body.Add(x with { Op = Op.Drop, Ref = null });
                            if (ph.kind == Kind.StoreLock) body.Add(x with { Op = Op.I32Const, Imm = 0, Ref = null });
                            break;
                        }
                        body.Add(x);
                        break;
                }
            }
            f.Body = body;
        }
    }
}
