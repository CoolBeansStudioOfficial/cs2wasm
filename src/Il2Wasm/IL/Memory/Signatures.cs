#nullable enable
using System.Collections.Generic;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>Ownership signatures, dispatch-group signatures, the call rule and the specialization policy
/// (docs/MEMORY-IR.md section 4.1 to 4.5). S1 agent a's file.
/// STUB (step 1): every parameter Lend, every result owned, the method's own entry at every call; nothing reads it
/// unless IL2WASM_MEM_PLAN=1. The signatures below are frozen (MEMORY-IR.md 11.4).</summary>
public static class Signatures
{
    /// <summary>After the solve: every reached method's signature (4.1, 4.3: HandOver by store or by
    /// KeepsRunningAfter; 4.5: dispatch groups), into <c>Solution.Facts(m).Signature</c>, to its fixed point (demand
    /// only grows).</summary>
    public static void Compute(ProgramSolution solution) { }

    /// <summary>A method's signature (the bottom for a method outside the solution, whose facts are unknown: every
    /// counted parameter HandOver there, so a caller never lends what such a callee may keep).</summary>
    public static OwnershipSignature Of(ProgramSolution solution, MethodDesc m)
    {
        if (solution.Covers(m)) return solution.Facts(m).Signature ?? OwnershipSignature.Bottom(m);
        var top = OwnershipSignature.Bottom(m);
        for (int i = 0; i < top.Params.Length; i++) { top.Params[i] = ParamConv.HandOver; top.Reasons[i] = HandOverReason.Indirect; }
        return top;
    }

    /// <summary>A call site's group signature: the join over its targets (4.5).</summary>
    public static OwnershipSignature GroupOf(ProgramSolution solution, Node call)
    {
        var m = (MethodDesc)call.X!;
        var g = OwnershipSignature.Bottom(m);
        foreach (var t in solution.CallTargets(call)) g.JoinFrom(Of(solution, t));
        return g;
    }

    /// <summary>The call rule (4.3) for one counted argument: lent when the value lives on (KeepAlive if the parameter
    /// is HandOver), handed over at its last use to a HandOver parameter, else lent and released after the call.</summary>
    public static ArgConv CallRule(PlanContext ctx, Node call, int arg, bool lastUse)
    {
        var sig = GroupOf(ctx.Solution, call);
        bool handOver = arg < sig.Params.Length && sig.Params[arg] == ParamConv.HandOver;
        return !handOver ? ArgConv.Lend : lastUse ? ArgConv.HandOver : ArgConv.KeepAlive;
    }

    /// <summary>KeepsRunningAfter(m, i) (4.3): a path from a last use of parameter i to an exit passes a loop header or
    /// a call that is not inlined there and not a cheap intrinsic (exceptional exits count).</summary>
    public static bool KeepsRunningAfter(ProgramSolution solution, Body body, int parameter) => false;

    /// <summary>The callee entry a call uses (4.4): the owned entry for the mask of arguments handed over at their last
    /// use, under the size caps; the borrowed-return entry where the caller borrows the result.</summary>
    public static Variant ChooseVariant(PlanContext ctx, Node call, uint handedOverMask) => Variant.Entry;

    /// <summary>The variant functions to make for a method (each lowered with its own plan), in stable key order.</summary>
    public static IReadOnlyList<Variant> VariantsOf(ProgramSolution solution, MethodDesc m) => [];
}
