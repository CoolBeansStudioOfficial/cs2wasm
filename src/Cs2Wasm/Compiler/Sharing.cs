using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Section 4.5, decided when compiling: which objects another thread can reach (docs/AUDIT.md, step 4). In a program
/// with threads, an object is reachable by more than one thread only through a static field (any thread can read it)
/// or the thread a thread start hands over (Threads.SpawnCore), and from there through what those objects hold. Types
/// none of whose objects can be reached so are the thread's own: their counts and the reads of their fields need no
/// atomic instruction and no lock.
///
/// What a slot can hold is known from where its values come from. At every store into a field, an element, a static,
/// at every argument and every `return`, the compiler records the value's source: a new object of an exact type, a
/// parameter, a method's result, a field, an element, or, for anything else, its static type (that type and every
/// subtype). The types another thread can reach are then those the sources of the static fields lead to, and so on
/// through what those types' slots hold: a reachability over sources, not a set of types per slot.
///
/// A parameter's sources are known only when every call of its method is one the compiler made from arguments it
/// saw (marked on the call instruction: <see cref="Certified"/>); a call from generated code, a dispatch stub, a
/// delegate or the host makes it its declared type. A result's are known only for a body compiled from source.
/// CS2WASM_SHARE_CHECK=1 makes the counts of objects found thread-local check, at run time, that they are counted by
/// the thread that made them.
/// </summary>
internal sealed partial class Sharing
{
    public enum K : byte { Exact, Top, Param, Ret, Node, Elem, Display, RefParam }
    /// <summary>A source of reference values. Exact: an object of Type (Index: a closure's or state machine's type id
    /// for Display). Top: any object of Type or a subtype. Param: method Key's parameter Index (declared Type). Ret:
    /// method Key's result (declared Type; Index 1: a virtual call, its implementations' too). Node: what the slot
    /// Key holds (declared Type). Elem: an element of an array of static type Type. RefParam: what method Key's
    /// `ref`, `in` or `out` parameter Index points at (the places its callers pass, what is stored through it).</summary>
    public readonly record struct Src(K Kind, string? Key = null, int Index = 0, ITypeSymbol? Type = null);

    /// <summary>Marks a call instruction (Instr.Imm2) whose arguments' sources were all recorded.</summary>
    public const long Certified = 1L << 62;
    /// <summary>Marks a call instruction whose `ref`, `in` and `out` arguments' places were all recorded (RefInNode).</summary>
    public const long CertifiedRefs = 1L << 61;

    /// <summary>Takes the marks off again, once nothing asks for them.</summary>
    public static void ClearCertified(WasmModule m)
    {
        foreach (var f in m.Functions)
        {
            if (f.IsImport) continue;
            var b = f.Body;
            for (int i = 0; i < b.Count; i++)
                if (b[i].Op == Op.Call && (b[i].Imm2 & (Certified | CertifiedRefs)) != 0) b[i] = b[i] with { Imm2 = b[i].Imm2 & ~(Certified | CertifiedRefs) };
        }
    }

    readonly Driver D;
    public Sharing(Driver d) { D = d; }

    readonly Dictionary<string, List<Src>> _nodes = [];
    /// <summary>Methods whose every `return` was recorded (a body compiled from source).</summary>
    public readonly HashSet<string> BodyRets = [];
    /// <summary>Statics of struct type that hold references (their slots are stored through addresses).</summary>
    readonly Dictionary<string, ITypeSymbol> _structStatics = [];
    readonly HashSet<string> _threadStatics = [];

    public static string ParamNode(string m, int i) => "p " + m + " " + i;
    public static string RetNode(string m) => "r " + m;
    public static string StaticNode(string key) => "s " + key;
    public static string RefNode(ITypeSymbol t) => "ref " + Types.Key(t);
    /// <summary>A lambda's parameter: what the delegates it becomes are invoked with.</summary>
    public static string LambdaNode(IMethodSymbol lambda, int i) => "lam " + LambdaKey(lambda) + " " + i;
    static string LambdaKey(IMethodSymbol lambda) => (lambda.Locations.FirstOrDefault() is { } l ? l.SourceTree?.FilePath + ":" + l.SourceSpan.Start : "") + " " + lambda.ToDisplayString();

    /// <summary>A lambda becomes a delegate of this type: its parameters get what the delegate's Invoke is called with.</summary>
    public void LambdaAs(IMethodSymbol lambda, ITypeSymbol delegateType)
    {
        if (delegateType is not INamedTypeSymbol { DelegateInvokeMethod: { } inv }) return;
        var key = Types.MethodKey(D.MethodTarget(D.CanonicalMethod(inv)));
        for (int i = 0; i < lambda.Parameters.Length && i < inv.Parameters.Length; i++)
            Add(LambdaNode(lambda, i), new Src(K.Param, key, i, D.Types.Canonical(inv.Parameters[i].Type)));
    }

    /// <summary>A store through a ref made from a pointer (Unsafe.AsRef(void*)): it lands in a place whose address was
    /// made a pointer (Unsafe.AsPointer).</summary>
    public static string PtrNode(ITypeSymbol t) => "ptr " + Types.Key(t);

    /// <summary>What reflection's calls return (Cs2Wasm.Runtime.TypeInfo.Invoke): the results of the members its
    /// thunks call, the values of the fields they read.</summary>
    public const string ReflectNode = "reflect";

    int _arrayNodes;
    /// <summary>A node for the array an element is read from.</summary>
    public string NewArrayNode() => "arr " + _arrayNodes++;

    /// <summary>A `ref`, `in` or `out` parameter of reference type ("method-key ordinal", a body compiled from source):
    /// whether the body keeps or remakes a ref of the place (Dirty), and the parameters it passes it on to.</summary>
    public readonly Dictionary<string, (bool Dirty, HashSet<string> To)> RefParamUse = [];
    HashSet<string>? _refDirty;

    /// <summary>Whether a store through some ref of the slot's type, not through its own field, can land in the slot:
    /// its address went somewhere that may keep a ref of it, directly or through the parameters it is passed on to.</summary>
    public bool RefsReach(Arenas.Site s)
    {
        if (!s.AddressTaken) return false;
        if (s.RefDirty) return true;
        RefDirtySet();
        foreach (var u in s.RefUses) if (_refDirty!.Contains(u)) return true;
        return false;
    }

    /// <summary>The nodes of statics and struct fields whose address is taken: where it went (as a site's RefUses;
    /// Dirty: anywhere).</summary>
    readonly Dictionary<string, (bool Dirty, HashSet<string> Uses)> _nodeAddr = [];
    public void NoteNodeAddress(string node, string? use)
    {
        if (!_nodeAddr.TryGetValue(node, out var a)) a = (false, []);
        if (use == null) a.Dirty = true; else if (use.Length > 0) a.Uses.Add(use);
        _nodeAddr[node] = a;
    }

    /// <summary>The same as RefsReach for a node of _nodes: a static's, a struct field's (its address taken somewhere
    /// a ref of it may be kept), an out parameter's (its body keeps a ref of the parameter: a store through that one).</summary>
    public bool NodeRefsReach(string node)
    {
        if (node.StartsWith("o ")) { RefDirtySet(); return !RefParamUse.ContainsKey(node[2..]) || _refDirty!.Contains(node[2..]); }
        if (!_nodeAddr.TryGetValue(node, out var a)) return false;
        if (a.Dirty) return true;
        RefDirtySet();
        foreach (var u in a.Uses) if (_refDirty!.Contains(u)) return true;
        return false;
    }

    void RefDirtySet()
    {
        if (_refDirty == null)
        {
            // (dirty: a parameter whose body keeps a ref, or that has no body seen; and every one passing to such a one)
            var users = new Dictionary<string, List<string>>();
            var dirty = new HashSet<string>();
            var work = new Queue<string>();
            foreach (var (k, (d, to)) in RefParamUse)
            {
                if (d && dirty.Add(k)) work.Enqueue(k);
                foreach (var t in to)
                {
                    if (!users.TryGetValue(t, out var l)) users[t] = l = [];
                    l.Add(k);
                    if (!RefParamUse.ContainsKey(t) && dirty.Add(t)) work.Enqueue(t);
                }
            }
            foreach (var site in D.Arenas.AllSites)
                foreach (var t in site.RefUses)
                    if (!RefParamUse.ContainsKey(t) && dirty.Add(t)) work.Enqueue(t);
            foreach (var (_, a) in _nodeAddr)
                foreach (var t in a.Uses)
                    if (!RefParamUse.ContainsKey(t) && dirty.Add(t)) work.Enqueue(t);
            while (work.Count > 0)
            {
                var x = work.Dequeue();
                if (users.TryGetValue(x, out var l)) foreach (var u in l) if (dirty.Add(u)) work.Enqueue(u);
            }
            _refDirty = dirty;
        }
    }

    readonly List<Action> _reflectWrites = [];
    bool _reflectApplied;
    /// <summary>A store reflection can make (FieldInfo.SetValue): applied if the program can call SetValue.</summary>
    public void ReflectWrite(Action a) => _reflectWrites.Add(a);

    /// <summary>What a method stores through its `out` or `ref` parameter.</summary>
    public static string OutNode(string m, int i) => "o " + m + " " + i;
    /// <summary>What the places passed to a method's `ref`, `in` or `out` parameter hold (the callers' arguments).</summary>
    public static string RefInNode(string m, int i) => "ri " + m + " " + i;
    /// <summary>A field of a struct, wherever the struct is (in a local, an object, an element, a box, a static).</summary>
    public static string StructFieldNode(ITypeSymbol structType, ISymbol field) => "sf " + Types.Key(structType) + "::" + field.Name;

    public void Add(string node, IEnumerable<Src> srcs)
    {
        if (!_nodes.TryGetValue(node, out var l)) _nodes[node] = l = [];
        foreach (var s in srcs) { l.Add(s); if (s_addWhy != null) AddWhy(node, s); }
    }

    public void Add(string node, Src s)
    {
        if (!_nodes.TryGetValue(node, out var l)) _nodes[node] = l = [];
        l.Add(s);
        if (s_addWhy != null) AddWhy(node, s);
    }

    /// <summary>CS2WASM_SHARING_ADD=text (a diagnosis): every source recorded for a node whose name has the text, with
    /// the method being compiled and the compiler's own stack.</summary>
    static readonly string? s_addWhy = Environment.GetEnvironmentVariable("CS2WASM_SHARING_ADD") is { Length: > 0 } aw ? aw : null;
    void AddWhy(string node, Src s)
    {
        if (!node.Contains(s_addWhy!)) return;
        Console.Error.WriteLine($"sharing-add {node}: {s.Kind} {s.Key} #{s.Index} {(s.Type != null ? Types.Key(s.Type) : "-")} in {(D.Compiling != null ? D.Compiling.Sym.ToDisplayString() : "?")}");
        Console.Error.WriteLine(string.Join("\n", Environment.StackTrace.Split('\n').Skip(3).Take(6)));
    }

    /// <summary>A store into a static field.</summary>
    public void Static(string key, ITypeSymbol type, bool threadStatic, IEnumerable<Src> srcs)
    {
        if (threadStatic) _threadStatics.Add(key);
        if (Types.IsStruct(type) && D.Types.LayoutOf(type).HasRefs) _structStatics[key] = type;
        if (Types.IsRef(type)) Add(StaticNode(key), srcs);
    }

    /// <summary>A store through an address the code does not know the object of (a `ref`, a span's element).</summary>
    public void RefStore(ITypeSymbol slotType, IEnumerable<Src> srcs, string? where = null)
    {
        if (!Types.IsRef(slotType)) return;
        var ct = D.Types.Canonical(slotType);
        if (s_flowRefs && (Types.IsObject(ct) || ct.TypeKind == TypeKind.Interface))
            Console.Error.WriteLine($"ref-store {Types.Key(ct)} in {where}: " + string.Join("; ", srcs.Select(x => x.Kind + " " + x.Key + " " + (x.Type != null ? Types.Key(x.Type) : ""))));
        _refTypes.TryAdd(RefNode(ct), ct);
        Add(RefNode(ct), srcs);
    }

    // ------------------------------------------------------------------
    // the closure
    // ------------------------------------------------------------------

    HashSet<int>? _shared;
    readonly Dictionary<string, bool> _mayBeShared = [];
    public bool Done => _shared != null;

    /// <summary>Whether an object another thread can reach can be in a place of this static type.</summary>
    /// <summary>CS2WASM_SHARING_NONE=1 (to see the share check trap, a test of the test): no type is shared.</summary>
    static readonly bool s_none = Environment.GetEnvironmentVariable("CS2WASM_SHARING_NONE") == "1";

    /// <summary>The array types another thread can reach only as an array with no element (EmptyArrayTag): such an
    /// object is counted by several threads, and has no slot any of them could read or store.</summary>
    HashSet<int>? _emptyOnly;
    readonly Dictionary<string, bool> _maySlotsBeShared = [];

    /// <summary>Whether a slot of an object another thread can reach (a field, an element) can be in an object in a
    /// place of this static type: as MayBeShared, without the arrays of no element. Asked where a read or a store
    /// of a slot is decided (a read that counts, a store's lock, a borrow); a count asks MayBeShared.</summary>
    public bool MaySlotsBeShared(ITypeSymbol t)
    {
        if (_shared == null || _emptyOnly == null || _emptyOnly.Count == 0) return MayBeShared(t);
        if (s_none) return false;
        if (t.TypeKind == TypeKind.Error || !Types.IsRef(t)) return false;
        var k = Types.Key(t);
        if (_maySlotsBeShared.TryGetValue(k, out var r)) return r;
        r = false;
        bool Is(int id) => _shared.Contains(id) && !_emptyOnly.Contains(id) && IdIs(id, t);
        var ix = Index();
        foreach (var id in ix.Candidates(t)) if (Is(id)) { r = true; break; }
        if (!r) foreach (var id in ix.Special) if (Is(id)) { r = true; break; }
        _sharedUnindexed ??= _shared.Where(id => !_indexed.Contains(id)).ToList();
        if (!r) foreach (var id in _sharedUnindexed) if (Is(id)) { r = true; break; }
        return _maySlotsBeShared[k] = r;
    }

    public bool MayBeShared(ITypeSymbol t)
    {
        if (s_none) return false;
        if (_shared == null) return true;
        if (t.TypeKind == TypeKind.Error || !Types.IsRef(t)) return false;
        var k = Types.Key(t);
        if (_mayBeShared.TryGetValue(k, out var r)) return r;
        r = false;
        // (the types that may be subtypes of t, by an index, not all of them: asked for every static type a count
        // has, a test of each shared type was five minutes of the compiler compiling itself)
        var ix = Index();
        foreach (var id in ix.Candidates(t)) if (_shared.Contains(id) && IdIs(id, t)) { r = true; break; }
        if (!r) foreach (var id in ix.Special) if (_shared.Contains(id) && IdIs(id, t)) { r = true; break; }
        // (and the shared types the index does not list: reached as an exact type without being among the constructed)
        _sharedUnindexed ??= _shared.Where(id => !_indexed.Contains(id)).ToList();
        if (!r) foreach (var id in _sharedUnindexed) if (IdIs(id, t)) { r = true; break; }
        return _mayBeShared[k] = r;
    }

    SubtypeIndex? _index;
    readonly HashSet<int> _indexed = [];
    List<int>? _sharedUnindexed;
    /// <summary>The program's types by what they may be subtypes of (arrays, closures and synthetic types apart).</summary>
    SubtypeIndex Index()
    {
        if (_index != null) return _index;
        _index = new SubtypeIndex(D.Types);
        foreach (var id in D.Constructed.Order())
        {
            if (id <= 1 || id >= D.TypeById.Count) continue;
            var t = D.TypeById[id];
            bool special = D.DisplayById.ContainsKey(id) || D.SyntheticById.ContainsKey(id) || t is IArrayTypeSymbol;
            _index.Add(id, special ? null : t);
            _indexed.Add(id);
        }
        return _index;
    }

    /// <summary>The statics that are roots: named by code a worker thread can run (or their address goes where a ref
    /// of it may be kept, or their slot is unknown). Every other static that holds references is the engine
    /// thread's own: only code no worker can run reads or writes it (Driver.WorkerStaticSlots), so what it holds is
    /// reachable by another thread only if something else hands it over.</summary>
    HashSet<string>? _staticRoots;
    /// <summary>CS2WASM_NO_ENGINE_STATICS=1: every static is a root, as before.</summary>
    static readonly bool s_noEngineStatics = Environment.GetEnvironmentVariable("CS2WASM_NO_ENGINE_STATICS") == "1";
    /// <summary>CS2WASM_EXP_ALL_ENGINE_STATICS=1 (a test of the tests, unsound): no static is a root.</summary>
    static readonly bool s_allEngineStatics = Environment.GetEnvironmentVariable("CS2WASM_EXP_ALL_ENGINE_STATICS") == "1";
    public const string StaticPrior = "static ";
    /// <summary>The tag (an Exact source's Key) of a delegate made of a lambda at one place of the text; Index is the
    /// lambda site (Driver.LambdaSites). Reaching such a delegate reaches its own closure, and makes its lambda code
    /// a worker thread can run; a delegate with no tag is any of its type, with every closure and every lambda the
    /// type's delegates were made with. For every other reader of the sources it is a delegate of its exact type.
    /// CS2WASM_NO_LAMBDA_SITES=1: no tag, as before. CS2WASM_EXP_LAMBDA_SITES_UNREACHED=1 (a test of the tests,
    /// unsound): a tagged delegate reaches nothing and makes no code a worker's.</summary>
    public const string LambdaTag = "lambda";
    internal static readonly bool s_noLambdaSites = Environment.GetEnvironmentVariable("CS2WASM_NO_LAMBDA_SITES") == "1";
    /// <summary>CS2WASM_EXP_NO_LAMBDA_RUNS=1 (a test of the tests, unsound): no lambda is code a worker runs by a delegate.</summary>
    static readonly bool s_expNoLambdaRuns = Environment.GetEnvironmentVariable("CS2WASM_EXP_NO_LAMBDA_RUNS") == "1";
    static readonly bool s_expLambdaUnreached = Environment.GetEnvironmentVariable("CS2WASM_EXP_LAMBDA_SITES_UNREACHED") == "1";
    /// <summary>In the map the next compilation is given: whether a slot of an object another thread can reach can be
    /// in an object in a place of the type (MaySlotsBeShared).</summary>
    public const string SlotsPrior = "slots ";
    /// <summary>The tag (a Top source's Key) of a value Arenas.AddRefStores gave a slot: Site.ByAddress has where it
    /// comes from. CS2WASM_NO_BY_ADDRESS_SOURCES=1: any object of its type, as before.</summary>
    public const string ByAddressTag = "by address";
    /// <summary>The tag (an Exact source's Key) of an array made with no element (`new T[0]`, an empty collection
    /// expression): no store can be made into it, so reaching it reaches nothing more. For every other reader of the
    /// sources it is an array of its exact type. CS2WASM_NO_EMPTY_ARRAYS=1: for Analyze too, as before.</summary>
    public const string EmptyArrayTag = "no element";
    static readonly bool s_noEmptyArrays = Environment.GetEnvironmentVariable("CS2WASM_NO_EMPTY_ARRAYS") == "1";
    /// <summary>CS2WASM_EXP_EVERY_ARRAY_EMPTY=1 (a test of the tests, unsound): every array made is taken as one with
    /// no element.</summary>
    /// <summary>CS2WASM_EXP_NO_PTR=1 (a test of the tests, unsound): a store through a pointer leaves nothing in an array.</summary>
    static readonly bool s_expNoPtr = Environment.GetEnvironmentVariable("CS2WASM_EXP_NO_PTR") == "1";
    /// <summary>CS2WASM_EXP_DISPLAY_FIRST_ONLY=1 (a test of the tests, unsound): a captured local holds only what its
    /// first write in the text stores.</summary>
    internal static readonly bool s_expDisplayFirstOnly = Environment.GetEnvironmentVariable("CS2WASM_EXP_DISPLAY_FIRST_ONLY") == "1";
    internal static readonly bool s_expEveryArrayEmpty =Environment.GetEnvironmentVariable("CS2WASM_EXP_EVERY_ARRAY_EMPTY") == "1";
    static readonly bool s_noByAddress =Environment.GetEnvironmentVariable("CS2WASM_NO_BY_ADDRESS_SOURCES") == "1";
    static readonly bool s_noNullSites = Environment.GetEnvironmentVariable("CS2WASM_NO_NULL_SITES") == "1";

    /// <summary>Everything this compilation found another thread can reach, as keys: the types (SharedTypeSymbols)
    /// and the statics that are roots.</summary>
    public HashSet<string> SharedKeys()
    {
        var r = SharedTypeSymbols().Select(Types.Key).ToHashSet();
        if (_staticRoots != null) foreach (var k in _staticRoots) r.Add(StaticPrior + k);
        // (and the types whose objects' slots can be: every one but an array type reached only with no element)
        if (_shared != null)
            foreach (var id in _shared)
                if (!D.DisplayById.ContainsKey(id) && !D.SyntheticById.ContainsKey(id) && id < D.TypeById.Count && !(_emptyOnly != null && _emptyOnly.Contains(id)))
                    r.Add(SlotsPrior + Types.Key(D.TypeById[id]));
        return r;
    }

    /// <summary>While compiling: whether the last compilation found that only the engine thread's code names this
    /// static (key: Driver.StaticKey). Then this compilation's code that names it is the engine thread's too (were it
    /// not, the static is a root now and the program is compiled again: Api.Compile), and it reads and writes the
    /// static as a program without threads does.</summary>
    public bool PriorEngineStatic(string key) =>
        D.SharingPrior is { } prior && !s_off && !s_noEngineStatics && prior.TryGetValue(StaticPrior + key, out var shared) && !shared;

    /// <summary>The types another thread can reach (not the closures' and state machines' own, which have no symbol).</summary>
    public List<ITypeSymbol> SharedTypeSymbols() =>
        _shared == null ? [] : _shared.Where(id => !D.DisplayById.ContainsKey(id) && !D.SyntheticById.ContainsKey(id) && id < D.TypeById.Count).Select(id => D.TypeById[id]).ToList();

    /// <summary>For each type this compilation knows (its classes and arrays, and their bases), by its key: whether
    /// an object another thread can reach can be in a place of that type. What the next compilation of the program
    /// is given (Driver.SharingPrior): keys, not symbols, since each compilation has symbols of its own.</summary>
    public Dictionary<string, bool> PriorMap(IReadOnlyDictionary<string, bool>? merge = null)
    {
        var r = new Dictionary<string, bool>();
        foreach (var id in D.Constructed)
        {
            if (id <= 0 || id >= D.TypeById.Count || D.DisplayById.ContainsKey(id) || D.SyntheticById.ContainsKey(id)) continue;
            var t = D.TypeById[id];
            if (!Types.IsRef(t)) continue;
            for (var b = t; b != null; b = (b as INamedTypeSymbol)?.BaseType is { } bt ? D.Types.Canonical(bt) : null)
            {
                var k = Types.Key(b);
                if (r.ContainsKey(k)) break;
                r[k] = MayBeShared(b);
                r[SlotsPrior + k] = MaySlotsBeShared(b);
                if (b is IArrayTypeSymbol) break;
            }
        }
        // (the statics that hold references, each a root or the engine thread's own)
        if (_staticRoots != null)
        {
            foreach (var k in _nodes.Keys) if (k.StartsWith("s ") && !_threadStatics.Contains(k[2..])) r[StaticPrior + k[2..]] = _staticRoots.Contains(k[2..]);
            foreach (var k in _structStatics.Keys) if (!_threadStatics.Contains(k)) r[StaticPrior + k] = _staticRoots.Contains(k);
        }
        if (merge != null) foreach (var (k, v) in merge) r[k] = r.TryGetValue(k, out var x) ? x || v : v;
        return r;
    }

    /// <summary>While compiling: whether the last compilation found that no object another thread can reach can be in
    /// a place of this static type (then reading and borrowing from such an object is as without threads). False
    /// when there was none, and for a type it did not know.</summary>
    public bool PriorThreadLocal(ITypeSymbol container)
    {
        if (D.SharingPrior is not { } prior || s_off) return false;
        if (container.TypeKind is TypeKind.Error or TypeKind.TypeParameter || !Types.IsRef(container)) return false;
        // (object and the interfaces any closure or state machine may be: their objects are taken as shared)
        if (Types.IsObject(container) || container.TypeKind == TypeKind.Interface) return false;
        // (a slot of such an object: an array type reached only as an array with no element has none, SlotsPrior)
        return prior.TryGetValue(SlotsPrior + Types.Key(D.Types.Canonical(container)), out var shared) && !shared;
    }

    /// <summary>Whether an object of this type id (a closure's or state machine's) can be reached by another thread.</summary>
    public bool IdShared(int id) => _shared == null || _shared.Contains(id);

    bool IdIs(int id, ITypeSymbol t)
    {
        if (D.DisplayById.ContainsKey(id))
            return Types.IsObject(t) || D.SyntheticById.TryGetValue(id, out var st) && st.Interfaces.Any(i => D.Types.IsSubtype(i, t));
        if (id >= D.TypeById.Count) return false;
        return D.Types.IsSubtype(D.TypeById[id], t);
    }

    readonly Dictionary<string, List<int>> _topIds = [];
    List<int>? _allIds;
    HashSet<int>? _allIdSet;

    List<int> TopIds(ITypeSymbol t)
    {
        var k = Types.Key(t);
        if (_topIds.TryGetValue(k, out var r)) return r;
        _allIds ??= D.Constructed.Where(id => id > 1 && id < D.TypeById.Count && (D.DisplayById.ContainsKey(id) || !D.SyntheticById.ContainsKey(id) || D.TypeById[id] is INamedTypeSymbol { IsAnonymousType: true })).Order().ToList();
        r = [];
        if (Types.IsRef(t))
        {
            // (the candidates by the index, in id order as before)
            var allowed = _allIdSet ??= [.. _allIds];
            var ix = Index();
            // (no array is a subtype of most types: Types.ArraysMayBe, once, not a test per array)
            bool arrays = D.Types.ArraysMayBe(t);
            var cl = ix.Candidates(t);
            // (a type of t's own list, not a special one, is a subtype without asking: SubtypeIndex.IsExact)
            bool exact = ix.IsExact(cl);
            foreach (var id in cl.Concat(ix.Special).Distinct().Order())
                if (allowed.Contains(id) && (!Types.IsImmortalType(D.TypeById[id]) || D.DisplayById.ContainsKey(id))
                    && (exact && !(D.DisplayById.ContainsKey(id) || D.SyntheticById.ContainsKey(id) || D.TypeById[id] is IArrayTypeSymbol)
                        || !(!arrays && !D.DisplayById.ContainsKey(id) && D.TypeById[id] is IArrayTypeSymbol) && IdIs(id, t))) r.Add(id);
        }
        return _topIds[k] = r;
    }

    static readonly bool s_debug = Environment.GetEnvironmentVariable("CS2WASM_SHARING_DEBUG") == "1";
    /// <summary>CS2WASM_SHARING_WALK=key (a diagnosis): every source and node ArrayIdsOf goes through from that start.</summary>
    /// <summary>CS2WASM_SHARING_TRACE=1 (a diagnosis): every step of Analyze's walk, and every node visit, with its filter.</summary>
    static readonly bool s_trace = Environment.GetEnvironmentVariable("CS2WASM_SHARING_TRACE") == "1";
    static readonly string? s_walk = Environment.GetEnvironmentVariable("CS2WASM_SHARING_WALK");
    public static bool Debug => s_debug;
    static readonly bool s_off = Environment.GetEnvironmentVariable("CS2WASM_NO_SHARING") == "1";

    /// <summary>The types another thread can reach. `sites`: the slots of the type graph (Arenas: fields by declaring
    /// type and offset, elements by array type, closures' slots by type id) with the sources stored there.</summary>
    public void Analyze(IReadOnlyCollection<Arenas.Site> sites, Func<WasmFunction, string?> methodKeyOf)
    {
        if (s_off) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // (reflection stores into fields only through FieldInfo.SetValue)
        if (!_reflectApplied && D.AllMethods.Any(m => m.Compiled && m.Sym.Name is "SetValue" or "SetValueDirect" && m.Sym.ContainingType?.Name is "RuntimeFieldInfo" or "FieldInfo"))
        { _reflectApplied = true; foreach (var a in _reflectWrites) a(); }
        // which methods' parameters are known: every call to them marked, none from elsewhere
        var uncertified = new HashSet<string>();
        var stubOf = new Dictionary<WasmFunction, VirtualStub>();
        foreach (var s in D.Stubs.Values) stubOf[s.Fn] = s;
        foreach (var f in D.Module.Functions)
        {
            if (f.IsImport) continue;
            bool inStub = stubOf.ContainsKey(f);
            var self = methodKeyOf(f);
            foreach (var ins in f.Body)
            {
                if (ins.Ref is not WasmFunction g) continue;
                if (ins.Op == Op.Call && ((ins.Imm2 & Certified) != 0 || inStub)) continue;
                // (a method's own forwarder, a variant of it calling its body: its parameters, passed on)
                if (ins.Op == Op.Call && self != null && methodKeyOf(g) == self) continue;
                if (methodKeyOf(g) is { } k) { if (uncertified.Add(k) && s_debug) Console.Error.WriteLine("uncertified " + k + " by " + (ins.Op == Op.Call ? "a call in " : ins.Op + " in ") + f.Name); }
                else if (stubOf.TryGetValue(g, out var st)) uncertified.Add(Types.MethodKey(D.MethodTarget(st.Slot)));
            }
        }
        foreach (var f in D.Module.Functions) if ((f.Exports.Count > 0 || f.TableRoot || f.InTable) && methodKeyOf(f) is { } ek) uncertified.Add(ek);
        // a dispatch stub passes its slot's arguments to the implementations, and their results back
        var implsOf = new Dictionary<string, List<string>>();
        foreach (var s in D.Stubs.Values)
        {
            var slotKey = Types.MethodKey(D.MethodTarget(s.Slot));
            if (!implsOf.TryGetValue(slotKey, out var l)) implsOf[slotKey] = l = [];
            foreach (var c in s.Cases.Values)
            {
                if (c.impl == null) continue;
                var ik = Types.MethodKey(c.impl.Sym);
                if (ik == slotKey) continue;
                l.Add(ik);
                for (int i = 0; i < s.Slot.Parameters.Length; i++) Add(ParamNode(ik, i), new Src(K.Param, slotKey, i, s.Slot.Parameters[i].Type));
            }
        }
        // the slots of each type
        var byContainer = new Dictionary<string, List<Arenas.Site>>();
        var byId = new Dictionary<int, List<Arenas.Site>>();
        var arraySites = new List<Arenas.Site>();
        foreach (var s in sites)
        {
            if (s.ContainerId >= 0) { if (!byId.TryGetValue(s.ContainerId, out var l)) byId[s.ContainerId] = l = []; l.Add(s); }
            else if (s.IsArray) arraySites.Add(s);
            else { var k = Types.Key(s.Container); if (!byContainer.TryGetValue(k, out var l)) byContainer[k] = l = []; l.Add(s); }
        }
        var refNodes = _nodes.Keys.Where(k => k.StartsWith("ref ")).ToList();

        var shared = new HashSet<int>();
        // (array types reached only as an array with no element: EmptyArrayTag)
        var emptyOnly = new HashSet<int>();
        // (delegate types reached only as delegates of lambda sites, and those sites: LambdaTag)
        var delegateOnly = new HashSet<int>();
        var lambdaShared = new HashSet<int>();
        var seenNodes = new HashSet<string>();
        var ptrSeen = new HashSet<(Arenas.Site, string?)>();
        var seenTops = new HashSet<string>();
        // (filter: what the slot can hold whatever is stored through a wider type, an array's element type)
        var work = new Stack<(Src src, string why, ITypeSymbol? filter)>();
        var whyType = s_debug ? new Dictionary<int, string>() : null;

        ITypeSymbol? filter = null;
        string FK(string k) => filter == null ? k : k + " |" + Types.Key(filter);
        // (the reason a type is reached is kept only to print it, CS2WASM_SHARING_DEBUG: built along every path, it
        // grows with the path, and over Roslyn's millions of steps that was all the memory there is)
        void Push(Src s, string why) => work.Push((s, s_debug ? why : "", filter));
        // A node is visited once per filter it is reached with. Past a few filters it is visited once with none,
        // which holds whatever any filter would let through (more types taken as reachable, never fewer): over
        // Roslyn the pairs of a node and a filter were more than the memory holds.
        const int FiltersPerNode = 4;
        var filtersSeen = new Dictionary<string, int>();
        var unfiltered = new HashSet<string>();
        bool Visit(string key)
        {
            if (s_trace) Console.Error.WriteLine("trace visit " + key + " |" + (filter != null ? Types.Key(filter) : "-") + " u" + unfiltered.Contains(key) + " s" + seenNodes.Contains(FK(key)) + " c" + filtersSeen.GetValueOrDefault(key));
            if (unfiltered.Contains(key)) return false;
            if (filter == null) { unfiltered.Add(key); return true; }
            if (!seenNodes.Add(FK(key))) return false;
            int c = filtersSeen.GetValueOrDefault(key) + 1;
            filtersSeen[key] = c;
            if (c > FiltersPerNode) { filter = null; unfiltered.Add(key); }
            return true;
        }
        void Node(string key, ITypeSymbol? declared, string why)
        {
            var saved = filter;
            if (!Visit(key)) return;
            if (_nodes.TryGetValue(key, out var l) && l.Count > 0) { foreach (var s in l) Push(s, why); }
            else if (declared != null) Push(new Src(K.Top, Type: declared), why + " (nothing recorded)");
            filter = saved;
        }
        void SiteNode(Arenas.Site s, ITypeSymbol? declared, string why)
        {
            var saved = filter;
            // (the values stores by address can leave here: by where those stores' values come from, Site.ByAddress)
            bool byAddress = !s_noByAddress && s.ByAddress.Count > 0;
            // The values stored through a pointer (the runtime's array code): the types such a value can be
            // (PtrIds), of which the slot takes its own. Done for every filter the slot is reached with, before the
            // visit below, which past a few filters goes on with none: with none, every such type would be taken.
            if (byAddress && !s_noPtrIds && ptrSeen.Add((s, filter != null ? Types.Key(filter) : null)) && !(filter != null && ptrSeen.Contains((s, null))))
                foreach (var x in s.ByAddress)
                {
                    if (x.Kind != K.Node || !x.Key!.StartsWith("ptr ")) continue;
                    var ids = PtrIds(x.Key, x.Type, uncertified, implsOf, arraySites);
                    string w = s_debug ? why + " (stored through a pointer)" : "";
                    if (filter == null) foreach (var id in ids) AddType(id, w);
                    else
                    {
                        foreach (var id in TopIds(filter)) if (ids.Contains(id)) AddType(id, w);
                        foreach (var id in ids) if (!_allIdSet!.Contains(id)) AddType(id, w);
                    }
                }
            if (!Visit("site " + s.Key)) { filter = saved; return; }
            if (s.Sources.Count > 0) foreach (var x in s.Sources) { if (byAddress && x.Kind == K.Top && x.Key == ByAddressTag) continue; Push(x, why); }
            if (byAddress)
                foreach (var x in s.ByAddress)
                    if (!(x.Kind == K.Node && x.Key!.StartsWith("ptr ") && !s_noPtrIds)) Push(x, why + " (by address)");
            foreach (var id in s.TargetIds) Push(new Src(K.Display, Index: id), why);
            // (no source: every store compiled into the slot stored null or the default, each recorded with what it
            // stores. Only a slot with no store recorded at all, or one whose address is taken, is taken to hold
            // anything. CS2WASM_NO_NULL_SITES=1: any slot with no source, as before)
            if (s.Sources.Count == 0 && s.TargetIds.Count == 0 && declared != null && !(s.Stored && !s.AddressTaken && !s_noNullSites)) Push(new Src(K.Top, Type: declared), why + " (nothing recorded)");
            filter = saved;
        }
        // what an array of type `a` holds: stores through its own type, and through a wider one (an object[] that is
        // one), of which only its element type's objects
        void ArrayContents(ITypeSymbol a, string why)
        {
            var et = D.Types.Canonical(((IArrayTypeSymbol)a).ElementType);
            WithFilter(et, () =>
            {
                bool any = false;
                foreach (var s in arraySites)
                    if (Types.Key(s.Container) == Types.Key(a) || D.Types.IsSubtype(a, s.Container)) { SiteNode(s, null, why); any = true; }
                if (!any) Push(new Src(K.Top, Type: et), why + " (no store seen)");
                RefStores(et, why);
            });
        }
        // (a `ref T` points at a slot of type T: refs do not convert. Unsafe.As makes a ref of another type of one)
        void RefStores(ITypeSymbol slotType, string why)
        {
            var k = RefNode(D.Types.Canonical(slotType));
            WithFilter(slotType, () =>
            {
                Node(k, null, why + " (through a ref)");
                if (_refAliases.TryGetValue(k, out var al)) foreach (var a in al) Node(a, null, why + " (through a reinterpreted ref)");
            });
        }
        // (a slot holds objects of its declared type only, whatever was stored through a wider one)
        void WithFilter(ITypeSymbol t, Action a)
        {
            var saved = filter;
            if (filter == null || D.Types.IsSubtype(t, filter)) filter = t;
            try { a(); } finally { filter = saved; }
        }
        void Slot(int id, string siteKeyHolder, int off, ITypeSymbol declared, string why, List<Arenas.Site>? candidates) => WithFilter(declared, () =>
        {
            bool found = false;
            if (candidates != null)
                foreach (var s in candidates)
                    if (s.Offset == off) { SiteNode(s, declared, why); found = true; }
            // (no store into it compiled anywhere: it is only ever null. A store the compiler writes itself goes
            // into an object of its own making, a closure's or a delegate's, whose slots are recorded as sites)
            if (!found && id < 0) Push(new Src(K.Top, Type: declared), why + " (no store seen)");
            RefStores(declared, why);
        });
        // the references in a struct, by its fields' nodes
        void StructFields(ITypeSymbol st, string why, int depth = 0)
        {
            if (depth > 8) return;
            foreach (var f in D.Types.LayoutOf(st).Fields)
            {
                if (Types.IsRef(f.Type)) { if (!Types.IsImmortalType(f.Type)) WithFilter(f.Type, () => { Node(StructFieldNode(D.Types.Canonical(st), f.Symbol), f.Type, why + "." + f.Symbol.Name); RefStores(f.Type, why); }); }
                else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs) StructFields(f.Type, why + "." + f.Symbol.Name, depth + 1);
            }
        }
        void AddType(int id, string why)
        {
            if (filter != null && !IdIs(id, filter)) return;
            if (!shared.Add(id)) return;
            var savedFilter = filter;
            filter = null;
            try { AddTypeSlots(id, why); } finally { filter = savedFilter; }
        }
        void AddTypeSlots(int id, string why)
        {
            if (whyType != null) whyType[id] = why;
            string name = D.DisplayById.ContainsKey(id) ? "display " + id : Types.Key(D.TypeById[id]);
            string w = name;
            if (D.DisplayById.TryGetValue(id, out var disp))
            {
                byId.TryGetValue(id, out var ds);
                foreach (var (off, t) in disp.Vars.Select(v => disp.Slots[v]).Concat(disp.ExtraSlots.Values))
                {
                    if (Types.IsRef(t)) Slot(-1, "d", off, t, w, ds);
                    else if (Types.IsStruct(t) && D.Types.LayoutOf(t).HasRefs) StructFields(t, w);
                }
                if (disp.Parent is { TypeId: > 0 } p) Push(new Src(K.Display, Index: p.TypeId), w + " (its enclosing closure)");
                return;
            }
            var type = D.TypeById[id];
            if (type is IArrayTypeSymbol at)
            {
                var et = D.Types.Canonical(at.ElementType);
                if (Types.IsRef(et)) ArrayContents(type, w);
                else if (Types.IsStruct(et) && D.Types.LayoutOf(et).HasRefs) StructFields(et, w + " (an element)");
                return;
            }
            if (type.IsValueType)
            {
                // a box: what its struct holds
                if (Types.IsStruct(type) && D.Types.LayoutOf(type).HasRefs) StructFields(type, w + " (a box)");
                return;
            }
            // a class, a delegate: each reference in its fields, its bases' too, and in the structs among them
            var lay = D.Types.LayoutOf(type);
            var cands = new List<Arenas.Site>();
            for (var b = type as INamedTypeSymbol; b != null; b = b.BaseType is { } bt ? (INamedTypeSymbol)D.Types.Canonical(bt) : null)
            {
                if (byContainer.TryGetValue(Types.Key(D.Types.Canonical(b)), out var l)) cands.AddRange(l);
                if (Types.IsObject(b)) break;
            }
            foreach (var f in lay.Fields)
            {
                if (Types.IsRef(f.Type)) { if (!Types.IsImmortalType(f.Type)) Slot(id, "f", f.Offset, f.Type, w + "." + f.Symbol.Name, cands); }
                else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs) StructFields(f.Type, w + "." + f.Symbol.Name);
            }
        }

        // the roots: what the statics hold, the thread a thread start hands over, what a static struct's fields hold
        // A static is a root only if code a worker thread can run names it (StaticIsRoot): one that only the engine
        // thread's code reads and writes hands nothing to another thread.
        // Which code a worker can run depends on which delegates a worker can reach (a lambda runs on a worker only
        // if a delegate made of it there, or by code a worker runs, exists: Driver.WorkerReachable), and which
        // delegates it can reach depends on the statics its code names: both grow together until neither does.
        // The roots are added as the worker's code is found to name them; everything only grows.
        var staticRoots = new HashSet<string>();
        var refStatics = _nodes.Keys.Where(k => k.StartsWith("s ") && !_threadStatics.Contains(k[2..])).Select(k => k[2..]).ToList();
        var structStatics = _structStatics.Where(kv => !_threadStatics.Contains(kv.Key)).ToList();
        bool LambdaRuns(WasmFunction fn)
        {
            if (s_expNoLambdaRuns) return false;
            foreach (var (dt, site) in D.DelegatesMade[fn])
            {
                if (lambdaShared.Contains(site)) return true;
                // (any delegate of the type can be reached: one with no tag was)
                if (D.ExistingTypeId(dt) is not int did || shared.Contains(did)) return true;
            }
            return false;
        }
        // (true when a root was added)
        bool AddStaticRoots()
        {
            filter = null;
            var workerSlots = s_noEngineStatics ? null : D.WorkerStaticSlots(s_noLambdaSites ? null : LambdaRuns);
            bool IsRoot(string key) => !s_allEngineStatics && (workerSlots == null || D.StaticSlotOf(key) is not { } slot || workerSlots.Contains(slot) || NodeRefsReach(StaticNode(key)));
            bool any = false;
            foreach (var k in refStatics)
                if (!staticRoots.Contains(k) && IsRoot(k)) { staticRoots.Add(k); Node(StaticNode(k), null, "static " + k); any = true; }
            foreach (var (k, t) in structStatics)
                if (!staticRoots.Contains("struct " + k) && IsRoot(k)) { staticRoots.Add("struct " + k); staticRoots.Add(k); StructFields(t, "static " + k); any = true; }
            return any;
        }
        var threadType = D.LibType("System.Threading.Thread");
        Push(new Src(K.Top, Type: threadType), "a thread start");

        int steps = 0, rounds = 0;
        while (true)
        {
        if (work.Count == 0)
        {
            // (what is reached so far: the statics a worker's code names now; none new: done)
            rounds++;
            if (!AddStaticRoots() && work.Count == 0) break;
            if (work.Count == 0) continue;
        }
        {
            var (s, why, f) = work.Pop();
            filter = f;
            steps++;
            if (s_trace) Console.Error.WriteLine("trace pop " + s.Kind + " " + s.Key + " #" + s.Index + " " + (s.Type != null ? Types.Key(s.Type) : "-") + " |" + (f != null ? Types.Key(f) : "-"));
            switch (s.Kind)
            {
                case K.Exact:
                    if (s.Type != null && D.ExistingTypeId(D.Types.Canonical(s.Type)) is int eid and > 0 && !Types.IsImmortalType(s.Type))
                    {
                        // (an array with no element: the object can be reached, nothing through it; its type is
                        // one another thread can reach, its elements' slots are not followed for it)
                        if (s.Key == EmptyArrayTag && !s_noEmptyArrays) { if ((filter == null || IdIs(eid, filter)) && !shared.Contains(eid) && emptyOnly.Add(eid) && whyType != null) whyType[eid] = why + " (an array with no element)"; }
                        else if (s.Key == LambdaTag && !s_noLambdaSites && s.Index < D.LambdaSites.Count && D.LambdaSites[s.Index] is { Unsure: false, Fn: not null } ls)
                        {
                            // (a delegate made of one lambda: the delegate, the closure it was made with and what that
                            // holds; not the closures of the type's other delegates, which its target's slot stands for)
                            if ((filter == null || IdIs(eid, filter)) && !s_expLambdaUnreached)
                            {
                                lambdaShared.Add(s.Index);
                                if (!shared.Contains(eid) && delegateOnly.Add(eid) && whyType != null) whyType[eid] = why + " (a lambda's delegate)";
                                if (ls.Env is { TypeId: > 0 } lenv) { var f0 = filter; filter = null; AddType(lenv.TypeId, why + " (the closure of the lambda " + ls.Fn.Name + ")"); filter = f0; }
                            }
                        }
                        else AddType(eid, why);
                    }
                    else if (s.Type != null && Types.IsRef(s.Type)) Push(new Src(K.Top, Type: s.Type), why);
                    break;
                case K.Display:
                    AddType(s.Index, why);
                    break;
                case K.Top:
                    if (s_debug && s.Type != null && (Types.IsObject(s.Type) || s.Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Object } || Types.FullName(s.Type) == "System.Array") && (filter == null || Types.IsObject(filter) || filter is IArrayTypeSymbol || Types.FullName(filter) == "System.Array"))
                        Console.Error.WriteLine("broad " + Types.Key(s.Type) + " [" + (s.Key ?? "?") + "] via " + (why.Length > 300 ? why[..300] : why));
                    if (s.Type == null || !Visit("top " + Types.Key(s.Type))) break;
                    foreach (var id in TopIds(s.Type)) AddType(id, why + " <- any " + Types.Key(s.Type) + (s.Key != null ? " [" + s.Key + "]" : ""));
                    break;
                case K.Param:
                    if (uncertified.Contains(s.Key!)) { if (s.Type != null) Push(new Src(K.Top, Type: s.Type), why + " <- " + s.Key + " #" + s.Index + " (called from elsewhere)"); }
                    else Node(ParamNode(s.Key!, s.Index), null, why + " <- " + s.Key + " #" + s.Index);
                    break;
                case K.Ret:
                    {
                        var keys = new List<string> { s.Key! };
                        bool dispatched = s.Index == 1 && implsOf.TryGetValue(s.Key!, out var impls) && impls.Count > 0;
                        if (dispatched) keys.AddRange(implsOf[s.Key!]);
                        foreach (var k in keys)
                            if (BodyRets.Contains(k)) Node(RetNode(k), null, why + " <- " + k + "()");
                            // (an abstract slot's own result is its implementations')
                            else if (dispatched && k == s.Key) continue;
                            else if (s.Type != null) Push(new Src(K.Top, Type: s.Type), why + " <- " + k + "() (no body)");
                        break;
                    }
                case K.Node:
                    if (s.Key!.StartsWith("site ")) { var site = D.Arenas.SiteByKey(s.Key[5..]); if (site != null) SiteNode(site, s.Type, why); else if (s.Type != null) Push(new Src(K.Top, Type: s.Type), why + " (no store seen)"); }
                    else Node(s.Key, s.Type, why);
                    break;
                case K.RefParam:
                    if (s.Type != null) Push(new Src(K.Top, Type: s.Type), why + " <- " + s.Key + " #" + s.Index + " (a ref parameter)");
                    break;
                case K.Elem:
                    {
                        // an element of the arrays the expression can be (any of the static type's, if that is unknown)
                        if (s.Type is not IArrayTypeSymbol sat) break;
                        var ids = s.Key != null ? ArrayIdsOf(s.Key, uncertified, implsOf) : null;
                        bool any = false;
                        foreach (var id in (IEnumerable<int>?)ids ?? TopIds(sat)) if (D.TypeById[id] is IArrayTypeSymbol && IdIs(id, sat))
                        {
                            if (s_debug && !Types.Key(D.TypeById[id]).StartsWith("System.")) Console.Error.WriteLine("elem-read " + Types.Key(D.TypeById[id]) + " from " + s.Key + (ids == null ? " (any of " + Types.Key(sat) + ")" : "") + " via " + (why.Length > 200 ? why[..200] : why));
                            ArrayContents(D.TypeById[id], why + " <- an element"); any = true;
                        }
                        if (!any && ids == null) RefStores(D.Types.Canonical(sat.ElementType), why);
                        break;
                    }
            }
        }
        }
        _staticRoots = staticRoots;
        int engineStatics = refStatics.Count(k => !staticRoots.Contains(k)) + structStatics.Count(kv => !staticRoots.Contains(kv.Key));
        if (s_debug) foreach (var k in refStatics.Concat(structStatics.Select(kv => kv.Key)).Where(k => !staticRoots.Contains(k))) Console.Error.WriteLine("engine-thread static " + k);
        emptyOnly.RemoveWhere(shared.Contains);
        foreach (var id in emptyOnly) shared.Add(id);
        foreach (var id in delegateOnly) shared.Add(id);
        _emptyOnly = emptyOnly;
        _shared = shared;
        D.Mark($"    sharing: {shared.Count} types another thread can reach, {engineStatics} statics only the engine thread's code names, {lambdaShared.Count} of {D.LambdaSites.Count} lambdas' delegates, {steps} steps, {rounds} rounds, {sw.ElapsedMilliseconds} ms");
        if (whyType != null)
        {
            Console.Error.WriteLine($"sharing: {shared.Count} of {TopIds(D.ObjectType).Count} types reachable by another thread; {uncertified.Count} methods called from elsewhere");
            foreach (var id in shared.Order())
                Console.Error.WriteLine("  " + (D.DisplayById.ContainsKey(id) ? "display " + id : Types.Key(D.TypeById[id])) + "   <- " + whyType[id]);
        }
    }

    readonly Dictionary<string, HashSet<int>> _ptrIds = [];
    static readonly bool s_noPtrIds = Environment.GetEnvironmentVariable("CS2WASM_NO_PTR_IDS") == "1";

    /// <summary>The exact types the values stored through pointers of one type can be (the node PtrNode: the stores
    /// the runtime's array code makes, Array.SetValue and Array.Copy across element types, into any array): a walk
    /// over the sources, as ArrayIdsOf, for every type. Kept as a set of types, not walked with the slot's filter:
    /// the node is reached from every array, and past a few filters a node is visited with none (Analyze), which
    /// took every such value into every array.
    /// What Cs2WasmArrays.GetBoxed returns (Array.Copy's read of an element across element types) is an element of
    /// some array of references, or a new box of a value array's element type (a struct with references: it throws):
    /// what the stores into the arrays of the program put there, and those boxes; not any object.</summary>
    HashSet<int> PtrIds(string start, ITypeSymbol? declared, HashSet<string> uncertified, Dictionary<string, List<string>> implsOf, List<Arenas.Site> arraySites)
    {
        if (_ptrIds.TryGetValue(start, out var known)) return known;
        if (s_expNoPtr) return _ptrIds[start] = [];
        var r = new HashSet<int>();
        var seen = new HashSet<string>();
        var work = new Stack<Src>();
        string? getBoxed = D.AllMethods.FirstOrDefault(m => m.Compiled && m.Sym.Name == "GetBoxed" && m.Sym.ContainingType is { Name: "Cs2WasmArrays", ContainingNamespace: { } ns } && ns.ToDisplayString() == "Cs2Wasm.Runtime") is { } gb ? Types.MethodKey(gb.Sym) : null;
        void Top(ITypeSymbol? t) { if (t != null && seen.Add("top " + Types.Key(t))) foreach (var id in TopIds(t)) r.Add(id); }
        void Node(string key, ITypeSymbol? type)
        {
            if (!seen.Add(key)) return;
            if (_nodes.TryGetValue(key, out var l) && l.Count > 0) foreach (var x in l) work.Push(x);
            else Top(type);
        }
        void Site(Arenas.Site s, ITypeSymbol? type)
        {
            if (!seen.Add("site " + s.Key)) return;
            bool byAddress = !s_noByAddress && s.ByAddress.Count > 0;
            foreach (var x in s.Sources) { if (byAddress && x.Kind == K.Top && x.Key == ByAddressTag) continue; work.Push(x); }
            if (byAddress) foreach (var x in s.ByAddress) work.Push(x);
            foreach (var id in s.TargetIds) r.Add(id);
            if (s.Sources.Count == 0 && s.TargetIds.Count == 0 && !(s.Stored && !s.AddressTaken && !s_noNullSites)) Top(type);
        }
        // (what an array of this type holds: Analyze's ArrayContents, with no filter)
        void Contents(ITypeSymbol a)
        {
            if (!seen.Add("contents " + Types.Key(a))) return;
            var et = D.Types.Canonical(((IArrayTypeSymbol)a).ElementType);
            bool any = false;
            foreach (var s in arraySites)
                if (Types.Key(s.Container) == Types.Key(a) || D.Types.IsSubtype(a, s.Container)) { Site(s, null); any = true; }
            if (!any) Top(et);
            var k = RefNode(et);
            if (_nodes.ContainsKey(k)) Node(k, null);
            if (_refAliases.TryGetValue(k, out var al)) foreach (var x in al) if (_nodes.ContainsKey(x)) Node(x, null);
        }
        Node(start, declared);
        while (work.Count > 0)
        {
            var s = work.Pop();
            switch (s.Kind)
            {
                case K.Exact:
                    if (s.Type != null && D.ExistingTypeId(D.Types.Canonical(s.Type)) is int eid and > 0 && !Types.IsImmortalType(s.Type)) r.Add(eid);
                    else if (s.Type != null && Types.IsRef(s.Type)) Top(s.Type);
                    break;
                case K.Display: r.Add(s.Index); break;
                case K.Top: Top(s.Type); break;
                case K.Param:
                    if (uncertified.Contains(s.Key!)) Top(s.Type); else Node(ParamNode(s.Key!, s.Index), null);
                    break;
                case K.Ret:
                    {
                        if (getBoxed != null && s.Key == getBoxed)
                        {
                            if (!seen.Add("boxed elements")) break;
                            foreach (var id in D.Constructed.Order())
                            {
                                if (id <= 0 || id >= D.TypeById.Count || D.DisplayById.ContainsKey(id) || D.TypeById[id] is not IArrayTypeSymbol at) continue;
                                var et = D.Types.Canonical(at.ElementType);
                                if (Types.IsRef(et)) Contents(D.TypeById[id]);
                                else if (D.ExistingTypeId(et) is int bid and > 0) r.Add(bid);
                            }
                            break;
                        }
                        bool dispatched = s.Index == 1 && implsOf.TryGetValue(s.Key!, out var impls) && impls.Count > 0;
                        var keys = new List<string> { s.Key! };
                        if (dispatched) keys.AddRange(implsOf[s.Key!]);
                        foreach (var k in keys)
                            if (BodyRets.Contains(k)) Node(RetNode(k), null);
                            else if (!(dispatched && k == s.Key)) Top(s.Type);
                        break;
                    }
                case K.Node:
                    if (s.Key!.StartsWith("site ")) { if (D.Arenas.SiteByKey(s.Key[5..]) is { } site) Site(site, s.Type); else Top(s.Type); }
                    else Node(s.Key, s.Type);
                    break;
                case K.RefParam: Top(s.Type); break;
                case K.Elem:
                    {
                        if (s.Type is not IArrayTypeSymbol sat) break;
                        var ids = s.Key != null ? ArrayIdsOf(s.Key, uncertified, implsOf) : null;
                        bool any = false;
                        foreach (var id in (IEnumerable<int>?)ids ?? TopIds(sat)) if (D.TypeById[id] is IArrayTypeSymbol && IdIs(id, sat)) { Contents(D.TypeById[id]); any = true; }
                        if (!any && ids == null) { var k = RefNode(D.Types.Canonical(sat.ElementType)); if (_nodes.ContainsKey(k)) Node(k, null); }
                        break;
                    }
            }
        }
        if (s_debug) Console.Error.WriteLine($"stored through a pointer ({start}): {r.Count} types; went through {string.Join(", ", seen.Where(k => !k.StartsWith("site ") && !k.StartsWith("contents ")).Take(40))}");
        return _ptrIds[start] = r;
    }

    readonly Dictionary<string, HashSet<int>> _arrayIds = [];

    /// <summary>The array types the values of a node can be: a walk over its sources (not over what holds them).</summary>
    HashSet<int> ArrayIdsOf(string start, HashSet<string> uncertified, Dictionary<string, List<string>> implsOf)
    {
        if (_arrayIds.TryGetValue(start, out var known)) return known;
        var r = new HashSet<int>();
        var seen = new HashSet<string>();
        var work = new Stack<Src>();
        string? tag = null;
        void Top(ITypeSymbol? t)
        {
            if (t == null) return;
            if (s_debug) Console.Error.WriteLine("broad-array " + Types.Key(t) + " [" + (tag ?? "?") + "]");
            foreach (var id in TopIds(t)) if (D.TypeById[id] is IArrayTypeSymbol) r.Add(id);
        }
        bool walk = s_walk != null && start == s_walk;
        void Node(string key, ITypeSymbol? declared)
        {
            if (!seen.Add(key)) return;
            if (walk) Console.Error.WriteLine("walk node " + key + " (" + (_nodes.TryGetValue(key, out var wl) ? wl.Count : -1) + " sources)");
            if (_nodes.TryGetValue(key, out var l) && l.Count > 0) foreach (var x in l) work.Push(x);
            else if (key.StartsWith("site ") && D.Arenas.SiteByKey(key[5..]) is { } site && site.Sources.Count > 0) foreach (var x in site.Sources) work.Push(x);
            else Top(declared);
        }
        Node(start, null);
        while (work.Count > 0)
        {
            var s = work.Pop();
            tag = s.Key;
            if (walk) Console.Error.WriteLine("walk pop " + s.Kind + " " + s.Key + " #" + s.Index + " " + (s.Type != null ? Types.Key(s.Type) : "-"));
            switch (s.Kind)
            {
                case K.Exact: if (s.Type is IArrayTypeSymbol && D.ExistingTypeId(D.Types.Canonical(s.Type)) is int eid) r.Add(eid); break;
                case K.Top: Top(s.Type); break;
                case K.Param:
                    if (uncertified.Contains(s.Key!)) Top(s.Type); else Node(ParamNode(s.Key!, s.Index), null);
                    break;
                case K.Ret:
                    {
                        bool dispatched = s.Index == 1 && implsOf.TryGetValue(s.Key!, out var impls) && impls.Count > 0;
                        var keys = new List<string> { s.Key! };
                        if (dispatched) keys.AddRange(implsOf[s.Key!]);
                        foreach (var k in keys)
                            if (BodyRets.Contains(k)) Node(RetNode(k), null);
                            else if (!(dispatched && k == s.Key)) Top(s.Type);
                        break;
                    }
                case K.Node: Node(s.Key!, s.Type); break;
                case K.Elem: Top(s.Type is IArrayTypeSymbol et ? D.Types.Canonical(et.ElementType) : null); break;
                case K.RefParam: Top(s.Type); break;
            }
        }
        return _arrayIds[start] = r;
    }

    // ---- for the arenas: what a slot can hold, by where its values come from rather than by their static types ----

    static readonly bool s_flowDebug = Environment.GetEnvironmentVariable("CS2WASM_FLOW_DEBUG") == "1";
    /// <summary>CS2WASM_FLOW_REFS=1: each store through a ref of type object or an interface, where, of what.</summary>
    static readonly bool s_flowRefs = Environment.GetEnvironmentVariable("CS2WASM_FLOW_REFS") == "1";
    /// <summary>CS2WASM_FLOW_OPTIMISTIC=1 (a measurement, unsound: it leaks cycles): any object of a wide type is taken
    /// as nothing, to see how far the groups could shrink were every source known.</summary>
    static readonly bool s_flowOptimistic = Environment.GetEnvironmentVariable("CS2WASM_FLOW_OPTIMISTIC") == "1";
    /// <summary>The measurement narrows arrays' elements too.</summary>
    public static bool OptimisticFlow => s_flowOptimistic;
    HashSet<string>? _flowUncertified;
    /// <summary>Methods some call of which did not record its `ref` arguments' places (or that are called from elsewhere).</summary>
    HashSet<string>? _flowUncertifiedRefs;
    Dictionary<string, List<string>>? _flowImpls;
    Dictionary<string, List<Src>>? _flowStubParams;

    /// <summary>Which methods' parameters are known (every call marked), and the dispatch stubs' implementations, as
    /// Analyze finds them; once, for SiteTypeIds, which runs before Analyze.</summary>
    void PrepareFlow()
    {
        if (_flowUncertified != null) return;
        // (reflection's stores into statics and structs' fields, if the program can make them: as Analyze does)
        if (!_reflectApplied && D.AllMethods.Any(m => m.Compiled && m.Sym.Name is "SetValue" or "SetValueDirect" && m.Sym.ContainingType?.Name is "RuntimeFieldInfo" or "FieldInfo"))
        { _reflectApplied = true; foreach (var a in _reflectWrites) a(); }
        var keyOf = new Dictionary<WasmFunction, string>();
        foreach (var mi in D.AllMethods) keyOf.TryAdd(mi.Fn, Types.MethodKey(mi.Sym));
        string? KeyOf(WasmFunction f) => keyOf.TryGetValue(f, out var k) ? k : null;
        var uncertified = new HashSet<string>();
        var uncertifiedRefs = new HashSet<string>();
        var stubOf = new Dictionary<WasmFunction, VirtualStub>();
        foreach (var s in D.Stubs.Values) stubOf[s.Fn] = s;
        foreach (var f in D.Module.Functions)
        {
            if (f.IsImport) continue;
            bool inStub = stubOf.ContainsKey(f);
            var self = KeyOf(f);
            foreach (var ins in f.Body)
            {
                if (ins.Ref is not WasmFunction g) continue;
                if (ins.Op == Op.Call && (inStub || self != null && KeyOf(g) == self)) continue;
                bool byValue = ins.Op == Op.Call && (ins.Imm2 & Certified) != 0, byRef = ins.Op == Op.Call && (ins.Imm2 & CertifiedRefs) != 0;
                if (byValue && byRef) continue;
                string? k = KeyOf(g) ?? (stubOf.TryGetValue(g, out var st) ? Types.MethodKey(D.MethodTarget(st.Slot)) : null);
                if (k == null) continue;
                if (!byValue) uncertified.Add(k);
                if (!byRef) uncertifiedRefs.Add(k);
            }
        }
        foreach (var f in D.Module.Functions) if ((f.Exports.Count > 0 || f.TableRoot || f.InTable) && KeyOf(f) is { } ek) uncertified.Add(ek);
        uncertifiedRefs.UnionWith(uncertified);
        var impls = new Dictionary<string, List<string>>();
        var stubParams = new Dictionary<string, List<Src>>();
        foreach (var s in D.Stubs.Values)
        {
            var slotKey = Types.MethodKey(D.MethodTarget(s.Slot));
            if (!impls.TryGetValue(slotKey, out var l)) impls[slotKey] = l = [];
            foreach (var c in s.Cases.Values)
            {
                if (c.impl == null) continue;
                var ik = Types.MethodKey(c.impl.Sym);
                if (ik == slotKey) continue;
                l.Add(ik);
                for (int i = 0; i < s.Slot.Parameters.Length; i++)
                {
                    bool byRef = s.Slot.Parameters[i].RefKind != RefKind.None;
                    var pk = byRef ? RefInNode(ik, i) : ParamNode(ik, i);
                    if (!stubParams.TryGetValue(pk, out var pl)) stubParams[pk] = pl = [];
                    pl.Add(new Src(byRef ? K.RefParam : K.Param, slotKey, i, s.Slot.Parameters[i].Type));
                }
            }
        }
        _flowUncertified = uncertified; _flowUncertifiedRefs = uncertifiedRefs; _flowImpls = impls; _flowStubParams = stubParams;
    }

    /// <summary>SiteTypeIds as a walk from one site (CS2WASM_FLOW_DEBUG: it prints each source that makes the site
    /// any object, with the path to it; the answer must be SiteTypeIds').</summary>
    HashSet<int>? SiteTypeIdsWalk(Arenas.Site site, int cap)
    {
        var uncertified = _flowUncertified!;
        var r = new HashSet<int>();
        var seen = new HashSet<string>();
        var work = new Stack<(Src s, int from)>();
        bool wide = false;
        Src cur = default;
        var causes = s_flowDebug ? new HashSet<string>() : null;
        // (CS2WASM_FLOW_DEBUG: the sources walked, each with the one it was reached from, to print a cause's path)
        var trail = s_flowDebug ? new List<(Src s, int from)>() : null;
        int curIx = -1;
        void Push(Src x) => work.Push((x, curIx));
        string Path()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = curIx, n = 0; i >= 0 && n < 12; i = trail![i].from, n++)
            {
                var x = trail![i].s;
                sb.Append(" <- ").Append(x.Kind).Append(' ').Append(x.Key);
                if (x.Kind is K.Param or K.RefParam) sb.Append(" #").Append(x.Index);
            }
            return sb.ToString();
        }
        void Top(ITypeSymbol? t)
        {
            if (t == null) return;
            if (s_flowOptimistic && (Types.IsObject(t) || t.TypeKind == TypeKind.Interface || TopIds(t).Count > 50)) return;
            if (Types.IsObject(t))
            {
                wide = true;
                // (CS2WASM_FLOW_DEBUG: every source that makes the site any object, once each)
                if (s_flowDebug && causes!.Add($"{cur.Kind} {cur.Key} #{cur.Index} {(cur.Type != null ? Types.Key(cur.Type) : "")}"))
                {
                    Console.Error.WriteLine($"wide-by {site.Key}: {cur.Kind} {cur.Key} #{cur.Index} {(cur.Type != null ? Types.Key(cur.Type) : "")}");
                    if (causes.Count <= 3) Console.Error.WriteLine("  path" + Path());
                }
                return;
            }
            if (!seen.Add("top " + Types.Key(t))) return;
            foreach (var id in TopIds(t)) r.Add(id);
        }
        void Node(string key, ITypeSymbol? declared)
        {
            if (!seen.Add(key)) return;
            bool any = false;
            if (_nodes.TryGetValue(key, out var l)) { foreach (var x in l) Push(x); any = true; }
            if (_flowStubParams!.TryGetValue(key, out var sp)) { foreach (var x in sp) Push(x); any = true; }
            if (!any) Top(declared);
        }
        void SiteSources(Arenas.Site s, ITypeSymbol? declared)
        {
            if (!seen.Add("site " + s.Key)) return;
            foreach (var x in s.Sources) Push(x);
            foreach (var id in s.TargetIds) r.Add(id);
            if (s.Sources.Count == 0 && s.TargetIds.Count == 0) Top(declared);
            if (s.DeclaredType != null && RefsReach(s)) Refs(s.DeclaredType);
        }
        void Refs(ITypeSymbol t)
        {
            var k = RefNode(D.Types.Canonical(t));
            if (_nodes.ContainsKey(k)) Node(k, null);
            if (_refAliases.TryGetValue(k, out var al)) foreach (var a in al) if (_nodes.ContainsKey(a)) Node(a, null);
        }
        var slotType = D.Types.Canonical(site.DeclaredType);
        SiteSources(site, slotType);
        // (an array's element is stored also through a wider array type that it is: an object[] that is a string[])
        if (site.IsArray)
            foreach (var other in D.Arenas.AllSites)
                if (other.IsArray && other != site && D.Types.IsSubtype(site.Container, other.Container)) SiteSources(other, null);
        while (work.Count > 0 && (!wide || s_flowDebug) && (r.Count <= cap || s_flowDebug))
        {
            var (s, from) = work.Pop();
            cur = s;
            if (trail != null) { trail.Add((s, from)); curIx = trail.Count - 1; }
            switch (s.Kind)
            {
                case K.Exact:
                    if (s.Type != null && D.ExistingTypeId(D.Types.Canonical(s.Type)) is int eid and > 0 && !Types.IsImmortalType(s.Type)) r.Add(eid);
                    else if (s.Type != null && Types.IsRef(s.Type)) Top(s.Type);
                    break;
                case K.Display: r.Add(s.Index); break;
                case K.Top: Top(s.Type); break;
                case K.Param:
                    if (uncertified.Contains(s.Key!)) Top(s.Type); else Node(ParamNode(s.Key!, s.Index), s.Type);
                    break;
                case K.Ret:
                    {
                        bool dispatched = s.Index == 1 && _flowImpls!.TryGetValue(s.Key!, out var impls) && impls.Count > 0;
                        var keys = new List<string> { s.Key! };
                        if (dispatched) keys.AddRange(_flowImpls![s.Key!]);
                        foreach (var k in keys)
                            if (BodyRets.Contains(k)) Node(RetNode(k), s.Type);
                            else if (!(dispatched && k == s.Key)) Top(s.Type);
                        break;
                    }
                case K.Node:
                    if (s.Key!.StartsWith("site ")) { if (D.Arenas.SiteByKey(s.Key[5..]) is { } os) SiteSources(os, s.Type); else Top(s.Type); }
                    else { Node(s.Key, s.Type); if (s.Type != null && NodeRefsReach(s.Key)) Refs(s.Type); }
                    break;
                case K.Elem:
                    Top(s.Type is IArrayTypeSymbol et ? D.Types.Canonical(et.ElementType) : null);
                    break;
                case K.RefParam:
                    // (the places the callers pass, and what the method and those it passes the ref to store there)
                    if (_flowUncertifiedRefs!.Contains(s.Key!)) Top(s.Type);
                    else
                    {
                        Node(RefInNode(s.Key!, s.Index), null);
                        Node(OutNode(s.Key!, s.Index), null);
                        if (s.Type != null && NodeRefsReach(OutNode(s.Key!, s.Index))) Refs(s.Type);
                    }
                    break;
            }
        }
        if (wide || r.Count > cap) return null;
        // (a slot holds objects of its declared type only, whatever was stored through a wider one)
        r.RemoveWhere(id => !IdIs(id, slotType));
        return r;
    }

    ITypeSymbol? RefTypeOf(string refNodeKey) => _refTypes.TryGetValue(refNodeKey, out var t) ? t : null;

    readonly Dictionary<string, ITypeSymbol> _refTypes = [];
    /// <summary>Unsafe.As&lt;TFrom, TTo&gt;(ref): a store through the TTo ref lands in a TFrom slot (key: TFrom's ref node).</summary>
    readonly Dictionary<string, HashSet<string>> _refAliases = [];
    public void RefAlias(ITypeSymbol from, ITypeSymbol to)
    {
        from = D.Types.Canonical(from); to = D.Types.Canonical(to);
        if (!Types.IsRef(from) && !Types.IsRef(to)) return;
        var fk = RefNode(from);
        if (!_refAliases.TryGetValue(fk, out var s)) _refAliases[fk] = s = [];
        s.Add(RefNode(to));
        // (and back: the two name one place)
        var tk = RefNode(to);
        if (!_refAliases.TryGetValue(tk, out var s2)) _refAliases[tk] = s2 = [];
        s2.Add(fk);
    }
}

sealed partial class FunctionCompiler
{
    Sharing? Sh => D.Sharing;

    /// <summary>Threads: an object of this static type is one no other thread can reach (the last compilation's answer).</summary>
    bool UnsharedContainer(ITypeSymbol t) => D.ThreadMode && D.Sharing is { } s && s.PriorThreadLocal(t);

    /// <summary>Threads: a static that only the engine thread's code names (Sharing.PriorEngineStatic): no other
    /// thread reads or stores it, and the code that does runs on one thread. It is read, borrowed from and read once
    /// by a loop as a static of a program without threads is.</summary>
    bool EngineStatic(IFieldSymbol f) =>
        D.ThreadMode && f.IsStatic && !f.IsVolatile && D.Sharing is { } s && s.PriorEngineStatic(Driver.StaticKey(T.Canonical(Owner(f)), StorageField(f)));

    /// <summary>Threads: the place an expression reads is one no other thread stores: a field of an object of such a
    /// type, an element of such an array, a thread's own static.</summary>
    bool UnsharedPlace(IOperation op)
    {
        switch (op)
        {
            case IFieldReferenceOperation { Field.IsStatic: true } sf: return IsThreadStatic(StorageField(sf.Field)) || EngineStatic(sf.Field);
            case IFieldReferenceOperation { Instance: { Type: not null } inst } when !TypeOf(inst).IsValueType: return UnsharedContainer(TypeOf(inst));
            case IArrayElementReferenceOperation { ArrayReference.Type: not null } ae: return UnsharedContainer(TypeOf(ae.ArrayReference));
            default: return false;
        }
    }
    HashSet<ISymbol>? _writtenParams;
    IOperation? _sharingBody;

    bool ParamWritten(IParameterSymbol p)
    {
        if (_writtenParams == null)
        {
            _writtenParams = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (_sharingBody != null)
                foreach (var op in _sharingBody.DescendantsAndSelf())
                {
                    IOperation? target = op switch
                    {
                        IAssignmentOperation a => a.Target,
                        IIncrementOrDecrementOperation inc => inc.Target,
                        IArgumentOperation { Parameter.RefKind: not RefKind.None and not RefKind.In } arg => arg.Value,
                        IAddressOfOperation ao => ao.Reference,
                        IDeclarationExpressionOperation => null,
                        _ => null,
                    };
                    if (target is IParameterReferenceOperation pr) _writtenParams.Add(pr.Parameter.OriginalDefinition);
                    if (op is IAssignmentOperation { Target: ITupleOperation tup })
                        foreach (var e in tup.Elements) if (e is IParameterReferenceOperation tp) _writtenParams.Add(tp.Parameter.OriginalDefinition);
                }
        }
        return _writtenParams.Contains(p.OriginalDefinition);
    }

    HashSet<ISymbol>? _refReassigned;
    /// <summary>A `ref` parameter made to point elsewhere in the body (`p = ref x`).</summary>
    bool RefReassigned(IParameterSymbol p)
    {
        if (_refReassigned == null)
        {
            _refReassigned = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (_sharingBody != null)
                foreach (var op in _sharingBody.DescendantsAndSelf())
                    if (op is ISimpleAssignmentOperation { IsRef: true, Target: IParameterReferenceOperation tp }) _refReassigned.Add(tp.Parameter.OriginalDefinition);
        }
        return _sharingBody == null || _refReassigned.Contains(p.OriginalDefinition);
    }

    Dictionary<ISymbol, List<IOperation>?>? _localSources;
    /// <summary>Locals passed as `out` or `ref` to a method with a body: what it stores through the parameter.</summary>
    Dictionary<ISymbol, List<Sharing.Src>>? _localOutSources;

    /// <summary>The values a local is ever assigned in this body; null when that is not all it can hold (captured,
    /// passed by reference, a ref local, a foreach or out or pattern variable).</summary>
    List<IOperation>? LocalSources(ILocalSymbol l) => _sharingBody == null || l.IsRef || IsCaptured(l) ? null : AssignedValues(l);

    /// <summary>A local of this body that lambdas or local functions capture lives in a closure's slot. Where the
    /// slot's values come from, when every write of the local is one this body's text shows: its declaration's
    /// initializer and plain assignments, here and in the nested functions (their bodies are part of this body), an
    /// `out` argument of a known callee. Not for a local a ref is made of, a parameter, `this`, or a state machine's
    /// variable: those slots hold any object of their type, as before (Arenas.AddDisplaySites).
    /// CS2WASM_NO_DISPLAY_SOURCES=1: every slot so.</summary>
    void RecordCapturedSources()
    {
        if (Sh == null || !D.ThreadMode || _sharingBody == null || _capturedHere.Count == 0 || Driver.s_noDisplaySources || _machine != null) return;
        HashSet<ISymbol>? refd = null;
        foreach (var (sym, d) in _capturedHere)
        {
            // (a parameter no code of the body writes: the slot holds what the method was called with)
            if (sym is IParameterSymbol { RefKind: RefKind.None } cp && SharingKey is { } ckey && !Sharing.s_noLambdaSites
                && SymbolEqualityComparer.Default.Equals(cp.ContainingSymbol?.OriginalDefinition, M.OriginalDefinition) && !ParamWritten(cp)
                && d.Slots.TryGetValue(sym, out var pslot) && Types.IsRef(pslot.type))
            {
                D.Arenas.DisplaySources[(d, pslot.offset)] = [new Sharing.Src(Sharing.K.Param, ckey, cp.Ordinal, pslot.type)];
                continue;
            }
            if (sym is not ILocalSymbol { IsRef: false } l || !d.Slots.TryGetValue(sym, out var slot) || !Types.IsRef(slot.type)) continue;
            if (AssignedValues(l) is not { } vals) continue;
            // (a ref of the local, kept in a ref local, returned or chosen by a ref conditional: stores through it)
            if (refd == null)
            {
                refd = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                foreach (var op in _sharingBody.DescendantsAndSelf())
                {
                    if (op is not ILocalReferenceOperation lr) continue;
                    bool asRef = lr.Parent switch
                    {
                        IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => true,
                        IConditionalOperation { IsRef: true } => true,
                        IReturnOperation => true,
                        IArgumentOperation { Parameter.RefKind: not RefKind.None } => OutCallee(lr.Parent) == null,
                        IAddressOfOperation => true,
                        _ => false,
                    };
                    // (a value returned is a read, unless the function returns by reference: that is not looked up
                    // here, so only a local that is never returned keeps its sources)
                    if (asRef) refd.Add(lr.Local);
                }
            }
            if (refd.Contains(l)) continue;
            var srcs = new List<Sharing.Src>();
            foreach (var v in Sharing.s_expDisplayFirstOnly ? vals.Take(1) : vals) FlowSrcsInto(v, srcs, 1);
            if (_localOutSources != null && _localOutSources.TryGetValue(l, out var outs)) srcs.AddRange(outs);
            D.Arenas.DisplaySources[(d, slot.offset)] = srcs;
        }
    }

    Dictionary<SyntaxNode, int>? _lambdaSiteAt;
    /// <summary>The lambda site of a lambda in this method's text (Driver.LambdaSites), made when first asked for:
    /// by the code that makes the delegate, or by a source that names it before that code is compiled.</summary>
    int LambdaSiteAt(SyntaxNode lambda)
    {
        _lambdaSiteAt ??= [];
        if (!_lambdaSiteAt.TryGetValue(lambda, out int site)) _lambdaSiteAt[lambda] = site = D.NewLambdaSite();
        return site;
    }

    List<IOperation>? AssignedValues(ILocalSymbol l)
    {
        if (_localSources == null)
        {
            _localSources = new Dictionary<ISymbol, List<IOperation>?>(SymbolEqualityComparer.Default);
            void Bad(ISymbol s) => _localSources[s] = null;
            void Add(ISymbol s, IOperation v)
            {
                if (_localSources.TryGetValue(s, out var l0)) { l0?.Add(v); return; }
                _localSources[s] = [v];
            }
            foreach (var op in _sharingBody.DescendantsAndSelf())
            {
                switch (op)
                {
                    case IVariableDeclaratorOperation vd:
                        if (vd.GetVariableInitializer() is { } init) Add(vd.Symbol, init.Value);
                        else if (!_localSources.ContainsKey(vd.Symbol)) _localSources[vd.Symbol] = [];
                        break;
                    case IAssignmentOperation { Target: ILocalReferenceOperation tl } a when a is ISimpleAssignmentOperation or ICoalesceAssignmentOperation:
                        Add(tl.Local, a.Value);
                        break;
                    case IAssignmentOperation { Target: ILocalReferenceOperation tl2 }:
                        Bad(tl2.Local); // (a compound assignment: an operator's result)
                        break;
                    case IAssignmentOperation { Target: ITupleOperation or IDeclarationExpressionOperation } da:
                        foreach (var d in da.Target.DescendantsAndSelf()) if (d is ILocalReferenceOperation dl) Bad(dl.Local);
                        break;
                    case IArgumentOperation { Parameter: { RefKind: RefKind.Out or RefKind.Ref } ap, Value: var av } when OutCallee(op) is { } oc
                        && (av is ILocalReferenceOperation { Local.IsRef: false } || av is IDeclarationExpressionOperation { Expression: ILocalReferenceOperation }):
                        {
                            var ol = av is ILocalReferenceOperation l1 ? l1.Local : (ILocalSymbol)((ILocalReferenceOperation)((IDeclarationExpressionOperation)av).Expression).Local;
                            _localOutSources ??= new Dictionary<ISymbol, List<Sharing.Src>>(SymbolEqualityComparer.Default);
                            if (!_localOutSources.TryGetValue(ol, out var os)) _localOutSources[ol] = os = [];
                            os.Add(new Sharing.Src(Sharing.K.Node, Sharing.OutNode(oc, ap.Ordinal)));
                            if (!_localSources.ContainsKey(ol)) _localSources[ol] = [];
                            break;
                        }
                    case IArgumentOperation { Parameter.RefKind: not RefKind.None, Value: var av }:
                        foreach (var d in av.DescendantsAndSelf()) if (d is ILocalReferenceOperation al) Bad(al.Local);
                        break;
                    case IAddressOfOperation { Reference: ILocalReferenceOperation ao }: Bad(ao.Local); break;
                    case IForEachLoopOperation fe:
                        {
                            // (`foreach (var x in c)`: what c's elements are, Current's result)
                            var src = ForEachSource(fe);
                            ILocalSymbol? lv = fe.LoopControlVariable switch
                            {
                                IVariableDeclaratorOperation vd2 => vd2.Symbol,
                                ILocalReferenceOperation lr2 => lr2.Local,
                                _ => null,
                            };
                            if (lv != null && src != null)
                            {
                                _localOutSources ??= new Dictionary<ISymbol, List<Sharing.Src>>(SymbolEqualityComparer.Default);
                                if (!_localOutSources.TryGetValue(lv, out var fs)) _localOutSources[lv] = fs = [];
                                fs.Add(src.Value);
                                if (!_localSources.ContainsKey(lv)) _localSources[lv] = [];
                            }
                            else foreach (var d in fe.LoopControlVariable.DescendantsAndSelf()) if (d is ILocalReferenceOperation fl) Bad(fl.Local);
                            foreach (var x in fe.Locals) if (!SymbolEqualityComparer.Default.Equals(x, lv) || src == null) Bad(x);
                            break;
                        }
                    case IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol dp } dpo:
                        // (`e is T v`, `case T v`: what e holds, of which the T objects)
                        if (MatchedValue(dpo) is { } mv) Add(dp, mv); else Bad(dp);
                        break;
                    case IRecursivePatternOperation { DeclaredSymbol: ILocalSymbol rp }: Bad(rp); break;
                    case ICatchClauseOperation cc: foreach (var x in cc.Locals) Bad(x); break;
                    case IUsingOperation u: foreach (var x in u.Locals) if (!_localSources.ContainsKey(x)) Bad(x); break;
                    case IIncrementOrDecrementOperation { Target: ILocalReferenceOperation il }: Bad(il.Local); break;
                    case IDeconstructionAssignmentOperation dc:
                        foreach (var d in dc.Target.DescendantsAndSelf()) if (d is ILocalReferenceOperation dl2) Bad(dl2.Local);
                        break;
                }
            }
        }
        return _localSources.TryGetValue(l, out var r) ? r : null;
    }

    /// <summary>Where a foreach loop's elements come from: an array's elements, or its enumerator's Current.</summary>
    Sharing.Src? ForEachSource(IForEachLoopOperation fe)
    {
        if (fe.IsAsynchronous || fe.Collection.Type == null) return null;
        var coll = fe.Collection;
        while (coll is IConversionOperation { IsImplicit: true } c) coll = c.Operand;
        if (coll.Type != null && TypeOf(coll) is IArrayTypeSymbol at && at.Rank == 1)
        {
            var key = Sh!.NewArrayNode();
            Sh.Add(key, FlowSrcs(coll));
            return new Sharing.Src(Sharing.K.Elem, key, Type: T.Canonical(at));
        }
        if (fe.Syntax is not Microsoft.CodeAnalysis.CSharp.Syntax.CommonForEachStatementSyntax fs) return null;
        var info = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetForEachStatementInfo(D.ModelFor(fs.SyntaxTree), fs);
        if (info.CurrentProperty?.GetMethod is not { } cur || info.ElementConversion is { IsUserDefined: true }) return null;
        var rs = new List<Sharing.Src>(1);
        AddRet(cur, cur.IsVirtual || cur.IsAbstract || cur.IsOverride || cur.ContainingType.TypeKind == TypeKind.Interface, rs);
        return rs.Count == 1 ? rs[0] : null;
    }

    /// <summary>The value a top-level pattern is matched against (null for a nested one).</summary>
    static IOperation? MatchedValue(IPatternOperation p)
    {
        var parent = p.Parent;
        while (parent is INegatedPatternOperation or IBinaryPatternOperation) return null;
        return parent switch
        {
            IIsPatternOperation ip => ip.Value,
            IPatternCaseClauseOperation { Parent: ISwitchCaseOperation { Parent: ISwitchOperation sw } } => sw.Value,
            ISwitchExpressionArmOperation { Parent: ISwitchExpressionOperation se } => se.Value,
            _ => null,
        };
    }

    /// <summary>The method an argument is passed to, when it is one whose stores through its parameters are all
    /// recorded (a body of its own, called directly); else null.</summary>
    string? OutCallee(IOperation arg)
    {
        IMethodSymbol? m = arg.Parent switch
        {
            IInvocationOperation inv when !inv.IsVirtual || !(inv.TargetMethod.IsVirtual || inv.TargetMethod.IsAbstract || inv.TargetMethod.IsOverride) => inv.TargetMethod,
            IObjectCreationOperation oc => oc.Constructor,
            _ => null,
        };
        if (m == null || m.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.DelegateInvoke) return null;
        if (m.ContainingType.TypeKind == TypeKind.Interface || m.IsExtern) return null;
        var sm = SubstMethod(m);
        if (D.IsIntrinsic(sm)) return null;
        return Types.MethodKey(D.MethodTarget(sm));
    }

    /// <summary>The method key this function's parameters and results are recorded under (null: lambdas and local
    /// functions, whose callers are delegates or closures).</summary>
    string? SharingKey => _closure == null && _machine == null && M.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.LocalFunction) ? Types.MethodKey(M) : null;

    static Sharing.Src TopSrc(ITypeSymbol t) => new(Sharing.K.Top, Type: t);

    Sharing.Src TopAt(ITypeSymbol t, IOperation v) => Sharing.Debug ? new(Sharing.K.Top, v.Kind + " " + DebugText(v) + " in " + M.ToDisplayString(), Type: t) : TopSrc(t);

    static string DebugText(IOperation v)
    {
        var s = v.Syntax.ToString().Replace("\r", " ").Replace("\n", " ");
        return s.Length > 80 ? s[..80] : s;
    }

    /// <summary>Where the reference values an expression produces come from (Sharing.Src).</summary>
    List<Sharing.Src> FlowSrcs(IOperation? v)
    {
        var r = new List<Sharing.Src>(1);
        if (v != null) FlowSrcsInto(v, r, 0);
        return r;
    }

    void FlowSrcsInto(IOperation v, List<Sharing.Src> r, int depth)
    {
        while (true)
        {
            if (v is IConversionOperation c)
            {
                if (c.OperatorMethod != null) { AddRet(c.OperatorMethod, false, r); return; }
                if (c.Operand.Type is { IsValueType: true } && c.Type is { IsReferenceType: true })
                {
                    // a box: of the struct (of a nullable's value)
                    var vt = TypeOf(c.Operand);
                    if (vt is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nn) vt = T.Canonical(nn.TypeArguments[0]);
                    r.Add(new Sharing.Src(Sharing.K.Exact, Type: vt));
                    return;
                }
                // a cast down (or `as`): an object of the type it names, whatever the operand could be
                if (!c.IsImplicit && c.Type is { IsReferenceType: true } && c.Operand.Type is { } ot && c.Conversion.IsReference && !c.Conversion.IsImplicit
                    && TypeOf(c) is var ct && !Types.IsObject(ct) && !D.Types.IsSubtype(TypeOf(c.Operand), ct))
                {
                    r.Add(TopAt(ct, c));
                    return;
                }
                v = c.Operand; continue;
            }
            if (v is IParenthesizedOperation p) { v = p.Operand; continue; }
            break;
        }
        if (v.ConstantValue is { HasValue: true } cv) { if (cv.Value is string) r.Add(new Sharing.Src(Sharing.K.Exact, Type: D.Main.GetSpecialType(SpecialType.System_String))); return; }
        if (v.Type == null) return;
        var t = TypeOf(v);
        if (!Types.IsRef(t)) return;
        if (depth > 8) { r.Add(TopAt(t, v)); return; }
        switch (v)
        {
            case IDefaultValueOperation:
                return;
            // (an array of no element: `new T[0]`, of one dimension, with no initializer)
            // (a lambda made into a delegate: the delegate of this place in the text, Sharing.LambdaTag)
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation laf } when D.ThreadMode && !Sharing.s_noLambdaSites && t.TypeKind == TypeKind.Delegate:
                r.Add(new Sharing.Src(Sharing.K.Exact, Sharing.LambdaTag, LambdaSiteAt(laf.Syntax), t));
                return;
            // (in a program with threads: the sources of one without are what they were)
            case IArrayCreationOperation { DimensionSizes: [{ ConstantValue: { HasValue: true, Value: 0 } }], Initializer: null or { ElementValues.Length: 0 } } when D.ThreadMode && t is IArrayTypeSymbol { Rank: 1 }:
            case IArrayCreationOperation when D.ThreadMode && Sharing.s_expEveryArrayEmpty && t is IArrayTypeSymbol:
            case ICollectionExpressionOperation { Elements.Length: 0 } when D.ThreadMode && t is IArrayTypeSymbol { Rank: 1 }:
                r.Add(new Sharing.Src(Sharing.K.Exact, Sharing.EmptyArrayTag, Type: t));
                return;
            case IObjectCreationOperation or ITypeParameterObjectCreationOperation or IArrayCreationOperation or IDelegateCreationOperation or IAnonymousObjectCreationOperation:
                r.Add(t.TypeKind is TypeKind.Interface or TypeKind.TypeParameter || t is INamedTypeSymbol { IsAbstract: true } ? TopSrc(t) : new Sharing.Src(Sharing.K.Exact, Type: t));
                return;
            case IInterpolatedStringOperation:
                r.Add(new Sharing.Src(Sharing.K.Exact, Type: t));
                return;
            case ILocalReferenceOperation lr when SharingKey != null || _closure == null:
                {
                    var assigned = LocalSources(lr.Local);
                    if (assigned == null) { r.Add(TopAt(t, v)); return; }
                    foreach (var a in assigned) FlowSrcsInto(a, r, depth + 1);
                    if (_localOutSources != null && _localOutSources.TryGetValue(lr.Local, out var outs)) r.AddRange(outs);
                    return;
                }
            case IParameterReferenceOperation { Parameter: var lps } when M.MethodKind == MethodKind.AnonymousFunction && lps.RefKind == RefKind.None
                && SymbolEqualityComparer.Default.Equals(lps.ContainingSymbol, M) && !IsCaptured(lps) && !ParamWritten(lps):
                r.Add(new Sharing.Src(Sharing.K.Node, Sharing.LambdaNode(M, lps.Ordinal), Type: t));
                return;
            case IParameterReferenceOperation { Parameter: { RefKind: not RefKind.None } rps } when SharingKey is { } rkey
                && SymbolEqualityComparer.Default.Equals(rps.ContainingSymbol?.OriginalDefinition, M.OriginalDefinition) && !RefReassigned(rps):
                r.Add(new Sharing.Src(Sharing.K.RefParam, rkey, rps.Ordinal, t));
                return;
            case IParameterReferenceOperation pr:
                {
                    var key = SharingKey;
                    var ps = pr.Parameter;
                    if (key != null && ps.RefKind == RefKind.None && SymbolEqualityComparer.Default.Equals(ps.ContainingSymbol?.OriginalDefinition, M.OriginalDefinition)
                        && !IsCaptured(ps) && !ParamWritten(ps))
                        r.Add(new Sharing.Src(Sharing.K.Param, key, ps.Ordinal, t));
                    else r.Add(TopAt(t, v));
                    return;
                }
            // (Unsafe.Add(ref x, n) and its kin: a ref into the place x names, an element of the same array or span)
            case IInvocationOperation uadd when SamePlaceRef(uadd) is { } basePlace:
                FlowSrcsInto(basePlace, r, depth + 1);
                return;
            // Interlocked.Exchange and CompareExchange, Volatile.Read: what the place held
            case IInvocationOperation { TargetMethod.ContainingType: { } ict } xinv when Types.FullName(ict.OriginalDefinition) is "System.Threading.Interlocked" or "System.Threading.Volatile"
                && xinv.TargetMethod.Name is "Exchange" or "CompareExchange" or "Read" && xinv.Arguments.Length > 0 && xinv.Arguments[0].Parameter?.RefKind is RefKind.Ref or RefKind.RefReadOnlyParameter or RefKind.In:
                FlowSrcsInto(xinv.Arguments[0].Value, r, depth + 1);
                return;
            case IInvocationOperation inv:
                if (inv.TargetMethod.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.DelegateInvoke) { r.Add(TopAt(t, v)); return; }
                AddRet(inv.TargetMethod, inv.IsVirtual, r);
                return;
            case IPropertyReferenceOperation prop:
                if (prop.Property.Parameters.Length == 0 && Driver.AutoPropertyField(prop.Property.OriginalDefinition) is { } bf && IsDirectAutoProperty(prop.Property, prop.Instance))
                {
                    FieldSrc(bf, Owner(prop.Property), t, r);
                    return;
                }
                if (prop.Property.GetMethod is { } g) { AddRet(g, prop.Property.IsVirtual || prop.Property.IsAbstract || prop.Property.IsOverride || prop.Property.ContainingType.TypeKind == TypeKind.Interface, r); return; }
                r.Add(TopAt(t, v));
                return;
            case IFieldReferenceOperation f:
                FieldSrc(StorageField(f.Field), Owner(f.Field), t, r);
                return;
            case IArrayElementReferenceOperation ae when ae.ArrayReference.Type != null && TypeOf(ae.ArrayReference) is IArrayTypeSymbol at:
                {
                    // (which arrays it can be: what the array expression holds, Sharing.ArrayIdsOf)
                    var key = Sh!.NewArrayNode();
                    var arr = new List<Sharing.Src>(1);
                    // (with the depth: `a = a[i]` would go round for ever)
                    FlowSrcsInto(ae.ArrayReference, arr, depth + 1);
                    Sh.Add(key, arr);
                    r.Add(new Sharing.Src(Sharing.K.Elem, key, Type: T.Canonical(at)));
                    return;
                }
            case IConditionalOperation co when co.WhenFalse != null:
                FlowSrcsInto(co.WhenTrue, r, depth + 1); FlowSrcsInto(co.WhenFalse, r, depth + 1);
                return;
            case ICoalesceOperation co:
                FlowSrcsInto(co.Value, r, depth + 1); FlowSrcsInto(co.WhenNull, r, depth + 1);
                return;
            case ISwitchExpressionOperation sw:
                foreach (var arm in sw.Arms) FlowSrcsInto(arm.Value, r, depth + 1);
                return;
            case IAssignmentOperation a:
                FlowSrcsInto(a.Value, r, depth + 1);
                return;
            default:
                r.Add(Sharing.Debug ? new Sharing.Src(Sharing.K.Top, v.Kind + " " + DebugText(v) + " in " + M.Name, Type: t) : TopSrc(t));
                return;
        }
    }

    void FieldSrc(IFieldSymbol f, INamedTypeSymbol owner, ITypeSymbol t, List<Sharing.Src> r)
    {
        // (a ref field's value is what the place it points at holds: any slot of its type)
        if (f.RefKind != RefKind.None) { r.Add(TopSrc(t)); return; }
        if (f.IsStatic) { r.Add(new Sharing.Src(Sharing.K.Node, Sharing.StaticNode(Driver.StaticKey(T.Canonical(owner), f)), Type: t)); return; }
        if (owner.IsValueType) { r.Add(new Sharing.Src(Sharing.K.Node, Sharing.StructFieldNode(T.Canonical(owner), f), Type: t)); return; }
        var co = T.Canonical(owner);
        r.Add(new Sharing.Src(Sharing.K.Node, "site f " + Types.Key(co) + " " + T.FieldIn(co, f).Offset, Type: t));
    }

    void AddRet(IMethodSymbol m, bool virt, List<Sharing.Src> r)
    {
        var sm = SubstMethod(m);
        var rt = T.Canonical(sm.ReturnType);
        // (reflection's call of a member: what the members it can call return, Reflection.cs)
        if (sm.Name == "Invoke" && Types.FullName(sm.ContainingType.OriginalDefinition) == "Cs2Wasm.Runtime.TypeInfo")
        { r.Add(new Sharing.Src(Sharing.K.Node, Sharing.ReflectNode)); return; }
        // (the runtime's boxing by a type handle: a box, of some value type)
        if (sm.Name is "InternalBoxEnum" or "ToObject" && Types.FullName(sm.ContainingType.OriginalDefinition) == "System.Enum")
        { r.Add(TopSrc(D.LibType("System.Enum"))); return; }
        if (sm.Name is "Box" or "InternalBox" && Types.FullName(sm.ContainingType.OriginalDefinition) == "System.Runtime.CompilerServices.RuntimeHelpers")
        { r.Add(TopSrc(D.LibType("System.ValueType"))); return; }
        if (D.IsIntrinsic(sm) || sm.ReturnsByRef || sm.ReturnsByRefReadonly) { r.Add(Sharing.Debug ? new Sharing.Src(Sharing.K.Top, "intrinsic " + sm.Name + " in " + M.Name, Type: rt) : TopSrc(rt)); return; }
        bool dispatch = virt && (sm.IsVirtual || sm.IsAbstract || sm.IsOverride || sm.ContainingType.TypeKind == TypeKind.Interface);
        r.Add(new Sharing.Src(Sharing.K.Ret, Types.MethodKey(D.MethodTarget(sm)), dispatch ? 1 : 0, rt));
    }

    /// <summary>The parameters whose arguments were recorded, in the calls being compiled (method key, ordinal).</summary>
    readonly List<(string key, int ord)> _recorded = [];

    /// <summary>An argument's sources, as the callee's parameter's.</summary>
    internal void RecordParam(IMethodSymbol m, IParameterSymbol p, List<Sharing.Src> srcs)
    {
        // (a function pointer's signature: no method's parameters)
        if (Sh == null || p.RefKind != RefKind.None || m.ContainingType == null || m.MethodKind == MethodKind.FunctionPointerSignature || !Types.IsRef(T.Canonical(p.Type))) return;
        var key = Types.MethodKey(D.MethodTarget(m));
        Sh.Add(Sharing.ParamNode(key, p.Ordinal), srcs);
        _recorded.Add((key, p.Ordinal));
    }

    /// <summary>A `ref`, `in` or `out` argument's place: what it holds, as the callee's parameter's (RefInNode).</summary>
    internal void RecordRefIn(IMethodSymbol m, IParameterSymbol p, IOperation? place)
    {
        if (Sh == null || p.RefKind == RefKind.None || m.ContainingType == null || m.MethodKind == MethodKind.FunctionPointerSignature || !Types.IsRef(T.Canonical(p.Type))) return;
        var key = Types.MethodKey(D.MethodTarget(m));
        // (a new variable, a discard: nothing in it before the callee stores there)
        if (place is not (null or IDeclarationExpressionOperation or IDiscardOperation)) Sh.Add(Sharing.RefInNode(key, p.Ordinal), FlowSrcs(place));
        _recorded.Add((key, -1 - p.Ordinal));
    }

    /// <summary>Whether every reference parameter of m passed by `ref`, `in` or `out` had its place recorded since `from`.</summary>
    bool AllRefsRecorded(IMethodSymbol m, int from)
    {
        if (m.ContainingType == null) return false;
        var key = Types.MethodKey(D.MethodTarget(m));
        foreach (var p in m.Parameters)
        {
            if (p.RefKind == RefKind.None || !Types.IsRef(T.Canonical(p.Type))) continue;
            bool found = false;
            for (int i = from; i < _recorded.Count && !found; i++) found = _recorded[i].ord == -1 - p.Ordinal && _recorded[i].key == key;
            if (!found) return false;
        }
        return true;
    }

    /// <summary>Whether every reference parameter of m passed by value had its argument recorded since `from`.</summary>
    bool AllRecorded(IMethodSymbol m, int from)
    {
        if (m.ContainingType == null) return false;
        var key = Types.MethodKey(D.MethodTarget(m));
        foreach (var p in m.Parameters)
        {
            if (p.RefKind != RefKind.None || !Types.IsRef(T.Canonical(p.Type))) continue;
            bool found = false;
            for (int i = from; i < _recorded.Count && !found; i++) found = _recorded[i].ord == p.Ordinal && _recorded[i].key == key;
            if (!found) return false;
        }
        return true;
    }

    /// <summary>Interlocked.Exchange and CompareExchange, Volatile.Write: the value goes into the place the `ref`
    /// names (its field, element, static), as a store there would.</summary>
    void RecordRefWrite(IMethodSymbol m, IOperation?[] byParam)
    {
        if (Sh == null || m.ContainingType is not { } ct || m.MethodKind == MethodKind.FunctionPointerSignature || Types.FullName(ct.OriginalDefinition) is not ("System.Threading.Interlocked" or "System.Threading.Volatile")) return;
        int loc = -1, val = -1;
        for (int i = 0; i < m.Parameters.Length; i++)
        {
            var p = m.Parameters[i];
            if (p.RefKind == RefKind.Ref && Types.IsRef(T.Canonical(p.Type)) && loc < 0) loc = i;
            else if (p.RefKind == RefKind.None && p.Name is "value" && Types.IsRef(T.Canonical(p.Type))) val = i;
        }
        if (loc < 0 || val < 0 || byParam[loc] is not { } place || byParam[val] is not { } value) return;
        AttachTo(place, T.Canonical(m.Parameters[loc].Type), FlowSrcs(value));
    }

    /// <summary>Interlocked's and Volatile's own stores through their `ref`: their callers say where (RecordRefWrite).</summary>
    bool InRefWriter => M.ContainingType is { } ct && Types.FullName(ct.OriginalDefinition) is "System.Threading.Interlocked" or "System.Threading.Volatile";

    /// <summary>The address an argument passes is one whose stores are recorded where they are made (RecordOutArg):
    /// the place does not become "anything of its type" for having its address taken.</summary>
    bool _preciseAddr;

    /// <summary>An `out` or `ref` argument: what the callee stores through it lands in the place passed.</summary>
    void RecordOutArg(IParameterSymbol p, IOperation place)
    {
        if (Sh == null || p.ContainingSymbol is not IMethodSymbol { ContainingType: not null } pm || pm.MethodKind == MethodKind.FunctionPointerSignature) return;
        var pt = T.Canonical(p.Type);
        if (!Types.IsRef(pt)) return;
        var callee = D.MethodTarget(pm);
        bool refWriter = Types.FullName(callee.ContainingType.OriginalDefinition) is "System.Threading.Interlocked" or "System.Threading.Volatile";
        // (a callee without a body of its own, or one the program does not compile: any object of the type)
        // (an intrinsic takes a ref to read or to move it: Unsafe.Add, As, AreSame; none stores a reference through one.
        // AsPointer makes a pointer of it: what is stored through a ref made from a pointer can land there)
        bool intrinsic = D.IsIntrinsic(callee);
        if (intrinsic && callee.Name == "AsPointer") AttachTo(place, pt, [new Sharing.Src(Sharing.K.Node, Sharing.PtrNode(pt))]);
        // (Unsafe.Copy<T>(ref T, void*): what a pointer of the type points at; (void*, ref T): what the place holds, there)
        if (intrinsic && callee.Name == "Copy" && Types.FullName(callee.ContainingType) == "System.Runtime.CompilerServices.Unsafe")
        {
            if (p.Ordinal == 0) AttachTo(place, pt, [new Sharing.Src(Sharing.K.Node, Sharing.PtrNode(pt))]);
            else Sh.Add(Sharing.PtrNode(pt), FlowSrcs(place));
        }
        bool opaque = !refWriter && !intrinsic && (callee.IsExtern || callee.IsAbstract || callee.IsVirtual || callee.IsOverride || callee.ContainingType.TypeKind == TypeKind.Interface);
        _preciseAddr = !opaque;
        // (Interlocked's and Volatile's stores: their value, recorded by RecordRefWrite)
        if (refWriter || intrinsic) return;
        List<Sharing.Src> srcs = opaque
            ? [TopAt(pt, place)]
            : [new Sharing.Src(Sharing.K.Node, Sharing.OutNode(Types.MethodKey(callee), p.Ordinal))];
        AttachTo(place, pt, srcs);
    }

    /// <summary>Where an address passed to this `ref`, `in` or `out` parameter goes: "" when nowhere a ref of it can
    /// be kept (Interlocked's and Volatile's, the comparisons of refs), the parameter ("method-key ordinal") of a body
    /// the compiler sees into, null when anywhere (a virtual or interface method, a delegate, a local function, an
    /// extern, an intrinsic that remakes refs).</summary>
    string? _addrUse;
    internal string? RefUseOf(IParameterSymbol p)
    {
        if (p.ContainingSymbol is not IMethodSymbol { ContainingType: not null } pm || pm.MethodKind is MethodKind.FunctionPointerSignature or MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.DelegateInvoke) return null;
        var callee = D.MethodTarget(pm);
        if (Types.FullName(callee.ContainingType.OriginalDefinition) is "System.Threading.Interlocked" or "System.Threading.Volatile") return "";
        if (D.IsIntrinsic(callee)) return callee.Name is "AreSame" or "IsAddressGreaterThan" or "IsAddressLessThan" or "ByteOffset" or "SkipInit" ? "" : null;
        if (callee.IsExtern || callee.IsAbstract || callee.IsVirtual || callee.IsOverride || callee.ContainingType.TypeKind == TypeKind.Interface) return null;
        return Types.MethodKey(callee) + " " + p.Ordinal;
    }

    /// <summary>A library method whose `ref T` parameters and result are all of its one type argument, and which only
    /// copies T's between such places or gives a ref of another: Unsafe.Add, Unsafe.Subtract, Buffer.Memmove.</summary>
    static bool SameTypeRefs(IMethodSymbol m) =>
        m.TypeArguments.Length == 1 && m.ContainingType is { ContainingType: null } ct
        && (m.Name is "Add" or "Subtract" && ct.Name == "Unsafe" && ct.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices"
            || m.Name == "Memmove" && ct.Name == "Buffer" && ct.ContainingNamespace?.ToDisplayString() == "System");

    /// <summary>Every reference field of the struct (and of the structs in it) may hold any object of its type.</summary>
    void StructLeavesAnything(ITypeSymbol st, int depth = 0)
    {
        if (Sh == null || depth > 8) return;
        var ct = T.Canonical(st);
        foreach (var f in T.LayoutOf(ct).Fields)
        {
            if (Types.IsRef(f.Type)) Sh.Add(Sharing.StructFieldNode(ct, f.Symbol), TopSrc(f.Type));
            else if (Types.IsStruct(f.Type) && T.LayoutOf(f.Type).HasRefs) StructLeavesAnything(f.Type, depth + 1);
        }
    }

    /// <summary>The body's `ref` parameters of reference type: whether it keeps or remakes a ref of one (a ref local,
    /// a ref return, a ref assignment, an address), and where it passes each (RefParamUse).</summary>
    void RecordRefParamUses(string key, IOperation body)
    {
        if (Sh == null) return;
        Dictionary<int, (bool dirty, HashSet<string> to)>? uses = null;
        foreach (var p in M.Parameters)
            if (p.RefKind != RefKind.None && Types.IsRef(T.Resolve(p.Type, Map))) (uses ??= [])[p.Ordinal] = (false, []);
        if (uses == null) return;
        foreach (var op in body.DescendantsAndSelf())
        {
            if (op is not IParameterReferenceOperation { Parameter: var ps } pr || ps.RefKind == RefKind.None
                || !SymbolEqualityComparer.Default.Equals(ps.ContainingSymbol?.OriginalDefinition, M.OriginalDefinition) || !uses.TryGetValue(ps.Ordinal, out var u)) continue;
            IOperation at = pr;
            while (at.Parent is IConversionOperation { Conversion.IsIdentity: true } or IParenthesizedOperation) at = at.Parent;
            switch (at.Parent)
            {
                case IArgumentOperation { Parameter: { RefKind: not RefKind.None } ap }:
                    {
                        // (the parameter as the callee sees it, in this body's terms)
                        string? to = at.Parent.Parent is IInvocationOperation inv && inv.IsVirtual && (inv.TargetMethod.IsVirtual || inv.TargetMethod.IsAbstract || inv.TargetMethod.IsOverride)
                            ? null : at.Parent.Parent is IInvocationOperation or IObjectCreationOperation ? RefUseOf(ap.ContainingSymbol is IMethodSymbol am ? SubstMethod(am).Parameters[ap.Ordinal] : ap) : null;
                        if (to == null) u.dirty = true; else if (to.Length > 0) u.to.Add(to);
                        break;
                    }
                case IArgumentOperation: break; // (by value: what it holds)
                case ISimpleAssignmentOperation { IsRef: true } ra when !ReferenceEquals(ra.Target, at): u.dirty = true; break;
                case IReturnOperation when M.ReturnsByRef || M.ReturnsByRefReadonly: u.dirty = true; break;
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.IsRef: true } }: u.dirty = true; break;
                case IConditionalOperation { IsRef: true }: u.dirty = true; break;
                case IAddressOfOperation: u.dirty = true; break;
                case ITupleOperation or IDeconstructionAssignmentOperation: u.dirty = true; break;
                case null: u.dirty = true; break;
                default:
                    if (at.Parent.Kind is OperationKind.None or OperationKind.Invalid) u.dirty = true;
                    break; // (a read of what it holds, or a store into it: recorded as the out node's)
            }
            uses[ps.Ordinal] = u;
        }
        foreach (var (ord, (d, to)) in uses)
        {
            var k = key + " " + ord;
            if (Sh.RefParamUse.TryGetValue(k, out var old)) { old.To.UnionWith(to); Sh.RefParamUse[k] = (old.Dirty || d, old.To); }
            else Sh.RefParamUse[k] = (d, to);
        }
    }

    /// <summary>Unsafe.Add, AddByteOffset, Subtract, SubtractByteOffset of a `ref T`, to a `ref T`: the ref argument
    /// (the result points into the same place: an element of the same array, span or inline array).</summary>
    static IOperation? SamePlaceRef(IInvocationOperation inv)
    {
        var m = inv.TargetMethod;
        if (!m.ReturnsByRef || inv.Arguments.Length == 0 || Types.FullName(m.ContainingType) != "System.Runtime.CompilerServices.Unsafe") return null;
        var a0 = inv.Arguments[0];
        if (a0.Parameter is not { RefKind: RefKind.Ref } p0) return null;
        // (Unsafe.As<TFrom, TTo>(ref TFrom), TFrom a reference: the same slot, seen as another type: what is stored
        // through it is there. Of a struct, a reference stored through it lands in one of the struct's fields)
        if (m.Name == "As" && inv.Arguments.Length == 1 && m.TypeArguments.Length == 2) return m.TypeArguments[0].IsReferenceType ? a0.Value : null;
        if (m.Name is not ("Add" or "AddByteOffset" or "Subtract" or "SubtractByteOffset") || inv.Arguments.Length != 2 || !SymbolEqualityComparer.Default.Equals(p0.Type, m.ReturnType)) return null;
        return a0.Value;
    }

    /// <summary>Values stored into the place an expression names (a field, an element, a static, a parameter's).</summary>
    void AttachTo(IOperation place, ITypeSymbol pt, List<Sharing.Src> srcs)
    {
        if (Sh == null) return;
        while (place is IConversionOperation { Conversion.IsIdentity: true } c) place = c.Operand;
        while (place is IInvocationOperation ui && SamePlaceRef(ui) is { } inner) place = inner;
        switch (place)
        {
            // (through a ref field: into the place it points at, a store through a ref)
            case IFieldReferenceOperation { Field.RefKind: not RefKind.None }:
                break;
            case IFieldReferenceOperation { Field.IsStatic: true } sf:
                Sh.Static(Driver.StaticKey(T.Canonical(Owner(sf.Field)), StorageField(sf.Field)), pt, IsThreadStatic(StorageField(sf.Field)), srcs);
                return;
            case IFieldReferenceOperation f when f.Instance != null && !TypeOf(f.Instance).IsValueType:
                if (D.Arenas.FieldSite(Owner(f.Field), T.FieldIn(Owner(f.Field), StorageField(f.Field))) is { } site) { site.Sources.AddRange(srcs); return; }
                break;
            case IFieldReferenceOperation f when f.Instance != null:
                Sh.Add(Sharing.StructFieldNode(T.Canonical(Owner(f.Field)), StorageField(f.Field)), srcs);
                return;
            case IArrayElementReferenceOperation ae when TypeOf(ae.ArrayReference) is IArrayTypeSymbol at && D.Arenas.ArraySite(at) is { } asite:
                asite.Sources.AddRange(srcs);
                return;
            case ILocalReferenceOperation { Local.IsRef: false }:
            case IDeclarationExpressionOperation:
                return; // (a local: it is then anything of its type, LocalSources)
            case IParameterReferenceOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } pp when SharingKey is { } k
                && SymbolEqualityComparer.Default.Equals(pp.Parameter.ContainingSymbol?.OriginalDefinition, M.OriginalDefinition):
                Sh.Add(Sharing.OutNode(k, pp.Parameter.Ordinal), srcs);
                return;
            case IParameterReferenceOperation { Parameter.RefKind: RefKind.None }:
                return;
        }
        Sh.RefStore(pt, srcs, place.Kind + " " + DebugText(place) + " in " + M.ToDisplayString());
    }

    bool _certifyCall, _certifyRefs;

    void MarkCertified()
    {
        long bits = (_certifyCall ? Sharing.Certified : 0) | (_certifyRefs ? Sharing.CertifiedRefs : 0);
        _certifyCall = _certifyRefs = false;
        if (bits == 0) return;
        var b = E.Fn.Body;
        if (b.Count > 0 && b[^1].Op == Op.Call) b[^1] = b[^1] with { Imm2 = b[^1].Imm2 | bits };
    }
}
