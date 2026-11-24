#nullable enable
using Il2Wasm.IL.Ir;

namespace Il2Wasm.IL.Memory;

/// <summary>The per-method OSSA plan (docs/MEMORY-IR.md 1.2, 2.1, 4.3, 4.6, 4.7): Owned and Guaranteed values, flips,
/// argument conventions, store, load, count and access kinds; it replaces Ownership.Insert and keeps
/// Ownership.Liveness and FindBases. S1 agent c's file (with Verify.cs).
/// STUB (step 1): the naive plan, so IL2WASM_MEM_PLAN=1 lowers exactly what the default does. The signature below is
/// frozen (MEMORY-IR.md 11.4).</summary>
public static class Plan
{
    /// <summary>Plans one body for one entry: inserts the Retain and Release nodes and Node.Moves bits into the body,
    /// and fills the plan's tables (Own from Borrows.Analyze, Args from Signatures.CallRule, Callees from
    /// Signatures.ChooseVariant, Stores, Loads, Counts, Accesses, Allocs, Categories).</summary>
    public static MethodPlan Build(PlanContext ctx) => MethodPlan.Naive(ctx.Body, Ownership.Run(ctx.Body));
}
