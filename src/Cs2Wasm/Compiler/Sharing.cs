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
internal sealed class Sharing
{
    public enum K : byte { Exact, Top, Param, Ret, Node, Elem, Display }
    /// <summary>A source of reference values. Exact: an object of Type (Index: a closure's or state machine's type id
    /// for Display). Top: any object of Type or a subtype. Param: method Key's parameter Index (declared Type). Ret:
    /// method Key's result (declared Type; Index 1: a virtual call, its implementations' too). Node: what the slot
    /// Key holds (declared Type). Elem: an element of an array of static type Type.</summary>
    public readonly record struct Src(K Kind, string? Key = null, int Index = 0, ITypeSymbol? Type = null);

    /// <summary>Marks a call instruction (Instr.Imm2) whose arguments' sources were all recorded.</summary>
    public const long Certified = 1L << 62;

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

    readonly List<Action> _reflectWrites = [];
    /// <summary>A store reflection can make (FieldInfo.SetValue): applied if the program can call SetValue.</summary>
    public void ReflectWrite(Action a) => _reflectWrites.Add(a);

    /// <summary>What a method stores through its `out` or `ref` parameter.</summary>
    public static string OutNode(string m, int i) => "o " + m + " " + i;
    /// <summary>A field of a struct, wherever the struct is (in a local, an object, an element, a box, a static).</summary>
    public static string StructFieldNode(ITypeSymbol structType, ISymbol field) => "sf " + Types.Key(structType) + "::" + field.Name;

    public void Add(string node, IEnumerable<Src> srcs)
    {
        if (!_nodes.TryGetValue(node, out var l)) _nodes[node] = l = [];
        foreach (var s in srcs) l.Add(s);
    }

    public void Add(string node, Src s)
    {
        if (!_nodes.TryGetValue(node, out var l)) _nodes[node] = l = [];
        l.Add(s);
    }

    /// <summary>A store into a static field.</summary>
    public void Static(string key, ITypeSymbol type, bool threadStatic, IEnumerable<Src> srcs)
    {
        if (threadStatic) _threadStatics.Add(key);
        if (Types.IsStruct(type) && D.Types.LayoutOf(type).HasRefs) _structStatics[key] = type;
        if (Types.IsRef(type)) Add(StaticNode(key), srcs);
    }

    /// <summary>A store through an address the code does not know the object of (a `ref`, a span's element).</summary>
    public void RefStore(ITypeSymbol slotType, IEnumerable<Src> srcs)
    {
        if (!Types.IsRef(slotType)) return;
        var ct = D.Types.Canonical(slotType);
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

    public bool MayBeShared(ITypeSymbol t)
    {
        if (s_none) return false;
        if (_shared == null) return true;
        if (t.TypeKind == TypeKind.Error || !Types.IsRef(t)) return false;
        var k = Types.Key(t);
        if (_mayBeShared.TryGetValue(k, out var r)) return r;
        r = false;
        foreach (var id in _shared) if (IdIs(id, t)) { r = true; break; }
        return _mayBeShared[k] = r;
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

    List<int> TopIds(ITypeSymbol t)
    {
        var k = Types.Key(t);
        if (_topIds.TryGetValue(k, out var r)) return r;
        _allIds ??= D.Constructed.Where(id => id > 1 && id < D.TypeById.Count && (D.DisplayById.ContainsKey(id) || !D.SyntheticById.ContainsKey(id) || D.TypeById[id] is INamedTypeSymbol { IsAnonymousType: true })).Order().ToList();
        r = [];
        if (Types.IsRef(t)) foreach (var id in _allIds) if (!Types.IsImmortalType(D.TypeById[id]) || D.DisplayById.ContainsKey(id)) if (IdIs(id, t)) r.Add(id);
        return _topIds[k] = r;
    }

    static readonly bool s_debug = Environment.GetEnvironmentVariable("CS2WASM_SHARING_DEBUG") == "1";
    public static bool Debug => s_debug;
    static readonly bool s_off = Environment.GetEnvironmentVariable("CS2WASM_NO_SHARING") == "1";

    /// <summary>The types another thread can reach. `sites`: the slots of the type graph (Arenas: fields by declaring
    /// type and offset, elements by array type, closures' slots by type id) with the sources stored there.</summary>
    public void Analyze(IReadOnlyCollection<Arenas.Site> sites, Func<WasmFunction, string?> methodKeyOf)
    {
        if (s_off) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // (reflection stores into fields only through FieldInfo.SetValue)
        if (D.AllMethods.Any(m => m.Compiled && m.Sym.Name is "SetValue" or "SetValueDirect" && m.Sym.ContainingType?.Name is "RuntimeFieldInfo" or "FieldInfo"))
            foreach (var a in _reflectWrites) a();
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
        var seenNodes = new HashSet<string>();
        var seenTops = new HashSet<string>();
        // (filter: what the slot can hold whatever is stored through a wider type, an array's element type)
        var work = new Stack<(Src src, string why, ITypeSymbol? filter)>();
        var whyType = s_debug ? new Dictionary<int, string>() : null;

        ITypeSymbol? filter = null;
        string FK(string k) => filter == null ? k : k + " |" + Types.Key(filter);
        void Push(Src s, string why) => work.Push((s, why, filter));
        void Node(string key, ITypeSymbol? declared, string why)
        {
            if (!seenNodes.Add(FK(key))) return;
            if (_nodes.TryGetValue(key, out var l) && l.Count > 0) { foreach (var s in l) Push(s, why); }
            else if (declared != null) Push(new Src(K.Top, Type: declared), why + " (nothing recorded)");
        }
        void SiteNode(Arenas.Site s, ITypeSymbol? declared, string why)
        {
            if (!seenNodes.Add(FK("site " + s.Key))) return;
            if (s.Sources.Count > 0) foreach (var x in s.Sources) Push(x, why);
            foreach (var id in s.TargetIds) Push(new Src(K.Display, Index: id), why);
            if (s.Sources.Count == 0 && s.TargetIds.Count == 0 && declared != null) Push(new Src(K.Top, Type: declared), why + " (nothing recorded)");
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
        foreach (var k in _nodes.Keys.Where(k => k.StartsWith("s ")))
            if (!_threadStatics.Contains(k[2..])) Node(k, null, "static " + k[2..]);
        foreach (var (k, t) in _structStatics)
            if (!_threadStatics.Contains(k))
                StructFields(t, "static " + k);
        var threadType = D.LibType("System.Threading.Thread");
        Push(new Src(K.Top, Type: threadType), "a thread start");

        int steps = 0;
        while (work.Count > 0)
        {
            var (s, why, f) = work.Pop();
            filter = f;
            steps++;
            switch (s.Kind)
            {
                case K.Exact:
                    if (s.Type != null && D.ExistingTypeId(D.Types.Canonical(s.Type)) is int eid and > 0 && !Types.IsImmortalType(s.Type)) AddType(eid, why);
                    else if (s.Type != null && Types.IsRef(s.Type)) Push(new Src(K.Top, Type: s.Type), why);
                    break;
                case K.Display:
                    AddType(s.Index, why);
                    break;
                case K.Top:
                    if (s_debug && s.Type != null && (Types.IsObject(s.Type) || s.Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Object } || Types.FullName(s.Type) == "System.Array") && (filter == null || Types.IsObject(filter) || filter is IArrayTypeSymbol || Types.FullName(filter) == "System.Array"))
                        Console.Error.WriteLine("broad " + Types.Key(s.Type) + " [" + (s.Key ?? "?") + "] via " + (why.Length > 300 ? why[..300] : why));
                    if (s.Type == null || !seenTops.Add(FK(Types.Key(s.Type)))) break;
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
        _shared = shared;
        D.Mark($"    sharing: {shared.Count} types another thread can reach, {steps} steps, {sw.ElapsedMilliseconds} ms");
        if (whyType != null)
        {
            Console.Error.WriteLine($"sharing: {shared.Count} of {TopIds(D.ObjectType).Count} types reachable by another thread; {uncertified.Count} methods called from elsewhere");
            foreach (var id in shared.Order())
                Console.Error.WriteLine("  " + (D.DisplayById.ContainsKey(id) ? "display " + id : Types.Key(D.TypeById[id])) + "   <- " + whyType[id]);
        }
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
        void Node(string key, ITypeSymbol? declared)
        {
            if (!seen.Add(key)) return;
            if (_nodes.TryGetValue(key, out var l) && l.Count > 0) foreach (var x in l) work.Push(x);
            else if (key.StartsWith("site ") && D.Arenas.SiteByKey(key[5..]) is { } site && site.Sources.Count > 0) foreach (var x in site.Sources) work.Push(x);
            else Top(declared);
        }
        Node(start, null);
        while (work.Count > 0)
        {
            var s = work.Pop();
            tag = s.Key;
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
            }
        }
        return _arrayIds[start] = r;
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

    Dictionary<ISymbol, List<IOperation>?>? _localSources;
    /// <summary>Locals passed as `out` or `ref` to a method with a body: what it stores through the parameter.</summary>
    Dictionary<ISymbol, List<Sharing.Src>>? _localOutSources;

    /// <summary>The values a local is ever assigned in this body; null when that is not all it can hold (captured,
    /// passed by reference, a ref local, a foreach or out or pattern variable).</summary>
    List<IOperation>? LocalSources(ILocalSymbol l)
    {
        if (_sharingBody == null || l.IsRef || IsCaptured(l)) return null;
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
        bool opaque = !refWriter && !intrinsic && (callee.IsExtern || callee.IsAbstract || callee.IsVirtual || callee.IsOverride || callee.ContainingType.TypeKind == TypeKind.Interface);
        _preciseAddr = !opaque;
        // (Interlocked's and Volatile's stores: their value, recorded by RecordRefWrite)
        if (refWriter || intrinsic) return;
        List<Sharing.Src> srcs = opaque
            ? [TopAt(pt, place)]
            : [new Sharing.Src(Sharing.K.Node, Sharing.OutNode(Types.MethodKey(callee), p.Ordinal))];
        AttachTo(place, pt, srcs);
    }

    /// <summary>Values stored into the place an expression names (a field, an element, a static, a parameter's).</summary>
    void AttachTo(IOperation place, ITypeSymbol pt, List<Sharing.Src> srcs)
    {
        if (Sh == null) return;
        while (place is IConversionOperation { Conversion.IsIdentity: true } c) place = c.Operand;
        switch (place)
        {
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
        Sh.RefStore(pt, srcs);
    }

    bool _certifyCall;

    void MarkCertified()
    {
        if (!_certifyCall) return;
        _certifyCall = false;
        var b = E.Fn.Body;
        if (b.Count > 0 && b[^1].Op == Op.Call) b[^1] = b[^1] with { Imm2 = b[^1].Imm2 | Sharing.Certified };
    }
}
