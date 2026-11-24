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

    // this round's
    readonly HashSet<FieldDesc> _written = [];
    readonly Dictionary<MethodDesc, Val[]> _args = new();
    readonly HashSet<MethodDesc> _indirect = [];
    readonly Dictionary<MethodDesc, HashSet<MethodDesc>> _directCallees = new();

    public ProgramFacts() { Pure = new PureCalls(Substitutions); }

    public Body Import(MethodDesc m, MethodIL il)
    {
        var body = Importer.Import(m, il);
        if (Off) return body;
        var s = Sccp.Solve(body, this);
        Record(m, body, s);
        var facts = Sccp.Facts(body, s);
        return facts == null ? body : Importer.Import(m, il, facts);
    }

    void Record(MethodDesc m, Body body, Sccp s)
    {
        var direct = new HashSet<MethodDesc>();
        _directCallees[m] = direct;
        var ret = Val.Top;
        foreach (var b in s.ExecutableBlocks)
            foreach (var n in b.Code)
                switch (n.Op)
                {
                    case Opc.StFld when n.X is FieldDesc sf:
                        _fields[sf] = _fields.TryGetValue(sf, out var fv) ? Val.Meet(fv, s.ValueOf(n.A[1])) : s.ValueOf(n.A[1]);
                        break;
                    case Opc.LdFldA when n.X is FieldDesc af:
                        _fields[af] = Val.Bottom;
                        break;
                    case Opc.Ret when n.A.Length > 0:
                        ret = Val.Meet(ret, s.ValueOf(n.A[0]));
                        break;
                    case Opc.StSFld: case Opc.LdSFldA:
                        if (n.X is FieldDesc f) { _written.Add(f); _written.Add(f.GetTypicalFieldDefinition()); }
                        break;
                    case Opc.TypeOf: case Opc.LdToken: case Opc.MkRefAny: // (__reftype gives a TypedReference's type object)
                        if (n.X is TypeDesc tt) TypeObjects.Add(tt);
                        break;
                    case Opc.LdFtn: case Opc.LdVirtFtn:
                        if (n.X is MethodDesc fm) _indirect.Add(fm);
                        break;
                    case Opc.Call when n.X is MethodDesc am && am.GetName() is "CreateInstance" or "MakeArrayType" && n.A.Length > 0 && n.A[0].Op == Opc.TypeOf && n.A[0].X is TypeDesc et
                        && am.OwningType is MetadataType { Name: var _ } amt && amt.GetNamespace() == "System" && amt.GetName() is "Array" or "Type" or "RuntimeType" or "Activator":
                        // (an array made at run time of a type the code names: Array.CreateInstance(typeof(X), n), typeof(X).MakeArrayType())
                        ArrayElementTypes.Add(et);
                        if ((CallKind)n.Sub == CallKind.Direct) { direct.Add(am); Meet(am, n.A.Select(s.ValueOf).ToArray()); }
                        break;
                    case Opc.Call when (CallKind)n.Sub == CallKind.Direct && n.X is MethodDesc cm:
                        // (a removed call is never reached, but its arguments only weaken the meet)
                        direct.Add(cm);
                        Meet(cm, n.A.Select(s.ValueOf).ToArray());
                        break;
                    case Opc.NewObj when n.X is MethodDesc ctor:
                        direct.Add(ctor);
                        Meet(ctor, [Val.NonNull, .. n.A.Select(s.ValueOf)]);
                        break;
                }
        if (!m.Signature.ReturnType.IsVoid && !Opaque(m)) _returns[m] = ret;
    }

    readonly Dictionary<MethodDesc, Val> _returns = new();
    /// <summary>The types typeof and ldtoken name in the code that can run (each a Type object the program can see).</summary>
    public readonly HashSet<TypeDesc> TypeObjects = [];
    /// <summary>The types code that can run makes arrays of at run time, by name (typeof straight into
    /// Array.CreateInstance or MakeArrayType).</summary>
    public readonly HashSet<TypeDesc> ArrayElementTypes = [];
    readonly Dictionary<FieldDesc, Val> _fields = new();

    public Val InstanceField(Node load) => Previous != null && load.X is FieldDesc f ? Previous.Field(f) : Val.Bottom;

    /// <summary>A method whose IL is not what runs: an intrinsic (il2wasm's or upstream's), an import, a P/Invoke or an
    /// internal call. Its IL says nothing about its value.</summary>
    static bool Opaque(MethodDesc m) => m.GetTypicalMethodDefinition() is not Internal.TypeSystem.Ecma.EcmaMethod em || em.IsPInvoke || em.IsInternalCall
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
        if (!_args.TryGetValue(m, out var a)) { _args[m] = args; return; }
        for (int i = 0; i < a.Length && i < args.Length; i++) a[i] = Val.Meet(a[i], args[i]);
    }

    public bool IsDirectCallee(MethodDesc caller, MethodDesc m) => _directCallees.TryGetValue(caller, out var d) && d.Contains(m);

    public void MarkIndirect(MethodDesc m) => _indirect.Add(m);

    /// <summary>This round's facts, for the next.</summary>
    public RoundFacts Close(Dictionary<MethodDesc, MethodDesc[]> impls)
    {
        // (the runtime's own methods are called by the generated code with arguments of its own)
        foreach (var m in _args.Keys)
            if (m.OwningType is MetadataType t && (t.GetNamespace().StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal) || t.ContainingType is MetadataType ct && ct.GetNamespace().StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal)))
                _indirect.Add(m);
        return new RoundFacts([.. _written], _args.ToDictionary(), [.. _indirect], impls, _returns) { Fields = _fields };
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
        if (Previous != null && Previous.Implementations.TryGetValue(m, out var impls) && impls.Length > 0)
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
