#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Reach;

/// <summary>What one round of reaching the whole program found, for the next round (NetWasm's reducing reachability
/// closure, on il2wasm's IR). The first round assumes nothing; each later round uses what the round before saw, and
/// reaches a subset of it, because every fact only removes code: so each round's facts hold for the next, and the
/// rounds stop when a round proves nothing new (IL2WASM_IL_ROUNDS=n caps them; 1: one round, no facts).
/// <list type="bullet">
/// <item>the static fields some reached code writes (stsfld, or their address taken): every other one reads as its
/// default (IL2WASM_NO_UNWRITTEN_STATICS=1: none);</item>
/// <item>each method's arguments, met over all the calls the constant propagation saw, for methods reached by no other
/// way (a dispatch, ldftn, the entry, the runtime's generated code): a constant at every call is a constant in the body
/// (IL2WASM_NO_CONSTANT_ARGS=1: none);</item>
/// <item>each dispatch slot's implementations over the types the round constructed: a virtual call whose every
/// implementation has the same constant value has that value (IL2WASM_NO_DEVIRT_VALUES=1: none).</item>
/// </list></summary>
public sealed class RoundFacts
{
    public static readonly int Rounds = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_IL_ROUNDS"), out var r) && r > 0 ? r : 6;
    public static readonly bool NoUnwrittenStatics = Environment.GetEnvironmentVariable("IL2WASM_NO_UNWRITTEN_STATICS") == "1";
    public static readonly bool NoConstantArgs = Environment.GetEnvironmentVariable("IL2WASM_NO_CONSTANT_ARGS") == "1";
    public static readonly bool NoDevirtValues = Environment.GetEnvironmentVariable("IL2WASM_NO_DEVIRT_VALUES") == "1";

    public readonly HashSet<FieldDesc> WrittenStatics;
    public readonly Dictionary<MethodDesc, Val[]> Args;
    public readonly HashSet<MethodDesc> Indirect;
    public readonly Dictionary<MethodDesc, MethodDesc[]> Implementations;
    /// <summary>Each method's value at every return the constant propagation saw run.</summary>
    public readonly Dictionary<MethodDesc, Val> Returns;
    readonly string _fingerprint;

    public RoundFacts(HashSet<FieldDesc> written, Dictionary<MethodDesc, Val[]> args, HashSet<MethodDesc> indirect, Dictionary<MethodDesc, MethodDesc[]> impls, Dictionary<MethodDesc, Val> returns)
    {
        WrittenStatics = written; Args = args; Indirect = indirect; Implementations = impls; Returns = returns;
        // (a round's facts, as text in a fixed order: two rounds that proved the same have the same)
        var parts = new List<string>();
        parts.AddRange(written.Select(f => "w " + f));
        parts.AddRange(args.Where(a => !indirect.Contains(a.Key)).Select(a => "a " + a.Key + " " + string.Join(",", a.Value)));
        parts.AddRange(impls.Select(i => "i " + i.Key + " " + string.Join(",", i.Value.Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal))));
        parts.AddRange(returns.Where(r => r.Value.State != Val.S.Bottom).Select(r => "r " + r.Key + " " + r.Value));
        parts.Sort(StringComparer.Ordinal);
        _fingerprint = string.Join("\n", parts);
        if (Environment.GetEnvironmentVariable("IL2WASM_IL_ROUNDS_DUMP") is { Length: > 0 } dump) System.IO.File.WriteAllText(dump + "." + s_dumps++, _fingerprint);
    }

    static int s_dumps;

    public bool SameAs(RoundFacts o) => _fingerprint == o._fingerprint;

    public override string ToString() => $"{Fields.Count(f => f.Value.State != Val.S.Bottom)} fields with a value, {WrittenStatics.Count} statics written, {Args.Count(a => !Indirect.Contains(a.Key) && a.Value.Any(v => v.IsConst))} methods with a constant argument, {Implementations.Count} slots";

    /// <summary>The value of a method's argument at every call, or Bottom.</summary>
    public Val Arg(MethodDesc m, int i)
    {
        if (NoConstantArgs || Indirect.Contains(m) || !Args.TryGetValue(m, out var a) || i >= a.Length) return Val.Bottom;
        // (IL2WASM_IL_ARGS_ONLY=text, a debugging aid: argument facts only for the methods whose name contains it)
        if (s_argsOnly != null && !m.ToString()!.Contains(s_argsOnly)) return Val.Bottom;
        // (Top: the method was not called last round, so it is not called now: nothing is known)
        return a[i].State == Val.S.Top ? Val.Bottom : a[i];
    }

    /// <summary>A method's value at every return (IL2WASM_NO_RETURN_VALUES=1: none), or Bottom.</summary>
    public Val Return(MethodDesc m) =>
        !NoReturnValues && Returns.TryGetValue(m, out var v) && v.State is Val.S.Const or Val.S.NonNull or Val.S.Typed ? v : Val.Bottom;

    /// <summary>Each class instance field's stored values met (a field whose address was taken: Bottom).</summary>
    public Dictionary<FieldDesc, Val> Fields = new();
    /// <summary>The constructed types and all their bases.</summary>
    public HashSet<TypeDesc> ConstructedClasses = [];
    /// <summary>The types Unsafe.As reinterprets to or from.</summary>
    public HashSet<TypeDesc> Aliased = [];
    public static readonly bool NoFieldValues = Environment.GetEnvironmentVariable("IL2WASM_NO_FIELD_VALUES") == "1";

    /// <summary>A class instance field's value: its default (every object starts zeroed) met with every store
    /// reached; Bottom for a field of a value type, a delegate, a string or an array (their storage is written by more
    /// than stfld), and for a field whose type is not an integer or a reference.</summary>
    public Val Field(FieldDesc f)
    {
        if (NoFieldValues || f.IsStatic || !FieldTracked(f)) return Val.Bottom;
        // (only a field of a class some made object is: a class no object is made of is only ever a view of other
        // objects, Unsafe.As<RawArrayData>(array), whose memory stfld never wrote; nor of a class Unsafe.As names)
        if (!ConstructedClasses.Contains(f.OwningType) || Aliased.Contains(f.OwningType)) return Val.Bottom;
        var k = Il2Wasm.IL.Ir.Kinds.Of(f.FieldType);
        var d = k == Il2Wasm.IL.Ir.VK.Ref ? Val.Null : k is Il2Wasm.IL.Ir.VK.I32 or Il2Wasm.IL.Ir.VK.I64 or Il2Wasm.IL.Ir.VK.Ptr ? Val.Of(0) : Val.Bottom;
        if (d.State == Val.S.Bottom) return d;
        return Fields.TryGetValue(f, out var v) ? Val.Meet(d, v) : d;
    }

    public static bool FieldTracked(FieldDesc f) => f.OwningType is MetadataType { IsValueType: false, IsDelegate: false, IsString: false } ot && !ot.IsArray
        && !ot.GetNamespace().StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal) && f.FieldType.Category is not TypeFlags.Single and not TypeFlags.Double;

    static readonly string? s_argsOnly = Environment.GetEnvironmentVariable("IL2WASM_IL_ARGS_ONLY") is { Length: > 0 } ao ? ao : null;

    public static readonly bool NoReturnValues = Environment.GetEnvironmentVariable("IL2WASM_NO_RETURN_VALUES") == "1";

    public bool Written(FieldDesc f) => NoUnwrittenStatics || WrittenStatics.Contains(f.GetTypicalFieldDefinition()) || WrittenStatics.Contains(f);
}
