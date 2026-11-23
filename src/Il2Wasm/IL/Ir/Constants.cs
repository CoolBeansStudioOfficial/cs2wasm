#nullable enable
using System;
using System.Linq;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Ir;

/// <summary>The runtime intrinsics whose value is fixed when the program is compiled (the program as compiled here:
/// one thread, no groups yet; the runtime's IL2WASM_* switches as the operation-tree compiler bakes them). The importer
/// folds them, so the code behind a switch that is off is never reached, as Folding.cs did.</summary>
public static class Constants
{
    public static long? OfIntrinsic(MethodDesc m)
    {
        if (m.Signature.Length != 0 || m.OwningType is not MetadataType ot || ot.GetName() != "Rt" || ot.GetNamespace() != "Il2Wasm.Runtime") return null;
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em || !em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute")) return null;
        var name = m.GetName();
        switch (name)
        {
            case "ThreadMode": case "ShareCheck": case "Checked": case "MemStatsOn": case "WideSizes": case "RuntimeLockHeld":
            case "FirstChanceWanted": case "FramesWanted": case "WeakAny": case "DeadlockDump": case "ArrayMoveMode":
                return 0;
            case "SingleThreaded": case "RipePages": case "HoldPages": case "FusedDrop":
                return 1;
        }
        return Switch(name);
    }

    /// <summary>The members the target fixes (as Folding.cs's KnownBool): no SIMD instructions are emitted yet, so the
    /// scalar paths are taken; no event tracing, no code generation at run time, no debugger, no startup hooks;
    /// invariant globalization (SPEC 9).</summary>
    public static long? Known(MethodDesc m)
    {
        if (m.OwningType is not MetadataType t) return null;
        var outer = t;
        while (outer.ContainingType is MetadataType c) outer = c;
        var ns = outer.GetNamespace();
        var owner = (t.ContainingType is MetadataType ct ? (ct.GetNamespace() + "." + ct.GetName() + ".") : (t.GetNamespace().Length > 0 ? t.GetNamespace() + "." : "")) + t.GetName();
        switch (m.GetName())
        {
            case "get_IsHardwareAccelerated" when ns is "System.Runtime.Intrinsics" or "System.Numerics": return 0;
            case "get_IsSupported" when ns.StartsWith("System.Runtime.Intrinsics."): return 0;
            case "get_IsSupported" when owner is "System.Diagnostics.Tracing.EventSource" or "System.StartupHookProvider": return 0;
            case "get_IsDynamicCodeSupported" or "get_IsDynamicCodeCompiled" when owner == "System.Runtime.CompilerServices.RuntimeFeature": return 0;
            case "get_IsAttached" when owner == "System.Diagnostics.Debugger": return 0;
            case "get_Invariant" when owner is "System.Globalization.GlobalizationMode" or "System.Globalization.GlobalizationMode.Settings": return 1;
            case "get_UseNls" when owner == "System.Globalization.GlobalizationMode": return 0;
        }
        return null;
    }

    /// <summary>Rt.No*, the *Break test hooks and the reports: IL2WASM_* at compile time (0 when unset).</summary>
    public static long? Switch(string name)
    {
        static string Snake(string s) => string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString()));
        static string? Env(string s) => Environment.GetEnvironmentVariable("IL2WASM_" + s);
        if (name == "NoPlainParts")
            return Env("NO_PLAIN_PARTS") == "1" || Env("PLAIN_PARTS") != "1" && string.IsNullOrEmpty(Env("PLAIN_PARTS_BREAK")) ? 1 : 0;
        if (name is "NodePathBreak" or "PlainPartsBreak") return int.TryParse(Env(Snake(name)), out var v) ? v : 0;
        if (name.StartsWith("No") && name.Length > 2 && char.IsUpper(name[2]) || name is "SlotWalkBreak" or "DropStats" or "StaleHolds" or "ArenaReport")
            return Env(Snake(name)) == "1" ? 1 : 0;
        return null;
    }
}
