#nullable enable
using System;
using Il2Wasm.IL.Ir;

namespace Il2Wasm.IL.Memory;

/// <summary>A plan that breaks the pairing rules: an internal compiler error naming the method, the value and the path.</summary>
public sealed class PlanVerifyException(string message) : Exception(message);

/// <summary>The ownership pairing verifier (docs/MEMORY-IR.md 8.1): after Plan.Build and after each inline and Canon
/// step; always in a Debug build, else with IL2WASM_VERIFY=1. S1 agent c's file (with Plan.cs).
/// STUB (step 1): checks nothing. The signature below is frozen (MEMORY-IR.md 11.4).</summary>
public static class Verify
{
    /// <summary>Checks 8.1's nine rules on the planned body; throws <see cref="PlanVerifyException"/> on the first
    /// failure.</summary>
    public static void Check(PlanContext ctx, MethodPlan plan) { }
}
