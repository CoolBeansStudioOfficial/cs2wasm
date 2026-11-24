#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>The per-method OSSA plan (docs/MEMORY-IR.md 1.2, 2.1, 4.3, 4.6, 4.7, 7): every counted value is Owned
/// (it holds one count, consumed exactly once on every path: moved into a consuming use, or released) or Guaranteed
/// (it holds none; its root lives over its uses) or Trivial (null, a literal). It replaces Ownership.Insert and keeps
/// Ownership's analysis (the split edges, the derived addresses, liveness with the handlers as successors and a
/// finally's end followed by its leave targets), with these differences:
/// - parameters follow the entry's convention (Signatures): a Lend parameter is Guaranteed(Arg), with no retain on
///   entry and no release (4.2); a HandOver one is Owned with no retain on entry (the caller's count moved in); a
///   method outside the solution keeps the naive retain on entry, as its callers may be generated code;
/// - counted loads, phis and borrowed-return results are Guaranteed where Borrows says so; a Guaranteed value's uses
///   are uses of its root (its Val root lives to them);
/// - at a direct call the call rule (4.3) and the variant chosen (4.4) decide each argument: HandOver (a move, Moves
///   bit), KeepAlive (lowering's retain at the push) or Lend (a release after the call at the last use);
/// - an address carried round a loop by a phi keeps its objects (7.3's one fix);
/// - a returned span or byref over an object only this function holds hands that object to its caller (7.4):
///   Rt.ReturnKeepers.Hold before the return, Rt.ReturnKeepers.Take after the call, an Owned base of the result; where the keeper
///   protocol does not reach (a callee outside the solution, the stub CoreLib), the naive keep stays (a leak, counted);
/// - GC.KeepAlive's argument is held by a count of this frame to the call (a borrow with a root that may be written
///   flips to Owned).
/// The body may come in unplanned or planned (Canon re-plans a spliced body): the count nodes and Moves bits of an
/// earlier plan are taken out first. The result depends only on the body and the solution (sorted ids, list order).</summary>
public static class Plan
{
    /// <summary>IL2WASM_NO_RETURN_KEEP=1: no keeper protocol (the naive keep: a returned span's base only this function
    /// held is never let go).</summary>
    static readonly bool s_noKeeper = Environment.GetEnvironmentVariable("IL2WASM_NO_RETURN_KEEP") == "1";

    /// <summary>The nodes a plan inserted besides Retain/Release (the keeper calls), taken out when a body is planned
    /// again.</summary>
    static readonly ConditionalWeakTable<Node, object> s_inserted = new();

    /// <summary>Per body: the values the plan leaves holding a count at a return on purpose (the naive keep where the
    /// keeper protocol does not reach), for the verifier.</summary>
    internal static readonly ConditionalWeakTable<Body, HashSet<Node>> KnownKeeps = new();

    /// <summary>Counters for the report (all plans of the process).</summary>
    public static int Keepers, MissedKeepers, Takes, Flips, Plans;

    /// <summary>IL2WASM_PLAN_STATS=1: the counters, and each method whose returned address keeps a base the protocol
    /// does not reach, on stderr when the process exits.</summary>
    static readonly bool s_stats = Environment.GetEnvironmentVariable("IL2WASM_PLAN_STATS") == "1";
    static readonly SortedSet<string> s_missed = new(StringComparer.Ordinal);

    static Plan()
    {
        if (s_stats)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Console.Error.WriteLine($"plan: {Plans} plans, {Keepers} keepers held, {Takes} takes, {MissedKeepers} keeps not handed on, {Flips} flips, {Verify.Checked} verified");
                foreach (var m in s_missed) Console.Error.WriteLine("plan: keep not handed on: " + m);
            };
    }

    /// <summary>Plans one body for one entry: inserts the Retain and Release nodes and Node.Moves bits into the body,
    /// and fills the plan's tables (Own from Borrows.Analyze, Args from Signatures.CallRule, Callees from
    /// Signatures.ChooseVariant, Stores, Loads, Counts, Accesses, Allocs, Categories).</summary>
    public static MethodPlan Build(PlanContext ctx)
    {
        Strip(ctx.Body);
        long t0 = s_slow > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var plan = new Planner(ctx).Run();
        if (s_slow > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds is var ms && ms >= s_slow)
            Console.Error.WriteLine($"plan: {ms:F0} ms for {ctx.Method} ({ctx.Body.Blocks.Count} blocks, {ctx.Body.ValueCount} values)");
        System.Threading.Interlocked.Increment(ref Plans);
        Built?.Invoke(ctx, plan);
        if (s_dump != null && ctx.Method.ToString()!.Contains(s_dump)) Console.Error.WriteLine("plan of " + ctx.Method + " (" + ctx.Entry + "):" + Environment.NewLine + ctx.Body.Dump());
        return plan;
    }

    /// <summary>Called with each plan Build makes, with its context (the tests read the variants chosen through it).</summary>
    public static Action<PlanContext, MethodPlan>? Built;

    /// <summary>IL2WASM_PLAN_SLOW=ms: each plan that takes at least that long, on stderr.</summary>
    static readonly int s_slow = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_PLAN_SLOW"), out int ms) ? ms : 0;

    /// <summary>IL2WASM_PLAN_DUMP=text: the planned body of each method whose name contains it, on stderr.</summary>
    static readonly string? s_dump = Environment.GetEnvironmentVariable("IL2WASM_PLAN_DUMP") is { Length: > 0 } d ? d : null;

    /// <summary>Takes an earlier plan out of a body: its Retain and Release nodes, its keeper calls, its Moves bits.</summary>
    static void Strip(Body body)
    {
        foreach (var b in body.Blocks)
        {
            b.Code.RemoveAll(n => n.Op is Opc.Retain or Opc.Release || s_inserted.TryGetValue(n, out _));
            foreach (var n in b.Phis) n.Moves = 0;
            foreach (var n in b.Code) n.Moves = 0;
        }
    }

    internal static void MarkInserted(Node n) => s_inserted.AddOrUpdate(n, true);
    internal static bool IsInserted(Node n) => s_inserted.TryGetValue(n, out _);

    // ------------------------------------------------------------------ shared with Verify

    /// <summary>A value that holds counts: a reference, or a struct with references in it.</summary>
    internal static bool IsRef(Node n) => Ownership.IsRef(n);

    /// <summary>Null, a literal string, a constant: holds no count and needs none (strings are immortal literals).</summary>
    internal static bool IsTrivial(Node n) => n.Op is Opc.LdNull or Opc.LdStr || n.Op == Opc.Const && n.Kind == VK.Ref;

    /// <summary>The operations whose result lowering can leave uncounted (a Guaranteed borrow): the counted loads, a
    /// phi, a call's result in its borrowed-return entry, and a parameter.</summary>
    internal static bool CanBorrow(Node n) => n.Op is Opc.LdFld or Opc.LdElem or Opc.LdSFld or Opc.LdInd or Opc.VarLd or Opc.Phi or Opc.Arg or Opc.Call;

    /// <summary>The parameter a call operand is passed to (this is 0; a constructor's operand k is parameter k+1).</summary>
    internal static int ParamOf(Node call, int k) => Signatures.ParamOf(call, k);

    /// <summary>A call site where a variant other than the entry may be chosen (S1: direct calls and constructors).</summary>
    internal static bool DirectSite(Node n) => n.X is MethodDesc m && (n.Op == Opc.Call && n.Sub == (byte)CallKind.Direct || n.Op == Opc.NewObj && m.OwningType is not ArrayType);

    /// <summary>The variant takes parameter p owned (HandOver at the caller).</summary>
    internal static bool Owns(Variant v, int p) => Signatures.Owns(v, p);

    /// <summary>The convention of entry v of m (Signatures.ConventionOf's parameters, by the same rule without reading the
    /// demand: the entry lends everything; an owned entry is HandOver at its mask; a borrowed-return entry lends
    /// everything); null for a method outside the solution (the naive convention:
    /// the callee retains each parameter on entry).</summary>
    internal static ParamConv[]? ConventionOf(ProgramSolution sol, MethodDesc m, Variant v)
    {
        if (!sol.Covers(m)) return null;
        int n = m.Signature.Length + (m.Signature.IsStatic ? 0 : 1);
        var c = new ParamConv[n];
        if (v.Kind == VariantKind.Owned) { for (int i = 0; i < n; i++) if (Owns(v, i)) c[i] = ParamConv.HandOver; return c; }
        // (the entry: every parameter Lend, Signatures.Of's convention; a HandOver here would need every caller,
        // generated ones included, to hand its count over. Not asked of Signatures.Of, which reads the solved facts:
        // their first use solves the whole program's Mods)
        return c;
    }

    /// <summary>A method's result is an address into memory (a byref, or a byref-like struct such as a span).</summary>
    internal static bool ReturnsAddress(MethodDesc m)
    {
        var r = m.Signature.ReturnType;
        return r is ByRefType || r is DefType { IsByRefLike: true, IsValueType: true };
    }

    /// <summary>The runtime's keeper calls (null without them: the stub CoreLib).</summary>
    internal static (MethodDesc Hold, MethodDesc Take)? KeeperCalls(TypeSystemContext c)
    {
        if (s_noKeeper || !Pipeline.Planned) return null;
        lock (s_keeperCalls)
        {
            if (s_keeperCalls.TryGetValue(c, out var k)) return k;
            (MethodDesc, MethodDesc)? r = null;
            if (c.SystemModule.GetType("Il2Wasm.Runtime"u8, "Rt"u8, throwIfNotFound: false) is MetadataType rt && rt.GetNestedType("ReturnKeepers"u8) is MetadataType rk)
            {
                var hold = rk.GetMethods().FirstOrDefault(x => x.GetName() == "Hold");
                var take = rk.GetMethods().FirstOrDefault(x => x.GetName() == "Take");
                if (hold != null && take != null) r = (hold, take);
            }
            s_keeperCalls.Add(c, r);
            return r;
        }
    }
    static readonly Dictionary<TypeSystemContext, (MethodDesc, MethodDesc)?> s_keeperCalls = new();

    /// <summary>The method may hand its caller a keeper (7.4): it is in the solution, returns an address, and either makes
    /// an object (a type that can be on the heap: a byref-like struct cannot), calls what it cannot see (calli, a
    /// delegate), calls something whose result is counted (Unsafe's forwarding casts aside), or calls a target that
    /// may itself hand it a keeper (the only ways an object only it holds comes to be). The callee hands a keeper only
    /// where this holds, and every caller of such a target takes after the call. A least fixed point over the calls
    /// (a recursion in progress counts as no keeper), memoized per solution.</summary>
    internal static bool MayKeep(ProgramSolution sol, MethodDesc m)
    {
        if (!ReturnsAddress(m) || sol.Summary(m) is null) return false;
        Dictionary<MethodDesc, bool> memo;
        lock (s_mayKeep) { if (!s_mayKeep.TryGetValue(sol, out memo!)) s_mayKeep.Add(sol, memo = new()); }
        lock (memo) return MayKeep(sol, m, memo);
    }

    static readonly ConditionalWeakTable<ProgramSolution, Dictionary<MethodDesc, bool>> s_mayKeep = new();

    static bool MayKeep(ProgramSolution sol, MethodDesc m, Dictionary<MethodDesc, bool> memo)
    {
        if (memo.TryGetValue(m, out bool r)) return r;
        if (!ReturnsAddress(m) || sol.Summary(m) is not { } s) return memo[m] = false;
        memo[m] = false;
        // (or it has an owned variant: a parameter handed over is the function's alone, and a span over it needs a keeper)
        r = s.CallsUnknown || s.Constructs.Any(t => t is not DefType { IsByRefLike: true })
            || Signatures.VariantsOf(sol, m).Any(v => v.Kind == VariantKind.Owned)
            || s.Calls.Concat(s.Intrinsics).Any(c => CountedResult(c) || ReturnsAddress(c) && MayKeep(sol, c, memo))
            || s.Slots.Any(c => CountedResult(c) || ReturnsAddress(c) && sol.Targets(c).Any(t => MayKeep(sol, t, memo)));
        return memo[m] = r;
    }

    static bool CountedResult(MethodDesc c)
    {
        if (c.OwningType is MetadataType u && u.GetName() == "Unsafe" && u.GetNamespace() == "System.Runtime.CompilerServices") return false;
        try { return c.Signature.ReturnType is { } t && !t.IsVoid && Kinds.Counted(t); } catch { return true; }
    }

    /// <summary>A call after which the caller takes a keeper: its result is an address and some target may keep (a
    /// target not known here, as through calli or a delegate, is taken after too: a take matches nothing when
    /// nothing was held).</summary>
    internal static bool NeedsTake(PlanContext ctx, Node n)
    {
        if (n.Op is not (Opc.Call or Opc.CallI) || IsInserted(n)) return false;
        if (n.Kind != VK.Ptr && !(n.Kind == VK.Struct && n.Type is DefType { IsByRefLike: true })) return false;
        if (n.Kind == VK.Ptr && n.Type is not ByRefType) return false;
        if (n.Op == Opc.CallI) return true;
        var m = (MethodDesc)n.X!;
        if (m.OwningType.IsDelegate && m.GetName() == "Invoke") return true;
        if (n.Sub == (byte)CallKind.Direct) return MayKeep(ctx.Solution, m);
        var targets = ctx.Solution.CallTargets(n);
        if (targets.Count == 0) return true;
        foreach (var t in targets) if (MayKeep(ctx.Solution, t)) return true;
        return false;
    }

    /// <summary>The address a keeper take reads: the result itself, or a byref-like struct's first address field.</summary>
    internal static FieldDesc? TakeField(Node n)
    {
        if (n.Kind == VK.Ptr) return null;
        foreach (var f in ((DefType)n.Type!).GetFields()) if (!f.IsStatic && Kinds.Of(f.FieldType) == VK.Ptr) return f;
        return null;
    }

    internal static bool IsGcKeepAlive(Node n) => n.Op == Opc.Call && n.X is MethodDesc m && m.GetName() == "KeepAlive"
        && m.OwningType is MetadataType t && t.GetName() == "GC" && t.GetNamespace() == "System";

    // ------------------------------------------------------------------ the planner

    sealed class Planner
    {
        readonly PlanContext _ctx;
        readonly Body _body;
        readonly Dictionary<Node, OwnKind> _kind = new();
        readonly Dictionary<Node, BorrowPath> _path = new();
        readonly Dictionary<int, Node> _byId = new();
        readonly Dictionary<Node, List<Node>> _bases = new();
        readonly Dictionary<Block, HashSet<Node>> _liveIn = new(), _liveOut = new();
        readonly HashSet<(Node, int)> _phiMoves = [];
        readonly Dictionary<Node, Variant> _variant = new();
        readonly Dictionary<Node, ArgConv[]> _args = new();
        readonly Dictionary<Node, RcCategory> _cat = new();
        readonly HashSet<Node> _keeps = [];
        ParamConv[]? _conv;
        (MethodDesc Hold, MethodDesc Take)? _keeper;
        bool _mayKeep;

        public Planner(PlanContext ctx) { _ctx = ctx; _body = ctx.Body; }

        public MethodPlan Run()
        {
            var info = Ownership.Analyze(_body);
            var borrows = Borrows.Analyze(_ctx, info);
            _conv = ConventionOf(_ctx.Solution, _ctx.Method, _ctx.Entry);
            _keeper = KeeperCalls(_ctx.Method.Context);
            _mayKeep = _keeper != null && MayKeep(_ctx.Solution, _ctx.Method);
            foreach (var b in _body.Blocks) { foreach (var n in b.Phis) _byId[n.Id] = n; foreach (var n in b.Code) _byId[n.Id] = n; }
            Kinds(info, borrows);
            Variants(info);
            Validate();
            InsertTakes();
            FindBases();
            Liveness();
            foreach (var b in _body.Blocks) Insert(b);
            EntryRetains();
            return Result();
        }

        OwnKind KindOf(Node n) => _kind.TryGetValue(n, out var k) ? k : IsTrivial(n) ? OwnKind.Trivial : OwnKind.Owned;
        bool Owned(Node n) => IsRef(n) && KindOf(n) == OwnKind.Owned;

        // ---- kinds

        void Kinds(Ownership.Info info, BorrowFacts borrows)
        {
            foreach (var v in info.RefValues)
            {
                if (IsTrivial(v)) { _kind[v] = OwnKind.Trivial; continue; }
                if (v.Op == Opc.Arg)
                {
                    int i = (int)v.I;
                    if (_conv != null && i < _conv.Length && _conv[i] == ParamConv.Lend) { _kind[v] = OwnKind.Guaranteed; _path[v] = new BorrowPath(RootKind.Arg, i); }
                    else _kind[v] = OwnKind.Owned;
                    continue;
                }
                var f = borrows.Own[v];
                // (an inlined node: the callee plan's fact, Ir/Inline.cs; a borrow rooted in the callee's own
                // parameters or frame does not carry over, one rooted in a value of this body or a static does)
                if (f.Kind != OwnKind.Guaranteed && Inline.SourceFactOf(_body, v) is { Own: { Kind: OwnKind.Guaranteed, Path: { Root: RootKind.Val or RootKind.Static } } } sf)
                    f = sf.Own;
                // (a call's result is borrowed only in its borrowed-return entry: decided with the variant)
                if (f.Kind == OwnKind.Guaranteed && f.Path != null && CanBorrow(v) && v.Op is not (Opc.Arg or Opc.Call)) { _kind[v] = OwnKind.Guaranteed; _path[v] = f.Path; }
                else if (f.Kind == OwnKind.Guaranteed && f.Path != null && v.Op == Opc.Call && DirectSite(v)) { _kind[v] = OwnKind.Guaranteed; _path[v] = f.Path; }
                else _kind[v] = OwnKind.Owned;
            }
            // (GC.KeepAlive: its argument is held by this frame to the call; a borrow whose root another write can
            // let go is counted instead)
            foreach (var b in _body.Blocks)
                foreach (var n in b.Code)
                    if (IsGcKeepAlive(n))
                        foreach (var a in n.A)
                            if (IsRef(a) && KindOf(a) == OwnKind.Guaranteed && _path[a].Root is not RootKind.Arg) Flip(a);
            Validate();
        }

        void Flip(Node v) { _kind[v] = OwnKind.Owned; _path.Remove(v); Flips++; }

        /// <summary>Guaranteed values whose root cannot hold them flip to Owned, to a fixed point (flips only make more
        /// values Owned, which only makes more roots valid): a Val root must be an Owned value of this body; a phi is
        /// Guaranteed only over Guaranteed or Trivial inputs.</summary>
        void Validate()
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var v in _kind.Keys.OrderBy(v => v.Id).ToList())
                {
                    if (_kind[v] != OwnKind.Guaranteed || v.Op == Opc.Arg) continue;
                    var p = _path[v];
                    bool ok = p.Root switch
                    {
                        RootKind.Val => _byId.TryGetValue(p.Index, out var r) && r != v && Owned(r),
                        RootKind.Arg => _body.Args.Any(a => a.I == p.Index),
                        _ => true,
                    };
                    if (ok && v.Op == Opc.Phi) ok = v.A.All(x => !IsRef(x) || KindOf(x) is OwnKind.Guaranteed or OwnKind.Trivial);
                    if (!ok) { Flip(v); changed = true; }
                }
            }
        }

        /// <summary>The Owned values a use of x keeps alive: x itself if Owned; a Guaranteed value's root; an address's
        /// bases (and theirs).</summary>
        IEnumerable<Node> Keep(Node x)
        {
            var seen = new HashSet<Node>();
            var r = new List<Node>();
            KeepInto(x, r, seen);
            return r;
        }

        void KeepInto(Node x, List<Node> r, HashSet<Node> seen)
        {
            if (!seen.Add(x)) return;
            if (IsRef(x))
            {
                var k = KindOf(x);
                if (k == OwnKind.Owned) { r.Add(x); return; }
                if (k != OwnKind.Guaranteed || !_path.TryGetValue(x, out var p)) return;
                if (RootNode(p) is { } root) KeepInto(root, r, seen);
                return;
            }
            if (_bases.TryGetValue(x, out var bs)) foreach (var b in bs) KeepInto(b, r, seen);
        }

        Node? RootNode(BorrowPath p) => p.Root switch
        {
            RootKind.Val => _byId.GetValueOrDefault(p.Index),
            // (a parameter this entry owns is a root like a value; a lent one is the caller's)
            RootKind.Arg => _body.Args.FirstOrDefault(a => a.I == p.Index && Owned(a)),
            _ => null,
        };

        // ---- variants and the call rule (4.3, 4.4)

        void Variants(Ownership.Info info)
        {
            foreach (var b in _body.Blocks)
            {
                // (the naive last uses: a superset of the planned ones' order, enough for the mask; a value that turns
                // out to live on after the call is kept alive instead of handed over)
                var live = new HashSet<Node>(info.LiveOut[b]);
                if (b.Succs.Distinct().Count() == 1 && b.Succs[0].Phis.Count > 0)
                {
                    int pi = b.Succs[0].Preds.IndexOf(b);
                    foreach (var phi in b.Succs[0].Phis) { var v = phi.A[pi]; if (IsRef(v)) live.Add(v); else if (info.Bases.TryGetValue(v, out var pb)) live.UnionWith(pb); }
                }
                for (int i = b.Code.Count - 1; i >= 0; i--)
                {
                    var n = b.Code[i];
                    live.Remove(n);
                    if (n.Op is Opc.Call or Opc.NewObj or Opc.CallI) Call(n, live);
                    foreach (var a in n.A) { if (IsRef(a)) live.Add(a); else if (info.Bases.TryGetValue(a, out var ab)) live.UnionWith(ab); }
                }
            }
        }

        void Call(Node n, HashSet<Node> liveAfter)
        {
            var v = Variant.Entry;
            // (a callee with no variants is called at its entry: the call rule is not asked, as it reads the solved facts)
            if (DirectSite(n) && _ctx.Solution.Covers((MethodDesc)n.X!) && Signatures.VariantsOf(_ctx.Solution, (MethodDesc)n.X!).Count > 0)
            {
                uint mask = 0;
                // (the Owned values another operand keeps, through a borrow or an address: not handed over here)
                var through = new HashSet<Node>();
                foreach (var a in n.A) foreach (var kv in Keep(a)) if (kv != a) through.Add(kv);
                var seen = new HashSet<Node>();
                for (int k = 0; k < n.A.Length; k++)
                {
                    var a = n.A[k];
                    if (!IsRef(a)) continue;
                    int p = ParamOf(n, k);
                    if (p >= 32) continue;
                    // (only a count the caller holds, once: not a borrow, a field's or an element's load, a literal)
                    bool owned = Owned(a) && !IsTrivial(a) && a.Op is not (Opc.LdFld or Opc.LdSFld or Opc.LdElem or Opc.LdInd or Opc.LdObj) && !through.Contains(a) && seen.Add(a);
                    if (Signatures.CallRule(_ctx, n, k, !liveAfter.Contains(a), owned) == ArgConv.HandOver) mask |= 1u << p;
                }
                v = Signatures.ChooseVariant(_ctx, n, mask);
                // (an owned entry for parameters that are not there, or a callee outside the solution: the entry)
                if (v.Kind == VariantKind.Owned && !_ctx.Solution.Covers((MethodDesc)n.X!)) v = Variant.Entry;
            }
            // (a borrowed result needs the borrowed-return entry, and that entry needs a borrowed result)
            if (n.HasValue && IsRef(n) && n.Op == Opc.Call)
            {
                bool borrowed = KindOf(n) == OwnKind.Guaranteed;
                if (borrowed && v.Kind != VariantKind.BorrowReturn) Flip(n);
                else if (!borrowed && v.Kind == VariantKind.BorrowReturn) v = Variant.Entry;
            }
            else if (v.Kind == VariantKind.BorrowReturn) v = Variant.Entry;
            _variant[n] = v;
        }

        // ---- keepers, caller side (7.4)

        void InsertTakes()
        {
            if (_keeper is not { } kc) return;
            foreach (var b in _body.Blocks)
                for (int i = 0; i < b.Code.Count; i++)
                {
                    var n = b.Code[i];
                    if (!NeedsTake(_ctx, n)) continue;
                    Node addr = n;
                    if (n.Kind == VK.Struct)
                    {
                        if (TakeField(n) is not { } f) continue;
                        addr = new Node { Op = Opc.LdFld, Kind = VK.Ptr, SType = StackType.ByRef, Type = f.FieldType, X = f, A = [n], Block = b, Id = _body.NextId() };
                        MarkInserted(addr);
                        b.Code.Insert(++i, addr);
                    }
                    var t = new Node
                    {
                        Op = Opc.Call, Sub = (byte)CallKind.Direct, Kind = VK.Ref, SType = StackType.ObjRef, Type = _ctx.Method.Context.GetWellKnownType(WellKnownType.Object),
                        X = kc.Take, A = [addr], Block = b, Id = _body.NextId(),
                    };
                    MarkInserted(t);
                    b.Code.Insert(++i, t);
                    _byId[t.Id] = t; _byId[addr.Id] = addr;
                    _kind[t] = OwnKind.Owned;
                    _variant[t] = Variant.Entry;
                    (_takes ??= new())[n] = t;
                    Takes++;
                }
        }

        Dictionary<Node, Node>? _takes;

        /// <summary>A keeper's take is a base of its call's result, but not at the take itself or at the read of the
        /// result's address it takes with (those come before it is made); nor is any value at its own definition.</summary>
        bool Skip(Node k, Node user) => k == user || IsInserted(user) && IsInserted(k) && k.X == _keeper?.Take;

        // ---- bases (Ownership.FindBases, with phis followed and keepers added)

        void FindBases()
        {
            var varBases = new Dictionary<int, HashSet<Node>>();
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var b in _body.Blocks)
                {
                    // (7.3: a phi of addresses has its inputs' bases)
                    foreach (var phi in b.Phis)
                    {
                        if (IsRef(phi)) continue;
                        List<Node>? l = null;
                        foreach (var a in phi.A) if (_bases.TryGetValue(a, out var ab)) (l ??= []).AddRange(ab);
                        if (l != null) changed |= SetBases(phi, l);
                    }
                    foreach (var n in b.Code)
                    {
                        List<Node>? l = null;
                        if (n.Op == Opc.VarSt && n.A.Length > 0 && _bases.TryGetValue(n.A[0], out var sb))
                        {
                            if (!varBases.TryGetValue((int)n.I, out var set)) varBases[(int)n.I] = set = [];
                            foreach (var x in sb) changed |= set.Add(x);
                            continue;
                        }
                        if (n.Op is Opc.LocA or Opc.VarLd) { if (varBases.TryGetValue((int)n.I, out var vb)) l = vb.OrderBy(x => x.Id).ToList(); }
                        else if (Ownership.Derives(n) && !IsInserted(n))
                            foreach (var a in n.A)
                            {
                                // (a struct with references is a base too: a span made from an ArraySegment points into
                                // the array the segment holds; the naive plan followed references only)
                                if (IsRef(a) && !IsTrivial(a)) (l ??= []).Add(a);
                                else if (_bases.TryGetValue(a, out var ab)) (l ??= []).AddRange(ab);
                            }
                        if (_takes != null && _takes.TryGetValue(n, out var t)) (l ??= []).Add(t);
                        if (l == null) continue;
                        changed |= SetBases(n, l);
                    }
                }
            }
        }

        bool SetBases(Node n, List<Node> l)
        {
            var d = l.Distinct().ToList();
            if (_bases.TryGetValue(n, out var old) && old.Count == d.Count && old.ToHashSet().SetEquals(d)) return false;
            if (old != null) d = old.Concat(d).Distinct().ToList();
            _bases[n] = d;
            return true;
        }

        // ---- liveness (Ownership.Liveness over the Owned values, through Keep)

        void Liveness()
        {
            var ends = Ownership.FinallyEnds(_body);
            foreach (var b in _body.Blocks) { _liveIn[b] = []; _liveOut[b] = []; }
            var exc = _body.Blocks.ToDictionary(b => b, b => Ownership.ExceptionSuccs(b).ToList());
            var keepOf = new Dictionary<Node, List<Node>>();
            List<Node> K(Node a) => keepOf.TryGetValue(a, out var l) ? l : keepOf[a] = Keep(a).ToList();
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int bi = _body.Blocks.Count - 1; bi >= 0; bi--)
                {
                    var b = _body.Blocks[bi];
                    var outSet = new HashSet<Node>();
                    foreach (var s in b.Succs.Concat(ends.GetValueOrDefault(b) ?? []).Concat(exc[b]))
                    {
                        foreach (var v in _liveIn[s]) outSet.Add(v);
                        if (b.Succs.Contains(s))
                        {
                            int pi = s.Preds.IndexOf(b);
                            if (pi >= 0) foreach (var phi in s.Phis) foreach (var k in K(phi.A[pi])) outSet.Add(k);
                        }
                    }
                    var live = new HashSet<Node>(outSet);
                    for (int i = b.Code.Count - 1; i >= 0; i--)
                    {
                        var n = b.Code[i];
                        live.Remove(n);
                        foreach (var a in n.A) foreach (var k in K(a)) if (!Skip(k, n)) live.Add(k);
                    }
                    foreach (var phi in b.Phis) live.Remove(phi);
                    if (!outSet.SetEquals(_liveOut[b])) { _liveOut[b] = outSet; changed = true; }
                    if (!live.SetEquals(_liveIn[b])) { _liveIn[b] = live; changed = true; }
                }
            }
        }

        // ---- insertion (Ownership.Insert, by kind)

        Node Count(Opc op, Node v, Block b, RcCategory c)
        {
            var n = new Node { Op = op, Kind = VK.Void, A = [v], Block = b, Id = _body.NextId(), ILOffset = -1 };
            _cat[n] = c;
            return n;
        }

        /// <summary>Operand j of n takes a count (a store of it); a return in a borrowed-return entry takes none.</summary>
        bool Consumes(Node n, int j) => Ownership.Consumes(n, j) && !(n.Op == Opc.Ret && _ctx.Entry.Kind == VariantKind.BorrowReturn);

        void Insert(Block b)
        {
            var live = new HashSet<Node>(_liveOut[b]);
            if (b.Succs.Distinct().Count() == 1 && b.Succs[0].Phis.Count > 0)
            {
                var s = b.Succs[0];
                int pi = s.Preds.IndexOf(b);
                var liveOn = new HashSet<Node>(_liveIn[s]);
                foreach (var phi in s.Phis)
                {
                    var v = phi.A[pi];
                    if (!IsRef(v) || !Owned(v) || !Owned(phi)) continue;
                    if (!liveOn.Contains(v)) { _phiMoves.Add((phi, pi)); liveOn.Add(v); }
                }
                // (what a phi copies lives through the copy: a value counted again is released at the successor's
                // entry if nothing else needs it there; a moved one is consumed by the copy)
                foreach (var phi in s.Phis) live.UnionWith(Keep(phi.A[pi]));
            }
            var code = b.Code;
            var output = new List<Node>(code.Count * 2);
            for (int i = code.Count - 1; i >= 0; i--)
            {
                var n = code[i];
                var after = new List<Node>();
                var before = new List<Node>();
                if (Owned(n) && !live.Contains(n) && !n.IsTerminator)
                    after.Add(Count(Opc.Release, n, b, n.Op is Opc.Call or Opc.NewObj or Opc.CallI ? RcCategory.Result : RcCategory.Generic));
                live.Remove(n);
                bool call = n.Op is Opc.Call or Opc.NewObj or Opc.CallI && !IsInserted(n);
                var variant = _variant.GetValueOrDefault(n);
                ArgConv[]? conv = call ? new ArgConv[n.A.Length] : null;
                // (the Owned values the operands keep through a borrow or an address: not moved at this operation)
                var through = new HashSet<Node>();
                foreach (var a in n.A) foreach (var k in Keep(a)) if (k != a && !Skip(k, n)) through.Add(k);
                List<Node>? kept = null;
                var seen = new HashSet<Node>();
                for (int k = 0; k < n.A.Length; k++)
                {
                    var v = n.A[k];
                    if (!IsRef(v) || !seen.Add(v)) continue;
                    var kind = KindOf(v);
                    if (kind == OwnKind.Trivial)
                    {
                        // (nothing to count: a store of a literal or null takes no retain)
                        for (int j = k; j < n.A.Length; j++) if (n.A[j] == v && Consumes(n, j)) n.Moves |= 1u << j;
                        continue;
                    }
                    bool lastUse = kind == OwnKind.Owned && !live.Contains(v);
                    // (kept through another operand, a borrow or an address of it: released after, never moved in)
                    bool movable = lastUse && !through.Contains(v);
                    bool moved = false;
                    for (int j = k; j < n.A.Length; j++)
                    {
                        if (n.A[j] != v) continue;
                        if (call && conv != null)
                        {
                            bool owns = DirectSite(n) && Owns(variant, ParamOf(n, j));
                            if (owns && movable && !moved) { conv[j] = ArgConv.HandOver; n.Moves |= 1u << j; moved = true; }
                            else conv[j] = owns ? ArgConv.KeepAlive : ArgConv.Lend;
                            continue;
                        }
                        if (!Consumes(n, j) || kind != OwnKind.Owned) continue;
                        if (movable && !moved) { n.Moves |= 1u << j; moved = true; }
                    }
                    if (lastUse && !moved)
                    {
                        // (a return in a borrowed-return entry: the value must be a borrow; an Owned one cannot be
                        // released before the return that reads it, so it is left to the verifier to reject)
                        if (n.Op == Opc.Ret) _keeps.Add(v);
                        else after.Add(Count(Opc.Release, v, b, call ? RcCategory.ArgAfterCall : RcCategory.Generic));
                    }
                    // (a borrow's root is not live yet here: the loop below gives it its release at this, its last use)
                    if (kind == OwnKind.Owned) live.Add(v);
                }
                // (a borrow's root and an address's objects: their last use is this one too)
                for (int k = 0; k < n.A.Length; k++)
                {
                    var a = n.A[k];
                    foreach (var v in Keep(a))
                    {
                        // (a keeper taken after a call is a base of the call's result, but not of its own take)
                        if (v == a || Skip(v, n) || !seen.Add(v)) continue;
                        if (!live.Contains(v))
                        {
                            if (n.Op == Opc.Ret && !IsRef(a) && KeepCase(v, a)) (kept ??= []).Add(v);
                            else (n.IsTerminator ? before : after).Add(Count(Opc.Release, v, b, RcCategory.Generic));
                        }
                        live.Add(v);
                    }
                }
                if (kept != null) Keeper(n, kept, b, before);
                if (conv != null) _args[n] = conv;
                after.Reverse();
                output.AddRange(after);
                output.Add(n);
                before.Reverse();
                output.AddRange(before);
            }
            // at the block's entry: what a predecessor (or, for a handler, its try) may hold that is dead here
            var dead = new HashSet<Node>();
            foreach (var p in b.Preds) foreach (var v in _liveOut[p]) dead.Add(v);
            bool handler = false;
            if (b.Preds.Count == 0 && b != _body.Blocks[0])
                foreach (var r in _body.Regions)
                    if (r.Entry == b && (r.IsHandler || r.Kind == RegionKind.Filter))
                    {
                        handler = true;
                        foreach (var tb in Ownership.BlocksWithin(r.Try!).Concat(r.Filter != null && r.Filter != r ? Ownership.BlocksWithin(r.Filter) : []))
                        {
                            foreach (var v in _liveIn[tb]) dead.Add(v);
                            foreach (var n in tb.Phis.Concat(tb.Code)) if (Owned(n)) dead.Add(n);
                        }
                    }
            dead.ExceptWith(_liveIn[b]);
            foreach (var phi in b.Phis) dead.Remove(phi);
            // (a phi nothing uses: its count, moved or copied in, goes at once; the naive plan left it, a leak per pass)
            foreach (var phi in b.Phis) if (Owned(phi) && !live.Contains(phi)) dead.Add(phi);
            foreach (var v in dead.OrderBy(v => v.Id)) output.Add(Count(Opc.Release, v, b, handler ? RcCategory.Unwind : RcCategory.Generic));
            output.Reverse();
            // (a handler's caught value comes first, before what its entry releases)
            int cv = output.FindIndex(n => n.Op == Opc.CatchVal);
            if (cv > 0) { var c = output[cv]; output.RemoveAt(cv); output.Insert(0, c); }
            code.Clear();
            code.AddRange(output);
        }

        /// <summary>A returned address's object that only this function holds (it made it, a call gave it, the caller
        /// handed it over): released here, the caller would get freed memory (Ownership.cs's keep).</summary>
        bool KeepCase(Node v, Node addr)
        {
            if (addr.Kind == VK.Ptr && addr.Type is not ByRefType) return false;
            // (a parameter handed over is this function's alone; one counted on entry, the naive convention, its caller holds too)
            if (v.Op == Opc.Arg) return _conv != null;
            return !HeldElsewhere(v, new HashSet<Node>());
        }

        static bool HeldElsewhere(Node v, HashSet<Node> seen)
        {
            if (Ownership.HeldElsewhere(v)) return true;
            if (v.Op == Opc.Phi && seen.Add(v)) return v.A.All(a => !IsRef(a) || IsTrivial(a) || HeldElsewhere(a, seen));
            return false;
        }

        /// <summary>The keeper (7.4): the object the returned address points into goes to the caller (Rt.ReturnKeepers.Hold
        /// counts it, found from the address itself, until the caller's ReturnKeepers.Take), then this function's own counts go
        /// as usual: one hold per return, whichever of the bases it turns out to be; where the protocol does not reach,
        /// the naive keep.</summary>
        void Keeper(Node ret, List<Node> kept, Block b, List<Node> before)
        {
            var a = ret.A[0];
            FieldDesc? f = a.Kind == VK.Struct ? TakeField(a) : null;
            if (!_mayKeep || _keeper is not { } kc || a.Kind == VK.Struct && f == null)
            {
                foreach (var v in kept) { _keeps.Add(v); MissedKeepers++; }
                if (s_stats) lock (s_missed) s_missed.Add(_ctx.Method + (_keeper == null ? " (no keeper calls)" : !_mayKeep ? " (not MayKeep)" : " (no address field)"));
                return;
            }
            // (in the order they run, before the return: the hold, then the releases)
            Node addr = a;
            var hold = new List<Node>();
            if (f != null)
            {
                addr = new Node { Op = Opc.LdFld, Kind = VK.Ptr, SType = StackType.ByRef, Type = f.FieldType, X = f, A = [a], Block = b, Id = _body.NextId() };
                MarkInserted(addr);
                hold.Add(addr);
            }
            var h = new Node { Op = Opc.Call, Sub = (byte)CallKind.Direct, Kind = VK.Void, X = kc.Hold, A = [addr], Block = b, Id = _body.NextId(), ILOffset = -1 };
            MarkInserted(h);
            hold.Add(h);
            before.InsertRange(0, hold);
            foreach (var v in kept) before.Add(Count(Opc.Release, v, b, RcCategory.Generic));
            Keepers++;
        }

        void EntryRetains()
        {
            // (a method outside the solution: its parameters are counted on entry, right after each one)
            if (_conv != null) return;
            var entry = _body.Blocks[0];
            foreach (var a in _body.Args)
                if (IsRef(a) && Owned(a)) entry.Code.Insert(entry.Code.IndexOf(a) + 1, Count(Opc.Retain, a, entry, RcCategory.Naive));
        }

        MethodPlan Result()
        {
            var counted = new List<Node>();
            foreach (var b in _body.Blocks)
            {
                foreach (var n in b.Phis) if (IsRef(n) && KindOf(n) != OwnKind.Guaranteed) counted.Add(n);
                foreach (var n in b.Code) if (IsRef(n) && KindOf(n) != OwnKind.Guaranteed) counted.Add(n);
            }
            var plan = new MethodPlan(_body, counted, _phiMoves, _bases) { Entry = _ctx.Entry };
            foreach (var (n, k) in _kind)
                plan.Own[n] = k switch { OwnKind.Guaranteed => OwnFact.Borrowed(_path[n]), OwnKind.Trivial => OwnFact.Trivial, _ => OwnFact.Owned };
            foreach (var (n, c) in _args) plan.Args[n] = c;
            foreach (var (n, v) in _variant) if (v != Variant.Entry) plan.Callees[n] = v;
            foreach (var (n, c) in _cat) plan.Categories[n] = c;
            if (_keeps.Count > 0) KnownKeeps.AddOrUpdate(_body, _keeps);
            else KnownKeeps.Remove(_body);
            return plan;
        }
    }
}
