#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Il2Wasm.IL.Ir;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Memory;

/// <summary>A body's borrow decisions (4.6): each counted value's ownership fact (Guaranteed with its path, or Owned
/// after a flip), and each speculative borrow's counter-evidence.</summary>
public sealed class BorrowFacts
{
    public readonly NodeTable<OwnFact> Own = new(OwnFact.Owned);
    public readonly NodeTable<BorrowEvidence> Evidence = new(BorrowEvidence.None);
    /// <summary>Speculative borrows that flipped to Owned (a Retain at the definition), for RcStats' flip categories.</summary>
    public readonly NodeTable<bool> Flipped = new(false);
}

/// <summary>Speculative borrows, path keys, the two proofs and flip decisions (docs/MEMORY-IR.md 2.2, 4.6). S1 row b.
/// <para>Every counted reference load starts as a borrow of its source path: <c>LdFld</c> and <c>LdElem</c> (the old
/// front end's FieldBorrows, with its element and nested-field forms), <c>LdSFld</c> (FieldBorrows' static form and
/// StaticLoads' conditions), <c>VarLd</c> of a frame variable whose address is never taken, casts of a borrow
/// (forwarding), and loop phis whose inputs are one path or values read out of the phi itself (LocalBorrows:
/// <c>for (n = head; n != null; n = n.Next)</c>). Values that come through one another are one <b>group</b>: a phi, its
/// external input, and the self-derived chain that feeds it back; a group is decided as a whole, over the union of its
/// members' live ranges, with the union of their keys. A value read out of a group is a child group, decided after
/// its parent; if the parent flipped, the child is re-rooted at the parent's (now Owned) value.</para>
/// <para>A group stays Guaranteed when both proofs hold over its interval (Ownership.Liveness, exceptional edges
/// included, def excluded, last use included):</para>
/// <list type="number">
/// <item>Proof 1: no store in the body to a key that may alias a path key (type-based: fields by typical definition,
/// element keys by castable element types, a <c>ref T</c> slot by castable T, raw pointers are wild), except a store of
/// the group's own value (a self-store: lowering retains the new value before it releases the old one); no call whose
/// Mods ∪ ReleaseEffects, plus the static constructors it may start, hit a key; no release (explicit, or implied at a
/// last use or a store's old value) whose destroy effects hit a key; no static constructor started by the body's own
/// node; FilterMods when the group is live into a handler; the root alive (Arg: the caller's convention; Val: the
/// value live over the whole interval; Var: never written in it, never address-taken, never written by a filter;
/// Static: its key is a path key). Init-only keys (InitOnlyFields' rule) need no write check.</item>
/// <item>Proof 2: no other thread (S1: the program reaches no Rt.SpawnCore), or every step key init-only.</item>
/// </list>
/// <para>Otherwise every member flips to Owned (one Retain at its definition). A path is at most three steps; anything
/// else is generic and Owned (Lobster's generic borrow is unsound). A Guaranteed value's interval includes every call
/// it is an argument of, so a Guaranteed argument never needs a KeepAlive.</para>
/// <para>Switches: IL2WASM_NO_SPEC_BORROWS (all), IL2WASM_NO_FIELD_BORROWS, IL2WASM_NO_ELEM_BORROWS,
/// IL2WASM_NO_STATIC_BORROWS, IL2WASM_NO_VAR_BORROWS, IL2WASM_NO_LOCAL_BORROWS (phi groups), IL2WASM_NO_INIT_ONLY,
/// IL2WASM_NO_SELF_STORES; the break twins IL2WASM_SPEC_BORROWS_BREAK (proof 1 ignores the body's own stores),
/// IL2WASM_INIT_ONLY_BREAK (every field init-only), IL2WASM_SELF_STORES_BREAK (every store a self-store).
/// IL2WASM_BORROW_STATS=1: counts per program on stderr at exit; IL2WASM_BORROW_TRACE=1: each flip.</para></summary>
public static class Borrows
{
    static bool Env(string n) => Environment.GetEnvironmentVariable(n) == "1";
    internal static readonly bool NoSpec = Env("IL2WASM_NO_SPEC_BORROWS");
    internal static readonly bool NoField = Env("IL2WASM_NO_FIELD_BORROWS");
    internal static readonly bool NoElem = Env("IL2WASM_NO_ELEM_BORROWS");
    internal static readonly bool NoStatic = Env("IL2WASM_NO_STATIC_BORROWS");
    internal static readonly bool NoVar = Env("IL2WASM_NO_VAR_BORROWS");
    internal static readonly bool NoLocal = Env("IL2WASM_NO_LOCAL_BORROWS");
    internal static readonly bool NoInitOnly = Env("IL2WASM_NO_INIT_ONLY");
    internal static readonly bool NoSelfStores = Env("IL2WASM_NO_SELF_STORES");
    internal static readonly bool NoReturnMods = Env("IL2WASM_NO_RETURN_MODS");
    internal static readonly bool BreakSpec = Env("IL2WASM_SPEC_BORROWS_BREAK");
    internal static readonly bool BreakInitOnly = Env("IL2WASM_INIT_ONLY_BREAK");
    internal static readonly bool BreakSelfStores = Env("IL2WASM_SELF_STORES_BREAK");
    /// <summary>IL2WASM_BORROW_STATS=1: the program's counts (loads, borrowed, flips by reason) on stderr at exit.</summary>
    public static readonly bool Stats = Env("IL2WASM_BORROW_STATS");
    internal static readonly bool Trace = Env("IL2WASM_BORROW_TRACE");

    /// <summary>The borrow facts of a body (see the class). <paramref name="liveness"/> is Ownership's liveness of
    /// this body (after its edge split); the body may already hold the naive plan's Retain and Release nodes (they are
    /// then the releases looked at) or none (the releases are those implied by last uses and stores). The body is not
    /// changed.</summary>
    public static BorrowFacts Analyze(PlanContext ctx, Ownership.Info liveness)
    {
        // (the counts are the last round's: each round has a solution of its own)
        if (Stats && !ReferenceEquals(Interlocked.Exchange(ref s_statsSolution, ctx.Solution), ctx.Solution)) Array.Clear(Counts);
        var a = new Analyzer(ctx, liveness, decide: true);
        a.Run();
        s_last.AddOrUpdate(ctx.Body, a);
        return a.Facts;
    }

    static readonly ConditionalWeakTable<Body, Analyzer> s_last = new();
    static ProgramSolution? s_statsSolution;

    /// <summary>A value's borrow path (2.2): the root and up to three field or element steps, or null (generic). For a
    /// loop phi's group it is the external input's path (the group's representative: its evidence holds the keys of
    /// the loop's steps too). From the last Analyze of the body, else computed from the body alone.</summary>
    public static BorrowPath? PathOf(PlanContext ctx, Node value)
    {
        if (!s_last.TryGetValue(ctx.Body, out var a))
        {
            a = new Analyzer(ctx, null, decide: false);
            a.Run();
            s_last.AddOrUpdate(ctx.Body, a);
        }
        return a.PathOf(value);
    }

    // ------------------------------------------------------------------ statistics

    /// <summary>IL2WASM_BORROW_SITES=file (with IL2WASM_BORROW_STATS=1): each counted load's decision, per function in
    /// node order (the last round's), written at exit.</summary>
    internal static readonly string? SitesFile = Stats && Environment.GetEnvironmentVariable("IL2WASM_BORROW_SITES") is { Length: > 0 } sf ? sf : null;
    static readonly List<string> s_sites = [];
    static ProgramSolution? s_sitesSolution;
    static int s_sitesHooked;
    internal static void AddSites(ProgramSolution sol, List<string> lines)
    {
        lock (s_sites)
        {
            if (!ReferenceEquals(s_sitesSolution, sol)) { s_sites.Clear(); s_sitesSolution = sol; }
            s_sites.AddRange(lines);
        }
        if (Interlocked.Exchange(ref s_sitesHooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (s_sites) System.IO.File.WriteAllLines(SitesFile!, s_sites); };
    }

    internal static readonly long[] Counts = new long[(int)Stat.Count];
    static int s_hooked;
    internal static void Count(Stat s, long n = 1)
    {
        if (!Stats) return;
        if (Interlocked.Exchange(ref s_hooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Error.WriteLine(Report());
        Interlocked.Add(ref Counts[(int)s], n);
    }

    /// <summary>The counts so far, one line per kind (IL2WASM_BORROW_STATS).</summary>
    public static string Report()
    {
        var c = Counts;
        long loads = c[(int)Stat.Loads], kept = c[(int)Stat.Borrowed];
        var sb = new System.Text.StringBuilder();
        sb.Append($"borrows: {loads} counted loads (field {c[(int)Stat.LoadField]}, elem {c[(int)Stat.LoadElem]}, static {c[(int)Stat.LoadStatic]}, var {c[(int)Stat.LoadVar]}), ");
        sb.Append($"{kept} borrowed ({(loads == 0 ? 0 : 100.0 * kept / loads):F1}%), {c[(int)Stat.Generic]} generic (no path or past 3 steps), {c[(int)Stat.Flipped]} flipped; ");
        sb.Append($"phis borrowed {c[(int)Stat.PhiBorrowed]}, casts {c[(int)Stat.CastBorrowed]}; init-only key uses {c[(int)Stat.InitOnlyUse]}; self-stores {c[(int)Stat.SelfStore]}; methods {c[(int)Stat.Methods]}\n");
        sb.Append("borrows: flips by first reason:");
        for (var s = Stat.FlipCallWild; s < Stat.Count; s++) if (c[(int)s] != 0) sb.Append(' ').Append(s.ToString()[4..]).Append('=').Append(c[(int)s]);
        return sb.ToString();
    }

    internal enum Stat
    {
        Methods, Loads, LoadField, LoadElem, LoadStatic, LoadVar, Borrowed, Generic, Flipped, PhiBorrowed, CastBorrowed, InitOnlyUse, SelfStore,
        // flip reasons (first found)
        FlipCallWild, FlipCallKey, FlipIntrinsic, FlipStore, FlipWildStore, FlipVarWrite, FlipRelease, FlipCctor, FlipFilter, FlipRootDies, FlipThread, FlipParent,
        Count,
    }
}

/// <summary>What a value is for path purposes.</summary>
enum PK : byte { None, Trivial, Root, Member, Self }

sealed class PInfo
{
    public PK Kind;
    /// <summary>Root: the root path (no steps); Member: the group's path.</summary>
    public BorrowPath? Path;
    public Group? G;
    /// <summary>Self: the in-progress phi it was read out of, the keys of the steps from it, and the chain's nodes.</summary>
    public Node? Phi;
    public List<MemKey>? SelfKeys;
    public List<Node>? Chain;
    public static readonly PInfo None = new() { Kind = PK.None };
    public static readonly PInfo Trivial = new() { Kind = PK.Trivial };
}

/// <summary>Values decided together (4.6): one borrow, its forwards, a loop phi with its input and the chain that
/// feeds it back.</summary>
sealed class Group
{
    public Group? Merged;
    public Group Find() { var g = this; while (g.Merged != null) g = g.Merged; return g; }
    public readonly List<Node> Members = [];
    public BorrowPath Path = null!;
    /// <summary>Every key a write to which breaks the group (the path's steps, the static root, the loop's steps).</summary>
    public readonly List<MemKey> Keys = [];
    public readonly List<Group> Parents = [];
    /// <summary>The group's single origin load (LdFld/LdElem) and its base, for re-rooting when the parent flips.</summary>
    public Node? Origin;
    public bool Rerootable = true;
    /// <summary>Val root: the node; Arg root: the Arg node.</summary>
    public Node? RootNode;
    public bool Loop;
    // decision
    public bool Decided, Holds;
    public Borrows.Stat Reason;
    public BorrowEvidence Evidence;
    public int Order;
}

sealed class Analyzer
{
    readonly PlanContext _ctx;
    readonly Body _body;
    readonly Ownership.Info? _info;
    readonly bool _decide;
    readonly ProgramBorrows _pb;
    readonly Dictionary<Node, PInfo> _memo = new();
    readonly HashSet<Node> _inProgress = [];
    readonly List<Group> _groups = [];
    public readonly BorrowFacts Facts = new();
    readonly HashSet<int> _addressTakenVars = [], _filterWrittenVars = [];
    readonly HashSet<MethodDesc> _intrinsics;
    string? _cause;
    OwnershipSignature? _sig;
    int _order;

    public Analyzer(PlanContext ctx, Ownership.Info? info, bool decide)
    {
        _ctx = ctx; _body = ctx.Body; _info = info; _decide = decide && info != null;
        _pb = ProgramBorrows.Of(ctx.Solution);
        _intrinsics = ctx.Summary is { } s ? [.. s.Intrinsics] : [];
    }

    public BorrowPath? PathOf(Node n)
    {
        var i = Info(n);
        return i.Kind switch { PK.Member => i.G!.Find().Path, PK.Root => i.Path, _ => null };
    }

    IEnumerable<Node> AllNodes() => _body.Blocks.SelectMany(b => b.Phis.Concat(b.Code));

    public void Run()
    {
        foreach (var n in AllNodes())
        {
            if (n.Op == Opc.LocA) _addressTakenVars.Add((int)n.I);
        }
        foreach (var r in _body.Regions)
            if (r.Kind == RegionKind.Filter)
                foreach (var b in Ownership.BlocksWithin(r)) foreach (var n in b.Code) if (n.Op == Opc.VarSt) _filterWrittenVars.Add((int)n.I);
        foreach (var n in AllNodes()) if (n.Kind == VK.Ref) Info(n);
        if (!_decide) return;
        Borrows.Count(Borrows.Stat.Methods);
        foreach (var n in AllNodes())
        {
            if (n.Op is Opc.LdNull or Opc.LdStr) Facts.Own[n] = OwnFact.Trivial;
            if (n.Kind != VK.Ref) continue;
            var st = n.Op switch { Opc.LdFld => Borrows.Stat.LoadField, Opc.LdElem => Borrows.Stat.LoadElem, Opc.LdSFld => Borrows.Stat.LoadStatic, Opc.VarLd => Borrows.Stat.LoadVar, _ => Borrows.Stat.Count };
            if (st == Borrows.Stat.Count) continue;
            Borrows.Count(Borrows.Stat.Loads); Borrows.Count(st);
            if (_memo.GetValueOrDefault(n)?.Kind != PK.Member) Borrows.Count(Borrows.Stat.Generic);
        }
        var groups = _groups.Select(g => g.Find()).Distinct().OrderBy(g => g.Order).ToList();
        foreach (var g in groups) Decide(g);
        foreach (var g in groups)
            foreach (var m in g.Members)
            {
                Facts.Evidence[m] = g.Evidence;
                bool load = m.Op is Opc.LdFld or Opc.LdElem or Opc.LdSFld or Opc.VarLd;
                if (g.Holds)
                {
                    Facts.Own[m] = OwnFact.Borrowed(g.Path);
                    if (load) Borrows.Count(Borrows.Stat.Borrowed);
                    else if (m.Op == Opc.Phi) Borrows.Count(Borrows.Stat.PhiBorrowed);
                    else Borrows.Count(Borrows.Stat.CastBorrowed);
                }
                else
                {
                    Facts.Own[m] = OwnFact.Owned;
                    Facts.Flipped[m] = true;
                    if (load) { Borrows.Count(Borrows.Stat.Flipped); Borrows.Count(g.Reason); }
                }
            }
        if (Borrows.SitesFile != null)
        {
            // (each counted load in block and node order, as lowering emits their retains, for tools/il-borrow-weight's
            // join with il-rcstats' executed counts)
            var fn = _ctx.Method.ToString()!;
            var lines = new List<string>();
            foreach (var b in _body.Blocks)
                foreach (var n in b.Code)
                {
                    if (n.Kind != VK.Ref) continue;
                    string? role = n.Op switch { Opc.LdFld => "field", Opc.LdElem => "element", Opc.LdSFld => "static", Opc.VarLd => _body.Vars[(int)n.I].InFrame ? "local-frame" : "local", _ => null };
                    if (role == null) continue;
                    string d = Facts.Own[n].Kind == OwnKind.Guaranteed ? "borrowed" : Facts.Flipped[n] ? "flip:" + groups.First(g => g.Members.Contains(n)).Reason.ToString()[4..] : "generic";
                    lines.Add(fn + "\t" + role + "\t" + d);
                }
            Borrows.AddSites(_ctx.Solution, lines);
        }
    }

    // ------------------------------------------------------------------ paths

    bool ParamOwned(int i)
    {
        if (_ctx.Entry.Kind == VariantKind.Owned && i < 32 && (_ctx.Entry.Mask & (1u << i)) != 0) return true;
        _sig ??= Signatures.Of(_ctx.Solution, _ctx.Method);
        return i < _sig.Params.Length && _sig.Params[i] == ParamConv.HandOver;
    }

    static bool IsCast(Node n) => n.Op is Opc.CastClass or Opc.IsInst
        || n.Op == Opc.Call && n.A.Length == 1 && n.A[0].Kind == VK.Ref && n.X is MethodDesc m && m.GetName() == "As"
           && m.OwningType is MetadataType ut && ut.GetName() == "Unsafe";

    PInfo RootOf(Node n) => new() { Kind = PK.Root, Path = new BorrowPath(RootKind.Val, n.Id) };

    PInfo Info(Node n)
    {
        if (_memo.TryGetValue(n, out var known)) return known;
        if (n.Op == Opc.Phi && _inProgress.Contains(n)) return new PInfo { Kind = PK.Self, Phi = n, SelfKeys = [], Chain = [] };
        var r = Compute(n);
        // (a loop phi reached through this node decided it already: it is in the phi's group, not a child of it)
        if (_memo.TryGetValue(n, out var set))
        {
            if (r.Kind == PK.Member && r.G!.Find() != set.G?.Find()) r.G.Find().Members.Remove(n);
            return set;
        }
        _memo[n] = r;
        return r;
    }

    Group NewGroup(Node member, BorrowPath path, IEnumerable<MemKey> keys, Group? parent)
    {
        var g = new Group { Path = path, Order = _order++ };
        g.Members.Add(member);
        g.Keys.AddRange(keys);
        if (parent != null) g.Parents.Add(parent);
        _groups.Add(g);
        return g;
    }

    PInfo Member(Group g) => new() { Kind = PK.Member, G = g };

    PInfo Compute(Node n)
    {
        if (n.Kind != VK.Ref) return PInfo.None;
        if (n.Op is Opc.LdNull or Opc.LdStr) return PInfo.Trivial;
        if (Borrows.NoSpec) return RootOf(n);
        switch (n.Op)
        {
            case Opc.Arg:
            {
                int i = (int)n.I;
                return ParamOwned(i) ? RootOf(n) : new PInfo { Kind = PK.Root, Path = new BorrowPath(RootKind.Arg, i) };
            }
            case Opc.LdSFld when n.X is FieldDesc sf:
            {
                if (Borrows.NoStatic || sf.IsThreadStatic) return RootOf(n);
                var key = MemKey.Static(sf);
                int id = _pb.Id(key);
                return Member(NewGroup(n, new BorrowPath(RootKind.Static, id), [key], null));
            }
            case Opc.VarLd:
            {
                int slot = (int)n.I;
                if (Borrows.NoVar || _addressTakenVars.Contains(slot) || _filterWrittenVars.Contains(slot)) return RootOf(n);
                return Member(NewGroup(n, new BorrowPath(RootKind.Var, slot), [MemKey.Var(slot)], null));
            }
            case Opc.LdFld when n.X is FieldDesc f && !f.IsStatic && n.A.Length > 0 && n.A[0].Kind == VK.Ref:
                if (Borrows.NoField) return RootOf(n);
                return Step(n, n.A[0], MemKey.Field(f), false);
            case Opc.LdElem when n.A.Length > 0 && n.A[0].Kind == VK.Ref && n.A[0].Type is ArrayType { IsSzArray: true } at:
                if (Borrows.NoElem) return RootOf(n);
                return Step(n, n.A[0], MemKey.Elem(at.ElementType), true);
            case Opc.Phi:
                return Phi(n);
        }
        if (IsCast(n))
        {
            var b = Info(n.A[0]);
            switch (b.Kind)
            {
                case PK.Member: { var g = b.G!.Find(); g.Members.Add(n); return Member(g); }
                case PK.Self: return new PInfo { Kind = PK.Self, Phi = b.Phi, SelfKeys = b.SelfKeys, Chain = [.. b.Chain!, n] };
                case PK.Trivial: return PInfo.Trivial;
            }
        }
        return RootOf(n);
    }

    PInfo Step(Node n, Node baseNode, MemKey key, bool elem)
    {
        var b = Info(baseNode);
        var step = new PathStep(_pb.Id(key), elem);
        switch (b.Kind)
        {
            case PK.Self:
                return new PInfo { Kind = PK.Self, Phi = b.Phi, SelfKeys = [.. b.SelfKeys!, key], Chain = [.. b.Chain!, n] };
            case PK.Member:
            {
                var pg = b.G!.Find();
                var p = pg.Path.Then(step);
                if (p == null) return RootOf(n);
                var g = NewGroup(n, p, pg.Keys.Append(key), pg);
                g.Origin = n;
                if (pg.Path.Root == RootKind.Val) g.RootNode = pg.RootNode;
                return Member(g);
            }
            case PK.Root:
            {
                var p = b.Path!.Then(step)!;
                var g = NewGroup(n, p, [key], null);
                g.Origin = n;
                g.RootNode = baseNode;
                return Member(g);
            }
        }
        return RootOf(n);
    }

    /// <summary>LocalBorrows on SSA: a phi whose inputs are null, one path (the external input), or values read out of
    /// the phi itself through at most three steps.</summary>
    PInfo Phi(Node p)
    {
        if (Borrows.NoLocal) return RootOf(p);
        _inProgress.Add(p);
        var infos = p.A.Select(Info).ToList();
        _inProgress.Remove(p);
        Group? ext = null;
        BorrowPath? rootPath = null;
        Node? rootNode = null;
        var selfKeys = new List<MemKey>();
        var chain = new List<Node>();
        bool ok = true, anySelf = false;
        for (int i = 0; i < infos.Count && ok; i++)
        {
            var x = infos[i];
            switch (x.Kind)
            {
                case PK.Trivial: break;
                case PK.Self when x.Phi == p && x.SelfKeys!.Count <= BorrowPath.MaxSteps:
                    anySelf = true; selfKeys.AddRange(x.SelfKeys); chain.AddRange(x.Chain!);
                    break;
                case PK.Member:
                {
                    var g = x.G!.Find();
                    if (ext == null) ext = g;
                    else if (ext != g)
                    {
                        if (!BorrowPath.Same(ext.Path, g.Path)) { ok = false; break; }
                        Merge(ext, g);
                    }
                    break;
                }
                case PK.Root:
                    // (a parameter, or an Owned value: `for (var n = this; n != null; n = n.Next)`)
                    // (an Owned value made inside the loop, `head = new Node { Next = head }`, is not one value for the
                    // phi's whole range: the phi is Owned itself, and loops over it are rooted at it)
                    if (x.Path!.Root == RootKind.Val && Reaches(p.Block, p.A[i].Block)) { ok = false; break; }
                    if (rootPath == null) { rootPath = x.Path; rootNode = x.Path!.Root == RootKind.Val ? p.A[i] : null; }
                    else if (!BorrowPath.Same(rootPath, x.Path)) ok = false;
                    break;
                default: ok = false; break;
            }
        }
        if (ok && rootPath != null)
        {
            if (ext != null) ok = false;
            else { ext = NewGroup(p, rootPath, [], null); ext.RootNode = rootNode; ext.Members.Remove(p); }
        }
        // (a phi of one path with no loop: `x = c ? this.f : this.f` is rare; one of a path and self-derived values is
        // the loop. Both are groups; anything else is Owned)
        if (!ok || ext == null)
        {
            // (the chain was read out of the phi while it was unknown: forget it, it is read out of an Owned phi)
            foreach (var c in chain) _memo.Remove(c);
            return RootOf(p);
        }
        ext.Members.Add(p);
        ext.Loop |= anySelf;
        if (anySelf) ext.Rerootable = false;
        foreach (var k in selfKeys) if (!ext.Keys.Contains(k)) ext.Keys.Add(k);
        var mi = Member(ext);
        foreach (var c in chain) { if (!ext.Members.Contains(c)) ext.Members.Add(c); _memo[c] = mi; }
        return mi;
    }

    /// <summary>Block <paramref name="to"/> is reachable from <paramref name="from"/> (itself included).</summary>
    static bool Reaches(Block from, Block to)
    {
        var seen = new HashSet<Block>();
        var work = new Stack<Block>();
        work.Push(from);
        while (work.Count > 0)
        {
            var b = work.Pop();
            if (b == to) return true;
            if (seen.Add(b)) foreach (var s in b.Succs) work.Push(s);
        }
        return false;
    }

    void Merge(Group into, Group from)
    {
        if (into == from) return;
        from.Merged = into;
        foreach (var m in from.Members) if (!into.Members.Contains(m)) into.Members.Add(m);
        foreach (var k in from.Keys) if (!into.Keys.Contains(k)) into.Keys.Add(k);
        foreach (var pa in from.Parents) if (!into.Parents.Contains(pa)) into.Parents.Add(pa);
        into.Rerootable = false;
        into.Order = Math.Max(into.Order, from.Order);
    }

    // ------------------------------------------------------------------ decisions

    void Fail(Group g, Borrows.Stat why, BorrowEvidence ev)
    {
        g.Holds = false; g.Reason = why; g.Evidence = ev;
        if (Borrows.Trace) Console.Error.WriteLine($"borrow flip {_ctx.Method} {string.Join(",", g.Members.Select(m => m + ":" + m.Op))} path {g.Path}: {why.ToString()[4..]}{(_cause != null ? " at " + _cause : "")}");
        _cause = null;
    }

    void Decide(Group g)
    {
        g.Decided = true;
        // the parents first (they are made before their children: lower order)
        foreach (var pa in g.Parents.Select(x => x.Find()))
        {
            if (!pa.Decided) Decide(pa);
            if (pa.Holds) continue;
            // the parent flipped: the child is read out of its Owned value (one origin), or flips too
            if (g.Rerootable && g.Parents.Count == 1 && g.Origin != null && g.Path.Steps.Length > 0)
            {
                var last = g.Path.Steps[^1];
                var key = g.Keys[^1];
                g.Path = new BorrowPath(RootKind.Val, g.Origin.A[0].Id, [last]);
                g.Keys.Clear(); g.Keys.Add(key);
                g.RootNode = g.Origin.A[0];
                g.Parents.Clear();
                break;
            }
            Fail(g, Borrows.Stat.FlipParent, new BorrowEvidence(KeySet.Empty, true, false));
            return;
        }
        var live = _info!;
        // the interval: per block, the hull of the members' live ranges
        var interval = new Dictionary<Block, (int From, int To)>();
        foreach (var m in g.Members)
            foreach (var (b, r) in LiveRange(m, live))
                interval[b] = interval.TryGetValue(b, out var o) ? (Math.Min(o.From, r.From), Math.Max(o.To, r.To)) : r;
        var keys = g.Keys.Where(k => !_pb.InitOnly(k)).ToList();
        int initOnly = g.Keys.Count - keys.Count;
        if (initOnly > 0) Borrows.Count(Borrows.Stat.InitOnlyUse);
        // proof 2: no other thread
        if (_ctx.Threaded && (g.Path.Root == RootKind.Static || keys.Any(k => k.Kind != KeyKind.Var)))
        { Fail(g, Borrows.Stat.FlipThread, new BorrowEvidence(KeySet.Empty, false, true)); return; }
        // the root alive
        if (g.Path.Root == RootKind.Val)
        {
            var rootNode = g.RootNode;
            if (rootNode == null || !Covers(LiveRange(rootNode, live), interval)) { Fail(g, Borrows.Stat.FlipRootDies, new BorrowEvidence(KeySet.Empty, true, false)); return; }
        }
        var members = g.Members.ToHashSet();
        var alias = _pb.Aliases(keys);
        int varSlot = g.Path.Root == RootKind.Var ? g.Path.Index : -1;
        bool planned = _body.Blocks.Any(b => b.Code.Any(n => n.Op is Opc.Release or Opc.Retain));
        // FilterMods: a filter (here or in any frame up the stack) runs before a handler that uses the group
        if (keys.Count > 0 && _body.Regions.Any(r => r.IsHandler && interval.TryGetValue(r.Entry, out var hr) && hr.From == 0 && live.LiveIn[r.Entry].Overlaps(members)))
        {
            var fm = _pb.FilterMods();
            if (alias.Hits(fm)) { Fail(g, Borrows.Stat.FlipFilter, new BorrowEvidence(fm, false, false)); return; }
        }
        if (keys.Count == 0 && varSlot < 0) { g.Holds = true; return; }
        foreach (var b in _body.Blocks)
        {
            if (!interval.TryGetValue(b, out var r) || r.To < r.From) continue;
            Dictionary<Node, int>? lastUse = planned ? null : LastUses(b, live);
            // (the block's entry releases what a predecessor holds that is dead here)
            if (!planned && r.From == 0)
                foreach (var pr in b.Preds)
                    foreach (var v in live.LiveOut[pr])
                        if (!live.LiveIn[b].Contains(v) && !members.Contains(v) && Counted(v) && Release(g, alias, v.Type)) return;
            for (int i = Math.Max(r.From, 0); i <= r.To && i < b.Code.Count; i++)
            {
                var n = b.Code[i];
                if (Effect(g, n, members, alias, varSlot)) return;
                if (!planned)
                {
                    // the releases this node implies: its counted operands' last uses that do not move, an unused result
                    for (int k = 0; k < n.A.Length; k++)
                    {
                        var a = n.A[k];
                        if (!Counted(a) || members.Contains(a) || lastUse!.GetValueOrDefault(a, -1) != i || Ownership.Consumes(n, k)) continue;
                        if (Array.IndexOf(n.A, a) != k) continue;
                        if (Release(g, alias, a.Type)) return;
                    }
                    if (Counted(n) && !members.Contains(n) && !live.LiveOut[b].Contains(n) && !lastUse!.ContainsKey(n) && !n.IsTerminator && Release(g, alias, n.Type)) return;
                }
            }
        }
        g.Holds = true;
        g.Evidence = BorrowEvidence.None;
    }

    // (a type object is immortal too: its release frees nothing)
    static bool Counted(Node v) => v.Op is not (Opc.LdNull or Opc.LdStr or Opc.TypeOf) && (v.Kind == VK.Ref || v.Kind == VK.Struct && v.Type != null && Kinds.Counted(v.Type));

    bool Release(Group g, AliasSet alias, TypeDesc? t)
    {
        var e = _pb.DestroyEffects(t);
        if (!alias.Hits(e)) return false;
        Fail(g, Borrows.Stat.FlipRelease, new BorrowEvidence(e, false, false));
        return true;
    }

    /// <summary>The node's own effect on the group's keys; true when it breaks the group (and the group failed).</summary>
    bool Effect(Group g, Node n, HashSet<Node> members, AliasSet alias, int varSlot)
    {
        MemKey? stored = null; Node? value = null; bool wild = false; TypeDesc? oldType = null;
        switch (n.Op)
        {
            case Opc.StFld when n.X is FieldDesc f: stored = f.IsStatic ? MemKey.Static(f) : MemKey.Field(f); value = n.A.ElementAtOrDefault(1); oldType = f.FieldType; break;
            case Opc.StSFld when n.X is FieldDesc sf: stored = MemKey.Static(sf); value = n.A.ElementAtOrDefault(0); oldType = sf.FieldType; break;
            case Opc.StElem:
                stored = n.A[0].Type is ArrayType sat ? MemKey.Elem(sat.ElementType) : MemKey.Slot(n.A[2].Type ?? _body.Method.Context.GetWellKnownType(WellKnownType.Object));
                value = n.A[2]; oldType = n.A[2].Type;
                break;
            case Opc.StInd: case Opc.InitObj:
            {
                var addr = n.A[0];
                var t = n.X as TypeDesc ?? n.A.ElementAtOrDefault(1)?.Type;
                value = n.Op == Opc.StInd ? n.A.ElementAtOrDefault(1) : null; oldType = t;
                switch (addr.Op)
                {
                    case Opc.LdFldA when addr.X is FieldDesc af: stored = af.IsStatic ? MemKey.Static(af) : MemKey.Field(af); break;
                    case Opc.LdSFldA when addr.X is FieldDesc asf: stored = MemKey.Static(asf); break;
                    case Opc.LdElemA when addr.A.Length > 0 && addr.A[0].Type is ArrayType aat: stored = MemKey.Elem(aat.ElementType); break;
                    case Opc.LocA: stored = MemKey.Var((int)addr.I); break;
                    default:
                        if (addr.Type is ByRefType br) stored = MemKey.Slot(br.ParameterType);
                        else if (t != null && addr.Kind == VK.Ptr && addr.Type is not PointerType && addr.Type?.IsPointer != true && addr.Op is Opc.Arg) stored = MemKey.Slot(t);
                        else wild = true;
                        break;
                }
                break;
            }
            case Opc.VarSt: stored = MemKey.Var((int)n.I); value = n.A.ElementAtOrDefault(0); oldType = _body.Vars[(int)n.I].Type; break;
            case Opc.CpBlk: case Opc.InitBlk: wild = true; break;
            case Opc.Call when members.Contains(n) && IsCast(n): break; // (Unsafe.As of the group's own value: a forward)
            case Opc.Call: case Opc.NewObj: case Opc.CallI:
            {
                var (e, intrinsic) = CallEffect(n, members);
                if (alias.Hits(e)) { if (Borrows.Trace) _cause = n.Op + " " + (n.X as MethodDesc)?.ToString(); Fail(g, e.Wild ? (intrinsic ? Borrows.Stat.FlipIntrinsic : Borrows.Stat.FlipCallWild) : Borrows.Stat.FlipCallKey, new BorrowEvidence(e, false, false)); return true; }
                break;
            }
            case Opc.Release when n.A.Length > 0 && !members.Contains(n.A[0]) && Counted(n.A[0]):
                if (Release(g, alias, n.A[0].Type)) return true;
                break;
        }
        // a static constructor this node may start
        var cc = _pb.CctorEffects(_body.Method, n);
        if (!cc.IsEmpty && alias.Hits(cc)) { Fail(g, Borrows.Stat.FlipCctor, new BorrowEvidence(cc, false, false)); return true; }
        if (Borrows.BreakSpec) return false;
        if (wild && (alias.Any || varSlot >= 0)) { Fail(g, Borrows.Stat.FlipWildStore, new BorrowEvidence(KeySet.All, false, false)); return true; }
        if (stored is not { } k) return false;
        bool self = !Borrows.NoSelfStores && (Borrows.BreakSelfStores || value != null && members.Contains(value));
        bool hit = k.Kind == KeyKind.Var ? k.Index == varSlot : alias.Hits(k);
        if (hit)
        {
            if (self) Borrows.Count(Borrows.Stat.SelfStore);
            else
            {
                var ws = k.Kind == KeyKind.Var ? KeySet.Empty : new KeySet(_ctx.Solution.Pool.Of(_pb.Id(k)), false);
                Fail(g, k.Kind == KeyKind.Var ? Borrows.Stat.FlipVarWrite : Borrows.Stat.FlipStore, new BorrowEvidence(ws, false, false));
                return true;
            }
        }
        // the old value's release (a counted slot)
        if (oldType != null && Kinds.Counted(oldType) && Release(g, alias, oldType)) return true;
        return false;
    }

    /// <summary>A call's effects on a group: the callees' whole Mods when the group is live into a handler the call's
    /// exception can reach (the callee may write on its way out, and the handler uses the borrow), else only what the
    /// callees may write on the runs that return (ProgramSolution.ReturnMods: a throw helper's work is then none of
    /// the group's business, since the group is dead if it throws). IL2WASM_NO_RETURN_MODS=1: always the whole Mods.</summary>
    (KeySet Effects, bool Intrinsic) CallEffect(Node n, HashSet<Node> members)
    {
        if (n.Op == Opc.CallI || n.X is not MethodDesc m) return (KeySet.All, false);
        if (n.Op == Opc.Call && _intrinsics.Contains(m))
        {
            // (Interlocked.Exchange(ref this._f, v) and the like: the key of the address, as Summary records it)
            if (IntrinsicSignatures.WritesThroughFirstRef(m) && n.A.Length > 0 && MethodSummary.AddressKey(n.A[0]) is { } ak)
                return (new KeySet(_ctx.Solution.Pool.Of(_pb.Id(ak)), false), true);
            return (ProgramSolution.IntrinsicEffects(_ctx.Solution, _ctx.Method, m), true);
        }
        bool full = Borrows.NoReturnMods || Ownership.ExceptionSuccs(n.Block).Any(h => _info!.LiveIn.TryGetValue(h, out var li) && li.Overlaps(members));
        return (_pb.CallEffects(n, full), false);
    }

    /// <summary>Per block, where the value is live: from after its definition (or the block's start) to its last use
    /// (or the block's end when live out); exceptional and phi successors are in the liveness already.</summary>
    Dictionary<Block, (int From, int To)> LiveRange(Node v, Ownership.Info live)
    {
        var r = new Dictionary<Block, (int, int)>();
        foreach (var b in _body.Blocks)
        {
            int from;
            if (v.Block == b && !b.Phis.Contains(v))
            {
                int at = b.Code.IndexOf(v);
                if (at < 0) continue;
                from = at + 1;
            }
            else if (v.Block == b || live.LiveIn.TryGetValue(b, out var li) && li.Contains(v)) from = 0;
            else continue;
            int to;
            if (live.LiveOut.TryGetValue(b, out var lo) && lo.Contains(v)) to = b.Code.Count - 1;
            else
            {
                to = from - 1;
                for (int i = from; i < b.Code.Count; i++) if (Uses(b.Code[i], v, live)) to = i;
            }
            r[b] = (from, to);
        }
        return r;
    }

    static bool Uses(Node n, Node v, Ownership.Info live)
    {
        foreach (var a in n.A)
            if (a == v || live.Bases.TryGetValue(a, out var bs) && bs.Contains(v)) return true;
        return false;
    }

    /// <summary>The root's live range covers the interval at every point.</summary>
    static bool Covers(Dictionary<Block, (int From, int To)> root, Dictionary<Block, (int From, int To)> interval)
    {
        foreach (var (b, r) in interval)
        {
            if (r.To < r.From) continue;
            if (!root.TryGetValue(b, out var x) || x.From > r.From || x.To < r.To) return false;
        }
        return true;
    }

    /// <summary>The last use, in the block, of each counted value not live out of it.</summary>
    static Dictionary<Node, int> LastUses(Block b, Ownership.Info live)
    {
        var d = new Dictionary<Node, int>();
        var lo = live.LiveOut[b];
        for (int i = 0; i < b.Code.Count; i++)
            foreach (var a in b.Code[i].A)
            {
                if (lo.Contains(a)) continue;
                if (Counted(a)) d[a] = i;
                if (live.Bases.TryGetValue(a, out var bs)) foreach (var x in bs) if (!lo.Contains(x)) d[x] = i;
            }
        return d;
    }
}

/// <summary>A group's write-breaking keys, expanded by type-based aliasing to dense key ids (keys interned after the
/// solve are in no method's Mods).</summary>
sealed class AliasSet(IdSet ids, bool any, Func<MemKey, bool> contains)
{
    public readonly IdSet Ids = ids;
    /// <summary>There is a key at all (a wild write hits).</summary>
    public readonly bool Any = any;
    public bool Hits(KeySet s) => Any && (s.Wild || s.Keys.Overlaps(Ids));
    public bool Hits(MemKey k) => Any && contains(k);
}

/// <summary>The program-wide facts the borrows read, per solution, made when first asked for: key aliasing, init-only
/// keys, destroy effects per type, the static constructors a call may start, FilterMods.</summary>
sealed class ProgramBorrows
{
    static readonly ConditionalWeakTable<ProgramSolution, ProgramBorrows> s_of = new();
    public static ProgramBorrows Of(ProgramSolution s) { lock (s_of) return s_of.GetValue(s, x => new ProgramBorrows(x)); }

    readonly ProgramSolution _s;
    ProgramBorrows(ProgramSolution s) { _s = s; }

    public int Id(MemKey k) => _s.Keys.Intern(k);

    // ---- aliasing (RefStores' rule: a ref T names any slot of type T; Unsafe.As merges)

    List<(int Id, MemKey Key)>? _slots;
    bool? _aliased;
    bool AnyUnsafeAs => _aliased ??= _s.Methods.Items.Any(m => _s.Summary(m) is { } su && su.Aliased.Count > 0);

    static TypeDesc? TypeOfKey(MemKey k) => k.Kind switch
    {
        KeyKind.Field or KeyKind.Static => ((FieldDesc)k.Target!).FieldType,
        KeyKind.Elem or KeyKind.Slot => (TypeDesc)k.Target!,
        _ => null,
    };

    bool MayAlias(TypeDesc a, TypeDesc b)
    {
        if (a == b) return true;
        if (a.IsValueType || b.IsValueType || a.IsPointer || b.IsPointer || a.IsByRef || b.IsByRef) return false;
        if (AnyUnsafeAs) return true;
        if (a.IsInterface || b.IsInterface || a.IsObject || b.IsObject || a.IsGenericParameter || b.IsGenericParameter) return true;
        return a.CanCastTo(b) || b.CanCastTo(a);
    }

    /// <summary>The ids of every key a write to which may write one of these (the keys themselves; slot keys and, for
    /// an element key, element keys of a castable type).</summary>
    public AliasSet Aliases(List<MemKey> keys)
    {
        if (keys.Count == 0) return new AliasSet(IdSet.Empty, false, _ => false);
        if (_slots == null)
        {
            if (_s.Methods.Count > 0) _s.Facts(_s.Methods[0]); // (the Mods solved: their keys interned)
            _slots = [];
            for (int i = 0; i < _s.Keys.Count; i++) if (_s.Keys[i].Kind is KeyKind.Slot or KeyKind.Elem) _slots.Add((i, _s.Keys[i]));
        }
        var ids = new List<int>();
        var set = new HashSet<MemKey>();
        foreach (var k in keys)
        {
            if (k.Kind == KeyKind.Var) continue;
            ids.Add(Id(k)); set.Add(k);
            var t = TypeOfKey(k);
            if (t == null) continue;
            foreach (var (id, sk) in _slots)
            {
                if (sk == k) continue;
                if (sk.Kind == KeyKind.Elem && k.Kind != KeyKind.Elem) continue;
                if (MayAlias((TypeDesc)sk.Target!, t)) { ids.Add(id); set.Add(sk); }
            }
        }
        bool any = keys.Any(k => k.Kind != KeyKind.Var);
        return new AliasSet(_s.Pool.Of(ids), any, mk => set.Contains(mk) || mk.Kind is KeyKind.Slot or KeyKind.Elem && keys.Any(k => k.Kind != KeyKind.Var && TypeOfKey(k) is { } kt
            && !(mk.Kind == KeyKind.Elem && k.Kind != KeyKind.Elem) && MayAlias((TypeDesc)mk.Target!, kt)));
    }

    // ---- init-only keys (InitOnlyFields.InitOnlyKey on the IR)

    readonly Dictionary<FieldDesc, bool> _initOnly = new();
    bool? _initOnlyOff;

    public bool InitOnly(MemKey k)
    {
        if (k.Kind != KeyKind.Field) return false;
        if (Borrows.BreakInitOnly) return true;
        if (Borrows.NoInitOnly) return false;
        var f = (FieldDesc)k.Target!;
        if (_initOnly.TryGetValue(f, out var r)) return r;
        return _initOnly[f] = ComputeInitOnly(f);
    }

    /// <summary>The field is `initonly`, of a class; no code takes its address (the constant propagation's field
    /// addresses: without it, IL2WASM_NO_SCCP, nothing is init-only), reflection cannot set it (no FieldInfo.SetValue
    /// reached), and every store to it is in a constructor of its own type, at most one per constructor, on `this`, in
    /// no loop, in a constructor that does not chain to another of its type that stores it too.</summary>
    bool ComputeInitOnly(FieldDesc f)
    {
        if (!f.IsInitOnly || f.IsStatic || f.OwningType.IsValueType || f.IsThreadStatic) return false;
        _initOnlyOff ??= Reach.ProgramFacts.Off || _s.Methods.Items.Any(m => m.GetName().StartsWith("SetValue", StringComparison.Ordinal) && DerivesFrom(m.OwningType, "System.Reflection", "FieldInfo"));
        if (_initOnlyOff == true) return false;
        var own = f.OwningType.GetTypeDefinition();
        var key = MemKey.Field(f);
        var writers = new List<MethodDesc>();
        foreach (var m in _s.Methods.Items)
        {
            if (_s.Summary(m) is not { } su) continue;
            foreach (var (sf, _, addr) in su.FieldStores) if (addr && MemKey.Field(sf) == key) return false;
            int n = su.Stores.Count(x => x == key);
            if (n == 0) continue;
            if (n > 1 || !m.IsConstructor || m.OwningType.GetTypeDefinition() != own) return false;
            writers.Add(m);
        }
        foreach (var w in writers) if (!OnceOnThis(w, key, writers)) return false;
        return true;
    }

    static bool DerivesFrom(TypeDesc? t, string ns, string name)
    {
        for (; t != null; t = t.BaseType) if (t is MetadataType mt && mt.GetName() == name && mt.GetNamespace() == ns) return true;
        return false;
    }

    /// <summary>The constructor's one store of the key is on `this`, in no loop, and it chains to no constructor of its
    /// type that stores it (the constructor imported again: import is deterministic).</summary>
    bool OnceOnThis(MethodDesc ctor, MemKey key, List<MethodDesc> writers)
    {
        Body body;
        try
        {
            if (ctor.GetTypicalMethodDefinition() is not EcmaMethod typical) return false;
            MethodIL il = EcmaMethodIL.Create(typical);
            if (ctor != typical) il = new InstantiatedMethodIL(ctor, il);
            body = Importer.Import(ctor, il);
        }
        catch (Exception) { return false; }
        var looping = Looping(body);
        foreach (var b in body.Blocks)
            foreach (var n in b.Code)
            {
                if (n.Op == Opc.StFld && n.X is FieldDesc f && MemKey.Field(f) == key && (n.A[0].Op != Opc.Arg || n.A[0].I != 0 || looping.Contains(b))) return false;
                if (n.Op == Opc.Call && n.X is MethodDesc c && c != ctor && writers.Contains(c)) return false;
            }
        return true;
    }

    /// <summary>The blocks on a cycle of the CFG.</summary>
    static HashSet<Block> Looping(Body body)
    {
        var r = new HashSet<Block>();
        foreach (var b in body.Blocks)
        {
            var seen = new HashSet<Block>();
            var work = new Stack<Block>(b.Succs);
            while (work.Count > 0)
            {
                var x = work.Pop();
                if (x == b) { r.Add(b); break; }
                if (seen.Add(x)) foreach (var s in x.Succs) work.Push(s);
            }
        }
        return r;
    }

    // ---- destroy effects (3.6): the solve's, per type (ProgramSolution.DestroyEffects)

    readonly Dictionary<TypeDesc, KeySet> _destroy = new();

    /// <summary>A release of a value of static type t: the Mods of the finalizers and weak-mark hooks its destroy can
    /// run (freeing writes no live object's field).</summary>
    public KeySet DestroyEffects(TypeDesc? t)
    {
        if (t == null) return _s.DestroyEffects(null);
        if (_destroy.TryGetValue(t, out var r)) return r;
        return _destroy[t] = _s.DestroyEffects(t);
    }

    // ---- calls: Mods ∪ ReleaseEffects of every target

    readonly Dictionary<MethodDesc, KeySet> _calls = new(), _returns = new();

    /// <summary>Mods ∪ ReleaseEffects of every target (<paramref name="full"/>), or only what they may write on the runs
    /// that return: their ReturnMods ∪ ReleaseEffects, and nothing for a target that never returns.</summary>
    public KeySet CallEffects(Node call, bool full = true)
    {
        var targets = _s.CallTargets(call);
        // (no target known: a call the solve cannot see through)
        if (targets.Count == 0) return KeySet.All;
        var e = KeySet.Empty;
        foreach (var t in targets)
        {
            e = e.Join(full ? Of(t) : OfReturn(t), _s.Pool);
            if (e.Wild) break;
        }
        return e;
    }

    KeySet Of(MethodDesc t)
    {
        if (_calls.TryGetValue(t, out var r)) return r;
        if (!_s.Covers(t)) r = KeySet.All;
        else
        {
            var f = _s.Facts(t);
            // (the solve's Mods include the static constructors it may start)
            r = f.Mods.Join(f.ReleaseEffects, _s.Pool);
        }
        _calls[t] = r;
        return r;
    }

    KeySet OfReturn(MethodDesc t)
    {
        if (_returns.TryGetValue(t, out var r)) return r;
        if (!_s.Covers(t)) r = KeySet.All;
        else if (_s.Summary(t) is { MayReturn: false } && ProgramSolution.DeclaredEffects(_s, t) == null) r = KeySet.Empty;
        else r = _s.ReturnMods(t);
        _returns[t] = r;
        return r;
    }

    /// <summary>What a node of the method may start itself (Summary's Initializes rules): a static of another type, a
    /// static method, constructor or value type method of a type that is not beforefieldinit, a constructor.</summary>
    public KeySet CctorEffects(MethodDesc method, Node n)
    {
        TypeDesc? t = null;
        switch (n.Op)
        {
            case Opc.LdSFld or Opc.LdSFldA or Opc.StSFld when n.X is FieldDesc sf:
                if (sf.OwningType != method.OwningType) t = sf.OwningType;
                break;
            case Opc.Call when (CallKind)n.Sub == CallKind.Direct && n.X is MethodDesc m:
                if (!m.IsStaticConstructor && (m.Signature.IsStatic || m.IsConstructor || m.OwningType.IsValueType) && m.OwningType is MetadataType { IsBeforeFieldInit: false } it) t = it;
                break;
            case Opc.NewObj when n.X is MethodDesc ctor:
                if (ctor.OwningType is MetadataType { IsBeforeFieldInit: false } ct && !ct.IsString) t = ct;
                break;
        }
        if (t?.GetStaticConstructor() is not { } cctor) return KeySet.Empty;
        return Of(cctor);
    }

    // ---- FilterMods (7.2)

    KeySet? _filterMods;

    /// <summary>The effects of every reachable method with a filter (the filter's code is the method's): it may run
    /// during the first pass, before a handler of any frame below it.</summary>
    public KeySet FilterMods()
    {
        if (_filterMods is { } known) return known;
        var r = KeySet.Empty;
        foreach (var m in _s.Methods.Items)
        {
            if (r.Wild) break;
            if (m.GetTypicalMethodDefinition() is not EcmaMethod em) continue;
            bool filter;
            try { filter = EcmaMethodIL.Create(em) is { } il && il.GetExceptionRegions().Any(x => x.Kind == ILExceptionRegionKind.Filter); }
            catch (Exception) { filter = false; }
            if (filter) r = r.Join(Of(m), _s.Pool);
        }
        _filterMods = r;
        return r;
    }
}
