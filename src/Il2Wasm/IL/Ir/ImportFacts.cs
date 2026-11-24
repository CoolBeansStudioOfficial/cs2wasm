#nullable enable
using System.Collections.Generic;

namespace Il2Wasm.IL.Ir;

/// <summary>What the constant propagation (Sccp.cs) proved about one method's IL, for its import: the conditional
/// branches and switches that always go one way (by the IL start of their block: the block the branch goes to), and the
/// static calls that are pure under their constant arguments (by the call's IL offset: their value, or none for a void
/// call). The importer builds only the code these leave reachable, so nothing behind a branch never taken is reached.</summary>
public sealed class ImportFacts
{
    public readonly Dictionary<int, int> BranchTo = new();
    public readonly Dictionary<int, (VK Kind, long Value)> Calls = new();
    public bool IsEmpty => BranchTo.Count == 0 && Calls.Count == 0;
}
