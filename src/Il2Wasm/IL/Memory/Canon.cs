#nullable enable
using System;
using System.Linq;
using Il2Wasm.IL.Ir;

namespace Il2Wasm.IL.Memory;

/// <summary>Local re-canonicalization after each inlining step (docs/MEMORY-IR.md 3.7). S1 agent d's file (with
/// Ir/Inline.cs).
///
/// Inline.Step leaves the caller's body with no plan in it: it strips the Retain and Release nodes and the Moves
/// bits, then splices the callees in. Canon plans the body again with the global solution unchanged (Plan.Build,
/// the same planner as the method's first plan) and puts the result into the caller's MethodPlan in place. The
/// result is OSSA form by construction:
///   - every count pair the splice exposed is gone. These are the callee's entry retain of an argument and its
///     release at the callee's last use, a lent argument's release after the call, and an owned result's release in
///     the caller. The planner now sees one value, the caller's, live across the inlined code;
///   - no release moves later than the planner puts it: a release stays at the value's last use in the combined
///     body.
/// The callee plan's own per-node facts are on the copies (Inline.SourceFactOf) for Plan.Build to start from.
///
/// IL2WASM_INLINE_TRACE=1 also prints the static count operations of each step ("canon: method counts a -> b": the
/// Retain and Release nodes before the step and after the re-plan).</summary>
public static class Canon
{
    static readonly bool s_trace = Environment.GetEnvironmentVariable("IL2WASM_INLINE_TRACE") == "1";

    /// <summary>Re-canonicalizes the caller's plan around one inlined step, with the global solution unchanged.</summary>
    public static void Run(PlanContext ctx, MethodPlan plan, InlineSite site)
    {
        int before = s_trace ? CountOps(plan) : 0;
        Inline.CanonClock.Start();
        var fresh = Plan.Build(ctx);
        CopyInto(fresh, plan);
        Inline.CanonClock.Stop();
        if (s_trace) Console.Error.WriteLine($"canon: {ctx.Method} counts {before} -> {CountOps(plan)}");
    }

    /// <summary>The planned count operations in the body (lowering's own load retains and store releases are not nodes).</summary>
    static int CountOps(MethodPlan plan) => plan.Body.Blocks.Sum(b => b.Code.Count(n => n.Op is Opc.Retain or Opc.Release));

    /// <summary>A new plan's contents put into the plan object lowering holds (PlanMethod keeps that object).</summary>
    static void CopyInto(MethodPlan from, MethodPlan to)
    {
        to.CountedValues.Clear(); to.CountedValues.AddRange(from.CountedValues);
        to.PhiMoves.Clear(); to.PhiMoves.UnionWith(from.PhiMoves);
        to.Bases.Clear(); foreach (var (k, v) in from.Bases) to.Bases[k] = v;
        to.Entry = from.Entry;
        foreach (var b in to.Body.Blocks)
            foreach (var n in b.Phis.Concat(b.Code))
            {
                Copy(from.Own, to.Own, n);
                Copy(from.Counts, to.Counts, n);
                Copy(from.Stores, to.Stores, n);
                Copy(from.Loads, to.Loads, n);
                Copy(from.Accesses, to.Accesses, n);
                Copy(from.Allocs, to.Allocs, n);
                Copy(from.Args, to.Args, n);
                Copy(from.Callees, to.Callees, n);
                Copy(from.Categories, to.Categories, n);
            }
    }

    /// <summary>A table entry copied; a table both plans left unset stays unset (it lowers as today).</summary>
    static void Copy<T>(NodeTable<T> from, NodeTable<T> to, Node n)
    {
        if (from.IsEmpty && to.IsEmpty) return;
        to[n] = from[n];
    }
}
