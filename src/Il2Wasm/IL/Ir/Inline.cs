#nullable enable
using System;
using Il2Wasm.IL.Memory;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Ir;

/// <summary>The IR inliner (docs/MEMORY-IR.md 3.7), on planned (OSSA) bodies after the solve: AggressiveInlining plus a
/// size budget; a Lend argument becomes a Guaranteed alias of the caller's value, a HandOver argument a move, the
/// callee's owned result the caller's owned value. S1 agent d's file (with Memory/Canon.cs).
/// STUB (step 1): inlines nothing. The signature below is frozen (MEMORY-IR.md 11.4).</summary>
public static class Inline
{
    /// <summary>The size budget in IR nodes (the old front end's figure: FINISH.md stream K); a target.</summary>
    public const int Budget = 24;

    /// <summary>One inlining step on the caller's planned body: the next call site to inline (in block and node order)
    /// is replaced by the callee's planned body, which <paramref name="callee"/> gives for the entry the site uses (null:
    /// not inlined). Returns the site, or null when nothing more is inlined.</summary>
    public static InlineSite? Step(PlanContext ctx, MethodPlan plan, Func<MethodDesc, Variant, (Body Body, MethodPlan Plan)?> callee) => null;
}
