#nullable enable
using Il2Wasm.IL.Ir;

namespace Il2Wasm.IL.Memory;

/// <summary>Local re-canonicalization after each inlining step (docs/MEMORY-IR.md 3.7, Swift's ARC cleanup): cancel a
/// Retain and a Release with no observer between, turn owned values used only in a borrow scope into Guaranteed ones,
/// re-run the allocation decision on what the inlined callee made. It never moves a release later. S1 agent d's file
/// (with Ir/Inline.cs).
/// STUB (step 1): does nothing. The signature below is frozen (MEMORY-IR.md 11.4).</summary>
public static class Canon
{
    /// <summary>Re-canonicalizes the caller's plan around one inlined site, with the global solution unchanged.</summary>
    public static void Run(PlanContext ctx, MethodPlan plan, InlineSite site) { }
}
