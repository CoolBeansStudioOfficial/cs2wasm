#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>Stores whose old value is known null (docs/MEMORY-IR.md 5.3's <c>Init</c>; the old front end's
/// InitOnlyFields.ResolveInitStores and FreshStores' "an object only the code running knows"): the store needs no
/// load and no release of the old value. A store <c>o.f = v</c> qualifies when o is an object this code made and has
/// not let out of its hands, and nothing has written o.f yet on any path to the store:
/// <list type="bullet">
/// <item>o is a <c>newobj</c> of this body whose constructor keeps <c>this</c> in (stores it nowhere, passes it to
///   nothing but the constructors it hands over to, captures it nowhere): o.f is null after it unless that constructor
///   (or one it hands over to) writes f;</item>
/// <item>o is <c>this</c> of a class's instance constructor: at entry its fields are null, except those that a
///   constructor handing over to this one (<c>: this(...)</c>, a derived class's <c>: base(...)</c>) may write first.
///   A class's constructor runs on an object only through <c>newobj</c> or from another constructor on its own
///   <c>this</c> (ECMA-335 III.1.8.1.4, what every compiler emits); a constructor called on some other object is seen
///   and turns the rule off for that constructor.</item>
/// </list>
/// Along the body, forward over normal and finally-end edges (a handler starts knowing nothing): a store to o.f or an
/// address of o.f marks f written; any use of o other than as the base of a field access, a comparison, a branch or a
/// count lets it out (it is no longer tracked); a use as a phi input lets it out everywhere. A value type's
/// constructor runs on existing storage (<c>s = new S(...)</c> on a local), so it is never fresh.
/// IL2WASM_NO_INIT_STORES=1 turns the rule off.</summary>
public static class InitStores
{
    static readonly bool s_off = Environment.GetEnvironmentVariable("IL2WASM_NO_INIT_STORES") == "1";

    /// <summary>Init stores found (all plans of the process), for IL2WASM_PLAN_STATS.</summary>
    public static int Found;

    /// <summary>A constructor's effect on <c>this</c>: whether it keeps it in, and the fields (typical definitions) it may
    /// write on it, with those of the constructors it hands over to.</summary>
    sealed record CtorEffect(bool Keeps, HashSet<FieldDesc> Writes);

    sealed class Memo
    {
        public readonly Dictionary<MethodDesc, CtorEffect?> Effects = new();
        /// <summary>Fields a constructor's object may have written at its entry (null: any).</summary>
        public readonly Dictionary<MethodDesc, HashSet<FieldDesc>?> Before = new();
        public Dictionary<MethodDesc, List<MethodDesc>>? CtorCallers;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProgramSolution, Memo> s_memo = new();

    static Memo MemoOf(ProgramSolution sol) { lock (s_memo) return s_memo.GetValue(sol, _ => new Memo()); }

    static FieldDesc Key(FieldDesc f) => f.GetTypicalFieldDefinition();

    static bool IsClassCtor(MethodDesc m) => m.IsConstructor && !m.Signature.IsStatic && m.OwningType is DefType { IsValueType: false } t
        && !t.IsString && !t.IsDelegate && m.OwningType is not ArrayType;

    /// <summary>The stores of the body whose old value is known null.</summary>
    public static HashSet<Node> Find(PlanContext ctx)
    {
        var r = new HashSet<Node>();
        if (s_off) return r;
        var body = ctx.Body;
        var memo = MemoOf(ctx.Solution);
        lock (memo) Analyze(ctx, body, memo, r);
        Found += r.Count;
        return r;
    }

    /// <summary>The forward analysis: per block, each tracked object and the fields written on it so far.</summary>
    static void Analyze(PlanContext ctx, Body body, Memo memo, HashSet<Node> result)
    {
        // (the objects to track: fresh newobjs, and this in a class's constructor; not those that flow into a phi)
        var inPhi = new HashSet<Node>();
        foreach (var b in body.Blocks) foreach (var p in b.Phis) foreach (var a in p.A) inPhi.Add(a);
        Node? self = null;
        HashSet<FieldDesc>? selfBefore = null;
        if (IsClassCtor(ctx.Method) && body.Args.FirstOrDefault(a => a.I == 0) is { } a0 && !inPhi.Contains(a0)
            && Before(ctx.Solution, ctx.Method, memo) is { } before)
        { self = a0; selfBefore = before; }
        bool any = self != null;
        if (!any)
            foreach (var b in body.Blocks)
                foreach (var n in b.Code)
                    if (n.Op == Opc.StFld && n.A[0].Op == Opc.NewObj) { any = true; break; }
        if (!any) return;

        var ends = Ownership.FinallyEnds(body);
        var succs = new Dictionary<Block, List<Block>>();
        var preds = body.Blocks.ToDictionary(b => b, _ => new List<Block>());
        foreach (var b in body.Blocks)
        {
            var s = b.Succs.Concat(ends.GetValueOrDefault(b) ?? []).Distinct().ToList();
            succs[b] = s;
            foreach (var t in s) preds[t].Add(b);
        }
        var outs = new Dictionary<Block, Dictionary<Node, HashSet<FieldDesc>>>();
        var entry = body.Blocks[0];
        // (iterate to a fixed point: the states only lose objects and gain fields)
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var b in body.Blocks)
            {
                var st = In(b, entry, preds[b], outs, self, selfBefore);
                Transfer(ctx, b, st, memo, inPhi, null);
                if (!outs.TryGetValue(b, out var old) || !Same(old, st)) { outs[b] = st; changed = true; }
            }
        }
        foreach (var b in body.Blocks)
        {
            var st = In(b, entry, preds[b], outs, self, selfBefore);
            Transfer(ctx, b, st, memo, inPhi, result);
        }
    }

    static Dictionary<Node, HashSet<FieldDesc>> In(Block b, Block entry, List<Block> preds, Dictionary<Block, Dictionary<Node, HashSet<FieldDesc>>> outs, Node? self, HashSet<FieldDesc>? selfBefore)
    {
        var st = new Dictionary<Node, HashSet<FieldDesc>>();
        if (b == entry)
        {
            // (the entry has no predecessors: SSA's entry block is not a loop header)
            if (self != null && preds.Count == 0) st[self] = new HashSet<FieldDesc>(selfBefore!);
            return st;
        }
        // (a block no edge reaches, a handler's or a filter's entry: nothing known)
        bool first = true;
        foreach (var p in preds)
        {
            if (!outs.TryGetValue(p, out var o)) continue; // (not reached yet: optimistic, revisited)
            if (first) { foreach (var (k, v) in o) st[k] = new HashSet<FieldDesc>(v); first = false; continue; }
            foreach (var k in st.Keys.ToList())
            {
                if (!o.TryGetValue(k, out var w)) st.Remove(k);
                else st[k].UnionWith(w);
            }
        }
        return st;
    }

    static bool Same(Dictionary<Node, HashSet<FieldDesc>> a, Dictionary<Node, HashSet<FieldDesc>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a) if (!b.TryGetValue(k, out var w) || !v.SetEquals(w)) return false;
        return true;
    }

    static void Transfer(PlanContext ctx, Block b, Dictionary<Node, HashSet<FieldDesc>> st, Memo memo, HashSet<Node> inPhi, HashSet<Node>? result)
    {
        foreach (var n in b.Code)
        {
            for (int j = 0; j < n.A.Length; j++)
            {
                var o = n.A[j];
                if (!st.TryGetValue(o, out var w)) continue;
                switch (n.Op)
                {
                    case Opc.StFld when j == 0:
                    {
                        var f = Key((FieldDesc)n.X!);
                        if (!w.Contains(f) && n.A[1] != o) result?.Add(n);
                        w.Add(f);
                        continue;
                    }
                    case Opc.LdFldA when j == 0:
                        w.Add(Key((FieldDesc)n.X!));
                        continue;
                    case Opc.LdFld when j == 0:
                    case Opc.Cmp or Opc.BrIf or Opc.Retain or Opc.Release:
                        continue;
                    case Opc.Call when j == 0 && n.Sub == (byte)CallKind.Direct && n.X is MethodDesc c && IsClassCtor(c)
                        && ctx.Method.IsConstructor && o.Op == Opc.Arg && Effect(ctx.Solution, c, memo) is { Keeps: true } e:
                        // (this constructor hands over to another one on its own object: what that one writes)
                        w.UnionWith(e.Writes);
                        continue;
                }
                st.Remove(o);
            }
            if (n.Op == Opc.NewObj && n.Kind == VK.Ref && n.X is MethodDesc ctor && IsClassCtor(ctor) && !inPhi.Contains(n)
                && Effect(ctx.Solution, ctor, memo) is { Keeps: true } ef)
                st[n] = new HashSet<FieldDesc>(ef.Writes);
        }
    }

    /// <summary>The class constructor keeps <c>this</c> in: it stores it nowhere and passes it only to the constructors
    /// it hands over to (FrameObjects' rule too).</summary>
    public static bool KeepsThis(ProgramSolution sol, MethodDesc ctor)
    {
        if (!IsClassCtor(ctor)) return false;
        var memo = MemoOf(sol);
        lock (memo) return Effect(sol, ctor, memo) is { Keeps: true };
    }

    /// <summary>What a class's constructor does to <c>this</c> (null: unknown, it may let it out or write anything).</summary>
    static CtorEffect? Effect(ProgramSolution sol, MethodDesc c, Memo memo)
    {
        if (memo.Effects.TryGetValue(c, out var r)) return r;
        memo.Effects[c] = null; // (while deciding: a ring of constructors handing over runs forever)
        r = Compute(sol, c, memo);
        memo.Effects[c] = r;
        return r;
    }

    static CtorEffect? Compute(ProgramSolution sol, MethodDesc c, Memo memo)
    {
        if (!IsClassCtor(c)) return null;
        // (Object's constructor does nothing)
        if (c.OwningType.IsObject) return new CtorEffect(true, []);
        if (!sol.Covers(c) || sol.BodyOf(c) is not { } body) return null;
        if (body.Args.FirstOrDefault(a => a.I == 0) is not { } self) return null;
        var writes = new HashSet<FieldDesc>();
        foreach (var b in body.Blocks)
        {
            foreach (var p in b.Phis) if (p.A.Contains(self)) return null;
            foreach (var n in b.Code)
                for (int j = 0; j < n.A.Length; j++)
                {
                    if (n.A[j] != self) continue;
                    switch (n.Op)
                    {
                        case Opc.StFld when j == 0 && n.A[1] != self:
                        case Opc.LdFldA when j == 0:
                            writes.Add(Key((FieldDesc)n.X!));
                            continue;
                        case Opc.LdFld when j == 0:
                        case Opc.Cmp or Opc.BrIf or Opc.Retain or Opc.Release:
                            continue;
                        case Opc.Call when j == 0 && n.Sub == (byte)CallKind.Direct && n.X is MethodDesc t && IsClassCtor(t):
                            if (Effect(sol, t, memo) is not { Keeps: true } e) return null;
                            writes.UnionWith(e.Writes);
                            continue;
                    }
                    return null;
                }
        }
        return new CtorEffect(true, writes);
    }

    /// <summary>The fields of a constructor's object that may be written at its entry: by the constructors that hand
    /// over to it (all they write: before or after the call is not looked at) and what theirs may have, to a fixed
    /// point up the chain. Null (any) when some constructor calls it on an object other than its own.</summary>
    static HashSet<FieldDesc>? Before(ProgramSolution sol, MethodDesc c, Memo memo)
    {
        if (memo.Before.TryGetValue(c, out var r)) return r;
        memo.Before[c] = null;
        r = ComputeBefore(sol, c, memo);
        memo.Before[c] = r;
        return r;
    }

    static HashSet<FieldDesc>? ComputeBefore(ProgramSolution sol, MethodDesc c, Memo memo)
    {
        if (memo.CtorCallers == null)
        {
            memo.CtorCallers = new();
            foreach (var m in sol.Methods.Items)
            {
                if (!m.IsConstructor || m.Signature.IsStatic || sol.Summary(m) is not { } s) continue;
                foreach (var t in s.Calls.Distinct())
                    if (t.IsConstructor && !t.Signature.IsStatic)
                    {
                        if (!memo.CtorCallers.TryGetValue(t, out var l)) memo.CtorCallers[t] = l = [];
                        l.Add(m);
                    }
            }
        }
        var r = new HashSet<FieldDesc>();
        foreach (var m in memo.CtorCallers.GetValueOrDefault(c) ?? [])
        {
            // (a constructor that makes another object of the type with newobj is no caller on this object)
            if (sol.BodyOf(m) is not { } body) return null;
            var self = body.Args.FirstOrDefault(a => a.I == 0);
            bool chains = false;
            foreach (var b in body.Blocks)
                foreach (var n in b.Code)
                    if (n.Op == Opc.Call && n.X is MethodDesc t && t == c)
                    {
                        if (self == null || n.A.Length == 0 || n.A[0] != self) return null;
                        chains = true;
                    }
            if (!chains) continue;
            if (Before(sol, m, memo) is not { } mb) return null;
            r.UnionWith(mb);
            foreach (var (f, _, _) in sol.Summary(m)!.FieldStores) r.Add(Key(f));
        }
        return r;
    }
}
