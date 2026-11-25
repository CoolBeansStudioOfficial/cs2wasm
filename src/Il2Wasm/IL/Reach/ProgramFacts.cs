#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Internal.IL;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Reach;

/// <summary>What the whole program knows when a method is imported: the substitutions and the feature switches, the
/// calls that evaluate at compile time, and the round before's facts (RoundFacts). A method is imported, its constant
/// propagation run with these, and, when it decides a branch or a call, imported again with the facts (Importer's
/// ImportFacts), so what can never run is never imported and what it calls is never reached (IL2WASM_NO_SCCP=1: one
/// import, as before, and one round). Each import also records this round's facts for the next.</summary>
public sealed class ProgramFacts : ISccpOracle
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("IL2WASM_NO_SCCP") == "1";
    public readonly Substitutions Substitutions = new();
    public readonly PureCalls Pure;
    /// <summary>The round before's facts (null in the first round).</summary>
    public RoundFacts? Previous;
    /// <summary>Imports kept (IL2WASM_NO_IMPORT_CACHE=1: none) across rounds (shared by the rounds of one compilation).</summary>
    public ImportCache? Cache;

    // this round's
    readonly HashSet<FieldDesc> _written = [];
    readonly Dictionary<MethodDesc, Val[]> _args = new();
    readonly HashSet<MethodDesc> _indirect = [];
    readonly Dictionary<MethodDesc, HashSet<MethodDesc>> _directCallees = new();

    public ProgramFacts() { Pure = new PureCalls(Substitutions); }

    /// <summary>A method imported: the importer, the constant propagation with these facts, and when it decided a
    /// branch or a call, the import again with them. Nothing of this round's facts is written here: the summary
    /// (Memory/Summary.cs) carries what the constant propagation saw, and <see cref="Merge"/> records it when lowering
    /// reaches the method (the reach phase imports before lowering, in an order of its own).</summary>
    public Body Import(MethodDesc m, MethodIL il, Il2Wasm.IL.Memory.IReachHost host, out Il2Wasm.IL.Memory.MethodSummary? summary, out ImportFacts? facts)
    {
        summary = null;
        facts = null;
        // (the round before imported it with facts that answer every read it made as these do: the same import)
        if (!Off && Cache != null && Previous != null && Cache.Hit(m, Previous) is { } hit)
        {
            summary = hit.Summary; facts = hit.Facts;
            return facts == null ? Importer.Import(m, il) : Importer.Import(m, il, facts);
        }
        var body = Importer.Import(m, il);
        if (Off) { summary = Il2Wasm.IL.Memory.MethodSummary.Build(m, null, null, body, host); return body; }
        var log = Cache != null && Previous != null ? new List<(char, object, int, string)>() : null;
        if (log != null) Previous!.Log = log;
        Sccp s;
        try { s = Sccp.Solve(body, this); }
        finally { if (log != null) Previous!.Log = null; }
        // (set before the second import: when it throws, the caller still records what the first one saw)
        summary = Il2Wasm.IL.Memory.MethodSummary.FromConstants(m, body, s);
        facts = Sccp.Facts(body, s);
        var final = facts == null ? body : Importer.Import(m, il, facts);
        summary.AddEdges(final, host);
        if (log != null) Cache!.Put(m, log, facts, summary);
        return final;
    }

    /// <summary>This round's facts from one method's summary: what each import recorded before the phase split, in the
    /// same order (each kind of fact in node order).</summary>
    public void Merge(Il2Wasm.IL.Memory.MethodSummary sum)
    {
        if (Off) return;
        var m = sum.Method;
        _directCallees[m] = sum.Direct;
        foreach (var (f, v, address) in sum.FieldStores)
            _fields[f] = address ? Val.Bottom : _fields.TryGetValue(f, out var fv) ? Val.Meet(fv, v) : v;
        foreach (var f in sum.StaticsWritten) { _written.Add(f); _written.Add(f.GetTypicalFieldDefinition()); }
        foreach (var t in sum.TypeObjects) TypeObjects.Add(t);
        foreach (var fm in sum.FunctionPointers) _indirect.Add(fm);
        foreach (var t in sum.ArrayElementTypes) ArrayElementTypes.Add(t);
        foreach (var t in sum.AttributeQueryTargets) AttributeQueryTargets.Add(t);
        foreach (var t in sum.AttributeQueryTypes) AttributeQueryTypes.Add(t);
        foreach (var t in sum.MarshalTargets) MarshalTargets.Add(t);
        MarshalUnknown |= sum.MarshalUnknown;
        AttributeQueryUnknownTarget |= sum.AttributeQueryUnknownTarget;
        AttributeQueryUnknownType |= sum.AttributeQueryUnknownType;
        foreach (var (c, args) in sum.ArgMeets) Meet(c, args);
        foreach (var t in sum.Aliased) _aliased.Add(t);
        if (sum.Return is { } ret) _returns[m] = ret;
    }

    readonly Dictionary<MethodDesc, Val> _returns = new();
    /// <summary>The types typeof and ldtoken name in the code that can run (each a Type object the program can see).</summary>
    public readonly HashSet<TypeDesc> TypeObjects = [];
    /// <summary>The types code that can run makes arrays of at run time, by name (typeof straight into
    /// Array.CreateInstance or MakeArrayType).</summary>
    public readonly HashSet<TypeDesc> ArrayElementTypes = [];
    /// <summary>The Type objects the code that can run asks attributes of (the first argument of a query), and the
    /// attribute types it asks for (a Type or generic argument naming an attribute type). The Unknown flags: a query
    /// whose Type is not one the code names (any type, or any attribute type).</summary>
    public readonly HashSet<TypeDesc> AttributeQueryTargets = [];
    public readonly HashSet<TypeDesc> AttributeQueryTypes = [];
    public bool AttributeQueryUnknownTarget, AttributeQueryUnknownType;
    /// <summary>The types the program's marshalling calls (Marshal.SizeOf, OffsetOf, StructureToPtr, PtrToStructure,
    /// DestroyStructure) name; MarshalUnknown when a call's type is not one the code names (any type then).</summary>
    public readonly HashSet<TypeDesc> MarshalTargets = [];
    public bool MarshalUnknown;
    readonly Dictionary<FieldDesc, Val> _fields = new();

    public Val InstanceField(Node load) => Previous != null && load.X is FieldDesc f ? Previous.Field(f) : Val.Bottom;

    /// <summary>A method whose IL is not what runs: an intrinsic (il2wasm's or upstream's), an import, a P/Invoke or an
    /// internal call. Its IL says nothing about its value.</summary>
    internal static bool Opaque(MethodDesc m) => m.GetTypicalMethodDefinition() is not Internal.TypeSystem.Ecma.EcmaMethod em || em.IsPInvoke || em.IsInternalCall
        || em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute") || em.HasCustomAttribute("Il2Wasm.Runtime", "ImportAttribute")
        || em.HasCustomAttribute("System.Runtime.CompilerServices", "IntrinsicAttribute") || Il2Wasm.IL.Lower.IlCompiler.IsUpstreamIntrinsic(m);

    /// <summary>The implementation a virtual slot has on an object of this exact type (the compiler's dispatch).</summary>
    public Func<TypeDesc, MethodDesc, MethodDesc?>? Resolve;

    /// <summary>A method's value: evaluated (pure, its own body under these arguments), or the round before's value at
    /// all its returns.</summary>
    Val ValueOf(MethodDesc impl, Val[] args)
    {
        var (pure, iv) = Pure.Evaluate(impl, args, valueOnly: true);
        if (pure && iv.IsConst) return iv;
        return Previous != null && !Opaque(impl) ? Previous.Return(impl) : Val.Bottom;
    }

    void Meet(MethodDesc m, Val[] args)
    {
        // (a copy: the summary's own array stays as the import made it, so a summary can be used again: ImportCache)
        if (!_args.TryGetValue(m, out var a)) { _args[m] = (Val[])args.Clone(); return; }
        for (int i = 0; i < a.Length && i < args.Length; i++) a[i] = Val.Meet(a[i], args[i]);
    }

    public bool IsDirectCallee(MethodDesc caller, MethodDesc m) => _directCallees.TryGetValue(caller, out var d) && d.Contains(m);

    public void MarkIndirect(MethodDesc m) => _indirect.Add(m);

    /// <summary>The IR inliner put a callee's body into the caller: the callee's own direct calls are the caller's now
    /// (their argument meets are in the callee's summary, merged too), so lowering them in the caller does not make
    /// their targets indirect.</summary>
    public void AddDirectCallees(MethodDesc caller, IEnumerable<MethodDesc> callees)
    {
        if (Off) return;
        var d = _directCallees.TryGetValue(caller, out var old) ? new HashSet<MethodDesc>(old) : [];
        d.UnionWith(callees);
        _directCallees[caller] = d;
    }

    /// <summary>This round's facts, for the next.</summary>
    readonly HashSet<TypeDesc> _aliased = [];

    public RoundFacts Close(Dictionary<MethodDesc, MethodDesc[]> impls, IEnumerable<TypeDesc> constructed)
    {
        // (the runtime's own methods are called by the generated code with arguments of its own)
        foreach (var m in _args.Keys)
            if (m.OwningType is MetadataType t && (t.GetNamespace().StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal) || t.ContainingType is MetadataType ct && ct.GetNamespace().StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal)))
                _indirect.Add(m);
        var classes = new HashSet<TypeDesc>();
        foreach (var t in constructed) for (TypeDesc? b = t; b != null; b = b.BaseType) classes.Add(b);
        return new RoundFacts([.. _written], _args.ToDictionary(), [.. _indirect], impls, _returns) { Fields = _fields, ConstructedClasses = classes, Aliased = _aliased };
    }

    public Val Arg(Body body, int index) => Previous?.Arg(body.Method, index) ?? Val.Bottom;

    public Val Call(Node call, Val[] args, out bool removable)
    {
        var v = Pure.CallValue(call, args, out removable);
        if (v.State != Val.S.Bottom || removable || call.X is not MethodDesc m) return v;
        if ((CallKind)call.Sub == CallKind.Direct)
            return Previous != null && !Opaque(m) ? Previous.Return(m) : Val.Bottom;
        if (RoundFacts.NoDevirtValues) return Val.Bottom;
        // a virtual call on an object of a known exact type: its one implementation's value
        if (args.Length > 0 && args[0].Type is { IsValueType: false } exact && Resolve?.Invoke(exact, m) is { } one)
            return ValueOf(one, args);
        // otherwise every implementation over the types the round before constructed, with the same value
        if (Previous != null && Previous.Impls(m) is { Length: > 0 } impls)
        {
            var r = Val.Top;
            foreach (var impl in impls)
            {
                // (a struct's method on its box: its this is the box's data, not known)
                var a = (Val[])args.Clone();
                if (a.Length > 0) a[0] = impl.OwningType.IsValueType ? Val.Bottom : Val.NonNull;
                var iv = ValueOf(impl, a);
                if (iv.State == Val.S.Bottom) return Val.Bottom;
                r = Val.Meet(r, iv);
            }
            return r;
        }
        return Val.Bottom;
    }

    public Val StaticField(Node load)
    {
        if (load.X is not FieldDesc f) return Val.Bottom;
        if (Substitutions.Field(f) is long v) return Val.Of(v);
        // an unwritten static: its default (a field with data in the image, or a literal, is not one)
        if (Previous != null && !Previous.Written(f) && !f.HasRva && !f.IsLiteral && !f.IsThreadStatic)
        {
            var k = Kinds.Of(f.FieldType);
            if (k is VK.I32 or VK.I64 or VK.Ptr) return Val.Of(0);
            if (k == VK.Ref) return Val.Null;
        }
        return Val.Bottom;
    }
}
