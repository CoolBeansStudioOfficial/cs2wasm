#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Memory;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Lower;

// A self-test of lowering from the plan, before the planner (S1 row c) fills the tables: IL2WASM_LOWER_SELFTEST=1
// rewrites each naive plan into an equivalent one that uses what lowering now reads, so the IL tests and the NetWasm
// suite exercise it. Off by default; it is not the planner and decides nothing the planner will.
// - owned entries (4.3, 4.4): an argument whose last use is a direct call (the naive plan's Release right after it)
//   is handed over instead (Args HandOver), and the call takes the callee's owned entry for those parameters
//   (Callees Owned(mask), made by IlCompiler.VariantFor); that entry's plan does not count those parameters on entry.
//   The count the caller released after the call is the one the callee released at its last use: the same counts,
//   the object freed at the callee's last use instead of after the call.
// - frame objects (2.3), with IL2WASM_LOWER_SELFTEST=2: an object made by a constructor that only calls
//   Object's, used only as the base of its field loads and stores and by its counts, is FrameOwned.
sealed partial class FunctionLowering
{
    static readonly int s_selfTest = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_LOWER_SELFTEST"), out int st) ? st : 0;

    void SelfTest()
    {
        if (s_selfTest <= 0 || C.Solution == null || Pipeline.Planned) return;
        // the owned entry: its parameters in the mask come counted
        if (_own.Entry.Kind == VariantKind.Owned)
        {
            var entry = _body.Blocks[0].Code;
            entry.RemoveAll(r => r.Op == Opc.Retain && r.A[0].Op == Opc.Arg && r.A[0].Kind == VK.Ref && (_own.Entry.Mask & (1u << (int)r.A[0].I)) != 0);
        }
        foreach (var b in _body.Blocks)
        {
            var code = b.Code;
            for (int i = 0; i < code.Count; i++)
            {
                var n = code[i];
                if (n.Op != Opc.Call || (CallKind)n.Sub != CallKind.Direct || n.A.Length > 31) continue;
                var m = (MethodDesc)n.X!;
                if (!CanTakeVariant(m)) continue;
                uint mask = 0;
                for (int j = i + 1; j < code.Count && code[j].Op == Opc.Release; j++)
                {
                    var v = code[j].A[0];
                    if (v.Kind != VK.Ref || v.Op is Opc.LdStr or Opc.LdNull or Opc.Const) continue;
                    int k = Array.IndexOf(n.A, v);
                    if (k < 0 || Array.LastIndexOf(n.A, v) != k) continue;
                    mask |= 1u << k;
                    code.RemoveAt(j--);
                }
                if (mask == 0) continue;
                var args = new ArgConv[n.A.Length];
                for (int k = 0; k < args.Length; k++) if ((mask & (1u << k)) != 0) args[k] = ArgConv.HandOver;
                _own.Args[n] = args;
                _own.Callees[n] = new Variant(VariantKind.Owned, mask);
            }
        }
        if (s_selfTest >= 2) FrameSelfTest();
    }

    /// <summary>A method lowered from its own IL, by FunctionLowering (not an intrinsic, a stub or the runtime's).</summary>
    bool CanTakeVariant(MethodDesc m)
    {
        if (!C.Solution!.Covers(m) || ((IReachHost)C).LoweredInline(m)) return false;
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em || em.IsPInvoke || em.IsInternalCall || em.IsRuntimeImplemented || em.IsAbstract) return false;
        if (em.HasCustomAttribute("Il2Wasm.Runtime", "ImportAttribute") || em.HasCustomAttribute("System.Runtime.CompilerServices", "IntrinsicAttribute")) return false;
        if (C.Facts.Substitutions.Method(m) is not null || IlCompiler.IsUpstreamIntrinsic(m)) return false;
        // (the counted allocator wraps these)
        if (m.OwningType is MetadataType t && t.GetName() == "Rt" && t.GetNamespace() == "Il2Wasm.Runtime") return false;
        return true;
    }

    void FrameSelfTest()
    {
        var uses = new Dictionary<Node, List<(Node, int)>>();
        foreach (var b in _body.Blocks)
            foreach (var n in b.Phis.Concat(b.Code))
                for (int i = 0; i < n.A.Length; i++)
                {
                    if (!uses.TryGetValue(n.A[i], out var l)) uses[n.A[i]] = l = [];
                    l.Add((n, i));
                }
        foreach (var b in _body.Blocks)
            foreach (var n in b.Code)
            {
                if (n.Op != Opc.NewObj || ((MethodDesc)n.X!).OwningType is not DefType { IsValueType: false, IsString: false, IsDelegate: false } t) continue;
                if (t.BaseType is not { IsObject: true } || !TrivialCtor((MethodDesc)n.X!)) continue;
                if (n.A.Length != 0 || IlCompiler.FinalizerOf(t) != null || Types.IlContext.InstanceSize(t) > MaxFrameObjectSize) continue;
                bool local = uses.TryGetValue(n, out var us) && us.All(u => u.Item1.Op switch
                {
                    Opc.LdFld => u.Item2 == 0,
                    Opc.StFld => u.Item2 == 0,
                    Opc.Release or Opc.Retain => true,
                    _ => false,
                });
                if (local) _own.Allocs[n] = AllocKind.FrameOwned;
            }
    }

    /// <summary>A constructor whose IL is only Object's constructor called on this (ldarg.0, call, ret, with nops).</summary>
    static bool TrivialCtor(MethodDesc ctor)
    {
        if (ctor.GetTypicalMethodDefinition() is not EcmaMethod em) return false;
        var il = Internal.IL.EcmaMethodIL.Create(em)?.GetILBytes();
        if (il == null) return false;
        int i = 0;
        while (i < il.Length && il[i] == 0x00) i++;
        if (i + 6 >= il.Length || il[i] != 0x02 || il[i + 1] != 0x28) return false;
        for (i += 6; i < il.Length && il[i] == 0x00; i++) { }
        return i == il.Length - 1 && il[i] == 0x2A;
    }
}
