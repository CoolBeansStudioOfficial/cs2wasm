#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>A plan that breaks the pairing rules: an internal compiler error naming the method, the value and the path.</summary>
public sealed class PlanVerifyException(string message) : Exception(message);

/// <summary>The ownership pairing verifier (docs/MEMORY-IR.md 8.1): after Plan.Build and after each inline and Canon
/// step; always in a Debug build, else with IL2WASM_VERIFY=1. S1 agent c's file (with Plan.cs).
///
/// It runs the planned body forward, with each Owned value's state at each point: held on every path, on some, or on
/// none (a release or a move clears the value's local, so a release of a value that may not hold a count is a no-op:
/// the plan's block-entry and handler-entry releases rely on it). Exceptional edges carry every state inside a try to
/// its handlers; a leave's own edge stands for the path through its finally. The checks:
/// 1. an Owned value holds no count at a return (none left on any path: a leak), except the naive keeps the plan
///    records; every Owned value is in CountedValues, which an exception leaving the function releases;
/// 2. every use of an Owned value is on a path where it holds its count (no use after its consuming use); a value is
///    not made again while it may still hold a count (a count lost round a loop);
/// 3. every use of a Guaranteed(Val) value is where its root holds its count; a Guaranteed value is never released
///    or moved;
/// 5. every use of an address is where its bases hold their counts;
/// 6. Moves bits only on consuming positions, or a call argument handed over;
/// 7. a Guaranteed phi's inputs are Guaranteed or Trivial;
/// 8. a call's argument conventions match the variant it calls (HandOver or KeepAlive exactly at the variant's owned
///    parameters, HandOver with its Moves bit); only direct sites call variants;
/// 9. in a build without threads, no Atomic count, load or access kinds;
/// and every call to a target that may keep (7.4) is followed by its Rt.ReturnKeepers.Take (after the releases that follow the
/// call, nothing else between). (Check 4, a flip's retain at the
/// definition, is lowering's: a flipped load is an Owned load, which lowering counts.)</summary>
public static class Verify
{
    /// <summary>Checks 8.1's rules on the planned body; throws <see cref="PlanVerifyException"/> on the first
    /// failure.</summary>
    public static void Check(PlanContext ctx, MethodPlan plan) => new Checker(ctx, plan).Run();

    /// <summary>Plans checked and failures found (all plans of the process), for the report.</summary>
    public static int Checked;

    sealed class Checker
    {
        readonly PlanContext _ctx;
        readonly MethodPlan _plan;
        readonly Body _body;
        readonly Dictionary<Node, int> _index = new();
        readonly List<Node> _owned = [];
        readonly Dictionary<int, Node> _byId = new();
        readonly HashSet<Node> _keeps;
        readonly HashSet<Node> _naiveArgs = [];
        int _words;

        public Checker(PlanContext ctx, MethodPlan plan)
        {
            _ctx = ctx; _plan = plan; _body = plan.Body;
            _keeps = Plan.KnownKeeps.TryGetValue(_body, out var k) ? k : [];
        }

        PlanVerifyException Fail(string what, Node? v = null, Block? at = null) =>
            new($"plan verifier: {_ctx.Method}: {what}" + (v != null ? $" (value {Body.Format(v)})" : "") + (at != null ? $" in {at}" : "")
               + (s_dump ? Environment.NewLine + _body.Dump() + "borrows:" + string.Concat(_body.Blocks.SelectMany(x => x.Phis.Concat(x.Code)).Where(x => _plan.Own[x].Kind == OwnKind.Guaranteed).Select(x => " " + x + "=" + _plan.Own[x].Path)) : ""));

        /// <summary>IL2WASM_VERIFY_DUMP=1: a failure prints the planned body.</summary>
        static readonly bool s_dump = Environment.GetEnvironmentVariable("IL2WASM_VERIFY_DUMP") == "1";

        OwnKind Kind(Node n) => _plan.Own[n].Kind;
        bool Owned(Node n) => Plan.IsRef(n) && Kind(n) == OwnKind.Owned && !Plan.IsTrivial(n);

        public void Run()
        {
            Checked++;
            foreach (var b in _body.Blocks)
                foreach (var n in b.Phis.Concat(b.Code))
                {
                    _byId[n.Id] = n;
                    if (Owned(n)) { _index[n] = _owned.Count; _owned.Add(n); }
                }
            _words = (_owned.Count + 63) / 64;
            // (a parameter counted on entry, the naive convention: it holds its count from its Retain on)
            var entry = _body.Blocks[0];
            foreach (var n in entry.Code) if (n.Op == Opc.Retain && n.A[0].Op == Opc.Arg) _naiveArgs.Add(n.A[0]);
            var counted = _plan.CountedValues.ToHashSet();
            foreach (var v in _owned) if (!counted.Contains(v)) throw Fail("an Owned value is not in CountedValues (an exception would leave its count)", v);
            Static();
            Flow();
        }

        // ---- the checks that need no paths

        void Static()
        {
            bool threaded = _ctx.Threaded;
            foreach (var b in _body.Blocks)
            {
                foreach (var phi in b.Phis)
                {
                    if (!Plan.IsRef(phi) || Kind(phi) != OwnKind.Guaranteed) continue;
                    foreach (var a in phi.A)
                        if (Plan.IsRef(a) && Kind(a) == OwnKind.Owned && !Plan.IsTrivial(a)) throw Fail("a Guaranteed phi has an Owned input (check 7)", phi, b);
                }
                for (int i = 0; i < b.Code.Count; i++)
                {
                    var n = b.Code[i];
                    if (n.Op is Opc.Retain or Opc.Release)
                    {
                        if (Plan.IsRef(n.A[0]) && Kind(n.A[0]) == OwnKind.Guaranteed) throw Fail("a Guaranteed value is counted (check 3)", n.A[0], b);
                        continue;
                    }
                    var args = _plan.Args[n];
                    bool call = n.Op is Opc.Call or Opc.NewObj or Opc.CallI && !Plan.IsInserted(n);
                    var v = _plan.Callees[n];
                    if (v != Variant.Entry && !Plan.DirectSite(n)) throw Fail("a variant at a site that is not direct (check 8)", n, b);
                    for (int j = 0; j < n.A.Length; j++)
                    {
                        bool moved = (n.Moves >> j & 1) != 0;
                        var a = n.A[j];
                        if (moved && Plan.IsRef(a) && Kind(a) == OwnKind.Guaranteed) throw Fail("a Guaranteed value is moved (check 3)", a, b);
                        if (call)
                        {
                            var c = args != null && j < args.Length ? args[j] : ArgConv.Lend;
                            if (moved != (c == ArgConv.HandOver)) throw Fail($"argument {j}: a Moves bit without a hand-over, or a hand-over without it (check 6)", n, b);
                            if (Plan.IsRef(a) && !Plan.IsTrivial(a))
                            {
                                bool owns = Plan.DirectSite(n) && Plan.Owns(v, Plan.ParamOf(n, j));
                                if (owns != (c is ArgConv.HandOver or ArgConv.KeepAlive)) throw Fail($"argument {j}: its convention {c} does not match the variant {v} (check 8)", n, b);
                            }
                        }
                        else if (moved && !Ownership.Consumes(n, j)) throw Fail($"operand {j} moved where it is not consumed (check 6)", n, b);
                        else if (n.Op == Opc.Ret && _ctx.Entry.Kind == VariantKind.BorrowReturn && Plan.IsRef(a) && !Plan.IsTrivial(a) && Kind(a) == OwnKind.Owned)
                            throw Fail("a borrowed-return entry returns an Owned value (its result must be a borrow of a parameter's path)", a, b);
                    }
                    if (!threaded && (_plan.Counts[n] == CountKind.Atomic || _plan.Loads[n] == LoadKind.LoadRetain || _plan.Accesses[n] == AccessKind.Atomic))
                        throw Fail("an atomic count, load or access in a build without threads (check 9)", n, b);
                    // (7.4: a call that may have held a keeper is followed by its take)
                    if (Plan.KeeperCalls(_ctx.Method.Context) is { } kc && Plan.NeedsTake(_ctx, n) && (n.Kind != VK.Struct || Plan.TakeField(n) != null))
                    {
                        int t = i + 1;
                        while (t < b.Code.Count && (b.Code[t].Op is Opc.Release or Opc.Retain || b.Code[t].Op == Opc.LdFld && Plan.IsInserted(b.Code[t]))) t++;
                        if (t >= b.Code.Count || b.Code[t].X != kc.Take || !Plan.IsInserted(b.Code[t])) throw Fail("a call that may return a keeper is not followed by Rt.ReturnKeepers.Take (7.4)", n, b);
                    }
                }
            }
        }

        // ---- the paths

        /// <summary>A state: may (held on some path) and must (held on every path), one bit per Owned value.</summary>
        sealed class State
        {
            public ulong[] May, Must;
            public State(int words, bool top) { May = new ulong[words]; Must = new ulong[words]; if (top) Array.Fill(Must, ~0UL); }
            public State Copy() { var s = new State(May.Length, false); May.CopyTo(s.May, 0); Must.CopyTo(s.Must, 0); return s; }
            /// <summary>Joins o in; true when this changed.</summary>
            public bool Join(State o)
            {
                bool c = false;
                for (int i = 0; i < May.Length; i++)
                {
                    ulong m = May[i] | o.May[i], u = Must[i] & o.Must[i];
                    if (m != May[i] || u != Must[i]) { May[i] = m; Must[i] = u; c = true; }
                }
                return c;
            }
            public bool MayHold(int i) => (May[i >> 6] >> (i & 63) & 1) != 0;
            public bool MustHold(int i) => (Must[i >> 6] >> (i & 63) & 1) != 0;
            public void Set(int i, bool held)
            {
                ulong bit = 1UL << (i & 63);
                if (held) { May[i >> 6] |= bit; Must[i >> 6] |= bit; } else { May[i >> 6] &= ~bit; Must[i >> 6] &= ~bit; }
            }
        }

        void Flow()
        {
            var ins = new Dictionary<Block, State>();
            // (the entry: nothing held; a block not reached yet is the top, which every join narrows)
            var work = new Queue<Block>();
            var queued = new HashSet<Block>();
            ins[_body.Blocks[0]] = new State(_words, false);
            work.Enqueue(_body.Blocks[0]); queued.Add(_body.Blocks[0]);
            void Flow(Block to, State s)
            {
                if (!ins.TryGetValue(to, out var cur)) { ins[to] = s.Copy(); if (queued.Add(to)) work.Enqueue(to); return; }
                if (cur.Join(s) && queued.Add(to)) work.Enqueue(to);
            }
            int steps = 0;
            while (work.Count > 0)
            {
                if (++steps > 200_000) throw Fail("the verifier's flow did not settle");
                var b = work.Dequeue();
                queued.Remove(b);
                var s = ins[b].Copy();
                var exc = Ownership.ExceptionSuccs(b).ToList();
                Run(b, s, exc.Count > 0 ? st => { foreach (var h in exc) Flow(h, st); } : null, check: false);
                foreach (var t in b.Succs.Distinct()) Flow(t, Edge(b, t, s, check: false));
                // (a finally's end is not followed to its leave targets: the leave's own edge carries that path's
                // state, and the finally's entry state, every state of its try, is not that path's. What a finally
                // lets go is dead at its leave targets by the plan's liveness, which follows the finally's end there)
            }
            // (the checks, on the settled states)
            foreach (var b in _body.Blocks)
            {
                if (!ins.TryGetValue(b, out var st)) continue;
                var s = st.Copy();
                Run(b, s, null, check: true);
                foreach (var t in b.Succs.Distinct()) Edge(b, t, s, check: true);
            }
        }

        /// <summary>The phi copies on the edge b to t: a moved input gives its count to the phi; the phis hold theirs.</summary>
        State Edge(Block b, Block t, State s, bool check)
        {
            if (t.Phis.Count == 0) return s;
            var o = s.Copy();
            int pi = t.Preds.IndexOf(b);
            if (pi < 0) return o;
            foreach (var phi in t.Phis)
            {
                var v = phi.A[pi];
                if (check) Use(v, s, b, phi);
                if (_plan.PhiMove(phi, pi) && _index.TryGetValue(v, out int i))
                {
                    if (check && !Owned(phi)) throw Fail("a count moved into a phi that holds none", phi, b);
                    o.Set(i, false);
                }
            }
            foreach (var phi in t.Phis) if (_index.TryGetValue(phi, out int i)) o.Set(i, true);
            return o;
        }

        void Run(Block b, State s, Action<State>? exceptional, bool check)
        {
            exceptional?.Invoke(s);
            foreach (var n in b.Code)
            {
                switch (n.Op)
                {
                    case Opc.Release:
                        if (_index.TryGetValue(n.A[0], out int r)) s.Set(r, false);
                        break;
                    case Opc.Retain:
                        if (_index.TryGetValue(n.A[0], out int q))
                        {
                            if (check && !_naiveArgs.Contains(n.A[0])) throw Fail("a Retain of an Owned value besides a naive parameter's", n.A[0], b);
                            s.Set(q, true);
                        }
                        break;
                    default:
                        if (check) foreach (var a in n.A) Use(a, s, b, n);
                        for (int j = 0; j < n.A.Length; j++)
                            if ((n.Moves >> j & 1) != 0 && _index.TryGetValue(n.A[j], out int m)) s.Set(m, false);
                        if (_index.TryGetValue(n, out int d) && !(n.Op == Opc.Arg && _naiveArgs.Contains(n)))
                        {
                            if (check && s.MayHold(d)) throw Fail("a value is made again while it may still hold its count (check 2: a count lost round a loop)", n, b);
                            s.Set(d, true);
                        }
                        if (check && n.Op == Opc.Ret)
                            for (int i = 0; i < _owned.Count; i++)
                                if (s.MayHold(i) && !_keeps.Contains(_owned[i])) throw Fail("an Owned value still holds its count at a return (check 1: a leak on some path)", _owned[i], b);
                        break;
                }
                exceptional?.Invoke(s);
            }
        }

        readonly Dictionary<Node, List<Node>> _direct = new();

        /// <summary>The bases an address has by its own derivation (an operand reference, an operand address's bases, a
        /// phi's inputs', a keeper taken after the call), without the plan's variable bases (everything ever stored in a
        /// variable, for every read of it: more than a path holds, by design).</summary>
        List<Node> DirectBases(Node x)
        {
            if (_direct.TryGetValue(x, out var l)) return l;
            _direct[x] = l = [];
            var r = new List<Node>();
            if (Plan.IsRef(x)) r.Add(x);
            else if (x.Op == Opc.Phi) foreach (var a in x.A) r.AddRange(DirectBases(a));
            else if (Ownership.Derives(x) && !Plan.IsInserted(x))
                foreach (var a in x.A) { if (Plan.IsRef(a)) { if (!Plan.IsTrivial(a)) r.Add(a); } else r.AddRange(DirectBases(a)); }
            if (_plan.Bases.TryGetValue(x, out var bs)) foreach (var t in bs) if (Plan.IsInserted(t) && t.A.Length == 1 && (t.A[0] == x || t.A[0].A.Length == 1 && t.A[0].A[0] == x)) r.Add(t);
            l.AddRange(r.Distinct());
            return l;
        }

        /// <summary>A use of a: a itself (Owned) and a borrow's root must hold their counts on every path; an address's
        /// bases on some path (the bases of a variable are everything ever stored in it, wherever it is read: a read on
        /// a path before the store keeps nothing there, and a release of a value that holds no count is a no-op).</summary>
        void Use(Node a, State s, Block b, Node user)
        {
            foreach (var (v, viaBase) in Held(a))
            {
                if (!_index.TryGetValue(v, out int i)) continue;
                // (a terminator's address operand: its objects are let go just before it, as nothing runs after it
                // here; the address it returns or branches with points into what the caller or a successor keeps, or
                // into a keeper handed on)
                if (user.IsTerminator && v != a) continue;
                if (v == user || Plan.IsInserted(user) && Plan.IsInserted(v)) continue;
                if (viaBase ? !s.MayHold(i) : !s.MustHold(i))
                    throw Fail($"used by {Body.Format(user)} where it {(viaBase ? "holds no count on any path" : "may not hold its count")} (checks 2, 3, 5)", v, b);
            }
        }

        IEnumerable<(Node, bool)> Held(Node a)
        {
            var seen = new HashSet<Node>();
            var stack = new Stack<(Node, bool)>();
            stack.Push((a, false));
            while (stack.Count > 0)
            {
                var (x, via) = stack.Pop();
                if (!seen.Add(x)) continue;
                if (Plan.IsRef(x))
                {
                    var f = _plan.Own[x];
                    if (f.Kind == OwnKind.Owned && !Plan.IsTrivial(x)) yield return (x, via);
                    else if (f.Kind == OwnKind.Guaranteed && f.Path is { } p)
                    {
                        if (p.Root == RootKind.Val && _byId.TryGetValue(p.Index, out var r)) stack.Push((r, via));
                        else if (p.Root == RootKind.Arg) foreach (var arg in _body.Args) if (arg.I == p.Index && arg != x) stack.Push((arg, via));
                    }
                    continue;
                }
                foreach (var bb in DirectBases(x)) stack.Push((bb, true));
            }
        }
    }
}
