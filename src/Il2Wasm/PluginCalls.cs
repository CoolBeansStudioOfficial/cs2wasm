using Microsoft.CodeAnalysis;

namespace Il2Wasm;

/// <summary>(Version 5.) A call of <see cref="Callee"/> in <see cref="Caller"/> (a reachable source method, a lambda
/// or a local function: the innermost function the call is in), with the conditions it runs under there (up to that
/// function's start, early returns included). A call of a virtual or interface method is a call site of every
/// override or implementation of it. A property read or write is a call of its accessor, `new` of its
/// constructor.</summary>
public sealed record CallSite(IMethodSymbol Caller, IMethodSymbol Callee, IOperation Call, IReadOnlyList<BranchCondition> Conditions)
{
    public Location Location => Call.Syntax.GetLocation();
}

/// <summary>(Version 5.) Where a path of calls to an operation starts.</summary>
public enum PathStart
{
    /// <summary>A method no source code calls: an export, the entry point, a method the library or the engine calls
    /// (an override, a static constructor).</summary>
    Entry,
    /// <summary>A function run where a delegate is invoked (a lambda, a method group): who invokes it is not known.</summary>
    Delegate,
    /// <summary>A method already on the path (recursion): the conditions of the paths into the cycle are the others.</summary>
    Recursion,
    /// <summary>The path was cut at the depth or the number of paths asked for.</summary>
    Cut,
}

/// <summary>(Version 5.) One way control reaches an operation: the calls from <see cref="Root"/>, outermost first, and
/// every condition along it, outermost first (the root's conditions at its call, then the next method's, then the
/// operation's own). The operation runs on this path only when all of them hold.</summary>
public sealed record ConditionPath(IMethodSymbol Root, PathStart Start, IReadOnlyList<CallSite> Calls, IReadOnlyList<BranchCondition> Conditions);
