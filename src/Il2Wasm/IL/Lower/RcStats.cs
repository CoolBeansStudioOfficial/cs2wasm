#nullable enable
using System;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Memory;
using Il2Wasm.Wasm;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Lower;

/// <summary>Executed counts per count site (docs/MEMORY-IR.md 9.1, R11): IL2WASM_COUNT_RC=1 gives each planned count
/// operation a site id and a category; lowering emits a counter increment there into a table in linear memory,
/// dumped at exit; IL2WASM_COUNT_RC_SITES=file writes the site table (tools/il-rcstats.mjs sums the dump). S1 agent
/// e's file.
/// STUB (step 1): off, no sites. The members below are frozen (MEMORY-IR.md 11.4); IlCompiler makes one when
/// <see cref="On"/>, FunctionLowering calls <see cref="Count"/> at each count operation, and GenerateLate calls
/// <see cref="EmitLate"/>.</summary>
public sealed class RcStats
{
    public static readonly bool On = Environment.GetEnvironmentVariable("IL2WASM_COUNT_RC") == "1";
    public static readonly string? SitesFile = Environment.GetEnvironmentVariable("IL2WASM_COUNT_RC_SITES") is { Length: > 0 } f ? f : null;

    readonly IlCompiler _c;
    internal RcStats(IlCompiler c) { _c = c; }

    /// <summary>A new site (dense id, in lowering order: stable, since lowering order is) for one count operation.</summary>
    internal int Site(MethodDesc method, Node countOp, RcCategory category) => -1;

    /// <summary>The counter increment of a site, emitted beside the count operation.</summary>
    internal void Count(Emitter e, MethodDesc method, Node countOp, RcCategory category) { }

    /// <summary>The table, its dump at exit, and the sites file (from GenerateLate).</summary>
    internal void EmitLate() { }
}
