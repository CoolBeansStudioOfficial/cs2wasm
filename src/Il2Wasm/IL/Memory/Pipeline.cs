#nullable enable
using System;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

/// <summary>Which parts of the memory model's per-method phase run (docs/MEMORY-IR.md 1.2, 3.8's switches). Until the
/// planner is built, lowering uses the naive plan (Ir/Ownership.cs) unless IL2WASM_MEM_PLAN=1; IL2WASM_MEM_NAIVE=1
/// always means the naive plan, the baseline the removal share is measured against.</summary>
public static class Pipeline
{
    public static readonly bool Naive = Environment.GetEnvironmentVariable("IL2WASM_MEM_NAIVE") == "1";
    /// <summary>Plan.Build (and Signatures.Compute after the solve) replace Ownership.Run.</summary>
    public static readonly bool Planned = !Naive && Environment.GetEnvironmentVariable("IL2WASM_MEM_PLAN") == "1";
    /// <summary>The IR inliner with Canon after each step (IL2WASM_NO_IR_INLINE=1: none).</summary>
    public static readonly bool Inline = Planned && Environment.GetEnvironmentVariable("IL2WASM_NO_IR_INLINE") != "1";
    /// <summary>The pairing verifier: always in a Debug build of the compiler, else with IL2WASM_VERIFY=1.</summary>
    public static readonly bool Verify = Planned && (Environment.GetEnvironmentVariable("IL2WASM_VERIFY") == "1" || IsDebugBuild);
#if DEBUG
    const bool IsDebugBuild = true;
#else
    const bool IsDebugBuild = false;
#endif
}

/// <summary>What every per-method pass reads: the method, its body, the program's solution, and the entry being
/// planned (the method's own function, or an owned or lend variant, 4.4).</summary>
public sealed class PlanContext
{
    public readonly MethodDesc Method;
    public readonly Body Body;
    public readonly ProgramSolution Solution;
    public readonly Variant Entry;

    public PlanContext(MethodDesc method, Body body, ProgramSolution solution, Variant entry)
    {
        Method = method; Body = body; Solution = solution; Entry = entry;
    }

    /// <summary>The method's local summary (null for a method outside the solution: a late import).</summary>
    public MethodSummary? Summary => Solution.Summary(Method);
    /// <summary>The method's solved facts (the top for a method outside the solution).</summary>
    public MethodFacts Facts => Solution.Facts(Method);
    /// <summary>The program can start a thread (a reachable Rt.SpawnCore): counts and accesses may need atomics.</summary>
    public bool Threaded => Solution.Threaded;
}

/// <summary>One inlining step's result (Ir/Inline.cs): the call that was replaced and the blocks the callee's body
/// became in the caller, for Canon's local re-canonicalization (3.7).</summary>
public sealed record InlineSite(Node Call, MethodDesc Callee, Block First, Block Last);
