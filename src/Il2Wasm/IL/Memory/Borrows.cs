#nullable enable
using Il2Wasm.IL.Ir;

namespace Il2Wasm.IL.Memory;

/// <summary>A body's borrow decisions (4.6): each counted value's ownership fact (Guaranteed with its path, or Owned
/// after a flip), and each speculative borrow's counter-evidence.</summary>
public sealed class BorrowFacts
{
    public readonly NodeTable<OwnFact> Own = new(OwnFact.Owned);
    public readonly NodeTable<BorrowEvidence> Evidence = new(BorrowEvidence.None);
    /// <summary>Speculative borrows that flipped to Owned (a Retain at the definition), for RcStats' flip categories.</summary>
    public readonly NodeTable<bool> Flipped = new(false);
}

/// <summary>Speculative borrows, path keys, the two proofs and flip decisions (docs/MEMORY-IR.md 2.2, 4.6). S1 agent
/// b's file.
/// STUB (step 1): no value is borrowed (every counted value Owned), which is the naive plan's meaning. The signatures
/// below are frozen (MEMORY-IR.md 11.4).</summary>
public static class Borrows
{
    /// <summary>The borrow facts of a body: every counted load, VarLd of a frame slot and borrowed-return result starts
    /// Guaranteed(path) and flips to Owned unless proof 1 (no write to the path in its interval: Mods, ReleaseEffects,
    /// FilterMods, self-stores) and proof 2 (no other thread: Local base, init-only key, Local static) hold. The
    /// interval is the value's live range by <paramref name="liveness"/> (Ownership.Liveness, exceptional edges
    /// included).</summary>
    public static BorrowFacts Analyze(PlanContext ctx, Ownership.Info liveness) => new();

    /// <summary>A value's borrow path (2.2): the root and up to three field or element steps, or null (generic).</summary>
    public static BorrowPath? PathOf(PlanContext ctx, Node value) => null;
}
