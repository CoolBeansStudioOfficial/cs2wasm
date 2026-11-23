using Microsoft.CodeAnalysis;

namespace Il2Wasm.Compiler;

/// <summary>
/// What each arena site's slots can hold by where its values come from (<see cref="SiteTypeIds"/>), for every site at
/// once: the sources form a graph (a site, a parameter, a result, a struct's field, a ref's place, each leading to
/// the sources recorded for it), and each node's answer is the union of its own leaves (exact types, any object of a
/// type) and its successors' answers. Computed once per strongly connected component of that graph (Tarjan, built as
/// it is walked), so a node many sites reach is walked once, not once per site: Roslyn's sites reach the same few
/// thousand parameters and results.
/// The rules are those of the walk in <see cref="SiteTypeIdsWalk"/> (kept for IL2WASM_FLOW_DEBUG, which says which
/// source made a site wide); an answer is "wide" (null) when it holds any object, or more than the cap of types.
/// </summary>
internal sealed partial class Sharing
{
    sealed class FlowNode
    {
        public int Index = -1, Low;
        public bool OnStack;
        public List<int>? Succ;
        public HashSet<int>? Leaves;
        public bool Wide;
        /// <summary>The component's answer, once it is complete (null with Done: wide).</summary>
        public HashSet<int>? Result;
        public bool Done;
        /// <summary>IL2WASM_FLOW_CAUSES: the source that made it wide.</summary>
        public string? Cause;
    }

    /// <summary>IL2WASM_FLOW_CAUSES=1: for each site the narrowing is asked about and that stays wide, one source that
    /// makes it so and the path to it ("wide-cause" lines; cheaper than IL2WASM_FLOW_DEBUG's walk per site).</summary>
    static readonly bool s_flowCauses = Environment.GetEnvironmentVariable("IL2WASM_FLOW_CAUSES") == "1";

    readonly Dictionary<string, int> _flowIds = [];
    readonly List<FlowNode> _flowNodes = [];
    int _flowCap;
    const string Sep = " §";

    int FlowId(string key)
    {
        if (_flowIds.TryGetValue(key, out var id)) return id;
        id = _flowNodes.Count;
        _flowNodes.Add(new FlowNode());
        _flowIds[key] = id;
        _flowKeys.Add(key);
        return id;
    }
    readonly List<string> _flowKeys = [];

    /// <summary>A node's successors and leaves, by the rules of the walk.</summary>
    void FlowExpand(int id)
    {
        var n = _flowNodes[id];
        if (n.Succ != null) return;
        var succ = new List<int>();
        var leaves = new HashSet<int>();
        bool wide = false;
        var key = _flowKeys[id];
        Src cur = default;
        void Top(ITypeSymbol? t)
        {
            if (t == null || wide) return;
            if (s_flowOptimistic && (Types.IsObject(t) || t.TypeKind == TypeKind.Interface || TopIds(t).Count > 50)) return;
            if (Types.IsObject(t)) { wide = true; if (s_flowCauses) n.Cause = $"{cur.Kind} {cur.Key} #{cur.Index} {(cur.Type != null ? Types.Key(cur.Type) : "-")} in {key}"; return; }
            foreach (var x in TopIds(t)) leaves.Add(x);
            if (leaves.Count > _flowCap) { wide = true; if (s_flowCauses) n.Cause = "over the cap: " + Types.Key(t); }
        }
        void To(string k) => succ.Add(FlowId(k));
        // "n <key>|<declared>": a node of _nodes (Param, Ret, Out, RefIn, statics, struct fields, arrays...), with
        // any object of the declared type when nothing is recorded for it
        // "site <key>|<declared>": an arena site's recorded sources
        // "refs <type>": the stores through a ref of the type (and the reinterpreted refs of it)
        // "start <site>": a site with the refs of its slot's type and the wider array types' sites
        int bar = key.IndexOf(Sep, StringComparison.Ordinal);
        if (key.StartsWith("start "))
        {
            var site = D.Arenas.SiteByKey(key[6..])!;
            var slotType = D.Types.Canonical(site.DeclaredType!);
            To("site " + site.Key + Sep + Types.Key(slotType));
            _flowTypes.TryAdd(Types.Key(slotType), slotType);
            if (site.IsArray)
                foreach (var other in D.Arenas.AllSites)
                    if (other.IsArray && other != site && D.Types.IsSubtype(site.Container, other.Container)) To("site " + other.Key + Sep);
        }
        else if (key.StartsWith("refs "))
        {
            var k = RefNode(_flowTypes[key[5..]]);
            if (_nodes.ContainsKey(k)) To("n " + k + Sep);
            if (_refAliases.TryGetValue(k, out var al)) foreach (var a in al) if (_nodes.ContainsKey(a)) To("n " + a + Sep);
        }
        else if (key.StartsWith("site "))
        {
            var sk = key[5..bar];
            var declared = bar + Sep.Length < key.Length ? _flowTypes[key[(bar + Sep.Length)..]] : null;
            if (D.Arenas.SiteByKey(sk) is { } s)
            {
                foreach (var x in s.Sources) Src(x);
                foreach (var tid in s.TargetIds) leaves.Add(tid);
                if (s.Sources.Count == 0 && s.TargetIds.Count == 0) Top(declared);
                // (a slot whose address is taken: also what is stored through a ref of its type. Only such a slot can
                // be stored through a ref: every ref to a field or an element is made by EmitAddress, which marks it)
                if (s.DeclaredType != null && RefsReach(s)) To("refs " + TypeKey(s.DeclaredType));
            }
            else Top(declared);
        }
        else if (key.StartsWith("n "))
        {
            var nk = key[2..bar];
            var declared = bar + Sep.Length < key.Length ? _flowTypes[key[(bar + Sep.Length)..]] : null;
            bool any = false;
            // (recorded, even with no reference: only nulls were; not recorded at all: any object of its type)
            if (_nodes.TryGetValue(nk, out var l)) { foreach (var x in l) Src(x); any = true; }
            if (_flowStubParams!.TryGetValue(nk, out var sp)) { foreach (var x in sp) Src(x); any = true; }
            if (!any) Top(declared);
        }
        void Src(Src s)
        {
            if (wide) return;
            cur = s;
            switch (s.Kind)
            {
                case K.Exact:
                    if (s.Type != null && D.ExistingTypeId(D.Types.Canonical(s.Type)) is int eid and > 0 && !Types.IsImmortalType(s.Type)) leaves.Add(eid);
                    else if (s.Type != null && Types.IsRef(s.Type)) Top(s.Type);
                    break;
                case K.Display: leaves.Add(s.Index); break;
                case K.Top: Top(s.Type); break;
                case K.Param:
                    if (_flowUncertified!.Contains(s.Key!)) Top(s.Type); else To("n " + ParamNode(s.Key!, s.Index) + Sep + TypeKey(s.Type));
                    break;
                case K.Ret:
                    {
                        bool dispatched = s.Index == 1 && _flowImpls!.TryGetValue(s.Key!, out var impls) && impls.Count > 0;
                        if (BodyRets.Contains(s.Key!)) To("n " + RetNode(s.Key!) + Sep + TypeKey(s.Type));
                        else if (!dispatched) Top(s.Type);
                        if (dispatched)
                            foreach (var k in _flowImpls![s.Key!])
                                if (BodyRets.Contains(k)) To("n " + RetNode(k) + Sep + TypeKey(s.Type)); else Top(s.Type);
                        break;
                    }
                case K.Node:
                    if (s.Key!.StartsWith("site ")) To("site " + s.Key[5..] + Sep + TypeKey(s.Type));
                    else { To("n " + s.Key + Sep + TypeKey(s.Type)); if (s.Type != null && NodeRefsReach(s.Key)) To("refs " + TypeKey(s.Type)); }
                    break;
                case K.Elem:
                    Top(s.Type is IArrayTypeSymbol et ? D.Types.Canonical(et.ElementType) : null);
                    break;
                case K.RefParam:
                    if (_flowUncertifiedRefs!.Contains(s.Key!)) Top(s.Type);
                    else
                    {
                        To("n " + RefInNode(s.Key!, s.Index) + Sep);
                        To("n " + OutNode(s.Key!, s.Index) + Sep);
                        // (and through a ref the body keeps of it, when it keeps one)
                        if (s.Type != null && NodeRefsReach(OutNode(s.Key!, s.Index))) To("refs " + TypeKey(s.Type));
                    }
                    break;
            }
        }
        n.Succ = wide ? [] : succ;
        n.Leaves = wide ? null : leaves;
        n.Wide = wide;
    }

    readonly Dictionary<string, ITypeSymbol> _flowTypes = [];
    string TypeKey(ITypeSymbol? t)
    {
        if (t == null) return "";
        var c = D.Types.Canonical(t);
        var k = Types.Key(c);
        _flowTypes.TryAdd(k, c);
        return k;
    }

    int _flowIndex;

    /// <summary>The answer of a node: its component's, computed with every component it reaches (iterative Tarjan).</summary>
    HashSet<int>? FlowResult(int root)
    {
        if (_flowNodes[root].Done) return _flowNodes[root].Result;
        var stack = new Stack<int>();
        var work = new Stack<(int v, int i)>();
        void Open(int v)
        {
            var n = _flowNodes[v];
            n.Index = n.Low = _flowIndex++;
            n.OnStack = true;
            stack.Push(v);
            FlowExpand(v);
            work.Push((v, 0));
        }
        Open(root);
        while (work.Count > 0)
        {
            var (v, i) = work.Pop();
            var nv = _flowNodes[v];
            if (i < nv.Succ!.Count)
            {
                work.Push((v, i + 1));
                int w = nv.Succ[i];
                var nw = _flowNodes[w];
                if (nw.Done) continue;
                if (nw.Index < 0) Open(w);
                else if (nw.OnStack) nv.Low = Math.Min(nv.Low, nw.Index);
                continue;
            }
            if (work.Count > 0) { var p = _flowNodes[work.Peek().v]; p.Low = Math.Min(p.Low, nv.Low); }
            if (nv.Low != nv.Index) continue;
            // a component: its members' leaves and the answers of the components it leads to
            var members = new List<int>();
            int x;
            do { x = stack.Pop(); _flowNodes[x].OnStack = false; members.Add(x); } while (x != v);
            bool wide = false;
            HashSet<int>? only = null; // (the one answer it would share, when it adds nothing of its own)
            bool own = false;
            var mset = members.Count > 1 ? new HashSet<int>(members) : null;
            foreach (var m in members)
            {
                var nm = _flowNodes[m];
                if (nm.Wide) { wide = true; break; }
                if (nm.Leaves!.Count > 0) own = true;
                foreach (var s in nm.Succ!)
                {
                    if (s == m || mset != null && mset.Contains(s)) continue;
                    var ns = _flowNodes[s];
                    if (ns.Result == null) { wide = true; break; }
                    if (only == null) only = ns.Result;
                    else if (!ReferenceEquals(only, ns.Result)) own = true;
                }
                if (wide) break;
            }
            HashSet<int>? result = null;
            if (!wide)
            {
                if (!own) result = only ?? s_empty;
                else
                {
                    result = [];
                    foreach (var m in members)
                    {
                        var nm = _flowNodes[m];
                        result.UnionWith(nm.Leaves!);
                        foreach (var s in nm.Succ!) if (_flowNodes[s].Done) result.UnionWith(_flowNodes[s].Result!);
                        if (result.Count > _flowCap) { result = null; break; }
                    }
                }
            }
            foreach (var m in members)
            {
                var nm = _flowNodes[m];
                nm.Result = result; nm.Done = true;
                if (!s_flowCauses) { nm.Leaves = null; nm.Succ = null; } // (no longer needed: a completed component is only asked for its answer)
            }
        }
        return _flowNodes[root].Result;
    }
    static readonly HashSet<int> s_empty = [];

    /// <summary>The nearest node that is wide of itself, from a wide one, and the path to it.</summary>
    string WideCause(int from)
    {
        var prev = new Dictionary<int, int> { [from] = -1 };
        var q = new Queue<int>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            int v = q.Dequeue();
            var n = _flowNodes[v];
            if (n.Wide || n.Cause != null)
            {
                var path = new List<string>();
                for (int x = prev[v]; x >= 0 && path.Count < 30; x = prev[x]) path.Add(_flowKeys[x]);
                path.Reverse();
                return (n.Cause ?? "?") + "  at " + _flowKeys[v] + "  via " + string.Join(" -> ", path.Skip(1));
            }
            foreach (var s in n.Succ ?? []) if (_flowNodes[s].Result == null && prev.TryAdd(s, v)) q.Enqueue(s);
        }
        return "over the cap (a union)";
    }

    /// <summary>The type ids a site's slots can hold, from the sources of what is stored there, followed through
    /// parameters, results and the slots values are read from; null when that is no narrower than the static types
    /// (any object, or more than `cap` types). A parameter of a method called from elsewhere, a result without a body,
    /// a slot with nothing recorded is any object of its declared type; stores through a ref of the slot's type count;
    /// an element read is any object of the element type.</summary>
    public HashSet<int>? SiteTypeIds(Arenas.Site site, int cap)
    {
        if (site.DeclaredType == null) return null;
        PrepareFlow();
        var walked = s_flowDebug ? SiteTypeIdsWalk(site, cap) : null;
        if (_flowCap == 0) _flowCap = cap;
        else if (_flowCap != cap) throw new InvalidOperationException("internal: one cap for the sites' answers");
        var r = FlowResult(FlowId("start " + site.Key));
        var slotType = D.Types.Canonical(site.DeclaredType);
        // (a slot holds objects of its declared type only, whatever was stored through a wider one)
        var answer = r?.Where(id => IdIs(id, slotType)).ToHashSet();
        if (s_flowCauses && answer == null) Console.Error.WriteLine($"wide-cause {site.Key}: {WideCause(FlowId("start " + site.Key))}");
        if (s_flowDebug && (answer == null) != (walked == null) || answer != null && walked != null && !answer.SetEquals(walked))
            Console.Error.WriteLine($"flow-mismatch {site.Key}: {answer?.Count.ToString() ?? "wide"} against the walk's {walked?.Count.ToString() ?? "wide"}");
        return answer;
    }
}
