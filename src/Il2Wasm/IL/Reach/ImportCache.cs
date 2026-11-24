#nullable enable
using System;
using System.Collections.Generic;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Reach;

/// <summary>Imports kept across the rounds of one compilation (on by default, IL2WASM_NO_IMPORT_CACHE=1: off; MEMORY-IR 3.4's phase 2).
/// A method's import is its IL, the substitutions and switches, the compile-time calls (PureCalls, whose answers are a
/// function of the call since its memo is order-free) and the round before's facts. Of those only the facts change
/// between rounds, and the import reads them through RoundFacts' accessors: each read, with its answer, is recorded.
/// In a later round, when every recorded read gives the same answer from that round's facts, the import is the same.
/// Then the constant propagation and the first import are skipped: the body is imported once with the decisions kept
/// (ImportFacts), and the summary is the one made then. Summaries are never written after they are built
/// (ProgramFacts.Meet copies), so one can serve again.</summary>
public sealed class ImportCache
{
    public static readonly bool On = Environment.GetEnvironmentVariable("IL2WASM_NO_IMPORT_CACHE") != "1";

    sealed record Entry(List<(char Kind, object Key, int Index, string Answer)> Reads, ImportFacts? Facts, Il2Wasm.IL.Memory.MethodSummary Summary);

    readonly Dictionary<MethodDesc, Entry> _entries = new();
    public int Hits, Misses;

    internal void Put(MethodDesc m, List<(char, object, int, string)> reads, ImportFacts? facts, Il2Wasm.IL.Memory.MethodSummary summary) =>
        _entries[m] = new Entry(reads, facts, summary);

    /// <summary>The kept import, when the facts answer every read it made as before.</summary>
    internal (ImportFacts? Facts, Il2Wasm.IL.Memory.MethodSummary Summary)? Hit(MethodDesc m, RoundFacts facts)
    {
        if (!_entries.TryGetValue(m, out var e)) { Misses++; return null; }
        foreach (var (kind, key, index, answer) in e.Reads)
            if (facts.Answer(kind, key, index) != answer) { Misses++; return null; }
        Hits++;
        return (e.Facts, e.Summary);
    }
}
